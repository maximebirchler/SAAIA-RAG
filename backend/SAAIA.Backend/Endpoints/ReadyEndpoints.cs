using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Security;

namespace SAAIA.Backend.Endpoints;

public static class ReadyEndpoints
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private const string ReadyCacheKey = "ready:v3";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private sealed record ReadinessSnapshot(bool Ok, object Payload);

    public static void Map(WebApplication app)
    {
        app.MapGet("/ready", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        HttpContext ctx,
        IHostEnvironment env,
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        IOptions<RagOptions> ragOpt,
        SignedConfigStatus? cfgStatus,
        IMemoryCache cache,
        CancellationToken ct)
    {
        var requestId = ctx.GetRequestId();

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

            // TEI check (embedding ping, timeout court) => donne aussi la dimension (utile pour bootstrap Qdrant)
            var teiDim = 0;
            try
            {
                var rag = ragOpt.Value;
                var tei = httpFactory.CreateClient("tei");
                tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

                using var teiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                teiCts.CancelAfter(TimeSpan.FromSeconds(10));

                var vectors = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, new[] { "ping" }, teiCts.Token);
                teiDim = (vectors is { Length: > 0 }) ? vectors[0].Length : 0;

                details["tei"] = teiDim > 0;
                details["tei_dim"] = teiDim;
                if (teiDim <= 0) ok = false;
            }
            catch (Exception ex)
            {
                ok = false;
                details["tei"] = false;
                details["tei_error"] = ex.Message;
            }

            // Qdrant check (+ auto-create collection if missing and TEI is OK)
            try
            {
                var rag = ragOpt.Value;

                // P2.1c: policy (signed) + secret resolution (ENV/FILE) via Rag:QdrantApiKeyRef
                var effectiveKey = SecretRefResolver.Resolve(rag.QdrantApiKey, rag.QdrantApiKeyRef, env.ContentRootPath, out var keySource);
                details["qdrant_auth_policy_required"] = string.Equals(env.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase) && rag.RequireQdrantAuthInProd;
                details["qdrant_auth_configured"] = !string.IsNullOrWhiteSpace(effectiveKey);
                details["qdrant_auth_mode"] = rag.QdrantAuthMode;
                details["qdrant_auth_source"] = keySource;

                var qdrant = httpFactory.CreateClient("qdrant");
                qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

                // Fresh install: bootstrap collection so /ready can turn green without requiring a first ingestion/query.
                if (teiDim > 0)
                {
                    await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, teiDim, ct);
                    details["qdrant_collection_bootstrap"] = true;
                }
                else
                {
                    details["qdrant_collection_bootstrap"] = false;
                }

                using var resp = await qdrant.GetAsync($"/collections/{rag.QdrantCollection}", ct);
                details["qdrant"] = resp.IsSuccessStatusCode;
                details["qdrant_status"] = (int)resp.StatusCode;

                if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                {
                    details["qdrant_auth_required"] = true;
                    if (string.IsNullOrWhiteSpace(effectiveKey))
                        details["qdrant_auth_hint"] = "Qdrant returned 401/403 but backend has no effective Qdrant key (Rag:QdrantApiKey/Ref).";
                }

                if (!resp.IsSuccessStatusCode) ok = false;
            }
            catch (Exception ex)
            {
                ok = false;
                details["qdrant"] = false;
                details["qdrant_error"] = ex.Message;
            }

            // LLM: client-only en v2.7
            details["llm"] = "client-only";

            // Signed deployment config status (M2.3)
            if (cfgStatus is not null)
            {
                details["config_signature_mode"] = cfgStatus.Mode;
                details["config_signature_present"] = cfgStatus.SignaturePresent;
                details["config_signature_verified"] = cfgStatus.Verified;
            }

            var payload = new { ok, ts = DateTimeOffset.UtcNow, requestId, details };
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
