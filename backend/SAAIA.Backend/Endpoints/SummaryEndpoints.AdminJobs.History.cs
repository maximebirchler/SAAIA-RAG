using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Endpoints;

public static partial class SummaryEndpoints
{
    private static async Task<IResult> DeleteAdminJobHistoryAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        static void CollectIds(JsonElement root, string propertyName, List<Guid> target)
        {
            if (!root.TryGetProperty(propertyName, out var prop))
                return;

            if (prop.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var parsed))
                        target.Add(parsed);
                }
                return;
            }

            if (prop.ValueKind == JsonValueKind.String && Guid.TryParse(prop.GetString(), out var single))
                target.Add(single);
        }

        List<Guid> parsedIds = new();
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var body = await reader.ReadToEndAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    CollectIds(root, "jobIds", parsedIds);
                    CollectIds(root, "ids", parsedIds);
                    CollectIds(root, "jobId", parsedIds);
                }
            }
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid_json" });
        }

        var ids = parsedIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
            return Results.BadRequest(new { error = "job_ids_required" });

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string deleteAdminSql = @"
DELETE FROM admin_jobs
WHERE tenant_id=@tenant
  AND job_id = ANY(@ids)
  AND COALESCE(lower(status),'') NOT IN ('queued','running','paused')
RETURNING job_id;";

        const string deleteIngestionSql = @"
DELETE FROM ingestion_jobs
WHERE tenant_id=@tenant
  AND job_id = ANY(@ids)
  AND COALESCE(lower(status),'') NOT IN ('queued','running','paused')
RETURNING job_id;";

        var deletedSummaryIds = (await conn.QueryAsync<Guid>(
            new CommandDefinition(deleteAdminSql, new { tenant = tenantId, ids }, transaction: tx, cancellationToken: ct))).ToArray();
        var deletedIngestionIds = (await conn.QueryAsync<Guid>(
            new CommandDefinition(deleteIngestionSql, new { tenant = tenantId, ids }, transaction: tx, cancellationToken: ct))).ToArray();

        await tx.CommitAsync(ct);

        var deletedIds = deletedSummaryIds.Concat(deletedIngestionIds)
            .Distinct()
            .ToArray();
        var deletedSummary = deletedSummaryIds.Length;
        var deletedIngestion = deletedIngestionIds.Length;
        var deleted = deletedIds.Length;
        var skippedIds = ids.Except(deletedIds).ToArray();
        var skipped = skippedIds.Length;

        try
        {
            await AuditWriter.WriteAsync(
                conn,
                tenantId,
                actorApiKeyId,
                actorIsAdmin,
                action: "admin.jobs.delete_history",
                target: "admin.jobs",
                payload: new { jobIds = ids, deletedIds, skippedIds, deletedSummary, deletedIngestion, deleted, skipped },
                ip: ctx.Connection.RemoteIpAddress?.ToString(),
                ct: ct);
        }
        catch
        {
        }

        return Results.Ok(new
        {
            requested = ids.Length,
            deleted,
            skipped,
            deletedSummary,
            deletedIngestion,
            deletedIds,
            skippedIds
        });
    }

    private static async Task<IResult> PurgeAdminJobHistoryAsync(HttpContext ctx, NpgsqlDataSource ds, JobPurgeCommand? cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        var scope = (cmd?.Scope ?? "terminal").Trim().ToLowerInvariant();
        var type = string.IsNullOrWhiteSpace(cmd?.Type) ? null : cmd!.Type!.Trim().ToLowerInvariant();

        if (scope is not ("terminal" or "all" or "done" or "failed"))
            return Results.BadRequest(new { error = "invalid_scope", expected = new[] { "terminal", "all", "done", "failed" } });
        if (type is not null && type is not ("summary" or "ingestion"))
            return Results.BadRequest(new { error = "invalid_type", expected = new[] { "summary", "ingestion" } });

        static string BuildStatusFilter(string tableAlias, string scopeValue)
            => scopeValue switch
            {
                "done" => $"COALESCE(lower({tableAlias}.status),'') = 'done'",
                "failed" => $"COALESCE(lower({tableAlias}.status),'') IN ('failed','canceled','cancelled')",
                _ => $"COALESCE(lower({tableAlias}.status),'') NOT IN ('queued','running','paused')"
            };

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var adminDeletedIds = Array.Empty<Guid>();
        var ingestionDeletedIds = Array.Empty<Guid>();

        if (type is null or "summary")
        {
            var adminSql = $"""
DELETE FROM admin_jobs a
WHERE a.tenant_id=@tenant
  AND {BuildStatusFilter("a", scope)}
RETURNING a.job_id;
""";
            adminDeletedIds = (await conn.QueryAsync<Guid>(
                new CommandDefinition(adminSql, new { tenant = tenantId }, transaction: tx, cancellationToken: ct))).ToArray();
        }

        if (type is null or "ingestion")
        {
            var ingestionSql = $"""
DELETE FROM ingestion_jobs i
WHERE i.tenant_id=@tenant
  AND {BuildStatusFilter("i", scope)}
RETURNING i.job_id;
""";
            ingestionDeletedIds = (await conn.QueryAsync<Guid>(
                new CommandDefinition(ingestionSql, new { tenant = tenantId }, transaction: tx, cancellationToken: ct))).ToArray();
        }

        await tx.CommitAsync(ct);

        var deletedIds = adminDeletedIds.Concat(ingestionDeletedIds).Distinct().ToArray();
        var deletedSummary = adminDeletedIds.Length;
        var deletedIngestion = ingestionDeletedIds.Length;
        var deleted = deletedIds.Length;

        try
        {
            await AuditWriter.WriteAsync(
                conn,
                tenantId,
                actorApiKeyId,
                actorIsAdmin,
                action: "admin.jobs.purge",
                target: "admin.jobs",
                payload: new { scope, type, deletedIds, deletedSummary, deletedIngestion, deleted },
                ip: ctx.Connection.RemoteIpAddress?.ToString(),
                ct: ct);
        }
        catch
        {
        }

        return Results.Ok(new
        {
            scope,
            type = type ?? "all",
            deleted,
            deletedSummary,
            deletedIngestion,
            deletedIds
        });
    }
}
