using GraphPaper.Application.Interfaces;
using GraphPaper.Domain.Entities;
using System.Net.Http.Json;
using System.Text.Json;

namespace GraphPaper.Application.Services;

public class GeminiKnowledgeExtractionService : IKnowledgeExtractionService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const string ModelId = "gemini-2.0-flash";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public GeminiKnowledgeExtractionService(HttpClient httpClient, string apiKey)
    {
        _apiKey = apiKey;
        _httpClient = httpClient;
    }

    public async Task<KnowledgeExtractionResult> ExtractFromChunkAsync(DocumentChunk chunk)
    {
        var url = $"{BaseUrl}{ModelId}:generateContent?key={_apiKey}";

        var prompt = BuildExtractionPrompt(chunk.Content);

        var request = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                temperature = 0.1
            }
        };

        var response = await _httpClient.PostAsJsonAsync(url, request);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();
        var geminiResult = JsonSerializer.Deserialize<GeminiResponse>(jsonResponse, JsonOptions);

        var text = geminiResult?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(text))
            return new KnowledgeExtractionResult();

        var extraction = JsonSerializer.Deserialize<ExtractionSchema>(text, JsonOptions);

        if (extraction is null)
            return new KnowledgeExtractionResult();

        return MapToEntities(extraction, chunk.Id);
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

        // Build entity lookup: name → entity
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

        // Build relationships referencing entity Ids
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

    #region Gemini Response Models

    private class GeminiResponse
    {
        public List<Candidate>? Candidates { get; set; }
    }

    private class Candidate
    {
        public ContentBlock? Content { get; set; }
    }

    private class ContentBlock
    {
        public List<Part>? Parts { get; set; }
    }

    private class Part
    {
        public string? Text { get; set; }
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
