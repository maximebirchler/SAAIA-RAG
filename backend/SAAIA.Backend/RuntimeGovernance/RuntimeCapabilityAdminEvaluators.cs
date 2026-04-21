using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityAdminEvaluators
{
    internal static Task<CapabilityEvaluation> EvaluateCapabilityAAsync(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeWarmupProfileDto profile,
        AdminRuntimeCapabilityStateDto? existingState,
        bool? selectWhenQualified,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        string capabilityKey,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var installed = true;
        var configured = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
            && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel);
        var healthy = configured;
        var qualified = configured;
        var lastCheckedAt = DateTimeOffset.UtcNow;
        var selectionRequested = selectWhenQualified == true;
        var desiredEnabled = existingState?.DesiredEnabled ?? definition.DefaultDesiredEnabled;
        if (selectionRequested)
            desiredEnabled = true;
        var authorized = qualified && desiredEnabled && (existingState?.Authorized ?? selectionRequested);
        var selected = qualified && authorized && desiredEnabled && (existingState?.Selected ?? selectionRequested);
        var qualificationFingerprint = RuntimeCapabilityStateProjector.BuildQualificationFingerprint(definition.Key, profile, options, rag);

        var checks = new Dictionary<string, object?>
        {
            ["documentsCatalogAccessible"] = true,
            ["retrievalStackConfigured"] = configured,
            ["qdrantConfigured"] = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl) && !string.IsNullOrWhiteSpace(rag.QdrantCollection),
            ["embeddingsConfigured"] = !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl) && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel)
        };

        var details = new Dictionary<string, object?>
        {
            ["status"] = qualified ? "qualified" : "configured",
            ["mode"] = "corpus_enrichment_admin",
            ["passesRequired"] = 1,
            ["passesSucceeded"] = qualified ? 1 : 0,
            ["checks"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["pass"] = 1,
                    ["status"] = qualified ? "passed" : "failed",
                    ["check"] = capabilityKey,
                    ["measuredAt"] = lastCheckedAt,
                    ["details"] = checks
                }
            },
            ["qualificationFingerprint"] = qualificationFingerprint.Hash,
            ["qualificationFingerprintInputs"] = qualificationFingerprint.Inputs,
            ["qualificationFingerprintGeneratedAt"] = lastCheckedAt,
            ["qualificationFreshness"] = qualified ? "fresh" : "candidate",
            ["freshnessPolicy"] = profile.FreshnessPolicy,
            ["runtimeEnvironment"] = RuntimeGovernanceService.BuildRuntimeEnvironmentSnapshot(),
            ["capabilityContract"] = new Dictionary<string, object?>
            {
                ["adminOnly"] = true,
                ["planEndpoint"] = "/admin/runtime/capabilities/capability_a.corpus_enrichment/candidates",
                ["enqueueEndpoint"] = "/admin/runtime/capabilities/capability_a.corpus_enrichment/enqueue",
                ["executionMode"] = "reindex_existing_ingestion_pipeline"
            }
        };

        var state = new AdminRuntimeCapabilityStateDto(
            Key: definition.Key,
            DisplayName: definition.DisplayName,
            Family: definition.Family,
            RuntimeKey: definition.RuntimeKey,
            Implemented: true,
            DesiredEnabled: desiredEnabled,
            Installed: installed,
            Configured: configured,
            Healthy: healthy,
            Qualified: qualified,
            Authorized: authorized,
            Selected: selected,
            ProfileKey: profile.Key,
            PassCount: 1,
            LastCheckedAt: lastCheckedAt,
            LastQualifiedAt: qualified ? lastCheckedAt : null,
            LastError: qualified ? null : "capability A requires the retrieval stack to be configured before it can enqueue enrichment jobs",
            Details: details,
            Stale: false,
            QualificationFingerprint: qualificationFingerprint.Hash,
            StaleReason: null,
            QualificationAgeHours: 0d,
            QualificationExpiresAt: profile.FreshnessPolicy?.MaxQualificationAgeHours is long maxAgeHours
                ? lastCheckedAt.AddHours(maxAgeHours)
                : null,
            PersistedAuthorized: authorized,
            PersistedSelected: selected,
            EffectiveAuthorized: authorized,
            EffectiveSelected: selected);

        var warmupResult = new AdminRuntimeWarmupResultDto(
            WarmupResultId: Guid.NewGuid(),
            CapabilityKey: definition.Key,
            ProfileKey: profile.Key,
            PassCount: 1,
            Passed: qualified,
            MeasuredAt: lastCheckedAt,
            Details: details);

        return Task.FromResult(new CapabilityEvaluation(state, warmupResult));
    }

    internal static Task<CapabilityEvaluation> EvaluateCapabilityBAsync(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeWarmupProfileDto profile,
        AdminRuntimeCapabilityStateDto? existingState,
        bool? selectWhenQualified,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        string capabilityKey,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var backofficeEnabled = RuntimeCatalogBuilder.IsBackofficeGenerationEnabled();
        var installed = true;
        var configured = backofficeEnabled;
        var healthy = configured;
        var qualified = configured;
        var lastCheckedAt = DateTimeOffset.UtcNow;
        var selectionRequested = selectWhenQualified == true;
        var desiredEnabled = existingState?.DesiredEnabled ?? definition.DefaultDesiredEnabled;
        if (selectionRequested)
            desiredEnabled = true;
        var authorized = qualified && desiredEnabled && (existingState?.Authorized ?? selectionRequested);
        var selected = qualified && authorized && desiredEnabled && (existingState?.Selected ?? selectionRequested);
        var qualificationFingerprint = RuntimeCapabilityStateProjector.BuildQualificationFingerprint(definition.Key, profile, options, rag);

        var checks = new Dictionary<string, object?>
        {
            ["adminJobsAvailable"] = true,
            ["summaryEndpointsAvailable"] = true,
            ["backofficeEnabled"] = backofficeEnabled
        };

        var details = new Dictionary<string, object?>
        {
            ["status"] = qualified ? "qualified" : "configured",
            ["mode"] = "summary_generation_admin",
            ["passesRequired"] = 1,
            ["passesSucceeded"] = qualified ? 1 : 0,
            ["checks"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["pass"] = 1,
                    ["status"] = qualified ? "passed" : "failed",
                    ["check"] = capabilityKey,
                    ["measuredAt"] = lastCheckedAt,
                    ["details"] = checks
                }
            },
            ["qualificationFingerprint"] = qualificationFingerprint.Hash,
            ["qualificationFingerprintInputs"] = qualificationFingerprint.Inputs,
            ["qualificationFingerprintGeneratedAt"] = lastCheckedAt,
            ["qualificationFreshness"] = qualified ? "fresh" : "candidate",
            ["freshnessPolicy"] = profile.FreshnessPolicy,
            ["runtimeEnvironment"] = RuntimeGovernanceService.BuildRuntimeEnvironmentSnapshot(),
            ["capabilityContract"] = new Dictionary<string, object?>
            {
                ["adminOnly"] = true,
                ["planEndpoint"] = "/admin/runtime/capabilities/capability_b.backoffice_generation/candidates",
                ["enqueueEndpoint"] = "/admin/runtime/capabilities/capability_b.backoffice_generation/enqueue",
                ["executionMode"] = "server_backoffice_summary_jobs",
                ["requiresBackofficeLlmEnabled"] = true
            }
        };

        var state = new AdminRuntimeCapabilityStateDto(
            Key: definition.Key,
            DisplayName: definition.DisplayName,
            Family: definition.Family,
            RuntimeKey: definition.RuntimeKey,
            Implemented: true,
            DesiredEnabled: desiredEnabled,
            Installed: installed,
            Configured: configured,
            Healthy: healthy,
            Qualified: qualified,
            Authorized: authorized,
            Selected: selected,
            ProfileKey: profile.Key,
            PassCount: 1,
            LastCheckedAt: lastCheckedAt,
            LastQualifiedAt: qualified ? lastCheckedAt : null,
            LastError: qualified ? null : "capability B requires BACKOFFICE_LLM_ENABLED=true before it can enqueue server backoffice summary jobs",
            Details: details,
            Stale: false,
            QualificationFingerprint: qualificationFingerprint.Hash,
            StaleReason: null,
            QualificationAgeHours: 0d,
            QualificationExpiresAt: profile.FreshnessPolicy?.MaxQualificationAgeHours is long maxAgeHours
                ? lastCheckedAt.AddHours(maxAgeHours)
                : null,
            PersistedAuthorized: authorized,
            PersistedSelected: selected,
            EffectiveAuthorized: authorized,
            EffectiveSelected: selected);

        var warmupResult = new AdminRuntimeWarmupResultDto(
            WarmupResultId: Guid.NewGuid(),
            CapabilityKey: definition.Key,
            ProfileKey: profile.Key,
            PassCount: 1,
            Passed: qualified,
            MeasuredAt: lastCheckedAt,
            Details: details);

        return Task.FromResult(new CapabilityEvaluation(state, warmupResult));
    }
}
