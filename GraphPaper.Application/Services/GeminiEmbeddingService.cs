using GraphPaper.Application.Interfaces;
using System.Net.Http.Json;
using System.Text.Json;

namespace GraphPaper.Application.Services;

public sealed class GeminiEmbeddingService : IEmbeddingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private const string ModelId = "gemini-embedding-2-preview";
    private const int MaxParallelRequests = 5;
    private const int OutputDimensionality = 768;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const int MaxTextLength = 8000;

    public GeminiEmbeddingService(IHttpClientFactory httpClientFactory, string apiKey)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = apiKey;
    }

    public async Task<float[]> GetEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty for embedding.", nameof(text));

        if (text.Length > MaxTextLength)
            text = text[..MaxTextLength];

        using var client = _httpClientFactory.CreateClient("Gemini");
        var url = $"v1beta/models/{ModelId}:embedContent?key={_apiKey}";

        var request = new
        {
            content = new
            {
                parts = new[]
                {
                    new { text }
                }
            },
            outputDimensionality = OutputDimensionality
        };

        var response = await client.PostAsJsonAsync(url, request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Gemini Embedding API returned {(int)response.StatusCode}: {errorBody}");
        }

        var jsonResponse = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<EmbeddingResponse>(jsonResponse, JsonOptions);

        return result?.Embedding?.Values
               ?? throw new InvalidOperationException("Failed to get embedding from Gemini API");
    }

    public async Task<List<float[]>> GetBatchEmbeddingsAsync(List<string> texts)
    {
        const int batchSize = 10;
        var results = new List<float[]>(texts.Count);

        foreach (var batch in texts.Chunk(batchSize))
        {
            var tasks = batch.Select(GetEmbeddingAsync);
            var batchResults = await Task.WhenAll(tasks);
            results.AddRange(batchResults);

            await Task.Delay(100);
        }

        return results;
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
