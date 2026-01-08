using Pgvector;
using System.ComponentModel.DataAnnotations.Schema;

namespace GraphPaper.Domain.Entities
{
    public class DocumentChunk : BaseEntity
    {
        public Guid DocumentId { get; set; }
        public int ChunkIndex { get; set; }
        public int PageNumber { get; set; }
        public string Content { get; set; } = string.Empty;

        // AI Vector (1536 dims)
        [Column(TypeName = "vector(1536)")]
        public Vector? Embedding { get; set; }

        // Navigation
        public Document Document { get; set; } = null!;
        public ICollection<ExtractedEntity> ExtractedEntities { get; set; } = new List<ExtractedEntity>();
    }
}
