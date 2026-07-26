using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record WarmupProfilesArtifact(
    string Artifact,
    string CdcAlignment,
    int WarmupPassCount,
    IReadOnlyList<WarmupProfileItem> Items);

internal sealed record WarmupProfileItem(
    string ProfileId,
    string ModelId,
    string Runtime,
    string Mode,
    QualifiedProfile Candidate,
    WarmupThresholds Thresholds,
    IReadOnlyList<string> HardGateRefs,
    string? FallbackProfileRef);

internal sealed record WarmupThresholds(
    int WarmupMaxLoadMs,
    int WarmupMaxTtftMs,
    double WarmupMinTokPerSec,
    int WarmupPassCount,
    int IdleTimeoutSeconds,
    int? MinDxgiBudgetMiB = null,
    int? MinAvailableRamMiB = null);

internal static class WarmupProfileStore
{
    public static WarmupProfileItem? ResolveReferenceProfile(string? runtime, string? modelId)
    {
        var canonicalModelId = ModelCatalogStore.ResolveCanonicalModelId(modelId) ?? modelId;
        return CreateDefaultWarmupProfiles().Items
            .Where(item =>
                string.Equals(item.Runtime, runtime, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ModelId, canonicalModelId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Mode switch
            {
                "nominal" => 0,
                "fallback" => 1,
                "safe" => 2,
                _ => 3
            })
            .FirstOrDefault();
    }

    public static WarmupProfileItem? FindProfile(string? profileId)
        => string.IsNullOrWhiteSpace(profileId)
            ? null
            : CreateDefaultWarmupProfiles().Items.FirstOrDefault(item =>
                string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

    public static async Task<WarmupProfileItem?> FindProfileAsync(
        string? profileId,
        string? root = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return null;

        var read = await GovernanceArtifactStore.ReadAsync<WarmupProfilesArtifact>(
            GovernanceArtifactStore.WarmupProfilesFile,
            root,
            ct).ConfigureAwait(false);
        if (read.Status == GovernanceArtifactReadStatus.Ok && read.Value is not null)
        {
            return read.Value.Items.FirstOrDefault(item => string.Equals(
                item.ProfileId,
                profileId,
                StringComparison.OrdinalIgnoreCase));
        }

        return FindProfile(profileId);
    }

    public static async Task UpsertMeasuredProfilesAsync(
        IReadOnlyList<WarmupProfileItem> profiles,
        string? root = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0)
            return;

        await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(
            new AppSettings(),
            root,
            ct).ConfigureAwait(false);
        var read = await GovernanceArtifactStore.ReadAsync<WarmupProfilesArtifact>(
            GovernanceArtifactStore.WarmupProfilesFile,
            root,
            ct).ConfigureAwait(false);
        var artifact = read.Status == GovernanceArtifactReadStatus.Ok && read.Value is not null
            ? read.Value
            : CreateDefaultWarmupProfiles();
        var items = artifact.Items.ToList();
        foreach (var profile in profiles)
        {
            if (!string.Equals(profile.ProfileId, profile.Candidate.ProfileId, StringComparison.Ordinal)
                || !string.Equals(profile.ModelId, profile.Candidate.ModelId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(profile.Runtime, profile.Candidate.Runtime, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Measured warmup profile identity mismatch: {profile.ProfileId}.");
            }

            var index = items.FindIndex(item => string.Equals(
                item.ProfileId,
                profile.ProfileId,
                StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                items[index] = profile;
            else
                items.Add(profile);
        }

        await GovernanceArtifactStore.WriteAsync(
            GovernanceArtifactStore.WarmupProfilesFile,
            artifact with
            {
                CdcAlignment = "v3.1",
                WarmupPassCount = Math.Max(3, artifact.WarmupPassCount),
                Items = items
            },
            root,
            ct).ConfigureAwait(false);
    }

    public static WarmupProfilesArtifact CreateDefaultWarmupProfiles() => new(
        GovernanceArtifactStore.WarmupProfilesFile,
        "v3.1",
        WarmupPassCount: 3,
        Items: new[]
        {
            new WarmupProfileItem(
                ProfileId: "qwen3-4b-2507-q5km-cuda-4gb-quality",
                ModelId: "qwen3-4b-instruct-2507-q5-k-m",
                Runtime: "llama.cpp-cuda",
                Mode: "nominal",
                Candidate: CreateQwen3Q5Cuda4GbProfile(),
                Thresholds: new WarmupThresholds(
                    WarmupMaxLoadMs: 35000,
                    WarmupMaxTtftMs: 14000,
                    WarmupMinTokPerSec: 5.0,
                    WarmupPassCount: 3,
                    IdleTimeoutSeconds: 120,
                    MinDxgiBudgetMiB: 3000),
                HardGateRefs: new[]
                {
                    "checksum_verified",
                    "not_blacklisted",
                    "dxgi_budget_available",
                    "runtime_supports_qwen3",
                    "runtime_supports_flash_attn"
                },
                FallbackProfileRef: "qwen3-4b-2507-q5km-cuda-4gb-stable"),
            new WarmupProfileItem(
                ProfileId: "qwen3-4b-2507-q5km-cuda-4gb-stable",
                ModelId: "qwen3-4b-instruct-2507-q5-k-m",
                Runtime: "llama.cpp-cuda",
                Mode: "fallback",
                Candidate: CreateQwen3Q5Cuda4GbFallbackProfile(),
                Thresholds: new WarmupThresholds(
                    WarmupMaxLoadMs: 40000,
                    WarmupMaxTtftMs: 18000,
                    WarmupMinTokPerSec: 4.0,
                    WarmupPassCount: 3,
                    IdleTimeoutSeconds: 120,
                    MinDxgiBudgetMiB: 2800),
                HardGateRefs: new[]
                {
                    "checksum_verified",
                    "not_blacklisted",
                    "dxgi_budget_available",
                    "runtime_supports_qwen3"
                },
                FallbackProfileRef: "qwen3-4b-2507-q5km-cpu-safe"),
            new WarmupProfileItem(
                ProfileId: "qwen3-4b-2507-q5km-cpu-safe",
                ModelId: "qwen3-4b-instruct-2507-q5-k-m",
                Runtime: "llama.cpp-cpu",
                Mode: "safe",
                Candidate: CreateQwen3Q5CpuSafeProfile(),
                Thresholds: new WarmupThresholds(
                    WarmupMaxLoadMs: 60000,
                    WarmupMaxTtftMs: 30000,
                    WarmupMinTokPerSec: 1.5,
                    WarmupPassCount: 3,
                    IdleTimeoutSeconds: 120,
                    MinAvailableRamMiB: 6144),
                HardGateRefs: new[]
                {
                    "checksum_verified",
                    "not_blacklisted",
                    "system_ram_available",
                    "runtime_supports_qwen3"
                },
                FallbackProfileRef: "qwen3-4b-2507-q5km-cpu-safe")
        });

    public static QualifiedProfile CreateQwen3Q5Cuda4GbProfile() => new(
        ProfileId: "qwen3-4b-2507-q5km-cuda-4gb-quality",
        Runtime: "llama.cpp-cuda",
        ModelId: "qwen3-4b-instruct-2507-q5-k-m",
        CtxSize: 4096,
        BatchSize: 512,
        UbatchSize: 128,
        Threads: 4,
        ThreadsBatch: 4,
        Ngl: 37,
        FlashAttn: true,
        Mlock: false,
        BatteryPolicyRef: "client-balanced",
        FallbackProfileRef: "qwen3-4b-2507-q5km-cuda-4gb-stable")
    {
        DeviceIds = new[] { "CUDA0" },
        SplitMode = "none",
        CacheTypeK = "f16",
        CacheTypeV = "f16",
        Parallel = 1
    };

    public static QualifiedProfile CreateQwen3Q5Cuda4GbFallbackProfile() => new(
        ProfileId: "qwen3-4b-2507-q5km-cuda-4gb-stable",
        Runtime: "llama.cpp-cuda",
        ModelId: "qwen3-4b-instruct-2507-q5-k-m",
        CtxSize: 3072,
        BatchSize: 256,
        UbatchSize: 64,
        Threads: 4,
        ThreadsBatch: 4,
        Ngl: 37,
        FlashAttn: false,
        Mlock: false,
        BatteryPolicyRef: "client-balanced",
        FallbackProfileRef: "qwen3-4b-2507-q5km-cpu-safe")
    {
        DeviceIds = new[] { "CUDA0" },
        SplitMode = "none",
        CacheTypeK = "q8_0",
        CacheTypeV = "q8_0",
        Parallel = 1
    };

    public static QualifiedProfile CreateQwen3Q5CpuSafeProfile() => new(
        ProfileId: "qwen3-4b-2507-q5km-cpu-safe",
        Runtime: "llama.cpp-cpu",
        ModelId: "qwen3-4b-instruct-2507-q5-k-m",
        CtxSize: 3072,
        BatchSize: 256,
        UbatchSize: 64,
        Threads: Math.Max(4, Environment.ProcessorCount / 2),
        ThreadsBatch: Math.Max(2, Environment.ProcessorCount / 2),
        Ngl: 0,
        FlashAttn: false,
        Mlock: false,
        BatteryPolicyRef: "client-balanced",
        FallbackProfileRef: "qwen3-4b-2507-q5km-cpu-safe")
    {
        DeviceIds = new[] { "none" },
        SplitMode = "none",
        CacheTypeK = "q8_0",
        CacheTypeV = "q8_0",
        Parallel = 1
    };

}
