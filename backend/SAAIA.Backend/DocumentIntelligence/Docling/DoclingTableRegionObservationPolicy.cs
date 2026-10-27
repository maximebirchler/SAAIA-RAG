using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts.DocumentIntelligence;

internal static class DoclingTableRegionObservationPolicy
{
    internal static void Validate(DocumentIntelligenceOptions options, DoclingConvertResponse response)
    {
        var envelope = response.Document.JsonContent?.TableRegionObservations;
        var observed = response.Execution?.TableObservationSettingsJson;
        if (!options.RegionalTableAlternativeReadingsEnabled)
        {
            if (envelope is not null || observed is not null)
                throw new InvalidDataException("docling_table_observation_activation_mismatch");
            return;
        }
        if (envelope is null || observed is null)
            throw new InvalidDataException("docling_table_observation_configuration_unattested");
        ValidateSettings(options, observed.Value);
        if (!string.Equals(CanonicalContractJson.ComputeSha256(observed.Value),
                CanonicalContractJson.ComputeSha256(envelope.Settings), StringComparison.Ordinal))
            throw new InvalidDataException("docling_table_observation_settings_mismatch");
        ValidateEnvelope(response.Document.JsonContent!, envelope);
    }

    internal static void ValidateEnvelope(DoclingDocument source, DoclingTableRegionObservationEnvelope value)
    {
        Require(value.SchemaVersion == "saaia_table_region_observations_v1");
        Require(value.Availability is "observed" or "partial" or "unavailable");
        Require(value.Reason is null || Code(value.Reason));
        if (value.Availability == "unavailable")
        {
            Require((value.SourceSha256 is null || Sha(value.SourceSha256))
                && value.Reason is not null && value.Attempts is null && value.Observations is null
                && value.SourceTableCount is null && value.InspectedTableCount is null
                && value.UninspectedTableCount is null && value.AttemptedRegionCount is null
                && value.CompletedObservationCount is null);
            return;
        }
        Require(Sha(value.SourceSha256));
        Require(value.Attempts is not null && value.Observations is not null
            && value.SourceTableCount == source.Tables.Count && value.InspectedTableCount is >= 0 and <= 1024
            && value.UninspectedTableCount is >= 0
            && value.InspectedTableCount + value.UninspectedTableCount == value.SourceTableCount
            && value.AttemptedRegionCount is >= 0 and <= 8 && value.CompletedObservationCount is >= 0 and <= 8);
        var attempts = value.Attempts!;
        var readings = value.Observations!;
        Require(attempts.Count == value.InspectedTableCount && readings.Count == value.CompletedObservationCount
            && attempts.Count(row => row.Attempted == true) == value.AttemptedRegionCount
            && attempts.Select(row => row.SourceTableRef).Distinct(StringComparer.Ordinal).Count() == attempts.Count);
        var refs = source.Tables.Select((table, index) => (table, index))
            .ToDictionary(pair => $"#/tables/{pair.index}", pair => pair.table, StringComparer.Ordinal);
        foreach (var row in attempts)
        {
            Require(refs.TryGetValue(row.SourceTableRef, out var table) && table.SelfRef == row.SourceTableRef);
            Require(row.Attempted.HasValue && row.Status is "completed" or "not_attempted" or "timeout" or "error" or "refused");
            Require(row.Reason is null || Code(row.Reason));
            Require(row.PageNumber is null || table!.Provenance.Any(prov => prov.PageNumber == row.PageNumber));
            var matches = readings.Where(reading => reading.SourceTableRef == row.SourceTableRef).ToArray();
            if (row.Status == "completed")
                Require(row.Attempted == true && row.PageNumber is > 0 && Sha(row.RawObservationSha256)
                    && matches.Length == 1 && matches[0].PageNumber == row.PageNumber
                    && matches[0].RawObservationSha256 == row.RawObservationSha256);
            else
                Require(matches.Length == 0 && (row.RawObservationSha256 is null
                    || row.Status == "refused" && Sha(row.RawObservationSha256)));
        }
        Require(readings.All(row => attempts.Any(attempt => attempt.Status == "completed"
            && attempt.SourceTableRef == row.SourceTableRef && attempt.PageNumber == row.PageNumber)));
        if (value.Availability == "observed")
            Require(value.UninspectedTableCount == 0 && attempts.All(row => row.Status == "completed"));
    }

    internal static void ValidateSettings(DocumentIntelligenceOptions options, JsonElement settings)
    {
        Require(settings.ValueKind == JsonValueKind.Object
            && Text(settings, "schema_version") == "saaia_table_observation_settings_v1"
            && Boolean(settings, "enabled") == true && Boolean(settings, "primary_projection") == false
            && Boolean(settings, "repair_enabled") == options.RegionalTableRepairEnabled
            && Integer(settings, "max_regions") is >= 1 and <= 8
            && Number(settings, "total_timeout_seconds") is >= .1 and <= 60
            && Number(settings, "region_timeout_seconds") is >= .1 and <= 15
            && Integer(settings, "max_rendered_pixels") is >= 1 and <= 20_000_000
            && Number(settings, "render_scale") == options.RegionalTableRepairScale
            && Number(settings, "padding_points") == options.RegionalTableRepairPaddingPoints
            && Number(settings, "minimum_score") == options.RegionalTableRepairMinimumScore
            && Text(settings, "selection_policy") == "sparse_then_descending_table_tail_v1"
            && Text(settings, "budget_scope") == "shared_regional_table_work"
            && Text(settings, "process_isolation") == "per_region_linux_process_group"
            && Text(settings, "engine") == "rapidocr");
    }

    internal static object SafeSettings(JsonElement settings) => new
    {
        enabled = Boolean(settings, "enabled"), maxRegions = Number(settings, "max_regions"),
        totalTimeoutSeconds = Number(settings, "total_timeout_seconds"),
        regionTimeoutSeconds = Number(settings, "region_timeout_seconds"),
        renderScale = Number(settings, "render_scale"), paddingPoints = Number(settings, "padding_points"),
        minimumScore = Number(settings, "minimum_score"), maxRenderedPixels = Number(settings, "max_rendered_pixels"),
        primaryProjection = Boolean(settings, "primary_projection"),
        repairEnabled = Boolean(settings, "repair_enabled")
    };

    internal static bool Sha(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    internal static bool Code(string value) => value.Length <= 128 && Regex.IsMatch(value, "^[a-z0-9_]+$", RegexOptions.CultureInvariant);
    internal static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    internal static double? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number
        && item.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    internal static int? Integer(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number
        && item.TryGetInt32(out var number) ? number : null;
    private static bool? Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : null;
    internal static void Require(bool condition)
    {
        if (!condition) throw new InvalidDataException("docling_table_observation_contract_invalid");
    }
}
