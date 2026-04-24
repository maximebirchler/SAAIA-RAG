using System.Diagnostics;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal sealed class RuntimeDiagnosticsService(NpgsqlDataSource ds, IHostEnvironment env)
{
    private const string CdcAlignment = "v3.1";
    private const string DiagnosticsArtifact = "runtime_diagnostics";
    private const string DiagnosticsJsonArtifact = "diagnostics.json";
    private const string OperationalSummaryArtifact = "runtime_operational_summary";
    private const string OperationalSummaryJsonArtifact = "operational_summary.json";
    private const string CapabilityACorpusEnrichmentKey = "capability_a.corpus_enrichment";
    private const string CapabilityBBackofficeGenerationKey = "capability_b.backoffice_generation";
    private readonly IHttpClientFactory? _httpFactory = null;

    internal RuntimeDiagnosticsService(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        IHttpClientFactory? httpFactory)
        : this(ds, env)
    {
        _httpFactory = httpFactory;
    }

    internal async Task<AdminRuntimeDiagnosticsResponseDto> GetDiagnosticsAsync(
        Guid tenantId,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(DiagnosticsArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var persisted = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);
            var capabilityAOperationalSummary = await RuntimeCapabilityDiagnosticsBuilder.LoadCapabilityAOperationalSummaryAsync(
                conn,
                tenantId,
                CapabilityACorpusEnrichmentKey,
                ct);
            var capabilityBCandidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesForDiagnosticsAsync(
                conn,
                options,
                ct);
            var capabilityBOperationalSummary = await RuntimeCapabilityDiagnosticsBuilder.LoadCapabilityBOperationalSummaryAsync(
                conn,
                CapabilityBBackofficeGenerationKey,
                capabilityBCandidates,
                ct);
            var items = RuntimeCapabilityRegistry.Definitions
                .Select(def => RuntimeCapabilityStateResolver.ResolveState(
                    def,
                    persisted.TryGetValue(def.Key, out var row) ? row : null,
                    options,
                    rag))
                .Select(state => RuntimeCapabilityDiagnosticsBuilder.BuildCapabilityDiagnostic(
                    state,
                    CapabilityACorpusEnrichmentKey,
                    CapabilityBBackofficeGenerationKey,
                    string.Equals(state.Key, CapabilityACorpusEnrichmentKey, StringComparison.Ordinal)
                        ? capabilityAOperationalSummary
                        : string.Equals(state.Key, CapabilityBBackofficeGenerationKey, StringComparison.Ordinal)
                            ? capabilityBOperationalSummary
                            : null))
                .ToArray();
            items = await AnnotateCapabilityBLiveRuntimeAvailabilityAsync(items, ct);
            var summary = RuntimeCapabilityDiagnosticsBuilder.BuildDiagnosticsSummary(
                items,
                CapabilityACorpusEnrichmentKey,
                CapabilityBBackofficeGenerationKey);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, DiagnosticsArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeDiagnosticsResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                DateTimeOffset.UtcNow,
                summary,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, DiagnosticsArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal async Task<AdminRuntimeDiagnosticsArtifactDto> GetDiagnosticsArtifactAsync(
        Guid tenantId,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        CancellationToken ct)
        => await ExecuteArtifactReadAsync(
            DiagnosticsJsonArtifact,
            async () =>
            {
                var diagnostics = await GetDiagnosticsAsync(tenantId, options, rag, ct);
                return new AdminRuntimeDiagnosticsArtifactDto(
                    DiagnosticsJsonArtifact,
                    diagnostics.CdcAlignment,
                    diagnostics.Environment,
                    diagnostics.GeneratedAt,
                    diagnostics.Summary,
                    diagnostics.Items);
            });

    internal async Task<AdminRuntimeOperationalSummaryResponseDto> GetOperationalSummaryAsync(
        Guid tenantId,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(OperationalSummaryArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var diagnostics = await GetDiagnosticsAsync(tenantId, options, rag, ct);
            var summary = diagnostics.Summary.Operational ?? new AdminRuntimeDiagnosticsOperationalSummaryDto(
                CapabilityACandidateCount: 0,
                CapabilityAReadyToEnqueueCount: 0,
                CapabilityAOffsetBackfillCandidateCount: 0,
                CapabilityBBacklogCount: 0,
                CapabilityBReadyToEnqueueCount: 0,
                CapabilityBActiveJobCount: 0,
                CapabilityBLatestCampaignProgressPercent: null);
            var items = RuntimeCapabilityDiagnosticsBuilder.BuildOperationalItems(diagnostics.Items);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, OperationalSummaryArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeOperationalSummaryResponseDto(
                diagnostics.CdcAlignment,
                diagnostics.Environment,
                diagnostics.GeneratedAt,
                summary,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, OperationalSummaryArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal async Task<AdminRuntimeOperationalSummaryArtifactDto> GetOperationalSummaryArtifactAsync(
        Guid tenantId,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        CancellationToken ct)
        => await ExecuteArtifactReadAsync(
            OperationalSummaryJsonArtifact,
            async () =>
            {
                var operational = await GetOperationalSummaryAsync(tenantId, options, rag, ct);
                return new AdminRuntimeOperationalSummaryArtifactDto(
                    OperationalSummaryJsonArtifact,
                    operational.CdcAlignment,
                    operational.Environment,
                    operational.GeneratedAt,
                    operational.Summary,
                    operational.Items);
            });

    private static async Task<T> ExecuteArtifactReadAsync<T>(string artifactName, Func<Task<T>> build)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(artifactName);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await build();
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

    private async Task<AdminRuntimeCapabilityDiagnosticDto[]> AnnotateCapabilityBLiveRuntimeAvailabilityAsync(
        AdminRuntimeCapabilityDiagnosticDto[] items,
        CancellationToken ct)
    {
        var capabilityBIndex = Array.FindIndex(items, item => string.Equals(item.Key, CapabilityBBackofficeGenerationKey, StringComparison.Ordinal));
        if (capabilityBIndex < 0)
            return items;

        var capabilityB = items[capabilityBIndex];
        if (!capabilityB.Implemented || !capabilityB.Selected || capabilityB.Stale)
            return items;

        var probe = await CapabilityBLiveRuntimeProbe.ProbeAsync(_httpFactory, ct);
        if (probe.Available)
            return items;

        var blockers = capabilityB.Blockers
            .Concat(["runtime_live_unavailable"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var recommendations = capabilityB.Recommendations
            .Concat(["live capability B runtime probe failed; server backoffice will fall back to client_admin until the LLM runtime recovers"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        items[capabilityBIndex] = capabilityB with
        {
            Blockers = blockers,
            Recommendations = recommendations,
            LastError = capabilityB.LastError ?? probe.Error
        };

        return items;
    }
}
