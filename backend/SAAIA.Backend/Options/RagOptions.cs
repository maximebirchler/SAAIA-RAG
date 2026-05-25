sealed class RagOptions
{
    public const int DefaultSearchDenseEmbeddingTimeoutSeconds = 15;
    public const int MaxSearchDenseEmbeddingTimeoutSeconds = 120;
    public const int DefaultSearchSparseCommandTimeoutSeconds = 12;
    public const int MaxSearchSparseCommandTimeoutSeconds = 60;

    public string QdrantBaseUrl { get; set; } = "http://localhost:6333/";
    public string QdrantCollection { get; set; } = "knowledge_base";

    /// <summary>
    /// Optional: Qdrant API key (recommended in prod).
    /// If set, backend will send it on every Qdrant REST call.
    /// </summary>
    public string? QdrantApiKey { get; set; }

    /// <summary>
    /// Recommended for prod: reference to the secret instead of embedding it in the signed config.
    /// Supported forms:
    /// - "ENV:QDRANT_API_KEY"  -> reads Environment variable QDRANT_API_KEY
    /// - "FILE:/run/secrets/qdrant_api_key" -> reads file content (trimmed)
    /// Relative FILE paths are resolved from ContentRootPath.
    /// </summary>
    public string? QdrantApiKeyRef { get; set; }

    /// <summary>
    /// Security policy: in Production, require Qdrant auth configured in the backend.
    /// This is a POLICY (should remain in signed config).
    /// </summary>
    public bool RequireQdrantAuthInProd { get; set; } = true;

    /// <summary>
    /// How to send QdrantApiKey:
    /// - "api-key" (default): header "api-key: &lt;key&gt;"
    /// - "bearer": header "Authorization: Bearer &lt;key&gt;"
    /// </summary>
    public string QdrantAuthMode { get; set; } = "api-key";

    public string EmbeddingsBaseUrl { get; set; } = "http://localhost:8081/";
    public string EmbeddingsModel { get; set; } = "intfloat/multilingual-e5-base";
    public bool EnableRerank { get; set; } = false;
    public string? RerankBaseUrl { get; set; }
    public string? RerankModel { get; set; }
    public int RerankMaxCandidates { get; set; } = 12;

    public int DefaultTopK { get; set; } = 5;
    public int MaxTopK { get; set; } = 20;

    /// <summary>
    /// Maximum number of concurrent interactive RAG searches admitted by this backend instance.
    /// Excess requests can wait in a short bounded queue instead of saturating TEI/Qdrant/Postgres.
    /// </summary>
    public int SearchMaxConcurrency { get; set; } = 4;

    /// <summary>
    /// Maximum number of interactive RAG searches allowed to wait for a slot.
    /// Requests above this bound receive HTTP 429 with Retry-After.
    /// </summary>
    public int SearchQueueLimit { get; set; } = 16;

    /// <summary>
    /// Maximum time an interactive RAG search can wait for an execution slot.
    /// </summary>
    public int SearchQueueWaitTimeoutSeconds { get; set; } = 25;

    /// <summary>
    /// Retry-After value returned when the bounded RAG search queue is full or times out.
    /// </summary>
    public int SearchRetryAfterSeconds { get; set; } = 3;

    /// <summary>
    /// Per-dense-retriever embedding budget for interactive RAG. If TEI is saturated
    /// by ingestion, dense retrieval degrades and exact/sparse/title routes can still answer.
    /// Set to 0 to wait for the outer request cancellation token.
    /// </summary>
    public int SearchDenseEmbeddingTimeoutSeconds { get; set; } = DefaultSearchDenseEmbeddingTimeoutSeconds;

    /// <summary>
    /// Per sparse/profile-card SQL command budget for interactive RAG. Slow lexical
    /// expansions degrade instead of consuming the whole request while exact/dense/title routes continue.
    /// </summary>
    public int SearchSparseCommandTimeoutSeconds { get; set; } = DefaultSearchSparseCommandTimeoutSeconds;

    // ==========================
    // Backward compatible aliases
    // ==========================
    // Anciennes clés JSON : TeiBaseUrl / CollectionName
    public string TeiBaseUrl { get => EmbeddingsBaseUrl; set => EmbeddingsBaseUrl = value; }
    public string CollectionName { get => QdrantCollection; set => QdrantCollection = value; }

    public static int ResolveSearchDenseEmbeddingTimeoutSeconds(int value)
        => value <= 0 ? 0 : Math.Clamp(value, 1, MaxSearchDenseEmbeddingTimeoutSeconds);

    public static int ResolveSearchSparseCommandTimeoutSeconds(int value)
        => Math.Clamp(value, 1, MaxSearchSparseCommandTimeoutSeconds);
}
