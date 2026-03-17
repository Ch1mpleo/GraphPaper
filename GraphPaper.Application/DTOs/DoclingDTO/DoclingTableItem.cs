using System.Text.Json.Serialization;

namespace GraphPaper.Application.DTOs.DoclingDTO;

/// <summary>
/// Table segment extracted by Docling.
/// </summary>
public sealed class DoclingTableItem
{
    /// <summary>
    /// Table content serialized as text (typically markdown).
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Provenance metadata for this table segment.
    /// </summary>
    [JsonPropertyName("prov")]
    public List<DoclingProvenance>? Provenance { get; set; }
}
