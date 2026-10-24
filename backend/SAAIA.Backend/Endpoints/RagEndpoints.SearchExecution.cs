using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;
using SAAIA.Backend.Retrieval;

namespace SAAIA.Backend.Endpoints;

public static partial class RagEndpoints
{
    private static RetrievalExecutionContext ExecutionContext(HttpContext context)
        => new(context.GetTenantId(), context.GetRequestId(), context.RequestAborted,
            context.RequestServices.GetService<TeiWorkloadGovernor>(),
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("SAAIA.Backend.Retrieval"));

    internal static Task<RagSearchResponse> SearchCoreAsync(HttpContext context,
        NpgsqlDataSource dataSource, RagOptions options, IHttpClientFactory clients, RagSearchRequestDto request)
    {
        // Translate transport validation here; the retrieval engine itself has no HTTP context.
        if (string.IsNullOrWhiteSpace(request.Query)) throw new BadHttpRequestException("query is required");
        return RetrievalEngine.SearchAsync(ExecutionContext(context), dataSource, options, clients, request);
    }

    private static Task<RagSearchResponseDto> BuildSearchResponseDtoAsync(HttpContext context,
        NpgsqlDataSource dataSource, RagOptions options, IHttpClientFactory clients, RagSearchRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Query)) throw new BadHttpRequestException("query is required");
        return RetrievalResultProjector.BuildSearchResponseDtoAsync(
            ExecutionContext(context), dataSource, options, clients, request);
    }
}
