using System.Globalization;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static RetrievalRequest ToRetrievalRequest(
        string toolName,
        JsonElement args,
        int turn,
        int? nextOffset,
        int materializedEvidenceCount,
        int newEvidenceCount,
        IReadOnlyList<string>? newEvidenceIds = null)
    {
        var queryVariants = GetStringArray(args, "queries");
        var query = GetString(args, "query", "q")
                    ?? (queryVariants.Count > 0 ? string.Join(" | ", queryVariants) : string.Empty);
        return new RetrievalRequest(
            toolName,
            query,
            GetString(args, "categoryPath", "category"),
            "native_agent_turn_" + turn.ToString(CultureInfo.InvariantCulture),
            DocId: GetString(args, "docId"),
            DocPath: GetString(args, "docPath"),
            DocRef: GetString(args, "docRef"),
            ChunkId: GetString(args, "chunkId"),
            PageStart: GetInt(args, "pageStart"),
            PageEnd: GetInt(args, "pageEnd"),
            Limit: GetInt(args, "topK", "limit"),
            Offset: GetInt(args, "offset"),
            QueryVariants: queryVariants,
            NextOffset: nextOffset,
            MaterializedEvidenceCount: materializedEvidenceCount,
            NewEvidenceCount: newEvidenceCount,
            ToolArguments: args.Clone(),
            NewEvidenceIds: newEvidenceIds);
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in root.EnumerateObject())
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return string.IsNullOrWhiteSpace(property.Value.GetString())
                    ? null
                    : property.Value.GetString()!.Trim();
            }
        }

        return null;
    }

    private static int? GetInt(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in root.EnumerateObject())
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return property.Value.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()?.Trim())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private static SourceBackedTraceEvent Trace(
        string traceId,
        ref int sequence,
        SourceBackedPipelineStep step,
        string eventName,
        params (string key, object? value)[] fields)
    {
        sequence++;
        return SourceBackedTraceEvent.Create(
            traceId,
            sequence,
            step,
            eventName,
            fields.Select(static field => new KeyValuePair<string, string>(
                field.key,
                FormatTraceValue(field.value))));
    }

    private void AddTrace(
        ICollection<SourceBackedTraceEvent> traces,
        SourceBackedTraceEvent trace)
    {
        traces.Add(trace);
        _traceSink?.Invoke(trace);
    }

    private void TraceStructuredRendererCompletion(
        SemanticSelectionLayout renderedLayout,
        IReadOnlyList<string>? activeSemanticSelectionIds,
        SourceBackedAgentCompletion completion,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Writer,
            "source_backed_agent_v2.structured_renderer.completed",
            ("turn", turn),
            ("rows", renderedLayout.RowLabels.Count),
            ("columns", renderedLayout.Columns.Count),
            ("evidence_ids", activeSemanticSelectionIds?.Count ?? 0),
            ("selection_source", "llm_evidence_workspace"),
            ("content_characters", completion.Content.Length)));

    private void TraceVerifiedAnswer(
        SourceVerificationResult verification,
        SelectedWriterVerification selectedWriterVerification,
        bool writerCitedContextEvidenceBeyondSemanticSelection,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.SourceVerifier,
            "source_backed_agent_v2.answer.verified",
            ("turn", turn),
            ("valid", verification.IsValid),
            ("cited_evidence", verification.CitedEvidence.Count),
            ("cited_evidence_ids", verification.CitedEvidence
                .Select(static item => item.EvidenceId)
                .ToArray()),
            ("writer_allowed_evidence_ids", selectedWriterVerification.AllowedEvidenceIds),
            ("required_evidence_groups", selectedWriterVerification.RequiredEvidenceGroupCount),
            ("context_evidence_beyond_selection", writerCitedContextEvidenceBeyondSemanticSelection),
            ("errors", verification.Errors.Select(static error => error.Code).ToArray())));

    private static string FormatTraceValue(object? value)
        => value switch
        {
            null => string.Empty,
            string text => text,
            bool flag => flag ? "true" : "false",
            IEnumerable<string> values => string.Join(",", values),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
}
