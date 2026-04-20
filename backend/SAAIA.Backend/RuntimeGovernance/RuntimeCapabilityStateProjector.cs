using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityStateProjector
{
    private const string CoreRetrievalCapabilityKey = "core.retrieval";
    private const string CapabilityACorpusEnrichmentKey = "capability_a.corpus_enrichment";
    private const string CapabilityBBackofficeGenerationKey = "capability_b.backoffice_generation";

    internal static AdminRuntimeCapabilityStateDto ProjectState(
        string capabilityKey,
        bool implemented,
        AdminRuntimeCapabilityStateDto state,
        RuntimeGovernanceOptions options,
        RagOptions rag)
    {
        if (!implemented || (!string.Equals(capabilityKey, CoreRetrievalCapabilityKey, StringComparison.Ordinal)
                             && !string.Equals(capabilityKey, CapabilityACorpusEnrichmentKey, StringComparison.Ordinal)
                             && !string.Equals(capabilityKey, CapabilityBBackofficeGenerationKey, StringComparison.Ordinal)))
        {
            return state with
            {
                Stale = false,
                QualificationFingerprint = ResolveQualificationFingerprint(state.Details),
                StaleReason = null,
                QualificationAgeHours = null,
                QualificationExpiresAt = null,
                PersistedAuthorized = state.PersistedAuthorized,
                PersistedSelected = state.PersistedSelected,
                EffectiveAuthorized = state.PersistedAuthorized,
                EffectiveSelected = state.PersistedSelected
            };
        }

        var profile = RuntimeCatalogBuilder.ResolveProfile(state.ProfileKey, options);
        var currentFingerprint = BuildQualificationFingerprint(capabilityKey, profile, options, rag);
        var storedFingerprint = ResolveQualificationFingerprint(state.Details);
        var staleReason = ResolveStaleQualificationReason(state, profile, storedFingerprint, currentFingerprint.Hash, out var freshness);
        var stale = staleReason is not null;

        var details = new Dictionary<string, object?>(state.Details ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            ["qualificationFingerprintCurrent"] = currentFingerprint.Hash,
            ["qualificationFreshness"] = stale ? "stale" : "fresh",
            ["freshnessPolicy"] = profile.FreshnessPolicy,
            ["qualificationAgeHours"] = freshness.QualificationAgeHours,
            ["qualificationExpiresAt"] = freshness.ExpiresAt
        };

        if (stale && staleReason is not null)
        {
            details["qualificationFingerprintStored"] = storedFingerprint;
            details["staleQualificationReason"] = staleReason;
            details["staleQualificationDetectedAt"] = DateTimeOffset.UtcNow;
            details["qualificationFingerprintInputsCurrent"] = currentFingerprint.Inputs;
            details["persistedAuthorized"] = state.Authorized;
            details["persistedSelected"] = state.Selected;
            RuntimeGovernanceTelemetry.RecordStaleQualificationDetected(capabilityKey, profile.Key, staleReason);
        }
        else if (storedFingerprint is not null)
        {
            details["qualificationFingerprintStored"] = storedFingerprint;
        }

        return state with
        {
            Details = details,
            Stale = stale,
            StaleReason = staleReason,
            QualificationAgeHours = freshness.QualificationAgeHours,
            QualificationExpiresAt = freshness.ExpiresAt,
            Authorized = stale ? false : state.Authorized,
            Selected = stale ? false : state.Selected,
            QualificationFingerprint = storedFingerprint ?? currentFingerprint.Hash,
            PersistedAuthorized = state.PersistedAuthorized,
            PersistedSelected = state.PersistedSelected,
            EffectiveAuthorized = stale ? false : state.PersistedAuthorized,
            EffectiveSelected = stale ? false : state.PersistedSelected
        };
    }

    internal static string? ResolveQualificationFingerprint(IReadOnlyDictionary<string, object?>? details)
        => details is not null
           && details.TryGetValue("qualificationFingerprint", out var value)
           && value is string fingerprint
            ? fingerprint
            : null;

    internal static (string Hash, IReadOnlyDictionary<string, object?> Inputs) BuildQualificationFingerprint(
        string capabilityKey,
        AdminRuntimeWarmupProfileDto profile,
        RuntimeGovernanceOptions options,
        RagOptions rag)
    {
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capabilityKey"] = capabilityKey,
            ["profile"] = new Dictionary<string, object?>
            {
                ["key"] = profile.Key,
                ["passCount"] = profile.PassCount,
                ["checks"] = profile.Checks.ToArray(),
                ["hardwareRequirements"] = profile.HardwareRequirements,
                ["performanceBudgets"] = profile.PerformanceBudgets,
                ["checkPolicy"] = profile.CheckPolicy,
                ["runtimeRequirements"] = profile.RuntimeRequirements
            },
            ["rag"] = new Dictionary<string, object?>
            {
                ["qdrantBaseUrl"] = rag.QdrantBaseUrl,
                ["qdrantCollection"] = rag.QdrantCollection,
                ["embeddingsBaseUrl"] = rag.EmbeddingsBaseUrl,
                ["embeddingsModel"] = rag.EmbeddingsModel,
                ["enableRerank"] = rag.EnableRerank,
                ["rerankBaseUrl"] = rag.RerankBaseUrl,
                ["rerankBaseUrlEffective"] = string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl,
                ["rerankModel"] = rag.RerankModel
            },
            ["runtimeGates"] = new Dictionary<string, object?>
            {
                ["qdrantMinCpuCores"] = options.QdrantRuntimeMinCpuCores,
                ["qdrantMinAvailableMemoryMb"] = options.QdrantRuntimeMinAvailableMemoryMb,
                ["embeddingsMinCpuCores"] = options.EmbeddingsRuntimeMinCpuCores,
                ["embeddingsMinAvailableMemoryMb"] = options.EmbeddingsRuntimeMinAvailableMemoryMb,
                ["rerankMinCpuCores"] = options.RerankRuntimeMinCpuCores,
                ["rerankMinAvailableMemoryMb"] = options.RerankRuntimeMinAvailableMemoryMb
            }
        };

        if (string.Equals(capabilityKey, CapabilityACorpusEnrichmentKey, StringComparison.Ordinal))
        {
            inputs["capabilityA"] = new Dictionary<string, object?>
            {
                ["mode"] = "corpus_enrichment_admin",
                ["requiresRetrievalStack"] = true,
                ["planEndpoint"] = "/admin/runtime/capabilities/capability_a.corpus_enrichment/candidates",
                ["enqueueEndpoint"] = "/admin/runtime/capabilities/capability_a.corpus_enrichment/enqueue"
            };
        }

        if (string.Equals(capabilityKey, CapabilityBBackofficeGenerationKey, StringComparison.Ordinal))
        {
            inputs["capabilityB"] = new Dictionary<string, object?>
            {
                ["mode"] = "summary_generation_admin",
                ["backofficeEnabled"] = RuntimeCatalogBuilder.IsBackofficeGenerationEnabled(),
                ["planEndpoint"] = "/admin/runtime/capabilities/capability_b.backoffice_generation/candidates",
                ["enqueueEndpoint"] = "/admin/runtime/capabilities/capability_b.backoffice_generation/enqueue",
                ["executionMode"] = "server_backoffice_summary_jobs"
            };
        }

        var json = JsonSerializer.Serialize(inputs);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return (hash, inputs);
    }

    private static string? ResolveStaleQualificationReason(
        AdminRuntimeCapabilityStateDto state,
        AdminRuntimeWarmupProfileDto profile,
        string? storedFingerprint,
        string currentFingerprint,
        out QualificationFreshness freshness)
    {
        freshness = EvaluateQualificationFreshness(state, profile);
        var shouldEvaluateFreshness = state.Qualified || state.Authorized || state.Selected || state.LastQualifiedAt is not null;
        if (!shouldEvaluateFreshness)
            return null;

        if (freshness.IsExpired && profile.FreshnessPolicy?.RequireRequalificationWhenExpired != false)
            return "qualification_expired";

        if (string.IsNullOrWhiteSpace(storedFingerprint))
            return "missing_qualification_fingerprint";

        return string.Equals(storedFingerprint, currentFingerprint, StringComparison.Ordinal)
            ? null
            : "qualification_inputs_changed";
    }

    private static QualificationFreshness EvaluateQualificationFreshness(
        AdminRuntimeCapabilityStateDto state,
        AdminRuntimeWarmupProfileDto profile)
    {
        var policy = profile.FreshnessPolicy;
        if (policy?.MaxQualificationAgeHours is not long maxAgeHours || state.LastQualifiedAt is null)
            return new QualificationFreshness(false, null, null);

        var qualifiedAt = state.LastQualifiedAt.Value;
        var expiresAt = qualifiedAt.AddHours(maxAgeHours);
        var ageHours = (DateTimeOffset.UtcNow - qualifiedAt).TotalHours;
        return new QualificationFreshness(DateTimeOffset.UtcNow > expiresAt, ageHours, expiresAt);
    }
    private sealed record QualificationFreshness(
        bool IsExpired,
        double? QualificationAgeHours,
        DateTimeOffset? ExpiresAt);
}
