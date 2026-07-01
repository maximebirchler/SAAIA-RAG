using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int MaxStoredRagTraceEvents = 240;
    private const int MaxStoredResearchWorkingNotes = 32;
    private const int DefaultRagTraceValueChars = 180;
    private readonly object _ragTraceLock = new();
    private readonly Stopwatch _ragTraceTurnStopwatch = new();
    private string _ragTraceId = string.Empty;
    private int _ragTraceSequence;

    private void ResetRagTraceTurn()
    {
        lock (_ragTraceLock)
        {
            _ragTraceId = string.Empty;
            _ragTraceSequence = 0;
            _ragTraceTurnStopwatch.Reset();
            _mem.Execution.LastRagTraceEvents = new();
        }
    }

    private void BeginRagTraceTurn(
        string userMessage,
        IReadOnlyList<(string role, string content)> chatHistory)
    {
        lock (_ragTraceLock)
        {
            _ragTraceId = "rag-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8];
            _ragTraceSequence = 0;
            _ragTraceTurnStopwatch.Restart();
            _mem.Execution.LastRagTraceEvents = new();
        }

        EmitRagTrace(
            "turn.begin",
            ("history", chatHistory.Count),
            ("user_chars", userMessage?.Length ?? 0),
            ("planning", LooksLikeAnyDocumentaryPlanningRequest(userMessage)),
            ("query", userMessage));
    }

    private void EmitRagTrace(string eventName, params (string Key, object? Value)[] fields)
    {
        var traceId = EnsureRagTraceId();
        var seq = Interlocked.Increment(ref _ragTraceSequence);
        var elapsedMs = _ragTraceTurnStopwatch.IsRunning
            ? _ragTraceTurnStopwatch.ElapsedMilliseconds
            : 0;
        var line = BuildRagTraceLine(eventName, traceId, seq, elapsedMs, fields);

        ClientLog.Info(line);

        lock (_ragTraceLock)
        {
            var events = _mem.Execution.LastRagTraceEvents;
            if (events.Count >= MaxStoredRagTraceEvents)
                events.RemoveRange(0, Math.Min(events.Count, events.Count - MaxStoredRagTraceEvents + 1));
            events.Add(line);
        }
    }

    private string EnsureRagTraceId()
    {
        if (!string.IsNullOrWhiteSpace(_ragTraceId))
            return _ragTraceId;

        lock (_ragTraceLock)
        {
            if (string.IsNullOrWhiteSpace(_ragTraceId))
            {
                _ragTraceId = "rag-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N")[..8];
                if (!_ragTraceTurnStopwatch.IsRunning)
                    _ragTraceTurnStopwatch.Start();
            }

            return _ragTraceId;
        }
    }

    private static string BuildRagTraceLine(
        string eventName,
        string traceId,
        int sequence,
        long elapsedMs,
        IEnumerable<(string Key, object? Value)> fields)
    {
        var sb = new StringBuilder();
        sb.Append("[RAG_TRACE");
        sb.Append(" event=").Append(SanitizeRagTraceToken(eventName, "unknown"));
        sb.Append(" trace_id=").Append(SanitizeRagTraceToken(traceId, "trace"));
        sb.Append(" seq=").Append(sequence.ToString(CultureInfo.InvariantCulture));
        sb.Append(" elapsed_ms=").Append(Math.Max(0, elapsedMs).ToString(CultureInfo.InvariantCulture));

        foreach (var (key, value) in fields)
        {
            var safeKey = SanitizeRagTraceKey(key);
            if (string.IsNullOrWhiteSpace(safeKey))
                continue;

            sb.Append(' ');
            sb.Append(safeKey);
            sb.Append('=');
            sb.Append(FormatRagTraceFieldValue(value));
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string SanitizeRagTraceKey(string? key)
    {
        var normalized = Regex.Replace((key ?? string.Empty).Trim(), @"[^A-Za-z0-9_.-]+", "_");
        return normalized.Trim('_');
    }

    private static string SanitizeRagTraceToken(string? value, string fallback)
    {
        var normalized = SanitizeRagTraceKey(value);
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string FormatRagTraceFieldValue(object? value)
    {
        if (value is null)
            return "null";

        return value switch
        {
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            TimeSpan ts => Math.Round(ts.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            string s => JsonSerializer.Serialize(FormatRagTraceValue(s, DefaultRagTraceValueChars)),
            IEnumerable<string> strings => JsonSerializer.Serialize(strings
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => FormatRagTraceValue(item, 120))
                .Take(16)
                .ToArray()),
            IEnumerable enumerable => JsonSerializer.Serialize(FormatRagTraceEnumerable(enumerable)),
            _ => JsonSerializer.Serialize(FormatRagTraceValue(value.ToString(), DefaultRagTraceValueChars))
        };
    }

    private static string[] FormatRagTraceEnumerable(IEnumerable enumerable)
    {
        var values = new List<string>();
        foreach (var item in enumerable)
        {
            if (item is null)
                continue;

            values.Add(FormatRagTraceValue(item.ToString(), 120));
            if (values.Count >= 16)
                break;
        }

        return values.ToArray();
    }

    private void EmitSourceBackedResearchInventoryTrace(
        ToolResults toolResults,
        string query,
        string language,
        SourceBackedEvidenceSufficiency analysis,
        int ragCallBudget,
        int remainingRagCalls)
    {
        var inventorySw = Stopwatch.StartNew();
        var ragItemCount = CountRagRetrievalToolCalls(toolResults);
        var explorationTraces = _mem.Execution.LastRagEvidenceExploration ?? new List<ToolMemory.RagEvidenceExplorationTrace>();
        EmitRagTrace(
            "research.inventory.start",
            ("query", query),
            ("kind", analysis.Kind),
            ("reason", analysis.Reason),
            ("candidates", analysis.CandidateCount),
            ("minimum_candidates", analysis.MinimumCandidateCount),
            ("target_slots", analysis.TargetSlotCount),
            ("distinct_pages", analysis.DistinctSourcePageCount),
            ("rag_items", ragItemCount),
            ("remaining_rag_calls", remainingRagCalls));

        var snapshot = BuildSourceBackedResearchInventorySnapshot(
            toolResults,
            query,
            language,
            analysis);
        EmitRagTrace(
            "research.inventory.snapshot",
            ("candidate_count", snapshot.CandidateCount),
            ("candidate_titles", snapshot.CandidateTitles),
            ("distinct_candidate_pages", snapshot.DistinctCandidatePageCount),
            ("preview_count", snapshot.InventoryPreview.Length),
            ("candidate_ms", snapshot.CandidateMs),
            ("preview_ms", snapshot.PreviewMs),
            ("ms", inventorySw.ElapsedMilliseconds));

        EmitRagTrace(
            "research.inventory",
            ("query", query),
            ("kind", analysis.Kind),
            ("reason", analysis.Reason),
            ("score", analysis.Score),
            ("usable_hits", analysis.UsableHitCount),
            ("candidates", analysis.CandidateCount),
            ("minimum_candidates", analysis.MinimumCandidateCount),
            ("target_slots", analysis.TargetSlotCount),
            ("distinct_docs", analysis.DistinctDocumentCount),
            ("distinct_pages", analysis.DistinctSourcePageCount),
            ("rag_items", ragItemCount),
            ("rag_call_budget", ragCallBudget),
            ("remaining_rag_calls", remainingRagCalls),
            ("exploration_passes", explorationTraces.Count),
            ("accepted_exploration_passes", explorationTraces.Count(static pass => pass.Accepted)),
            ("rejected_exploration_passes", explorationTraces.Count(static pass => !pass.Accepted)),
            ("planner_passes", explorationTraces.Count(static pass => string.Equals(pass.Origin, "llm_planner", StringComparison.OrdinalIgnoreCase))),
            ("distinct_candidate_pages", snapshot.DistinctCandidatePageCount),
            ("candidate_titles", snapshot.CandidateTitles),
            ("inventory_preview", snapshot.InventoryPreview),
            ("ms", inventorySw.ElapsedMilliseconds));
    }

    private sealed record SourceBackedResearchInventorySnapshot(
        string[] CandidateTitles,
        string[] InventoryPreview,
        int CandidateCount,
        int DistinctCandidatePageCount,
        long CandidateMs,
        long PreviewMs);

    private static SourceBackedResearchInventorySnapshot BuildSourceBackedResearchInventorySnapshot(
        ToolResults toolResults,
        string query,
        string language,
        SourceBackedEvidenceSufficiency analysis)
    {
        var candidateSw = Stopwatch.StartNew();
        List<SourceBackedOptionCandidate> candidates;
        try
        {
            var diagnosticCandidateLimit = Math.Clamp(Math.Max(analysis.MinimumCandidateCount, 24), 12, 48);
            candidates = SelectSourceBackedPlanningCandidates(
                    toolResults,
                    query,
                    diagnosticCandidateLimit,
                    NormalizeLanguageCode(language),
                    requireStrictStructuredEvidence: ShouldGateStructuredSourceBackedPlanningCoverage(query))
                .ToList();
        }
        catch
        {
            candidates = new List<SourceBackedOptionCandidate>();
        }

        var candidateMs = candidateSw.ElapsedMilliseconds;
        var titles = candidates
            .Select(static candidate => candidate.Title)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(title => TruncateForPrompt(title, 120))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        var distinctCandidatePageCount = candidates
            .Select(static candidate => BuildRagHitVisiblePageMergeKey(candidate.Hit))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var previewSw = Stopwatch.StartNew();
        var preview = new List<string>();
        try
        {
            foreach (var candidate in candidates.Take(8))
            {
                AddSourceBackedCandidateLeadLine(preview, "item", candidate.Title, candidate.Hit, language);
            }

            if (preview.Count == 0)
            {
                foreach (var hit in EnumerateRagHitSummaries(toolResults)
                    .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
                    .OrderByDescending(static hit => ComputeSourceBackedEvidenceRichnessScore(hit))
                    .ThenByDescending(static hit => hit.Score)
                    .Take(8))
                {
                    var title = ExtractReadablePartialPlanningLeadTitle(hit, query);
                    if (string.IsNullOrWhiteSpace(title))
                        title = CollapseWhitespace(hit.SectionTitle ?? hit.HeadingPath ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(title))
                        title = "source-backed context";

                    AddSourceBackedCandidateLeadLine(preview, "context", title, hit, language);
                }
            }
        }
        catch
        {
            preview.Clear();
        }

        return new SourceBackedResearchInventorySnapshot(
            titles,
            preview
                .Select(line => TruncateForPrompt(line, 220))
                .Take(8)
                .ToArray(),
            candidates.Count,
            distinctCandidatePageCount,
            candidateMs,
            previewSw.ElapsedMilliseconds);
    }

    private void RememberSourceBackedEvidenceExplorationPass(
        SourceBackedEvidenceExplorationPass pass,
        SourceBackedEvidenceSufficiency before,
        SourceBackedEvidenceSufficiency? after,
        long elapsedMs,
        bool accepted,
        string? rejectReason,
        string? effectiveUserMessage = null,
        string? language = null)
    {
        var traces = _mem.Execution.LastRagEvidenceExploration;
        if (traces.Count >= 16)
            traces.RemoveAt(0);

        traces.Add(new ToolMemory.RagEvidenceExplorationTrace
        {
            Label = TruncateForPrompt(pass.Label, 80),
            Origin = TruncateForPrompt(pass.Origin ?? "unknown", 48),
            Purpose = TruncateForPrompt(pass.Purpose, 180),
            Queries = pass.Queries
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .Select(query => TruncateForPrompt(query, 180))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            CategoryScope = TruncateForPrompt(pass.CategoryScope ?? string.Empty, 160),
            DocId = TruncateForPrompt(pass.DocId ?? string.Empty, 120),
            DocPath = TruncateForPrompt(pass.DocPath ?? string.Empty, 240),
            PageStart = pass.PageStart,
            PageEnd = pass.PageEnd,
            KindBefore = before.Kind,
            ReasonBefore = before.Reason,
            ScoreBefore = before.Score,
            UsableHitsBefore = before.UsableHitCount,
            CandidateCountBefore = before.CandidateCount,
            DistinctDocumentsBefore = before.DistinctDocumentCount,
            DistinctPagesBefore = before.DistinctSourcePageCount,
            MinimumCandidates = before.MinimumCandidateCount,
            TargetSlots = before.TargetSlotCount,
            ElapsedMs = Math.Max(0, elapsedMs),
            Accepted = accepted,
            RejectReason = rejectReason,
            ReasonAfter = after?.Reason,
            ScoreAfter = after?.Score,
            UsableHitsAfter = after?.UsableHitCount,
            CandidateCountAfter = after?.CandidateCount,
            DistinctDocumentsAfter = after?.DistinctDocumentCount,
            DistinctPagesAfter = after?.DistinctSourcePageCount
        });
        RememberSourceBackedResearchWorkingNote(pass, before, after, elapsedMs, accepted, rejectReason, effectiveUserMessage, language);
        EmitRagTrace(
            "evidence.exploration.pass",
            ("label", pass.Label),
            ("origin", pass.Origin),
            ("purpose", pass.Purpose),
            ("category_scope", pass.CategoryScope),
            ("doc_id", pass.DocId),
            ("doc_path", pass.DocPath),
            ("page_start", pass.PageStart),
            ("page_end", pass.PageEnd),
            ("accepted", accepted),
            ("reject_reason", rejectReason),
            ("elapsed_ms", elapsedMs),
            ("queries", pass.Queries),
            ("kind_before", before.Kind),
            ("reason_before", before.Reason),
            ("score_before", before.Score),
            ("usable_hits_before", before.UsableHitCount),
            ("candidates_before", before.CandidateCount),
            ("distinct_docs_before", before.DistinctDocumentCount),
            ("distinct_pages_before", before.DistinctSourcePageCount),
            ("reason_after", after?.Reason),
            ("score_after", after?.Score),
            ("usable_hits_after", after?.UsableHitCount),
            ("candidates_after", after?.CandidateCount),
            ("distinct_docs_after", after?.DistinctDocumentCount),
            ("distinct_pages_after", after?.DistinctSourcePageCount));
    }

    private void RememberSourceBackedResearchWorkingNote(
        SourceBackedEvidenceExplorationPass pass,
        SourceBackedEvidenceSufficiency before,
        SourceBackedEvidenceSufficiency? after,
        long elapsedMs,
        bool accepted,
        string? rejectReason,
        string? effectiveUserMessage,
        string? language)
    {
        if (string.IsNullOrWhiteSpace(effectiveUserMessage)
            || pass.Queries is null
            || pass.Queries.Length == 0)
        {
            return;
        }

        var topicKey = BuildSourceBackedResearchTopicKey(effectiveUserMessage, language);
        if (string.IsNullOrWhiteSpace(topicKey))
            return;

        var queries = pass.Queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(query => TruncateForPrompt(CollapseWhitespace(query), 180))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        if (queries.Count == 0)
            return;

        var outcome = ResolveSourceBackedResearchWorkingNoteOutcome(accepted, rejectReason);
        var note = new ToolMemory.ResearchWorkingNote
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TopicKey = topicKey,
            RequestShape = BuildSourceBackedResearchShapeKey(effectiveUserMessage),
            Label = TruncateForPrompt(pass.Label, 80),
            Origin = TruncateForPrompt(pass.Origin ?? "unknown", 48),
            Purpose = TruncateForPrompt(pass.Purpose, 180),
            Queries = queries,
            CategoryScope = TruncateForPrompt(pass.CategoryScope ?? string.Empty, 160),
            DocPath = TruncateForPrompt(pass.DocPath ?? string.Empty, 240),
            PageStart = pass.PageStart,
            PageEnd = pass.PageEnd,
            Outcome = outcome,
            Accepted = accepted,
            RejectReason = rejectReason,
            ReasonBefore = before.Reason,
            ReasonAfter = after?.Reason,
            CandidateCountBefore = before.CandidateCount,
            CandidateCountAfter = after?.CandidateCount,
            CandidateDelta = after is null ? null : after.CandidateCount - before.CandidateCount,
            DistinctPagesBefore = before.DistinctSourcePageCount,
            DistinctPagesAfter = after?.DistinctSourcePageCount,
            DistinctPageDelta = after is null ? null : after.DistinctSourcePageCount - before.DistinctSourcePageCount,
            UsableHitsBefore = before.UsableHitCount,
            UsableHitsAfter = after?.UsableHitCount,
            UsableHitDelta = after is null ? null : after.UsableHitCount - before.UsableHitCount,
            ElapsedMs = Math.Max(0, elapsedMs)
        };

        var notes = _mem.ResearchWorkingNotes;
        notes.RemoveAll(existing =>
            string.Equals(existing.TopicKey, note.TopicKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Label, note.Label, StringComparison.OrdinalIgnoreCase)
            && string.Equals(string.Join("\n", existing.Queries), string.Join("\n", note.Queries), StringComparison.OrdinalIgnoreCase));
        notes.Add(note);
        if (notes.Count > MaxStoredResearchWorkingNotes)
            notes.RemoveRange(0, notes.Count - MaxStoredResearchWorkingNotes);

        EmitRagTrace(
            "research.working_note",
            ("label", note.Label),
            ("outcome", note.Outcome),
            ("accepted", note.Accepted),
            ("query_count", note.Queries.Count),
            ("candidate_delta", note.CandidateDelta),
            ("distinct_page_delta", note.DistinctPageDelta),
            ("reject_reason", note.RejectReason));
    }

    private static string ResolveSourceBackedResearchWorkingNoteOutcome(bool accepted, string? rejectReason)
    {
        if (accepted)
            return "accepted";

        return NormalizeLexicalLookup(rejectReason) switch
        {
            "no_hits" => "rejected_no_hits",
            "no_coverage_gain" => "rejected_no_gain",
            "planning_coverage_regression" => "rejected_regression",
            "error" => "error",
            "" => "rejected",
            _ => "rejected_" + NormalizeLexicalLookup(rejectReason).Replace(' ', '_')
        };
    }
}
