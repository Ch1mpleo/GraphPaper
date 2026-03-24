namespace GraphPaper.Application.DTOs.MindmapDTO;

public sealed class MindmapDto
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string MermaidCode { get; set; } = null!;
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Key = Mermaid node ID ("N" + entityId.ToString("N"), no hyphens)
    // Value = entity detail object for frontend click-through
    public Dictionary<string, MindmapEntityDto> EntityIndex { get; set; } = [];
}

public sealed class MindmapEntityDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string EntityType { get; set; } = null!;
    public string Description { get; set; } = null!;
}
