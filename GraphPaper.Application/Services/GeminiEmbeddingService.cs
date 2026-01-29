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

        public GeminiEmbeddingService(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
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
                        new { text = text }
                    }
                }
            };

            var response = await _httpClient.PostAsJsonAsync(url, request);
            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<EmbeddingResponse>(jsonResponse, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return result?.Embedding?.Values ?? throw new InvalidOperationException("Failed to get embedding from Gemini API");
        }

        public async Task<List<float[]>> GetBatchEmbeddingsAsync(List<string> texts)
        {
            var results = new List<float[]>();

            // Process each text individually since Gemini doesn't have a batch endpoint
            // For better performance, you could implement Task.WhenAll for parallel processing
            foreach (var text in texts)
            {
                var embedding = await GetEmbeddingAsync(text);
                results.Add(embedding);
            }

            return results;
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
