sealed class IngestionOptions
{
    public string DocumentsRoot { get; set; } = "";
    public bool WatcherEnabled { get; set; } = true;
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
}
