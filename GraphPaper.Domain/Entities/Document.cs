using GraphPaper.Domain.Enums;

namespace GraphPaper.Domain.Entities
{
    public class Document : BaseEntity
    {
        public Guid UserId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

        // Navigation
        public User User { get; set; } = null!;
        public ICollection<DocumentChunk> Chunks { get; set; } = new List<DocumentChunk>();
        public ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();
    }
}
