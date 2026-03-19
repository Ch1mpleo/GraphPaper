using GraphPaper.Application.Interfaces;
using GraphPaper.Application.DTOs.DoclingDTO;
using GraphPaper.Domain.Entities;
using GraphPaper.Domain.Enums;
using GraphPaper.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using System.Text.RegularExpressions;

namespace GraphPaper.Application.Services;

public sealed class DocumentProcessingService : IDocumentProcessingService
{
    // Detects the START of a data-URI image (used for ShouldExtractKnowledge guard).
    private static readonly Regex DataUriImageRegex = new(@"!\[[^\]]*\]\(data:image\/[a-zA-Z]+;base64,", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches a COMPLETE inline markdown image whose src is a base64 data URI.
    // Uses Singleline so '.' crosses newlines (some base64 strings wrap lines).
    private static readonly Regex DataUriImageBlockRegex = new(
        @"!\[[^\]]*\]\(data:image\/[a-zA-Z]+;base64,[A-Za-z0-9+/\r\n=]+\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex MarkdownOnlyRegex = new(@"^[#>*_`\-\s]+$", RegexOptions.Compiled);

    // Strips base64 image blocks, decodes HTML entities, and collapses excess whitespace.
    private static string CleanContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Decode HTML entities produced by Docling (e.g. &amp; → &, &lt; → <).
        var decoded = System.Net.WebUtility.HtmlDecode(text);

        // Remove the entire ![](...base64...) markdown image block.
        var cleaned = DataUriImageBlockRegex.Replace(decoded, string.Empty);

        // Collapse 3+ consecutive newlines down to 2 (one blank line).
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");

        // Rejoin page-break mid-sentence: if a double-newline is followed by a
        // lowercase letter (Latin or Vietnamese), the break was artificial — merge.
        cleaned = Regex.Replace(
            cleaned,
            @"([^\n])\n\n([a-z\u00e0-\u01ff])",
            "$1 $2");

        return cleaned.Trim();
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClaimsService _claimsService;
    private readonly ILogger<DocumentProcessingService> _logger;
    private readonly DocumentProcessingOptions _options;

    public DocumentProcessingService(
        IServiceScopeFactory scopeFactory,
        IClaimsService claimsService,
        ILogger<DocumentProcessingService> logger,
        IOptions<DocumentProcessingOptions> options)
    {
        _scopeFactory = scopeFactory;
        _claimsService = claimsService;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<Document> IngestAsync(IFormFile file)
    {
        if (file is null)
            throw new ArgumentNullException(nameof(file));

        var userId = _claimsService.GetCurrentUserId;

        if (userId == Guid.Empty)
            throw new ArgumentException("User id is required.", nameof(userId));

        // FIX 1: Read the file to a byte[] exactly once.
        // Previously: file stream was opened once in SaveDocumentRecordAsync (to write to disk)
        // and then read a second time via CopyToAsync(memoryStream) here — two full reads.
        using var ms = new MemoryStream((int)file.Length);
        await file.CopyToAsync(ms);
        var fileBytes = ms.ToArray();

        Document document;
        using (var scope = _scopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            document = await SaveDocumentRecordAsync(fileBytes, file.FileName, userId, unitOfWork);
        }

        // Pass the already-buffered bytes to the background task — no second read.
        var filePayload = new FilePayload(fileBytes, file.FileName, file.ContentType);

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var doclingClient = scope.ServiceProvider.GetRequiredService<IDoclingClient>();
            var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
            var knowledgeExtractionService = scope.ServiceProvider.GetRequiredService<IKnowledgeExtractionService>();

            await ProcessDocumentAsync(
                document.Id,
                filePayload,
                unitOfWork,
                doclingClient,
                embeddingService,
                knowledgeExtractionService,
                _options,
                _logger);
        });

        return document;
    }

    private static async Task ProcessDocumentAsync(
        Guid documentId,
        FilePayload filePayload,
        IUnitOfWork unitOfWork,
        IDoclingClient doclingClient,
        IEmbeddingService embeddingService,
        IKnowledgeExtractionService knowledgeExtractionService,
        DocumentProcessingOptions options,
        ILogger logger)
    {
        var document = await unitOfWork.Documents.GetByIdAsync(documentId);
        if (document is null)
            return;

        // FIX 2: Track the file path so we can clean it up on failure.
        var filePath = document.FilePath;

        try
        {
            await UpdateDocumentStatusAsync(unitOfWork, document, DocumentStatus.Chunking);

            var doclingResult = await doclingClient.ParseAsync(filePayload.FileBytes, filePayload.FileName, filePayload.ContentType);
            var chunks = BuildChunksFromDocling(doclingResult.Document, document.Id, options, logger);

            if (chunks.Count == 0)
                throw new InvalidOperationException("Could not extract any text from the document.");

            // Post-processing: merge tiny chunks, then add overlap between neighbours.
            chunks = MergeSmallChunks(chunks, options);
            ApplyChunkOverlap(chunks, options);

            await UpdateDocumentStatusAsync(unitOfWork, document, DocumentStatus.Extracting);

            var texts = chunks.Select(c => c.Content).ToList();
            var embeddings = await embeddingService.GetBatchEmbeddingsAsync(texts);

            AttachEmbeddings(chunks, embeddings);

            await unitOfWork.DocumentChunks.AddRangeAsync(chunks);
            await unitOfWork.SaveChangesAsync();

            await ExtractKnowledgeAsync(chunks, knowledgeExtractionService, unitOfWork, options, logger);

            await UpdateDocumentStatusAsync(unitOfWork, document, DocumentStatus.Ready);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process document {DocumentId}", documentId);
            await UpdateDocumentStatusAsync(unitOfWork, document, DocumentStatus.Failed);

            // FIX 2: Delete the orphaned file from disk when processing fails.
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    logger.LogInformation("Deleted orphaned file {FilePath} for failed document {DocumentId}", filePath, documentId);
                }
                catch (Exception deleteEx)
                {
                    logger.LogWarning(deleteEx, "Could not delete orphaned file {FilePath}", filePath);
                }
            }
        }
    }

    private static List<DocumentChunk> BuildChunksFromDocling(
        DoclingDocument? document,
        Guid documentId,
        DocumentProcessingOptions options,
        ILogger logger)
    {
        if (document is null)
            return [];

        var chunksFromTextItems = BuildChunksFromTextItems(document.Texts, documentId);
        if (chunksFromTextItems.Count > 0)
            return chunksFromTextItems;

        var chunksFromMarkdown = BuildChunksFromMarkdown(document.MarkdownContent, documentId, options);
        if (chunksFromMarkdown.Count > 0)
            logger.LogInformation("Using Docling markdown fallback for document {DocumentId}", documentId);

        return chunksFromMarkdown;
    }

    private static List<DocumentChunk> BuildChunksFromTextItems(IReadOnlyList<DoclingTextItem>? textItems, Guid documentId)
    {
        if (textItems is null || textItems.Count == 0)
            return [];

        var chunks = new List<DocumentChunk>();
        var chunkIndex = 0;

        foreach (var textItem in textItems)
        {
            // Strip inline base64 images before checking content.
            var content = CleanContent(textItem.Text);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            chunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                ChunkIndex = chunkIndex++,
                PageNumber = textItem.Provenance?.FirstOrDefault()?.PageNumber ?? 0,
                Content = content
            });
        }

        return chunks;
    }

    /// <summary>
    /// Splits markdown into heading-scoped sections first (## / ###), then sub-splits
    /// sections that are still too large by paragraph (\n\n), and finally by sentence
    /// boundary if a paragraph still exceeds MaxChunkCharacters.
    /// This produces semantically coherent chunks that keep the heading context intact,
    /// unlike a naive \n\n split which loses section membership.
    /// </summary>
    private static readonly Regex HeadingSplitRegex =
        new(@"(?=^#{1,3} )", RegexOptions.Compiled | RegexOptions.Multiline);

    private static List<DocumentChunk> BuildChunksFromMarkdown(
        string? markdownContent,
        Guid documentId,
        DocumentProcessingOptions options)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
            return [];

        // Clean: strip base64 images, decode HTML entities, collapse blank lines.
        var cleaned = CleanContent(markdownContent);
        if (string.IsNullOrWhiteSpace(cleaned))
            return [];

        // ── Step 1: split by heading (##/###) to get semantically scoped sections ──
        // Each section string still starts with its heading text.
        var sections = HeadingSplitRegex
            .Split(cleaned)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim());

        var chunks = new List<DocumentChunk>();
        var chunkIndex = 0;

        foreach (var section in sections)
        {
            if (section.Length <= options.MaxChunkCharacters)
            {
                // ── Small section: store as one chunk ──────────────────────────────
                chunks.Add(new DocumentChunk
                {
                    Id         = Guid.NewGuid(),
                    DocumentId = documentId,
                    ChunkIndex = chunkIndex++,
                    PageNumber = 0,
                    Content    = section
                });
            }
            else
            {
                // ── Large section: sub-split by paragraph then by sentence ─────────
                var paragraphs = section
                    .Split(["\r\n\r\n", "\n\n"],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => !string.IsNullOrWhiteSpace(p));

                foreach (var para in paragraphs)
                {
                    foreach (var subChunk in SplitParagraph(para, options.MaxChunkCharacters))
                    {
                        chunks.Add(new DocumentChunk
                        {
                            Id         = Guid.NewGuid(),
                            DocumentId = documentId,
                            ChunkIndex = chunkIndex++,
                            PageNumber = 0,
                            Content    = subChunk
                        });
                    }
                }
            }
        }

        return chunks;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into sub-strings no longer than <paramref name="maxLength"/>,
    /// preferring to break at sentence endings (. ! ?) or newlines.
    /// </summary>
    private static IEnumerable<string> SplitParagraph(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= maxLength)
            {
                yield return text[start..].Trim();
                yield break;
            }

            // Search for a sentence boundary within [start, start+maxLength).
            // Count must be exactly the window size so we never look before `start`.
            var windowEnd  = start + maxLength;
            var windowSize = windowEnd - start;           // == maxLength, but explicit
            var breakPoint = text.LastIndexOfAny(['.', '!', '?', '\n'], windowEnd - 1, windowSize);

            var length = (breakPoint > start) ? breakPoint - start + 1 : maxLength;
            var slice  = text.Substring(start, length).Trim();

            if (!string.IsNullOrWhiteSpace(slice))
                yield return slice;

            start += length;
        }
    }

    /// <summary>
    /// Merges consecutive chunks that are shorter than <see cref="DocumentProcessingOptions.MinChunkCharacters"/>
    /// into their successor, stopping when the combined text would exceed
    /// <see cref="DocumentProcessingOptions.MaxChunkCharacters"/>.
    /// This prevents tiny heading-only or bullet-point chunks from being embedded in isolation.
    /// </summary>
    private static List<DocumentChunk> MergeSmallChunks(List<DocumentChunk> chunks, DocumentProcessingOptions options)
    {
        if (options.MinChunkCharacters <= 0 || chunks.Count <= 1)
            return chunks;

        var merged = new List<DocumentChunk>(chunks.Count);
        var buffer = new System.Text.StringBuilder();
        int bufferPage = 0;
        var documentId = chunks[0].DocumentId;

        void FlushBuffer()
        {
            if (buffer.Length == 0) return;
            merged.Add(new DocumentChunk
            {
                Id        = Guid.NewGuid(),
                DocumentId = documentId,
                ChunkIndex = merged.Count,
                PageNumber  = bufferPage,
                Content     = buffer.ToString().Trim()
            });
            buffer.Clear();
        }

        foreach (var chunk in chunks)
        {
            // A chunk that begins with a heading is usually a natural boundary.
            // However, if it is extremely tiny (< MinChunkCharacters/2, e.g. a bare
            // section title with no body), still absorb it so it doesn't become an
            // isolated heading-only chunk that embeds poorly.
            var isTinyHeading = chunk.Content.TrimStart().StartsWith('#')
                                && chunk.Content.Length < options.MinChunkCharacters / 2;

            if (buffer.Length == 0)
            {
                // Start a fresh buffer with this chunk.
                buffer.Append(chunk.Content);
                bufferPage = chunk.PageNumber;
            }
            else if (!chunk.Content.TrimStart().StartsWith('#') || isTinyHeading)
            {
                // Normal (non-heading) chunk, or tiny heading: merge if buffer is small
                // and the combined result stays within max.
                if (buffer.Length < options.MinChunkCharacters &&
                    buffer.Length + chunk.Content.Length + 2 <= options.MaxChunkCharacters)
                {
                    buffer.Append("\n\n");
                    buffer.Append(chunk.Content);
                }
                else
                {
                    FlushBuffer();
                    buffer.Append(chunk.Content);
                    bufferPage = chunk.PageNumber;
                }
            }
            else
            {
                // Full-size heading chunk — always starts a new buffer.
                FlushBuffer();
                buffer.Append(chunk.Content);
                bufferPage = chunk.PageNumber;
            }
        }

        // If last buffered piece is tiny, try to absorb it into the previous merged chunk.
        if (buffer.Length > 0)
        {
            if (merged.Count > 0 && buffer.Length < options.MinChunkCharacters)
            {
                var last    = merged[^1];
                var combined = last.Content + "\n\n" + buffer.ToString().Trim();
                if (combined.Length <= options.MaxChunkCharacters)
                {
                    last.Content = combined;
                    return merged;   // already attached — no new chunk needed
                }
            }
            FlushBuffer();
        }

        return merged;
    }

    /// <summary>
    /// Prepends the last <see cref="DocumentProcessingOptions.ChunkOverlapCharacters"/> characters
    /// of chunk[i-1] to chunk[i], breaking at a word boundary so the model receives coherent text.
    /// This preserves context that would otherwise be lost at a hard chunk boundary.
    /// </summary>
    private static void ApplyChunkOverlap(List<DocumentChunk> chunks, DocumentProcessingOptions options)
    {
        if (options.ChunkOverlapCharacters <= 0 || chunks.Count <= 1)
            return;

        for (var i = 1; i < chunks.Count; i++)
        {
            var current = chunks[i].Content;

            // Skip overlap when the chunk starts with a markdown heading (##/###).
            // A heading marks a natural section boundary — prepending context from
            // the previous section creates incoherent fragments, not useful context.
            if (current.TrimStart().StartsWith('#'))
                continue;

            var prev = chunks[i - 1].Content;
            if (prev.Length == 0) continue;

            // Take up to ChunkOverlapCharacters from the end of the previous chunk.
            var overlap = prev.Length <= options.ChunkOverlapCharacters
                ? prev
                : prev[^options.ChunkOverlapCharacters..];

            // Trim to the first word boundary so we don't start mid-word.
            if (prev.Length > options.ChunkOverlapCharacters)
            {
                var spaceIdx = overlap.IndexOf(' ');
                if (spaceIdx > 0)
                    overlap = overlap[(spaceIdx + 1)..];
            }

            if (!string.IsNullOrWhiteSpace(overlap))
                chunks[i].Content = overlap.TrimStart() + "\n\n" + chunks[i].Content;
        }
    }

    private static void AttachEmbeddings(List<DocumentChunk> chunks, IReadOnlyList<float[]> embeddings)
    {
        for (var i = 0; i < chunks.Count; i++)
            chunks[i].Embedding = new Vector(embeddings[i]);
    }

    private static async Task ExtractKnowledgeAsync(
        IReadOnlyList<DocumentChunk> chunks,
        IKnowledgeExtractionService knowledgeExtractionService,
        IUnitOfWork unitOfWork,
        DocumentProcessingOptions options,
        ILogger logger)
    {
        var extractionDelay = TimeSpan.FromSeconds(options.ExtractionDelaySecs);

        // In-memory dedup across all chunks of this document.
        // Prevents the overlap prefix from generating duplicate entities/relationships
        // that were already stored from the previous chunk's main content.
        var seenEntityNames       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRelationshipKeys  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < chunks.Count; i++)
        {
            if (!ShouldExtractKnowledge(chunks[i].Content))
                continue;

            try
            {
                if (i > 0)
                    await Task.Delay(extractionDelay);

                var extraction = await knowledgeExtractionService.ExtractFromChunkAsync(chunks[i]);

                // ── Entity dedup ─────────────────────────────────────────────────────
                // Keep only entities whose name has not been stored yet for this document.
                // seenEntityNames.Add() returns false if the name already existed.
                var newEntities = extraction.Entities
                    .Where(e => seenEntityNames.Add(e.Name))
                    .ToList();

                // ── Relationship dedup ────────────────────────────────────────────────
                // A relationship is only valid if BOTH its source and target are in the
                // set of entities we're about to insert (same chunk extraction).
                // Referencing an entity that was filtered out would leave a dangling FK.
                var validEntityIds = new HashSet<Guid>(newEntities.Select(e => e.Id));

                var newRelationships = extraction.Relationships
                    .Where(r =>
                        validEntityIds.Contains(r.SourceEntityId) &&
                        validEntityIds.Contains(r.TargetEntityId) &&
                        seenRelationshipKeys.Add(
                            $"{r.SourceEntityId}|{r.TargetEntityId}|{r.RelationType}"))
                    .ToList();

                if (newEntities.Count > 0)
                    await unitOfWork.ExtractedEntities.AddRangeAsync(newEntities);

                if (newRelationships.Count > 0)
                    await unitOfWork.ExtractedRelationships.AddRangeAsync(newRelationships);

                if (newEntities.Count > 0 || newRelationships.Count > 0)
                    await unitOfWork.SaveChangesAsync();

                if (newEntities.Count < extraction.Entities.Count)
                    logger.LogDebug(
                        "Chunk {ChunkId}: skipped {Dupes} duplicate entities from overlap text.",
                        chunks[i].Id,
                        extraction.Entities.Count - newEntities.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Knowledge extraction failed for chunk {ChunkId}. Continuing.", chunks[i].Id);
                continue;
            }
        }
    }


    private static bool ShouldExtractKnowledge(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var normalized = content.Trim();
        if (normalized.Length < 40)
            return false;

        if (MarkdownOnlyRegex.IsMatch(normalized))
            return false;

        if (DataUriImageRegex.IsMatch(normalized))
            return false;

        var alphaNumericCount = normalized.Count(char.IsLetterOrDigit);
        return alphaNumericCount >= 20;
    }

    private static async Task UpdateDocumentStatusAsync(
        IUnitOfWork unitOfWork,
        Document document,
        DocumentStatus status)
    {
        document.Status = status;
        await unitOfWork.Documents.Update(document);
        await unitOfWork.SaveChangesAsync();
    }

    // FIX 1: Accepts byte[] instead of opening the file stream a second time.
    private static async Task<Document> SaveDocumentRecordAsync(
        byte[] fileBytes,
        string fileName,
        Guid userId,
        IUnitOfWork unitOfWork)
    {
        var filePath = await SaveFileAsync(fileBytes, fileName);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = Path.GetFileNameWithoutExtension(fileName),
            FilePath = filePath,
            Status = DocumentStatus.Pending
        };

        await unitOfWork.Documents.AddAsync(document);
        await unitOfWork.SaveChangesAsync();

        return document;
    }

    // FIX 1: Writes from the already-loaded byte[] — no stream re-read.
    private static async Task<string> SaveFileAsync(byte[] fileBytes, string fileName)
    {
        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
        Directory.CreateDirectory(uploadsDir);

        var uniqueFileName = $"{Guid.NewGuid()}_{fileName}";
        var filePath = Path.Combine(uploadsDir, uniqueFileName);

        await File.WriteAllBytesAsync(filePath, fileBytes);

        return filePath;
    }

    private sealed record FilePayload(byte[] FileBytes, string FileName, string? ContentType);
}
