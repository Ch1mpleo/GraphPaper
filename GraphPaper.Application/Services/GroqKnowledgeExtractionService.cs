using GraphPaper.Application;
using GraphPaper.Application.Interfaces;
using GraphPaper.Domain.Entities;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace GraphPaper.Application.Services;

public sealed class GroqKnowledgeExtractionService : IKnowledgeExtractionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly DocumentProcessingOptions _options;

    private const string BASE_URL = "https://api.groq.com/openai/v1/chat/completions";
    private const string MODEL_ID = "llama-3.3-70b-versatile";
    private const string SYSTEM_PROMPT = "You are a knowledge graph extraction assistant. Always respond with valid JSON only.";
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public GroqKnowledgeExtractionService(IHttpClientFactory httpClientFactory, string apiKey, DocumentProcessingOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = apiKey;
        _options = options;
    }

    public async Task<KnowledgeExtractionResult> ExtractFromChunkAsync(DocumentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        using var client = _httpClientFactory.CreateClient("GroqKnowledge");
        var request = BuildRequest(chunk.Content, _options.KnowledgeMaxChunkLength);

        for (var attempt = 0; attempt <= _options.KnowledgeMaxRetries; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BASE_URL);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            httpRequest.Content = JsonContent.Create(request);

            using var response = await client.SendAsync(httpRequest);

            if (response.IsSuccessStatusCode)
                return await ParseSuccessResponseAsync(response, chunk.Id);

            if ((int)response.StatusCode == 429 && attempt < _options.KnowledgeMaxRetries)
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

    private static object BuildRequest(string chunkContent, int maxChunkLength)
    {
        var prompt = BuildExtractionPrompt(chunkContent, maxChunkLength);

        return new
        {
            model = MODEL_ID,
            messages = new[]
            {
                new { role = "system", content = SYSTEM_PROMPT },
                new { role = "user", content = prompt }
            },
            temperature = 0.1,
            response_format = new { type = "json_object" }
        };
    }

    private async Task<KnowledgeExtractionResult> ParseSuccessResponseAsync(HttpResponseMessage response, Guid chunkId)
    {
        var jsonResponse = await response.Content.ReadAsStringAsync();
        var groqResult = JsonSerializer.Deserialize<GroqResponse>(jsonResponse, JsonOptions);
        var text = groqResult?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text))
            return new KnowledgeExtractionResult();

        if (!TryDeserializeExtraction(text, out var extraction) || extraction is null)
            return new KnowledgeExtractionResult();

        return MapToEntities(extraction, chunkId);
    }

    private static string BuildExtractionPrompt(string chunkContent, int maxChunkLength)
    {
        var normalizedChunkContent = NormalizeChunkContent(chunkContent, maxChunkLength);

        return $$"""
            Analyze the following text and extract knowledge graph data.

            Extract:
            1. **Entities**: Key concepts, people, methods, technologies, findings mentioned.
            2. **Relationships**: How entities relate to each other.

            Return ONLY valid JSON in this exact format:
            {
              "entities": [
                { "name": "entity name", "entityType": "Concept|Person|Method|Technology|Finding|Organization|...", "description": "fully definition" }
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
            - Ignore markdown-only lines and data URI image blocks
            - If no entities found, return {"entities": [], "relationships": []}

            Text:
            {{normalizedChunkContent}}
            """;
    }

    private KnowledgeExtractionResult MapToEntities(ExtractionSchema schema, Guid chunkId)
    {
        var result = new KnowledgeExtractionResult();
        var entityLookup = new Dictionary<string, ExtractedEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in schema.Entities ?? [])
        {
            var entityName = NormalizeEntityName(e.Name);
            if (string.IsNullOrWhiteSpace(entityName)) continue;
            if (entityLookup.ContainsKey(entityName)) continue;

            var entity = new ExtractedEntity
            {
                Id = Guid.NewGuid(),
                ChunkId = chunkId,
                Name = entityName,
                EntityType = NormalizeWhitespace(e.EntityType) is { Length: > 0 } type ? type : "Concept",
                Description = NormalizeWhitespace(e.Description)
            };

            entityLookup[entityName] = entity;
            result.Entities.Add(entity);
        }

        foreach (var r in schema.Relationships ?? [])
        {
            var source = NormalizeEntityName(r.Source);
            var target = NormalizeEntityName(r.Target);
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) continue;
            if (!entityLookup.TryGetValue(source, out var sourceEntity)) continue;
            if (!entityLookup.TryGetValue(target, out var targetEntity)) continue;

            var confidence = Math.Clamp(r.ConfidenceScore, 0f, 1f);
            if (confidence < _options.KnowledgeMinConfidence) continue;

            result.Relationships.Add(new ExtractedRelationship
            {
                Id = Guid.NewGuid(),
                SourceEntityId = sourceEntity.Id,
                TargetEntityId = targetEntity.Id,
                RelationType = NormalizeWhitespace(r.RelationType) is { Length: > 0 } relationType ? relationType : "related_to",
                ConfidenceScore = confidence
            });
        }

        return result;
    }

    private static bool TryDeserializeExtraction(string content, out ExtractionSchema? extraction)
    {
        extraction = JsonSerializer.Deserialize<ExtractionSchema>(content, JsonOptions);
        if (extraction is not null)
            return true;

        var firstBrace = content.IndexOf('{');
        var lastBrace = content.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
            return false;

        var candidate = content[firstBrace..(lastBrace + 1)];
        extraction = JsonSerializer.Deserialize<ExtractionSchema>(candidate, JsonOptions);
        return extraction is not null;
    }

    private static string NormalizeChunkContent(string chunkContent, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(chunkContent))
            return string.Empty;

        var normalized = chunkContent.Trim();
        if (normalized.Length > maxLength)
            normalized = normalized[..maxLength];

        return normalized;
    }

    private static string NormalizeEntityName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return NormalizeWhitespace(value).Trim('#', '*', '-', '`', ' ');
    }

    private static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return MultiSpaceRegex.Replace(value.Trim(), " ");
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
