using Npgsql;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalEngine
{
    /// <summary>Common execution boundary for both canonical and interactive searches.</summary>
    internal static async Task<RagSearchResponse> SearchAsync(
        RetrievalExecutionContext context,
        NpgsqlDataSource dataSource,
        RagOptions options,
        IHttpClientFactory httpClientFactory,
        RagSearchRequestDto request)
    {
        var mode = request.SourceBackedCanonical == true ? "source_backed_canonical" : request.Mode ?? "balanced";
        using var operation = BackendDiagnostics.Start("retrieval.search", context.Logger);
        operation.Set("request_id", context.RequestId);
        operation.Set("canonical", request.SourceBackedCanonical == true);
        operation.Set("query_characters", request.Query?.Length ?? 0);
        operation.Set("requested_top_k", request.TopK);
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(request.Query))
                throw new ArgumentException("query is required", nameof(request));
            var response = request.SourceBackedCanonical == true
                ? await CanonicalRetrieval.SearchSourceBackedCanonicalCoreAsync(
                    context, dataSource, options, httpClientFactory, request).ConfigureAwait(false)
                : await SearchInteractiveAsync(context, dataSource, options, httpClientFactory, request).ConfigureAwait(false);
            operation.Set("returned", response.Matches.Count);
            operation.Set("degraded_retrievers", response.DegradedRetrievers?.Count ?? 0);
            operation.Complete(response.DegradedRetrievers?.Count > 0 ? "degraded" : "success");
            return response;
        }
        catch (Exception error)
        {
            operation.Fail(error);
            RetrievalTelemetry.RecordFailedSearch(mode,
                !string.IsNullOrWhiteSpace(request.Category) || !string.IsNullOrWhiteSpace(request.CategoryPath),
                !string.IsNullOrWhiteSpace(request.DocId) || !string.IsNullOrWhiteSpace(request.DocPath),
                operation.ElapsedMilliseconds, error);
            throw;
        }
    }
}
