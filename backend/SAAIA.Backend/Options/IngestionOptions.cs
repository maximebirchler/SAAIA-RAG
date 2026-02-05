sealed class IngestionOptions
{
    public string DocumentsRoot { get; set; } = "";

    public bool WatcherEnabled { get; set; } = true;

    // IMPORTANT : désormais réellement utilisé par le FileWatcherService
    public int WatcherDebounceMs { get; set; } = 500;

    public int MissingGraceSeconds { get; set; } = 120;

    // Chunking / embeddings
    public int ChunkMaxWords { get; set; } = 200;
    public int ChunkOverlapWords { get; set; } = 35;
    public int ChunkMinWords { get; set; } = 25;
    public int EmbeddingsBatchSize { get; set; } = 32;

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
    public string DefaultCategory { get; set; } = "general";
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

    // Auto-heal si Qdrant est vide alors que la DB contient des documents
    public bool ReindexIfQdrantEmpty { get; set; } = true;


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
        set => EmbeddingsBatchSize = Math.Clamp(value, 1, 512);
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
}
