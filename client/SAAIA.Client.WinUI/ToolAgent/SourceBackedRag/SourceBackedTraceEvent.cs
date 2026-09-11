using System.Collections.ObjectModel;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record SourceBackedTraceEvent(
    string TraceId,
    int Sequence,
    SourceBackedPipelineStep Step,
    string EventName,
    IReadOnlyDictionary<string, string> Fields)
{
    public static SourceBackedTraceEvent Create(
        string traceId,
        int sequence,
        SourceBackedPipelineStep step,
        string eventName,
        IEnumerable<KeyValuePair<string, string>>? fields = null)
        => new(
            traceId,
            sequence,
            step,
            eventName,
            new ReadOnlyDictionary<string, string>(
                (fields ?? Array.Empty<KeyValuePair<string, string>>())
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
                .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Last().Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)));
}
