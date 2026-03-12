using GraphPaper.Application.Interfaces;
using GraphPaper.Domain.Entities;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GraphPaper.Application.Services;

public class GroqKnowledgeExtractionService : IKnowledgeExtractionService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private const string BaseUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string ModelId = "llama-3.3-70b-versatile";
    private const int MaxRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public GroqKnowledgeExtractionService(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    public async Task<KnowledgeExtractionResult> ExtractFromChunkAsync(DocumentChunk chunk)
    {
        var prompt = BuildExtractionPrompt(chunk.Content);

        var request = new
        {
            model = ModelId,
            messages = new[]
            {
                new { role = "system", content = "You are a knowledge graph extraction assistant. Always respond with valid JSON only." },
                new { role = "user", content = prompt }
            },
            temperature = 0.1,
            response_format = new { type = "json_object" }
        };

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            httpRequest.Content = JsonContent.Create(request);

            var response = await _httpClient.SendAsync(httpRequest);

            if (response.IsSuccessStatusCode)
            {
                var jsonResponse = await response.Content.ReadAsStringAsync();
                var groqResult = JsonSerializer.Deserialize<GroqResponse>(jsonResponse, JsonOptions);

                var text = groqResult?.Choices?.FirstOrDefault()?.Message?.Content;

                if (string.IsNullOrWhiteSpace(text))
                    return new KnowledgeExtractionResult();

                var extraction = JsonSerializer.Deserialize<ExtractionSchema>(text, JsonOptions);

                if (extraction is null)
                    return new KnowledgeExtractionResult();

                return MapToEntities(extraction, chunk.Id);
            }

            if ((int)response.StatusCode == 429 && attempt < MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(delay);
                continue;
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Groq API returned {(int)response.StatusCode}: {errorBody}");
        }

        return new KnowledgeExtractionResult();
    }

    private static string BuildExtractionPrompt(string chunkContent)
    {
        return $$"""
            Analyze the following text and extract knowledge graph data.

            Extract:
            1. **Entities**: Key concepts, people, methods, technologies, findings mentioned.
            2. **Relationships**: How entities relate to each other.

            Return ONLY valid JSON in this exact format:
            {
              "entities": [
                { "name": "entity name", "entityType": "Concept|Person|Method|Technology|Finding|Organization", "description": "brief description" }
              ],
              "relationships": [
                { "source": "source entity name", "target": "target entity name", "relationType": "relationship type", "confidenceScore": 0.0 to 1.0 }
              ]
            }

            Rules:
            - Entity names must be concise and normalized (e.g. "BERT" not "the BERT model")
            - Relationship source/target must exactly match an entity name
            - relationType examples: "is_based_on", "uses", "improves", "authored_by", "part_of", "related_to"
            - confidenceScore: how confident you are about this relationship (0.0 to 1.0)
            - If no entities found, return {"entities": [], "relationships": []}

            Text:
            {{chunkContent}}
            """;
    }

    private static KnowledgeExtractionResult MapToEntities(ExtractionSchema schema, Guid chunkId)
    {
        var result = new KnowledgeExtractionResult();

        var entityLookup = new Dictionary<string, ExtractedEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in schema.Entities ?? [])
        {
            if (string.IsNullOrWhiteSpace(e.Name)) continue;
            if (entityLookup.ContainsKey(e.Name)) continue;

            var entity = new ExtractedEntity
            {
                Id = Guid.NewGuid(),
                ChunkId = chunkId,
                Name = e.Name.Trim(),
                EntityType = e.EntityType?.Trim() ?? "Concept",
                Description = e.Description?.Trim() ?? string.Empty
            };

            entityLookup[e.Name] = entity;
            result.Entities.Add(entity);
        }

        foreach (var r in schema.Relationships ?? [])
        {
            if (string.IsNullOrWhiteSpace(r.Source) || string.IsNullOrWhiteSpace(r.Target)) continue;
            if (!entityLookup.TryGetValue(r.Source, out var sourceEntity)) continue;
            if (!entityLookup.TryGetValue(r.Target, out var targetEntity)) continue;

            result.Relationships.Add(new ExtractedRelationship
            {
                Id = Guid.NewGuid(),
                SourceEntityId = sourceEntity.Id,
                TargetEntityId = targetEntity.Id,
                RelationType = r.RelationType?.Trim() ?? "related_to",
                ConfidenceScore = Math.Clamp(r.ConfidenceScore, 0f, 1f)
            });
        }

        return result;
    }

    #region Groq Response Models

    private class GroqResponse
    {
        public List<Choice>? Choices { get; set; }
    }

    private class Choice
    {
        public Message? Message { get; set; }
    }

    private class Message
    {
        public string? Content { get; set; }
    }

    #endregion

    #region Extraction Schema

    private class ExtractionSchema
    {
        public List<EntitySchema>? Entities { get; set; }
        public List<RelationshipSchema>? Relationships { get; set; }
    }

    private class EntitySchema
    {
        public string Name { get; set; } = string.Empty;
        public string? EntityType { get; set; }
        public string? Description { get; set; }
    }

    private class RelationshipSchema
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string? RelationType { get; set; }
        public float ConfidenceScore { get; set; }
    }

    #endregion
}
