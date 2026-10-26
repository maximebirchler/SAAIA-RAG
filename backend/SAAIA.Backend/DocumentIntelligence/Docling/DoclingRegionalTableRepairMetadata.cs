using System.Text.Json;
using static DoclingRegionalTableRepairPolicy;

internal sealed record DoclingRegionalTableRepairSettings(string Json, string Availability);
internal sealed record DoclingRegionalTableRepairModels(string ArtifactsJson, string RuntimeJson,
    string EngineVersion, string Availability);

/// <summary>Compact allowlisted metadata; OCR text, polygons, paths and unknown fields stay out of stage summaries.</summary>
internal static class DoclingRegionalTableRepairMetadata
{
    internal static DoclingRegionalTableRepairSettings Settings(DocumentIntelligenceOptions options, DoclingConvertResponse response)
    {
        if (response.Execution?.TableRepairSettingsJson is not { } value)
            return new("null", "unattested");
        var schema = String(value, "schema_version");
        var regions = Count(value, "max_regions", 8);
        var pixels = Count(value, "max_rendered_pixels", 20_000_000);
        var total = Number(value, "total_timeout_seconds", .1, 60);
        var region = Number(value, "region_timeout_seconds", .1, 15);
        var scale = Number(value, "render_scale", 1, 8);
        var padding = Number(value, "padding_points", 0, 72);
        var minimum = Number(value, "minimum_score", 0, 1);
        var isolation = String(value, "process_isolation");
        var engine = String(value, "engine");
        var resolution = String(value, "model_resolution");
        var secondaryAbsent = value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("secondary_engine", out var secondary) && secondary.ValueKind == JsonValueKind.Null;
        var valid = schema == "saaia_table_repair_settings_v2" && regions is > 0 && pixels is > 0
            && total.HasValue && region.HasValue && scale == options.RegionalTableRepairScale
            && padding == options.RegionalTableRepairPaddingPoints && minimum == options.RegionalTableRepairMinimumScore
            && isolation == "per_region_linux_process_group" && engine == "rapidocr"
            && resolution == "bundled_local_files_only" && secondaryAbsent;
        // Categorical fields are rendered only when their exact finite value is recognized.
        var snapshot = JsonSerializer.Serialize(new
        {
            schema_version = schema == "saaia_table_repair_settings_v2" ? schema : null,
            max_regions = regions, total_timeout_seconds = total, region_timeout_seconds = region,
            render_scale = scale, padding_points = padding, minimum_score = minimum, max_rendered_pixels = pixels,
            process_isolation = isolation == "per_region_linux_process_group" ? isolation : null,
            engine = engine == "rapidocr" ? engine : null, secondary_engine = (string?)null,
            model_resolution = resolution == "bundled_local_files_only" ? resolution : null
        });
        return new(snapshot, valid ? "observed" : "invalid");
    }

    internal static DoclingRegionalTableRepairModels Models(DoclingConvertResponse response)
    {
        if (!SourceReferencesAreValid(response) || Observe(response).AttemptAvailability == "invalid")
            return new("[]", "[]", "unattested", "invalid");
        var sources = (response.Document.JsonContent?.Tables ?? [])
            .Select((table, ordinal) => new { Table = table, SourceTableOrdinal = ordinal })
            .Where(source => source.Table.TableRepair?.OcrObservationJson.HasValue == true
                || source.Table.RepairAttempt?.OcrObservationJson.HasValue == true)
            .Take(9)
            .Select(source => new { source.Table.SelfRef, source.SourceTableOrdinal,
                Selection = SelectObservation(response, source.Table) }).ToArray();
        if (sources.Length > 8)
            return new("[]", "[]", "unattested", "limit_exceeded");
        if (sources.Any(source => source.Selection.Invalid))
            return new("[]", "[]", "unattested", "invalid");
        var observations = sources.Where(source => source.Selection.Observation.HasValue).Take(9).ToArray();
        var artifacts = new List<object>();
        var runtime = new List<object>();
        var versions = new SortedSet<string>(StringComparer.Ordinal);
        var references = new HashSet<string>(StringComparer.Ordinal);
        var invalid = false;
        for (var index = 0; index < observations.Length; index++)
        {
            var source = observations[index];
            var observation = source.Selection.Observation!.Value;
            var version = String(observation, "engine_version");
            var model = String(observation, "model_id");
            if (source.SelfRef != "#/tables/" + source.SourceTableOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || !references.Add(source.SelfRef)
                || String(observation, "engine") != "rapidocr" || !SafeCode(version) || !SafeCode(model))
            {
                invalid = true;
                continue;
            }
            versions.Add(version!);
            if (observation.TryGetProperty("model_artifacts", out var models) && models.ValueKind == JsonValueKind.Object)
            {
                var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var fields = models.EnumerateObject().Take(9).ToArray();
                var safe = fields.Length is > 0 and <= 8;
                foreach (var field in fields)
                {
                    if (!SafeFilename(field.Name) || field.Value.ValueKind != JsonValueKind.String
                        || field.Value.GetString() is not { } hash || hash.Length != 64 || !hash.All(Uri.IsHexDigit)
                        || !hashes.TryAdd(field.Name, hash.ToLowerInvariant()))
                        safe = false;
                }
                if (safe) artifacts.Add(new { tableRef = source.SelfRef, sourceTableOrdinal = source.SourceTableOrdinal,
                    engine = "rapidocr", modelId = model, modelArtifacts = hashes });
                else invalid = true;
            }
            else invalid = true;
            if (observation.TryGetProperty("runtime_settings", out var settings) && settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty("providers", out var providers) && providers.ValueKind == JsonValueKind.Array
                && providers.GetArrayLength() == 1 && providers[0].ValueKind == JsonValueKind.String
                && providers[0].GetString() == "CPUExecutionProvider"
                && Count(settings, "observed_session_count", 3) == 3
                && Count(settings, "onnxruntime_intra_op_num_threads", 2) == 2
                && Count(settings, "onnxruntime_inter_op_num_threads", 1) == 1)
                runtime.Add(new { tableRef = source.SelfRef, sourceTableOrdinal = source.SourceTableOrdinal,
                    providers = new[] { "CPUExecutionProvider" },
                    observed_session_count = 3, onnxruntime_intra_op_num_threads = 2, onnxruntime_inter_op_num_threads = 1 });
            else invalid = true;
        }
        return new(JsonSerializer.Serialize(artifacts), JsonSerializer.Serialize(runtime),
            versions.Count == 0 ? "unattested" : string.Join(';', versions),
            observations.Length == 0 ? "unattested" : invalid ? "mixed_unknown" : "observed");
    }

    private static (JsonElement? Observation, bool Invalid) SelectObservation(
        DoclingConvertResponse response, DoclingTableItem table)
    {
        var applied = table.TableRepair?.OcrObservationJson;
        var attempt = table.RepairAttempt;
        var inconclusive = attempt?.OcrObservationJson;
        if (applied.HasValue && inconclusive.HasValue)
            return (null, true);
        if (applied.HasValue || !inconclusive.HasValue)
            return (applied, false);
        var summary = response.TableRepairs.SingleOrDefault(row => row.TableRef == table.SelfRef);
        var completed = attempt!.SchemaVersion == "saaia_table_text_repair_v1"
            && attempt.TableRef == table.SelfRef && attempt.Attempted == true && attempt.Status == "not_applied"
            && attempt.Reason is not ("region_budget_exhausted" or "region_or_document_time_budget_exhausted"
                or "document_time_or_scan_budget_exhausted")
            && attempt.OcrDurationMs is >= 0 and <= 15_000
            && Count(inconclusive!.Value, "duration_ms", 15_000) == attempt.OcrDurationMs
            && summary is not null && summary.Status == attempt.Status && summary.Attempted == true
            && summary.Reason == attempt.Reason && summary.OcrDurationMs == attempt.OcrDurationMs;
        return completed ? (inconclusive, false) : (null, true);
    }

    private static double? Number(JsonElement value, string name, double minimum, double maximum)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number)
            && double.IsFinite(number) && number >= minimum && number <= maximum ? number : null;

    private static bool SafeCode(string? value)
        => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.' or '+' or ':');

    private static bool SafeFilename(string value)
        => value.Length is > 0 and <= 128 && !value.Contains("..", StringComparison.Ordinal)
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
}
