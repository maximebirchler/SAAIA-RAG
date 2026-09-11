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
        g.MapPatch("/messages/{messageId:guid}", PatchMessageAsync);
        g.MapGet("/messages/{messageId:guid}/tracking", GetMessageTrackingAsync);
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

    private static async Task<IResult> GetMessageTrackingAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid messageId,
        string? userId)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        if (string.IsNullOrWhiteSpace(userId))
            throw new BadHttpRequestException("userId query parameter is required");

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string msgSql = @"
SELECT
  m.tracking_meta_json::text AS ""TrackingMetaJson"",
  m.content                 AS ""Content"",
  m.status_note             AS ""StatusNote"",
  m.progress_text           AS ""ProgressText""
FROM chat_messages m
INNER JOIN chat_sessions s ON m.tenant_id=s.tenant_id AND m.session_id=s.session_id
WHERE m.tenant_id=@tenant AND m.message_id=@mid AND s.user_id=@user_id
LIMIT 1;";

        var messageRow = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(msgSql, new { tenant = tenantId, mid = messageId, user_id = userId }, cancellationToken: ct));
        if (messageRow is null)
            return Results.NotFound(new { error = "message_not_found", messageId });

        string? trackingMetaJson = messageRow.TrackingMetaJson;
        string? jobIdRaw = null;
        string? trackedJobType = null;
        string? metaDocId = null;
        string? metaDocPath = null;
        string? metaStatus = null;
        string? metaPhase = null;
        int? metaCurrent = null;
        int? metaTotal = null;
        int? metaPercent = null;
        DateTimeOffset? metaStartedAt = null;
        DateTimeOffset? metaSnapshotAt = null;
        bool metaTerminal = false;

        try
        {
            if (!string.IsNullOrWhiteSpace(trackingMetaJson))
            {
                using var doc = JsonDocument.Parse(trackingMetaJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "jobId", out var el) && el.ValueKind == JsonValueKind.String)
                        jobIdRaw = el.GetString();
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "jobType", out el) && el.ValueKind == JsonValueKind.String)
                        trackedJobType = el.GetString();
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "docId", out el) && el.ValueKind == JsonValueKind.String)
                        metaDocId = el.GetString();
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "docPath", out el) && el.ValueKind == JsonValueKind.String)
                        metaDocPath = el.GetString();
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "lastKnownStatus", out el) && el.ValueKind == JsonValueKind.String)
                        metaStatus = el.GetString();
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "lastKnownProgressPhase", out el) && el.ValueKind == JsonValueKind.String)
                        metaPhase = el.GetString();
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "lastKnownProgressCurrent", out el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var current))
                        metaCurrent = current;
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "lastKnownProgressTotal", out el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var total))
                        metaTotal = total;
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "lastKnownProgressPercent", out el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var percent))
                        metaPercent = percent;
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "startedAtUtc", out el) && el.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(el.GetString(), out var startedAt))
                        metaStartedAt = startedAt;
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "lastSnapshotAtUtc", out el) && el.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(el.GetString(), out var snapshotAt))
                        metaSnapshotAt = snapshotAt;
                    if (TryGetPropertyIgnoreCase(doc.RootElement, "isTerminal", out el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        metaTerminal = el.GetBoolean();
                }
            }
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(jobIdRaw) || !Guid.TryParse(jobIdRaw, out var jobId))
            return Results.NotFound(new { error = "tracking_not_configured", messageId });

        const string jobSql = @"
SELECT * FROM (
  SELECT
    job_id       AS ""JobId"",
    'summary'    AS ""Type"",
    job_type     AS ""JobType"",
    status       AS ""Status"",
    doc_id       AS ""DocId"",
    COALESCE(payload ->> 'docPath', doc_path) AS ""DocPath"",
    level        AS ""Level"",
    last_error   AS ""LastError"",
    created_at   AS ""CreatedAt"",
    started_at   AS ""StartedAt"",
    finished_at  AS ""FinishedAt"",
    NULL::text   AS ""ProgressPhase"",
    NULL::int    AS ""ProgressCurrent"",
    NULL::int    AS ""ProgressTotal"",
    NULL::int    AS ""ProgressPercent""
  FROM admin_jobs
  WHERE tenant_id=@tenant AND job_id=@jobId

  UNION ALL

  SELECT
    job_id       AS ""JobId"",
    'ingestion'  AS ""Type"",
    action       AS ""JobType"",
    CASE
      WHEN finished_at IS NOT NULL AND status='done' THEN 'done'
      WHEN status='queued'
           AND finished_at IS NULL
           AND (
             payload #>> '{progress,phase}' IS NOT NULL
             OR jsonb_typeof(payload->'progress'->'current')='number'
             OR jsonb_typeof(payload->'progress'->'total')='number'
             OR jsonb_typeof(payload->'progress'->'percent')='number'
           )
        THEN 'running'
      ELSE status
    END AS ""Status"",
    CASE WHEN jsonb_typeof(payload->'docId')='string' THEN (payload->>'docId')::uuid ELSE NULL::uuid END AS ""DocId"",
    doc_path     AS ""DocPath"",
    NULL::text   AS ""Level"",
    last_error   AS ""LastError"",
    created_at   AS ""CreatedAt"",
    started_at   AS ""StartedAt"",
    finished_at  AS ""FinishedAt"",
    payload #>> '{progress,phase}' AS ""ProgressPhase"",
    CASE WHEN jsonb_typeof(payload->'progress'->'current')='number' THEN (payload->'progress'->>'current')::int ELSE NULL END AS ""ProgressCurrent"",
    CASE WHEN jsonb_typeof(payload->'progress'->'total')='number' THEN (payload->'progress'->>'total')::int ELSE NULL END AS ""ProgressTotal"",
    CASE WHEN jsonb_typeof(payload->'progress'->'percent')='number' THEN (payload->'progress'->>'percent')::int ELSE NULL END AS ""ProgressPercent""
  FROM ingestion_jobs
  WHERE tenant_id=@tenant AND job_id=@jobId
) j
WHERE (@trackedType IS NULL OR lower(""Type"")=@trackedType)
LIMIT 1;";

        var row = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(jobSql, new
        {
            tenant = tenantId,
            jobId,
            trackedType = string.IsNullOrWhiteSpace(trackedJobType) ? null : trackedJobType.Trim().ToLowerInvariant()
        }, cancellationToken: ct));

        if (row is not null)
        {
            var status = (string?)row.Status;
            var isTerminal = !string.IsNullOrWhiteSpace(status)
                && (status.Equals("done", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("success", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("error", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase));

            return Results.Ok(new
            {
                row.JobId,
                row.Type,
                row.JobType,
                row.Status,
                DocId = row.DocId ?? metaDocId,
                DocPath = row.DocPath ?? metaDocPath,
                row.Level,
                row.LastError,
                row.CreatedAt,
                StartedAt = row.StartedAt ?? metaStartedAt,
                row.FinishedAt,
                row.ProgressPhase,
                row.ProgressCurrent,
                row.ProgressTotal,
                row.ProgressPercent,
                IsTerminal = isTerminal
            });
        }

        if (string.IsNullOrWhiteSpace(metaStatus) && !metaTerminal)
            return Results.NotFound(new { error = "job_not_found", messageId, jobId });

        return Results.Ok(new
        {
            JobId = jobId,
            Type = string.IsNullOrWhiteSpace(trackedJobType) ? "ingestion" : trackedJobType,
            JobType = string.IsNullOrWhiteSpace(trackedJobType) ? "ingestion" : trackedJobType,
            Status = string.IsNullOrWhiteSpace(metaStatus) ? (metaTerminal ? "done" : "running") : metaStatus,
            DocId = metaDocId,
            DocPath = metaDocPath,
            Level = (string?)null,
            LastError = (string?)null,
            CreatedAt = metaStartedAt ?? metaSnapshotAt,
            StartedAt = metaStartedAt,
            FinishedAt = metaTerminal ? metaSnapshotAt : (DateTimeOffset?)null,
            ProgressPhase = metaPhase,
            ProgressCurrent = metaCurrent,
            ProgressTotal = metaTotal,
            ProgressPercent = metaPercent,
            IsTerminal = metaTerminal
        });
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static async Task<IResult> PatchMessageAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        ILogger<LogTag> log,
        Guid messageId,
        string? userId,
        JsonElement req)
    {
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        var effectiveUserId = userId;
        if (TryReadOptionalString(req, "userId", out var bodyUserId) && !string.IsNullOrWhiteSpace(bodyUserId))
            effectiveUserId = bodyUserId;
        if (string.IsNullOrWhiteSpace(effectiveUserId))
            throw new BadHttpRequestException("userId is required (body or query)");

        var hasContent = TryReadOptionalString(req, "content", out var contentRaw);
        var hasStatusNote = TryReadOptionalString(req, "statusNote", out var statusNoteRaw);
        var hasProgressText = TryReadOptionalString(req, "progressText", out var progressTextRaw);
        string? trackingMetaRaw = null;
        var hasTrackingMeta = TryReadOptionalJsonString(req, "trackingMetaJson", out trackingMetaRaw);
        if (!hasTrackingMeta)
            hasTrackingMeta = TryReadOptionalJsonString(req, "trackingMeta", out trackingMetaRaw);

        if (!hasContent && !hasStatusNote && !hasProgressText && !hasTrackingMeta)
            throw new BadHttpRequestException("at least one patch field is required");

        var content = hasContent ? NormalizeContent(contentRaw) : null;
        var statusNote = hasStatusNote ? NormalizeSmall(statusNoteRaw, 200) : null;
        var progressText = hasProgressText ? NormalizeSmall(progressTextRaw, 240) : null;
        var trackingMetaJson = hasTrackingMeta ? NormalizeJsonText(trackingMetaRaw, log) : null;

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
UPDATE chat_messages m
SET content = CASE WHEN @has_content THEN @content ELSE m.content END,
    status_note = CASE WHEN @has_status_note THEN @status_note ELSE m.status_note END,
    progress_text = CASE WHEN @has_progress_text THEN @progress_text ELSE m.progress_text END,
    tracking_meta_json = CASE WHEN @has_tracking_meta THEN CASE WHEN @tracking_meta_json IS NULL THEN NULL ELSE @tracking_meta_json::jsonb END ELSE m.tracking_meta_json END
FROM chat_sessions s
WHERE m.tenant_id=@tenant
  AND m.message_id=@mid
  AND s.tenant_id=m.tenant_id
  AND s.session_id=m.session_id
  AND s.user_id=@user_id;";

        var n = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant = tenantId,
            mid = messageId,
            user_id = effectiveUserId,
            has_content = hasContent,
            content,
            has_status_note = hasStatusNote,
            status_note = statusNote,
            has_progress_text = hasProgressText,
            progress_text = progressText,
            has_tracking_meta = hasTrackingMeta,
            tracking_meta_json = trackingMetaJson
        }, cancellationToken: ct));

        if (n == 0)
            return Results.NotFound(new { error = "message not found" });

        const string readSql = @"
SELECT
  m.message_id         AS ""MessageId"",
  m.role               AS ""Role"",
  m.content            AS ""Content"",
  m.sources_json       AS ""SourcesJson"",
  m.status_note        AS ""StatusNote"",
  m.progress_text      AS ""ProgressText"",
  m.tracking_meta_json AS ""TrackingMetaJson"",
  m.created_at         AS ""CreatedAt""
FROM chat_messages m
INNER JOIN chat_sessions s ON m.tenant_id=s.tenant_id AND m.session_id=s.session_id
WHERE m.tenant_id=@tenant AND m.message_id=@mid AND s.user_id=@user_id
LIMIT 1;";

        var row = await conn.QueryFirstOrDefaultAsync<StoredChatMessage>(new CommandDefinition(readSql, new { tenant = tenantId, mid = messageId, user_id = effectiveUserId }, cancellationToken: ct));
        if (row is null)
            return Results.NotFound(new { error = "message not found" });

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "chat.message.patch",
            target: messageId.ToString(),
            payload: new { userId = effectiveUserId, hasContent, hasStatusNote, hasProgressText, hasTrackingMeta },
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(new ChatMessageDto(
            row.MessageId,
            row.Role,
            row.Content,
            row.SourcesJson,
            new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            row.StatusNote,
            row.ProgressText,
            row.TrackingMetaJson));
    }

    // Npgsql reads timestamptz as UTC DateTime; keep the database projection
    // separate from the public DTO constructor and its DateTimeOffset timestamp.
    private sealed record StoredChatMessage(
        Guid MessageId,
        string Role,
        string Content,
        string? SourcesJson,
        string? StatusNote,
        string? ProgressText,
        string? TrackingMetaJson,
        DateTime CreatedAt);

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

        // Petit label UX optionnel (ex: "Génération interrompue.")
        var statusNote = NormalizeSmall(req.StatusNote, 200);
        var progressText = NormalizeSmall(req.ProgressText, 240);
        var trackingMetaJson = NormalizeJsonText(req.TrackingMetaJson, log);

        // On accepte un message vide uniquement si un statusNote est fourni (cas: annulation/erreur)
        if (content.Length == 0 && string.IsNullOrWhiteSpace(statusNote))
            throw new BadHttpRequestException("content is required (or statusNote)");

        if (content.Length > 200000)
            throw new BadHttpRequestException("content too large");

        var sourcesJson = string.IsNullOrWhiteSpace(req.SourcesJson) ? null : req.SourcesJson;

        // Best-effort validation JSON (ne casse pas si invalid => stocke quand même)
        if (sourcesJson is not null)
        {
            try { JsonDocument.Parse(sourcesJson); }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "ChatStore: invalid sourcesJson received for session {SessionId}; storing raw payload.", sessionId);
            }
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
INSERT INTO chat_messages(tenant_id, session_id, message_id, role, content, sources_json, status_note, progress_text, tracking_meta_json, created_at)
VALUES (@tenant, @sid, @mid, @role, @content,
        CASE WHEN @sources_json IS NULL THEN NULL ELSE @sources_json::jsonb END,
        @status_note,
        @progress_text,
        CASE WHEN @tracking_meta_json IS NULL THEN NULL ELSE @tracking_meta_json::jsonb END,
        now());";

        await conn.ExecuteAsync(new CommandDefinition(insSql, new
        {
            tenant = tenantId,
            sid = sessionId,
            mid = messageId,
            role,
            content,
            sources_json = sourcesJson,
            status_note = statusNote,
            progress_text = progressText,
            tracking_meta_json = trackingMetaJson
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
            payload: new { userId = effectiveUserId, sessionId, messageId, role, hasSources = sourcesJson is not null, hasStatusNote = statusNote is not null, hasProgressText = progressText is not null, hasTrackingMeta = trackingMetaJson is not null, contentChars = content.Length },
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(new ChatMessageDto(messageId, role, content, sourcesJson, DateTimeOffset.UtcNow, statusNote, progressText, trackingMetaJson));
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
  m.message_id         AS ""MessageId"",
  m.role               AS ""Role"",
  m.content            AS ""Content"",
  m.sources_json       AS ""Sources"",
  m.status_note        AS ""StatusNote"",
  m.progress_text      AS ""ProgressText"",
  m.tracking_meta_json AS ""TrackingMetaJson"",
  m.created_at         AS ""CreatedAt""
FROM chat_messages m
INNER JOIN chat_sessions s ON m.tenant_id = s.tenant_id AND m.session_id = s.session_id
WHERE m.tenant_id=@tenant AND m.session_id=@sid AND s.user_id=@user_id
ORDER BY m.created_at ASC
LIMIT @lim;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, sid = sessionId, user_id = userId, lim });
        return Results.Ok(rows);
    }

    private static string? NormalizeContent(string? s)
    {
        s = (s ?? string.Empty).Trim();
        if (s.Length == 0) return string.Empty;
        if (s.Length > 200000)
            throw new BadHttpRequestException("content too large");
        return s;
    }

    private static string? NormalizeJsonText(string? s, ILogger<LogTag>? log = null)
    {
        s = (s ?? string.Empty).Trim();
        if (s.Length == 0) return null;
        try { JsonDocument.Parse(s); }
        catch (JsonException ex)
        {
            log?.LogWarning(ex, "ChatStore: invalid JSON side payload preserved as raw text.");
        }
        return s;
    }

    private static bool TryReadOptionalString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var prop))
            return false;

        value = prop.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Undefined => null,
            _ => prop.GetRawText()
        };
        return true;
    }

    private static bool TryReadOptionalJsonString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var prop))
            return false;

        value = prop.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => prop.GetRawText(),
            JsonValueKind.Undefined => null,
            _ => prop.GetRawText()
        };
        return true;
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
