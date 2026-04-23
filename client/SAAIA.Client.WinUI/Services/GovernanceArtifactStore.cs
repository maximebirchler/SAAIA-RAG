using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record QualifiedProfile(
    string ProfileId,
    string Runtime,
    string ModelId,
    int CtxSize,
    int BatchSize,
    int UbatchSize,
    int Threads,
    int ThreadsBatch,
    int Ngl,
    bool FlashAttn,
    bool Mlock,
    string BatteryPolicyRef,
    string FallbackProfileRef);

internal enum GovernanceArtifactReadStatus
{
    Ok,
    Missing,
    MissingChecksum,
    ChecksumMismatch,
    InvalidJson,
    Error
}

internal sealed record GovernanceArtifactReadResult<T>(
    T? Value,
    GovernanceArtifactReadStatus Status,
    string? Error = null);

internal static class GovernanceArtifactStore
{
    public const string ModelCatalogFile = "model_catalog.json";
    public const string ModelCollectionsFile = "model_collections.json";
    public const string ModelPolicyFile = "model_policy.json";
    public const string ModelSourcesFile = "model_sources.json";
    public const string WarmupProfilesFile = "warmup_profiles.json";
    public const string WarmupResultsFile = "warmup_results.json";
    public const string HardwareProbeFile = "hardware_probe.json";
    public const string LastKnownGoodProfileFile = "last_known_good_profile.json";
    public const string BlacklistFile = "blacklist.json";
    public const string RollbackLogFile = "rollback_log.json";
    public const string BlacklistAppliedFile = "blacklist_applied.json";
    public const string CapabilityStateFile = "capability_state.json";
    public const string AcquisitionLogFile = "acquisition_log.json";

    // Local disk artifacts use snake_case per CDC v3.1 section 5.8.
    // Backend API path segments may remain kebab-case; do not mix both conventions
    // in the same context.
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAAIA", "governance");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task WriteAsync<T>(
        string fileName,
        T value,
        string? root = null,
        CancellationToken ct = default)
    {
        var path = ResolvePath(fileName, root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = JsonSerializer.Serialize(value, JsonOptions);
        var checksum = ComputeSha256Hex(json);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var tempChecksumPath = tempPath + ".sha256";

        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(tempChecksumPath, checksum, Encoding.ASCII, ct).ConfigureAwait(false);

        MoveReplace(tempPath, path);
        MoveReplace(tempChecksumPath, ChecksumPath(path));
    }

    public static async Task<GovernanceArtifactReadResult<T>> ReadAsync<T>(
        string fileName,
        string? root = null,
        CancellationToken ct = default)
    {
        var path = ResolvePath(fileName, root);
        var checksumPath = ChecksumPath(path);

        if (!File.Exists(path))
            return new GovernanceArtifactReadResult<T>(default, GovernanceArtifactReadStatus.Missing);

        if (!File.Exists(checksumPath))
            return new GovernanceArtifactReadResult<T>(default, GovernanceArtifactReadStatus.MissingChecksum);

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var expected = (await File.ReadAllTextAsync(checksumPath, ct).ConfigureAwait(false)).Trim();
            var actual = ComputeSha256Hex(json);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                return new GovernanceArtifactReadResult<T>(
                    default,
                    GovernanceArtifactReadStatus.ChecksumMismatch,
                    $"Expected {expected}, got {actual}.");
            }

            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is null
                ? new GovernanceArtifactReadResult<T>(default, GovernanceArtifactReadStatus.InvalidJson)
                : new GovernanceArtifactReadResult<T>(value, GovernanceArtifactReadStatus.Ok);
        }
        catch (JsonException ex)
        {
            return new GovernanceArtifactReadResult<T>(default, GovernanceArtifactReadStatus.InvalidJson, ex.Message);
        }
        catch (Exception ex)
        {
            return new GovernanceArtifactReadResult<T>(default, GovernanceArtifactReadStatus.Error, ex.Message);
        }
    }

    public static async Task EnsureDefaultArtifactsAsync(
        AppSettings settings,
        string? root = null,
        CancellationToken ct = default)
    {
        var governanceRoot = root ?? DefaultRoot;
        Directory.CreateDirectory(governanceRoot);

        await WriteIfMissingAsync(ModelCatalogFile, ModelCatalogStore.CreateDefaultCatalog(), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(ModelCollectionsFile, ModelCatalogStore.CreateDefaultCollections(), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(ModelPolicyFile, ModelCatalogStore.CreateDefaultPolicy(), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(ModelSourcesFile, ModelCatalogStore.CreateDefaultSources(), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(WarmupProfilesFile, WarmupProfileStore.CreateDefaultWarmupProfiles(), governanceRoot, ct).ConfigureAwait(false);

        await WriteIfMissingAsync(WarmupResultsFile, new WarmupResultsArtifact("warmup_results.json", "v3.1", Array.Empty<WarmupResultItem>()), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(HardwareProbeFile, HardwareProbeArtifact.Empty(), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(LastKnownGoodProfileFile, new LastKnownGoodProfileArtifact("last_known_good_profile.json", "v3.1", null, null), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(BlacklistFile, new BlacklistArtifact("blacklist.json", "v3.1", Array.Empty<BlacklistRule>()), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(RollbackLogFile, new RollbackLogArtifact("rollback_log.json", "v3.1", Array.Empty<RollbackLogItem>()), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(BlacklistAppliedFile, new GovernanceListArtifact<object>("blacklist_applied.json", "v3.1", Array.Empty<object>()), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(CapabilityStateFile, new GovernanceListArtifact<object>("capability_state.json", "v3.1", Array.Empty<object>()), governanceRoot, ct).ConfigureAwait(false);
        await WriteIfMissingAsync(AcquisitionLogFile, new GovernanceListArtifact<object>("acquisition_log.json", "v3.1", Array.Empty<object>()), governanceRoot, ct).ConfigureAwait(false);

        settings.QualifiedProfile ??= WarmupProfileStore.CreateReferenceCudaProfile();
    }

    public static string ResolvePath(string fileName, string? root = null)
    {
        if (Path.IsPathRooted(fileName))
            throw new ArgumentException("Governance artifact names must be relative.", nameof(fileName));

        if (fileName.Contains('/') || fileName.Contains('\\'))
            throw new ArgumentException("Governance artifact names must not contain path separators.", nameof(fileName));

        return Path.Combine(root ?? DefaultRoot, fileName);
    }

    public static string ComputeSha256Hex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async Task WriteIfMissingAsync<T>(
        string fileName,
        T value,
        string root,
        CancellationToken ct)
    {
        var path = ResolvePath(fileName, root);
        if (File.Exists(path) && File.Exists(ChecksumPath(path)))
            return;

        await WriteAsync(fileName, value, root, ct).ConfigureAwait(false);
    }

    private static string ChecksumPath(string path) => path + ".sha256";

    private static void MoveReplace(string source, string destination)
    {
        if (File.Exists(destination))
            File.Delete(destination);
        File.Move(source, destination);
    }
}

internal sealed record GovernanceListArtifact<T>(
    string Artifact,
    string CdcAlignment,
    IReadOnlyList<T> Items);

internal sealed record HardwareProbeArtifact(
    string Artifact,
    string CdcAlignment,
    string Status,
    DateTimeOffset? CapturedAt,
    string? MachineFingerprint,
    IReadOnlyDictionary<string, object?> Hardware)
{
    public static HardwareProbeArtifact Empty() => new(
        "hardware_probe.json",
        "v3.1",
        "not_captured",
        null,
        null,
        new Dictionary<string, object?>());
}

internal sealed record LastKnownGoodProfileArtifact(
    string Artifact,
    string CdcAlignment,
    QualifiedProfile? Profile,
    DateTimeOffset? QualifiedAt);
