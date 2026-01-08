namespace GraphPaper.Domain.Entities
{
    public class ExtractedEntity : BaseEntity
    {
        public Guid ChunkId { get; set; } // Changed to Guid
        public string Name { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        // Navigation
        public DocumentChunk Chunk { get; set; } = null!;

        // Graph Navigations
        public ICollection<ExtractedRelationship> OutgoingEdges { get; set; } = new List<ExtractedRelationship>();
        public ICollection<ExtractedRelationship> IncomingEdges { get; set; } = new List<ExtractedRelationship>();
    }
}
