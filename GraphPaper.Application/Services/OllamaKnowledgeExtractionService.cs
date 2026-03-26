using GraphPaper.Application.Interfaces;
using GraphPaper.Domain.Entities;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphPaper.Application.Services;

/// <summary>
/// Knowledge extraction backed by local Ollama — no quota, no rate limits.
/// Requires Ollama running on host at OLLAMA_BASE_URL (default: http://host.docker.internal:11434).
/// Pass 1: ExtractEntitiesAsync    — entity-only prompt per chunk.
/// Pass 2: ExtractRelationshipsAsync — relationship-only prompt per chunk, resolves FKs via globalEntityMap.
/// </summary>
public sealed class OllamaKnowledgeExtractionService : IKnowledgeExtractionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly string _modelId;
    private readonly DocumentProcessingOptions _options;

    private const string DEFAULT_MODEL = "llama3.1:8b";
    private const string DEFAULT_BASE_URL = "http://host.docker.internal:11434";

    private const string DEFAULT_ENTITY_TYPE = "Khái niệm";
    private const string DEFAULT_RELATION_TYPE = "có_liên_hệ_với";

    // Maximum number of entity names to inject into the relationship prompt to avoid exceeding context.
    private const int MAX_ENTITY_NAMES_IN_PROMPT = 80;

    private static readonly HashSet<string> AllowedEntityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Khái niệm", "Lý thuyết", "Định lý/Quy luật", "Mô hình", "Phương trình/Công thức",
        "Cấu trúc dữ liệu", "Thuật toán", "Giao thức/Chuẩn", "Cấu trúc vật chất",
        "Hệ thống/Kiến trúc", "Quá trình/Phản ứng", "Hiện tượng", "Cơ chế",
        "Phương pháp", "Công cụ/Công nghệ", "Tổ chức/Thể chế", "Nhà khoa học/Tác giả",
        "Địa danh", "Đại lượng/Đơn vị", "Chỉ số/Tham số",
        "Môn học/Chương trình", "Công trình nghiên cứu",
    };

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ── System prompts ─────────────────────────────────────────────────────────

    private const string ENTITY_SYSTEM_PROMPT =
        "Bạn là chuyên gia xây dựng đồ thị tri thức học thuật. " +
        "Nhiệm vụ: chỉ trích xuất CÁC THỰC THỂ HỌC THUẬT từ đoạn văn bản. " +
        "KHÔNG trích xuất mối quan hệ. " +
        "Ngôn ngữ đầu ra: tiếng Việt (giữ nguyên thuật ngữ kỹ thuật/viết tắt tiếng Anh). " +
        "Chỉ trả về JSON hợp lệ, không có văn bản nào khác, không có markdown code fence.";

    private const string RELATIONSHIP_SYSTEM_PROMPT =
        "Bạn là chuyên gia xây dựng đồ thị tri thức học thuật. " +
        "Nhiệm vụ: chỉ tìm CÁC MỐI QUAN HỆ giữa các thực thể đã cho. " +
        "KHÔNG tạo thực thể mới. Source và target PHẢI là tên chính xác từ danh sách entity. " +
        "Ngôn ngữ đầu ra: tiếng Việt (giữ nguyên thuật ngữ kỹ thuật/viết tắt tiếng Anh). " +
        "Chỉ trả về JSON hợp lệ, không có văn bản nào khác, không có markdown code fence.";

    public OllamaKnowledgeExtractionService(
        IHttpClientFactory httpClientFactory,
        DocumentProcessingOptions options,
        string? baseUrl = null,
        string? modelId = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _baseUrl = baseUrl ?? DEFAULT_BASE_URL;
        _modelId = modelId ?? DEFAULT_MODEL;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Pass 1 — Entity-only extraction
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<List<ExtractedEntity>> ExtractEntitiesAsync(DocumentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var prompt = BuildEntityPrompt(chunk.Content, _options.KnowledgeMaxChunkLength);
        var responseText = await CallOllamaAsync(ENTITY_SYSTEM_PROMPT, prompt);
        if (responseText is null) return [];

        if (!TryDeserializeEntities(responseText, out var schema) || schema?.Entities is null)
            return [];

        return MapEntities(schema.Entities, chunk.Id);
    }

    private static string BuildEntityPrompt(string content, int maxLength)
    {
        if (content.Length > maxLength)
            content = content[..maxLength];

        return $$"""
            Phân tích đoạn văn bản học thuật sau và trích xuất các THỰC THỂ HỌC THUẬT.

            THỰC THỂ (entityType): Khái niệm | Lý thuyết | Định lý/Quy luật | Mô hình |
            Phương trình/Công thức | Thuật toán | Cấu trúc dữ liệu | Giao thức/Chuẩn |
            Quá trình/Phản ứng | Hiện tượng | Cơ chế | Phương pháp | Công cụ/Công nghệ |
            Tổ chức/Thể chế | Đại lượng/Đơn vị | Môn học/Chương trình

            Trả về JSON hợp lệ:
            {
              "entities": [{"name": "...", "entityType": "...", "description": "KHÁI NIỆM/ĐỊNH NGHĨA tối thiểu 15 từ"}]
            }

            Quy tắc: Nếu không có thực thể học thuật: {"entities": []}

            Văn bản:
            {{content}}
            """;
    }

    private List<ExtractedEntity> MapEntities(List<EntitySchema> schemas, Guid chunkId)
    {
        var result = new List<ExtractedEntity>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in schemas)
        {
            var name = NormalizeEntityName(e.Name);
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                continue;

            result.Add(new ExtractedEntity
            {
                Id = Guid.NewGuid(),
                ChunkId = chunkId,
                Name = name,
                EntityType = ValidateEntityType(NormalizeWs(e.EntityType)),
                Description = NormalizeWs(e.Description)
            });
        }

        return result;
    }

    private static bool TryDeserializeEntities(string content, out EntityOnlySchema? schema)
    {
        schema = null;
        try
        {
            schema = JsonSerializer.Deserialize<EntityOnlySchema>(content, JsonOptions);
            if (schema is not null) return true;
        }
        catch { }

        try
        {
            var first = content.IndexOf('{');
            var last = content.LastIndexOf('}');
            if (first < 0 || last <= first) return false;
            schema = JsonSerializer.Deserialize<EntityOnlySchema>(content[first..(last + 1)], JsonOptions);
            return schema is not null;
        }
        catch { return false; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Pass 2 — Relationship-only extraction
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<List<ExtractedRelationship>> ExtractRelationshipsAsync(
        DocumentChunk chunk,
        IReadOnlyDictionary<string, Guid> globalEntityMap)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(globalEntityMap);

        if (globalEntityMap.Count == 0) return [];

        var prompt = BuildRelationshipPrompt(chunk.Content, globalEntityMap, _options.KnowledgeMaxChunkLength);
        var responseText = await CallOllamaAsync(RELATIONSHIP_SYSTEM_PROMPT, prompt);
        if (responseText is null) return [];

        if (!TryDeserializeRelationships(responseText, out var schema) || schema?.Relationships is null)
            return [];

        return MapRelationships(schema.Relationships, globalEntityMap);
    }

    private static string BuildRelationshipPrompt(
        string content,
        IReadOnlyDictionary<string, Guid> globalEntityMap,
        int maxLength)
    {
        if (content.Length > maxLength)
            content = content[..maxLength];

        // Inject only the first MAX_ENTITY_NAMES_IN_PROMPT entity names to stay within token budget.
        var entityList = new StringBuilder();
        var count = 0;
        foreach (var name in globalEntityMap.Keys)
        {
            if (count >= MAX_ENTITY_NAMES_IN_PROMPT) break;
            entityList.AppendLine($"- {name}");
            count++;
        }

        return $$"""
            Dưới đây là danh sách thực thể đã biết:
            {{entityList}}

            Đọc đoạn văn bản này và chỉ tìm CÁC MỐI QUAN HỆ giữa các thực thể trong danh sách trên.
            KHÔNG tạo thực thể mới. source và target phải khớp chính xác tên trong danh sách.

            MỐI QUAN HỆ (relationType snake_case): là_trường_hợp_đặc_biệt_của | cấu_thành |
            bao_gồm | tạo_ra | dẫn_đến | là_điều_kiện_cần_của | ngăn_chặn | tăng_cường |
            chứng_minh | đối_lập_với | tương_quan_với | sử_dụng | hiện_thực_hóa |
            giải_quyết | mô_hình_hóa | đo_lường | được_phát_triển_từ | thay_thế | phụ_thuộc_vào

            Trả về JSON hợp lệ:
            {
              "relationships": [{"source": "...", "target": "...", "relationType": "...", "confidenceScore": 0.0}]
            }

            Quy tắc: confidenceScore ≥ 0.5. Nếu không có mối quan hệ: {"relationships": []}

            Văn bản:
            {{content}}
            """;
    }

    private List<ExtractedRelationship> MapRelationships(
        List<RelationshipSchema> schemas,
        IReadOnlyDictionary<string, Guid> globalEntityMap)
    {
        var result = new List<ExtractedRelationship>();

        foreach (var r in schemas)
        {
            var src = NormalizeEntityName(r.Source);
            var tgt = NormalizeEntityName(r.Target);
            if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt))
                continue;

            // Resolve against global entity map — valid across all chunks
            if (!globalEntityMap.TryGetValue(src, out var srcId))
                continue;
            if (!globalEntityMap.TryGetValue(tgt, out var tgtId))
                continue;

            var confidence = Math.Clamp(r.ConfidenceScore, 0f, 1f);
            if (confidence < _options.KnowledgeMinConfidence)
                continue;

            result.Add(new ExtractedRelationship
            {
                Id = Guid.NewGuid(),
                SourceEntityId = srcId,
                TargetEntityId = tgtId,
                RelationType = NormalizeWs(r.RelationType) is { Length: > 0 } rel ? rel : DEFAULT_RELATION_TYPE,
                ConfidenceScore = confidence
            });
        }

        return result;
    }

    private static bool TryDeserializeRelationships(string content, out RelationshipOnlySchema? schema)
    {
        schema = null;
        try
        {
            schema = JsonSerializer.Deserialize<RelationshipOnlySchema>(content, JsonOptions);
            if (schema is not null) return true;
        }
        catch { }

        try
        {
            var first = content.IndexOf('{');
            var last = content.LastIndexOf('}');
            if (first < 0 || last <= first) return false;
            schema = JsonSerializer.Deserialize<RelationshipOnlySchema>(content[first..(last + 1)], JsonOptions);
            return schema is not null;
        }
        catch { return false; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Shared Ollama HTTP helper
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<string?> CallOllamaAsync(string systemPrompt, string userPrompt)
    {
        using var client = _httpClientFactory.CreateClient("Ollama");
        var url = $"{_baseUrl}/api/chat";
        var request = BuildRequest(systemPrompt, userPrompt);

        for (var attempt = 0; attempt <= _options.KnowledgeMaxRetries; attempt++)
        {
            try
            {
                using var response = await client.PostAsJsonAsync(url, request, JsonOptions);

                if (response.IsSuccessStatusCode)
                    return await ExtractContentAsync(response);

                var statusCode = (int)response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync();

                if (attempt < _options.KnowledgeMaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
                    continue;
                }

                throw new HttpRequestException($"Ollama API returned {statusCode}: {errorBody}");
            }
            catch (HttpRequestException) when (attempt < _options.KnowledgeMaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        return null;
    }

    private object BuildRequest(string systemPrompt, string userPrompt) => new
    {
        model = _modelId,
        messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userPrompt }
        },
        stream = false,
        format = "json",
        options = new
        {
            temperature = 0.1,
            num_predict = 8192
        }
    };

    private static async Task<string?> ExtractContentAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement
                          .GetProperty("message")
                          .GetProperty("content")
                          .GetString();

            if (string.IsNullOrWhiteSpace(text)) return null;
            return StripCodeFences(text);
        }
        catch
        {
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Utility
    // ══════════════════════════════════════════════════════════════════════════

    private static string StripCodeFences(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```"))
        {
            var newline = t.IndexOf('\n');
            if (newline > 0) t = t[(newline + 1)..];
            if (t.EndsWith("```")) t = t[..^3];
        }
        return t.Trim();
    }

    private static string NormalizeEntityName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = NormalizeWs(value).Trim('#', '*', '-', '`', ' ');
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var hasLower = normalized.Any(char.IsLower);
        var hasVietnamese = normalized.Any(c => c > 127);
        if (!hasLower && !hasVietnamese)
            return normalized;

        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }

    private static string NormalizeWs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return MultiSpaceRegex.Replace(value.Trim(), " ");
    }

    private static string ValidateEntityType(string? rawType)
    {
        if (string.IsNullOrWhiteSpace(rawType))
            return DEFAULT_ENTITY_TYPE;
        return AllowedEntityTypes.Contains(rawType) ? rawType : DEFAULT_ENTITY_TYPE;
    }

    // ── Private schema types ────────────────────────────────────────────────

    private sealed class EntityOnlySchema
    {
        public List<EntitySchema>? Entities { get; set; }
    }

    private sealed class RelationshipOnlySchema
    {
        public List<RelationshipSchema>? Relationships { get; set; }
    }

    private sealed class EntitySchema
    {
        public string Name { get; set; } = string.Empty;
        public string? EntityType { get; set; }
        public string? Description { get; set; }
    }

    private sealed class RelationshipSchema
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string? RelationType { get; set; }
        public float ConfidenceScore { get; set; }
    }
}
