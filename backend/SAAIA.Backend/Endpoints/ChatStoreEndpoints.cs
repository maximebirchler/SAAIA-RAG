using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Endpoints;

public static class ChatStoreEndpoints
{
    // ✅ Logger tag non-static (évite CS0718 sur ILogger<ChatStoreEndpoints>)
    private sealed class LogTag { }

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

        // CDC v2.7 aliases (compat + simplicité côté client)
        // POST /chat/messages?sessionId=...  (body: userId, role, content, sourcesJson)
        // GET  /chat/messages?sessionId=...&userId=...
        g.MapPost("/messages", AddMessageCdcAsync);
        g.MapGet("/messages", ListMessagesCdcAsync);
    }

    // -------------------------
    // CDC aliases
    // -------------------------
    private static Task<IResult> AddMessageCdcAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        ILogger<LogTag> log,
        Guid sessionId,
        string? userId,
        ChatMessageCreateRequestDto req)
        => AddMessageAsync(ctx, ds, log, sessionId, userId, req);

    private static Task<IResult> ListMessagesCdcAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid sessionId,
        string? userId,
        int? limit)
        => ListMessagesAsync(ctx, ds, sessionId, userId, limit);

    public sealed record CreateSessionRequest(string? Title = null, string? ClientUser = null);
    public sealed record CreateSessionResponse(Guid SessionId, string? Title, string? ClientUser, DateTimeOffset CreatedAtUtc);

    private static async Task<IResult> CreateSessionAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        ILogger<LogTag> log,
        ChatSessionCreateRequestDto req)
    {
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        // userId obligatoire (CDC v2.7)
        if (string.IsNullOrWhiteSpace(req.UserId))
            throw new BadHttpRequestException("userId is required");

        using var _scope = log.BeginScope(new Dictionary<string, object> { { "user_id", req.UserId } });

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

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "chat.session.create",
            target: sessionId.ToString(),
            payload: new { userId = req.UserId, title, clientUser },
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(new CreateSessionResponse(sessionId, title, clientUser, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> ListSessionsAsync(HttpContext ctx, NpgsqlDataSource ds, string? userId, int? limit, int? offset)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        // userId obligatoire (CDC v2.7)
        if (string.IsNullOrWhiteSpace(userId))
            throw new BadHttpRequestException("userId query parameter is required");

        var lim = Math.Clamp(limit ?? 200, 1, 1000);
        var off = Math.Max(offset ?? 0, 0);

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  session_id      AS ""SessionId"",
  title           AS ""Title"",
  client_user     AS ""ClientUser"",
  created_at      AS ""CreatedAt"",
  updated_at      AS ""UpdatedAt"",
  last_message_at AS ""LastMessageAt""
FROM chat_sessions
WHERE tenant_id=@tenant AND user_id=@user_id
ORDER BY COALESCE(last_message_at, created_at) DESC
LIMIT @lim OFFSET @off;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, user_id = userId, lim, off });
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetSessionAsync(HttpContext ctx, NpgsqlDataSource ds, Guid sessionId, string? userId)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        // userId obligatoire (CDC v2.7)
        if (string.IsNullOrWhiteSpace(userId))
            throw new BadHttpRequestException("userId query parameter is required");

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  session_id      AS ""SessionId"",
  title           AS ""Title"",
  client_user     AS ""ClientUser"",
  created_at      AS ""CreatedAt"",
  updated_at      AS ""UpdatedAt"",
  last_message_at AS ""LastMessageAt""
FROM chat_sessions
WHERE tenant_id=@tenant AND session_id=@sid AND user_id=@user_id
LIMIT 1;";

        var row = await conn.QueryFirstOrDefaultAsync(sql, new { tenant = tenantId, sid = sessionId, user_id = userId });
        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    public sealed record UpdateSessionRequest(string? Title);

    private static async Task<IResult> UpdateSessionAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        ILogger<LogTag> log,
        Guid sessionId,
        string? userId,
        UpdateSessionRequest req)
    {
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        if (string.IsNullOrWhiteSpace(userId))
            throw new BadHttpRequestException("userId query parameter is required");

        using var _scope = log.BeginScope(new Dictionary<string, object> { { "user_id", userId } });

        var title = NormalizeTitle(req.Title);

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
UPDATE chat_sessions
SET title=@title, updated_at=now()
WHERE tenant_id=@tenant AND session_id=@sid AND user_id=@user_id;";

        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new { tenant = tenantId, sid = sessionId, user_id = userId, title }, cancellationToken: ct));
        if (n == 0) return Results.NotFound();

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "chat.session.update",
            target: sessionId.ToString(),
            payload: new { userId, title },
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(new { sessionId, title });
    }

    private static async Task<IResult> DeleteSessionAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        ILogger<LogTag> log,
        Guid sessionId,
        string? userId)
    {
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        if (string.IsNullOrWhiteSpace(userId))
            throw new BadHttpRequestException("userId query parameter is required");

        using var _scope = log.BeginScope(new Dictionary<string, object> { { "user_id", userId } });

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string delMsg = @"
DELETE FROM chat_messages
WHERE tenant_id=@tenant AND session_id=@sid
  AND EXISTS (SELECT 1 FROM chat_sessions s WHERE s.tenant_id=@tenant AND s.session_id=@sid AND s.user_id=@user_id);";

        await conn.ExecuteAsync(new CommandDefinition(delMsg, new { tenant = tenantId, sid = sessionId, user_id = userId }, cancellationToken: ct));

        const string delSess = @"
DELETE FROM chat_sessions
WHERE tenant_id=@tenant AND session_id=@sid AND user_id=@user_id;";

        var n = await conn.ExecuteAsync(new CommandDefinition(delSess, new { tenant = tenantId, sid = sessionId, user_id = userId }, cancellationToken: ct));
        if (n == 0) return Results.NotFound();

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "chat.session.delete",
            target: sessionId.ToString(),
            payload: new { userId },
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(new { sessionId });
    }

    private static async Task<IResult> AddMessageAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        ILogger<LogTag> log,
        Guid sessionId,
        string? userId,
        ChatMessageCreateRequestDto req)
    {
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        // userId obligatoire (CDC v2.7) - on accepte dans body (preferred) ou query
        var effectiveUserId = !string.IsNullOrWhiteSpace(req.UserId) ? req.UserId : userId;
        if (string.IsNullOrWhiteSpace(effectiveUserId))
            throw new BadHttpRequestException("userId is required (body or query)");

        using var _scope = log.BeginScope(new Dictionary<string, object> { { "user_id", effectiveUserId } });

        var role = (req.Role ?? "").Trim().ToLowerInvariant();
        if (role is not ("system" or "user" or "assistant" or "tool"))
            throw new BadHttpRequestException("role must be system|user|assistant|tool");

        var content = (req.Content ?? "").Trim();
        if (content.Length == 0)
            throw new BadHttpRequestException("content is required");

        if (content.Length > 200000)
            throw new BadHttpRequestException("content too large");

        var sourcesJson = string.IsNullOrWhiteSpace(req.SourcesJson) ? null : req.SourcesJson;

        // Best-effort validation JSON (ne casse pas si invalid => stocke quand même)
        if (sourcesJson is not null)
        {
            try { JsonDocument.Parse(sourcesJson); }
            catch { /* ignore */ }
        }

        var messageId = Guid.NewGuid();

        await using var conn = await ds.OpenConnectionAsync(ct);

        // 1) Vérifie session appartient à l'user
        const string existsSql = @"
SELECT 1
FROM chat_sessions
WHERE tenant_id=@tenant AND session_id=@sid AND user_id=@user_id
LIMIT 1;";

        var ok = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(existsSql, new { tenant = tenantId, sid = sessionId, user_id = effectiveUserId }, cancellationToken: ct));
        if (!ok.HasValue)
            return Results.NotFound(new { error = "session not found" });

        // 2) Insert message
        const string insSql = @"
INSERT INTO chat_messages(tenant_id, session_id, message_id, role, content, sources_json, created_at)
VALUES (@tenant, @sid, @mid, @role, @content,
        CASE WHEN @sources_json IS NULL THEN NULL ELSE @sources_json::jsonb END,
        now());";

        await conn.ExecuteAsync(new CommandDefinition(insSql, new
        {
            tenant = tenantId,
            sid = sessionId,
            mid = messageId,
            role,
            content,
            sources_json = sourcesJson
        }, cancellationToken: ct));


        // 3) Update last_message_at
        const string upd = @"
UPDATE chat_sessions
SET last_message_at = now(), updated_at = now()
WHERE tenant_id=@tenant AND session_id=@sid AND user_id=@user_id;";

        await conn.ExecuteAsync(new CommandDefinition(upd, new { tenant = tenantId, sid = sessionId, user_id = effectiveUserId }, cancellationToken: ct));

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "chat.message.add",
            target: $"{sessionId}:{messageId}",
            payload: new { userId = effectiveUserId, sessionId, messageId, role, hasSources = sourcesJson is not null, contentChars = content.Length },
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(new ChatMessageDto(messageId, role, content, sourcesJson, DateTimeOffset.UtcNow));
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
  m.message_id   AS ""MessageId"",
  m.role         AS ""Role"",
  m.content      AS ""Content"",
  m.sources_json AS ""Sources"",
  m.created_at   AS ""CreatedAt""
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
