using GraphPaper.Domain.Enums;

namespace GraphPaper.Domain.Entities
{
    public class ChatMessage : BaseEntity
    {
        public Guid SessionId { get; set; } // Changed to Guid
        public ChatRole Role { get; set; }
        public string Content { get; set; } = string.Empty;

        // CreatedAt is inherited from BaseEntity

        // Navigation
        public ChatSession Session { get; set; } = null!;
        public ICollection<MessageCitation> Citations { get; set; } = new List<MessageCitation>();
    }
}
