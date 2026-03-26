using GraphPaper.Application.Interfaces;
using GraphPaper.Domain.Entities;
using GraphPaper.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphPaper.Application.Services;

/// <summary>
/// Step 3 of the extraction pipeline — cross-chunk relationship enrichment.
///
/// Algorithm per document:
///   For each entity E:
///     1. Embed "E.Name: E.Description"  → query vector
///     2. Cosine similarity against all chunk embeddings in the document
///     3. Take top-K chunks with similarity ≥ MIN_SIMILARITY
///     4. Discover which other entities appear in those chunks
///     5. For each (E, E') pair with no existing relationship → call Ollama
///        using only the retrieved chunks as context
///     6. Persist new ExtractedRelationship records
/// </summary>
public sealed class OllamaRelationshipEnrichmentService : IRelationshipEnrichmentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEmbeddingService _embeddingService;
    private readonly DocumentProcessingOptions _options;
    private readonly ILogger<OllamaRelationshipEnrichmentService> _logger;
    private readonly string _baseUrl;
    private readonly string _modelId;

    private const int    TOP_K_CHUNKS            = 5;
    private const double MIN_SIMILARITY           = 0.55;
    private const int    MAX_ENTITY_PAIRS_PER_DOC = 300;
    private const string DEFAULT_RELATION_TYPE    = "có_liên_hệ_với";

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private const string SYSTEM_PROMPT =
        "Bạn là chuyên gia xây dựng đồ thị tri thức học thuật. " +
        "Nhiệm vụ: chỉ tìm MỐI QUAN HỆ giữa hai thực thể cụ thể được cung cấp. " +
        "KHÔNG tạo thực thể mới. Chỉ trả về JSON hợp lệ, không có markdown code fence.";

    public OllamaRelationshipEnrichmentService(
        IUnitOfWork unitOfWork,
        IHttpClientFactory httpClientFactory,
        IEmbeddingService embeddingService,
        DocumentProcessingOptions options,
        ILogger<OllamaRelationshipEnrichmentService> logger,
        string baseUrl,
        string modelId)
    {
        _unitOfWork        = unitOfWork;
        _httpClientFactory = httpClientFactory;
        _embeddingService  = embeddingService;
        _options           = options;
        _logger            = logger;
        _baseUrl           = baseUrl;
        _modelId           = modelId;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Main entry point
    // ══════════════════════════════════════════════════════════════════════════

    public async Task EnrichRelationshipsAsync(Guid documentId)
    {
        // ── 1. Load chunks for this document, then load entities by chunkId ────
        // This avoids loading ALL entities from the entire DB (prior bug).
        var chunks = await _unitOfWork.DocumentChunks
            .GetAllAsync(c => c.DocumentId == documentId);

        var embeddedChunks = chunks.Where(c => c.Embedding is not null).ToList();
        if (embeddedChunks.Count == 0)
        {
            _logger.LogInformation(
                "Enrichment skipped for {DocumentId}: no embedded chunks.", documentId);
            return;
        }

        var chunkIds = new HashSet<Guid>(chunks.Select(c => c.Id));
        var entities = await _unitOfWork.ExtractedEntities
            .GetAllAsync(e => chunkIds.Contains(e.ChunkId));

        if (entities.Count < 2)
        {
            _logger.LogInformation(
                "Enrichment skipped for {DocumentId}: only {Count} entities.",
                documentId, entities.Count);
            return;
        }

        // ── 2. Load existing relationship keys to skip already-found pairs ─────
        // MakePairKey produces a canonical sorted key (smaller GUID first),
        // so no symmetric lookup is needed.
        var entityIds  = entities.Select(e => e.Id).ToList();
        var storedKeys = await GetExistingRelationshipKeysAsync(entityIds);
        var seenPairs  = new HashSet<string>(storedKeys, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Starting cross-chunk enrichment for {DocumentId}: {EntityCount} entities, {ChunkCount} embedded chunks.",
            documentId, entities.Count, embeddedChunks.Count);

        var pairsQueued      = 0;
        var newRelationships = new List<ExtractedRelationship>();

        // ── 3. For each entity: embed → cosine rank → pairwise LLM calls ───────
        foreach (var entity in entities)
        {
            if (pairsQueued >= MAX_ENTITY_PAIRS_PER_DOC) break;

            var queryText = string.IsNullOrWhiteSpace(entity.Description)
                ? entity.Name
                : $"{entity.Name}: {entity.Description}";

            float[] queryVector;
            try
            {
                queryVector = await _embeddingService.GetEmbeddingAsync(queryText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Embedding failed for entity '{Name}'", entity.Name);
                continue;
            }

            var topChunks = RankChunksBySimilarity(embeddedChunks, queryVector, TOP_K_CHUNKS, MIN_SIMILARITY);
            if (topChunks.Count == 0) continue;

            var relevantChunkIds  = new HashSet<Guid>(topChunks.Select(c => c.Id));
            var colocatedEntities = entities
                .Where(e => e.Id != entity.Id && relevantChunkIds.Contains(e.ChunkId))
                .ToList();

            if (colocatedEntities.Count == 0) continue;

            var contextWindow = string.Join("\n\n---\n\n", topChunks.Select(c => c.Content));

            foreach (var other in colocatedEntities)
            {
                if (pairsQueued >= MAX_ENTITY_PAIRS_PER_DOC) break;

                var pairKey = MakePairKey(entity.Id, other.Id);
                if (!seenPairs.Add(pairKey))   // Add returns false if already present
                    continue;

                pairsQueued++;

                var found = await ExtractPairRelationshipsAsync(
                    entity, other, contextWindow, newRelationships);

                if (!found)
                    _logger.LogDebug(
                        "No relationship found between '{A}' and '{B}'.",
                        entity.Name, other.Name);
            }
        }

        // ── 4. Persist new relationships ──────────────────────────────────────
        if (newRelationships.Count > 0)
        {
            await _unitOfWork.ExtractedRelationships.AddRangeAsync(newRelationships);
            await _unitOfWork.SaveChangesAsync();
            _logger.LogInformation(
                "Enrichment added {Count} new relationships for document {DocumentId}.",
                newRelationships.Count, documentId);
        }
        else
        {
            _logger.LogInformation(
                "Enrichment found no new relationships for document {DocumentId}.", documentId);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Cosine similarity ranking
    // ══════════════════════════════════════════════════════════════════════════

    private static List<DocumentChunk> RankChunksBySimilarity(
        List<DocumentChunk> chunks,
        float[] queryVector,
        int topK,
        double minSimilarity)
    {
        var scored = new List<(DocumentChunk chunk, double score)>();

        foreach (var chunk in chunks)
        {
            if (chunk.Embedding is null) continue;
            var score = CosineSimilarity(queryVector, chunk.Embedding.ToArray());
            if (score >= minSimilarity)
                scored.Add((chunk, score));
        }

        return scored
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.chunk)
            .ToList();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom < 1e-10 ? 0 : dot / denom;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // LLM call for a specific entity pair
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<bool> ExtractPairRelationshipsAsync(
        ExtractedEntity entityA,
        ExtractedEntity entityB,
        string context,
        List<ExtractedRelationship> accumulator)
    {
        var prompt = BuildPairPrompt(entityA.Name, entityB.Name, context);

        try
        {
            using var client = _httpClientFactory.CreateClient("Ollama");
            var url = $"{_baseUrl}/api/chat";
            var request = new
            {
                model    = _modelId,
                messages = new[]
                {
                    new { role = "system", content = SYSTEM_PROMPT },
                    new { role = "user",   content = prompt }
                },
                stream  = false,
                format  = "json",
                options = new { temperature = 0.1, num_predict = 1024 }
            };

            for (var attempt = 0; attempt <= _options.KnowledgeMaxRetries; attempt++)
            {
                try
                {
                    using var response = await client.PostAsJsonAsync(url, request, JsonOptions);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (attempt < _options.KnowledgeMaxRetries)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
                            continue;
                        }
                        // Max retries reached — give up gracefully (do not propagate)
                        return false;
                    }

                    var text = await ExtractContentAsync(response);
                    if (text is null) return false;

                    return ParsePairResponse(text, entityA, entityB, accumulator);
                }
                catch (HttpRequestException) when (attempt < _options.KnowledgeMaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
                catch (HttpRequestException)
                {
                    // Final attempt failed — log and return false instead of throwing
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LLM call failed for pair '{A}' / '{B}'.", entityA.Name, entityB.Name);
        }

        return false;
    }

    private static string BuildPairPrompt(string nameA, string nameB, string context)
    {
        var ctx = context.Length > 3000 ? context[..3000] : context;

        return $$"""
            Tìm mối quan hệ giữa hai thực thể:
            - Thực thể A: {{nameA}}
            - Thực thể B: {{nameB}}

            Chỉ dùng thông tin trong đoạn văn bản dưới đây. Nếu không có mối quan hệ rõ ràng,
            trả về {"relationships": []}.

            MỐI QUAN HỆ (relationType snake_case): là_trường_hợp_đặc_biệt_của | cấu_thành |
            bao_gồm | tạo_ra | dẫn_đến | là_điều_kiện_cần_của | ngăn_chặn | tăng_cường |
            chứng_minh | đối_lập_với | tương_quan_với | sử_dụng | hiện_thực_hóa |
            giải_quyết | mô_hình_hóa | đo_lường | được_phát_triển_từ | thay_thế | phụ_thuộc_vào

            Trả về JSON:
            {
              "relationships": [{"source": "...", "target": "...", "relationType": "...", "confidenceScore": 0.0}]
            }

            Văn bản:
            {{ctx}}
            """;
    }

    private bool ParsePairResponse(
        string text,
        ExtractedEntity entityA,
        ExtractedEntity entityB,
        List<ExtractedRelationship> accumulator)
    {
        RelationshipOnlySchema? schema = null;
        try
        {
            schema = JsonSerializer.Deserialize<RelationshipOnlySchema>(text, JsonOptions);
        }
        catch
        {
            var first = text.IndexOf('{');
            var last  = text.LastIndexOf('}');
            if (first >= 0 && last > first)
                try { schema = JsonSerializer.Deserialize<RelationshipOnlySchema>(text[first..(last + 1)], JsonOptions); }
                catch { /* ignore */ }
        }

        if (schema?.Relationships is null || schema.Relationships.Count == 0)
            return false;

        var added = false;
        foreach (var r in schema.Relationships)
        {
            var confidence = Math.Clamp(r.ConfidenceScore, 0f, 1f);
            if (confidence < _options.KnowledgeMinConfidence) continue;

            var srcName = NormalizeWs(r.Source);
            var tgtName = NormalizeWs(r.Target);

            Guid srcId, tgtId;
            if (string.Equals(srcName, entityA.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tgtName, entityB.Name, StringComparison.OrdinalIgnoreCase))
            {
                srcId = entityA.Id; tgtId = entityB.Id;
            }
            else if (string.Equals(srcName, entityB.Name, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(tgtName, entityA.Name, StringComparison.OrdinalIgnoreCase))
            {
                srcId = entityB.Id; tgtId = entityA.Id;
            }
            else continue; // LLM hallucinated different names — skip

            accumulator.Add(new ExtractedRelationship
            {
                Id              = Guid.NewGuid(),
                SourceEntityId  = srcId,
                TargetEntityId  = tgtId,
                RelationType    = NormalizeWs(r.RelationType) is { Length: > 0 } rel
                                      ? rel : DEFAULT_RELATION_TYPE,
                ConfidenceScore = confidence
            });
            added = true;
        }

        return added;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<List<string>> GetExistingRelationshipKeysAsync(List<Guid> entityIds)
    {
        var rels = await _unitOfWork.ExtractedRelationships
            .GetAllAsync(r => entityIds.Contains(r.SourceEntityId) ||
                              entityIds.Contains(r.TargetEntityId));

        return rels.Select(r => MakePairKey(r.SourceEntityId, r.TargetEntityId)).ToList();
    }

    /// <summary>
    /// Returns a canonical sorted key so (A,B) and (B,A) map to the same string.
    /// </summary>
    private static string MakePairKey(Guid a, Guid b)
        => a.CompareTo(b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    private static string NormalizeWs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return MultiSpaceRegex.Replace(value.Trim(), " ");
    }

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

            var t = text.Trim();
            if (t.StartsWith("```"))
            {
                var nl = t.IndexOf('\n');
                if (nl > 0) t = t[(nl + 1)..];
                if (t.EndsWith("```")) t = t[..^3];
            }
            return t.Trim();
        }
        catch { return null; }
    }

    // ── Private schema ────────────────────────────────────────────────────────

    private sealed class RelationshipOnlySchema
    {
        public List<RelationshipSchema>? Relationships { get; set; }
    }

    private sealed class RelationshipSchema
    {
        public string Source         { get; set; } = string.Empty;
        public string Target         { get; set; } = string.Empty;
        public string? RelationType  { get; set; }
        public float ConfidenceScore { get; set; }
    }
}
