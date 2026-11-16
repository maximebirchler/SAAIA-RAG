using System.Diagnostics;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)>
        TryHandleUnanchoredAcronymDefinitionAsync(
            string displayUserMessage,
            string language,
            Stopwatch swTotalPipeline,
            CancellationToken ct,
            Action<string>? onDelta,
            Action<string>? onProgress)
    {
        if (!EnforcesLocalCapabilityBoundary
            || !SourceBackedAcronymDefinitionProbe.TryExtractAcronym(
                displayUserMessage, out var acronym))
            return (false, string.Empty, null);

        EmitRagTrace("source_backed_acronym_definition.probe.start",
            ("acronym_length", acronym.Length));
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var raw = await _api.RagSearchToolAsync(
                acronym, 12, category: null, mode: "balanced", timeout.Token,
                sourceBackedCanonical: true).ConfigureAwait(false);
            var unanchored =
                SourceBackedAcronymDefinitionProbe.HasNoReliableAcronymAnchor(
                    raw, acronym);
            EmitRagTrace("source_backed_acronym_definition.probe.end",
                ("unanchored", unanchored),
                ("ms", sw.ElapsedMilliseconds));
            if (!unanchored)
                return (false, string.Empty, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            EmitRagTrace("source_backed_acronym_definition.probe.end",
                ("reason", "timeout"), ("ms", sw.ElapsedMilliseconds));
            return (false, string.Empty, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitRagTrace("source_backed_acronym_definition.probe.end",
                ("reason", "search_failed"),
                ("error_type", ex.GetType().Name),
                ("ms", sw.ElapsedMilliseconds));
            return (false, string.Empty, null);
        }

        var answer = SourceBackedTerminalAnswer.BuildUnanchoredAcronymDefinition(
            language, acronym);
        _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
        _mem.LastToolNames = new List<string> { "rag.search" };
        _mem.LastRagQueries = new List<string> { acronym };
        _mem.LastRagTraceEvents = new List<string>();
        _lastAnswerSource = "source_backed_acronym_definition:unanchored";
        await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
        onProgress?.Invoke(string.Empty);
        EmitRagTrace("source_backed_pipeline.active.terminal",
            ("decision", "insufficient_evidence"),
            ("reason", "unanchored_acronym_definition"),
            ("visible_sources", false),
            ("answered", false));
        var finalized = FinalizeAndReturn(
            swTotalPipeline,
            displayUserMessage,
            answer,
            null,
            "insufficient_evidence",
            _mem.LastToolNames,
            _mem.LastReasoningTracePublic);
        return (true, finalized.finalAnswer, finalized.sourcesPayload);
    }
}
