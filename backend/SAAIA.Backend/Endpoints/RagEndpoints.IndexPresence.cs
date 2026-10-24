using Npgsql;
using SAAIA.Backend.Retrieval;

namespace SAAIA.Backend.Endpoints;

public static partial class RagEndpoints
{
    private static async Task<IResult> IndexPresenceAsync(HttpContext context, NpgsqlDataSource dataSource,
        Guid docId, Guid? revisionId, int? physicalPage, Guid? chunkId)
    {
        if (physicalPage is <= 0) return Results.BadRequest(new { error = "physical_page_must_be_positive" });
        return Results.Ok(await CanonicalIndexProbe.CheckAsync(ExecutionContext(context), dataSource,
            docId, revisionId, physicalPage, chunkId).ConfigureAwait(false));
    }
}
