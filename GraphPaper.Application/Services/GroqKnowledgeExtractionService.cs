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

    private const string SYSTEM_PROMPT =
        "Bạn là chuyên gia phân tích học thuật liên ngành và xây dựng đồ thị tri thức chuyên sâu. " +
        "Bạn có khả năng phân tích văn bản thuộc mọi lĩnh vực: khoa học tự nhiên, kỹ thuật, khoa học xã hội, " +
        "kinh tế, y học, luật học, triết học, v.v. " +
        "Nhiệm vụ: trích xuất KHÁI NIỆM HỌC THUẬT CHÍNH XÁC và MỐI QUAN HỆ CÓ CHIỀU SÂU CHUYÊN MÔN. " +
        "Ngôn ngữ đầu ra: tiếng Việt (giữ nguyên thuật ngữ kỹ thuật/viết tắt tiếng Anh như AI, GPU, DNA, HTTP, " +
        "NaCl, O(n log n), IEEE, v.v.). " +
        "Chỉ trả về JSON hợp lệ, không có văn bản nào khác.";

    private const string DEFAULT_ENTITY_TYPE = "Khái niệm";
    private const string DEFAULT_RELATION_TYPE = "có_liên_hệ_với";
    private const int TPM_LOW_WATER_MARK = 2500;
    private const int MIN_DELAY_MS = 5000;
    private const int LOW_TOKEN_DELAY_MS = 9000;

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
            {
                await ApplyAdaptiveDelayAsync(response.Headers);
                return await ParseSuccessResponseAsync(response, chunk.Id);
            }

            if ((int)response.StatusCode == 429 && attempt < _options.KnowledgeMaxRetries)
            {
                var retryAfter = GetRetryAfterDelay(response.Headers);
                var backoff = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 2));
                await Task.Delay(backoff);
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

    private static async Task ApplyAdaptiveDelayAsync(HttpResponseHeaders headers)
    {
        var remainingTokens = ParseIntHeader(headers, "x-ratelimit-remaining-tokens");

        if (remainingTokens.HasValue && remainingTokens.Value < TPM_LOW_WATER_MARK)
        {
            var resetMs = ParseResetDelayMs(headers, "x-ratelimit-reset-tokens");
            var waitMs = resetMs.HasValue
                ? Math.Max(resetMs.Value + 200, LOW_TOKEN_DELAY_MS)
                : LOW_TOKEN_DELAY_MS;
            await Task.Delay(waitMs);
        }
        else
        {
            await Task.Delay(MIN_DELAY_MS);
        }
    }

    private static TimeSpan? GetRetryAfterDelay(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("retry-after", out var values))
            return null;

        var raw = values.FirstOrDefault();
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds + 1);

        return null;
    }

    private static int? ParseIntHeader(HttpResponseHeaders headers, string name)
    {
        if (headers.TryGetValues(name, out var values) &&
            int.TryParse(values.FirstOrDefault(), out var result))
            return result;

        return null;
    }

    private static int? ParseResetDelayMs(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
            return null;

        var raw = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(raw))
            return null;

        if (raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(raw[..^2], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var milliseconds))
            return (int)milliseconds;

        if (raw.EndsWith('s') &&
            double.TryParse(raw[..^1], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return (int)(seconds * 1000);

        return null;
    }

    private static string BuildExtractionPrompt(string chunkContent, int maxChunkLength)
    {
        var normalizedChunkContent = NormalizeChunkContent(chunkContent, maxChunkLength);

        return $$"""
            Phân tích đoạn văn bản học thuật sau và trích xuất đồ thị tri thức với độ chính xác chuyên môn cao.
            Văn bản có thể thuộc bất kỳ lĩnh vực nào: khoa học máy tính, toán học, vật lý, hóa học, sinh học,
            địa chất, kinh tế, triết học, y học, luật học, v.v. Hãy nhận diện đúng lĩnh vực và dùng thuật ngữ
            chuyên ngành phù hợp.

            ══════════════════════════════════════════
            PHẦN 1: TRÍCH XUẤT THỰC THỂ (entities)
            ══════════════════════════════════════════

            Trích xuất các thực thể TRỌng YẾU được đề cập hoặc định nghĩa trong văn bản.
            Bỏ qua các từ chung chung không mang nội hàm chuyên môn.

            ── PHÂN LOẠI THỰC THỂ (entityType) ──────────────────────────────────────────

            Chọn ĐÚNG một nhãn phù hợp nhất với lĩnh vực của văn bản:

            [KHÁI NIỆM & LÝ THUYẾT]
            • "Khái niệm"          — Định nghĩa, thuật ngữ chuyên ngành cơ bản
                                     CS: thuật toán, hàm băm, đệ quy
                                     Hóa: liên kết cộng hóa trị, độ âm điện
                                     Kinh tế: giá trị thặng dư, chi phí cơ hội
            • "Lý thuyết"          — Hệ thống lý luận, mô hình giải thích hiện tượng
                                     VD: Lý thuyết tương đối, Lý thuyết trò chơi, Lý thuyết Big Bang
            • "Định lý/Quy luật"   — Phát biểu có thể chứng minh hoặc quy luật tất yếu
                                     VD: Định lý Pythagorean, Định luật Newton II, Quy luật giá trị
            • "Mô hình"            — Biểu diễn trừu tượng hoặc toán học của hệ thống
                                     VD: Mô hình OSI, Mô hình nguyên tử Bohr, Mô hình hồi quy tuyến tính
            • "Phương trình/Công thức" — Biểu diễn toán học cụ thể
                                     VD: E=mc², phương trình Schrödinger, công thức Shannon

            [ĐỐI TƯỢNG & CẤU TRÚC]
            • "Cấu trúc dữ liệu"   — CS: danh sách liên kết, cây B+, bảng băm, đồ thị
            • "Thuật toán"         — CS: QuickSort, Dijkstra, backpropagation, gradient descent
            • "Giao thức/Chuẩn"    — CS/Kỹ thuật: TCP/IP, HTTP/2, IEEE 802.11, REST
            • "Cấu trúc vật chất"  — Hóa/Vật lý/Địa chất: phân tử, tinh thể, tầng địa chất, hạt nhân
            • "Hệ thống/Kiến trúc" — Tập hợp các thành phần tương tác có tổ chức
                                     VD: hệ thần kinh trung ương, kiến trúc von Neumann, hệ sinh thái

            [QUÁ TRÌNH & HIỆN TƯỢNG]
            • "Quá trình/Phản ứng" — Chuỗi biến đổi có hướng
                                     Hóa: phản ứng oxi hóa khử, quang hợp
                                     CS: biên dịch, garbage collection, đồng bộ hóa
                                     Địa chất: phong hóa, kiến tạo mảng
            • "Hiện tượng"         — Sự kiện hoặc trạng thái quan sát được
                                     VD: siêu dẫn, cộng hưởng từ, lạm phát, hiệu ứng quang điện
            • "Cơ chế"             — Cách thức hoạt động bên trong của một quá trình
                                     VD: cơ chế khóa mutex, cơ chế phản hồi enzyme, cơ chế độc quyền giá

            [CÔNG CỤ & PHƯƠNG PHÁP]
            • "Phương pháp"        — Quy trình hoặc kỹ thuật thực hiện
                                     VD: phương pháp Monte Carlo, phân tích quang phổ, thử nghiệm A/B
            • "Công cụ/Công nghệ"  — Phần mềm, thiết bị, nền tảng cụ thể
                                     VD: TensorFlow, máy quang phổ, CRISPR, GPU

            [CHỦ THỂ & TỔ CHỨC]
            • "Tổ chức/Thể chế"    — Doanh nghiệp, cơ quan, tổ chức, thể chế
            • "Nhà khoa học/Tác giả" — Người đóng góp vào lĩnh vực học thuật
            • "Địa danh"           — Vị trí địa lý có ý nghĩa khoa học hoặc kinh tế

            [ĐO LƯỜNG & ĐƠN VỊ]
            • "Đại lượng/Đơn vị"   — Đại lượng đo lường hoặc đơn vị cụ thể
                                     VD: entropy (J/K), độ phức tạp O(n²), tỷ suất lợi nhuận (%)
            • "Chỉ số/Tham số"     — Biến số hoặc hệ số đặc trưng của hệ thống

            [TÀI LIỆU & CHƯƠNG TRÌNH]
            • "Môn học/Chương trình" — Tên môn học, khóa học, chương trình đào tạo
            • "Công trình nghiên cứu" — Bài báo, luận văn, đề tài nghiên cứu cụ thể

            ── YÊU CẦU VỀ DESCRIPTION ───────────────────────────────────────────────────

            Mỗi description PHẢI:
            ✓ Tối thiểu 15 từ, dùng ngôn ngữ học thuật chính xác của lĩnh vực
            ✓ Nêu rõ BẢN CHẤT hoặc CƠ CHẾ HOẠT ĐỘNG, không chỉ là tên gọi lại
            ✓ Giải thích ký hiệu toán học/hóa học nếu có (VD: O(n log n), NaCl, m=giá trị thặng dư)
            ✓ Phân biệt với các khái niệm dễ nhầm lẫn nếu cần thiết

            Ví dụ tốt:
            - "Cấu trúc dữ liệu lưu trữ ánh xạ khóa-giá trị với thời gian truy xuất trung bình O(1) nhờ hàm băm phân tán các phần tử vào bucket"
            - "Quá trình tế bào chuyển đổi năng lượng ánh sáng mặt trời thành năng lượng hóa học dưới dạng ATP và NADPH thông qua chuỗi phản ứng sáng và tối"
            - "Phần giá trị mới do lao động tạo ra vượt quá giá trị sức lao động (v), bị nhà tư bản chiếm đoạt không qua trao đổi ngang giá"

            ══════════════════════════════════════════
            PHẦN 2: TRÍCH XUẤT MỐI QUAN HỆ (relationships)
            ══════════════════════════════════════════

            ── PHÂN LOẠI MỐI QUAN HỆ (relationType) ────────────────────────────────────

            Chọn nhãn phản ánh đúng bản chất của mối liên hệ giữa hai thực thể.
            Dùng tiếng Việt snake_case.

            [QUAN HỆ CẤU TRÚC & PHÂN CẤP]
            • "là_trường_hợp_đặc_biệt_của"    — A là instance/subtype của B
                                                  VD: QuickSort → sắp xếp so sánh
                                                      Axit sulfuric → axit mạnh
            • "cấu_thành"                      — A là thành phần tạo nên B
                                                  VD: TCP → cấu_thành → TCP/IP stack
                                                      Electron → cấu_thành → nguyên tử
            • "bao_gồm"                        — B là thành phần con của A
            • "là_trường_hợp_điển_hình_của"    — A là ví dụ minh họa cụ thể của B
            • "tương_đương_với"                — A và B mô tả cùng một khái niệm theo cách khác nhau
                                                  VD: lực ≡ đạo hàm động lượng theo thời gian

            [QUAN HỆ NHÂN QUẢ & TÁC ĐỘNG]
            • "tạo_ra"                         — A trực tiếp sinh ra hoặc sản xuất B
            • "dẫn_đến"                        — A gây ra B như hệ quả (có thể gián tiếp)
            • "là_điều_kiện_cần_của"           — A phải tồn tại để B có thể xảy ra
            • "ngăn_chặn"                      — A ức chế hoặc ngăn cản B
            • "tăng_cường"                     — A làm gia tăng hiệu quả hoặc quy mô của B
            • "giảm_thiểu"                     — A làm giảm B
            • "tối_ưu_hóa"                     — A cải thiện B đến giá trị tốt hơn

            [QUAN HỆ LOGIC & TOÁN HỌC]
            • "chứng_minh"                     — A cung cấp bằng chứng hoặc suy diễn ra B
            • "là_tiên_đề_của"                 — A là giả thiết nền tảng để xây dựng B
            • "xấp_xỉ"                         — A là gần đúng của B trong điều kiện nhất định
            • "đối_lập_với"                    — A và B mâu thuẫn hoặc phủ nhau
                                                  VD: axit ↔ bazơ; O(1) ↔ O(n!)
            • "tương_quan_với"                 — A và B có quan hệ thống kê hoặc biến đổi cùng chiều/ngược chiều

            [QUAN HỆ CHỨC NĂNG & SỬ DỤNG]
            • "sử_dụng"                        — A dùng B như công cụ hoặc tài nguyên
            • "hiện_thực_hóa"                  — A là cài đặt/thực thi cụ thể của B (lý thuyết → thực tiễn)
                                                  VD: TensorFlow → hiện_thực_hóa → mạng nơ-ron nhân tạo
            • "giải_quyết"                     — A là phương pháp/thuật toán để xử lý B (vấn đề)
            • "mô_hình_hóa"                    — A là biểu diễn trừu tượng/toán học của B
            • "đo_lường"                       — A là đại lượng/chỉ số định lượng B
            • "điều_tiết"                      — A kiểm soát hoặc điều chỉnh B

            [QUAN HỆ NGUỒN GỐC & LỊCH SỬ]
            • "được_phát_triển_từ"             — A tiến hóa hoặc mở rộng từ B
            • "là_tiền_đề_của"                 — B ra đời nhờ nền tảng A tạo ra trước đó
            • "được_đề_xuất_bởi"               — A được giới thiệu hoặc chứng minh bởi B (nhà khoa học)
            • "thay_thế"                       — A thay thế B trong thực tiễn do ưu thế vượt trội

            [QUAN HỆ BIẾN ĐỔI & CHUYỂN HÓA]
            • "chuyển_hóa_thành"               — A biến đổi thành B qua quá trình cụ thể
                                                  VD: ADP + Pi → chuyển_hóa_thành → ATP
                                                      mã nguồn → chuyển_hóa_thành → bytecode
            • "là_hình_thức_biểu_hiện_của"     — A là dạng biểu hiện bề ngoài của bản chất B
            • "phụ_thuộc_vào"                  — Giá trị/hành vi của A bị quyết định bởi B

            ── LƯU Ý KHI CHỌN QUAN HỆ ──────────────────────────────────────────────────
            • Ưu tiên nhãn đặc thù hơn nhãn chung ("là_nguồn_gốc_của" tốt hơn "liên_quan_đến")
            • Nếu không có nhãn phù hợp, dùng snake_case mô tả chính xác mối quan hệ
            • Hướng quan hệ: source → relationType → target phải đọc thành câu có nghĩa

            ══════════════════════════════════════════
            ĐỊNH DẠNG ĐẦU RA
            ══════════════════════════════════════════

            Trả về CHỈ JSON hợp lệ:
            {
              "entities": [
                {
                  "name": "tên đầy đủ, giữ ký hiệu kỹ thuật nếu có (VD: 'Thuật toán Dijkstra', 'DNA', 'E=mc²')",
                  "entityType": "nhãn từ danh sách trên",
                  "description": "định nghĩa học thuật tối thiểu 15 từ, nêu bản chất và cơ chế"
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

            Quy tắc bắt buộc:
            - Giữ nguyên thuật ngữ kỹ thuật tiếng Anh/ký hiệu quốc tế (DNA, TCP/IP, O(n²), NaCl, GPU, AI, M&A...)
            - source/target phải khớp chính xác với name đã khai báo trong entities
            - confidenceScore: 0.0–1.0, chỉ giữ quan hệ ≥ 0.5
            - Nếu không có thực thể học thuật, trả về {"entities": [], "relationships": []}

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
    /// Pure ASCII all-caps strings (abbreviations like GPU, AI, DNA, TCP) are returned unchanged.
    /// </summary>
    private static string ToVietnameseSentenceCase(string value)
    {
        if (value.Length == 0)
            return value;

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