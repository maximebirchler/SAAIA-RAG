namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record EvidenceBundle(
    string BundleId,
    string UserQuestion,
    IReadOnlyList<EvidenceItem> Items,
    IReadOnlyList<SourceBackedTraceEvent> TraceEvents)
{
    public IReadOnlyList<SourceBackedRetrievalAttempt> RetrievalAttempts { get; init; } = Array.Empty<SourceBackedRetrievalAttempt>();

    public static EvidenceBundle Empty(string userQuestion)
        => new(
            "evidence-" + Guid.NewGuid().ToString("N"),
            userQuestion,
            Array.Empty<EvidenceItem>(),
            Array.Empty<SourceBackedTraceEvent>());

    public EvidenceBundle AddTrace(SourceBackedTraceEvent traceEvent)
        => this with { TraceEvents = TraceEvents.Concat(new[] { traceEvent }).ToArray() };

    public IReadOnlyDictionary<string, EvidenceItem> ById
        => Items
            .Where(static item => !string.IsNullOrWhiteSpace(item.EvidenceId))
            .GroupBy(static item => item.EvidenceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
}

public sealed record SourceBackedRetrievalAttempt(
    int Sequence,
    string ToolName,
    IReadOnlyList<string> Queries,
    string? CategoryPath,
    string Outcome,
    string? ErrorCode,
    bool Busy,
    IReadOnlyList<string> DegradedRetrievers,
    long DurationMs,
    int EvidenceItemCount)
{
    public bool TimedOut
        => string.Equals(ErrorCode, "rag_search_query_timeout", StringComparison.OrdinalIgnoreCase)
           || DegradedRetrievers.Contains("rag_search_query_timeout", StringComparer.OrdinalIgnoreCase);
}
