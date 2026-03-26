namespace GraphPaper.Application.Interfaces;

/// <summary>
/// Step 3 of the knowledge extraction pipeline.
/// After the 2-pass per-chunk extraction (entities → relationships), this service
/// uses embedding cosine similarity to discover relationships between entities that
/// appear in *different* chunks and would otherwise never be co-located in one
/// LLM context window.
/// </summary>
public interface IRelationshipEnrichmentService
{
    /// <summary>
    /// Enriches relationships for all extracted entities of a document by
    /// finding the top-K most similar chunks for each entity, building a combined
    /// context window, and calling the LLM to detect cross-chunk relationships.
    /// Only relationships between existing entities are produced — no new entities.
    /// </summary>
    Task EnrichRelationshipsAsync(Guid documentId);
}
