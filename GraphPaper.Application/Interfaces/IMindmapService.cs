using GraphPaper.Domain.Entities;

namespace GraphPaper.Application.Interfaces;

public interface IMindmapService
{
    /// <summary>
    /// Builds and persists a Mermaid mindmap from all entities and relationships
    /// belonging to the given document. Overwrites any existing mindmap for that document.
    /// </summary>
    Task<DocumentMindmap> GenerateAndSaveAsync(Guid documentId);

    /// <summary>
    /// Returns the stored mindmap for a document, or null if not yet generated.
    /// </summary>
    Task<DocumentMindmap?> GetByDocumentIdAsync(Guid documentId);
}