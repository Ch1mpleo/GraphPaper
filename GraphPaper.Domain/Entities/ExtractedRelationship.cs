namespace GraphPaper.Domain.Entities
{
    public class ExtractedRelationship : BaseEntity
    {
        public Guid SourceEntityId { get; set; }
        public Guid TargetEntityId { get; set; }

        public string RelationType { get; set; } = string.Empty;
        public float ConfidenceScore { get; set; }

        // Navigation
        public ExtractedEntity SourceEntity { get; set; } = null!;
        public ExtractedEntity TargetEntity { get; set; } = null!;
    }
}
