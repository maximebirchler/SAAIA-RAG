using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Backend;

namespace SAAIA.Backend.Retrieval;

/// <summary>Recovers a bounded current-document candidate window without re-embedding the query.</summary>
internal static class DenseActiveCandidateSearch
{
    internal const int MaximumSearchRequests = 3;
    internal const int MaximumExpandedCandidates = 1024;
    internal const string IncompleteReason = "dense_active_plan_candidate_budget_exhausted";

    internal static async Task<List<RagMatch>> ExecuteAsync(HttpClient qdrant, string collection,
        int dimensions, int initialLimit, int desiredActiveCount, Func<int, object> requestBody,
        Func<RagMatch, bool> embeddingCompatible,
        Func<IReadOnlyList<RagMatch>, CancellationToken, Task<List<RagMatch>>> filterCurrent,
        Action<int> status, Action<string, string?>? degraded, CancellationToken ct, ILogger? logger = null)
    {
        using var probe = BackendDiagnostics.Start("retrieval.dense_active_candidates", logger);
        var limit = Math.Max(1, initialLimit);
        // Existing caller limits are retained; expansion has its own absolute ceiling and request cap.
        var maximumLimit = Math.Max(limit, (int)Math.Min(MaximumExpandedCandidates, (long)desiredActiveCount * 8));
        probe.Set("initial_limit", limit);
        probe.Set("maximum_limit", maximumLimit);
        probe.Set("maximum_requests", MaximumSearchRequests);
        probe.Set("requested_active", desiredActiveCount);
        var attempts = 0;
        var rawCount = 0;
        var decodedCount = 0;
        var compatibleCount = 0;
        var activeCount = 0;
        var completedWindowAttempt = 0;
        var collectionEnsured = false;
        // More search requests must not multiply the timeout of the former single HTTP request.
        using var deadline = qdrant.Timeout == Timeout.InfiniteTimeSpan
            ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline?.CancelAfter(qdrant.Timeout);
        var operationCt = deadline?.Token ?? ct;
        try
        {
            while (attempts < MaximumSearchRequests)
            {
                operationCt.ThrowIfCancellationRequested();
                attempts++;
                probe.Set("attempts", attempts);
                probe.Set("final_limit", limit);
                using var content = new StringContent(JsonSerializer.Serialize(requestBody(limit)), Encoding.UTF8, "application/json");
                using var response = await qdrant.PostAsync($"/collections/{Uri.EscapeDataString(collection)}/points/search", content, operationCt);
                status((int)response.StatusCode);
                if (response.StatusCode == HttpStatusCode.NotFound && !collectionEnsured && attempts < MaximumSearchRequests)
                {
                    collectionEnsured = true;
                    await QdrantClient.EnsureCollectionAsync(qdrant, collection, dimensions, operationCt);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("qdrant_dense_search_http_error", null, response.StatusCode);

                await using var stream = await response.Content.ReadAsStreamAsync(operationCt);
                using var document = await ParseResponseAsync(stream, operationCt);
                if (!document.RootElement.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array
                    || results.GetArrayLength() > limit)
                    throw new InvalidDataException("qdrant_dense_search_response_invalid");
                var raw = QdrantClient.ParseSearchResults(document);
                var compatible = raw.Where(embeddingCompatible).ToList();
                // Replace, rather than accumulate, results: SQL may publish a different plan between rounds.
                var active = await filterCurrent(compatible, operationCt);
                rawCount = results.GetArrayLength();
                decodedCount = raw.Count;
                compatibleCount = compatible.Count;
                activeCount = active.Count;
                completedWindowAttempt = attempts;
                SetLastWindow();
                if (activeCount >= desiredActiveCount || rawCount < limit || rawCount == activeCount)
                {
                    probe.Complete();
                    return active;
                }
                if (attempts >= MaximumSearchRequests || limit >= maximumLimit)
                {
                    degraded?.Invoke("dense_qdrant", IncompleteReason);
                    RetrievalTelemetry.RecordRetrieverDegraded("dense_qdrant", new InvalidDataException(IncompleteReason));
                    probe.Complete("degraded", IncompleteReason);
                    return active;
                }
                limit = (int)Math.Min(maximumLimit, (long)limit * 2);
            }
            throw new InvalidOperationException("dense_active_candidate_search_not_completed");
        }
        catch (Exception error)
        {
            SetLastWindow();
            probe.Fail(error);
            throw;
        }

        void SetLastWindow()
        {
            // Counts describe the last successfully decoded window, not a sum of overlapping top-K rounds.
            probe.Set("raw_candidates", rawCount);
            probe.Set("decoded_candidates", decodedCount);
            probe.Set("embedding_compatible_candidates", compatibleCount);
            probe.Set("active_candidates", activeCount);
            probe.Set("rejected_candidates", Math.Max(0, rawCount - activeCount));
            probe.Set("completed_window_attempt", completedWindowAttempt);
        }
    }

    private static async Task<JsonDocument> ParseResponseAsync(Stream stream, CancellationToken ct)
    {
        try { return await JsonDocument.ParseAsync(stream, cancellationToken: ct); }
        catch (JsonException) { throw new InvalidDataException("qdrant_dense_search_response_invalid"); }
    }
}
