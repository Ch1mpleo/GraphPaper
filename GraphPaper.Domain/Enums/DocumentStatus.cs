namespace GraphPaper.Domain.Enums
{
    public enum DocumentStatus
    {
        Pending = 1,          // Uploaded, queued
        Chunking = 2,         // Splitting document into chunks + embedding
        ExtractingEntities = 3,       // Pass 1: entity-only LLM extraction
        ExtractingRelationships = 4,  // Pass 2: relationship-only LLM extraction
        EnrichingRelationships = 5,   // Step 3: cross-chunk enrichment via embedding similarity
        GeneratingMindmap = 6, // Building mindmap from knowledge graph
        Ready = 7,
        Failed = 8
    }
}
