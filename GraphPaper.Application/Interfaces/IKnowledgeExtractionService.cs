using GraphPaper.Domain.Entities;

namespace GraphPaper.Application.Interfaces;

public interface IKnowledgeExtractionService
{
    /// <summary>
    /// Calls Gemini LLM to extract entities and relationships from a chunk's text.
    /// </summary>
    Task<KnowledgeExtractionResult> ExtractFromChunkAsync(DocumentChunk chunk);
}

public class KnowledgeExtractionResult
{
    public List<ExtractedEntity> Entities { get; set; } = [];
    public List<ExtractedRelationship> Relationships { get; set; } = [];
}
