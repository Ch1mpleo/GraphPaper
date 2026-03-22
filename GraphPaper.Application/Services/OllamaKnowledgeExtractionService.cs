using GraphPaper.Application.Interfaces;
using GraphPaper.Domain.Entities;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphPaper.Application.Services;

/// <summary>
/// Knowledge extraction backed by local Ollama — no quota, no rate limits.
/// Requires Ollama running on host at OLLAMA_BASE_URL (default: http://host.docker.internal:11434).
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

    private const string SYSTEM_PROMPT =
        "Bạn là chuyên gia phân tích học thuật liên ngành và xây dựng đồ thị tri thức chuyên sâu. " +
        "Bạn có khả năng phân tích văn bản thuộc mọi lĩnh vực. " +
        "Nhiệm vụ: trích xuất KHÁI NIỆM HỌC THUẬT CHÍNH XÁC, KHÁI NIỆM/ĐỊNH NGHĨA CHÍNH XÁC ĐẦY ĐỦ và MỐI QUAN HỆ CÓ CHIỀU SÂU CHUYÊN MÔN. " +
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

    public async Task<KnowledgeExtractionResult> ExtractFromChunkAsync(DocumentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        using var client = _httpClientFactory.CreateClient("Ollama");
        var url = $"{_baseUrl}/api/chat";
        var prompt = BuildExtractionPrompt(chunk.Content, _options.KnowledgeMaxChunkLength);
        var request = BuildRequest(prompt);

        for (var attempt = 0; attempt <= _options.KnowledgeMaxRetries; attempt++)
        {
            try
            {
                using var response = await client.PostAsJsonAsync(url, request, JsonOptions);

                if (response.IsSuccessStatusCode)
                    return await ParseResponseAsync(response, chunk.Id);

                var statusCode = (int)response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync();

                if (attempt < _options.KnowledgeMaxRetries)
                {
                    var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                    await Task.Delay(backoff);
                    continue;
                }

                throw new HttpRequestException(
                    $"Ollama API returned {statusCode}: {errorBody}");
            }
            catch (HttpRequestException) when (attempt < _options.KnowledgeMaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        return new KnowledgeExtractionResult();
    }

    private object BuildRequest(string prompt) => new
    {
        model = _modelId,
        messages = new[]
        {
            new { role = "system", content = SYSTEM_PROMPT },
            new { role = "user", content = prompt }
        },
        stream = false,
        format = "json",
        options = new
        {
            temperature = 0.1,
            num_predict = 8192
        }
    };

    private async Task<KnowledgeExtractionResult> ParseResponseAsync(
        HttpResponseMessage response, Guid chunkId)
    {
        var json = await response.Content.ReadAsStringAsync();

        string? text;
        try
        {
            using var doc = JsonDocument.Parse(json);
            text = doc.RootElement
                      .GetProperty("message")
                      .GetProperty("content")
                      .GetString();
        }
        catch
        {
            return new KnowledgeExtractionResult();
        }

        if (string.IsNullOrWhiteSpace(text))
            return new KnowledgeExtractionResult();

        text = StripCodeFences(text);

        if (!TryDeserialize(text, out var schema) || schema is null)
            return new KnowledgeExtractionResult();

        return MapToEntities(schema, chunkId);
    }

    private static string StripCodeFences(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```"))
        {
            var newline = t.IndexOf('\n');
            if (newline > 0)
                t = t[(newline + 1)..];
            if (t.EndsWith("```"))
                t = t[..^3];
        }

        return t.Trim();
    }

    private KnowledgeExtractionResult MapToEntities(ExtractionSchema schema, Guid chunkId)
    {
        var result = new KnowledgeExtractionResult();
        var lookup = new Dictionary<string, ExtractedEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in schema.Entities ?? [])
        {
            var name = NormalizeEntityName(e.Name);
            if (string.IsNullOrWhiteSpace(name) || lookup.ContainsKey(name))
                continue;

            var entity = new ExtractedEntity
            {
                Id = Guid.NewGuid(),
                ChunkId = chunkId,
                Name = name,
                EntityType = ValidateEntityType(NormalizeWs(e.EntityType)),
                Description = NormalizeWs(e.Description)
            };
            lookup[name] = entity;
            result.Entities.Add(entity);
        }

        foreach (var r in schema.Relationships ?? [])
        {
            var src = NormalizeEntityName(r.Source);
            var tgt = NormalizeEntityName(r.Target);
            if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt))
                continue;
            if (!lookup.TryGetValue(src, out var srcEntity))
                continue;
            if (!lookup.TryGetValue(tgt, out var tgtEntity))
                continue;

            var confidence = Math.Clamp(r.ConfidenceScore, 0f, 1f);
            if (confidence < _options.KnowledgeMinConfidence)
                continue;

            result.Relationships.Add(new ExtractedRelationship
            {
                Id = Guid.NewGuid(),
                SourceEntityId = srcEntity.Id,
                TargetEntityId = tgtEntity.Id,
                RelationType = NormalizeWs(r.RelationType) is { Length: > 0 } rel ? rel : DEFAULT_RELATION_TYPE,
                ConfidenceScore = confidence
            });
        }

        return result;
    }

    private static bool TryDeserialize(string content, out ExtractionSchema? schema)
    {
        schema = null;

        try
        {
            schema = JsonSerializer.Deserialize<ExtractionSchema>(content, JsonOptions);
            if (schema is not null)
                return true;
        }
        catch
        {
        }

        try
        {
            var first = content.IndexOf('{');
            var last = content.LastIndexOf('}');
            if (first < 0 || last <= first)
                return false;

            schema = JsonSerializer.Deserialize<ExtractionSchema>(
                content[first..(last + 1)], JsonOptions);

            return schema is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildExtractionPrompt(string content, int maxLength)
    {
        if (content.Length > maxLength)
            content = content[..maxLength];

        return $$"""
            Phân tích đoạn văn bản học thuật sau và trích xuất đồ thị tri thức.

            THỰC THỂ (entityType): Khái niệm | Lý thuyết | Định lý/Quy luật | Mô hình |
            Phương trình/Công thức | Thuật toán | Cấu trúc dữ liệu | Giao thức/Chuẩn |
            Quá trình/Phản ứng | Hiện tượng | Cơ chế | Phương pháp | Công cụ/Công nghệ |
            Tổ chức/Thể chế | Đại lượng/Đơn vị | Môn học/Chương trình

            MỐI QUAN HỆ (relationType snake_case): là_trường_hợp_đặc_biệt_của | cấu_thành |
            bao_gồm | tạo_ra | dẫn_đến | là_điều_kiện_cần_của | ngăn_chặn | tăng_cường |
            chứng_minh | đối_lập_với | tương_quan_với | sử_dụng | hiện_thực_hóa |
            giải_quyết | mô_hình_hóa | đo_lường | được_phát_triển_từ | thay_thế | phụ_thuộc_vào

            Trả về JSON hợp lệ:
            {
              "entities": [{"name": "...", "entityType": "...", "description": "KHÁI NIỆM/ĐỊNH NGHĨA tối thiểu 15 từ"}],
              "relationships": [{"source": "...", "target": "...", "relationType": "...", "confidenceScore": 0.0}]
            }

            Quy tắc: source/target khớp chính xác với name đã khai báo. confidenceScore ≥ 0.5.
            Nếu không có thực thể học thuật: {"entities": [], "relationships": []}

            Văn bản:
            {{content}}
            """;
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

    private sealed class ExtractionSchema
    {
        public List<EntitySchema>? Entities { get; set; }
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
