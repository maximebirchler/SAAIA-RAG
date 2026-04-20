using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityStateResolver
{
    internal static AdminRuntimeCapabilityStateDto ResolveState(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeCapabilityStateDto? persistedState,
        RuntimeGovernanceOptions options,
        RagOptions rag)
        => persistedState is null
            ? BuildDefaultState(definition, options, rag)
            : RuntimeCapabilityStateProjector.ProjectState(
                definition.Key,
                definition.Implemented,
                persistedState,
                options,
                rag);

    internal static AdminRuntimeCapabilityStateDto BuildDefaultState(
        RuntimeCapabilityDefinition definition,
        RuntimeGovernanceOptions options,
        RagOptions rag)
    {
        if (definition.Key == "capability_a.corpus_enrichment")
        {
            var profile = RuntimeCatalogBuilder.ResolveProfile(options.DefaultProfileKey, options);
            return new AdminRuntimeCapabilityStateDto(
                Key: definition.Key,
                DisplayName: definition.DisplayName,
                Family: definition.Family,
                RuntimeKey: definition.RuntimeKey,
                Implemented: true,
                DesiredEnabled: definition.DefaultDesiredEnabled,
                Installed: true,
                Configured: !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
                    && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
                    && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl)
                    && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel),
                Healthy: false,
                Qualified: false,
                Authorized: false,
                Selected: false,
                ProfileKey: profile.Key,
                PassCount: 0,
                LastCheckedAt: null,
                LastQualifiedAt: null,
                LastError: null,
                Details: new Dictionary<string, object?>
                {
                    ["status"] = "candidate",
                    ["mode"] = "corpus_enrichment_admin"
                },
                Stale: false,
                QualificationFingerprint: null,
                StaleReason: null,
                QualificationAgeHours: null,
                QualificationExpiresAt: null,
                PersistedAuthorized: false,
                PersistedSelected: false,
                EffectiveAuthorized: false,
                EffectiveSelected: false);
        }

        if (definition.Key == "capability_b.backoffice_generation")
        {
            var profile = RuntimeCatalogBuilder.ResolveProfile(options.DefaultProfileKey, options);
            var backofficeEnabled = RuntimeCatalogBuilder.IsBackofficeGenerationEnabled();
            return new AdminRuntimeCapabilityStateDto(
                Key: definition.Key,
                DisplayName: definition.DisplayName,
                Family: definition.Family,
                RuntimeKey: definition.RuntimeKey,
                Implemented: true,
                DesiredEnabled: definition.DefaultDesiredEnabled,
                Installed: true,
                Configured: backofficeEnabled,
                Healthy: false,
                Qualified: false,
                Authorized: false,
                Selected: false,
                ProfileKey: profile.Key,
                PassCount: 0,
                LastCheckedAt: null,
                LastQualifiedAt: null,
                LastError: null,
                Details: new Dictionary<string, object?>
                {
                    ["status"] = backofficeEnabled ? "configured_not_checked" : "backoffice_disabled",
                    ["mode"] = "summary_generation_admin",
                    ["backofficeEnabled"] = backofficeEnabled
                },
                Stale: false,
                QualificationFingerprint: null,
                StaleReason: null,
                QualificationAgeHours: null,
                QualificationExpiresAt: null,
                PersistedAuthorized: false,
                PersistedSelected: false,
                EffectiveAuthorized: false,
                EffectiveSelected: false);
        }

        if (definition.Key == "core.retrieval")
        {
            var installed = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
                && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl);
            var configured = installed
                && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
                && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel);
            var profile = RuntimeCatalogBuilder.ResolveProfile(options.DefaultProfileKey, options);

            return new AdminRuntimeCapabilityStateDto(
                Key: definition.Key,
                DisplayName: definition.DisplayName,
                Family: definition.Family,
                RuntimeKey: definition.RuntimeKey,
                Implemented: true,
                DesiredEnabled: true,
                Installed: installed,
                Configured: configured,
                Healthy: false,
                Qualified: false,
                Authorized: false,
                Selected: false,
                ProfileKey: options.DefaultProfileKey,
                PassCount: options.WarmupPassCount,
                LastCheckedAt: null,
                LastQualifiedAt: null,
                LastError: null,
                Details: new Dictionary<string, object?>
                {
                    ["status"] = configured ? "configured_not_checked" : installed ? "installed_not_checked" : "missing_dependencies",
                    ["qdrantBaseUrl"] = rag.QdrantBaseUrl,
                    ["embeddingsBaseUrl"] = rag.EmbeddingsBaseUrl,
                    ["freshnessPolicy"] = profile.FreshnessPolicy
                },
                Stale: false,
                QualificationFingerprint: null,
                StaleReason: null,
                QualificationAgeHours: null,
                QualificationExpiresAt: null,
                PersistedAuthorized: false,
                PersistedSelected: false,
                EffectiveAuthorized: false,
                EffectiveSelected: false);
        }

        return new AdminRuntimeCapabilityStateDto(
            Key: definition.Key,
            DisplayName: definition.DisplayName,
            Family: definition.Family,
            RuntimeKey: definition.RuntimeKey,
            Implemented: false,
            DesiredEnabled: definition.DefaultDesiredEnabled,
            Installed: false,
            Configured: false,
            Healthy: false,
            Qualified: false,
            Authorized: false,
            Selected: false,
            ProfileKey: options.DefaultProfileKey,
            PassCount: 0,
            LastCheckedAt: null,
            LastQualifiedAt: null,
            LastError: null,
            Details: new Dictionary<string, object?>
            {
                ["status"] = "not_implemented",
                ["note"] = definition.StatusNote
            },
            Stale: false,
            QualificationFingerprint: null,
            StaleReason: null,
            QualificationAgeHours: null,
            QualificationExpiresAt: null,
            PersistedAuthorized: false,
            PersistedSelected: false,
            EffectiveAuthorized: false,
            EffectiveSelected: false);
    }
}
