using GraphPaper.Application.DTOs.MindmapDTO;
using GraphPaper.Domain.Entities;

namespace GraphPaper.Application.Interfaces;

public interface IMindmapService
{
    Task<DocumentMindmap> GenerateAndSaveAsync(Guid documentId);
    Task<DocumentMindmap?> GetByDocumentIdAsync(Guid documentId);

    // Returns dict of Mermaid nodeId → entity details.
    // Key format: "N" + entityId.ToString("N") (32 hex chars, no hyphens)
    Task<Dictionary<string, MindmapEntityDto>> BuildEntityIndexAsync(Guid documentId);
}