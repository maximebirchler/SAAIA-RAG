using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

public static class CanonicalArtifactTypes
{
    public const string SourceDocument = "source_document";
}

public sealed class IngestionManifestEnvelope
{
    [JsonPropertyName("manifest")]
    public IngestionManifest Manifest { get; set; } = new();

    [JsonPropertyName("manifestSha256")]
    public string ManifestSha256 { get; set; } = "";
}

public sealed class IngestionManifest
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CanonicalSchema.ManifestVersion;

    [JsonPropertyName("processingRunId")]
    public Guid ProcessingRunId { get; set; }

    [JsonPropertyName("documentId")]
    public Guid DocumentId { get; set; }

    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }

    [JsonPropertyName("sourceSha256")]
    public string SourceSha256 { get; set; } = "";

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("codeRevision")]
    public string CodeRevision { get; set; } = "";

    [JsonPropertyName("stages")]
    public List<IngestionStageManifest> Stages { get; set; } = [];

    [JsonPropertyName("artifacts")]
    public List<IngestionArtifactManifest> Artifacts { get; set; } = [];

    [JsonPropertyName("hardware")]
    public IngestionHardwareProfile Hardware { get; set; } = new();

    [JsonPropertyName("schemaBindings")]
    public Dictionary<string, string> SchemaBindings { get; set; } = new(StringComparer.Ordinal);
}

public sealed class IngestionStageManifest
{
    [JsonPropertyName("stageId")]
    public string StageId { get; set; } = "";

    [JsonPropertyName("stageType")]
    public string StageType { get; set; } = "";

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "";

    [JsonPropertyName("engineVersion")]
    public string EngineVersion { get; set; } = "";

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("modelSha256")]
    public string? ModelSha256 { get; set; }

    [JsonPropertyName("modelRevision")]
    public string? ModelRevision { get; set; }

    [JsonPropertyName("optionsSha256")]
    public string OptionsSha256 { get; set; } = "";

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("batchSize")]
    public int? BatchSize { get; set; }

    [JsonPropertyName("concurrency")]
    public int Concurrency { get; set; } = 1;

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.Ordinal);
}

public sealed class IngestionArtifactManifest
{
    [JsonPropertyName("artifactId")]
    public string ArtifactId { get; set; } = "";

    [JsonPropertyName("artifactType")]
    public string ArtifactType { get; set; } = "";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("storageKey")]
    public string StorageKey { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

public sealed class IngestionHardwareProfile
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("operatingSystem")]
    public string OperatingSystem { get; set; } = "";

    [JsonPropertyName("cpuModel")]
    public string CpuModel { get; set; } = "";

    [JsonPropertyName("logicalProcessorCount")]
    public int LogicalProcessorCount { get; set; }

    [JsonPropertyName("memoryBytes")]
    public long MemoryBytes { get; set; }

    [JsonPropertyName("devices")]
    public List<IngestionComputeDevice> Devices { get; set; } = [];
}

public sealed class IngestionComputeDevice
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("deviceType")]
    public string DeviceType { get; set; } = "";

    [JsonPropertyName("vendor")]
    public string Vendor { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("dedicatedMemoryBytes")]
    public long? DedicatedMemoryBytes { get; set; }

    [JsonPropertyName("sharedMemoryBytes")]
    public long? SharedMemoryBytes { get; set; }

    [JsonPropertyName("driverVersion")]
    public string? DriverVersion { get; set; }

    [JsonPropertyName("runtime")]
    public string? Runtime { get; set; }
}
