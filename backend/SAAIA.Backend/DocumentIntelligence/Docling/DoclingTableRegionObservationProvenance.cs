using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts.DocumentIntelligence;

internal static class DoclingTableRegionObservationProvenance
{
    internal static CanonicalTableRegionEngineProvenance Build(JsonElement value)
    {
        var result = new CanonicalTableRegionEngineProvenance
        {
            Engine = "rapidocr", EngineVersion = Identity(value, "engine_version"), ModelId = Identity(value, "model_id"),
            DurationMs = DoclingTableRegionObservationPolicy.Number(value, "duration_ms")
        };
        Require(result.DurationMs is >= 0 and <= 60_000);
        if (value.TryGetProperty("model_artifacts", out var artifacts) && artifacts.ValueKind != JsonValueKind.Null)
        {
            Require(artifacts.ValueKind == JsonValueKind.Object && artifacts.EnumerateObject().Count() <= 16);
            foreach (var item in artifacts.EnumerateObject())
            {
                Require(item.Name.Length is > 0 and <= 128 && item.Name != "." && item.Name != ".."
                    && Regex.IsMatch(item.Name, "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)
                    && item.Value.ValueKind == JsonValueKind.String
                    && DoclingTableRegionObservationPolicy.Sha(item.Value.GetString()));
                Require(result.ModelArtifacts.TryAdd(item.Name, item.Value.GetString()!));
            }
        }
        if (value.TryGetProperty("runtime_settings", out var runtime) && runtime.ValueKind != JsonValueKind.Null)
        {
            Require(runtime.ValueKind == JsonValueKind.Object);
            result.RuntimeSettings = new()
            {
                OnnxruntimeIntraOpNumThreads = Positive(runtime, "onnxruntime_intra_op_num_threads", 1024),
                OnnxruntimeInterOpNumThreads = Positive(runtime, "onnxruntime_inter_op_num_threads", 1024),
                ObservedSessionCount = Positive(runtime, "observed_session_count", 32)
            };
            if (runtime.TryGetProperty("providers", out var providers))
            {
                Require(providers.ValueKind == JsonValueKind.Array && providers.GetArrayLength() is > 0 and <= 16);
                var names = providers.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).ToArray();
                Require(names.All(name => name == "CPUExecutionProvider") && names.Distinct(StringComparer.Ordinal).Count() == names.Length);
                result.RuntimeSettings.Providers = names.Select(name => name!).ToList();
            }
        }
        return result;
    }

    private static int? Positive(JsonElement value, string key, int maximum)
    {
        if (!value.TryGetProperty(key, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        Require(item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) && number > 0 && number <= maximum);
        return item.GetInt32();
    }

    private static string Identity(JsonElement value, string key)
    {
        var text = DoclingTableRegionObservationPolicy.Text(value, key);
        Require(text is { Length: > 0 and <= 256 } && Regex.IsMatch(text, "^[A-Za-z0-9_.+-]+$", RegexOptions.CultureInvariant));
        return text!;
    }
    private static void Require(bool condition) => DoclingTableRegionObservationPolicy.Require(condition);
}
