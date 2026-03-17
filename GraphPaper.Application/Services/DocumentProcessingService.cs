using GraphPaper.Application.Interfaces;
using GraphPaper.Application.DTOs.DoclingDTO;
using GraphPaper.Domain.Entities;
using GraphPaper.Domain.Enums;
using GraphPaper.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pgvector;
using System.Text.RegularExpressions;

namespace GraphPaper.Application.Services;

public sealed class DocumentProcessingService : IDocumentProcessingService
{
    private static readonly TimeSpan ExtractionDelay = TimeSpan.FromSeconds(4);
    private static readonly Regex DataUriImageRegex = new(@"!\[[^\]]*\]\(data:image\/[a-zA-Z]+;base64,", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MarkdownOnlyRegex = new(@"^[#>*_`\-\s]+$", RegexOptions.Compiled);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClaimsService _claimsService;
    private readonly ILogger<DocumentProcessingService> _logger;

    public DocumentProcessingService(
        IServiceScopeFactory scopeFactory,
        IClaimsService claimsService,
        ILogger<DocumentProcessingService> logger)
    {
        _scopeFactory = scopeFactory;
        _claimsService = claimsService;
        _logger = logger;
    }

    public async Task<Document> IngestAsync(IFormFile file)
    {
        if (file is null)
            throw new ArgumentNullException(nameof(file));

        var userId = _claimsService.GetCurrentUserId;

        if (userId == Guid.Empty)
            throw new ArgumentException("User id is required.", nameof(userId));

        Document document;
        using (var scope = _scopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            document = await SaveDocumentRecordAsync(file, userId, unitOfWork);
        }

        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        var filePayload = new FilePayload(memoryStream.ToArray(), file.FileName, file.ContentType);

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
        ILogger logger)
    {
        var document = await unitOfWork.Documents.GetByIdAsync(documentId);
        if (document is null)
            return;

        try
        {
            await UpdateDocumentStatusAsync(unitOfWork, document, DocumentStatus.Chunking);

            var doclingResult = await doclingClient.ParseAsync(filePayload.FileBytes, filePayload.FileName, filePayload.ContentType);
            var chunks = BuildChunksFromDocling(doclingResult.Document, document.Id, logger);

            if (chunks.Count == 0)
                throw new InvalidOperationException("Could not extract any text from the document.");

            await UpdateDocumentStatusAsync(unitOfWork, document, DocumentStatus.Extracting);

            var texts = chunks.Select(c => c.Content).ToList();
            var embeddings = await embeddingService.GetBatchEmbeddingsAsync(texts);

            AttachEmbeddings(chunks, embeddings);

            await unitOfWork.DocumentChunks.AddRangeAsync(chunks);
            await unitOfWork.SaveChangesAsync();

            await ExtractKnowledgeAsync(chunks, knowledgeExtractionService, unitOfWork, logger);

            await UpdateDocumentStatusAsync(unitOfWork, document, DocumentStatus.Ready);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process document {DocumentId}", documentId);
            await UpdateDocumentStatusAsync(unitOfWork, document, DocumentStatus.Failed);
        }
    }

    private static List<DocumentChunk> BuildChunksFromDocling(DoclingDocument? document, Guid documentId, ILogger logger)
    {
        if (document is null)
            return [];

        var chunksFromTextItems = BuildChunksFromTextItems(document.Texts, documentId);
        if (chunksFromTextItems.Count > 0)
            return chunksFromTextItems;

        var chunksFromMarkdown = BuildChunksFromMarkdown(document.MarkdownContent, documentId);
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
            var content = textItem.Text.Trim();
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

    private static List<DocumentChunk> BuildChunksFromMarkdown(string? markdownContent, Guid documentId)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
            return [];

        var parts = markdownContent
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var chunks = new List<DocumentChunk>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            chunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                ChunkIndex = i,
                PageNumber = 0,
                Content = parts[i]
            });
        }

        return chunks;
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
        ILogger logger)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            if (!ShouldExtractKnowledge(chunks[i].Content))
                continue;

            try
            {
                if (i > 0)
                    await Task.Delay(ExtractionDelay);

                var extraction = await knowledgeExtractionService.ExtractFromChunkAsync(chunks[i]);

                if (extraction.Entities.Count > 0)
                    await unitOfWork.ExtractedEntities.AddRangeAsync(extraction.Entities);

                if (extraction.Relationships.Count > 0)
                    await unitOfWork.ExtractedRelationships.AddRangeAsync(extraction.Relationships);

                await unitOfWork.SaveChangesAsync();
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

    private static async Task<Document> SaveDocumentRecordAsync(IFormFile file, Guid userId, IUnitOfWork unitOfWork)
    {
        await using var stream = file.OpenReadStream();
        var filePath = await SaveFileAsync(stream, file.FileName);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = Path.GetFileNameWithoutExtension(file.FileName),
            FilePath = filePath,
            Status = DocumentStatus.Pending
        };

        await unitOfWork.Documents.AddAsync(document);
        await unitOfWork.SaveChangesAsync();

        return document;
    }

    private static async Task<string> SaveFileAsync(Stream stream, string fileName)
    {
        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
        Directory.CreateDirectory(uploadsDir);

        var uniqueFileName = $"{Guid.NewGuid()}_{fileName}";
        var filePath = Path.Combine(uploadsDir, uniqueFileName);

        await using var fs = new FileStream(filePath, FileMode.Create);
        await stream.CopyToAsync(fs);

        return filePath;
    }

    private sealed record FilePayload(byte[] FileBytes, string FileName, string? ContentType);
}
