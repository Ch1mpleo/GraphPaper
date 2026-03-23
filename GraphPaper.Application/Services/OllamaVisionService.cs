using GraphPaper.Application.Interfaces;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace GraphPaper.Application.Services;

/// <summary>
/// Describes inline images using a local Ollama multimodal model.
/// Cached by SHA-256 hash to avoid repeated calls for identical images.
/// </summary>
public sealed class OllamaVisionService : IImageDescriptionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly string _modelId;

    private const string DEFAULT_BASE_URL = "http://host.docker.internal:11434";
    private const string DEFAULT_MODEL = "llama3.2-vision:11b";

    private const string VISION_PROMPT =
        "This image is embedded in an academic document. " +
        "Extract and return ONLY the mathematical formula, equation, or variable symbol as plain text. " +
        "Rules:\n" +
        "- Single variable (e.g. italic M, v, c): return just the letter\n" +
        "- Full formula (e.g. M=PQ/V): return it as-is with operators\n" +
        "- Complex formula: use standard notation, e.g. m' = (m/v) × 100%\n" +
        "- Do NOT add explanation, LaTeX delimiters, or markdown\n" +
        "- If the image has no mathematical content: return empty string";

    private readonly ConcurrentDictionary<string, string> _cache = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public OllamaVisionService(
        IHttpClientFactory httpClientFactory,
        string? baseUrl = null,
        string? modelId = null)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = baseUrl ?? DEFAULT_BASE_URL;
        _modelId = modelId ?? DEFAULT_MODEL;
    }

    public async Task<string> DescribeAsync(byte[] imageBytes, string mimeType = "image/png")
    {
        if (imageBytes is null || imageBytes.Length == 0)
            return string.Empty;

        var cacheKey = ComputeHash(imageBytes);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = await CallOllamaAsync(imageBytes);
        _cache[cacheKey] = result;
        return result;
    }

    private async Task<string> CallOllamaAsync(byte[] imageBytes)
    {
        using var client = _httpClientFactory.CreateClient("OllamaVision");
        var url = $"{_baseUrl}/api/generate";
        var base64 = Convert.ToBase64String(imageBytes);

        var requestBody = new
        {
            model = _modelId,
            prompt = VISION_PROMPT,
            images = new[] { base64 },
            stream = false,
            options = new { temperature = 0.0 }
        };

        try
        {
            using var response = await client.PostAsJsonAsync(url, requestBody, JsonOptions);
            if (!response.IsSuccessStatusCode)
                return string.Empty;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var text = doc.RootElement
                .GetProperty("response")
                .GetString() ?? string.Empty;

            return text
                .Replace("\uFFFD", string.Empty)
                .Trim()
                .Trim('`', '*', '_');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ComputeHash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
