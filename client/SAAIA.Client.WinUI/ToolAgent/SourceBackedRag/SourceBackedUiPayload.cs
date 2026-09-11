namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record SourceBackedUiPayload(
    string Answer,
    IReadOnlyList<ToolMemory.SourceRef> Sources,
    IReadOnlyList<SourceBackedTraceEvent> TraceEvents);
