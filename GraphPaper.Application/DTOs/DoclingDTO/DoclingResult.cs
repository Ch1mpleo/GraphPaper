using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphPaper.Application.DTOs.DoclingDTO;

public sealed class DoclingResult
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
