using System.Diagnostics;

namespace SAAIA.Backend.Middleware;

/// <summary>
/// Middleware qui :
/// 1. Génère ou récupère X-Request-Id (depuis le header client ou génère un nouveau)
/// 2. Ajoute RequestId à HttpContext.Items
/// 3. Configure les log scopes
/// 4. Ajoute X-Request-Id à la réponse
/// </summary>
public sealed class RequestIdMiddleware
{
    private const string RequestIdHeaderName = "X-Request-Id";
    public const string RequestIdItemKey = "RequestId";
    public const string TenantIdItemKey = "TenantId";
    public const string UserIdItemKey = "UserId";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        // 1. Récupère ou génère le RequestId
        var requestId = ctx.Request.Headers.TryGetValue(RequestIdHeaderName, out var headerValue)
            ? headerValue.ToString()
            : null;

        if (!IsSafeIdentifier(requestId))
        {
            requestId = Activity.Current?.Id ?? ctx.TraceIdentifier ?? Guid.NewGuid().ToString();
        }

        // OpenTelemetry: enrichit l'Activity courante avec notre request_id
        Activity.Current?.SetTag("saaia.request_id", requestId);
        Activity.Current?.SetTag("http.request_id", requestId);
        var turnId = ReadSafeIdentifier(ctx.Request.Headers["X-SAAIA-Turn-Id"].ToString());
        var clientSessionId = ReadSafeIdentifier(ctx.Request.Headers["X-SAAIA-Session-Id"].ToString());
        Activity.Current?.SetTag("saaia.turn_id", turnId);
        Activity.Current?.SetTag("saaia.client_session_id", clientSessionId);

        // 2. Ajoute à Items pour accès dans les endpoints
        ctx.Items[RequestIdItemKey] = requestId;

        // 3. Ajoute à la réponse (même si non authentifié)
        ctx.Response.OnStarting(() =>
        {
            if (!ctx.Response.Headers.ContainsKey(RequestIdHeaderName))
            {
                ctx.Response.Headers[RequestIdHeaderName] = requestId;
            }
            return Task.CompletedTask;
        });

        // 4. Configure les log scopes (tenant_id, user_id si présents après auth)
        using (DiagnosticLogScope.Begin(_logger, new Dictionary<string, object>
        {
            { "request_id", requestId! },
            { "path", ctx.Request.Path.Value ?? "/" },
            { "method", ctx.Request.Method },
            { "turn_id", turnId ?? string.Empty },
            { "client_session_id", clientSessionId ?? string.Empty }
        }))
        {
            using var probe = BackendDiagnostics.Start("http.request", _logger);
            probe.Set("request_id", requestId);
            probe.Set("turn_id", turnId);
            probe.Set("client_session_id", clientSessionId);
            try
            {
                await _next(ctx);
                probe.Complete(ctx.RequestAborted.IsCancellationRequested ? "cancelled"
                    : ctx.Response.StatusCode >= 500 ? "failed"
                    : ctx.Response.StatusCode >= 400 ? "rejected" : "success");
            }
            catch (Exception ex)
            {
                probe.Fail(ex);
                throw;
            }
            finally
            {
                probe.Set("http_status", ctx.Response.StatusCode);
                probe.Set("response_started", ctx.Response.HasStarted);
                DiagnosticLogScope.Write(() => _logger.LogInformation(
                    "Request completed: {Method} {Path} {StatusCode} request_id={RequestId} duration_ms={DurationMs}",
                    ctx.Request.Method, ctx.Request.Path.Value, ctx.Response.StatusCode, requestId,
                    probe.ElapsedMilliseconds));
            }
        }
    }

    internal static string? ReadSafeIdentifier(string? value) => IsSafeIdentifier(value) ? value : null;

    private static bool IsSafeIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 256
           && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}

public static class RequestIdExtensions
{
    public static string GetRequestId(this HttpContext ctx)
        => ctx.Items.TryGetValue(RequestIdMiddleware.RequestIdItemKey, out var val) && val is string s
            ? s
            : ctx.TraceIdentifier;
}
