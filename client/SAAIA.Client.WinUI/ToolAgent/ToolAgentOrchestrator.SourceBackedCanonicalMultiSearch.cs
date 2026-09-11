using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int SourceBackedCanonicalMultiSearchMaxQueries = 12;
    private const int SourceBackedCanonicalMultiSearchMaxParallelism = 2;
    private const double SourceBackedCanonicalMultiSearchRrfK = 60.0;

    private sealed class SourceBackedCanonicalMultiSearchCandidate
    {
        public required JsonElement Hit { get; init; }

        public required string CanonicalKey { get; init; }

        public double ReciprocalRankScore { get; set; }

        public int FirstQueryIndex { get; set; }

        public int BestRank { get; set; }

        public List<SourceBackedCanonicalMultiSearchContribution> Contributions { get; } = new();
    }

    private sealed record SourceBackedCanonicalMultiSearchContribution(
        string Query,
        int QueryIndex,
        int HitRank,
        double Score);

    private sealed record SourceBackedCanonicalMultiSearchRun(
        int QueryIndex,
        string Query,
        JsonElement Result,
        long ElapsedMs,
        bool Busy,
        int RetryAfterSeconds,
        string? Error,
        string[] DegradedRetrievers);

    private async Task<JsonElement> ExecSourceBackedCanonicalMultiSearchAsync(
        JsonElement args,
        CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var queries = ReadSourceBackedCanonicalMultiSearchQueries(args);
        var topK = Math.Clamp(
            args.TryGetProperty("topK", out var topKElement)
            && topKElement.ValueKind == JsonValueKind.Number
            && topKElement.TryGetInt32(out var requestedTopK)
                ? requestedTopK
                : 8,
            1,
            50);
        var categoryScope = GetRagCategoryScopeArg(args);
        var docId = GetRagDocIdArg(args);
        var docPath = GetRagDocPathArg(args);
        var maxPerDoc = GetRagMaxPerDocArg(args);
        var maxPerPage = GetRagMaxPerPageArg(args);
        var pageStart = GetRagPageStartArg(args);
        var pageEnd = GetRagPageEndArg(args);
        var researchMode = GetRagResearchModeArg(args);
        var includeResearchSurfaces = GetRagIncludeResearchSurfacesArg(args);
        var mode = args.TryGetProperty("mode", out var modeElement)
                   && modeElement.ValueKind == JsonValueKind.String
            ? modeElement.GetString()
            : "balanced";

        EmitRagTrace(
            "rag.multi_search.canonical.start",
            ("queries", queries),
            ("top_k", topK),
            ("category", categoryScope),
            ("doc_id", docId),
            ("doc_path", docPath));

        if (queries.Count == 0)
        {
            var empty = JsonSerializer.SerializeToElement(new
            {
                hits = Array.Empty<object>(),
                meta = new
                {
                    queries,
                    mode,
                    category = categoryScope,
                    categoryPath = categoryScope,
                    sourceBackedCanonical = true,
                    fusion = "rrf",
                    rrfK = SourceBackedCanonicalMultiSearchRrfK,
                    queryRuns = Array.Empty<object>()
                }
            });
            RememberLastRagDiagnostics(queries, empty);
            return empty;
        }

        using var parallelism = new SemaphoreSlim(SourceBackedCanonicalMultiSearchMaxParallelism);
        var runs = await Task.WhenAll(queries.Select(async (query, queryIndex) =>
        {
            await parallelism.WaitAsync(ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            try
            {
                JsonElement result;
                try
                {
                    var raw = await _api.RagSearchToolAsync(
                            query,
                            topK,
                            categoryScope,
                            mode,
                            ct,
                            docId,
                            docPath,
                            maxPerDoc,
                            maxPerPage,
                            pageStart,
                            pageEnd,
                            researchMode,
                            includeResearchSurfaces,
                            sourceBackedCanonical: true)
                        .ConfigureAwait(false);
                    result = NormalizeRagHits(raw, sourceBackedCanonical: true);
                }
                catch (ApiClientBackendBusyException ex) when (!ct.IsCancellationRequested)
                {
                    result = BuildRagSearchBusyPayload(new[] { query }, categoryScope, mode, ex);
                }

                var degradedRetrievers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectRagDegradedRetrievers(result, degradedRetrievers);
                return new SourceBackedCanonicalMultiSearchRun(
                    queryIndex,
                    query,
                    result.Clone(),
                    sw.ElapsedMilliseconds,
                    IsRagSearchBusyPayload(result),
                    TryGetInt(result, "retryAfterSeconds") ?? 1,
                    TryGetString(result, "error"),
                    degradedRetrievers.ToArray());
            }
            finally
            {
                parallelism.Release();
            }
        })).ConfigureAwait(false);

        var candidates = new Dictionary<string, SourceBackedCanonicalMultiSearchCandidate>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs.OrderBy(static run => run.QueryIndex))
        {
            if (!run.Result.TryGetProperty("hits", out var hits)
                || hits.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var hitRank = 0;
            foreach (var hit in hits.EnumerateArray())
            {
                var canonicalKey = BuildRagHitDedupeKey(hit);
                var contribution = new SourceBackedCanonicalMultiSearchContribution(
                    run.Query,
                    run.QueryIndex,
                    hitRank,
                    ReadRagHitScore(hit));
                var reciprocalRankScore =
                    1.0 / (SourceBackedCanonicalMultiSearchRrfK + hitRank + 1.0);
                if (!candidates.TryGetValue(canonicalKey, out var candidate))
                {
                    candidate = new SourceBackedCanonicalMultiSearchCandidate
                    {
                        Hit = hit.Clone(),
                        CanonicalKey = canonicalKey,
                        ReciprocalRankScore = reciprocalRankScore,
                        FirstQueryIndex = run.QueryIndex,
                        BestRank = hitRank
                    };
                    candidates.Add(canonicalKey, candidate);
                }
                else
                {
                    candidate.ReciprocalRankScore += reciprocalRankScore;
                    candidate.FirstQueryIndex = Math.Min(candidate.FirstQueryIndex, run.QueryIndex);
                    candidate.BestRank = Math.Min(candidate.BestRank, hitRank);
                }

                candidate.Contributions.Add(contribution);
                hitRank++;
            }
        }

        var selected = candidates.Values
            .OrderByDescending(static candidate => candidate.ReciprocalRankScore)
            .ThenBy(static candidate => candidate.BestRank)
            .ThenBy(static candidate => candidate.FirstQueryIndex)
            .ThenBy(static candidate => candidate.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .Select(AnnotateSourceBackedCanonicalMultiSearchHit)
            .ToArray();
        var busyRuns = runs.Where(static run => run.Busy).ToArray();
        var allBusy = busyRuns.Length == runs.Length;
        var degradedRetrievers = runs
            .SelectMany(static run => run.DegradedRetrievers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static retriever => retriever, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var queryRuns = runs
            .OrderBy(static run => run.QueryIndex)
            .Select(run => new
            {
                query = run.Query,
                queryIndex = run.QueryIndex,
                clientElapsedMs = run.ElapsedMs,
                hitCount = CountRagHits(run.Result),
                error = string.IsNullOrWhiteSpace(run.Error) ? null : run.Error,
                busy = run.Busy ? true : (bool?)null,
                retryAfterSeconds = run.Busy ? run.RetryAfterSeconds : (int?)null,
                degradedRetrievers = run.DegradedRetrievers.Length == 0
                    ? null
                    : run.DegradedRetrievers
            })
            .ToArray();

        var payload = JsonSerializer.SerializeToElement(new
        {
            hits = selected,
            error = allBusy ? "rag_search_busy" : null,
            busy = allBusy ? true : (bool?)null,
            retryAfterSeconds = allBusy
                ? Math.Clamp(busyRuns.Max(static run => run.RetryAfterSeconds), 1, 300)
                : (int?)null,
            guidance = (object?)null,
            meta = new
            {
                queries,
                mode,
                category = categoryScope,
                categoryPath = categoryScope,
                docId,
                docPath,
                pageStart,
                pageEnd,
                maxPerDoc,
                maxPerPage,
                researchMode,
                includeResearchSurfaces,
                sourceBackedCanonical = true,
                fusion = "rrf",
                rrfK = SourceBackedCanonicalMultiSearchRrfK,
                fanoutParallelism = SourceBackedCanonicalMultiSearchMaxParallelism,
                degradedRetrievers = degradedRetrievers.Length == 0 ? null : degradedRetrievers,
                queryRuns
            }
        });

        payload = await EnrichSourceBackedEvidenceWindowsAsync(payload, ct)
            .ConfigureAwait(false);
        RememberLastRagDiagnostics(queries, payload);
        ClientLog.Info(
            "ToolAgent rag.multi_search canonical end: " +
            $"queries={queries.Count}|rawHits={candidates.Count}|hits={selected.Length}|" +
            $"busyRuns={busyRuns.Length}|ms={totalSw.ElapsedMilliseconds}");
        EmitRagTrace(
            "rag.multi_search.canonical.end",
            ("queries", queries.Count),
            ("unique_hits", candidates.Count),
            ("hits", selected.Length),
            ("busy_runs", busyRuns.Length),
            ("ms", totalSw.ElapsedMilliseconds));
        return payload;
    }

    private static IReadOnlyList<string> ReadSourceBackedCanonicalMultiSearchQueries(JsonElement args)
    {
        var queries = new List<string>();
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("queries", out var queryArray)
            && queryArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in queryArray.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    continue;
                AddExactSourceBackedCanonicalQuery(queries, element.GetString());
                if (queries.Count >= SourceBackedCanonicalMultiSearchMaxQueries)
                    break;
            }
        }

        if (queries.Count == 0
            && args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("query", out var query)
            && query.ValueKind == JsonValueKind.String)
        {
            AddExactSourceBackedCanonicalQuery(queries, query.GetString());
        }

        return queries;
    }

    private static void AddExactSourceBackedCanonicalQuery(List<string> queries, string? value)
    {
        var query = value?.Trim();
        if (string.IsNullOrWhiteSpace(query)
            || queries.Contains(query, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        queries.Add(query);
    }

    private static JsonElement AnnotateSourceBackedCanonicalMultiSearchHit(
        SourceBackedCanonicalMultiSearchCandidate candidate)
    {
        if (candidate.Hit.ValueKind != JsonValueKind.Object)
            return candidate.Hit.Clone();

        var payload = candidate.Hit.EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => (object?)property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        payload["multiSearchRrfScore"] = candidate.ReciprocalRankScore;
        // The stored hit comes from the first query contribution. Keep that
        // observed query on the hit while retaining every contribution below.
        payload["retrievalQuery"] = candidate.Contributions[0].Query;
        payload["retrievalQueryContributions"] = candidate.Contributions
            .OrderBy(static contribution => contribution.QueryIndex)
            .ThenBy(static contribution => contribution.HitRank)
            .Select(static contribution => new
            {
                query = contribution.Query,
                queryIndex = contribution.QueryIndex,
                hitRank = contribution.HitRank,
                score = contribution.Score
            })
            .ToArray();
        return JsonSerializer.SerializeToElement(payload);
    }
}
