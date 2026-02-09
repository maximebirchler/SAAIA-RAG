using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;

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

    private static async Task<IResult> CreateSessionAsync(HttpContext ctx, NpgsqlDataSource ds, ChatSessionCreateRequestDto req)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        // userId obligatoire (CDC v2.7)
        if (string.IsNullOrWhiteSpace(req.UserId))
            throw new BadHttpRequestException("userId is required");

        var sessionId = Guid.NewGuid();
        var title = NormalizeTitle(req.Title);
        var clientUser = NormalizeSmall(req.ClientUser, 80);

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
INSERT INTO chat_sessions(tenant_id, user_id, session_id, title, client_user, created_at, updated_at, last_message_at)
VALUES (@tenant, @user_id, @sid, @title, @client_user, now(), now(), NULL);";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant = tenantId,
            user_id = req.UserId,
            sid = sessionId,
            title,
            client_user = clientUser
        }, cancellationToken: ct));

        return Results.Ok(new CreateSessionResponse(sessionId, title, clientUser, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> ListSessionsAsync(HttpContext ctx, NpgsqlDataSource ds, string? userId, int? limit, int? offset)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        // userId obligatoire (CDC v2.7)
        if (string.IsNullOrWhiteSpace(userId))
            throw new BadHttpRequestException("userId query parameter is required");

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
WHERE tenant_id=@tenant AND user_id=@user_id
ORDER BY updated_at DESC
LIMIT @lim OFFSET @off;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, user_id = userId, lim, off });
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetSessionAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId, string? userId)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        // userId obligatoire pour validation (CDC v2.7)
        if (string.IsNullOrWhiteSpace(userId))
            throw new BadHttpRequestException("userId query parameter is required");

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
WHERE tenant_id=@tenant AND user_id=@user_id AND session_id=@sid;";

        var row = await conn.QueryFirstOrDefaultAsync(sql, new { tenant = tenantId, user_id = userId, sid = sessionId });
        return row is null ? Results.NotFound() : Results.Ok(row);
    }
    public sealed record UpdateSessionRequest(string? Title = null, string? ClientUser = null);

    private static async Task<IResult> UpdateSessionAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId, string? userId, UpdateSessionRequest req)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        // userId obligatoire (CDC v2.7)
        if (string.IsNullOrWhiteSpace(userId))
            throw new BadHttpRequestException("userId query parameter is required");

        var title = NormalizeTitle(req.Title);
        var clientUser = NormalizeSmall(req.ClientUser, 80);

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
UPDATE chat_sessions
SET title = COALESCE(@title, title),
    client_user = COALESCE(@client_user, client_user),
    updated_at = now()
WHERE tenant_id=@tenant AND user_id=@user_id AND session_id=@sid;";

        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant = tenantId,
            user_id = userId,
            sid = sessionId,
            title,
            client_user = clientUser
        }, cancellationToken: ct));

        return n == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
    }

    private static async Task<IResult> DeleteSessionAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId, string? userId)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        // userId obligatoire (CDC v2.7)
        if (string.IsNullOrWhiteSpace(userId))
            throw new BadHttpRequestException("userId query parameter is required");

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
DELETE FROM chat_sessions
WHERE tenant_id=@tenant AND user_id=@user_id AND session_id=@sid;";

        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { tenant = tenantId, user_id = userId, sid = sessionId }, cancellationToken: ct));
        return n == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
    }

    public sealed record AddMessageRequest(string Role, string Content, object? Sources = null);

    public sealed record AddMessageResponse(Guid MessageId, DateTimeOffset CreatedAtUtc);

    private static async Task<IResult> AddMessageAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId, ChatMessageCreateRequestDto req)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        // userId obligatoire (CDC v2.7)
        if (string.IsNullOrWhiteSpace(req.UserId))
            throw new BadHttpRequestException("userId is required");

        var role = (req.Role ?? "").Trim().ToLowerInvariant();
        if (role is not ("system" or "user" or "assistant" or "tool"))
            return Results.BadRequest(new { error = "role must be one of: system, user, assistant, tool" });

        var content = (req.Content ?? "").Trim();
        if (content.Length == 0)
            return Results.BadRequest(new { error = "content is required" });

        string? sourcesJson = req.SourcesJson;

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Ensure session exists et user_id matches (CDC v2.7)
        const string check = @"SELECT 1 FROM chat_sessions WHERE tenant_id=@tenant AND session_id=@sid AND user_id=@user_id;";
        var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(check, new { tenant = tenantId, sid = sessionId, user_id = req.UserId }, tx, cancellationToken: ct));
        if (exists is null)
        {
            await tx.RollbackAsync(ct);
            return Results.NotFound(new { error = "session not found or user not authorized" });
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

    private static async Task<IResult> ListMessagesAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId, string? userId, int? limit)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        // userId obligatoire (CDC v2.7)
        if (string.IsNullOrWhiteSpace(userId))
            throw new BadHttpRequestException("userId query parameter is required");

        var lim = Math.Clamp(limit ?? 200, 1, 1000);

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  m.message_id  AS ""MessageId"",
  m.role        AS ""Role"",
  m.content     AS ""Content"",
  m.sources_json AS ""Sources"",
  m.created_at  AS ""CreatedAt""
FROM chat_messages m
INNER JOIN chat_sessions s ON m.tenant_id = s.tenant_id AND m.session_id = s.session_id
WHERE m.tenant_id=@tenant AND m.session_id=@sid AND s.user_id=@user_id
ORDER BY m.created_at ASC
LIMIT @lim;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, sid = sessionId, user_id = userId, lim });
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
