using System.Text.Json.Serialization;

namespace GraphPaper.Application.DTOs.DoclingDTO;

/// <summary>
/// Parsed document content sections returned by Docling.
/// </summary>
public sealed class DoclingDocument
{
    /// <summary>
    /// Full markdown content representation.
    /// </summary>
    [JsonPropertyName("md_content")]
    public string? MarkdownContent { get; set; }

    /// <summary>
    /// Structured text items extracted from the document.
    /// </summary>
    [JsonPropertyName("texts")]
    public List<DoclingTextItem>? Texts { get; set; }

    /// <summary>
    /// Structured table items extracted from the document.
    /// </summary>
    [JsonPropertyName("tables")]
    public List<DoclingTableItem>? Tables { get; set; }
}
