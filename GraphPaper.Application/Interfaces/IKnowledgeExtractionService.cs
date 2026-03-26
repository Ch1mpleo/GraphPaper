using GraphPaper.Domain.Entities;

namespace GraphPaper.Application.Interfaces;

public interface IKnowledgeExtractionService
{
    /// <summary>
    /// Pass 1 — Entity-only extraction from a chunk.
    /// Returns new ExtractedEntity objects (IDs already assigned).
    /// </summary>
    Task<List<ExtractedEntity>> ExtractEntitiesAsync(DocumentChunk chunk);

    /// <summary>
    /// Pass 2 — Relationship-only extraction from a chunk.
    /// <paramref name="globalEntityMap"/> maps normalised entity name → persisted entity ID;
    /// source/target names are resolved against this map so cross-chunk FKs are valid.
    /// </summary>
    Task<List<ExtractedRelationship>> ExtractRelationshipsAsync(
        DocumentChunk chunk,
        IReadOnlyDictionary<string, Guid> globalEntityMap);
}

/// <summary>Kept for use by IRelationshipEnrichmentService.</summary>
public class KnowledgeExtractionResult
{
    public List<ExtractedEntity> Entities { get; set; } = [];
    public List<ExtractedRelationship> Relationships { get; set; } = [];
}
