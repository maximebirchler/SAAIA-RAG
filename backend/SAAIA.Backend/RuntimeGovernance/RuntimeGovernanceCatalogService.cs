using System.Diagnostics;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeGovernanceCatalogService
{
    private const string CdcAlignment = "v3.0";
    private const string RuntimeCatalogArtifact = "runtime_catalog.json";
    private const string ModelCatalogArtifact = "model_catalog.json";
    private const string WarmupProfilesArtifact = "warmup_profiles.json";
    private const string CapabilityACorpusEnrichmentKey = "capability_a.corpus_enrichment";
    private const string CapabilityBBackofficeGenerationKey = "capability_b.backoffice_generation";

    internal static AdminRuntimeCatalogResponseDto BuildCatalog(
        RuntimeGovernanceOptions options,
        RagOptions rag,
        ChatOptions chat,
        IHostEnvironment env)
    {
        var profiles = RuntimeCatalogBuilder.BuildWarmupProfiles(options);
        return new(
            CdcAlignment,
            env.EnvironmentName,
            RuntimeCatalogBuilder.BuildRuntimes(rag, chat, profiles, CapabilityACorpusEnrichmentKey, CapabilityBBackofficeGenerationKey),
            profiles,
            RuntimeCapabilityRegistry.GetCapabilityCatalog());
    }

    internal static AdminRuntimeRuntimeCatalogArtifactDto BuildRuntimeCatalogArtifact(
        RuntimeGovernanceOptions options,
        RagOptions rag,
        ChatOptions chat,
        IHostEnvironment env)
        => ExecuteArtifactRead(
            RuntimeCatalogArtifact,
            () =>
            {
                var profiles = RuntimeCatalogBuilder.BuildWarmupProfiles(options);
                return new AdminRuntimeRuntimeCatalogArtifactDto(
                    RuntimeCatalogArtifact,
                    CdcAlignment,
                    env.EnvironmentName,
                    DateTimeOffset.UtcNow,
                    RuntimeCatalogBuilder.BuildRuntimes(rag, chat, profiles, CapabilityACorpusEnrichmentKey, CapabilityBBackofficeGenerationKey),
                    profiles,
                    RuntimeCapabilityRegistry.GetCapabilityCatalog());
            });

    internal static AdminRuntimeModelCatalogArtifactDto BuildModelCatalogArtifact(
        RuntimeGovernanceOptions options,
        RagOptions rag,
        ChatOptions chat,
        IHostEnvironment env)
        => ExecuteArtifactRead(
            ModelCatalogArtifact,
            () =>
            {
                var profiles = RuntimeCatalogBuilder.BuildWarmupProfiles(options);
                var runtimes = RuntimeCatalogBuilder.BuildRuntimes(rag, chat, profiles, CapabilityACorpusEnrichmentKey, CapabilityBBackofficeGenerationKey);
                return new AdminRuntimeModelCatalogArtifactDto(
                    ModelCatalogArtifact,
                    CdcAlignment,
                    env.EnvironmentName,
                    DateTimeOffset.UtcNow,
                    RuntimeCatalogBuilder.BuildModelCatalogEntries(runtimes));
            });

    internal static AdminRuntimeWarmupProfilesArtifactDto BuildWarmupProfilesArtifact(
        RuntimeGovernanceOptions options,
        IHostEnvironment env)
        => ExecuteArtifactRead(
            WarmupProfilesArtifact,
            () => new AdminRuntimeWarmupProfilesArtifactDto(
                WarmupProfilesArtifact,
                CdcAlignment,
                env.EnvironmentName,
                DateTimeOffset.UtcNow,
                RuntimeCatalogBuilder.BuildWarmupProfiles(options)));

    private static T ExecuteArtifactRead<T>(string artifactName, Func<T> build)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(artifactName);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = build();
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, artifactName, success: true, durationMs: sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, artifactName, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }
}
