using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;

internal static class DoclingTableRegionObservationStage
{
    internal static IngestionStageManifest? Create(DocumentIntelligenceOptions options, DoclingConvertResponse response)
    {
        if (!options.RegionalTableAlternativeReadingsEnabled) return null;
        DoclingTableRegionObservationPolicy.Validate(options, response);
        var source = response.Document.JsonContent!;
        var work = source.TableRegionObservations!;
        var projection = source.TableObservationProjection;
        return new()
        {
            StageId = "regional_table_secondary_observations",
            StageType = "secondary_source_observation",
            Engine = "RapidOCR",
            EngineVersion = ObservedEngineVersion(work.Observations),
            ModelId = null,
            ModelRevision = null,
            DeviceId = "cpu:0",
            Concurrency = 1,
            OptionsSha256 = CanonicalContractJson.ComputeSha256(new
            {
                options.RegionalTableAlternativeReadingsEnabled,
                observedSettings = DoclingTableRegionObservationPolicy.SafeSettings(work.Settings)
            }),
            DurationMs = response.TableRepairSummaryJson is { } summary
                ? DoclingRegionalTableRepairPolicy.Count(summary, "duration_ms", 60_000) : null,
            Attributes = new(StringComparer.Ordinal)
            {
                ["availability"] = work.Availability,
                ["sourceTableCount"] = Count(work.SourceTableCount),
                ["inspectedTableCount"] = Count(work.InspectedTableCount),
                ["uninspectedTableCount"] = Count(work.UninspectedTableCount),
                ["sidecarAttemptedRegionCount"] = Count(work.AttemptedRegionCount),
                ["sidecarCompletedObservationCount"] = Count(work.CompletedObservationCount),
                ["admittedObservationCount"] = Count(projection?.AdmittedObservations),
                ["projectionRefusedObservationCount"] = Count(projection?.RefusedObservations),
                ["admittedRawBytes"] = Count(projection?.AdmittedRawBytes),
                ["admittedCanonicalBytesBeforeVectorEnrichment"] = Count(projection?.AdmittedCanonicalBytes),
                ["observedSettings"] = JsonSerializer.Serialize(DoclingTableRegionObservationPolicy.SafeSettings(work.Settings)),
                ["selectionPolicy"] = "sparse_then_descending_table_tail_v1",
                ["budgetScope"] = "shared_regional_table_work",
                ["modelIdentityScope"] = "per_observation_not_global_ocr_attestation",
                ["primaryProjection"] = "false",
                ["semanticAccuracy"] = "not_verified",
                ["cellMembership"] = "not_proven",
                ["semanticParentLinks"] = "not_asserted"
            }
        };
    }

    internal static void RecordDiagnostics(BackendDiagnostics.Operation operation,
        DocumentIntelligenceOptions options, DoclingConvertResponse response)
    {
        if (!options.RegionalTableAlternativeReadingsEnabled) return;
        var work = response.Document.JsonContent?.TableRegionObservations;
        operation.Set("table_observation_availability", work?.Availability ?? "unavailable");
        operation.Set("table_observation_attempted", work?.AttemptedRegionCount);
        operation.Set("table_observation_completed", work?.CompletedObservationCount);
        operation.Set("table_observation_uninspected", work?.UninspectedTableCount);
        operation.Set("table_observation_primary_projection", false);
    }

    private static string Count(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private static string ObservedEngineVersion(IReadOnlyList<DoclingTableRegionObservation>? observations)
    {
        if (observations is not { Count: > 0 and <= 8 }) return "unattested";
        string? version = null;
        foreach (var observation in observations)
        {
            if (observation.RawObservationJson.Length > 2_000_000) return "unattested";
            try
            {
                using var raw = JsonDocument.Parse(observation.RawObservationJson, new() { MaxDepth = 64 });
                var observed = DoclingTableRegionObservationPolicy.Text(raw.RootElement, "engine_version");
                if (observed is not { Length: > 0 and <= 256 }
                    || !Regex.IsMatch(observed, "^[A-Za-z0-9_.+-]+$", RegexOptions.CultureInvariant)
                    || version is not null && version != observed) return "unattested";
                version = observed;
            }
            catch (JsonException) { return "unattested"; }
        }
        return version ?? "unattested";
    }
}
