using GraphPaper.Domain.Enums;

namespace GraphPaper.Application.DTOs.DocumentDTO;

public class DocumentDetailDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public DocumentStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    public DocumentStatsDto Stats { get; set; } = null!;
    public List<ChunkDto> Chunks { get; set; } = [];
    public List<EntityDto> Entities { get; set; } = [];
    public List<RelationshipDto> Relationships { get; set; } = [];
}

public class DocumentStatsDto
{
    public int TotalChunks { get; set; }
    public int TotalEntities { get; set; }
    public int TotalRelationships { get; set; }
}

public class ChunkDto
{
    public Guid Id { get; set; }
    public int ChunkIndex { get; set; }
    public int PageNumber { get; set; }
    public string Content { get; set; } = null!;
    public bool HasEmbedding { get; set; }
    public List<EntityDto> Entities { get; set; } = [];
}

public class EntityDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string EntityType { get; set; } = null!;
    public string Description { get; set; } = null!;
}

public class RelationshipDto
{
    public Guid Id { get; set; }
    public string SourceEntity { get; set; } = null!;
    public string TargetEntity { get; set; } = null!;
    public string RelationType { get; set; } = null!;
    public float ConfidenceScore { get; set; }
}
