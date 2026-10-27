using System.Text.Json;

namespace SAAIA.Contracts.DocumentIntelligence;

public static partial class CanonicalTableRegionObservationValidator
{
    private static void ValidateRaw(CanonicalTableRegionObservation observation, bool required,
        List<CanonicalValidationIssue> issues)
    {
        var raw = observation.PublicationRawBytes;
        if (raw is null)
        {
            Check(!required, "rawObservation", "raw_required", issues);
            return; // A deserialized bundle does not carry duplicate raw bytes.
        }
        if (raw.Length is 0 or > MaximumRawBytes)
        { Check(false, "rawObservation", "raw_byte_budget", issues); return; }
        Check(Hash(raw) == observation.RawObservationSha256, "rawObservation", "raw_hash", issues);
        try
        {
            // Strict decoding rejects invalid UTF8 instead of hashing a replacement.
            var json = StrictUtf8.GetString(raw);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var remainingTokens = 100_000;
            if (!UniqueKeys(root, ref remainingTokens))
            { Check(false, "rawObservation", "ambiguous_raw_json", issues); return; }
            var provenance = observation.EngineProvenance;
            Check(root.ValueKind == JsonValueKind.Object && root.GetProperty("engine").GetString() == "rapidocr"
                && root.GetProperty("engine_version").GetString() == provenance.EngineVersion
                && root.GetProperty("model_id").GetString() == provenance.ModelId
                && root.GetProperty("coordinate_origin").GetString() == "TOPLEFT",
                "rawObservation", "raw_engine_or_frame", issues);
            var artifacts = root.GetProperty("model_artifacts");
            Check(artifacts.ValueKind == JsonValueKind.Object && provenance.ModelArtifacts.Count == artifacts.EnumerateObject().Count()
                && artifacts.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.String
                    && provenance.ModelArtifacts.TryGetValue(p.Name, out var hash) && p.Value.GetString() == hash),
                "rawObservation.model_artifacts", "raw_model_provenance", issues);
            var runtime = root.GetProperty("runtime_settings");
            var typed = provenance.RuntimeSettings;
            Check(typed is not null && runtime.GetProperty("onnxruntime_intra_op_num_threads").GetInt32() == typed.OnnxruntimeIntraOpNumThreads
                && runtime.GetProperty("onnxruntime_inter_op_num_threads").GetInt32() == typed.OnnxruntimeInterOpNumThreads
                && runtime.GetProperty("observed_session_count").GetInt32() == typed.ObservedSessionCount
                && runtime.GetProperty("providers").EnumerateArray().Select(p => p.GetString()).SequenceEqual(typed.Providers ?? []),
                "rawObservation.runtime_settings", "raw_runtime_provenance", issues);
            Check(provenance.DurationMs.HasValue && root.GetProperty("duration_ms").GetDouble() == provenance.DurationMs,
                "rawObservation.duration_ms", "raw_duration", issues);
            var lines = root.GetProperty("lines");
            if (lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() != observation.Lines.Count)
            { Check(false, "rawObservation.lines", "raw_line_count", issues); return; }
            if (observation.Lines.Count > MaximumLines) return;
            var frame = observation.CoordinateFrame;
            if (frame is null || !FinitePositive(frame.PageWidthPoints) || !FinitePositive(frame.PageHeightPoints)) return;
            for (var index = 0; index < observation.Lines.Count; index++)
            {
                var line = observation.Lines[index];
                var source = lines[index];
                Check(source.GetProperty("text").GetString() == line.Text, "rawObservation.lines.text", "literal_raw_mismatch", issues);
                var confidence = source.GetProperty("confidence");
                Check(confidence.ValueKind == JsonValueKind.Null ? line.Confidence is null
                    : confidence.GetDouble() == line.Confidence, "rawObservation.lines.confidence", "raw_confidence", issues);
                var polygon = source.GetProperty("polygon");
                if (polygon.ValueKind != JsonValueKind.Array || polygon.GetArrayLength() != 4 || line.Polygon.Points.Count != 4)
                { Check(false, "rawObservation.lines.polygon", "raw_polygon", issues); continue; }
                for (var point = 0; point < 4; point++)
                {
                    var pair = polygon[point];
                    Check(pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2
                        && pair[0].GetDouble() / frame.PageWidthPoints == line.Polygon.Points[point].X
                        && pair[1].GetDouble() / frame.PageHeightPoints == line.Polygon.Points[point].Y,
                        "rawObservation.lines.polygon", "raw_polygon_mapping", issues);
                }
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or ArgumentException or NullReferenceException)
        { Check(false, "rawObservation", "raw_schema", issues); }
    }

    private static bool UniqueKeys(JsonElement value, ref int remaining)
    {
        if (--remaining < 0) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueKeys(property.Value, ref remaining)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (!UniqueKeys(item, ref remaining)) return false;
        return true;
    }
}
