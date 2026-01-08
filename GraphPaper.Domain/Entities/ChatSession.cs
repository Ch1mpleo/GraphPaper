namespace GraphPaper.Domain.Entities
{
    public class ChatSession : BaseEntity
    {
        public Guid UserId { get; set; }
        public Guid DocumentId { get; set; }
        public string Title { get; set; } = "New Chat";

        // Navigation
        public User User { get; set; } = null!;
        public Document Document { get; set; } = null!;
        public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
    }
}
