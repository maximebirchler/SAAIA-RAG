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
            EmitRagTrace(
                "source_backed_pipeline.step",
                ("source_trace_id", trace.TraceId),
                ("source_seq", trace.Sequence),
                ("source_step", trace.Step.ToString()),
                ("source_event", trace.EventName),
                ("source_fields",
                    FormatSourceBackedPipelineTraceFields(trace.Fields)));
        }
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
