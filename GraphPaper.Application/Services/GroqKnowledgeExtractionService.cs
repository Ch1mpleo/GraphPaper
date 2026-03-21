using GraphPaper.Application.Interfaces;
using GraphPaper.Domain.Entities;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphPaper.Application.Services;

public sealed class GroqKnowledgeExtractionService : IKnowledgeExtractionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly DocumentProcessingOptions _options;

    private const string BASE_URL = "https://api.groq.com/openai/v1/chat/completions";
    private const string MODEL_ID = "llama-3.3-70b-versatile";

    // Vietnamese system prompt: instructs LLM to respond in Vietnamese by default.
    private const string SYSTEM_PROMPT =
        "Bạn là trợ lý trích xuất đồ thị tri thức. " +
        "Luôn trả lời bằng tiếng Việt (trừ tên viết tắt tiếng Anh như AI, GPU, M&A). " +
        "Chỉ trả về JSON hợp lệ, không có văn bản nào khác.";

    // Vietnamese fallback values used when LLM omits entityType / relationType.
    private const string DEFAULT_ENTITY_TYPE = "Khái niệm";
    private const string DEFAULT_RELATION_TYPE = "liên_quan_đến";

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
                new { role = "user",   content = prompt }
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
            Phân tích đoạn văn bản tiếng Việt sau và trích xuất dữ liệu đồ thị tri thức.

            Trích xuất:
            1. **Thực thể**: Các khái niệm, con người, phương pháp, công nghệ, tổ chức, địa điểm được đề cập.
            2. **Quan hệ**: Cách các thực thể liên quan đến nhau.

            Trả về CHỈ JSON hợp lệ theo định dạng sau:
            {
              "entities": [
                { "name": "tên thực thể", "entityType": "loại thực thể bằng tiếng Việt", "description": "định nghĩa đầy đủ bằng tiếng Việt" }
              ],
              "relationships": [
                { "source": "tên thực thể nguồn", "target": "tên thực thể đích", "relationType": "loại_quan_hệ", "confidenceScore": 0.0 }
              ]
            }

            Quy tắc bắt buộc:
            - Tên thực thể (name) PHẢI giữ nguyên tiếng Việt có dấu — ví dụ: "Giá trị thặng dư", không phải "Surplus Value"
            - Giữ nguyên viết tắt tiếng Anh phổ biến: AI, GPU, M&A, FPI, VBSP, Agribank, MLN122
            - entityType PHẢI bằng tiếng Việt — ví dụ: "Khái niệm", "Tổ chức", "Con người", "Phương pháp", "Công nghệ", "Địa điểm", "Môn học", "Phát hiện",...
            - description PHẢI bằng tiếng Việt, không dùng tiếng Anh
            - source/target phải khớp chính xác với name của thực thể đã khai báo
            - relationType dùng tiếng Việt snake_case — ví dụ: "dựa_trên", "sử_dụng", "dẫn_đến", "là_một_phần_của", "liên_quan_đến", "tạo_ra", "đối_lập_với", "điều_chỉnh", "bảo_vệ",...
            - confidenceScore: mức độ chắc chắn từ 0.0 đến 1.0
            - Bỏ qua dòng chỉ có markdown và ảnh base64
            - Nếu không có thực thể, trả về {"entities": [], "relationships": []}

            Văn bản:
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
                EntityType = NormalizeWhitespace(e.EntityType) is { Length: > 0 } type ? type : DEFAULT_ENTITY_TYPE,
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
                RelationType = NormalizeWhitespace(r.RelationType) is { Length: > 0 } rel ? rel : DEFAULT_RELATION_TYPE,
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

        var normalized = NormalizeWhitespace(value).Trim('#', '*', '-', '`', ' ');
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return ToVietnameseSentenceCase(normalized);
    }

    /// <summary>
    /// Capitalises the first character of an entity name.
    /// Pure ASCII all-caps strings (abbreviations like GPU, AI, FPI) are returned unchanged.
    /// </summary>
    private static string ToVietnameseSentenceCase(string value)
    {
        if (value.Length == 0)
            return value;

        // All-caps ASCII abbreviation → keep as-is (GPU, AI, M&A, FPI, VBSP...)
        var hasLower = value.Any(char.IsLower);
        var hasVietnamese = value.Any(c => c > 127);
        if (!hasLower && !hasVietnamese)
            return value;

        return char.ToUpperInvariant(value[0]) + value[1..];
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