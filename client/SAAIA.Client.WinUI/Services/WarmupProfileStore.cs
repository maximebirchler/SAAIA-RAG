using System;
using System.Collections.Generic;

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
    int IdleTimeoutSeconds);

internal static class WarmupProfileStore
{
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
                    IdleTimeoutSeconds: 120),
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
                    IdleTimeoutSeconds: 120),
                HardGateRefs: new[]
                {
                    "checksum_verified",
                    "not_blacklisted",
                    "dxgi_budget_available"
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
}
