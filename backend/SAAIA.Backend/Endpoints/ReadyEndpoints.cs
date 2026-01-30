using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Chat;

namespace SAAIA.Backend.Endpoints;

public static class ReadyEndpoints
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private const string ReadyCacheKey = "ready:v1";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private sealed record ReadinessSnapshot(bool Ok, object Payload);

    public static void Map(WebApplication app)
    {
        app.MapGet("/ready", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        IOptions<RagOptions> ragOpt,
        IOptions<ChatOptions> chatOpt,
        LlmClient llm,
        IMemoryCache cache,
        CancellationToken ct)
    {
        // 1) Cache rapide
        if (cache.TryGetValue(ReadyCacheKey, out ReadinessSnapshot? cached) && cached is not null)
        {
            return cached.Ok
                ? Results.Ok(cached.Payload)
                : Results.Json(cached.Payload, statusCode: 503);
        }

        // 2) Gate anti-parallélisme
        await _gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(ReadyCacheKey, out cached) && cached is not null)
            {
                return cached.Ok
                    ? Results.Ok(cached.Payload)
                    : Results.Json(cached.Payload, statusCode: 503);
            }

            var details = new Dictionary<string, object?>();
            var ok = true;

            // DB check
            try
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var cmd = new NpgsqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync(ct);
                details["db"] = true;
            }
            catch (Exception ex)
            {
                ok = false;
                details["db"] = false;
                details["db_error"] = ex.Message;
            }

            // Qdrant check
            try
            {
                var rag = ragOpt.Value;
                var qdrant = httpFactory.CreateClient("qdrant");
                qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

                using var resp = await qdrant.GetAsync($"/collections/{rag.QdrantCollection}", ct);
                details["qdrant"] = resp.IsSuccessStatusCode;
                details["qdrant_status"] = (int)resp.StatusCode;
                if (!resp.IsSuccessStatusCode) ok = false;
            }
            catch (Exception ex)
            {
                ok = false;
                details["qdrant"] = false;
                details["qdrant_error"] = ex.Message;
            }

            // TEI check (embedding ping, timeout court)
            try
            {
                var rag = ragOpt.Value;
                var tei = httpFactory.CreateClient("tei");
                tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

                using var teiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                teiCts.CancelAfter(TimeSpan.FromSeconds(10));

                var vectors = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, new[] { "ping" }, teiCts.Token);
                var dim = (vectors is { Length: > 0 }) ? vectors[0].Length : 0;

                details["tei"] = dim > 0;
                details["tei_dim"] = dim;
                if (dim <= 0) ok = false;
            }
            catch (Exception ex)
            {
                ok = false;
                details["tei"] = false;
                details["tei_error"] = ex.Message;
            }

            // LLM check
            try
            {
                var chat = chatOpt.Value;
                details["llm_model"] = chat.LlmModel;

                var llmOk = await llm.ProbeAsync(timeoutSeconds: 15, ct);
                details["llm"] = llmOk;
                if (!llmOk) ok = false;
            }
            catch (Exception ex)
            {
                ok = false;
                details["llm"] = false;
                details["llm_error"] = ex.Message;
            }

            var payload = new { ok, ts = DateTimeOffset.UtcNow, details };
            var snap = new ReadinessSnapshot(ok, payload);

            cache.Set(ReadyCacheKey, snap, CacheTtl);

            return ok
                ? Results.Ok(payload)
                : Results.Json(payload, statusCode: 503);
        }
        finally
        {
            _gate.Release();
        }
    }
}
