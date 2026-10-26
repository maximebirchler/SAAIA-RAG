using System.Globalization;
using SAAIA.Contracts.DocumentIntelligence;

internal static class DoclingRegionalTableRepairStage
{
    internal static IngestionStageManifest? Create(DocumentIntelligenceOptions options, DoclingConvertResponse response)
    {
        if (response.TableRepairs.Count == 0 && !response.TableRepairSummariesObserved
            && response.TableRepairSummaryJson is null && response.Execution?.TableRepairSettingsJson is null
            && response.Execution?.TableRepairEnabled.HasValue != true)
            return null;
        var work = DoclingRegionalTableRepairPolicy.Observe(response);
        var settings = DoclingRegionalTableRepairMetadata.Settings(options, response);
        var models = DoclingRegionalTableRepairMetadata.Models(response);
        var legacySettings = new
        {
            enabled = response.Execution?.TableRepairEnabled,
            scale = response.Execution?.TableRepairScale,
            paddingPoints = response.Execution?.TableRepairPaddingPoints,
            minimumScore = response.Execution?.TableRepairMinimumScore
        };
        return new()
        {
            StageId = "regional_table_ocr_repair",
            StageType = "table_text_repair",
            Engine = "RapidOCR",
            EngineVersion = models.EngineVersion,
            // Observed weights belong to each region. A Docling deployment label
            // is neither an OCR weight revision nor a global model attestation.
            ModelId = null,
            ModelRevision = null,
            OptionsSha256 = CanonicalContractJson.ComputeSha256(new
            {
                options.RegionalTableRepairEnabled, options.RegionalTableRepairScale,
                options.RegionalTableRepairPaddingPoints, options.RegionalTableRepairMinimumScore,
                observedSettings = settings.Json, legacySettings,
                trigger = "sparse_non_overlapping_table_grid"
            }),
            DeviceId = "cpu:0",
            Concurrency = 1,
            DurationMs = work.DurationMs ?? LegacyTiming(response),
            Attributes = new(StringComparer.Ordinal)
            {
                ["deploymentRevision"] = options.DeploymentRevision,
                ["deploymentRevisionSource"] = "declared_configuration",
                ["repairSchema"] = "saaia_table_text_repair_v1",
                ["observedSummaryCount"] = Count(work.Summaries),
                ["candidateTableCount"] = Count(work.Candidates),
                ["observedAttemptCount"] = Count(work.Attempted),
                ["attemptCountAvailability"] = work.AttemptAvailability,
                ["appliedTableCount"] = Count(work.Applied),
                ["failedTableCount"] = Count(work.Failed),
                ["notAppliedTableCount"] = Count(work.NotApplied),
                ["uninspectedTableCount"] = Count(work.Uninspected),
                ["outcome"] = settings.Availability == "invalid" ? "degraded" : work.Outcome,
                ["configurationSource"] = settings.Availability == "unattested" ? "legacy_or_unattested" : "sidecar_response",
                ["configurationAvailability"] = settings.Availability,
                ["observedSettings"] = settings.Json,
                ["observedModelArtifacts"] = models.ArtifactsJson,
                ["observedRuntimeSettings"] = models.RuntimeJson,
                ["modelObservationAvailability"] = models.Availability,
                ["modelIdentityScope"] = "per_observation_provenance_not_global_model_attestation",
                ["inferenceScope"] = "regional_rapidocr_only",
                ["cancellationScope"] = "regional_process_group_not_upstream_docling",
                ["confidenceKind"] = "ocr_engine_line_scores_not_semantic_truth",
                ["semanticDecisionOwner"] = "llm_client"
            }
        };
    }

    private static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private static long? LegacyTiming(DoclingConvertResponse response)
    {
        if (!response.Timings.TryGetValue("saaia_table_repair", out var timing) || timing.Times.Count == 0)
            return null;
        var times = timing.Times.Where(value => double.IsFinite(value) && value >= 0).ToArray();
        var seconds = times.Sum();
        return times.Length == timing.Times.Count && double.IsFinite(seconds) && seconds <= 86_400
            ? (long)Math.Round(seconds * 1_000, MidpointRounding.AwayFromZero) : null;
    }
}
