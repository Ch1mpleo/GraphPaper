using Pgvector;
using System.ComponentModel.DataAnnotations.Schema;

namespace GraphPaper.Domain.Entities
{
    public class DocumentChunk : BaseEntity
    {
        public Guid DocumentId { get; set; }

        //If the answer spans across two chunks, the index allows the AI to "read" the next or previous segment to maintain flow.
        public int ChunkIndex { get; set; }

        // The physical page in the original PDF.
        public int PageNumber { get; set; }

        // The raw cleaned text extracted from the PDF.
        public string Content { get; set; } = string.Empty;

        // AI Vector (1536 dims)
        [Column(TypeName = "vector(1536)")]
        public Vector? Embedding { get; set; }

        // Navigation
        public Document Document { get; set; } = null!;
        public ICollection<ExtractedEntity> ExtractedEntities { get; set; } = new List<ExtractedEntity>();
    }
}
