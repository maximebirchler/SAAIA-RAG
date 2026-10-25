using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private void EmitSourceBackedPipelineTraceEvents(
        SourceBackedPipelineResult result)
    {
        foreach (var trace in result.TraceEvents.Where(static trace =>
                     !trace.EventName.StartsWith(
                         "source_backed_agent_v2.",
                         StringComparison.OrdinalIgnoreCase)))
        {
            EmitRagTrace("source_backed_pipeline.step", SourceBackedTraceMetadata(trace));
        }
    }

    private static (string Key, object? Value)[] SourceBackedTraceMetadata(SourceBackedTraceEvent trace)
    {
        var fields = new List<(string Key, object? Value)>
        {
            ("source_trace_id", trace.TraceId), ("source_seq", trace.Sequence),
            ("source_step", trace.Step.ToString()), ("source_event", trace.EventName),
            ("source_fields", FormatSourceBackedPipelineTraceFields(trace.Fields))
        };
        // Explicit projection only: no free text or document identity is exported.
        foreach (var key in new[] { "status", "reason" })
            if (trace.Fields.TryGetValue(key, out var value) && IsSafeMetadataTraceValue(value))
                fields.Add(("source_" + key, value));
        foreach (var key in new[] { "candidate_count", "exact_match_count", "requested_reference_length",
                     "executable_actions", "quarantined_actions", "pages", "items", "elapsed_ms" })
            if (trace.Fields.TryGetValue(key, out var value)
                && long.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 0)
                fields.Add(("source_" + key, count));
        if (trace.Fields.TryGetValue("requested_reference", out var reference))
            fields.Add(("source_reference_has_directory", reference.Contains('/') || reference.Contains('\\') ? 1L : 0L));
        return fields.ToArray();
    }

    private static string FormatSourceBackedPipelineTraceFields(
        IReadOnlyDictionary<string, string> fields)
        => string.Join(
            ";",
            fields
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Key + "=" + pair.Value)
                .Take(16));

    private static string FormatSourceBackedMemoryTraceEvent(SourceBackedTraceEvent trace)
    {
        var fields = FormatSourceBackedPipelineTraceFields(trace.Fields);
        var prefix = $"{trace.Sequence}:{trace.Step}:{trace.EventName}";
        if (string.IsNullOrWhiteSpace(fields))
            return prefix;

        const int maxFieldsLength = 520;
        return prefix + " {" + (fields.Length <= maxFieldsLength ? fields : fields[..maxFieldsLength] + "...") + "}";
    }
}
