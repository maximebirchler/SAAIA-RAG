using System;
using System.Collections.Generic;
using System.Linq;

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
    public static WarmupProfileItem? FindProfile(string? profileId)
        => string.IsNullOrWhiteSpace(profileId)
            ? null
            : CreateDefaultWarmupProfiles().Items.FirstOrDefault(item =>
                string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

    public static WarmupProfilesArtifact CreateDefaultWarmupProfiles() => new(
        GovernanceArtifactStore.WarmupProfilesFile,
        "v3.1",
        WarmupPassCount: 3,
        Items: new[]
        {
            new WarmupProfileItem(
                ProfileId: "qwen25-3b-q4km-cuda-p520-interactive",
                ModelId: "qwen2.5-3b-instruct-q4-k-m",
                Runtime: "llama.cpp-cuda",
                Mode: "nominal",
                Candidate: CreateReferenceCudaProfile(),
                Thresholds: new WarmupThresholds(
                    WarmupMaxLoadMs: 30000,
                    WarmupMaxTtftMs: 12000,
                    WarmupMinTokPerSec: 5.0,
                    WarmupPassCount: 3,
                    IdleTimeoutSeconds: 120,
                    MinDxgiBudgetMiB: 3000),
                HardGateRefs: new[]
                {
                    "checksum_verified",
                    "not_blacklisted",
                    "dxgi_budget_available",
                    "runtime_supports_flash_attn"
                },
                FallbackProfileRef: "qwen25-3b-q4km-cuda-p520-stable"),
            new WarmupProfileItem(
                ProfileId: "qwen25-3b-q4km-cuda-p520-stable",
                ModelId: "qwen2.5-3b-instruct-q4-k-m",
                Runtime: "llama.cpp-cuda",
                Mode: "fallback",
                Candidate: CreateReferenceCudaFallbackProfile(),
                Thresholds: new WarmupThresholds(
                    WarmupMaxLoadMs: 30000,
                    WarmupMaxTtftMs: 14000,
                    WarmupMinTokPerSec: 5.0,
                    WarmupPassCount: 3,
                    IdleTimeoutSeconds: 120,
                    MinDxgiBudgetMiB: 2800),
                HardGateRefs: new[]
                {
                    "checksum_verified",
                    "not_blacklisted",
                    "dxgi_budget_available"
                },
                FallbackProfileRef: "qwen25-3b-q4km-cpu-safe"),
            new WarmupProfileItem(
                ProfileId: "qwen25-3b-q4km-cpu-safe",
                ModelId: "qwen2.5-3b-instruct-q4-k-m",
                Runtime: "llama.cpp-cpu",
                Mode: "safe",
                Candidate: CreateReferenceCpuSafeProfile(),
                Thresholds: new WarmupThresholds(
                    WarmupMaxLoadMs: 45000,
                    WarmupMaxTtftMs: 22000,
                    WarmupMinTokPerSec: 2.0,
                    WarmupPassCount: 3,
                    IdleTimeoutSeconds: 120,
                    MinAvailableRamMiB: 4096),
                HardGateRefs: new[]
                {
                    "checksum_verified",
                    "not_blacklisted",
                    "system_ram_available"
                },
                FallbackProfileRef: "qwen25-3b-q4km-cpu-safe")
        });

    public static QualifiedProfile CreateReferenceCudaProfile() => new(
        ProfileId: "qwen25-3b-q4km-cuda-p520-interactive",
        Runtime: "llama.cpp-cuda",
        ModelId: "qwen2.5-3b-instruct-q4-k-m",
        CtxSize: 3072,
        BatchSize: 1024,
        UbatchSize: 256,
        Threads: 6,
        ThreadsBatch: 6,
        Ngl: 36,
        FlashAttn: true,
        Mlock: false,
        BatteryPolicyRef: "client-balanced",
        FallbackProfileRef: "qwen25-3b-q4km-cuda-p520-stable");

    public static QualifiedProfile CreateReferenceCudaFallbackProfile() => new(
        ProfileId: "qwen25-3b-q4km-cuda-p520-stable",
        Runtime: "llama.cpp-cuda",
        ModelId: "qwen2.5-3b-instruct-q4-k-m",
        CtxSize: 3072,
        BatchSize: 1024,
        UbatchSize: 256,
        Threads: 6,
        ThreadsBatch: 6,
        Ngl: 36,
        FlashAttn: false,
        Mlock: false,
        BatteryPolicyRef: "client-balanced",
        FallbackProfileRef: "qwen25-3b-q4km-cpu-safe");

    public static QualifiedProfile CreateReferenceCpuSafeProfile() => new(
        ProfileId: "qwen25-3b-q4km-cpu-safe",
        Runtime: "llama.cpp-cpu",
        ModelId: "qwen2.5-3b-instruct-q4-k-m",
        CtxSize: 3072,
        BatchSize: 512,
        UbatchSize: 128,
        Threads: Math.Max(4, Environment.ProcessorCount / 2),
        ThreadsBatch: Math.Max(2, Environment.ProcessorCount / 2),
        Ngl: 0,
        FlashAttn: false,
        Mlock: false,
        BatteryPolicyRef: "client-balanced",
        FallbackProfileRef: "qwen25-3b-q4km-cpu-safe");
}
