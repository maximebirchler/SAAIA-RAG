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
    private const string Qwen25_3B_Q4KmSha256 = "9c9f56a391a3abbd5b89d0245bf6106081bcc3173119d4229235dd9d23253f94";
    private const string Qwen25_3B_Q6KlSha256 = "930d792ba9cebbb98faaef6755c62b47cb24bb2d16fb10a338ac80d721b81796";
    private const string Qwen25_3B_Q80Sha256 = "12491ec9f03aab7f0b96cdb7742695e6583d17ee129de48332d04b9cf6acd960";
    private const string Mistral7B_V03_Iq3MSha256 = "4ea14c5a6c787ac2703505f04a4ee746f746d1ace3ffd907af28f6f179e6b224";
    private const string Mistral7B_V03_Q4KmSha256 = "56d2db1ee4e4330338433c3a2d1f98f3d647db9cef785fd6e640061e1c98dde2";
    private const string Gemma4_E2B_Q4KmSha256 = "ac0069ebccd39925d836f24a88c0f0c858d20578c29b21ab7cedce66ee576845";
    private const string Gemma4_E2B_Q80Sha256 = "6db0088e7e2b6459dfb29fa59b0b1d7299d249ef28debc464d4d564caf444511";
    private const string Gemma4_E4B_Q4KmSha256 = "dff0ffba4c90b4082d70214d53ce9504a28d4d8d998276dcb3b8881a656c742a";

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

    public static string? TryGetReferenceChecksum(string? modelIdOrFileName)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrFileName))
            return null;

        var probe = modelIdOrFileName.Trim();
        var catalog = CreateDefaultCatalog();
        return catalog.Items
            .FirstOrDefault(item =>
                string.Equals(item.ModelId, probe, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.FileName, probe, StringComparison.OrdinalIgnoreCase))
            ?.ChecksumSha256;
    }

    public static ModelCatalogItem? TryGetItem(string? modelIdOrFileName)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrFileName))
            return null;

        var probe = modelIdOrFileName.Trim();
        return CreateDefaultCatalog().Items
            .FirstOrDefault(item =>
                string.Equals(item.ModelId, probe, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.FileName, probe, StringComparison.OrdinalIgnoreCase));
    }

    public static ModelCatalogArtifact CreateDefaultCatalog() => new(
        GovernanceArtifactStore.ModelCatalogFile,
        "v3.1",
        "2026-04-23.model-checksums",
        DateTimeOffset.UtcNow,
        new[]
        {
            QwenItem(
                "qwen2.5-3b-instruct-q4-k-m",
                "Qwen2.5 3B Instruct Q4_K_M",
                "Q4_K_M",
                "Qwen2.5-3B-Instruct-Q4_K_M.gguf",
                Qwen25_3B_Q4KmSha256,
                "client-baseline"),
            QwenItem(
                "qwen2.5-3b-instruct-q6-k-l",
                "Qwen2.5 3B Instruct Q6_K_L",
                "Q6_K_L",
                "Qwen2.5-3B-Instruct-Q6_K_L.gguf",
                Qwen25_3B_Q6KlSha256,
                "client-quality"),
            QwenItem(
                "qwen2.5-3b-instruct-q8-0",
                "Qwen2.5 3B Instruct Q8_0",
                "Q8_0",
                "Qwen2.5-3B-Instruct-Q8_0.gguf",
                Qwen25_3B_Q80Sha256,
                "client-quality"),
            MistralItem(
                "mistral-7b-instruct-v0.3-iq3-m",
                "Mistral 7B Instruct v0.3 IQ3_M",
                "IQ3_M",
                "Mistral-7B-Instruct-v0.3-IQ3_M.gguf",
                Mistral7B_V03_Iq3MSha256,
                "client-large"),
            MistralItem(
                "mistral-7b-instruct-v0.3-q4-k-m",
                "Mistral 7B Instruct v0.3 Q4_K_M",
                "Q4_K_M",
                "Mistral-7B-Instruct-v0.3-Q4_K_M.gguf",
                Mistral7B_V03_Q4KmSha256,
                "client-large"),
            Gemma4Item(
                "gemma-4-e2b-it-q4-k-m",
                "Gemma 4 E2B IT Q4_K_M",
                "E2B",
                "Q4_K_M",
                "gemma-4-E2B-it-Q4_K_M.gguf",
                "hf-unsloth-gemma4-e2b",
                checksumSha256: Gemma4_E2B_Q4KmSha256,
                supportTier: "client-apache-test"),
            Gemma4Item(
                "gemma-4-e2b-it-q8-0",
                "Gemma 4 E2B IT Q8_0",
                "E2B",
                "Q8_0",
                "gemma-4-E2B-it-Q8_0.gguf",
                "hf-unsloth-gemma4-e2b",
                checksumSha256: Gemma4_E2B_Q80Sha256,
                supportTier: "client-apache-test"),
            Gemma4Item(
                "gemma-4-e4b-it-q4-k-m",
                "Gemma 4 E4B IT Q4_K_M",
                "E4B",
                "Q4_K_M",
                "gemma-4-E4B-it-Q4_K_M.gguf",
                "hf-unsloth-gemma4-e4b",
                checksumSha256: Gemma4_E4B_Q4KmSha256,
                supportTier: "client-apache-test")
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
                ModelIds: new[]
                {
                    "qwen2.5-3b-instruct-q4-k-m",
                    "qwen2.5-3b-instruct-q6-k-l",
                    "qwen2.5-3b-instruct-q8-0"
                }),
            new ModelCollectionItem(
                Key: "4gb-vram",
                Scope: "client",
                VisibleInInstaller: true,
                ModelIds: new[] { "qwen2.5-3b-instruct-q4-k-m" }),
            new ModelCollectionItem(
                Key: "8gb-vram",
                Scope: "client",
                VisibleInInstaller: true,
                ModelIds: new[] { "qwen2.5-3b-instruct-q6-k-l", "qwen2.5-3b-instruct-q8-0" }),
            new ModelCollectionItem(
                Key: "10gb-vram",
                Scope: "client",
                VisibleInInstaller: true,
                ModelIds: new[] { "mistral-7b-instruct-v0.3-iq3-m", "mistral-7b-instruct-v0.3-q4-k-m" }),
            new ModelCollectionItem(
                Key: "apache-test-family",
                Scope: "client",
                VisibleInInstaller: true,
                ModelIds: new[] { "gemma-4-e2b-it-q4-k-m", "gemma-4-e2b-it-q8-0", "gemma-4-e4b-it-q4-k-m", "mistral-7b-instruct-v0.3-q4-k-m" }),
            new ModelCollectionItem(
                Key: "backend-baseline",
                Scope: "backend",
                VisibleInInstaller: false,
                ModelIds: new[] { "qwen2.5-3b-instruct-q4-k-m", "mistral-7b-instruct-v0.3-q4-k-m" })
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
                Uri: "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF",
                RequiresChecksum: true,
                AllowedInAirGap: false),
            new ModelSourceItem(
                Key: "hf-bartowski-mistral-7b-v03",
                Kind: "huggingface",
                Uri: "https://huggingface.co/bartowski/Mistral-7B-Instruct-v0.3-GGUF",
                RequiresChecksum: true,
                AllowedInAirGap: false),
            new ModelSourceItem(
                Key: "hf-unsloth-gemma4-e2b",
                Kind: "huggingface",
                Uri: "https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF",
                RequiresChecksum: true,
                AllowedInAirGap: false),
            new ModelSourceItem(
                Key: "hf-unsloth-gemma4-e4b",
                Kind: "huggingface",
                Uri: "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF",
                RequiresChecksum: true,
                AllowedInAirGap: false),
            new ModelSourceItem(
                Key: "local-bundle",
                Kind: "local-bundle",
                Uri: "%LOCALAPPDATA%/SAAIA/Models",
                RequiresChecksum: true,
                AllowedInAirGap: true)
        });

    private static ModelCatalogItem QwenItem(
        string modelId,
        string displayName,
        string quantization,
        string fileName,
        string checksumSha256,
        string supportTier)
        => new(
            ModelId: modelId,
            DisplayName: displayName,
            Family: "qwen2.5",
            Quantization: quantization,
            FileName: fileName,
            SourceRef: "hf-bartowski-qwen25-3b",
            ChecksumSha256: checksumSha256,
            ChecksumStatus: "verified_reference_hash",
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
            SupportTier: supportTier);

    private static ModelCatalogItem MistralItem(
        string modelId,
        string displayName,
        string quantization,
        string fileName,
        string checksumSha256,
        string supportTier)
        => new(
            ModelId: modelId,
            DisplayName: displayName,
            Family: "mistral",
            Quantization: quantization,
            FileName: fileName,
            SourceRef: "hf-bartowski-mistral-7b-v03",
            ChecksumSha256: checksumSha256,
            ChecksumStatus: "verified_reference_hash",
            License: new ModelLicenseInfo(
                LicenseFamily: "apache-2.0",
                LicenseDisplayName: "Apache License 2.0",
                CommercialUseAllowed: true,
                CommercialUseConditions: null,
                CommercialUseThresholdMau: null,
                RequiresSeparateCommercialLicenseAboveThreshold: false),
            Gguf: new ModelGgufMetadata(
                Architecture: "llama",
                BlockCount: 32,
                HeadCount: 32,
                HeadCountKv: 8,
                EmbeddingLength: 4096,
                ContextLength: 32768,
                FeedForwardLength: 14336),
            ApprovedRuntimeRefs: new[] { "llama.cpp-cuda", "llama.cpp-vulkan", "llama.cpp-cpu" },
            SupportedScopes: new[] { "client", "capability_b_backoffice" },
            BusinessStates: new[] { "known", "authorized", "installable" },
            ArtifactStates: new[] { "downloaded_pending", "verification_required" },
            SupportTier: supportTier);

    private static ModelCatalogItem Gemma4Item(
        string modelId,
        string displayName,
        string family,
        string quantization,
        string fileName,
        string sourceRef,
        string? checksumSha256,
        string supportTier)
        => new(
            ModelId: modelId,
            DisplayName: displayName,
            Family: "gemma4-" + family.ToLowerInvariant(),
            Quantization: quantization,
            FileName: fileName,
            SourceRef: sourceRef,
            ChecksumSha256: checksumSha256,
            ChecksumStatus: string.IsNullOrWhiteSpace(checksumSha256) ? "pending_reference_hash" : "verified_reference_hash",
            License: new ModelLicenseInfo(
                LicenseFamily: "apache-2.0",
                LicenseDisplayName: "Apache License 2.0",
                CommercialUseAllowed: true,
                CommercialUseConditions: null,
                CommercialUseThresholdMau: null,
                RequiresSeparateCommercialLicenseAboveThreshold: false),
            Gguf: new ModelGgufMetadata(
                Architecture: "gemma4",
                BlockCount: family.Equals("E2B", StringComparison.OrdinalIgnoreCase) ? 35 : 42,
                HeadCount: null,
                HeadCountKv: null,
                EmbeddingLength: null,
                ContextLength: 131072,
                FeedForwardLength: null),
            ApprovedRuntimeRefs: new[] { "llama.cpp-cuda", "llama.cpp-vulkan", "llama.cpp-cpu" },
            SupportedScopes: new[] { "client", "capability_b_backoffice" },
            BusinessStates: new[] { "known", "authorized", "installable", "experimental" },
            ArtifactStates: new[] { "download_required", "verification_required" },
            SupportTier: supportTier);
}
