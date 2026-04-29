using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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
    internal static string? GovernanceRootOverride { get; set; }

    private const string Qwen25_3B_Q4KmSha256 = "9c9f56a391a3abbd5b89d0245bf6106081bcc3173119d4229235dd9d23253f94";
    private const string Qwen25_3B_Q6KlSha256 = "930d792ba9cebbb98faaef6755c62b47cb24bb2d16fb10a338ac80d721b81796";
    private const string Qwen25_3B_Q80Sha256 = "12491ec9f03aab7f0b96cdb7742695e6583d17ee129de48332d04b9cf6acd960";
    private const string Mistral7B_V03_Iq3MSha256 = "4ea14c5a6c787ac2703505f04a4ee746f746d1ace3ffd907af28f6f179e6b224";
    private const string Mistral7B_V03_Q4KmSha256 = "56d2db1ee4e4330338433c3a2d1f98f3d647db9cef785fd6e640061e1c98dde2";
    private const string Gemma4_E2B_Q4KmSha256 = "ac0069ebccd39925d836f24a88c0f0c858d20578c29b21ab7cedce66ee576845";
    private const string Gemma4_E2B_Q80Sha256 = "6db0088e7e2b6459dfb29fa59b0b1d7299d249ef28debc464d4d564caf444511";
    private const string Gemma4_E4B_Q4KmSha256 = "dff0ffba4c90b4082d70214d53ce9504a28d4d8d998276dcb3b8881a656c742a";
    private const string Qwen36_27B_Q4KmSha256 = "5ed60d0af4650a854b1755bd392f9aef4872643dc25a254bc68043fa638392a0";
    private const string Qwen36_35B_A3B_Q3KsSha256 = "212ccdf37d416167ce8dcd7e3a59bcd45b30ac7531822a1e7bb79bfbacb2d1aa";
    private const string Qwen36_35B_A3B_Q3KmSha256 = "1b715841683f960bd9a49f008181bd910ee169b78d4cf465b6fde7f4d929ff99";
    private const string Qwen36_35B_A3B_Iq4XsSha256 = "649d7508507b84638732c4f52c24c8b15843c6dca2f3ff793ae07c14a67ebbb3";
    private const string Qwen36_35B_A3B_Q4KmSha256 = "ac0e2c1189e055faa36eff361580e79c5bd6f8e76bffb4ce547f167d53e31a61";
    private const string Qwen36_35B_A3B_Q5KmSha256 = "c13ce26253ea334df472bd8fbd2d6da66d8a41195c17f6fcbf44c4d20ece0932";

    public static string? ResolveCanonicalModelId(string? modelIdOrFileName)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrFileName))
            return null;

        var probe = modelIdOrFileName.Trim();
        var catalog = GetEffectiveCatalog();
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
        var catalog = GetEffectiveCatalog();
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
        return GetEffectiveCatalog().Items
            .FirstOrDefault(item =>
                string.Equals(item.ModelId, probe, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.FileName, probe, StringComparison.OrdinalIgnoreCase));
    }

    public static ModelCatalogArtifact GetEffectiveCatalog(string? governanceRoot = null)
        => ReadEffectiveArtifact(
            GovernanceArtifactStore.ModelCatalogFile,
            CreateDefaultCatalog,
            governanceRoot);

    public static ModelCollectionsArtifact GetEffectiveCollections(string? governanceRoot = null)
        => ReadEffectiveArtifact(
            GovernanceArtifactStore.ModelCollectionsFile,
            CreateDefaultCollections,
            governanceRoot);

    public static ModelPolicyArtifact GetEffectivePolicy(string? governanceRoot = null)
        => ReadEffectiveArtifact(
            GovernanceArtifactStore.ModelPolicyFile,
            CreateDefaultPolicy,
            governanceRoot);

    public static ModelSourcesArtifact GetEffectiveSources(string? governanceRoot = null)
        => ReadEffectiveArtifact(
            GovernanceArtifactStore.ModelSourcesFile,
            CreateDefaultSources,
            governanceRoot);

    internal static bool IsDiscoveryAllowed(string? governanceRoot = null)
        => GetEffectivePolicy(governanceRoot).AllowDiscovery;

    internal static int GetMaxActiveClientModels(string? governanceRoot = null)
        => Math.Max(1, GetEffectivePolicy(governanceRoot).MaxActiveModelsClient);

    internal static IReadOnlyList<ModelCatalogItem> GetCollectionModels(string key, string? governanceRoot = null)
    {
        var catalog = GetEffectiveCatalog(governanceRoot);
        var collections = GetEffectiveCollections(governanceRoot);
        var modelIds = collections.Items
            .Where(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.ModelIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return catalog.Items
            .Where(item => modelIds.Contains(item.ModelId))
            .ToArray();
    }

    internal static IReadOnlyList<ModelCatalogItem> GetInstallerVisibleClientModels(string? governanceRoot = null)
    {
        var catalog = GetEffectiveCatalog(governanceRoot);
        var collections = GetEffectiveCollections(governanceRoot);
        var modelIds = collections.Items
            .Where(item => string.Equals(item.Scope, "client", StringComparison.OrdinalIgnoreCase) && item.VisibleInInstaller)
            .SelectMany(item => item.ModelIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = catalog.Items
            .Where(item => item.SupportedScopes.Contains("client", StringComparer.OrdinalIgnoreCase)
                        && modelIds.Contains(item.ModelId))
            .ToArray();

        return selected.Length > 0
            ? selected
            : catalog.Items.Where(item => item.SupportedScopes.Contains("client", StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal static bool IsInstallerVisibleClientModel(string? modelIdOrFileName, string? governanceRoot = null)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrFileName))
            return false;

        var probe = modelIdOrFileName.Trim();
        var canonical = ResolveCanonicalModelId(probe);
        return GetInstallerVisibleClientModels(governanceRoot).Any(item =>
            string.Equals(item.ModelId, canonical, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.FileName, probe, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? GetClientCatalogPolicyViolation(string? modelIdOrFileName, string? governanceRoot = null)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrFileName))
            return null;

        if (IsDiscoveryAllowed(governanceRoot))
            return null;

        return IsInstallerVisibleClientModel(modelIdOrFileName, governanceRoot)
            ? null
            : $"Model '{modelIdOrFileName}' is not approved by the governed client catalog.";
    }

    internal static string? TryBuildDownloadUrl(ModelCatalogItem model, string? governanceRoot = null)
    {
        var policy = GetEffectivePolicy(governanceRoot);
        if (policy.RequireChecksum && string.IsNullOrWhiteSpace(model.ChecksumSha256))
            return null;

        var source = GetEffectiveSources(governanceRoot).Items.FirstOrDefault(item =>
            string.Equals(item.Key, model.SourceRef, StringComparison.OrdinalIgnoreCase));
        if (source is null)
            return null;

        if (string.Equals(source.Kind, "huggingface", StringComparison.OrdinalIgnoreCase))
        {
            var baseUri = (source.Uri ?? string.Empty).Trim().TrimEnd('/');
            if (!baseUri.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase))
                return null;

            return $"{baseUri}/resolve/main/{model.FileName}";
        }

        return null;
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
                supportTier: "client-apache-test"),
            Qwen36ServerItem(
                "qwen3.6-27b-q4-k-m",
                "Qwen3.6 27B Q4_K_M",
                "qwen3.6",
                "Q4_K_M",
                "Qwen3.6-27B-Q4_K_M.gguf",
                Qwen36_27B_Q4KmSha256,
                "backend-qwen3.6"),
            Qwen36ServerItem(
                "qwen3.6-35b-a3b-ud-q3-k-s",
                "Qwen3.6 35B A3B UD Q3_K_S",
                "qwen3.6-a3b",
                "Q3_K_S",
                "Qwen3.6-35B-A3B-UD-Q3_K_S.gguf",
                Qwen36_35B_A3B_Q3KsSha256,
                "backend-a3b"),
            Qwen36ServerItem(
                "qwen3.6-35b-a3b-ud-q3-k-m",
                "Qwen3.6 35B A3B UD Q3_K_M",
                "qwen3.6-a3b",
                "Q3_K_M",
                "Qwen3.6-35B-A3B-UD-Q3_K_M.gguf",
                Qwen36_35B_A3B_Q3KmSha256,
                "backend-a3b"),
            Qwen36ServerItem(
                "qwen3.6-35b-a3b-ud-iq4-xs",
                "Qwen3.6 35B A3B UD IQ4_XS",
                "qwen3.6-a3b",
                "IQ4_XS",
                "Qwen3.6-35B-A3B-UD-IQ4_XS.gguf",
                Qwen36_35B_A3B_Iq4XsSha256,
                "backend-a3b"),
            Qwen36ServerItem(
                "qwen3.6-35b-a3b-ud-q4-k-m",
                "Qwen3.6 35B A3B UD Q4_K_M",
                "qwen3.6-a3b",
                "Q4_K_M",
                "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
                Qwen36_35B_A3B_Q4KmSha256,
                "backend-a3b"),
            Qwen36ServerItem(
                "qwen3.6-35b-a3b-ud-q5-k-m",
                "Qwen3.6 35B A3B UD Q5_K_M",
                "qwen3.6-a3b",
                "Q5_K_M",
                "Qwen3.6-35B-A3B-UD-Q5_K_M.gguf",
                Qwen36_35B_A3B_Q5KmSha256,
                "backend-a3b")
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
                ModelIds: new[] { "qwen2.5-3b-instruct-q4-k-m", "mistral-7b-instruct-v0.3-q4-k-m" }),
            new ModelCollectionItem(
                Key: "backend-low-capacity",
                Scope: "backend",
                VisibleInInstaller: false,
                ModelIds: new[]
                {
                    "qwen2.5-3b-instruct-q4-k-m",
                    "qwen2.5-3b-instruct-q6-k-l",
                    "qwen2.5-3b-instruct-q8-0",
                    "gemma-4-e2b-it-q4-k-m",
                    "mistral-7b-instruct-v0.3-iq3-m"
                }),
            new ModelCollectionItem(
                Key: "backend-qwen3.6",
                Scope: "backend",
                VisibleInInstaller: false,
                ModelIds: new[]
                {
                    "qwen3.6-27b-q4-k-m",
                    "qwen3.6-35b-a3b-ud-q3-k-s",
                    "qwen3.6-35b-a3b-ud-q3-k-m",
                    "qwen3.6-35b-a3b-ud-iq4-xs",
                    "qwen3.6-35b-a3b-ud-q4-k-m",
                    "qwen3.6-35b-a3b-ud-q5-k-m"
                }),
            new ModelCollectionItem(
                Key: "backend-a3b",
                Scope: "backend",
                VisibleInInstaller: false,
                ModelIds: new[]
                {
                    "qwen3.6-35b-a3b-ud-q3-k-s",
                    "qwen3.6-35b-a3b-ud-q3-k-m",
                    "qwen3.6-35b-a3b-ud-iq4-xs",
                    "qwen3.6-35b-a3b-ud-q4-k-m",
                    "qwen3.6-35b-a3b-ud-q5-k-m"
                })
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
            SupportedScopes: new[] { "client", "backend", "capability_b_backoffice" },
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
            SupportedScopes: new[] { "client", "backend", "capability_b_backoffice" },
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
            SupportedScopes: new[] { "client", "backend", "capability_b_backoffice" },
            BusinessStates: new[] { "known", "authorized", "installable", "experimental" },
            ArtifactStates: new[] { "download_required", "verification_required" },
            SupportTier: supportTier);

    private static ModelCatalogItem Qwen36ServerItem(
        string modelId,
        string displayName,
        string family,
        string quantization,
        string fileName,
        string checksumSha256,
        string supportTier)
        => new(
            ModelId: modelId,
            DisplayName: displayName,
            Family: family,
            Quantization: quantization,
            FileName: fileName,
            SourceRef: "local-bundle",
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
                Architecture: "qwen3",
                BlockCount: null,
                HeadCount: null,
                HeadCountKv: null,
                EmbeddingLength: null,
                ContextLength: 40960,
                FeedForwardLength: null),
            ApprovedRuntimeRefs: new[] { "llama.cpp-cuda", "llama.cpp-vulkan", "llama.cpp-cpu" },
            SupportedScopes: new[] { "backend", "capability_b_backoffice" },
            BusinessStates: new[] { "known", "authorized", "installable" },
            ArtifactStates: new[] { "downloaded_pending", "verification_required" },
            SupportTier: supportTier);

    private static T ReadEffectiveArtifact<T>(
        string fileName,
        Func<T> fallback,
        string? governanceRoot = null)
    {
        try
        {
            var root = governanceRoot ?? GovernanceRootOverride ?? GovernanceArtifactStore.DefaultRoot;
            var path = GovernanceArtifactStore.ResolvePath(fileName, root);
            var checksumPath = path + ".sha256";
            if (!File.Exists(path) || !File.Exists(checksumPath))
                return fallback();

            var json = File.ReadAllText(path);
            var expected = File.ReadAllText(checksumPath).Trim();
            var actual = GovernanceArtifactStore.ComputeSha256Hex(json);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return fallback();

            var value = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return value ?? fallback();
        }
        catch
        {
            return fallback();
        }
    }
}
