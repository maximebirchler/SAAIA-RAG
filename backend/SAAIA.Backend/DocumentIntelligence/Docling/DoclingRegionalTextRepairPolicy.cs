using System.Text.Json;
using SAAIA.Backend;
using static DoclingRegionalTextRepairJson;

internal sealed record DoclingRegionalTextRepairObservation(string Status, int Candidates, int Attempted,
    int Applied, int NotApplied, int Uninspected, long? DurationMs);

/// <summary>Signed admission settings and observed conversion work are kept separate and compared explicitly.</summary>
internal static class DoclingRegionalTextRepairPolicy
{
    internal static void Validate(DocumentIntelligenceOptions options, DoclingConvertResponse response)
    {
        ValidateOptions(options);
        var settings = response.Execution?.TextRepairSettings;
        if (settings is null)
        {
            if (options.RegionalTextRepairEnabled || HasRepairData(response)) throw Invalid("execution_settings_missing");
            return;
        }
        EqualString(settings.Value, "schema_version", "saaia_text_repair_settings_v1");
        if (Bool(settings.Value, "enabled") != options.RegionalTextRepairEnabled
            || Int(settings.Value, "max_regions") != options.RegionalTextRepairMaxRegions
            || Number(settings.Value, "total_timeout_seconds") != options.RegionalTextRepairTotalTimeoutSeconds
            || Number(settings.Value, "region_timeout_seconds") != options.RegionalTextRepairRegionTimeoutSeconds
            || Number(settings.Value, "render_scale") != options.RegionalTextRepairRenderScale
            || Number(settings.Value, "padding_points") != options.RegionalTextRepairPaddingPoints
            || Number(settings.Value, "minimum_rapid_score") != options.RegionalTextRepairMinimumRapidScore
            || Number(settings.Value, "minimum_secondary_score") != options.RegionalTextRepairMinimumSecondaryScore
            || Int(settings.Value, "max_rendered_pixels") != options.RegionalTextRepairMaxRenderedPixels)
            throw Invalid("execution_settings_mismatch");
        EqualString(settings.Value, "secondary_engine", "tesseract");
        EqualString(settings.Value, "secondary_language", "eng");
        if (Int(settings.Value, "secondary_page_segmentation_mode") != 6) throw Invalid("execution_settings");
        EqualString(settings.Value, "agreement_normalization", "unicode_nfc_and_whitespace_only");
        EqualString(settings.Value, "linguistic_prefix_negation_guard", "fr_en_de_finite_v1");
        EqualString(settings.Value, "process_isolation", "per_region_linux_process_group");
        if (Bool(settings.Value, "universal_multilingual_validation")) throw Invalid("execution_settings");
        if (!options.RegionalTextRepairEnabled)
        {
            if (HasRepairData(response)) throw Invalid("disabled_execution");
            return;
        }
        var work = Observe(response);
        if (work.Attempted > options.RegionalTextRepairMaxRegions || response.TextRepairSummary is not { } totals
            || Int(totals, "limit_max_regions") != options.RegionalTextRepairMaxRegions
            || Number(totals, "limit_total_timeout_seconds") != options.RegionalTextRepairTotalTimeoutSeconds)
            throw Invalid("execution_budget");
        foreach (var item in response.Document.JsonContent?.Texts ?? [])
        {
            if (item.RegionalTextRepair is not { } proof) continue;
            foreach (var observation in Array(proof, "observations", 2))
            {
                var minimum = String(observation, "engine") switch
                {
                    "rapidocr" => options.RegionalTextRepairMinimumRapidScore,
                    "tesseract" => options.RegionalTextRepairMinimumSecondaryScore,
                    _ => throw Invalid()
                };
                if (Number(observation, "minimum_confidence") < minimum) throw Invalid();
            }
        }
    }

    internal static DoclingRegionalTextRepairObservation Observe(DoclingConvertResponse response)
    {
        var settings = response.Execution?.TextRepairSettings;
        var enabled = settings is not null && Bool(settings.Value, "enabled");
        var status = settings is null ? "unattested" : enabled ? "enabled" : "disabled";
        if (!enabled) return new(status, 0, 0, 0, 0, 0, null);
        var items = response.Document.JsonContent?.Texts ?? throw Invalid("summary");
        if (response.TextRepairs.Count > items.Count + 1) throw Invalid("summary");
        var refs = new HashSet<string>(StringComparer.Ordinal);
        var candidates = 0; var attempted = 0; var applied = 0; var notApplied = 0; var uninspected = 0;
        foreach (var summary in response.TextRepairs)
        {
            EqualString(summary, "schema_version", Schema);
            var outcome = String(summary, "status");
            if (outcome is not ("applied" or "not_applied")) throw Invalid("summary");
            var reason = String(summary, "reason", 128);
            if (reason.Length == 0 || reason.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')) throw Invalid("summary");
            if (!summary.TryGetProperty("source_ref", out _))
            {
                if (uninspected > 0 || outcome != "not_applied" || reason != "document_time_budget_exhausted") throw Invalid("summary");
                uninspected = Int(summary, "uninspected_item_count");
                if (uninspected < 1 || uninspected > items.Count) throw Invalid("summary");
                continue;
            }
            var sourceRef = String(summary, "source_ref");
            var matching = items.Where(item => item.SelfRef == sourceRef).Take(2).ToArray();
            if (matching.Length != 1 || !refs.Add(sourceRef)) throw Invalid("summary");
            var item = matching[0];
            var wasAttempted = Bool(summary, "attempted");
            candidates++; if (wasAttempted) attempted++;
            if (outcome == "applied")
            {
                if (!wasAttempted || item.RegionalTextRepair is not { } appliedProof || item.RegionalTextRepairAttempt is not null
                    || String(appliedProof, "reason") != reason) throw Invalid("summary");
                applied++;
            }
            else
            {
                if (item.RegionalTextRepair is not null || item.RegionalTextRepairAttempt is not { } attempt
                    || Bool(attempt, "attempted") != wasAttempted || String(attempt, "reason") != reason) throw Invalid("summary");
                notApplied++;
            }
        }
        if (items.Any(item => (item.RegionalTextRepair is not null || item.RegionalTextRepairAttempt is not null) && !refs.Contains(item.SelfRef)))
            throw Invalid("summary");
        var totals = response.TextRepairSummary ?? throw Invalid("summary");
        EqualString(totals, "schema_version", Schema);
        var duration = Int(totals, "duration_ms");
        if (duration < 0 || Int(totals, "candidate_count") != candidates || Int(totals, "attempted_count") != attempted
            || Int(totals, "applied_count") != applied) throw Invalid("summary");
        return new(status, candidates, attempted, applied, notApplied, uninspected, duration);
    }

    internal static void RecordDiagnostics(BackendDiagnostics.Operation operation, DoclingConvertResponse response)
    {
        var work = Observe(response);
        operation.Set("regional_text_repair_status", work.Status);
        operation.Set("regional_text_repair_candidates", work.Candidates);
        operation.Set("regional_text_repair_attempted", work.Attempted);
        operation.Set("regional_text_repair_applied", work.Applied);
        operation.Set("regional_text_repair_not_applied", work.NotApplied);
        operation.Set("regional_text_repair_uninspected", work.Uninspected);
        operation.Set("regional_text_repair_duration_ms", work.DurationMs);
    }

    private static bool HasRepairData(DoclingConvertResponse response)
        => response.TextRepairs.Count > 0 || response.TextRepairSummary is not null
            || response.Document.JsonContent?.Texts.Any(item => item.RegionalTextRepair is not null || item.RegionalTextRepairAttempt is not null) == true;

    private static void ValidateOptions(DocumentIntelligenceOptions options)
    {
        if (options.RegionalTextRepairMaxRegions is < 1 or > 4 || options.RegionalTextRepairMaxRenderedPixels is < 1 or > 20_000_000
            || !InRange(options.RegionalTextRepairTotalTimeoutSeconds, .1, 60)
            || !InRange(options.RegionalTextRepairRegionTimeoutSeconds, .1, 15)
            || !InRange(options.RegionalTextRepairRenderScale, 1, 4) || !InRange(options.RegionalTextRepairPaddingPoints, 0, 2)
            || !InRange(options.RegionalTextRepairMinimumRapidScore, .98, 1) || !InRange(options.RegionalTextRepairMinimumSecondaryScore, .95, 1))
            throw Invalid("configuration");
    }
    private static bool InRange(double value, double minimum, double maximum)
        => double.IsFinite(value) && value >= minimum && value <= maximum;
}
