using GraphPaper.Application.Interfaces;
using GraphPaper.Application.Utils;
using GraphPaper.Domain.Entities;
using GraphPaper.Domain.Enums;
using GraphPaper.Infrastructure.Interfaces;
using Pgvector;

namespace GraphPaper.Application.Services;

public class DocumentProcessingService : IDocumentProcessingService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDocumentParserService _parserService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IKnowledgeExtractionService _knowledgeExtractionService;
    private readonly IClaimsService _claimsService;

    public DocumentProcessingService(
        IUnitOfWork unitOfWork,
        IDocumentParserService parserService,
        IEmbeddingService embeddingService,
        IKnowledgeExtractionService knowledgeExtractionService,
        IClaimsService claimsService)
    {
        _unitOfWork = unitOfWork;
        _parserService = parserService;
        _embeddingService = embeddingService;
        _knowledgeExtractionService = knowledgeExtractionService;
        _claimsService = claimsService;
    }

    public async Task<Guid> ProcessDocumentAsync(Stream fileStream, string fileName)
    {
        // 1. Buffer the stream so it can be read twice (save + parse)
        using var memoryStream = new MemoryStream();
        await fileStream.CopyToAsync(memoryStream);

        // 2. Save file to disk
        memoryStream.Position = 0;
        var filePath = await SaveFileAsync(memoryStream, fileName);

        // 3. Create Document record (Pending)
        var userId = _claimsService.GetCurrentUserId;

        var document = new Document
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = Path.GetFileNameWithoutExtension(fileName),
            FilePath = filePath,
            Status = DocumentStatus.Pending
        };
        await _unitOfWork.Documents.AddAsync(document);
        await _unitOfWork.SaveChangesAsync();

        try
        {
            // 4. Parse file → extract text by pages
            document.Status = DocumentStatus.Chunking;
            await _unitOfWork.Documents.Update(document);
            await _unitOfWork.SaveChangesAsync();

            memoryStream.Position = 0;
            var pages = _parserService.Parse(memoryStream, fileName);

            if (pages.Count == 0)
                throw ErrorHelper.BadRequest("Could not extract any text from the document.");

            // 5. Build chunk entities
            var chunks = new List<DocumentChunk>();
            int chunkIndex = 0;

            foreach (var page in pages)
            {
                chunks.Add(new DocumentChunk
                {
                    Id = Guid.NewGuid(),
                    DocumentId = document.Id,
                    ChunkIndex = chunkIndex++,
                    PageNumber = page.PageNumber,
                    Content = page.Content
                });
            }

            // 6. Generate embeddings
            document.Status = DocumentStatus.Extracting;
            await _unitOfWork.Documents.Update(document);
            await _unitOfWork.SaveChangesAsync();

            var texts = chunks.Select(c => c.Content).ToList();
            var embeddings = await _embeddingService.GetBatchEmbeddingsAsync(texts);

            for (int i = 0; i < chunks.Count; i++)
            {
                chunks[i].Embedding = new Vector(embeddings[i]);
            }

            // 7. Persist chunks
            await _unitOfWork.DocumentChunks.AddRangeAsync(chunks);
            await _unitOfWork.SaveChangesAsync();

            // 8. Extract knowledge graph (entities + relationships) from each chunk
            // Saves per-chunk so that one failure doesn't lose all previous extractions
            // Delay between calls to respect free-tier per-minute rate limits
            for (int i = 0; i < chunks.Count; i++)
            {
                try
                {
                    if (i > 0)
                        await Task.Delay(TimeSpan.FromSeconds(4));

                    var extraction = await _knowledgeExtractionService.ExtractFromChunkAsync(chunks[i]);

                    if (extraction.Entities.Count > 0)
                        await _unitOfWork.ExtractedEntities.AddRangeAsync(extraction.Entities);

                    if (extraction.Relationships.Count > 0)
                        await _unitOfWork.ExtractedRelationships.AddRangeAsync(extraction.Relationships);

                    await _unitOfWork.SaveChangesAsync();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Rate limit, timeout, or API error — stop extraction, keep what's already saved
                    break;
                }
            }

            // 9. Mark document as Ready
            document.Status = DocumentStatus.Ready;
            await _unitOfWork.Documents.Update(document);
            await _unitOfWork.SaveChangesAsync();

            return document.Id;
        }
        catch
        {
            document.Status = DocumentStatus.Failed;
            await _unitOfWork.Documents.Update(document);
            await _unitOfWork.SaveChangesAsync();
            throw;
        }
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
}
