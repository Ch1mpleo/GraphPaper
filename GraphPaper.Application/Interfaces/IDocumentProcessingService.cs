using GraphPaper.Domain.Entities;
using Microsoft.AspNetCore.Http;

namespace GraphPaper.Application.Interfaces;

public interface IDocumentProcessingService
{
    /// <summary>
    /// Full pipeline: save file → parse text → chunk → embed → persist to DB.
    /// Returns the created Document Id.
    /// </summary>
    Task<Document> IngestAsync(IFormFile file);
}
