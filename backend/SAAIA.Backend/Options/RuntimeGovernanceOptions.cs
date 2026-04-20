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
}
