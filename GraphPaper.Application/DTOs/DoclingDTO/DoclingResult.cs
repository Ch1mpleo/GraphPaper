using System.Text.Json.Serialization;

namespace GraphPaper.Application.DTOs.DoclingDTO;

/// <summary>
/// Root response payload from Docling convert endpoint.
/// </summary>
public sealed class DoclingResult
{
    /// <summary>
    /// Parsed document details.
    /// </summary>
    [JsonPropertyName("document")]
    public DoclingDocument? Document { get; set; }
}
