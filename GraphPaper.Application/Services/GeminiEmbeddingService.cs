using GraphPaper.Application.Interfaces;
using System.Net.Http.Json;
using System.Text.Json;

namespace GraphPaper.Application.Services
{
    public class GeminiEmbeddingService : IEmbeddingService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
        private const string ModelId = "text-embedding-004";
        private const int MaxParallelRequests = 5;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public GeminiEmbeddingService(HttpClient httpClient, string apiKey)
        {
            _httpClient = httpClient;
            _apiKey = apiKey;
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var url = $"{BaseUrl}{ModelId}:embedContent?key={_apiKey}";

            var request = new
            {
                content = new
                {
                    parts = new[]
                    {
                        new { text }
                    }
                }
            };

            var response = await _httpClient.PostAsJsonAsync(url, request);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<EmbeddingResponse>(jsonResponse, JsonOptions);

            return result?.Embedding?.Values
                   ?? throw new InvalidOperationException("Failed to get embedding from Gemini API");
        }

        public async Task<List<float[]>> GetBatchEmbeddingsAsync(List<string> texts)
        {
            var results = new float[texts.Count][];

            // Process in parallel with a concurrency limit to avoid rate limiting
            using var semaphore = new SemaphoreSlim(MaxParallelRequests);
            var tasks = texts.Select(async (text, index) =>
            {
                await semaphore.WaitAsync();
                try
                {
                    results[index] = await GetEmbeddingAsync(text);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            return [.. results];
        }

        private class EmbeddingResponse
        {
            public EmbeddingData? Embedding { get; set; }
        }

        private class EmbeddingData
        {
            public float[]? Values { get; set; }
        }
    }
}
