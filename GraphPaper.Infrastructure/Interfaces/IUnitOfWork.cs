using GraphPaper.Domain.Entities;

namespace GraphPaper.Infrastructure.Interfaces;

public interface IUnitOfWork : IDisposable
{
    public IGenericRepository<User> Users { get; }
    public IGenericRepository<Document> Documents { get; }
    public IGenericRepository<DocumentChunk> DocumentChunks { get; }
    public IGenericRepository<ExtractedEntity> ExtractedEntities { get; }
    public IGenericRepository<ExtractedRelationship> ExtractedRelationships { get; }
    public IGenericRepository<ChatSession> ChatSessions { get; }
    public IGenericRepository<ChatMessage> ChatMessages { get; }
    public IGenericRepository<MessageCitation> MessageCitations { get; }

    Task<int> SaveChangesAsync();
}