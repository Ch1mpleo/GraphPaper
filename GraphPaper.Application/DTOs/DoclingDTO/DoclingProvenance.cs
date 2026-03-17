using System.Text.Json.Serialization;

namespace GraphPaper.Application.DTOs.DoclingDTO;

/// <summary>
/// Source location metadata for extracted content.
/// </summary>
public sealed class DoclingProvenance
{
    /// <summary>
    /// 1-based page number in the source document.
    /// </summary>
    [JsonPropertyName("page_no")]
    public int PageNumber { get; set; }
}
