using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using SAAIA.Backend.Auth;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Backend.Chat;

public sealed class ChatStreamEndpoint
{
    private static readonly JsonSerializerOptions JsonOpt = new(JsonSerializerDefaults.Web);
    private static readonly Regex CitationRegex = new(@"\[S\d+\]", RegexOptions.Compiled);

    public static void Map(WebApplication app)
    {
        app.MapPost("/chat/stream", HandleAsync);
    }

    // IMPORTANT: doit rester private (sinon CS0051 avec RagOptions/RagRetriever internal)
    private static async Task<IResult> HandleAsync(
        HttpContext ctx,
        IOptions<RagOptions> ragOpt,
        IOptions<ChatOptions> chatOpt,
        RagRetriever retriever,
        ChatPromptBuilder promptBuilder,
        LlmClient llm,
        ChatLimiter limiter,
        ILogger<ChatStreamEndpoint> log,
        ChatStreamRequest req)
    {
        req ??= new ChatStreamRequest("", "general", 5, null);

        // Defensive: if the client didn't send a query (binding failed or empty body),
        // return 400 instead of continuing with an empty query and returning
        // the default/degraded message which is confusing for callers.
        if (string.IsNullOrWhiteSpace(req.Query))
        {
            log.LogWarning("Empty ChatStreamRequest.Query received (Content-Type={ContentType}, Length={ContentLength})", ctx.Request.ContentType, ctx.Request.ContentLength);
            return Results.BadRequest(new { error = "Missing or empty 'query' in request body" });
        }

        var o = chatOpt.Value;

        // Limiteur
        var lease = await limiter.TryAcquireAsync(ctx.RequestAborted);
        if (lease is null)
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        await using var leaseDispose = lease;

        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";
        ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";

        // --- Retrieval ---
        var hits = new List<RagHit>();
        RagHitSelection.Info? selInfo = null;

        if (req.TopK > 0)
        {
            try
            {
                // TenantId injecté par ApiKeyAuthMiddleware
                var tenantId = ctx.Items.TryGetValue(ApiKeyAuth.TenantIdItemKey, out var v) && v is Guid g
                    ? g
                    : Guid.Empty;

                var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category;

                // candidateK = topK * multiplier (puis déduplication par similarité)
                var requestedTopK = Math.Max(0, req.TopK);
                var mult = Math.Clamp(o.RetrievalCandidateMultiplier, 1, 10);
                var candidateK = requestedTopK * mult;

                // clamp côté config Rag (évite un "limit" absurde)
                var maxTopK = Math.Max(1, ragOpt.Value.MaxTopK);
                candidateK = Math.Clamp(candidateK, requestedTopK, maxTopK);

                // ✅ signature: (tenantId, query, category, topK, ct)
                var candidates = await retriever.RetrieveAsync(tenantId, req.Query, category, candidateK, ctx.RequestAborted);

                // Sélection finale: évite les quasi-doublons, sans interdire les chunks adjacents
                var selection = RagHitSelection.Select(candidates, requestedTopK, o);
                hits = selection.Selected;
                selInfo = selection.SelectionInfo;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Retriever failed, continuing without sources.");
                hits = new List<RagHit>();
                selInfo = null;
            }
        }

        // meta
        var meta = new
        {
            type = "meta",
            query = req.Query,
            category = req.Category,
            topK = req.TopK,
            count = hits.Count,
            retrieval = selInfo is null ? null : new
            {
                requestedTopK = selInfo.RequestedTopK,
                candidateK = selInfo.CandidateK,
                selectedK = selInfo.SelectedK,
                confidence = selInfo.Confidence,
                bestScore = selInfo.BestScore,
                scoreGap = selInfo.ScoreGap
            },
            sources = hits.Select((h, i) => new
            {
                sourceId = $"S{i + 1}",
                score = h.Score,
                docPath = h.DocPath,
                docName = h.DocName,
                pageStart = h.PageStart,
                pageEnd = h.PageEnd,
                chunkIndex = h.ChunkIndex
            }).ToList()
        };

        await WriteEventAsync(ctx, "meta", meta, ctx.RequestAborted);

        // Ping keepalive
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!pingCts.IsCancellationRequested)
                {
                    var pingEvery = Math.Clamp(o.PingIntervalSeconds, 2, 60);
                    await Task.Delay(TimeSpan.FromSeconds(pingEvery), pingCts.Token);
                    await ctx.Response.WriteAsync(": ping\n\n", pingCts.Token);
                    await ctx.Response.Body.FlushAsync(pingCts.Token);
                }
            }
            catch { /* ignore */ }
        });

        // Timeouts: on ne veut PAS dépendre du HttpClient.Timeout (doit être infini)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.LlmTimeoutSeconds)));

        // “premier token” timeout
        using var firstTokenCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        if (o.LlmFirstTokenTimeoutSeconds > 0)
            firstTokenCts.CancelAfter(TimeSpan.FromSeconds(o.LlmFirstTokenTimeoutSeconds));

        var confidence = selInfo?.Confidence;

        // Build system prompt (instructions + sources). Allow explicit override via req.SystemPrompt.
        var builtSystem = promptBuilder.BuildSystemPrompt(hits, req.Query, confidence);
        var systemPrompt = string.IsNullOrWhiteSpace(req.SystemPrompt) ? builtSystem : req.SystemPrompt;

        var userMessage = req.Query ?? string.Empty;

        // Debug: log the built system prompt so we can verify the model actually received
        // the RAG instructions + sources. Truncate in logs to avoid excessive output.
        try
        {
            var len = systemPrompt?.Length ?? 0;
            var preview = systemPrompt is null ? "(null)" : (systemPrompt.Length > 2000 ? systemPrompt.Substring(0, 2000) + "…(truncated)" : systemPrompt);
            log.LogInformation("Built system prompt (len={Len}):\n{Preview}", len, preview);
        }
        catch { /* never fail the request because of logging */ }

        var full = new StringBuilder();
        var buf = new StringBuilder();
        var gotFirstToken = false;

        try
        {
            // systemPrompt envoyé en rôle system au LLM (override)
            await foreach (var chunk in llm.StreamAsync(userMessage ?? string.Empty, o, systemPrompt, firstTokenCts.Token))
            {
                if (!gotFirstToken)
                {
                    gotFirstToken = true;
                    // désactive le timer “first token”
                    firstTokenCts.CancelAfter(Timeout.InfiniteTimeSpan);

                    // Log the first chunk returned by the LLM (truncated) to help
                    // diagnose irrelevant/default responses.
                    try
                    {
                        var preview = chunk.Length > 400 ? chunk.Substring(0, 400) + "…(truncated)" : chunk;
                        log.LogInformation("LLM first chunk: {Preview}", preview);
                    }
                    catch { }
                }

                full.Append(chunk);
                buf.Append(chunk);

                if (ShouldFlush(buf, o.StreamFlushChars))
                {
                    var s = buf.ToString();
                    buf.Clear();
                    await WriteEventAsync(ctx, "delta", new { type = "delta", content = s }, timeoutCts.Token);
                }
            }

            if (buf.Length > 0)
            {
                var s = buf.ToString();
                buf.Clear();
                await WriteEventAsync(ctx, "delta", new { type = "delta", content = s }, timeoutCts.Token);
            }

            // Append citations if missing at the end (RAG only)
            if (req.TopK > 0 && hits.Count > 0 && o.AppendCitationsAtEnd)
            {
                var tail = Tail(full.ToString(), 120);
                if (!CitationRegex.IsMatch(tail))
                {
                    var citations = " " + string.Join(" ", hits.Take(o.PromptMaxSources).Select((_, i) => $"[S{i + 1}]"));
                    await WriteEventAsync(ctx, "delta", new { type = "delta", content = citations }, timeoutCts.Token);
                }
            }

            await WriteEventAsync(ctx, "done", new { type = "done" }, timeoutCts.Token);
            return Results.Empty;
        }
        catch (OperationCanceledException)
        {
            // Mode dégradé
            pingCts.Cancel();

            var degraded = BuildRetrievalOnlyFallback(hits, o);
            await WriteEventAsync(ctx, "delta", new { type = "delta", content = degraded }, CancellationToken.None);
            await WriteEventAsync(ctx, "done", new { type = "done" }, CancellationToken.None);
            return Results.Empty;
        }
        catch (Exception ex)
        {
            pingCts.Cancel();
            log.LogWarning(ex, "LLM stream error, fallback retrieval-only.");

            var degraded = BuildRetrievalOnlyFallback(hits, o);
            await WriteEventAsync(ctx, "delta", new { type = "delta", content = degraded }, CancellationToken.None);
            await WriteEventAsync(ctx, "done", new { type = "done" }, CancellationToken.None);
            return Results.Empty;
        }
        finally
        {
            pingCts.Cancel();
        }
    }

    private static bool ShouldFlush(StringBuilder buf, int flushChars)
    {
        if (buf.Length >= Math.Max(16, flushChars)) return true;

        // flush on newline or sentence ending to keep it readable
        var s = buf.ToString();
        if (s.Contains('\n')) return true;

        var last = s.Length > 0 ? s[^1] : '\0';
        return last is '.' or '!' or '?' or ';' or ':' or ')';
    }

    private static string BuildRetrievalOnlyFallback(List<RagHit> hits, ChatOptions o)
    {
        if (hits is null || hits.Count == 0)
            return "Je n’ai trouvé aucune source pertinente dans la base. Je ne peux pas répondre sans sources.";

        var sb = new StringBuilder();
        sb.AppendLine("Le modèle de génération est indisponible ou trop lent (timeout).");
        sb.AppendLine();

        // Avertissement si les scores retrieval sont faibles (probable hors-sujet)
        var bestScore = hits.Max(h => h.Score);
        var low = o.RetrievalLowConfidenceScore;
        var ok = o.RetrievalOkScore;
        if (ok < low) (low, ok) = (ok, low);

        if (bestScore > 0 && bestScore < low)
        {
            sb.AppendLine("ATTENTION : les extraits récupérés semblent peu pertinents pour la question (fort risque hors-sujet).");
            sb.AppendLine();
        }
        else if (bestScore > 0 && bestScore < ok)
        {
            sb.AppendLine("Note : la pertinence des extraits est incertaine (réponse potentiellement incomplète).");
            sb.AppendLine();
        }

        sb.AppendLine("Voici les extraits les plus pertinents (mode dégradé, sans génération) :");
        sb.AppendLine();

        var maxSources = Math.Clamp(o.FallbackMaxSources, 1, 20);
        var maxChars = Math.Clamp(o.FallbackMaxCharsPerSource, 200, 3000);

        for (int i = 0; i < hits.Count && i < maxSources; i++)
        {
            var h = hits[i];
            var sid = $"S{i + 1}";
            var title = h.DocName ?? h.DocPath ?? "(document inconnu)";
            var pages = (h.PageStart is not null || h.PageEnd is not null)
                ? $"p.{h.PageStart?.ToString() ?? "?"}-{h.PageEnd?.ToString() ?? "?"}"
                : "p.?";

            var chunk = h.ChunkIndex is not null ? $"chunk {h.ChunkIndex}" : "chunk ?";
            var excerpt = (h.Text ?? "").Trim();
            if (excerpt.Length > maxChars) excerpt = excerpt[..maxChars] + "…";

            sb.AppendLine($"[{sid}] {title} ({pages}, {chunk})");
            sb.AppendLine(excerpt);
            sb.AppendLine();
        }

        sb.AppendLine("Tu peux relancer la question dès que le LLM est revenu, ou réduire max_tokens / sources pour accélérer.");
        return sb.ToString().TrimEnd();
    }

    private static async Task WriteEventAsync(HttpContext ctx, string eventName, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data, JsonOpt);
        await ctx.Response.WriteAsync($"event: {eventName}\n", ct);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    private static string Tail(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= n) return s;
        return s[^n..];
    }
}

public sealed record ChatStreamRequest(string Query, string Category, int TopK, string? SystemPrompt);
