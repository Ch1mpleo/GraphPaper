namespace GraphPaper.Domain.Entities
{
    public class User : BaseEntity
    {
        public const string RoleCustomer = "Customer";

        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string HashedPassword { get; set; } = string.Empty;

        // 1 role
        public string Role { get; set; } = RoleCustomer;

        // Navigation
        public ICollection<Document> Documents { get; set; } = new List<Document>();
        public ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();
    }
}
