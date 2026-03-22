using GraphPaper.Application.Interfaces;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace GraphPaper.Application.Services;

/// <summary>
/// Describes inline images using Gemini Vision (gemini-2.0-flash).
/// Uses GEMINI_KNOWLEDGE_API_KEY (key 2) — does NOT consume embedding quota.
/// Singleton with in-memory SHA-256 cache: same image across paragraphs/documents = 1 API call total.
/// </summary>
public sealed class GeminiVisionService : IImageDescriptionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;

    private const string MODEL_ID = "gemini-2.0-flash";
    private const string URL_TEMPLATE = "v1beta/models/{0}:generateContent?key={1}";

    private const string VISION_PROMPT =
        "This image contains a mathematical formula, equation, or variable symbol " +
        "embedded in an academic document. " +
        "Extract and return ONLY the formula or symbol as plain text. " +
        "Rules:\n" +
        "- Single variable (e.g. italic M, v, c): return just the letter\n" +
        "- Full formula (e.g. M=PQ/V): return it as-is with operators\n" +
        "- Complex formula: use standard notation, e.g. m' = (m/v) × 100%\n" +
        "- Do NOT add explanation, LaTeX delimiters, or markdown\n" +
        "- If the image has no mathematical content: return empty string\n" +
        "Examples of correct output: M | v | T - H - T' | W = c + v + m | m' = (m/v) × 100%";

    private readonly ConcurrentDictionary<string, string> _cache = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public GeminiVisionService(IHttpClientFactory httpClientFactory, string apiKey)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = apiKey;
    }

    public async Task<string> DescribeAsync(byte[] imageBytes, string mimeType = "image/png")
    {
        if (imageBytes is null || imageBytes.Length == 0)
            return string.Empty;

        var cacheKey = ComputeHash(imageBytes);

        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = await CallVisionApiAsync(imageBytes, mimeType);
        _cache[cacheKey] = result;
        return result;
    }

    private async Task<string> CallVisionApiAsync(byte[] imageBytes, string mimeType)
    {
        using var client = _httpClientFactory.CreateClient("GeminiKnowledge");
        var url = string.Format(URL_TEMPLATE, MODEL_ID, _apiKey);
        var base64 = Convert.ToBase64String(imageBytes);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { inline_data = new { mime_type = mimeType, data = base64 } },
                        new { text = VISION_PROMPT }
                    }
                }
            },
            generation_config = new { temperature = 0.0, max_output_tokens = 64 }
        };

        try
        {
            using var response = await client.PostAsJsonAsync(url, requestBody, JsonOpts);
            if (!response.IsSuccessStatusCode)
                return string.Empty;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty;

            return text.Trim().Trim('`', '*', '_');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ComputeHash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }
}
