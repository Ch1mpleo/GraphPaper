using GraphPaper.Application.Interfaces;
using GraphPaper.Domain.Entities;
using GraphPaper.Domain.Enums;
using GraphPaper.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Http;
using Pgvector;

namespace GraphPaper.Application.Services;

public sealed class DocumentProcessingService : IDocumentProcessingService
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

    public async Task<Document> IngestAsync(IFormFile file)
    {
        if (file is null)
            throw new ArgumentNullException(nameof(file));

        var userId = _claimsService.GetCurrentUserId;

        if (userId == Guid.Empty)
            throw new ArgumentException("User id is required.", nameof(userId));

        var document = await SaveDocumentRecordAsync(file, userId);

        await using var fileStream = file.OpenReadStream();
        using var memoryStream = new MemoryStream();
        await fileStream.CopyToAsync(memoryStream);
        var fileBytes = memoryStream.ToArray();

        _ = Task.Run(() => ProcessDocumentAsync(document.Id, file.FileName, fileBytes));

        return document;
    }

    private async Task ProcessDocumentAsync(Guid documentId, string fileName, byte[] fileBytes)
    {
        var document = await _unitOfWork.Documents.GetByIdAsync(documentId);
        if (document is null)
            return;

        try
        {
            document.Status = DocumentStatus.Chunking;
            await _unitOfWork.Documents.Update(document);
            await _unitOfWork.SaveChangesAsync();

            using var parseStream = new MemoryStream(fileBytes);
            var pages = _parserService.Parse(parseStream, fileName);

            if (pages.Count == 0)
                throw new InvalidOperationException("Could not extract any text from the document.");

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

            document.Status = DocumentStatus.Extracting;
            await _unitOfWork.Documents.Update(document);
            await _unitOfWork.SaveChangesAsync();

            var texts = chunks.Select(c => c.Content).ToList();
            var embeddings = await _embeddingService.GetBatchEmbeddingsAsync(texts);

            for (int i = 0; i < chunks.Count; i++)
            {
                chunks[i].Embedding = new Vector(embeddings[i]);
            }

            await _unitOfWork.DocumentChunks.AddRangeAsync(chunks);
            await _unitOfWork.SaveChangesAsync();

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
                    break;
                }
            }

            document.Status = DocumentStatus.Ready;
            await _unitOfWork.Documents.Update(document);
            await _unitOfWork.SaveChangesAsync();
        }
        catch
        {
            document.Status = DocumentStatus.Failed;
            await _unitOfWork.Documents.Update(document);
            await _unitOfWork.SaveChangesAsync();
        }
    }

    private async Task<Document> SaveDocumentRecordAsync(IFormFile file, Guid userId)
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

        await _unitOfWork.Documents.AddAsync(document);
        await _unitOfWork.SaveChangesAsync();

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
}
