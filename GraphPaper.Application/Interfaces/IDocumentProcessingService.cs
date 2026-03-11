namespace GraphPaper.Application.Interfaces;

public interface IDocumentProcessingService
{
    /// <summary>
    /// Full pipeline: save file → parse text → chunk → embed → persist to DB.
    /// Returns the created Document Id.
    /// </summary>
    Task<Guid> ProcessDocumentAsync(Stream fileStream, string fileName);
}
