namespace SAAIA.Backend;

sealed class RuntimeGovernanceOptions
{
    public int WarmupPassCount { get; set; } = 3;
    public string DefaultProfileKey { get; set; } = "default-local";
    public bool SelectQualifiedCoreRetrieval { get; set; } = true;
    public bool AutoAuthorizeQualifiedCoreRetrieval { get; set; } = true;
    public bool AutoSelectQualifiedCoreRetrieval { get; set; } = true;
    public int MinCpuCores { get; set; } = 4;
    public long MinAvailableMemoryMb { get; set; } = 4096;
    public bool Require64BitProcess { get; set; } = true;
    public int StrictProfileMinCpuCores { get; set; } = 6;
    public long StrictProfileMinAvailableMemoryMb { get; set; } = 8192;
    public int StrictRerankProfileMinCpuCores { get; set; } = 8;
    public long StrictRerankProfileMinAvailableMemoryMb { get; set; } = 12288;
    public long MaxQualificationAgeHours { get; set; } = 168;
    public long StrictProfileMaxQualificationAgeHours { get; set; } = 72;
    public long StrictRerankProfileMaxQualificationAgeHours { get; set; } = 24;
    public int QdrantRuntimeMinCpuCores { get; set; } = 2;
    public long QdrantRuntimeMinAvailableMemoryMb { get; set; } = 2048;
    public int EmbeddingsRuntimeMinCpuCores { get; set; } = 4;
    public long EmbeddingsRuntimeMinAvailableMemoryMb { get; set; } = 4096;
    public int RerankRuntimeMinCpuCores { get; set; } = 6;
    public long RerankRuntimeMinAvailableMemoryMb { get; set; } = 8192;
    public long MaxWarmupPassDurationMs { get; set; } = 3000;
    public long MaxQdrantCheckMs { get; set; } = 1000;
    public long MaxEmbeddingsCheckMs { get; set; } = 2000;
    public long MaxRerankCheckMs { get; set; } = 1500;
    public long CapabilityBRecentFailureCooldownHours { get; set; } = 24;
    public long CapabilityBRecentCancellationCooldownHours { get; set; } = 6;
    public bool CapabilityBWorkerEnabled { get; set; } = true;
    public int CapabilityBWorkerEmptyDelayMs { get; set; } = 5000;
    public int CapabilityBWorkerErrorDelayMs { get; set; } = 1000;
    public bool CapabilityBRequireIngestionIdleForExecution { get; set; } = true;
    public bool CapabilityBRequireRagIdleForExecution { get; set; }
    public bool CapabilityBAutoEnqueueWhenIngestionIdleEnabled { get; set; } = true;
    public int CapabilityBIngestionIdleDelaySeconds { get; set; } = 900;
    public int CapabilityBRagIdleDelaySeconds { get; set; } = 120;
    public int CapabilityBRunningJobLeaseTimeoutSeconds { get; set; } = 3600;
    public int CapabilityBAutoEnqueueBatchSize { get; set; } = 25;
    public int CapabilityBAutoEnqueueTenantLimit { get; set; } = 8;
    public int CapabilityBDocumentProfileSectionTitleLimit { get; set; } = 12;
    public int CapabilityBDocumentProfileExcerptLimit { get; set; } = 10;
    public int RetrievalKpiObservationWindowMinutes { get; set; } = 15;
    public double RetrievalP95TargetMs { get; set; } = 800;
    public double RerankP95TargetMs { get; set; } = 300;
    public double ZeroResultRateTargetPercent { get; set; } = 5;
    public int CapabilityAKpiObservationWindowMinutes { get; set; } = 15;
    public double CapabilityAOperationP95TargetMs { get; set; } = 2000;
    public double CapabilityASkipRateTargetPercent { get; set; } = 15;
    public double CapabilityAReadyToEnqueueRateTargetPercent { get; set; } = 50;
    public double CapabilityAOffsetBackfillShareTargetPercent { get; set; } = 10;
    public int CapabilityBKpiObservationWindowMinutes { get; set; } = 15;
    public double CapabilityBGenerationP95TargetMs { get; set; } = 2500;
    public double CapabilityBLiveFallbackRateTargetPercent { get; set; } = 2;
    public double CapabilityBFailureRateTargetPercent { get; set; } = 3;
    public double CapabilityBQualityScoreTarget { get; set; } = 0.6;
}
