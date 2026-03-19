namespace GraphPaper.Application;

/// <summary>
/// Tunable options for the document-upload processing pipeline.
/// Bind from appsettings.json section "DocumentProcessing".
/// </summary>
public sealed class DocumentProcessingOptions
{
    public const string Section = "DocumentProcessing";

    // ── Chunking ──────────────────────────────────────────────────────────
    /// <summary>Maximum characters a single chunk may contain. Larger paragraphs
    /// are sub-split at sentence boundaries.</summary>
    public int MaxChunkCharacters { get; init; } = 2000;

    /// <summary>Chunks shorter than this are merged with the next chunk
    /// (up to MaxChunkCharacters). Set to 0 to disable merging.</summary>
    public int MinChunkCharacters { get; init; } = 200;

    /// <summary>Number of characters from the end of the previous chunk
    /// to prepend to the current chunk, preserving context at boundaries.
    /// Set to 0 to disable overlap.</summary>
    public int ChunkOverlapCharacters { get; init; } = 80;

    // ── Gemini Embedding ──────────────────────────────────────────────────
    /// <summary>Number of chunks per embedding batch request.</summary>
    public int EmbeddingBatchSize { get; init; } = 10;

    /// <summary>Maximum concurrent requests within one batch.</summary>
    public int EmbeddingMaxParallel { get; init; } = 5;

    /// <summary>Characters sent to Gemini; longer text is truncated.</summary>
    public int EmbeddingMaxTextLength { get; init; } = 8000;

    /// <summary>Output vector dimensionality (must match pgvector column).</summary>
    public int EmbeddingOutputDimensionality { get; init; } = 768;

    // ── Groq Knowledge Extraction ─────────────────────────────────────────
    /// <summary>Delay between per-chunk Groq calls to avoid rate-limiting.</summary>
    public int ExtractionDelaySecs { get; init; } = 4;

    /// <summary>Maximum characters sent to Groq per chunk.</summary>
    public int KnowledgeMaxChunkLength { get; init; } = 6000;

    /// <summary>Maximum retry attempts on HTTP 429.</summary>
    public int KnowledgeMaxRetries { get; init; } = 3;

    /// <summary>Relationships below this confidence are discarded.</summary>
    public float KnowledgeMinConfidence { get; init; } = 0.35f;
}
