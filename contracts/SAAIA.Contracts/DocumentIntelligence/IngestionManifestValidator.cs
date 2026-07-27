namespace SAAIA.Contracts.DocumentIntelligence;

public static class IngestionManifestValidator
{
    public static IReadOnlyList<CanonicalValidationIssue> Validate(IngestionManifestEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var manifest = envelope.Manifest;
        var issues = new List<CanonicalValidationIssue>();

        Require(manifest.SchemaVersion == CanonicalSchema.ManifestVersion, "$.manifest.schemaVersion", "schema_version", "Unsupported ingestion manifest schema.", issues);
        Require(manifest.ProcessingRunId != Guid.Empty, "$.manifest.processingRunId", "required", "Processing run ID is required.", issues);
        Require(manifest.DocumentId != Guid.Empty, "$.manifest.documentId", "required", "Document ID is required.", issues);
        Require(manifest.RevisionId != Guid.Empty, "$.manifest.revisionId", "required", "Revision ID is required.", issues);
        RequireSha256(manifest.SourceSha256, "$.manifest.sourceSha256", issues);
        Require(manifest.CreatedAtUtc != default, "$.manifest.createdAtUtc", "required", "Creation timestamp is required.", issues);
        Require(!string.IsNullOrWhiteSpace(manifest.CodeRevision), "$.manifest.codeRevision", "required", "Code revision is required.", issues);
        Require(!string.IsNullOrWhiteSpace(manifest.Hardware.ProfileId), "$.manifest.hardware.profileId", "required", "Hardware profile ID is required.", issues);
        Require(manifest.Hardware.LogicalProcessorCount > 0, "$.manifest.hardware.logicalProcessorCount", "range", "Logical processor count must be positive.", issues);
        Require(manifest.Hardware.MemoryBytes > 0, "$.manifest.hardware.memoryBytes", "range", "Memory size must be positive.", issues);

        var deviceIds = UniqueIds(
            manifest.Hardware.Devices.Select(item => item.DeviceId),
            "$.manifest.hardware.devices",
            "deviceId",
            issues);

        for (var index = 0; index < manifest.Hardware.Devices.Count; index++)
        {
            var device = manifest.Hardware.Devices[index];
            var path = $"$.manifest.hardware.devices[{index}]";
            Require(!string.IsNullOrWhiteSpace(device.DeviceType), $"{path}.deviceType", "required", "Device type is required.", issues);
            Require(device.DedicatedMemoryBytes is null or >= 0, $"{path}.dedicatedMemoryBytes", "range", "Dedicated memory cannot be negative.", issues);
            Require(device.SharedMemoryBytes is null or >= 0, $"{path}.sharedMemoryBytes", "range", "Shared memory cannot be negative.", issues);
        }

        UniqueIds(
            manifest.Stages.Select(item => item.StageId),
            "$.manifest.stages",
            "stageId",
            issues);

        for (var index = 0; index < manifest.Stages.Count; index++)
        {
            var stage = manifest.Stages[index];
            var path = $"$.manifest.stages[{index}]";
            Require(!string.IsNullOrWhiteSpace(stage.StageType), $"{path}.stageType", "required", "Stage type is required.", issues);
            Require(!string.IsNullOrWhiteSpace(stage.Engine), $"{path}.engine", "required", "Engine is required.", issues);
            Require(!string.IsNullOrWhiteSpace(stage.EngineVersion), $"{path}.engineVersion", "required", "Engine version is required.", issues);
            RequireSha256(stage.OptionsSha256, $"{path}.optionsSha256", issues);
            Require(deviceIds.Contains(stage.DeviceId), $"{path}.deviceId", "broken_reference", $"Compute device '{stage.DeviceId}' is not declared.", issues);
            Require(stage.Concurrency > 0, $"{path}.concurrency", "range", "Concurrency must be positive.", issues);
            Require(stage.BatchSize is null or > 0, $"{path}.batchSize", "range", "Batch size must be positive.", issues);
            Require(stage.DurationMs is null or >= 0, $"{path}.durationMs", "range", "Duration cannot be negative.", issues);

            var hasModel = !string.IsNullOrWhiteSpace(stage.ModelId);
            var hasModelHash = !string.IsNullOrWhiteSpace(stage.ModelSha256);
            var hasModelRevision = !string.IsNullOrWhiteSpace(stage.ModelRevision);
            Require(!hasModelHash || hasModel, $"{path}.modelSha256", "model_identity", "A model hash requires a model ID.", issues);
            Require(!hasModelRevision || hasModel, $"{path}.modelRevision", "model_identity", "A model revision requires a model ID.", issues);
            Require(!hasModel || hasModelHash || hasModelRevision, $"{path}.modelId", "model_identity", "A model ID requires an immutable model hash or revision.", issues);
            if (hasModelHash)
                RequireSha256(stage.ModelSha256!, $"{path}.modelSha256", issues);
        }

        UniqueIds(
            manifest.Artifacts.Select(item => item.ArtifactId),
            "$.manifest.artifacts",
            "artifactId",
            issues);
        Require(
            manifest.Artifacts.Count > 0,
            "$.manifest.artifacts",
            "required",
            "At least one content-addressed ingestion artifact is required.",
            issues);

        for (var index = 0; index < manifest.Artifacts.Count; index++)
        {
            var artifact = manifest.Artifacts[index];
            var path = $"$.manifest.artifacts[{index}]";
            Require(!string.IsNullOrWhiteSpace(artifact.ArtifactType), $"{path}.artifactType", "required", "Artifact type is required.", issues);
            Require(!string.IsNullOrWhiteSpace(artifact.SchemaVersion), $"{path}.schemaVersion", "required", "Artifact schema version is required.", issues);
            RequireSha256(artifact.Sha256, $"{path}.sha256", issues);
            Require(!string.IsNullOrWhiteSpace(artifact.StorageKey), $"{path}.storageKey", "required", "Artifact storage key is required.", issues);
            Require(artifact.SizeBytes >= 0, $"{path}.sizeBytes", "range", "Artifact size cannot be negative.", issues);
        }

        RequireSha256(envelope.ManifestSha256, "$.manifestSha256", issues);
        Require(CanonicalContractJson.HasValidHash(envelope), "$.manifestSha256", "hash_mismatch", "Manifest envelope hash does not match its payload.", issues);
        return issues;
    }

    public static void ValidateOrThrow(IngestionManifestEnvelope envelope)
    {
        var issues = Validate(envelope);
        if (issues.Count > 0)
            throw new CanonicalContractException(issues);
    }

    private static HashSet<string> UniqueIds(
        IEnumerable<string> values,
        string path,
        string field,
        List<CanonicalValidationIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                issues.Add(new($"{path}[{index}].{field}", "required", $"{field} is required."));
            else if (!ids.Add(value))
                issues.Add(new($"{path}[{index}].{field}", "duplicate_id", $"ID '{value}' is duplicated."));
            index++;
        }

        return ids;
    }

    private static void RequireSha256(string value, string path, List<CanonicalValidationIssue> issues)
        => Require(
            value is { Length: 64 } && value.All(Uri.IsHexDigit),
            path,
            "sha256",
            "A 64-character SHA-256 value is required.",
            issues);

    private static void Require(
        bool condition,
        string path,
        string code,
        string message,
        List<CanonicalValidationIssue> issues)
    {
        if (!condition)
            issues.Add(new(path, code, message));
    }
}
