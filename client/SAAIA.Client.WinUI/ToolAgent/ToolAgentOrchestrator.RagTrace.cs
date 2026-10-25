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
            _ragTraceId = ClientDiagnostics.Journal.CurrentTraceId ?? "rag-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
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
        var explicitOutcome = fields.FirstOrDefault(field => field.Key is "outcome" or "status").Value as string;
        var outcome = explicitOutcome is "failed" or "error" or "invalid_result" ? "failed"
            : explicitOutcome is "cancelled" or "canceled" ? "cancelled"
            : fields.Any(field => (field.Key is "ok" or "success") && field.Value is false)
                || eventName.Contains(".error", StringComparison.OrdinalIgnoreCase)
                || eventName.Contains(".failed", StringComparison.OrdinalIgnoreCase) ? "failed"
            : eventName.Contains(".cancel", StringComparison.OrdinalIgnoreCase) ? "cancelled"
            : eventName.Contains(".retry", StringComparison.OrdinalIgnoreCase)
                || eventName.Contains(".reject", StringComparison.OrdinalIgnoreCase) ? "degraded" : "success";
        var reason = fields.FirstOrDefault(field => field.Key == "reason").Value as string;
        ClientDiagnostics.Journal.Emit("agent." + eventName, outcome,
            reason: IsSafeMetadataTraceValue(reason) ? reason : null, measurements: fields
            .Where(field => field.Value is int or long)
            .GroupBy(field => field.Key).ToDictionary(group => group.Key, group => (long?)Convert.ToInt64(group.Last().Value)),
            identifiers: fields.Where(field => (IsSourceTraceMetadataField(field.Key)
                    || ClientRoutingTracePolicy.Allows(field.Key, field.Value))
                    && field.Value is string && IsSafeMetadataTraceValue(field.Value))
                .GroupBy(field => field.Key).ToDictionary(group => group.Key, group => (string)group.Last().Value!));
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

            if (!ClientDiagnostics.IncludeContent && value is not (null or bool or int or long or double or float or decimal or TimeSpan)
                && ((!IsMetadataTraceField(safeKey) && !ClientRoutingTracePolicy.Allows(safeKey, value)) || !IsSafeMetadataTraceValue(value)))
                continue;

            sb.Append(' ');
            sb.Append(safeKey);
            sb.Append('=');
            var maximumStringCharacters =
                safeKey.Equals(
                    "source_fields",
                    StringComparison.OrdinalIgnoreCase)
                || safeKey.Equals(
                    "route_arguments",
                    StringComparison.OrdinalIgnoreCase)
                    ? 1200
                    : DefaultRagTraceValueChars;
            sb.Append(FormatRagTraceFieldValue(
                value,
                maximumStringCharacters));
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static bool IsMetadataTraceField(string key)
        => key.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
           || IsSourceTraceMetadataField(key)
           || key is "intent" or "kind" or "outcome" or "status" or "reason" or "reject_reason"
               or "reason_before" or "reason_after" or "answer_source" or "sources_payload"
               or "trace_path" or "trace_step" or "tool" or "tools" or "route" or "mode";

    private static bool IsSourceTraceMetadataField(string key)
        => key is "source_event" or "source_step" or "source_status" or "source_reason";

    private static bool IsSafeMetadataTraceValue(object? value)
        => value switch
        {
            null => true,
            string text => Regex.IsMatch(text, @"^[A-Za-z0-9_.:\-]{1,120}$"),
            IEnumerable<string> values => values.All(static text => Regex.IsMatch(text, @"^[A-Za-z0-9_.:\-]{1,120}$")),
            _ => false
        };

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

    private static string FormatRagTraceFieldValue(
        object? value,
        int maximumStringCharacters)
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
            string s => JsonSerializer.Serialize(FormatRagTraceValue(
                s,
                maximumStringCharacters)),
            IEnumerable<string> strings => JsonSerializer.Serialize(strings
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => FormatRagTraceValue(item, 120))
                .Take(16)
                .ToArray()),
            IEnumerable enumerable => JsonSerializer.Serialize(FormatRagTraceEnumerable(enumerable)),
            _ => JsonSerializer.Serialize(FormatRagTraceValue(
                value.ToString(),
                maximumStringCharacters))
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
        var normalizedQuery = query ?? string.Empty;
        List<RagHitSummary> sourcePages;
        try
        {
            var diagnosticCandidateLimit = Math.Clamp(Math.Max(analysis.MinimumCandidateCount, 24), 12, 48);
            var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(normalizedQuery);
            sourcePages = EnumerateRagHitSummaries(toolResults)
                .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
                .Where(static hit => !LooksLikePageReferenceOnlyHit(hit))
                .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, normalizedQuery))
                .Where(hit => !strictStructuredPlanning
                    || ShouldExposeHitForStrictSourceBackedPlanningInventory(hit, normalizedQuery))
                .OrderByDescending(static hit => BackendSelectionHintsPreferUsableEvidence(hit) ? 1 : 0)
                .ThenByDescending(static hit => ComputeSourceBackedEvidenceRichnessScore(hit))
                .ThenByDescending(static hit => hit.Score)
                .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .Take(diagnosticCandidateLimit)
                .ToList();
        }
        catch
        {
            sourcePages = new List<RagHitSummary>();
        }

        var candidateMs = candidateSw.ElapsedMilliseconds;
        var titles = sourcePages
            .Select(hit => BuildSourceBackedResearchInventoryHitTitle(hit, normalizedQuery))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(title => TruncateForPrompt(title, 120))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        var distinctCandidatePageCount = sourcePages
            .Select(BuildRagHitVisiblePageMergeKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var previewSw = Stopwatch.StartNew();
        var preview = new List<string>();
        try
        {
            foreach (var hit in sourcePages.Take(8))
            {
                var title = BuildSourceBackedResearchInventoryHitTitle(hit, normalizedQuery);
                AddSourceBackedCandidateLeadLine(preview, "source_page", title, hit, language);
            }

            if (preview.Count == 0)
            {
                foreach (var hit in EnumerateRagHitSummaries(toolResults)
                    .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
                    .Where(static hit => !LooksLikePageReferenceOnlyHit(hit))
                    .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, normalizedQuery))
                    .Where(hit => !ShouldGateStructuredSourceBackedPlanningCoverage(normalizedQuery)
                        || ShouldExposeHitForStrictSourceBackedPlanningInventory(hit, normalizedQuery))
                    .OrderByDescending(static hit => ComputeSourceBackedEvidenceRichnessScore(hit))
                    .ThenByDescending(static hit => hit.Score)
                    .Take(8))
                {
                    var title = ExtractReadablePartialPlanningLeadTitle(hit, normalizedQuery);
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
            sourcePages.Count,
            distinctCandidatePageCount,
            candidateMs,
            previewSw.ElapsedMilliseconds);
    }

    private static string BuildSourceBackedResearchInventoryHitTitle(RagHitSummary hit, string query)
    {
        if (ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return BuildSourceBackedPlanningRuntimeTraceTitle(hit, query);

        var title = ExtractReadablePartialPlanningLeadTitle(hit, query);
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        title = CollapseWhitespace(hit.SectionTitle ?? hit.HeadingPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        title = hit.MatchedContentCards?
            .Select(static card => CollapseWhitespace(card.Title))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        title = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 100);
        return string.IsNullOrWhiteSpace(title)
            ? "source-backed context"
            : title;
    }
}
