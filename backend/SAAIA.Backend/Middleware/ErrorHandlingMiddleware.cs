using System.Text.Json;
using SAAIA.Backend.Models;
using SAAIA.Backend.Middleware;

namespace SAAIA.Backend;

/// <summary>
/// Exception handling middleware qui formatte les erreurs en { error, requestId }.
/// Doit être placé AVANT les autres middlewares pour capturer toutes les exceptions.
/// </summary>
public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);

            // Si pas encore d'erreur mais statut 401/403/404/429, formate en réponse d'erreur
            if (ctx.Response.StatusCode >= 400 && !ctx.Response.HasStarted)
            {
                await HandleErrorResponseAsync(ctx);
            }
        }
        catch (BadHttpRequestException ex)
        {
            var requestId = ctx.Items.TryGetValue(RequestIdMiddleware.RequestIdItemKey, out var val) && val is string s
                ? s
                : ctx.TraceIdentifier;

            _logger.LogWarning(ex, "BadHttpRequestException: {Message}", ex.Message);

            await WriteErrorResponseAsync(ctx, StatusCodes.Status400BadRequest, ex.Message, requestId);
        }
        catch (UnauthorizedAccessException ex)
        {
            var requestId = ctx.Items.TryGetValue(RequestIdMiddleware.RequestIdItemKey, out var val) && val is string s
                ? s
                : ctx.TraceIdentifier;

            _logger.LogWarning(ex, "UnauthorizedAccessException: {Message}", ex.Message);

            await WriteErrorResponseAsync(ctx, StatusCodes.Status403Forbidden, ex.Message, requestId);
        }
        catch (Exception ex)
        {
            var requestId = ctx.Items.TryGetValue(RequestIdMiddleware.RequestIdItemKey, out var val) && val is string s
                ? s
                : ctx.TraceIdentifier;

            _logger.LogError(ex, "Unhandled exception: {Message}", ex.Message);

            // Ne pas exposer les détails techniques en prod
            var message = ctx.RequestServices.GetService<IHostEnvironment>()?.IsDevelopment() ?? false
                ? ex.ToString()
                : "Internal server error";

            await WriteErrorResponseAsync(ctx, StatusCodes.Status500InternalServerError, message, requestId);
        }
    }

    private static async Task HandleErrorResponseAsync(HttpContext ctx)
    {
        var requestId = ctx.Items.TryGetValue(RequestIdMiddleware.RequestIdItemKey, out var val) && val is string s
            ? s
            : ctx.TraceIdentifier;

        var statusCode = ctx.Response.StatusCode;
        var message = statusCode switch
        {
            StatusCodes.Status400BadRequest => "Bad request",
            StatusCodes.Status401Unauthorized => "Unauthorized",
            StatusCodes.Status403Forbidden => "Forbidden",
            StatusCodes.Status404NotFound => "Not found",
            StatusCodes.Status429TooManyRequests => "Too many requests",
            _ => $"HTTP {statusCode}"
        };

        ctx.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(new ErrorResponse(message, requestId), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await ctx.Response.WriteAsync(json);
    }

    private static async Task WriteErrorResponseAsync(HttpContext ctx, int statusCode, string message, string requestId)
    {
        if (ctx.Response.HasStarted)
        {
            return;
        }

        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers["X-Request-Id"] = requestId;

        var json = JsonSerializer.Serialize(new ErrorResponse(message, requestId), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await ctx.Response.WriteAsync(json);
    }
}
