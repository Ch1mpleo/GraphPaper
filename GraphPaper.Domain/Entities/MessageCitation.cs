namespace GraphPaper.Domain.Entities
{
    public class MessageCitation : BaseEntity
    {
        public Guid MessageId { get; set; }
        public Guid ChunkId { get; set; }
        public float RelevanceScore { get; set; }

        // Navigation
        public ChatMessage Message { get; set; } = null!;
        public DocumentChunk Chunk { get; set; } = null!;
    }
}
