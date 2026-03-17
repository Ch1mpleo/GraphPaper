using System.Text.Json.Serialization;

namespace GraphPaper.Application.DTOs.DoclingDTO;

/// <summary>
/// Text segment extracted by Docling.
/// </summary>
public sealed class DoclingTextItem
{
    /// <summary>
    /// Segment text content.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Segment label such as paragraph or section_header.
    /// </summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Optional heading level.
    /// </summary>
    [JsonPropertyName("level")]
    public int? Level { get; set; }

    /// <summary>
    /// Provenance metadata for this text segment.
    /// </summary>
    [JsonPropertyName("prov")]
    public List<DoclingProvenance>? Provenance { get; set; }
}
