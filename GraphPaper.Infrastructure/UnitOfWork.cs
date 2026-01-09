using GraphPaper.Domain;
using GraphPaper.Domain.Entities;
using GraphPaper.Infrastructure.Interfaces;

namespace GraphPaper.Infrastructure;

public class UnitOfWork : IUnitOfWork
{
    private readonly GraphPaperDbContext _dbContext;

    public UnitOfWork(GraphPaperDbContext dbContext,
        IGenericRepository<User> users,
        IGenericRepository<Document> documents,
        IGenericRepository<DocumentChunk> documentChunks,
        IGenericRepository<ExtractedEntity> extractedEntities,
        IGenericRepository<ExtractedRelationship> extractedRelationships,
        IGenericRepository<ChatSession> chatSessions,
        IGenericRepository<ChatMessage> chatMessages,
        IGenericRepository<MessageCitation> messageCitations)
    {
        _dbContext = dbContext;
        Users = users;
        Documents = documents;
        DocumentChunks = documentChunks;
        ExtractedEntities = extractedEntities;
        ExtractedRelationships = extractedRelationships;
        ChatSessions = chatSessions;
        ChatMessages = chatMessages;
        MessageCitations = messageCitations;
    }

    public IGenericRepository<User> Users { get; }
    public IGenericRepository<Document> Documents { get; }
    public IGenericRepository<DocumentChunk> DocumentChunks { get; }
    public IGenericRepository<ExtractedEntity> ExtractedEntities { get; }
    public IGenericRepository<ExtractedRelationship> ExtractedRelationships { get; }
    public IGenericRepository<ChatSession> ChatSessions { get; }
    public IGenericRepository<ChatMessage> ChatMessages { get; }
    public IGenericRepository<MessageCitation> MessageCitations { get; }

    public void Dispose() => _dbContext.Dispose();

    public async Task<int> SaveChangesAsync()
    {
        return await _dbContext.SaveChangesAsync();
    }
}