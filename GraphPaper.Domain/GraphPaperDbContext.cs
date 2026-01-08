using GraphPaper.Domain.Commons;
using GraphPaper.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GraphPaper.Domain
{
    public class GraphPaperDbContext : DbContext
    {
        public GraphPaperDbContext(DbContextOptions<GraphPaperDbContext> options)
            : base(options)
        {
        }

        // DbSets
        public DbSet<User> Users { get; set; }
        public DbSet<Document> Documents { get; set; }
        public DbSet<DocumentChunk> DocumentChunks { get; set; }
        public DbSet<ExtractedEntity> ExtractedEntities { get; set; }
        public DbSet<ExtractedRelationship> ExtractedRelationships { get; set; }
        public DbSet<ChatSession> ChatSessions { get; set; }
        public DbSet<ChatMessage> ChatMessages { get; set; }
        public DbSet<MessageCitation> MessageCitations { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 1. Enable PostgreSQL Vector Extension
            modelBuilder.HasPostgresExtension("vector");

            // 2. Enum Conversions 
            modelBuilder.UseStringForEnums();

            // 3. Graph Relationships 
            modelBuilder.Entity<ExtractedRelationship>(entity =>
            {
                entity.HasOne(e => e.SourceEntity)
                      .WithMany(n => n.OutgoingEdges)
                      .HasForeignKey(e => e.SourceEntityId)
                      .OnDelete(DeleteBehavior.Restrict); // Prevent cascading delete cycles

                entity.HasOne(e => e.TargetEntity)
                      .WithMany(n => n.IncomingEdges)
                      .HasForeignKey(e => e.TargetEntityId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // 4. Citation Relationships
            modelBuilder.Entity<MessageCitation>(entity =>
            {
                entity.HasOne(c => c.Message)
                      .WithMany(m => m.Citations)
                      .HasForeignKey(c => c.MessageId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(c => c.Chunk)
                      .WithMany()
                      .HasForeignKey(c => c.ChunkId)
                      .OnDelete(DeleteBehavior.Restrict); // Don't delete chunks just because a chat is deleted
            });
        }
    }
}
