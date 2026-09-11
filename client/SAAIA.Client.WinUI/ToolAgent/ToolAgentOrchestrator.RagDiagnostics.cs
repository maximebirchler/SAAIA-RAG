using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool HasRagBusyQueries(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("meta", out var meta)
            || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("busyQueries", out var busyQueries)
            || busyQueries.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return busyQueries.EnumerateArray().Any();
    }

    private static bool IsRagSearchBusyPayload(JsonElement value)
        => value.ValueKind == JsonValueKind.Object
           && ((value.TryGetProperty("busy", out var busy)
                    && busy.ValueKind is JsonValueKind.True)
               || (value.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.String
                   && string.Equals(error.GetString(), "rag_search_busy", StringComparison.OrdinalIgnoreCase)));

    private static void CollectRagDegradedRetrievers(JsonElement normalizedRagResult, ISet<string> degradedRetrievers)
    {
        if (degradedRetrievers is null)
            return;

        var meta = TryGetObject(normalizedRagResult, "meta")
                   ?? TryGetObject(normalizedRagResult, "Meta");
        if (!meta.HasValue)
            return;

        CollectRagDegradedRetrieversFromMeta(meta.Value, degradedRetrievers);

        var queryRuns = TryGetArray(meta.Value, "queryRuns")
                        ?? TryGetArray(meta.Value, "QueryRuns")
                        ?? TryGetArray(meta.Value, "query_runs");
        if (!queryRuns.HasValue)
            return;

        foreach (var run in queryRuns.Value.EnumerateArray())
        {
            if (run.ValueKind != JsonValueKind.Object)
                continue;

            var runMeta = TryGetObject(run, "meta")
                          ?? TryGetObject(run, "Meta");
            if (runMeta.HasValue)
                CollectRagDegradedRetrieversFromMeta(runMeta.Value, degradedRetrievers);
        }
    }

    private static void CollectRagDegradedRetrieversFromMeta(JsonElement meta, ISet<string> degradedRetrievers)
    {
        var directValues = TryGetArray(meta, "degradedRetrievers")
                           ?? TryGetArray(meta, "DegradedRetrievers")
                           ?? TryGetArray(meta, "degraded_retrievers");
        AddRagDegradedRetrieverValues(directValues, degradedRetrievers);

        var metrics = TryGetObject(meta, "metrics")
                      ?? TryGetObject(meta, "Metrics");
        if (!metrics.HasValue)
            return;

        var values = TryGetArray(metrics.Value, "degradedRetrievers")
                     ?? TryGetArray(metrics.Value, "DegradedRetrievers")
                     ?? TryGetArray(metrics.Value, "degraded_retrievers");
        AddRagDegradedRetrieverValues(values, degradedRetrievers);
    }

    private static void AddRagDegradedRetrieverValues(JsonElement? values, ISet<string> degradedRetrievers)
    {
        if (!values.HasValue || values.Value.ValueKind != JsonValueKind.Array)
            return;

        foreach (var value in values.Value.EnumerateArray())
        {
            var retriever = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString();
            if (!string.IsNullOrWhiteSpace(retriever))
                degradedRetrievers.Add(retriever.Trim());
        }
    }

    private static bool ShouldPreferMultiSearchGuidance(string? currentBehavior, string? candidateBehavior)
        => GuidancePriority(candidateBehavior) > GuidancePriority(currentBehavior);

    private static int GuidancePriority(string? behavior)
        => (behavior ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "ask_clarification" => 3,
            "answer_with_caveat" => 2,
            "" => 0,
            _ => 1
        };

    private void RememberLastRagDiagnostics(IEnumerable<string> queries, JsonElement result)
    {
        _mem.LastRagQueries = queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var degradedRetrievers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (result.ValueKind == JsonValueKind.Object)
            CollectRagDegradedRetrievers(result, degradedRetrievers);
        _mem.LastRagDegradedRetrievers = degradedRetrievers
            .Take(16)
            .ToList();

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("hits", out var hits)
            || hits.ValueKind != JsonValueKind.Array)
        {
            _mem.LastRagHitLabels = new();
            return;
        }

        _mem.LastRagHitLabels = hits.EnumerateArray()
            .Where(static hit => hit.ValueKind == JsonValueKind.Object)
            .Take(30)
            .Select(FormatRagDiagnosticHitLabel)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .ToList();
    }

    private static string FormatRagDiagnosticHitLabel(JsonElement hit)
    {
        var summary = BuildRagHitSummary(hit);
        var docName = string.IsNullOrWhiteSpace(summary.DocName)
            ? Path.GetFileName(summary.DocPath ?? string.Empty)
            : summary.DocName;
        var pageStart = summary.PageStart;
        var pageEnd = summary.PageEnd;
        var score = summary.Score;
        var scoreText = score.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var role = string.IsNullOrWhiteSpace(summary.SelectionHintRole) ? "unknown" : summary.SelectionHintRole;
        var cardCount = summary.MatchedContentCards?.Count ?? 0;
        var factCount = ExtractContentCardEvidenceFacts(new[] { summary }).Length;
        var rankText = summary.RetrievalHitRank.HasValue
            ? $" r={summary.RetrievalHitRank.Value}"
            : string.Empty;
        var queryText = summary.RetrievalQueryIndex.HasValue
            ? $" q={summary.RetrievalQueryIndex.Value}"
            : string.Empty;
        return $"{docName} {SourceBackedPagePrefix(string.Empty)}{pageStart}{(pageEnd != pageStart ? $"-{pageEnd}" : string.Empty)} score={scoreText} role={role} table={summary.HasTable.ToString().ToLowerInvariant()} cards={cardCount} facts={factCount}{queryText}{rankText}";
    }
}
