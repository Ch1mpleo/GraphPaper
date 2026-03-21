using GraphPaper.Application.Interfaces;
using System.Net.Http.Json;
using System.Text.Json;

namespace GraphPaper.Application.Services;

public sealed class GeminiEmbeddingService : IEmbeddingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly DocumentProcessingOptions _options;

    private const string ModelId = "gemini-embedding-2-preview";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public GeminiEmbeddingService(IHttpClientFactory httpClientFactory, string apiKey, DocumentProcessingOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = apiKey;
        _options = options;
    }

    public async Task<float[]> GetEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty for embedding.", nameof(text));

        var normalizedText = NormalizeText(text, _options.EmbeddingMaxTextLength);

        using var client = _httpClientFactory.CreateClient("Gemini");
        var url = $"v1beta/models/{ModelId}:embedContent?key={_apiKey}";
        var request = BuildEmbeddingRequest(normalizedText, _options.EmbeddingOutputDimensionality);

        using var response = await client.PostAsJsonAsync(url, request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Gemini Embedding API returned {(int)response.StatusCode}: {errorBody}");
        }

        return await DeserializeEmbeddingAsync(response);
    }

    public async Task<List<float[]>> GetBatchEmbeddingsAsync(List<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var results = new float[texts.Count][];

        foreach (var batch in texts.Select((text, index) => (text, index)).Chunk(_options.EmbeddingBatchSize))
        {
            using var throttler = new SemaphoreSlim(_options.EmbeddingMaxParallel, _options.EmbeddingMaxParallel);

            var tasks = batch.Select(async item =>
            {
                await throttler.WaitAsync();
                try
                {
                    results[item.index] = await GetEmbeddingAsync(item.text);
                }
                finally
                {
                    throttler.Release();
                }
            });

            await Task.WhenAll(tasks);

            await Task.Delay(100);
        }

        return [.. results];
    }

    private static object BuildEmbeddingRequest(string text, int outputDimensionality)
    {
        return new
        {
            content = new
            {
                parts = new[]
                {
                    new { text }
                }
            },
            outputDimensionality
        };
    }

    private static async Task<float[]> DeserializeEmbeddingAsync(HttpResponseMessage response)
    {
        var jsonResponse = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<EmbeddingResponse>(jsonResponse, JsonOptions);

        return result?.Embedding?.Values
               ?? throw new InvalidOperationException("Failed to get embedding from Gemini API");
    }

    private static string NormalizeText(string text, int maxLength)
    {
        if (text.Length > maxLength)
            return text[..maxLength];

        return text;
    }

    private sealed class EmbeddingResponse
    {
        public EmbeddingData? Embedding { get; set; }
    }

    private sealed class EmbeddingData
    {
        public float[]? Values { get; set; }
    }
}
