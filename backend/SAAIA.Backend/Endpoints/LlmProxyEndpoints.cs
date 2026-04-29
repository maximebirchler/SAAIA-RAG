using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Middleware;

namespace SAAIA.Backend.Endpoints;

public static class LlmProxyEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/llm/v1/chat/completions", ChatCompletionsAsync);
        app.MapGet("/llm/v1/models", ModelsAsync);
    }

    private static async Task<IResult> ModelsAsync(
        HttpContext ctx,
        IHttpClientFactory httpFactory,
        IOptions<ChatOptions> chatOptions)
    {
        if (!IsConfigured(chatOptions.Value))
            return Results.Json(new { error = "llm_not_configured", requestId = ctx.GetRequestId() }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var llm = httpFactory.CreateClient("llm");
        using var upstream = await llm.GetAsync("v1/models", HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        return await ProxyResponseAsync(ctx, upstream);
    }

    private static async Task<IResult> ChatCompletionsAsync(
        HttpContext ctx,
        IHttpClientFactory httpFactory,
        IOptions<ChatOptions> chatOptions,
        RuntimeLlmCapacityPlanService capacityPlanService,
        RuntimeLlmQueueManager queueManager)
    {
        if (!IsConfigured(chatOptions.Value))
            return Results.Json(new { error = "llm_not_configured", requestId = ctx.GetRequestId() }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var capacityPlan = await capacityPlanService.GetQueuePlanAsync(ctx.RequestAborted).ConfigureAwait(false);
        using var lease = await queueManager
            .AcquireOrQueueAsync(GetUserQueueKey(ctx), capacityPlan, TimeSpan.FromSeconds(30), ctx.RequestAborted)
            .ConfigureAwait(false);

        if (lease is null)
        {
            ctx.Response.Headers.RetryAfter = "5";
            return Results.Json(new { error = "llm_queue_full", requestId = ctx.GetRequestId(), retryAfterSeconds = 5 }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StreamContent(ctx.Request.Body)
        };

        upstreamRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(ctx.Request.ContentType) ? "application/json" : ctx.Request.ContentType);

        var llm = httpFactory.CreateClient("llm");
        using var upstream = await llm.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted)
            .ConfigureAwait(false);

        return await ProxyResponseAsync(ctx, upstream).ConfigureAwait(false);
    }

    private static bool IsConfigured(ChatOptions chat)
        => !string.IsNullOrWhiteSpace(chat.LlmBaseUrl)
            && !string.IsNullOrWhiteSpace(chat.LlmModel);

    private static string GetUserQueueKey(HttpContext ctx)
        => ctx.GetApiKeyIdOrNull()?.ToString("N")
            ?? ctx.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

    private static async Task<IResult> ProxyResponseAsync(HttpContext ctx, HttpResponseMessage upstream)
    {
        ctx.Response.StatusCode = (int)upstream.StatusCode;

        foreach (var header in upstream.Headers)
            ctx.Response.Headers[header.Key] = header.Value.ToArray();

        foreach (var header in upstream.Content.Headers)
            ctx.Response.Headers[header.Key] = header.Value.ToArray();

        ctx.Response.Headers.Remove("transfer-encoding");

        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
        await upstreamStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);

        return Results.Empty;
    }
}
