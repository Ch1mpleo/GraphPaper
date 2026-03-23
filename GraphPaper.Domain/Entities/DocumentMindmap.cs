namespace GraphPaper.Domain.Entities;

public class DocumentMindmap : BaseEntity
{
    public Guid DocumentId { get; set; }

    /// <summary>
    /// Mermaid graph/mindmap syntax generated from the document's knowledge graph.
    /// </summary>
    public string MermaidCode { get; set; } = string.Empty;

    /// <summary>
    /// Total number of nodes (entities) rendered in this mindmap.
    /// </summary>
    public int NodeCount { get; set; }

    /// <summary>
    /// Total number of edges (relationships) rendered in this mindmap.
    /// </summary>
    public int EdgeCount { get; set; }

    // Navigation
    public Document Document { get; set; } = null!;
}