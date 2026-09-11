namespace SAAIA.Backend;

sealed class AdvancedAnalysisOptions
{
    public int RetentionDays { get; set; } = 30;

    public int MaximumQueuedJobsPerUser { get; set; } = 20;

    public bool WorkerEnabled { get; set; }

    public int PollDelayMilliseconds { get; set; } = 1_000;

    public int LeaseSeconds { get; set; } = 120;

    public int HeartbeatMilliseconds { get; set; } = 5_000;

    public int RetryDelayMilliseconds { get; set; } = 5_000;

    public int MaximumAttempts { get; set; } = 3;

    public int MaximumEvidenceCharactersPerItem { get; set; } = 24_000;

    public int MaximumEvidenceCharactersTotal { get; set; } = 256_000;

    public bool AllowExternalProviderContent { get; set; }

    public bool AllowExternalProviderMetadata { get; set; }

    public int MaximumToolCalls { get; set; } = 32;

    public int MaximumSearchTopK { get; set; } = 60;

    public int MaximumAccumulatedEvidenceItems { get; set; } = 256;

    public long MaximumToolElapsedMilliseconds { get; set; } = 300_000;
}
