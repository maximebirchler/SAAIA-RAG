using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCatalogBuilder
{
    internal static IReadOnlyList<AdminRuntimeCatalogRuntimeDto> BuildRuntimes(
        RagOptions rag,
        IReadOnlyList<AdminRuntimeWarmupProfileDto> profiles,
        string capabilityAKey,
        string capabilityBKey)
        =>
        [
            BuildRuntimeDescriptor(
                runtimeKey: "qdrant",
                label: "Qdrant vector store",
                kind: "vector_store",
                enabled: !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl),
                baseUrl: rag.QdrantBaseUrl,
                model: rag.QdrantCollection,
                configurationSource: "rag_options.qdrant_base_url",
                expectedCapabilityKeys: ["core.retrieval"],
                requiredSettingKeys: ["qdrant_base_url", "qdrant_collection"],
                missingSettingKeys: ResolveMissingSettingKeys(
                    ("qdrant_base_url", rag.QdrantBaseUrl),
                    ("qdrant_collection", rag.QdrantCollection)),
                profiles: profiles),
            BuildRuntimeDescriptor(
                runtimeKey: "tei-embeddings",
                label: "TEI embeddings",
                kind: "embedding_runtime",
                enabled: !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl),
                baseUrl: rag.EmbeddingsBaseUrl,
                model: rag.EmbeddingsModel,
                configurationSource: "rag_options.embeddings_base_url",
                expectedCapabilityKeys: ["core.retrieval"],
                requiredSettingKeys: ["embeddings_base_url", "embeddings_model"],
                missingSettingKeys: ResolveMissingSettingKeys(
                    ("embeddings_base_url", rag.EmbeddingsBaseUrl),
                    ("embeddings_model", rag.EmbeddingsModel)),
                profiles: profiles),
            BuildRuntimeDescriptor(
                runtimeKey: "tei-rerank",
                label: "TEI rerank",
                kind: "rerank_runtime",
                enabled: rag.EnableRerank,
                baseUrl: string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl,
                model: rag.RerankModel,
                configurationSource: ResolveRerankConfigurationSource(rag),
                expectedCapabilityKeys: ["core.retrieval"],
                requiredSettingKeys: ["rerank_enabled", "rerank_base_url_or_embeddings_base_url", "rerank_model"],
                missingSettingKeys: rag.EnableRerank
                    ? ResolveMissingSettingKeys(
                        ("rerank_base_url_or_embeddings_base_url", string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl),
                        ("rerank_model", rag.RerankModel))
                    : [],
                profiles: profiles),
            BuildRuntimeDescriptor(
                runtimeKey: "server-capability-a",
                label: "Server capability A",
                kind: "backoffice_capability",
                enabled: true,
                baseUrl: null,
                model: "corpus_enrichment_admin",
                configurationSource: "backend_internal",
                expectedCapabilityKeys: [capabilityAKey],
                requiredSettingKeys: [],
                missingSettingKeys: [],
                profiles: profiles),
            BuildRuntimeDescriptor(
                runtimeKey: "server-capability-b",
                label: "Server capability B",
                kind: "backoffice_generation",
                enabled: IsBackofficeGenerationEnabled(),
                baseUrl: null,
                model: "summary.generate",
                configurationSource: "environment.BACKOFFICE_LLM_ENABLED",
                expectedCapabilityKeys: [capabilityBKey],
                requiredSettingKeys: ["BACKOFFICE_LLM_ENABLED"],
                missingSettingKeys: IsBackofficeGenerationEnabled() ? [] : ["BACKOFFICE_LLM_ENABLED"],
                profiles: profiles)
        ];

    internal static AdminRuntimeModelCatalogEntryDto[] BuildModelCatalogEntries(
        IReadOnlyList<AdminRuntimeCatalogRuntimeDto> runtimes)
        => runtimes
            .Select(runtime => new AdminRuntimeModelCatalogEntryDto(
                Key: runtime.Key,
                Label: runtime.Label,
                RuntimeKey: runtime.Key,
                Kind: runtime.Kind,
                Enabled: runtime.Enabled,
                Implemented: true,
                BaseUrl: runtime.BaseUrl,
                Model: runtime.Model,
                StatusNote: runtime.ReadinessStatus,
                ReadinessStatus: runtime.ReadinessStatus,
                ConfigurationSource: runtime.ConfigurationSource,
                UsedByProfileKeys: runtime.UsedByProfileKeys,
                RequiredByProfileKeys: runtime.RequiredByProfileKeys,
                ExpectedCapabilityKeys: runtime.ExpectedCapabilityKeys,
                RequiredSettingKeys: runtime.RequiredSettingKeys,
                MissingSettingKeys: runtime.MissingSettingKeys))
            .ToArray();

    internal static IReadOnlyList<AdminRuntimeWarmupProfileDto> BuildWarmupProfiles(RuntimeGovernanceOptions options)
        =>
        [
            new(
                Key: options.DefaultProfileKey,
                Label: "Default local qualification",
                PassCount: Math.Max(1, options.WarmupPassCount),
                Checks:
                [
                    "qdrant.collection",
                    "tei.embeddings",
                    "tei.rerank_if_enabled"
                ],
                HardwareRequirements: new AdminRuntimeHardwareRequirementsDto(
                    options.MinCpuCores,
                    options.MinAvailableMemoryMb,
                    options.Require64BitProcess),
                PerformanceBudgets: BuildPerformanceBudgets(options),
                CheckPolicy: new AdminRuntimeWarmupCheckPolicyDto(
                    RequireRerankEnabled: false,
                    AllowSkippedChecks: true,
                    EnforcePerformanceBudgets: true),
                RuntimeRequirements: new AdminRuntimeWarmupRuntimeRequirementsDto(
                    RequireQdrant: true,
                    RequireEmbeddings: true,
                    RequireRerank: false,
                    RequireCollection: true,
                    RequireEmbeddingsModel: true,
                    RequireRerankModel: false),
                FreshnessPolicy: new AdminRuntimeWarmupFreshnessPolicyDto(
                    MaxQualificationAgeHours: options.MaxQualificationAgeHours,
                    RequireRequalificationWhenExpired: true)),
            new(
                Key: "strict-local",
                Label: "Strict local qualification",
                PassCount: Math.Max(1, options.WarmupPassCount),
                Checks:
                [
                    "qdrant.collection",
                    "tei.embeddings",
                    "tei.rerank_if_enabled"
                ],
                HardwareRequirements: new AdminRuntimeHardwareRequirementsDto(
                    Math.Max(options.StrictProfileMinCpuCores, 4),
                    Math.Max(options.StrictProfileMinAvailableMemoryMb, 4096),
                    true),
                PerformanceBudgets: BuildStrictPerformanceBudgets(options),
                CheckPolicy: new AdminRuntimeWarmupCheckPolicyDto(
                    RequireRerankEnabled: false,
                    AllowSkippedChecks: true,
                    EnforcePerformanceBudgets: true),
                RuntimeRequirements: new AdminRuntimeWarmupRuntimeRequirementsDto(
                    RequireQdrant: true,
                    RequireEmbeddings: true,
                    RequireRerank: false,
                    RequireCollection: true,
                    RequireEmbeddingsModel: true,
                    RequireRerankModel: false),
                FreshnessPolicy: new AdminRuntimeWarmupFreshnessPolicyDto(
                    MaxQualificationAgeHours: options.StrictProfileMaxQualificationAgeHours,
                    RequireRequalificationWhenExpired: true)),
            new(
                Key: "strict-rerank",
                Label: "Strict local qualification with rerank",
                PassCount: Math.Max(1, options.WarmupPassCount),
                Checks:
                [
                    "qdrant.collection",
                    "tei.embeddings",
                    "tei.rerank_required"
                ],
                HardwareRequirements: new AdminRuntimeHardwareRequirementsDto(
                    Math.Max(options.StrictRerankProfileMinCpuCores, 4),
                    Math.Max(options.StrictRerankProfileMinAvailableMemoryMb, 4096),
                    true),
                PerformanceBudgets: BuildStrictPerformanceBudgets(options),
                CheckPolicy: new AdminRuntimeWarmupCheckPolicyDto(
                    RequireRerankEnabled: true,
                    AllowSkippedChecks: false,
                    EnforcePerformanceBudgets: true),
                RuntimeRequirements: new AdminRuntimeWarmupRuntimeRequirementsDto(
                    RequireQdrant: true,
                    RequireEmbeddings: true,
                    RequireRerank: true,
                    RequireCollection: true,
                    RequireEmbeddingsModel: true,
                    RequireRerankModel: true),
                FreshnessPolicy: new AdminRuntimeWarmupFreshnessPolicyDto(
                    MaxQualificationAgeHours: options.StrictRerankProfileMaxQualificationAgeHours,
                    RequireRequalificationWhenExpired: true))
        ];

    internal static AdminRuntimeWarmupProfileDto ResolveProfile(string? requestedProfileKey, RuntimeGovernanceOptions options)
    {
        var profiles = BuildWarmupProfiles(options);
        return profiles.FirstOrDefault(profile => string.Equals(profile.Key, requestedProfileKey, StringComparison.OrdinalIgnoreCase))
            ?? profiles[0];
    }

    internal static bool IsBackofficeGenerationEnabled()
        => string.Equals(Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);

    private static AdminRuntimeCatalogRuntimeDto BuildRuntimeDescriptor(
        string runtimeKey,
        string label,
        string kind,
        bool enabled,
        string? baseUrl,
        string? model,
        string configurationSource,
        IReadOnlyList<string> expectedCapabilityKeys,
        IReadOnlyList<string> requiredSettingKeys,
        IReadOnlyList<string> missingSettingKeys,
        IReadOnlyList<AdminRuntimeWarmupProfileDto> profiles)
    {
        var usedByProfiles = ResolveProfilesForRuntime(runtimeKey, profiles, requiredOnly: false);
        var requiredByProfiles = ResolveProfilesForRuntime(runtimeKey, profiles, requiredOnly: true);
        var readinessStatus = ResolveRuntimeReadinessStatus(runtimeKey, enabled, missingSettingKeys);

        return new AdminRuntimeCatalogRuntimeDto(
            Key: runtimeKey,
            Label: label,
            Kind: kind,
            Enabled: enabled,
            BaseUrl: baseUrl,
            Model: model,
            ReadinessStatus: readinessStatus,
            ConfigurationSource: configurationSource,
            UsedByProfileKeys: usedByProfiles,
            RequiredByProfileKeys: requiredByProfiles,
            ExpectedCapabilityKeys: expectedCapabilityKeys,
            RequiredSettingKeys: requiredSettingKeys,
            MissingSettingKeys: missingSettingKeys);
    }

    private static IReadOnlyList<string> ResolveProfilesForRuntime(
        string runtimeKey,
        IReadOnlyList<AdminRuntimeWarmupProfileDto> profiles,
        bool requiredOnly)
        => profiles
            .Where(profile => RuntimeMatchesProfile(runtimeKey, profile, requiredOnly))
            .Select(profile => profile.Key)
            .ToArray();

    private static bool RuntimeMatchesProfile(
        string runtimeKey,
        AdminRuntimeWarmupProfileDto profile,
        bool requiredOnly)
    {
        var requirements = profile.RuntimeRequirements ?? new AdminRuntimeWarmupRuntimeRequirementsDto(
            RequireQdrant: true,
            RequireEmbeddings: true,
            RequireRerank: false,
            RequireCollection: true,
            RequireEmbeddingsModel: true,
            RequireRerankModel: false);

        return runtimeKey switch
        {
            "qdrant" => requirements.RequireQdrant || requirements.RequireCollection,
            "tei-embeddings" => requirements.RequireEmbeddings || requirements.RequireEmbeddingsModel,
            "tei-rerank" => requiredOnly
                ? requirements.RequireRerank || requirements.RequireRerankModel || profile.CheckPolicy?.RequireRerankEnabled == true
                : profile.Checks.Any(static check => check.Contains("tei.rerank", StringComparison.OrdinalIgnoreCase))
                  || requirements.RequireRerank
                  || requirements.RequireRerankModel
                  || profile.CheckPolicy?.RequireRerankEnabled == true,
            _ => false
        };
    }

    private static IReadOnlyList<string> ResolveMissingSettingKeys(params (string Key, string? Value)[] settings)
        => settings
            .Where(static setting => string.IsNullOrWhiteSpace(setting.Value))
            .Select(static setting => setting.Key)
            .ToArray();

    private static string ResolveRerankConfigurationSource(RagOptions rag)
    {
        if (!rag.EnableRerank)
            return "rag_options.rerank_disabled";

        return string.IsNullOrWhiteSpace(rag.RerankBaseUrl)
            ? "derived_from_rag_options.embeddings_base_url"
            : "rag_options.rerank_base_url";
    }

    private static string ResolveRuntimeReadinessStatus(
        string runtimeKey,
        bool enabled,
        IReadOnlyList<string> missingSettingKeys)
    {
        if (runtimeKey == "tei-rerank" && !enabled)
            return "disabled";

        if (!enabled)
            return "not_configured";

        return missingSettingKeys.Count == 0
            ? "configured"
            : "partially_configured";
    }

    private static AdminRuntimeWarmupPerformanceBudgetsDto BuildPerformanceBudgets(RuntimeGovernanceOptions options)
        => new(
            MaxPassDurationMs: options.MaxWarmupPassDurationMs,
            MaxQdrantCheckMs: options.MaxQdrantCheckMs,
            MaxEmbeddingsCheckMs: options.MaxEmbeddingsCheckMs,
            MaxRerankCheckMs: options.MaxRerankCheckMs);

    private static AdminRuntimeWarmupPerformanceBudgetsDto BuildStrictPerformanceBudgets(RuntimeGovernanceOptions options)
        => new(
            MaxPassDurationMs: TightenBudget(options.MaxWarmupPassDurationMs),
            MaxQdrantCheckMs: TightenBudget(options.MaxQdrantCheckMs),
            MaxEmbeddingsCheckMs: TightenBudget(options.MaxEmbeddingsCheckMs),
            MaxRerankCheckMs: TightenBudget(options.MaxRerankCheckMs));

    private static long TightenBudget(long value)
        => Math.Max(1, (long)Math.Floor(value * 0.75d));
}
