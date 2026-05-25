sealed class IngestionOptions
{
    public const int DefaultAutoOcrMaxLanguages = 5;
    public const int AbsoluteAutoOcrMaxLanguages = 32;
    public const int MinEmbeddingsBatchSize = 1;
    public const int DefaultEmbeddingsBatchSize = 32;
    public const int MaxEmbeddingsBatchSize = 256;

    public string DocumentsRoot { get; set; } = "";

    public bool WatcherEnabled { get; set; } = true;

    // IMPORTANT : désormais réellement utilisé par le FileWatcherService
    public int WatcherDebounceMs { get; set; } = 500;

    public int MissingGraceSeconds { get; set; } = 120;

    // Chunking / embeddings
    public int ChunkMaxWords { get; set; } = 200;
    public int ChunkOverlapWords { get; set; } = 35;
    public int ChunkMinWords { get; set; } = 25;
    public int EmbeddingsBatchSize { get; set; } = DefaultEmbeddingsBatchSize;
    public bool EmbeddingsBatchAdaptiveRetryEnabled { get; set; } = true;

    // Worker
    public int WorkerConcurrency { get; set; } = 2;
    public int WorkerEmptyDelayMs { get; set; } = 500;
    public int StaleRunningMinutes { get; set; } = 15;

    // Scanner
    public bool ScannerEnabled { get; set; } = true;
    public int ScanIntervalSeconds { get; set; } = 10;
    public int MinFileAgeSeconds { get; set; } = 2;
    public int MaxFilesPerScan { get; set; } = 5000;

    // Catégorie
    public string DefaultCategory { get; set; } = "";
    public bool CategoryFromFirstFolder { get; set; } = true;

    // Anti-freeze (IMPORTANT)
    // Si TEI ou Qdrant pend, on annule et on marque le job failed/canceled plutôt que “bloquer pour toujours”
    public int TeiTimeoutSeconds { get; set; } = 180;
    public int QdrantTimeoutSeconds { get; set; } = 180;

    // Bulkheads (limite la concurrence réelle TEI/Qdrant)
    public int TeiMaxConcurrency { get; set; } = 2;
    public int QdrantMaxConcurrency { get; set; } = 4;

    // Temps max d'attente pour entrer dans un bulkhead (évite deadlocks)
    public int BulkheadAcquireTimeoutSeconds { get; set; } = 30;
    public int OcrBulkheadAcquireTimeoutSeconds { get; set; } = 1800;
    public int OcrBulkheadQueueWaitTimeoutSeconds { get; set; } = 60;

    // Auto-heal si Qdrant est vide alors que la DB contient des documents
    public bool ReindexIfQdrantEmpty { get; set; } = true;

    // OCR optionnel pour les PDF scannes ou avec trop peu de texte extractible.
    // Par defaut, le backend detecte seulement le besoin OCR. L'execution OCR
    // demande un outil externe explicite, typiquement ocrmypdf.
    public bool OcrEnabled { get; set; } = false;
    public string OcrCommand { get; set; } = "";
    public string OcrArguments { get; set; } = "";
    public string OcrForceArguments { get; set; } = "";
    public string OcrLanguages { get; set; } = "auto";
    public string OcrAutoFallbackLanguages { get; set; } = "fra+eng+deu+ita+spa";
    public int OcrMaxLanguages { get; set; } = 5;
    public bool OcrAutoDetectLanguages { get; set; } = true;
    public int OcrTimeoutSeconds { get; set; } = 900;
    public int OcrMinWords { get; set; } = 5;
    public int OcrMaxConcurrency { get; set; } = 1;
    public bool OcrImagePageEnabled { get; set; } = true;
    public string OcrImageRendererCommand { get; set; } = "gs";
    public string OcrImageTextCommand { get; set; } = "tesseract";
    // 0 means no page budget: OCR every image-bearing page for maximum ingestion fidelity.
    public int OcrImagePageMaxPages { get; set; } = 0;
    public int OcrImagePageRenderDpi { get; set; } = 220;
    public int OcrImagePageSegmentationMode { get; set; } = 3;
    public int OcrImagePageTimeoutSeconds { get; set; } = 120;
    // Global per-document budget for image-page OCR. 0 disables the global budget.
    public int OcrImagePageMaxTotalSeconds { get; set; } = 1800;
    public int OcrImagePageMinWords { get; set; } = 3;

    // Backoff auto-upsert après échecs répétés (évite les boucles infinies scanner -> worker failed -> scanner)
    public int AutoRetryBackoffSeconds { get; set; } = 120;
    public int AutoRetryFailureStreak { get; set; } = 3;


    // ==========================
    // Backward compatible aliases
    // ==========================
    // NOTE : on garde des alias sur les anciennes clés, pour ne pas casser des environnements existants.

    private const int AvgCharsPerWord = 6; // heuristique conservative

    // Anciennes clés JSON : ChunkMaxChars / ChunkOverlapChars
    public int ChunkMaxChars
    {
        get => ChunkMaxWords * AvgCharsPerWord;
        set => ChunkMaxWords = Math.Clamp(value / AvgCharsPerWord, 50, 5000);
    }

    public int ChunkOverlapChars
    {
        get => ChunkOverlapWords * AvgCharsPerWord;
        set => ChunkOverlapWords = Math.Clamp(value / AvgCharsPerWord, 0, 5000);
    }

    // Ancienne clé JSON : TeiBatchSize
    public int TeiBatchSize
    {
        get => EmbeddingsBatchSize;
        set => EmbeddingsBatchSize = ResolveEmbeddingsBatchSize(value);
    }

    // Anciennes clés JSON : ScannerIntervalSeconds / PollSeconds
    public int ScannerIntervalSeconds
    {
        get => ScanIntervalSeconds;
        set => ScanIntervalSeconds = value;
    }

    public int PollSeconds
    {
        get => (int)Math.Round(WorkerEmptyDelayMs / 1000.0);
        set => WorkerEmptyDelayMs = Math.Clamp(value, 0, 3600) * 1000;
    }

    public static int ResolveEmbeddingsBatchSize(int value)
        => Math.Clamp(value, MinEmbeddingsBatchSize, MaxEmbeddingsBatchSize);

    public static int ResolveOcrBulkheadQueueWaitTimeoutSeconds(
        int ocrBulkheadAcquireTimeoutSeconds,
        int ocrBulkheadQueueWaitTimeoutSeconds)
    {
        var legacyCeiling = Math.Clamp(ocrBulkheadAcquireTimeoutSeconds, 1, 86400);
        var queueWait = Math.Clamp(ocrBulkheadQueueWaitTimeoutSeconds, 1, 3600);
        return Math.Min(legacyCeiling, queueWait);
    }
}
