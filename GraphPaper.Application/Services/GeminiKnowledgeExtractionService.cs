using GraphPaper.Application.Interfaces;
using GraphPaper.Domain.Entities;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphPaper.Application.Services;

/// <summary>
/// Knowledge extraction service backed by Gemini 2.0 Flash.
/// Free tier: 1,500 RPD, 1,000,000 TPM — no daily token cap.
/// </summary>
public sealed class GeminiKnowledgeExtractionService : IKnowledgeExtractionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly DocumentProcessingOptions _options;

    // gemini-2.0-flash: best free-tier model for structured JSON extraction
    private const string MODEL_ID = "gemini-2.0-flash";
    private const string BASE_URL_TEMPLATE =
        "v1beta/models/{0}:generateContent?key={1}";

    // ~1,000 RPD safety margin → 1 request per ~58s worst case.
    // In practice flash handles bursts fine; 5s between chunks helps keep free-tier RPM stable.
    private const int MIN_DELAY_MS = 5_000;

    private const string DEFAULT_ENTITY_TYPE = "Khái niệm";
    private const string DEFAULT_RELATION_TYPE = "có_liên_hệ_với";

    private static readonly Regex MultiSpaceRegex =
        new(@"\s+", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private const string SYSTEM_INSTRUCTION =
        "Bạn là chuyên gia phân tích học thuật liên ngành và xây dựng đồ thị tri thức chuyên sâu. " +
        "Bạn có khả năng phân tích văn bản thuộc mọi lĩnh vực: khoa học tự nhiên, kỹ thuật, khoa học xã hội, " +
        "kinh tế, y học, luật học, triết học, v.v. " +
        "Nhiệm vụ: trích xuất KHÁI NIỆM HỌC THUẬT CHÍNH XÁC và MỐI QUAN HỆ CÓ CHIỀU SÂU CHUYÊN MÔN. " +
        "Ngôn ngữ đầu ra: tiếng Việt (giữ nguyên thuật ngữ kỹ thuật/viết tắt tiếng Anh như AI, GPU, DNA, HTTP, " +
        "NaCl, O(n log n), IEEE, v.v.). " +
        "Chỉ trả về JSON hợp lệ, không có văn bản nào khác, không có markdown code fence.";

    public GeminiKnowledgeExtractionService(
        IHttpClientFactory httpClientFactory,
        string apiKey,
        DocumentProcessingOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = apiKey;
        _options = options;
    }

    public async Task<KnowledgeExtractionResult> ExtractFromChunkAsync(DocumentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        using var client = _httpClientFactory.CreateClient("GeminiKnowledge");
        var url = string.Format(BASE_URL_TEMPLATE, MODEL_ID, _apiKey);
        var prompt = BuildExtractionPrompt(chunk.Content, _options.KnowledgeMaxChunkLength);
        var request = BuildRequest(prompt);

        for (var attempt = 0; attempt <= _options.KnowledgeMaxRetries; attempt++)
        {
            using var response = await client.PostAsJsonAsync(url, request, JsonOptions);

            if (response.IsSuccessStatusCode)
            {
                await Task.Delay(MIN_DELAY_MS);
                return await ParseSuccessResponseAsync(response, chunk.Id);
            }

            var statusCode = (int)response.StatusCode;

            // 429 — quota / rate limit
            if (statusCode == 429 && attempt < _options.KnowledgeMaxRetries)
            {
                // Gemini returns Retry-After header or a retryDelay field in the body.
                var retryDelay = await ParseRetryDelayAsync(response);
                var backoff = retryDelay ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 3) + 15);
                await Task.Delay(backoff);
                continue;
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Gemini Knowledge API returned {statusCode}: {errorBody}");
        }

        return new KnowledgeExtractionResult();
    }

    private static object BuildRequest(string prompt) => new
    {
        system_instruction = new
        {
            parts = new[] { new { text = SYSTEM_INSTRUCTION } }
        },
        contents = new[]
        {
            new
            {
                role = "user",
                parts = new[] { new { text = prompt } }
            }
        },
        generation_config = new
        {
            temperature = 0.1,
            response_mime_type = "application/json"
        }
    };

    private async Task<KnowledgeExtractionResult> ParseSuccessResponseAsync(
        HttpResponseMessage response, Guid chunkId)
    {
        var jsonResponse = await response.Content.ReadAsStringAsync();

        string? text;
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            text = doc.RootElement
                      .GetProperty("candidates")[0]
                      .GetProperty("content")
                      .GetProperty("parts")[0]
                      .GetProperty("text")
                      .GetString();
        }
        catch
        {
            return new KnowledgeExtractionResult();
        }

        if (string.IsNullOrWhiteSpace(text))
            return new KnowledgeExtractionResult();

        text = StripCodeFences(text);

        if (!TryDeserializeExtraction(text, out var extraction) || extraction is null)
            return new KnowledgeExtractionResult();

        return MapToEntities(extraction, chunkId);
    }

    private static string StripCodeFences(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```"))
        {
            var firstNewline = t.IndexOf('\n');
            if (firstNewline > 0)
                t = t[(firstNewline + 1)..];
            if (t.EndsWith("```"))
                t = t[..^3];
        }

        return t.Trim();
    }

    private static async Task<TimeSpan?> ParseRetryDelayAsync(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var headerValues))
        {
            var raw = headerValues.FirstOrDefault();
            if (int.TryParse(raw, out var secs))
                return TimeSpan.FromSeconds(secs + 1);
        }

        try
        {
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("details", out var details))
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.TryGetProperty("retryDelay", out var delayProp))
                    {
                        var delayStr = delayProp.GetString() ?? string.Empty;
                        if (delayStr.EndsWith("ms") &&
                            double.TryParse(delayStr[..^2], out var ms))
                            return TimeSpan.FromMilliseconds(ms + 500);
                        if (delayStr.EndsWith('s') &&
                            double.TryParse(delayStr[..^1], out var s))
                            return TimeSpan.FromSeconds(s + 1);
                    }
                }
            }
        }
        catch
        {
            // ignore parse failures
        }

        return null;
    }

    private static string BuildExtractionPrompt(string chunkContent, int maxLength)
    {
        var content = chunkContent.Trim();
        if (content.Length > maxLength)
            content = content[..maxLength];

        return $$"""
            Phân tích đoạn văn bản học thuật sau và trích xuất đồ thị tri thức với độ chính xác chuyên môn cao.
            Văn bản có thể thuộc bất kỳ lĩnh vực nào: khoa học máy tính, toán học, vật lý, hóa học, sinh học,
            địa chất, kinh tế, triết học, y học, luật học, v.v. Hãy nhận diện đúng lĩnh vực và dùng thuật ngữ
            chuyên ngành phù hợp.

            ══════════════════════════════════════════
            PHẦN 1: TRÍCH XUẤT THỰC THỂ (entities)
            ══════════════════════════════════════════

            Trích xuất các thực thể TRỌNG YẾU được đề cập hoặc định nghĩa trong văn bản.
            Bỏ qua các từ chung chung không mang nội hàm chuyên môn.

            ── PHÂN LOẠI THỰC THỂ (entityType) ──────────────────────────────────────────

            Chọn ĐÚNG một nhãn phù hợp nhất với lĩnh vực của văn bản:

            [KHÁI NIỆM & LÝ THUYẾT]
            • "Khái niệm"              — Định nghĩa, thuật ngữ chuyên ngành cơ bản
            • "Lý thuyết"              — Hệ thống lý luận, mô hình giải thích hiện tượng
            • "Định lý/Quy luật"       — Phát biểu có thể chứng minh hoặc quy luật tất yếu
            • "Mô hình"                — Biểu diễn trừu tượng hoặc toán học của hệ thống
            • "Phương trình/Công thức" — Biểu diễn toán học cụ thể (E=mc², M=PQ/V, G=c+v+m)

            [ĐỐI TƯỢNG & CẤU TRÚC]
            • "Cấu trúc dữ liệu"   — CS: danh sách liên kết, cây B+, bảng băm
            • "Thuật toán"         — CS: QuickSort, Dijkstra, gradient descent
            • "Giao thức/Chuẩn"    — TCP/IP, HTTP/2, IEEE 802.11
            • "Cấu trúc vật chất"  — Hóa/Vật lý: phân tử, tinh thể, hạt nhân
            • "Hệ thống/Kiến trúc" — Tập hợp các thành phần tương tác có tổ chức

            [QUÁ TRÌNH & HIỆN TƯỢNG]
            • "Quá trình/Phản ứng" — Chuỗi biến đổi có hướng
            • "Hiện tượng"         — Sự kiện hoặc trạng thái quan sát được
            • "Cơ chế"             — Cách thức hoạt động bên trong của một quá trình

            [CÔNG CỤ & PHƯƠNG PHÁP]
            • "Phương pháp"        — Quy trình hoặc kỹ thuật thực hiện
            • "Công cụ/Công nghệ"  — Phần mềm, thiết bị, nền tảng cụ thể

            [CHỦ THỂ & TỔ CHỨC]
            • "Tổ chức/Thể chế"      — Doanh nghiệp, cơ quan, tổ chức
            • "Nhà khoa học/Tác giả" — Người đóng góp vào lĩnh vực học thuật
            • "Địa danh"             — Vị trí địa lý có ý nghĩa khoa học hoặc kinh tế

            [ĐO LƯỜNG & ĐƠN VỊ]
            • "Đại lượng/Đơn vị" — Đại lượng đo lường hoặc đơn vị cụ thể
            • "Chỉ số/Tham số"   — Biến số hoặc hệ số đặc trưng của hệ thống

            [TÀI LIỆU & CHƯƠNG TRÌNH]
            • "Môn học/Chương trình"  — Tên môn học, khóa học, chương trình đào tạo
            • "Công trình nghiên cứu" — Bài báo, luận văn, đề tài nghiên cứu cụ thể

            ── YÊU CẦU VỀ DESCRIPTION ───────────────────────────────────────────────────

            Mỗi description PHẢI:
            ✓ Tối thiểu 15 từ, dùng ngôn ngữ học thuật chính xác của lĩnh vực
            ✓ Nêu rõ BẢN CHẤT hoặc CƠ CHẾ HOẠT ĐỘNG, không chỉ là tên gọi lại
            ✓ Giải thích ký hiệu toán học/hóa học nếu có

            ══════════════════════════════════════════
            PHẦN 2: TRÍCH XUẤT MỐI QUAN HỆ (relationships)
            ══════════════════════════════════════════

            Chọn nhãn phản ánh đúng bản chất. Dùng tiếng Việt snake_case.

            Các nhãn quan hệ:
            là_trường_hợp_đặc_biệt_của | cấu_thành | bao_gồm | tương_đương_với
            tạo_ra | dẫn_đến | là_điều_kiện_cần_của | ngăn_chặn | tăng_cường | giảm_thiểu
            chứng_minh | là_tiên_đề_của | đối_lập_với | tương_quan_với
            sử_dụng | hiện_thực_hóa | giải_quyết | mô_hình_hóa | đo_lường | điều_tiết
            được_phát_triển_từ | là_tiền_đề_của | được_đề_xuất_bởi | thay_thế
            chuyển_hóa_thành | là_hình_thức_biểu_hiện_của | phụ_thuộc_vào

            ══════════════════════════════════════════
            ĐỊNH DẠNG ĐẦU RA
            ══════════════════════════════════════════

            Trả về CHỈ JSON hợp lệ (không có markdown, không có ```):
            {
              "entities": [
                {
                  "name": "tên đầy đủ, giữ ký hiệu kỹ thuật nếu có",
                  "entityType": "nhãn từ danh sách trên",
                  "description": "định nghĩa học thuật tối thiểu 15 từ"
                }
              ],
              "relationships": [
                {
                  "source": "tên thực thể nguồn (khớp chính xác với name đã khai báo)",
                  "target": "tên thực thể đích (khớp chính xác với name đã khai báo)",
                  "relationType": "nhãn từ danh sách hoặc snake_case mô tả chính xác",
                  "confidenceScore": 0.0
                }
              ]
            }

            Quy tắc:
            - source/target phải khớp chính xác với name đã khai báo trong entities
            - confidenceScore: 0.0–1.0, chỉ giữ quan hệ ≥ 0.5
            - Nếu không có thực thể học thuật: {"entities": [], "relationships": []}

            Văn bản:
            {{content}}
            """;
    }

    private KnowledgeExtractionResult MapToEntities(ExtractionSchema schema, Guid chunkId)
    {
        var result = new KnowledgeExtractionResult();
        var entityLookup = new Dictionary<string, ExtractedEntity>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in schema.Entities ?? [])
        {
            var name = NormalizeEntityName(e.Name);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (entityLookup.ContainsKey(name)) continue;

            var entity = new ExtractedEntity
            {
                Id = Guid.NewGuid(),
                ChunkId = chunkId,
                Name = name,
                EntityType = NormalizeWs(e.EntityType) is { Length: > 0 } t ? t : DEFAULT_ENTITY_TYPE,
                Description = NormalizeWs(e.Description)
            };

            entityLookup[name] = entity;
            result.Entities.Add(entity);
        }

        foreach (var r in schema.Relationships ?? [])
        {
            var src = NormalizeEntityName(r.Source);
            var tgt = NormalizeEntityName(r.Target);
            if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt)) continue;
            if (!entityLookup.TryGetValue(src, out var srcEntity)) continue;
            if (!entityLookup.TryGetValue(tgt, out var tgtEntity)) continue;

            var confidence = Math.Clamp(r.ConfidenceScore, 0f, 1f);
            if (confidence < _options.KnowledgeMinConfidence) continue;

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

    private static bool TryDeserializeExtraction(string content, out ExtractionSchema? extraction)
    {
        extraction = JsonSerializer.Deserialize<ExtractionSchema>(content, JsonOptions);
        if (extraction is not null)
            return true;

        var first = content.IndexOf('{');
        var last = content.LastIndexOf('}');
        if (first < 0 || last <= first)
            return false;

        extraction = JsonSerializer.Deserialize<ExtractionSchema>(content[first..(last + 1)], JsonOptions);
        return extraction is not null;
    }

    private static string NormalizeEntityName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = NormalizeWs(value).Trim('#', '*', '-', '`', ' ');
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        // Keep pure ASCII all-caps abbreviations (GPU, AI, DNA) unchanged
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
