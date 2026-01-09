using GraphPaper.Domain.Enums;

namespace GraphPaper.Domain.Entities
{
    public class ChatMessage : BaseEntity
    {
        public Guid SessionId { get; set; }
        public ChatRole Role { get; set; }
        public string Content { get; set; } = string.Empty;

        // Navigation
        public ChatSession Session { get; set; } = null!;
        public ICollection<MessageCitation> Citations { get; set; } = new List<MessageCitation>();
    }
}
