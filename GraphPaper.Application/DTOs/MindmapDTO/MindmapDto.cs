namespace GraphPaper.Application.DTOs.MindmapDTO;

public class MindmapDto
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string MermaidCode { get; set; } = null!;
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}