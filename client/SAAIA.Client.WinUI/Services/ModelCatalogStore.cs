using System;
using System.Collections.Generic;
using System.Linq;

namespace SAAIA.Client.WinUI.Services;

internal sealed record ModelCatalogArtifact(
    string Artifact,
    string CdcAlignment,
    string Version,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ModelCatalogItem> Items);

internal sealed record ModelCatalogItem(
    string ModelId,
    string DisplayName,
    string Family,
    string Quantization,
    string FileName,
    string SourceRef,
    string? ChecksumSha256,
    string ChecksumStatus,
    ModelLicenseInfo License,
    ModelGgufMetadata Gguf,
    IReadOnlyList<string> ApprovedRuntimeRefs,
    IReadOnlyList<string> SupportedScopes,
    IReadOnlyList<string> BusinessStates,
    IReadOnlyList<string> ArtifactStates,
    string SupportTier);

internal sealed record ModelLicenseInfo(
    string LicenseFamily,
    string LicenseDisplayName,
    bool CommercialUseAllowed,
    string? CommercialUseConditions,
    long? CommercialUseThresholdMau,
    bool RequiresSeparateCommercialLicenseAboveThreshold);

internal sealed record ModelGgufMetadata(
    string Architecture,
    int? BlockCount,
    int? HeadCount,
    int? HeadCountKv,
    int? EmbeddingLength,
    int? ContextLength,
    int? FeedForwardLength);

internal sealed record ModelCollectionsArtifact(
    string Artifact,
    string CdcAlignment,
    IReadOnlyList<ModelCollectionItem> Items);

internal sealed record ModelCollectionItem(
    string Key,
    string Scope,
    bool VisibleInInstaller,
    IReadOnlyList<string> ModelIds);

internal sealed record ModelPolicyArtifact(
    string Artifact,
    string CdcAlignment,
    bool AllowDiscovery,
    bool RequireChecksum,
    int MaxActiveModelsClient,
    string BlacklistRef,
    IReadOnlyList<string> Rules);

internal sealed record ModelSourcesArtifact(
    string Artifact,
    string CdcAlignment,
    IReadOnlyList<ModelSourceItem> Items);

internal sealed record ModelSourceItem(
    string Key,
    string Kind,
    string Uri,
    bool RequiresChecksum,
    bool AllowedInAirGap);

internal static class ModelCatalogStore
{
    public static string? ResolveCanonicalModelId(string? modelIdOrFileName)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrFileName))
            return null;

        var probe = modelIdOrFileName.Trim();
        var catalog = CreateDefaultCatalog();
        return catalog.Items
            .FirstOrDefault(item =>
                string.Equals(item.ModelId, probe, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.FileName, probe, StringComparison.OrdinalIgnoreCase))
            ?.ModelId;
    }

    public static ModelCatalogArtifact CreateDefaultCatalog() => new(
        GovernanceArtifactStore.ModelCatalogFile,
        "v3.1",
        "2026-04-22.phase0",
        DateTimeOffset.UtcNow,
        new[]
        {
            new ModelCatalogItem(
                ModelId: "qwen2.5-3b-instruct-q4-k-m",
                DisplayName: "Qwen2.5 3B Instruct Q4_K_M",
                Family: "qwen2.5",
                Quantization: "Q4_K_M",
                FileName: "Qwen2.5-3B-Instruct-Q4_K_M.gguf",
                SourceRef: "hf-bartowski-qwen25-3b",
                ChecksumSha256: null,
                ChecksumStatus: "pending_reference_hash",
                License: new ModelLicenseInfo(
                    LicenseFamily: "qwen",
                    LicenseDisplayName: "Qwen Research License",
                    CommercialUseAllowed: true,
                    CommercialUseConditions: "Commercial use allowed; separate license required above 100,000,000 monthly active users.",
                    CommercialUseThresholdMau: 100000000,
                    RequiresSeparateCommercialLicenseAboveThreshold: true),
                Gguf: new ModelGgufMetadata(
                    Architecture: "qwen2",
                    BlockCount: 36,
                    HeadCount: 16,
                    HeadCountKv: 2,
                    EmbeddingLength: 2048,
                    ContextLength: 32768,
                    FeedForwardLength: 11008),
                ApprovedRuntimeRefs: new[] { "llama.cpp-cuda", "llama.cpp-vulkan", "llama.cpp-cpu" },
                SupportedScopes: new[] { "client", "capability_b_backoffice" },
                BusinessStates: new[] { "known", "authorized", "installable" },
                ArtifactStates: new[] { "downloaded_pending", "verification_required" },
                SupportTier: "client-baseline")
        });

    public static ModelCollectionsArtifact CreateDefaultCollections() => new(
        GovernanceArtifactStore.ModelCollectionsFile,
        "v3.1",
        new[]
        {
            new ModelCollectionItem(
                Key: "client-baseline",
                Scope: "client",
                VisibleInInstaller: true,
                ModelIds: new[] { "qwen2.5-3b-instruct-q4-k-m" }),
            new ModelCollectionItem(
                Key: "4gb-vram",
                Scope: "client",
                VisibleInInstaller: true,
                ModelIds: new[] { "qwen2.5-3b-instruct-q4-k-m" }),
            new ModelCollectionItem(
                Key: "backend-baseline",
                Scope: "backend",
                VisibleInInstaller: false,
                ModelIds: new[] { "qwen2.5-3b-instruct-q4-k-m" })
        });

    public static ModelPolicyArtifact CreateDefaultPolicy() => new(
        GovernanceArtifactStore.ModelPolicyFile,
        "v3.1",
        AllowDiscovery: false,
        RequireChecksum: true,
        MaxActiveModelsClient: 1,
        BlacklistRef: GovernanceArtifactStore.BlacklistFile,
        Rules: new[]
        {
            "one_active_client_model",
            "no_latest_or_unpinned_source",
            "checksum_required_before_qualification",
            "warmup_required_before_selection",
            "no_online_discovery_in_nominal_runtime"
        });

    public static ModelSourcesArtifact CreateDefaultSources() => new(
        GovernanceArtifactStore.ModelSourcesFile,
        "v3.1",
        new[]
        {
            new ModelSourceItem(
                Key: "hf-bartowski-qwen25-3b",
                Kind: "huggingface",
                Uri: "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf",
                RequiresChecksum: true,
                AllowedInAirGap: false),
            new ModelSourceItem(
                Key: "local-bundle",
                Kind: "local-bundle",
                Uri: "%LOCALAPPDATA%/SAAIA/Models",
                RequiresChecksum: true,
                AllowedInAirGap: true)
        });
}
