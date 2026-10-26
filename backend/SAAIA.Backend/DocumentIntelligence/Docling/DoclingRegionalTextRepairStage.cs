using System.Globalization;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;

internal static class DoclingRegionalTextRepairStage
{
    internal static IngestionStageManifest? Create(DocumentIntelligenceOptions options, DoclingConvertResponse response)
    {
        if (response.Execution?.TextRepairSettings is null && response.TextRepairs.Count == 0) return null;
        var work = DoclingRegionalTextRepairPolicy.Observe(response);
        var observations = (response.Document.JsonContent?.Texts ?? [])
            .Select(item => item.RegionalTextRepair ?? item.RegionalTextRepairAttempt)
            .Where(value => value is not null).SelectMany(value => ReadObservations(value!.Value)).ToArray();
        var versions = observations.Select(value => Read(value, "engine") + "=" + Read(value, "engine_version"))
            .Where(value => !value.EndsWith('=')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var models = observations.Select(value => Read(value, "model_id")).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new()
        {
            StageId = "regional_text_ocr_repair",
            StageType = "text_region_repair",
            Engine = "RapidOCR+Tesseract",
            EngineVersion = versions.Length == 0 ? "unattested" : string.Join(';', versions),
            // A label without an observed immutable weight identity must not satisfy
            // the manifest's stronger ModelId/ModelRevision attestation contract.
            ModelId = null,
            OptionsSha256 = CanonicalContractJson.ComputeSha256(new
            {
                options.RegionalTextRepairEnabled, options.RegionalTextRepairMaxRegions,
                options.RegionalTextRepairTotalTimeoutSeconds, options.RegionalTextRepairRegionTimeoutSeconds,
                options.RegionalTextRepairRenderScale, options.RegionalTextRepairPaddingPoints,
                options.RegionalTextRepairMinimumRapidScore, options.RegionalTextRepairMinimumSecondaryScore,
                options.RegionalTextRepairMaxRenderedPixels,
                observedSettings = response.Execution?.TextRepairSettings
            }),
            DeviceId = "cpu:0", Concurrency = 1, DurationMs = work.DurationMs,
            Attributes = new(StringComparer.Ordinal)
            {
                ["configurationSource"] = response.Execution?.TextRepairSettings is null ? "unattested" : "sidecar_response",
                ["signedSettings"] = JsonSerializer.Serialize(new
                {
                    enabled = options.RegionalTextRepairEnabled, max_regions = options.RegionalTextRepairMaxRegions,
                    total_timeout_seconds = options.RegionalTextRepairTotalTimeoutSeconds,
                    region_timeout_seconds = options.RegionalTextRepairRegionTimeoutSeconds,
                    render_scale = options.RegionalTextRepairRenderScale, padding_points = options.RegionalTextRepairPaddingPoints,
                    minimum_rapid_score = options.RegionalTextRepairMinimumRapidScore,
                    minimum_secondary_score = options.RegionalTextRepairMinimumSecondaryScore,
                    max_rendered_pixels = options.RegionalTextRepairMaxRenderedPixels
                }),
                ["observedSettings"] = SettingsSnapshot(response.Execution?.TextRepairSettings),
                ["observedRegionSummaries"] = SummarySnapshot(response.TextRepairs),
                ["enabledStatus"] = work.Status,
                ["candidateCount"] = Count(work.Candidates), ["attemptedCount"] = Count(work.Attempted),
                ["appliedCount"] = Count(work.Applied), ["notAppliedCount"] = Count(work.NotApplied),
                ["uninspectedItemCount"] = Count(work.Uninspected),
                ["repairSchema"] = DoclingRegionalTextRepairJson.Schema,
                ["observedModelIds"] = string.Join(';', models),
                ["modelIdentityScope"] = "per_observation_provenance_not_global_model_attestation",
                ["agreementNormalization"] = "unicode_nfc_and_whitespace_only",
                ["sourceRetention"] = "raw_and_both_observations_preserved",
                ["reliableSuffixPolicy"] = "exact_contiguous_terminal_tokens",
                ["lowercaseTwoLetterPrefixWordsPreserved"] = "true",
                ["linguisticPrefixNegationGuard"] = "fr_en_de_finite_v1",
                ["universalMultilingualValidation"] = "false",
                ["confidenceKind"] = "independent_engine_line_scores_not_semantic_truth",
                ["outcome"] = work.Status != "enabled" ? work.Status
                    : work.NotApplied > 0 || work.Uninspected > 0 ? "inconclusive_or_bounded"
                    : work.Candidates == 0 ? "no_candidates_observed" : "completed"
            }
        };
    }

    private static IEnumerable<JsonElement> ReadObservations(JsonElement value)
        => value.TryGetProperty("observations", out var observations) && observations.ValueKind == JsonValueKind.Array
            ? observations.EnumerateArray().ToArray() : [];
    private static string Read(JsonElement value, string name)
        => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { Length: <= 256 } text
            ? text : "";
    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string SettingsSnapshot(JsonElement? value)
    {
        if (value is not { } settings) return "null";
        return JsonSerializer.Serialize(new
        {
            schema_version = DoclingRegionalTextRepairJson.String(settings, "schema_version"),
            enabled = DoclingRegionalTextRepairJson.Bool(settings, "enabled"),
            max_regions = DoclingRegionalTextRepairJson.Int(settings, "max_regions"),
            total_timeout_seconds = DoclingRegionalTextRepairJson.Number(settings, "total_timeout_seconds"),
            region_timeout_seconds = DoclingRegionalTextRepairJson.Number(settings, "region_timeout_seconds"),
            render_scale = DoclingRegionalTextRepairJson.Number(settings, "render_scale"),
            padding_points = DoclingRegionalTextRepairJson.Number(settings, "padding_points"),
            minimum_rapid_score = DoclingRegionalTextRepairJson.Number(settings, "minimum_rapid_score"),
            minimum_secondary_score = DoclingRegionalTextRepairJson.Number(settings, "minimum_secondary_score"),
            max_rendered_pixels = DoclingRegionalTextRepairJson.Int(settings, "max_rendered_pixels"),
            secondary_engine = DoclingRegionalTextRepairJson.String(settings, "secondary_engine"),
            secondary_language = DoclingRegionalTextRepairJson.String(settings, "secondary_language"),
            secondary_page_segmentation_mode = DoclingRegionalTextRepairJson.Int(settings, "secondary_page_segmentation_mode"),
            agreement_normalization = DoclingRegionalTextRepairJson.String(settings, "agreement_normalization"),
            linguistic_prefix_negation_guard = DoclingRegionalTextRepairJson.String(settings, "linguistic_prefix_negation_guard"),
            universal_multilingual_validation = DoclingRegionalTextRepairJson.Bool(settings, "universal_multilingual_validation"),
            process_isolation = DoclingRegionalTextRepairJson.String(settings, "process_isolation")
        });
    }

    private static string SummarySnapshot(IEnumerable<JsonElement> summaries)
        => JsonSerializer.Serialize(summaries.Select(value => new
        {
            schema_version = DoclingRegionalTextRepairJson.String(value, "schema_version"),
            source_ref = Read(value, "source_ref"), status = Read(value, "status"), reason = Read(value, "reason"),
            attempted = value.TryGetProperty("attempted", out _) ? DoclingRegionalTextRepairJson.Bool(value, "attempted") : (bool?)null,
            uninspected_item_count = value.TryGetProperty("uninspected_item_count", out _)
                ? DoclingRegionalTextRepairJson.Int(value, "uninspected_item_count") : (int?)null
        }));
}
