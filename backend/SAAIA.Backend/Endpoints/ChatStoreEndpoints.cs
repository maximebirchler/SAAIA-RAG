using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Endpoints;

public static class ChatStoreEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/chat");

        g.MapPost("/sessions", CreateSessionAsync);
        g.MapGet("/sessions", ListSessionsAsync);
        g.MapGet("/sessions/{sessionId:guid}", GetSessionAsync);
        g.MapPatch("/sessions/{sessionId:guid}", UpdateSessionAsync);
        g.MapDelete("/sessions/{sessionId:guid}", DeleteSessionAsync);

        g.MapPost("/sessions/{sessionId:guid}/messages", AddMessageAsync);
        g.MapGet("/sessions/{sessionId:guid}/messages", ListMessagesAsync);
    }

    public sealed record CreateSessionRequest(string? Title = null, string? ClientUser = null);
    public sealed record CreateSessionResponse(Guid SessionId, string? Title, string? ClientUser, DateTimeOffset CreatedAtUtc);

    private static async Task<IResult> CreateSessionAsync(HttpContext ctx, NpgsqlDataSource ds, CreateSessionRequest req)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var sessionId = Guid.NewGuid();
        var title = NormalizeTitle(req.Title);
        var clientUser = NormalizeSmall(req.ClientUser, 80);

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
INSERT INTO chat_sessions(tenant_id, session_id, title, client_user, created_at, updated_at, last_message_at)
VALUES (@tenant, @sid, @title, @client_user, now(), now(), NULL);";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant = tenantId,
            sid = sessionId,
            title,
            client_user = clientUser
        }, cancellationToken: ct));

        return Results.Ok(new CreateSessionResponse(sessionId, title, clientUser, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> ListSessionsAsync(HttpContext ctx, NpgsqlDataSource ds, int? limit, int? offset)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var lim = Math.Clamp(limit ?? 50, 1, 200);
        var off = Math.Max(0, offset ?? 0);

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  session_id     AS ""SessionId"",
  title          AS ""Title"",
  client_user    AS ""ClientUser"",
  created_at     AS ""CreatedAt"",
  updated_at     AS ""UpdatedAt"",
  last_message_at AS ""LastMessageAt""
FROM chat_sessions
WHERE tenant_id=@tenant
ORDER BY updated_at DESC
LIMIT @lim OFFSET @off;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, lim, off });
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetSessionAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  session_id     AS ""SessionId"",
  title          AS ""Title"",
  client_user    AS ""ClientUser"",
  created_at     AS ""CreatedAt"",
  updated_at     AS ""UpdatedAt"",
  last_message_at AS ""LastMessageAt""
FROM chat_sessions
WHERE tenant_id=@tenant AND session_id=@sid;";

        var row = await conn.QueryFirstOrDefaultAsync(sql, new { tenant = tenantId, sid = sessionId });
        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    public sealed record UpdateSessionRequest(string? Title = null, string? ClientUser = null);

    private static async Task<IResult> UpdateSessionAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId, UpdateSessionRequest req)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var title = NormalizeTitle(req.Title);
        var clientUser = NormalizeSmall(req.ClientUser, 80);

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
UPDATE chat_sessions
SET title = COALESCE(@title, title),
    client_user = COALESCE(@client_user, client_user),
    updated_at = now()
WHERE tenant_id=@tenant AND session_id=@sid;";

        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant = tenantId,
            sid = sessionId,
            title,
            client_user = clientUser
        }, cancellationToken: ct));

        return n == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
    }

    private static async Task<IResult> DeleteSessionAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
DELETE FROM chat_sessions
WHERE tenant_id=@tenant AND session_id=@sid;";

        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { tenant = tenantId, sid = sessionId }, cancellationToken: ct));
        return n == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
    }

    public sealed record AddMessageRequest(string Role, string Content, object? Sources = null);

    public sealed record AddMessageResponse(Guid MessageId, DateTimeOffset CreatedAtUtc);

    private static async Task<IResult> AddMessageAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId, AddMessageRequest req)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var role = (req.Role ?? "").Trim().ToLowerInvariant();
        if (role is not ("system" or "user" or "assistant" or "tool"))
            return Results.BadRequest(new { error = "role must be one of: system, user, assistant, tool" });

        var content = (req.Content ?? "").Trim();
        if (content.Length == 0)
            return Results.BadRequest(new { error = "content is required" });

        string? sourcesJson = null;
        if (req.Sources is not null)
        {
            sourcesJson = JsonSerializer.Serialize(req.Sources);
        }

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Ensure session exists
        const string check = @"SELECT 1 FROM chat_sessions WHERE tenant_id=@tenant AND session_id=@sid;";
        var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(check, new { tenant = tenantId, sid = sessionId }, tx, cancellationToken: ct));
        if (exists is null)
        {
            await tx.RollbackAsync(ct);
            return Results.NotFound(new { error = "session not found" });
        }

        var messageId = Guid.NewGuid();

        const string ins = @"
INSERT INTO chat_messages(tenant_id, session_id, message_id, role, content, sources_json, created_at)
VALUES (@tenant, @sid, @mid, @role, @content, CASE WHEN @sources_json IS NULL THEN NULL ELSE @sources_json::jsonb END, now());";

        await conn.ExecuteAsync(new CommandDefinition(ins, new
        {
            tenant = tenantId,
            sid = sessionId,
            mid = messageId,
            role,
            content,
            sources_json = sourcesJson
        }, tx, cancellationToken: ct));

        const string upd = @"
UPDATE chat_sessions
SET updated_at = now(),
    last_message_at = now()
WHERE tenant_id=@tenant AND session_id=@sid;";

        await conn.ExecuteAsync(new CommandDefinition(upd, new { tenant = tenantId, sid = sessionId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        return Results.Ok(new AddMessageResponse(messageId, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> ListMessagesAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId, int? limit)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var lim = Math.Clamp(limit ?? 200, 1, 1000);

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  message_id  AS ""MessageId"",
  role        AS ""Role"",
  content     AS ""Content"",
  sources_json AS ""Sources"",
  created_at  AS ""CreatedAt""
FROM chat_messages
WHERE tenant_id=@tenant AND session_id=@sid
ORDER BY created_at ASC
LIMIT @lim;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, sid = sessionId, lim });
        return Results.Ok(rows);
    }

    private static string? NormalizeTitle(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.Length > 120) s = s[..120];
        return s;
    }

    private static string? NormalizeSmall(string? s, int max)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.Length > max) s = s[..max];
        return s;
    }
}
