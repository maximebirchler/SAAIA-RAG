using Dapper;
using Npgsql;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Endpoints;

public static class AdminAuditEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/admin/audit");

        g.MapGet("", ListAsync);
        g.MapGet("/{auditId:guid}", GetAsync);
    }

    // ✅ POCO avec setters -> Dapper OK (évite les soucis de record ctor)
    public sealed class AuditEventDto
    {
        public Guid AuditId { get; set; }
        public DateTime Ts { get; set; } // timestamptz -> DateTime (UTC) par défaut Npgsql
        public Guid? ActorApiKeyId { get; set; }
        public bool ActorIsAdmin { get; set; }
        public string Action { get; set; } = "";
        public string? Target { get; set; }
        public string? PayloadJson { get; set; }
        public string? Ip { get; set; }
    }

    private static async Task<IResult> ListAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? action,
        string? target,
        Guid? actorApiKeyId,
        bool? actorIsAdmin,
        DateTimeOffset? since,
        DateTimeOffset? until,
        int? limit,
        int? offset)
    {
        AdminAuth.EnsureAdmin(ctx);

        var ct = ctx.RequestAborted;
        var tenantId = ctx.GetTenantId();

        var lim = Math.Clamp(limit ?? 200, 1, 1000);
        var off = Math.Max(offset ?? 0, 0);

        var where = new List<string> { "tenant_id=@tenant" };
        var p = new DynamicParameters();
        p.Add("tenant", tenantId);
        p.Add("lim", lim);
        p.Add("off", off);

        if (!string.IsNullOrWhiteSpace(action))
        {
            where.Add(@"""action""=@action"); // action peut être ambigu -> on quote par sécurité
            p.Add("action", action.Trim());
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            where.Add("target ILIKE @target");
            p.Add("target", "%" + target.Trim() + "%");
        }

        if (actorApiKeyId.HasValue)
        {
            where.Add("actor_api_key_id=@actor_api_key_id");
            p.Add("actor_api_key_id", actorApiKeyId.Value);
        }

        if (actorIsAdmin.HasValue)
        {
            where.Add("actor_is_admin=@actor_is_admin");
            p.Add("actor_is_admin", actorIsAdmin.Value);
        }

        if (since.HasValue)
        {
            where.Add("ts >= @since");
            p.Add("since", since.Value.UtcDateTime);
        }

        if (until.HasValue)
        {
            where.Add("ts <= @until");
            p.Add("until", until.Value.UtcDateTime);
        }

        var whereSql = "WHERE " + string.Join(" AND ", where);

        await using var conn = await ds.OpenConnectionAsync(ct);

        var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM audit_events {whereSql};",
            p,
            cancellationToken: ct));

        const string select = @"
SELECT
  audit_id         AS ""AuditId"",
  ts               AS ""Ts"",
  actor_api_key_id AS ""ActorApiKeyId"",
  actor_is_admin   AS ""ActorIsAdmin"",
  ""action""       AS ""Action"",
  target           AS ""Target"",
  payload_json     AS ""PayloadJson"",
  ip               AS ""Ip""
FROM audit_events
";

        var sql = $"{select} {whereSql} ORDER BY ts DESC LIMIT @lim OFFSET @off;";

        var items = (await conn.QueryAsync<AuditEventDto>(
            new CommandDefinition(sql, p, cancellationToken: ct))).ToArray();

        return Results.Ok(new
        {
            items,
            limit = lim,
            offset = off,
            total
        });
    }

    private static async Task<IResult> GetAsync(HttpContext ctx, NpgsqlDataSource ds, Guid auditId)
    {
        AdminAuth.EnsureAdmin(ctx);

        var ct = ctx.RequestAborted;
        var tenantId = ctx.GetTenantId();

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  audit_id         AS ""AuditId"",
  ts               AS ""Ts"",
  actor_api_key_id AS ""ActorApiKeyId"",
  actor_is_admin   AS ""ActorIsAdmin"",
  ""action""       AS ""Action"",
  target           AS ""Target"",
  payload_json     AS ""PayloadJson"",
  ip               AS ""Ip""
FROM audit_events
WHERE tenant_id=@tenant AND audit_id=@id
LIMIT 1;";

        var row = await conn.QueryFirstOrDefaultAsync<AuditEventDto>(
            new CommandDefinition(sql, new { tenant = tenantId, id = auditId }, cancellationToken: ct));

        return row is null
            ? Results.NotFound(new { error = "not found" })
            : Results.Ok(row);
    }
}
