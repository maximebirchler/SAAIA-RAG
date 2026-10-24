using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;
using SAAIA.Backend.Retrieval;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;

namespace SAAIA.Backend.Endpoints;

public static partial class RagEndpoints
{

    public static void Map(WebApplication app)
    {
        app.MapGet("/rag/categories", CategoriesAsync);
        app.MapPost("/rag/search", SearchAsync);
        app.MapPost("/rag/query", QueryAsync);
        app.MapPost("/admin/rag/test-retrieval", TestRetrievalAsync).RequireAdminKey();
        app.MapGet("/admin/rag/index-presence", IndexPresenceAsync).RequireAdminKey();
        app.MapGet("/rag/debug/scroll", ScrollAsync).RequireAdminKey();
    }


    private static async Task<IResult> CategoriesAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var tenantId = ctx.GetTenantId();
        await using var conn = await ds.OpenConnectionAsync(ctx.RequestAborted);

        const string sql = """
WITH snapshot AS (
  SELECT
    path AS category,
    display_order,
    name
  FROM documents_catalog_categories
  WHERE tenant_id=@tenant_id
    AND path <> ''
    AND position('/' in path) = 0
),
dynamic AS (
  SELECT DISTINCT
    split_part(replace(doc_path, chr(92), '/'), '/', 1) AS category
  FROM documents
  WHERE tenant_id=@tenant_id
    AND status='indexed'
    AND position('/' in replace(doc_path, chr(92), '/')) > 0
)
SELECT category
FROM (
  SELECT category, display_order, name FROM snapshot
  UNION ALL
  SELECT category, 2147483647 AS display_order, category AS name
  FROM dynamic d
  WHERE NOT EXISTS (
    SELECT 1
    FROM snapshot s
    WHERE LOWER(s.category) = LOWER(d.category)
  )
) categories
WHERE category <> ''
ORDER BY display_order, name;
""";

        var cats = (await conn.QueryAsync<string>(
            new CommandDefinition(sql, new { tenant_id = tenantId }, cancellationToken: ctx.RequestAborted)
        )).ToArray();

        return Results.Ok(cats);
    }


    private static async Task<IResult> QueryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchBulkhead searchBulkhead,
        TeiWorkloadGovernor teiGovernor,
        RagSearchRequestDto req)
    {
        using var interactiveTei = teiGovernor.BeginInteractiveRequest();
        using var interactiveRetrieval = RuntimeCapabilityBRagIdleCoordinator.BeginInteractiveRetrieval(
            ctx.RequestServices.GetService<IOptions<RuntimeGovernanceOptions>>()?.Value);
        using var admission = await searchBulkhead.AcquireAsync(ctx.RequestAborted);
        if (admission is null)
            return BuildRagSearchBusyResult(ctx, ragOpt.Value, searchBulkhead);

        AddRagSearchAdmissionHeaders(ctx, admission, searchBulkhead.GetSnapshot());
        var responseDto = await BuildSearchResponseDtoAsync(ctx, ds, ragOpt.Value, httpFactory, req with { IncludeDiagnostics = false });
        return Results.Ok(new
        {
            query = responseDto.Query,
            queryNormalized = responseDto.QueryNormalized,
            category = responseDto.Category,
            topK = responseDto.TopK,
            matches = responseDto.Items,
            metrics = responseDto.Metrics,
            guidance = responseDto.Guidance
        });
    }


    private static async Task<IResult> SearchAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchBulkhead searchBulkhead,
        TeiWorkloadGovernor teiGovernor,
        RagSearchRequestDto req)
    {
        using var interactiveTei = teiGovernor.BeginInteractiveRequest();
        using var interactiveRetrieval = RuntimeCapabilityBRagIdleCoordinator.BeginInteractiveRetrieval(
            ctx.RequestServices.GetService<IOptions<RuntimeGovernanceOptions>>()?.Value);
        using var admission = await searchBulkhead.AcquireAsync(ctx.RequestAborted);
        if (admission is null)
            return BuildRagSearchBusyResult(ctx, ragOpt.Value, searchBulkhead);

        AddRagSearchAdmissionHeaders(ctx, admission, searchBulkhead.GetSnapshot());
        var responseDto = await BuildSearchResponseDtoAsync(ctx, ds, ragOpt.Value, httpFactory, req with { IncludeDiagnostics = false });
        return Results.Ok(responseDto);
    }


    private static async Task<IResult> TestRetrievalAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchBulkhead searchBulkhead,
        TeiWorkloadGovernor teiGovernor,
        RagSearchRequestDto req)
    {
        using var interactiveTei = teiGovernor.BeginInteractiveRequest();
        using var interactiveRetrieval = RuntimeCapabilityBRagIdleCoordinator.BeginInteractiveRetrieval(
            ctx.RequestServices.GetService<IOptions<RuntimeGovernanceOptions>>()?.Value);
        using var admission = await searchBulkhead.AcquireAsync(ctx.RequestAborted);
        if (admission is null)
            return BuildRagSearchBusyResult(ctx, ragOpt.Value, searchBulkhead);

        AddRagSearchAdmissionHeaders(ctx, admission, searchBulkhead.GetSnapshot());
        var responseDto = await BuildSearchResponseDtoAsync(
            ctx,
            ds,
            ragOpt.Value,
            httpFactory,
            req with { IncludeDiagnostics = true });
        return Results.Ok(responseDto);
    }


    private static IResult BuildRagSearchBusyResult(HttpContext ctx, RagOptions rag, RagSearchBulkhead searchBulkhead)
    {
        var snapshot = searchBulkhead.GetSnapshot();
        var retryAfterSeconds = ComputeRagSearchRetryAfterSeconds(rag, snapshot);
        ctx.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        AddRagSearchCapacityHeaders(ctx, snapshot);

        return Results.Json(new
        {
            error = "rag_search_busy",
            requestId = ctx.GetRequestId(),
            retryAfterSeconds,
            active = snapshot.Active,
            queued = snapshot.Queued,
            maxConcurrency = snapshot.MaxConcurrency,
            queueLimit = snapshot.QueueLimit,
            availableSlots = snapshot.AvailableSlots,
            queueWaitTimeoutSeconds = snapshot.QueueWaitTimeoutSeconds,
            detail = "The RAG search service is busy. Retry shortly."
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }


    internal static int ComputeRagSearchRetryAfterSeconds(RagOptions rag, RagSearchBulkheadSnapshot snapshot)
    {
        var configured = Math.Clamp(rag.SearchRetryAfterSeconds, 1, 300);
        var maxConcurrency = Math.Max(1, snapshot.MaxConcurrency);
        var queueRounds = Math.Max(1, (int)Math.Ceiling((snapshot.Queued + 1) / (double)maxConcurrency));
        var estimated = configured * queueRounds;
        var upperBound = Math.Clamp(snapshot.QueueWaitTimeoutSeconds, configured, 300);
        return Math.Clamp(estimated, configured, upperBound);
    }


    private static void AddRagSearchCapacityHeaders(HttpContext ctx, RagSearchBulkheadSnapshot snapshot)
    {
        ctx.Response.Headers["X-SAAIA-RAG-Active"] = snapshot.Active.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Queued"] = snapshot.Queued.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Max-Concurrency"] = snapshot.MaxConcurrency.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Queue-Limit"] = snapshot.QueueLimit.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Available-Slots"] = snapshot.AvailableSlots.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Queue-Wait-Timeout-Seconds"] = snapshot.QueueWaitTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
    }


    private static void AddRagSearchAdmissionHeaders(
        HttpContext ctx,
        RagSearchBulkhead.RagSearchBulkheadLease admission,
        RagSearchBulkheadSnapshot snapshot)
    {
        ctx.Response.Headers["X-SAAIA-RAG-Queue-Wait-Ms"] = admission.WaitMs.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Waited-Queued"] = admission.WaitedQueued ? "true" : "false";
        AddRagSearchCapacityHeaders(ctx, snapshot);
    }


    private static async Task<IResult> ScrollAsync(
        HttpContext ctx,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        int? limit,
        string? cursor,
        Guid? docId,
        string? docPath,
        string? category,
        int? pageStart,
        int? pageEnd,
        string? chunkType,
        string? contentRole)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var rag = ragOpt.Value;

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        docPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        category = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(category);
        category = string.IsNullOrWhiteSpace(category) ? null : category.ToLowerInvariant();
        chunkType = string.IsNullOrWhiteSpace(chunkType) ? null : chunkType.Trim();
        contentRole = string.IsNullOrWhiteSpace(contentRole) ? null : contentRole.Trim();
        var normalizedPageStart = pageStart is > 0 ? pageStart.Value : (int?)null;
        var normalizedPageEnd = pageEnd is > 0 ? pageEnd.Value : normalizedPageStart;
        if (normalizedPageStart is not null && normalizedPageEnd is not null && normalizedPageEnd < normalizedPageStart)
            normalizedPageEnd = normalizedPageStart;

        var must = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };
        if (docId is not null)
            must.Add(new { key = "doc_id", match = new { value = docId.Value.ToString() } });
        if (!string.IsNullOrWhiteSpace(docPath))
            must.Add(new { key = "doc_path", match = new { value = docPath } });
        if (!string.IsNullOrWhiteSpace(category))
            must.Add(new { key = "category", match = new { value = category } });
        if (normalizedPageEnd is not null)
            must.Add(new { key = "page_start", range = new { lte = normalizedPageEnd.Value } });
        if (normalizedPageStart is not null)
            must.Add(new { key = "page_end", range = new { gte = normalizedPageStart.Value } });
        if (!string.IsNullOrWhiteSpace(chunkType))
            must.Add(new { key = "chunk_type", match = new { value = chunkType } });
        if (!string.IsNullOrWhiteSpace(contentRole))
            must.Add(new { key = "content_role", match = new { value = contentRole } });

        var body = new Dictionary<string, object?>
        {
            ["limit"] = Math.Clamp(limit ?? 20, 1, 500),
            ["with_payload"] = true,
            ["with_vector"] = false,
            ["filter"] = new { must = must.ToArray() }
        };
        if (!string.IsNullOrWhiteSpace(cursor))
            body["offset"] = cursor.Trim();

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await qdrant.PostAsync(
            $"/collections/{rag.QdrantCollection}/points/scroll",
            content,
            ctx.RequestAborted);

        if (!resp.IsSuccessStatusCode)
            return Results.Problem($"Qdrant scroll failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var json = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        return Results.Text(json, "application/json; charset=utf-8", Encoding.UTF8);
    }

}
