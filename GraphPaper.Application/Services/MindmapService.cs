using GraphPaper.Application.Interfaces;
using GraphPaper.Domain.Entities;
using GraphPaper.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;

namespace GraphPaper.Application.Services;

/// <summary>
/// Builds a Mermaid flowchart (LR) from a document's extracted knowledge graph
/// and persists it in the DocumentMindmaps table.
/// </summary>
public sealed class MindmapService : IMindmapService
{
    // Mermaid node IDs must be alphanumeric — strip everything else.
    private static readonly System.Text.RegularExpressions.Regex NonAlphanumRegex =
        new(@"[^a-zA-Z0-9]", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Maximum label length to keep diagram readable.
    private const int MAX_LABEL_LENGTH = 40;

    // Cap nodes/edges to avoid Mermaid render failures on huge graphs.
    private const int MAX_NODES = 150;
    private const int MAX_EDGES = 200;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<MindmapService> _logger;

    public MindmapService(IUnitOfWork unitOfWork, ILogger<MindmapService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<DocumentMindmap> GenerateAndSaveAsync(Guid documentId)
    {
        _logger.LogInformation("Generating mindmap for document {DocumentId}", documentId);

        // ── 1. Load entities belonging to this document ──────────────────────
        var entities = await _unitOfWork.ExtractedEntities
            .GetQueryable()
            .Where(e => !e.IsDeleted && e.Chunk.DocumentId == documentId)
            .Select(e => new { e.Id, e.Name, e.EntityType })
            .Take(MAX_NODES)
            .ToListAsync();

        if (entities.Count == 0)
        {
            _logger.LogWarning("No entities found for document {DocumentId}. Returning empty mindmap.", documentId);
            return await PersistAsync(documentId, BuildEmptyMermaid(), 0, 0);
        }

        var entityIds = entities.Select(e => e.Id).ToHashSet();

        // ── 2. Load relationships between those entities ──────────────────────
        var relationships = await _unitOfWork.ExtractedRelationships
            .GetQueryable()
            .Where(r => !r.IsDeleted
                        && entityIds.Contains(r.SourceEntityId)
                        && entityIds.Contains(r.TargetEntityId))
            .Select(r => new { r.SourceEntityId, r.TargetEntityId, r.RelationType, r.ConfidenceScore })
            .Take(MAX_EDGES)
            .ToListAsync();

        // ── 3. Build stable node-ID map  (entityId → safe Mermaid ID) ─────────
        var nodeIds = entities.ToDictionary(
            e => e.Id,
            e => "N" + NonAlphanumRegex.Replace(e.Id.ToString("N"), ""));

        // ── 4. Build Mermaid source ───────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine("graph LR");

        // Node declarations with label and entity type as tooltip class
        var typeGroups = new Dictionary<string, List<string>>();

        foreach (var entity in entities)
        {
            var nodeId = nodeIds[entity.Id];
            var label = Truncate(SanitizeLabel(entity.Name), MAX_LABEL_LENGTH);
            sb.AppendLine($"    {nodeId}[\"{label}\"]");

            // Group by entity type for styling
            var safeType = NonAlphanumRegex.Replace(entity.EntityType, "_");
            if (!typeGroups.ContainsKey(safeType))
                typeGroups[safeType] = [];
            typeGroups[safeType].Add(nodeId);
        }

        sb.AppendLine();

        // Edges
        foreach (var rel in relationships)
        {
            var srcId = nodeIds[rel.SourceEntityId];
            var tgtId = nodeIds[rel.TargetEntityId];
            var edgeLabel = Truncate(SanitizeLabel(rel.RelationType.Replace('_', ' ')), 30);
            sb.AppendLine($"    {srcId} -->|\"{edgeLabel}\"| {tgtId}");
        }

        sb.AppendLine();

        // Class definitions per entity type (colour coding)
        AppendClassDefs(sb, typeGroups.Keys.ToList());

        // Assign nodes to classes
        foreach (var (typeName, nodeList) in typeGroups)
            sb.AppendLine($"    class {string.Join(",", nodeList)} {typeName};");

        var mermaidCode = sb.ToString().TrimEnd();

        _logger.LogInformation(
            "Mindmap built for document {DocumentId}: {Nodes} nodes, {Edges} edges.",
            documentId, entities.Count, relationships.Count);

        return await PersistAsync(documentId, mermaidCode, entities.Count, relationships.Count);
    }

    public async Task<DocumentMindmap?> GetByDocumentIdAsync(Guid documentId)
    {
        return await _unitOfWork.DocumentMindmaps
            .FirstOrDefaultAsync(m => m.DocumentId == documentId && !m.IsDeleted);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<DocumentMindmap> PersistAsync(
        Guid documentId, string mermaidCode, int nodeCount, int edgeCount)
    {
        // Upsert: remove stale entry if it exists
        var existing = await _unitOfWork.DocumentMindmaps
            .FirstOrDefaultAsync(m => m.DocumentId == documentId && !m.IsDeleted);

        if (existing is not null)
        {
            existing.MermaidCode = mermaidCode;
            existing.NodeCount = nodeCount;
            existing.EdgeCount = edgeCount;
            await _unitOfWork.DocumentMindmaps.Update(existing);
            await _unitOfWork.SaveChangesAsync();
            return existing;
        }

        var mindmap = new DocumentMindmap
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            MermaidCode = mermaidCode,
            NodeCount = nodeCount,
            EdgeCount = edgeCount
        };

        await _unitOfWork.DocumentMindmaps.AddAsync(mindmap);
        await _unitOfWork.SaveChangesAsync();
        return mindmap;
    }

    private static string BuildEmptyMermaid() =>
        "graph LR\n    EMPTY[\"No knowledge extracted yet\"]";

    private static string SanitizeLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Unknown";

        // Escape double-quotes inside Mermaid labels
        return text.Replace("\"", "'").Replace("\n", " ").Trim();
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    /// <summary>
    /// Emits a small set of fill/stroke class definitions so different entity types
    /// get distinct colours in the rendered diagram.
    /// </summary>
    private static void AppendClassDefs(StringBuilder sb, List<string> typeNames)
    {
        // Predefined palette — cycles if there are more types than colours.
        string[] palette =
        [
            "fill:#4a90d9,stroke:#2c5f8a,color:#fff",   // blue
            "fill:#e8734a,stroke:#b54d28,color:#fff",   // orange
            "fill:#5cad6e,stroke:#3a7a4b,color:#fff",   // green
            "fill:#9b6bc4,stroke:#6d3fa0,color:#fff",   // purple
            "fill:#d4a843,stroke:#a07c22,color:#fff",   // amber
            "fill:#4abcad,stroke:#2a8a7d,color:#fff",   // teal
            "fill:#c45c7a,stroke:#8f3050,color:#fff",   // rose
            "fill:#7a8ea0,stroke:#4e6070,color:#fff",   // slate
        ];

        for (var i = 0; i < typeNames.Count; i++)
        {
            var style = palette[i % palette.Length];
            sb.AppendLine($"    classDef {typeNames[i]} {style}");
        }
    }
}