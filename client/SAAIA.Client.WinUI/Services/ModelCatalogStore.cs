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
    bool AllowedInAirGap,
    string? Revision = null);

internal static class ModelCatalogStore
{
    internal static string? GovernanceRootOverride { get; set; }

    private const string Qwen3_4B_2507_Q4KmSha256 = "2fde00ce69dd4899c70d020845e2638353015bba0fdf161b3eb965f2bca4464e";
    private const string Qwen3_4B_2507_Q5KmSha256 = "66713ce35a58a82fe87642d4ec13425bf9b9a46800fff5c49a665ef5701439dc";

    public static string? ResolveCanonicalModelId(string? modelIdOrFileName)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrFileName))
            return null;

        var probe = modelIdOrFileName.Trim();
        return FindKnownModel(probe)?.ModelId;
    }

    public static string? TryGetReferenceChecksum(string? modelIdOrFileName)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrFileName))
            return null;

        var probe = modelIdOrFileName.Trim();
        return FindKnownModel(probe)?.ChecksumSha256;
    }

    public static ModelCatalogItem? TryGetItem(string? modelIdOrFileName)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrFileName))
            return null;

        var probe = modelIdOrFileName.Trim();
        return FindKnownModel(probe);
    }

    private static ModelCatalogItem? FindKnownModel(string probe)
    {
        var effective = GetEffectiveCatalog().Items
            .FirstOrDefault(item =>
                string.Equals(item.ModelId, probe, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.FileName, probe, StringComparison.OrdinalIgnoreCase));
        if (effective is not null)
            return effective;

        return CreateDefaultCatalog().Items.FirstOrDefault(item =>
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

            var revision = string.IsNullOrWhiteSpace(source.Revision)
                ? "main"
                : source.Revision.Trim();
            return $"{baseUri}/resolve/{revision}/{model.FileName}";
        }

        return null;
    }

    public static ModelCatalogArtifact CreateDefaultCatalog() => new(
        GovernanceArtifactStore.ModelCatalogFile,
        "v3.1",
        "2026-07-25.qwen3-4b-2507-promotion",
        DateTimeOffset.UtcNow,
        new[]
        {
            Qwen3_4B_2507_Item(
                "qwen3-4b-instruct-2507-q5-k-m",
                "Qwen3 4B Instruct 2507 Q5_K_M",
                "Q5_K_M",
                "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
                Qwen3_4B_2507_Q5KmSha256,
                "client-recommended"),
            Qwen3_4B_2507_Item(
                "qwen3-4b-instruct-2507-q4-k-m",
                "Qwen3 4B Instruct 2507 Q4_K_M",
                "Q4_K_M",
                "Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
                Qwen3_4B_2507_Q4KmSha256,
                "client-low-memory-fallback")
        });

    public static ModelCollectionsArtifact CreateDefaultCollections() => new(
        GovernanceArtifactStore.ModelCollectionsFile,
        "v3.1",
        new[]
        {
            new ModelCollectionItem(
                Key: "client-recommended-qwen3-4b-2507",
                Scope: "client",
                VisibleInInstaller: true,
                ModelIds: new[]
                {
                    "qwen3-4b-instruct-2507-q5-k-m",
                    "qwen3-4b-instruct-2507-q4-k-m"
                }),
            new ModelCollectionItem(
                Key: "backend-recommended-qwen3-4b-2507",
                Scope: "backend",
                VisibleInInstaller: false,
                ModelIds: new[]
                {
                    "qwen3-4b-instruct-2507-q5-k-m",
                    "qwen3-4b-instruct-2507-q4-k-m"
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
                Key: "hf-bartowski-qwen3-4b-2507",
                Kind: "huggingface",
                Uri: "https://huggingface.co/bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF",
                RequiresChecksum: true,
                AllowedInAirGap: false,
                Revision: "ae44f08e1392f39c0e474af10c3ff8355c8b6688"),
            new ModelSourceItem(
                Key: "local-bundle",
                Kind: "local-bundle",
                Uri: "%LOCALAPPDATA%/SAAIA/Models",
                RequiresChecksum: true,
                AllowedInAirGap: true)
        });

    private static ModelCatalogItem Qwen3_4B_2507_Item(
        string modelId,
        string displayName,
        string quantization,
        string fileName,
        string checksumSha256,
        string supportTier)
        => new(
            ModelId: modelId,
            DisplayName: displayName,
            Family: "qwen3-4b-instruct-2507",
            Quantization: quantization,
            FileName: fileName,
            SourceRef: "hf-bartowski-qwen3-4b-2507",
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
                Architecture: "qwen3",
                BlockCount: 36,
                HeadCount: 32,
                HeadCountKv: 8,
                EmbeddingLength: null,
                ContextLength: 262144,
                FeedForwardLength: null),
            ApprovedRuntimeRefs: new[] { "llama.cpp-cuda", "llama.cpp-vulkan", "llama.cpp-sycl", "llama.cpp-hip", "llama.cpp-cpu" },
            SupportedScopes: new[] { "client", "backend", "capability_b_backoffice" },
            BusinessStates: new[] { "known", "authorized", "installable", "qualified" },
            ArtifactStates: new[] { "download_required", "verification_required" },
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
