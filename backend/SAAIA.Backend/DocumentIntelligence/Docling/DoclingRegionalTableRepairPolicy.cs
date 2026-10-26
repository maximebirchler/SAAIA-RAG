using System.Text.Json;
using System.Globalization;
using SAAIA.Backend;

internal sealed record DoclingRegionalTableRepairWork(
    int? Summaries, int? Candidates, int? Attempted, int? Applied, int? Failed, int? NotApplied,
    int? Uninspected, long? DurationMs, string Outcome, string AttemptAvailability);

/// <summary>Counts observed work, without treating absent legacy diagnostics as clean execution.</summary>
internal static class DoclingRegionalTableRepairPolicy
{
    internal static DoclingRegionalTableRepairWork Observe(DoclingConvertResponse response)
    {
        var rows = response.TableRepairs;
        var observed = response.TableRepairSummariesObserved || rows.Count > 0;
        if (rows.Count > 1_025)
            return new(null, null, null, null, null, null, null, null, "degraded", "invalid");
        var regions = rows.Where(row => !string.IsNullOrEmpty(row.TableRef)).ToArray();
        var aggregates = rows.Where(row => string.IsNullOrEmpty(row.TableRef)).ToArray();
        var validRefs = SourceReferencesAreValid(response)
            && regions.All(row => ResolvesSourceReference(response, row.TableRef))
            && regions.Select(row => row.TableRef).Distinct(StringComparer.Ordinal).Count() == regions.Length;
        var invalid = !validRefs || aggregates.Length > 1
            || rows.Any(row => row.SchemaVersion is not ("" or "saaia_table_text_repair_v1"))
            || rows.Any(row => row.Status is not ("applied" or "not_applied" or "error"))
            || aggregates.Any(row => row.UninspectedTableCount is not > 0 or > 1_000_000 || row.Attempted is not null
                || row.Status != "not_applied" || row.Reason != "document_time_or_scan_budget_exhausted");
        int? summaries = observed ? rows.Count : null;
        int? candidates = observed ? regions.Length : null;
        int? attempts = regions.Length > 0 && regions.All(row => row.Attempted.HasValue)
            ? regions.Count(row => row.Attempted == true) : null;
        int? applied = observed ? regions.Count(row => row.Status == "applied") : null;
        int? failed = observed ? regions.Count(row => row.Status == "error") : null;
        int? notApplied = observed ? regions.Count(row => row.Status == "not_applied") : null;
        int? uninspected = !invalid && aggregates.Length == 1 ? aggregates[0].UninspectedTableCount : null;
        long? duration = null;
        var completeZero = false;
        if (response.TableRepairSummaryJson is { } total)
        {
            var inspectedTotal = Count(total, "inspected_table_count");
            var candidateTotal = Count(total, "candidate_count");
            var attemptTotal = Count(total, "attempted_count");
            var appliedTotal = Count(total, "applied_count");
            var remainingTotal = Count(total, "uninspected_table_count");
            duration = Count(total, "duration_ms", 86_400_000);
            invalid |= inspectedTotal is null || candidateTotal is null || attemptTotal is null
                || appliedTotal is null || remainingTotal is null || duration is null
                || String(total, "schema_version") != "saaia_table_text_repair_v1"
                || inspectedTotal + remainingTotal != response.Document.JsonContent?.Tables.Count
                || candidateTotal > inspectedTotal || attemptTotal > candidateTotal || appliedTotal > attemptTotal
                || observed && (candidateTotal != candidates || appliedTotal != applied)
                || attempts.HasValue && attemptTotal != attempts
                || uninspected.HasValue && uninspected != remainingTotal;
            uninspected = invalid ? null : remainingTotal;
            completeZero = !invalid && observed && rows.Count == 0 && candidateTotal == 0 && attemptTotal == 0
                && appliedTotal == 0 && remainingTotal == 0
                && inspectedTotal == response.Document.JsonContent?.Tables.Count;
            if (completeZero) attempts = 0;
        }
        var settings = response.Execution?.TableRepairSettingsJson;
        var maximumAttempts = settings is { } value ? Count(value, "max_regions", 8) : 8;
        invalid |= attempts > maximumAttempts || regions.Any(row => row.Status == "applied" && row.Attempted == false);
        // Preserve elapsed time verbatim, but never call an over-budget execution complete.
        invalid |= duration.HasValue && settings is { } budget
            && SecondsBudget(budget) is { } seconds && duration > seconds * 1_000;
        var budgetLimited = uninspected > 0 || rows.Any(row => row.Reason is "region_budget_exhausted"
            or "region_or_document_time_budget_exhausted" or "document_time_or_scan_budget_exhausted");
        var outcome = invalid || failed > 0 ? "degraded"
            : response.Execution?.TableRepairEnabled == false ? rows.Count > 0 ? "degraded" : "disabled"
            : budgetLimited ? "budget_limited"
            : completeZero ? "no_candidates_observed"
            : attempts is null ? "unknown" : notApplied > 0 ? "inconclusive" : "completed";
        return new(summaries, candidates, attempts, applied, failed, notApplied, uninspected, duration,
            outcome, invalid ? "invalid" : attempts.HasValue ? "observed" : "unavailable");
    }

    internal static bool SourceReferencesAreValid(DoclingConvertResponse response)
    {
        var tables = response.Document.JsonContent?.Tables;
        return tables is { Count: <= 1_000_000 }
            && tables.Select((table, ordinal) => table is not null
                && table.SelfRef == "#/tables/" + ordinal.ToString(CultureInfo.InvariantCulture)).All(valid => valid);
    }

    private static bool ResolvesSourceReference(DoclingConvertResponse response, string reference)
    {
        const string prefix = "#/tables/";
        var tables = response.Document.JsonContent?.Tables;
        return reference.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(reference.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && tables is not null && index >= 0 && index < tables.Count
            && tables[index].SelfRef == reference;
    }

    private static double? SecondsBudget(JsonElement settings)
        => settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("total_timeout_seconds", out var item)
            && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var seconds)
            && double.IsFinite(seconds) && seconds is > 0 and <= 60 ? seconds : null;

    internal static void RecordDiagnostics(BackendDiagnostics.Operation operation,
        DocumentIntelligenceOptions options, DoclingConvertResponse response)
    {
        var work = Observe(response);
        operation.Set("regional_table_repair_status",
            DoclingRegionalTableRepairMetadata.Settings(options, response).Availability == "invalid" ? "degraded" : work.Outcome);
        operation.Set("regional_table_repair_summaries", work.Summaries);
        operation.Set("regional_table_repair_candidates", work.Candidates);
        operation.Set("regional_table_repair_attempted", work.Attempted);
        operation.Set("regional_table_repair_attempts_available", work.Attempted.HasValue);
        operation.Set("regional_table_repair_applied", work.Applied);
        operation.Set("regional_table_repair_failed", work.Failed);
        operation.Set("regional_table_repair_uninspected", work.Uninspected);
        operation.Set("regional_table_repair_duration_ms", work.DurationMs);
    }

    internal static int? Count(JsonElement value, string name, int maximum = 1_000_000)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number)
            && number >= 0 && number <= maximum ? number : null;

    internal static string? String(JsonElement value, string name, int maximum = 128)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.String && item.GetString() is { } text && text.Length <= maximum ? text : null;
}
