using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using PdfPig = UglyToad.PdfPig; // namespace alias for clarity
using SAAIA.Backend.Auth;
using SAAIA.Backend.Bootstrap;
using SAAIA.Backend.Db;
using SAAIA.Backend.Chat;

var builder = WebApplication.CreateBuilder(args);

// Local overrides (non versionné)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);


// ---------- JSON ----------
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// ---------- Swagger ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();

// ---------- Options ----------
builder.Services.Configure<ApiKeyAuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection("Bootstrap"));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection("Ingestion"));

// Stabilise les chemins relatifs par rapport au dossier du projet (ContentRootPath)
var contentRoot = builder.Environment.ContentRootPath;
builder.Services.PostConfigure<IngestionOptions>(opt =>
{
    if (!string.IsNullOrWhiteSpace(opt.DocumentsRoot) && !Path.IsPathRooted(opt.DocumentsRoot))
        opt.DocumentsRoot = Path.GetFullPath(Path.Combine(contentRoot, opt.DocumentsRoot));
});

builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection("Chat"));

// ---------- Postgres (DataSource pool) ----------
builder.Services.AddSingleton(sp =>
{
    var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    var dsb = new NpgsqlDataSourceBuilder(db.ConnectionString);
    return dsb.Build();
});

// ---------- HTTP clients ----------
builder.Services.AddHttpClient("qdrant");
builder.Services.AddHttpClient("tei");
// LLM: streaming => HttpClient.Timeout doit être infini, on gère les timeouts via CancellationToken.
builder.Services.AddHttpClient("llm", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});

// Compat (si tu as encore du code qui crée "ollama")
builder.Services.AddHttpClient("ollama", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});


// ---------- Chat (SSE) ----------
builder.Services.AddSingleton<ChatLimiter>();
builder.Services.AddSingleton<RagRetriever>();
builder.Services.AddSingleton<ChatPromptBuilder>();
builder.Services.AddSingleton<LlmClient>();

// ---------- Worker ----------
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<IngestionScanner>();
builder.Services.AddHostedService<FileWatcherService>();

// Warmup LLM au démarrage (évite le cold start / TTFT élevé sur la 1ère requête)
builder.Services.AddHostedService<LlmWarmupService>();

var app = builder.Build();

// ---------- Swagger ----------
app.UseSwagger();
app.UseSwaggerUI();

// ---------- Dev UI (static files) ----------
if (app.Environment.IsDevelopment())
{
    // UI accessible sur /ui
    app.UseDefaultFiles(new DefaultFilesOptions { RequestPath = "/ui" });
    app.UseStaticFiles(new StaticFileOptions { RequestPath = "/ui" });
}

// ---------- Auth middleware ----------
app.UseMiddleware<ApiKeyAuthMiddleware>();

// ---------- Startup: migrate + bootstrap ----------
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    var ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
    var bootstrap = scope.ServiceProvider.GetRequiredService<IOptions<BootstrapOptions>>();

    var migrationsDir = Path.Combine(env.ContentRootPath, "Db", "Migrations");
    await DbMigrator.ApplyMigrationsAsync(db.ConnectionString, migrationsDir, app.Lifetime.ApplicationStopping);

    await Bootstrapper.EnsureBootstrapAsync(ds, bootstrap, app.Lifetime.ApplicationStopping);
}

// ---------- Endpoints ----------
app.MapGet("/health", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow }));

// Cache readiness (évite de ping TEI + LLM trop souvent)
var readyGate = new SemaphoreSlim(1, 1);
const string ReadyCacheKey = "ready:v1";
var readyCacheTtl = TimeSpan.FromSeconds(10);

app.MapGet("/ready", async (
    NpgsqlDataSource ds,
    IHttpClientFactory httpFactory,
    IOptions<RagOptions> ragOpt,
    IOptions<ChatOptions> chatOpt,
    LlmClient llm,
    IMemoryCache cache,
    CancellationToken ct) =>
{
    // 1) Cache rapide
    if (cache.TryGetValue(ReadyCacheKey, out ReadinessSnapshot? cached) && cached is not null)
    {
        return cached.Ok
            ? Results.Ok(cached.Payload)
            : Results.Json(cached.Payload, statusCode: 503);
    }

    // 2) Gate pour éviter 10 checks en parallèle
    await readyGate.WaitAsync(ct);
    try
    {
        // double-check après lock
        if (cache.TryGetValue(ReadyCacheKey, out cached) && cached is not null)
        {
            return cached.Ok
                ? Results.Ok(cached.Payload)
                : Results.Json(cached.Payload, statusCode: 503);
        }

        var details = new Dictionary<string, object?>();
        var ok = true;

        // ---- DB check ----
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

        // ---- Qdrant check ----
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

        // ---- TEI (embeddings) check ----
        try
        {
            var rag = ragOpt.Value;
            var tei = httpFactory.CreateClient("tei");
            tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

            using var teiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            teiCts.CancelAfter(TimeSpan.FromSeconds(10));

            // Utilise le client TEI déjà utilisé dans RagRetriever (EmbedAsync)
            var vectors = await global::TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, new[] { "ping" }, teiCts.Token);

            var dim = (vectors is not null && vectors.Length > 0) ? vectors[0].Length : 0;
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

        // ---- LLM check (vraie completion via ProbeAsync) ----
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

        var payload = new
        {
            ok,
            ts = DateTimeOffset.UtcNow,
            details
        };

        var snap = new ReadinessSnapshot(ok, payload);
        cache.Set(ReadyCacheKey, snap, readyCacheTtl);

        return ok
            ? Results.Ok(payload)
            : Results.Json(payload, statusCode: 503);
    }
    finally
    {
        readyGate.Release();
    }
});


// Enqueue ingestion job (upsert/delete)
app.MapPost("/ingest/enqueue", async (
    HttpContext ctx,
    NpgsqlDataSource ds,
    IOptions<IngestionOptions> ingestOpt,
    IngestEnqueueRequest req) =>
{
    var tenantId = ctx.GetTenantId();
    var ingest = ingestOpt.Value;
    var ct = ctx.RequestAborted;

    if (string.IsNullOrWhiteSpace(req.DocPath))
        return Results.BadRequest(new { error = "docPath is required" });

    string relDocPath;
    try
    {
        // ✅ accepte relatif OU absolu (si sous DocumentsRoot), et sort toujours un relatif propre
        relDocPath = DocPathNormalizer.NormalizeToRelative(req.DocPath, ingest.DocumentsRoot);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    if (IngestionPathFilter.ShouldIgnoreRel(relDocPath))
        return Results.BadRequest(new { error = "docPath is ignored by ingestion filter (must be a non-temp .pdf path)" });

    var action = (req.Action ?? "upsert").Trim().ToLowerInvariant();
    if (action is not ("upsert" or "delete"))
        return Results.BadRequest(new { error = "action must be upsert|delete" });

    // ✅ category toujours en lower; défaut = config
    var category = string.IsNullOrWhiteSpace(req.Category)
        ? ingest.DefaultCategory
        : req.Category.Trim().ToLowerInvariant();

    await using var conn = await ds.OpenConnectionAsync(ct);

    if (action == "delete")
    {
        var r = await IngestionEnqueue.EnqueueDeleteAsync(conn, tenantId, relDocPath, ct);
        return Results.Ok(new
        {
            jobId = r.JobId,
            tenantId,
            docId = r.DocId,
            version = r.Version,
            action,
            docPath = relDocPath,
            category = (string?)null
        });
    }
    else
    {
        // Best-effort file metadata
        FileInfo? fi = null;
        try
        {
            var abs = DocPathNormalizer.ToAbsoluteFromRelative(relDocPath, ingest.DocumentsRoot);
            if (File.Exists(abs)) fi = new FileInfo(abs);
        }
        catch { /* ignore */ }

        var r = await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, relDocPath, category, fi, ct);
        return Results.Ok(new
        {
            jobId = r.JobId,
            tenantId,
            docId = r.DocId,
            version = r.Version,
            action = "upsert",
            docPath = relDocPath,
            category
        });
    }
});


app.MapPost("/admin/reindex", async (
    HttpContext ctx,
    NpgsqlDataSource ds,
    IOptions<IngestionOptions> ingestOpt,
    ReindexRequest req) =>
{
    var tenantId = ctx.GetTenantId();
    var ingest = ingestOpt.Value;
    var ct = ctx.RequestAborted;

    var max = Math.Clamp(req.Max ?? 5000, 1, 200000);
    var root = ingest.DocumentsRoot;

    var files = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
        .Take(max)
        .ToArray();

    await using var conn = await ds.OpenConnectionAsync(ct);

    int enqueued = 0;
    foreach (var abs in files)
    {
        var rel = DocPathNormalizer.NormalizeToRelative(abs, root);

        // même logique que le scanner
        var category = ingest.CategoryFromFirstFolder
            ? (rel.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ingest.DefaultCategory ?? "general").Trim().ToLowerInvariant()
            : (ingest.DefaultCategory ?? "general").Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(req.Category) &&
            category != req.Category.Trim().ToLowerInvariant())
            continue;

        var fi = new FileInfo(abs);
        await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, rel, category, fi, ct);
        enqueued++;
    }

    return Results.Ok(new { enqueued, scanned = files.Length, max });
});


// Categories (from registry DB)
app.MapGet("/rag/categories", async (HttpContext ctx, NpgsqlDataSource ds) =>
{
    var tenantId = ctx.GetTenantId();
    await using var conn = await ds.OpenConnectionAsync(ctx.RequestAborted);

    const string sql = @"
SELECT DISTINCT category
FROM documents
WHERE tenant_id=@tenant_id AND status='indexed'
ORDER BY category;";

    var cats = (await conn.QueryAsync<string>(new CommandDefinition(sql, new { tenant_id = tenantId }, cancellationToken: ctx.RequestAborted))).ToArray();
    return Results.Ok(cats);
});

// Retrieval (Qdrant search) tenant hard
app.MapPost("/rag/query", async (HttpContext ctx, IOptions<RagOptions> ragOpt, IHttpClientFactory httpFactory, RagQueryRequest req) =>
{
    var tenantId = ctx.GetTenantId();
    var rag = ragOpt.Value;

    var topK = req.TopK ?? rag.DefaultTopK;
    topK = Math.Clamp(topK, 1, rag.MaxTopK);

    var category = string.IsNullOrWhiteSpace(req.Category)
        ? null
        : req.Category.Trim().ToLowerInvariant();

    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required" });

    // Embed query (TEI OpenAI-compatible)
    var tei = httpFactory.CreateClient("tei");
    tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

    var emb = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, new[] { req.Query }, ctx.RequestAborted);
    var qvec = emb[0];

    // Search Qdrant
    var qdrant = httpFactory.CreateClient("qdrant");
    qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

    var ct = ctx.RequestAborted;
    var col = rag.QdrantCollection;

    var filterMust = new List<object>
    {
        new { key = "tenant_id", match = new { value = tenantId.ToString() } }
    };
    if (!string.IsNullOrWhiteSpace(category))
        filterMust.Add(new { key = "category", match = new { value = category } });

    var payload = new
    {
        vector = qvec,
        limit = topK,
        with_payload = true,
        filter = new { must = filterMust }
    };

    var url = $"/collections/{col}/points/search";
    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    var resp = await qdrant.PostAsync(url, content, ct);

    // ✅ auto-create si la collection n'existe pas
    if (resp.StatusCode == HttpStatusCode.NotFound)
    {
        resp.Dispose();
        await QdrantClient.EnsureCollectionAsync(qdrant, col, qvec.Length, ct);

        content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        resp = await qdrant.PostAsync(url, content, ct);
    }

    if (!resp.IsSuccessStatusCode)
    {
        var errBody = await resp.Content.ReadAsStringAsync(ct);
        return Results.Problem($"Qdrant search failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {errBody}");
    }

    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    var result = QdrantClient.ParseSearchResults(doc);

    return Results.Ok(new
    {
        query = req.Query,
        category,
        topK,
        matches = result
    });
});

// Debug scroll (tenant hard)
app.MapGet("/rag/debug/scroll", async (HttpContext ctx, IOptions<RagOptions> ragOpt, IHttpClientFactory httpFactory, int? limit) =>
{
    var tenantId = ctx.GetTenantId();
    var rag = ragOpt.Value;

    var qdrant = httpFactory.CreateClient("qdrant");
    qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

    var body = new
    {
        limit = Math.Clamp(limit ?? 20, 1, 200),
        with_payload = true,
        filter = new
        {
            must = new object[]
            {
                new { key = "tenant_id", match = new { value = tenantId.ToString() } }
            }
        }
    };

    var resp = await qdrant.PostAsync($"/collections/{rag.QdrantCollection}/points/scroll",
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        ctx.RequestAborted);

    if (!resp.IsSuccessStatusCode)
        return Results.Problem($"Qdrant scroll failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

    var json = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
    return Results.Text(json, "application/json");
});

// Chat streaming SSE (CDC v2.5)
app.MapChatEndpoints();

app.Run();


// ============================================================================
// Options + DTOs
// ============================================================================
sealed class DatabaseOptions
{
    public string ConnectionString { get; set; } = "";
}

sealed class RagOptions
{
    public string QdrantBaseUrl { get; set; } = "http://localhost:6333/";
    public string QdrantCollection { get; set; } = "knowledge_base";
    public string EmbeddingsBaseUrl { get; set; } = "http://localhost:8081/";
    public string EmbeddingsModel { get; set; } = "intfloat/multilingual-e5-base";
    public int DefaultTopK { get; set; } = 5;
    public int MaxTopK { get; set; } = 20;
}

sealed class IngestionOptions
{
    public string DocumentsRoot { get; set; } = "";
    public bool WatcherEnabled { get; set; } = true;
    public int MissingGraceSeconds { get; set; } = 120;

    // Chunking / embeddings
    public int ChunkMaxWords { get; set; } = 200;
    public int ChunkOverlapWords { get; set; } = 35;
    public int ChunkMinWords { get; set; } = 25;
    public int EmbeddingsBatchSize { get; set; } = 32;

    // Worker
    public int WorkerConcurrency { get; set; } = 2;

    
    public int WorkerEmptyDelayMs { get; set; } = 500;
    public int StaleRunningMinutes { get; set; } = 15;
// Scanner (remplace n8n)
    public bool ScannerEnabled { get; set; } = true;
    public int ScanIntervalSeconds { get; set; } = 10;
    public int MinFileAgeSeconds { get; set; } = 2;
    public int MaxFilesPerScan { get; set; } = 5000;

    // Catégorie (simple et scalable)
    public string DefaultCategory { get; set; } = "general";
    public bool CategoryFromFirstFolder { get; set; } = true;
}

sealed record IngestEnqueueRequest(string DocPath, string? Category, string? Action);

sealed record RagQueryRequest(string Query, string? Category, int? TopK);

sealed record ReindexRequest(int? Max = 5000, string? Category = null);

sealed record ReadinessSnapshot(bool Ok, object Payload);

// ============================================================================
// Background Worker (DB queue -> PDF -> chunks -> TEI -> Qdrant upsert/delete)
// ============================================================================
sealed class JobCanceledException : Exception
{
    public string Reason { get; }

    public JobCanceledException(string reason) : base(reason)
    {
        Reason = reason;
    }
}

class IngestionWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<IngestionWorker> _log;

    public IngestionWorker(IServiceProvider sp, ILogger<IngestionWorker> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Multi-consumer inside one instance (and can scale horizontally via replicas)
        return RunConsumersAsync(stoppingToken);
    }

    private async Task RunConsumersAsync(CancellationToken ct)
    {
        using var scope0 = _sp.CreateScope();
        var opt = scope0.ServiceProvider.GetRequiredService<IOptions<IngestionOptions>>().Value;

        var tasks = Enumerable.Range(0, Math.Clamp(opt.WorkerConcurrency, 1, 16))
            .Select(i => RunLoopAsync($"w{i}", ct))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task RunLoopAsync(string workerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
                var rag = scope.ServiceProvider.GetRequiredService<IOptions<RagOptions>>().Value;
                var ingest = scope.ServiceProvider.GetRequiredService<IOptions<IngestionOptions>>().Value;
                var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

                var requeued = await JobRepo.RequeueStaleRunningAsync(ds, TimeSpan.FromMinutes(ingest.StaleRunningMinutes), ct);
                if (requeued > 0)
                    _log.LogWarning("Requeued {Count} stale running ingestion jobs", requeued);

                var job = await JobRepo.TryDequeueAsync(ds, workerId, ct);
                if (job is null)
                {
                    await Task.Delay(ingest.WorkerEmptyDelayMs, ct);
                    continue;
                }

                try
                {
                    if (job.Action == "delete")
                        await ProcessDeleteAsync(ds, httpFactory, rag, ingest, job, ct);
                    else
                        await ProcessUpsertAsync(ds, httpFactory, rag, ingest, job, ct);


                    await JobRepo.MarkDoneAsync(ds, job.JobId, ct);
                }
                catch (JobCanceledException jc)
                {
                    _log.LogInformation("Job canceled {JobId} {Action} {DocPath} reason={Reason}", job.JobId, job.Action, job.DocPath, jc.Reason);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Job failed {JobId} {Action} {DocPath}", job.JobId, job.Action, job.DocPath);
                    await JobRepo.MarkFailedAsync(ds, job.JobId, ex.Message, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker loop error");
                await Task.Delay(1000, ct);
            }
        }
    }

    static async Task<bool> IsCurrentDocVersionAsync(NpgsqlDataSource ds, Guid tenantId, string docPath, int version, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = "SELECT ingestion_version FROM documents WHERE tenant_id=@tenant_id AND doc_path=@doc_path;";
        var v = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = docPath }, cancellationToken: ct));
        return v.HasValue && v.Value == version;
    }

    private static async Task ProcessDeleteAsync(NpgsqlDataSource ds, IHttpClientFactory httpFactory, RagOptions rag, IngestionOptions ingest, IngestionJob job, CancellationToken ct)
    {
        var tenantId = job.TenantId;
        var docId = job.DocId;

        // Qdrant delete by filter (tenant_id + doc_id)
        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);
        var relDocPath = DocPathNormalizer.NormalizeToRelative(job.DocPath, ingest.DocumentsRoot);

        if (job.Version > 0)
        {
            var ok = await IsCurrentDocVersionAsync(ds, tenantId, relDocPath, job.Version, ct);
            if (!ok)
            {
                await JobRepo.MarkCanceledAsync(ds, job.JobId, "superseded_version", ct);
                throw new JobCanceledException("superseded_version");
            }
        }

        var filter = new
        {
            must = new object[]
            {
                new { key = "tenant_id", match = new { value = tenantId.ToString() } },
                new { key = "doc_id", match = new { value = docId.ToString() } }
            }
        };

        var body = new { filter };

        var resp = await qdrant.PostAsync($"/collections/{rag.QdrantCollection}/points/delete?wait=true",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

        // If collection doesn't exist yet, treat delete as idempotent OK
        if (resp.StatusCode != HttpStatusCode.NotFound && !resp.IsSuccessStatusCode)
            throw new Exception($"Qdrant delete failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        // Mark document deleted in DB
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE documents
SET status='deleted', updated_at=now()
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            doc_path = relDocPath
        }, cancellationToken: ct));
    }

    private static async Task ProcessUpsertAsync(NpgsqlDataSource ds, IHttpClientFactory httpFactory, RagOptions rag, IngestionOptions ingest, IngestionJob job, CancellationToken ct)
    {
        var tenantId = job.TenantId;
        var docId = job.DocId;
        var relDocPath = DocPathNormalizer.NormalizeToRelative(job.DocPath, ingest.DocumentsRoot);
        var absPath = DocPathNormalizer.ToAbsoluteFromRelative(relDocPath, ingest.DocumentsRoot);

        if (job.Version > 0)
        {
            var ok = await IsCurrentDocVersionAsync(ds, tenantId, relDocPath, job.Version, ct);
            if (!ok)
            {
                await JobRepo.MarkCanceledAsync(ds, job.JobId, "superseded_version", ct);
                throw new JobCanceledException("superseded_version");
            }
        }

        if (!File.Exists(absPath))
        {
            await JobRepo.MarkCanceledAsync(ds, job.JobId, "file_missing", ct);
            throw new JobCanceledException("file_missing");
        }
// Compute file hash + size
        byte[] hash;
        long size;
        await using (var fs = File.OpenRead(absPath))
        {
            size = fs.Length;
            hash = await SHA256.HashDataAsync(fs, ct);
        }

        // Extract tokens with page mapping (PdfPig)
        var tokens = PdfExtractor.ExtractWordTokens(absPath);
        if (tokens.Count == 0)
            throw new Exception("No text extracted from PDF");

        var chunks = Chunker.MakeChunks(tokens, ingest.ChunkMaxWords, ingest.ChunkOverlapWords, ingest.ChunkMinWords);

        // Ensure collection exists (vector size derived from TEI once)
        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        var dim = await TeiClient.GetVectorDimAsync(tei, rag.EmbeddingsModel, ct);

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, dim, ct);

        // Delete previous points for this doc (idempotent upsert)
        await QdrantClient.DeleteByDocAsync(qdrant, rag.QdrantCollection, tenantId, docId, ct);

        // Embed + upsert in batches
        var batchSize = Math.Clamp(ingest.EmbeddingsBatchSize, 1, 256);
        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            var slice = chunks.Skip(i).Take(batchSize).ToList();
            var inputs = slice.Select(c => c.Text).ToArray();

            var vectors = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, inputs, ct);

            var points = new List<object>(slice.Count);
            for (int j = 0; j < slice.Count; j++)
            {
                var c = slice[j];
                var chunkId = IdUtil.DeterministicGuid($"{docId}:{c.ChunkIndex}");
                static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
                var nowIso = DateTimeOffset.UtcNow.ToString("O");
                var hashHex = ToHex(hash);

                points.Add(new
                {
                    id = chunkId.ToString(),
                    vector = vectors[j],
                    payload = new Dictionary<string, object?>
                    {
                        ["tenant_id"] = tenantId.ToString(),
                        ["doc_id"] = docId.ToString(),
                        ["doc_path"] = relDocPath,
                        ["doc_name"] = Path.GetFileName(relDocPath),
                        ["category"] = (job.Category ?? ingest.DefaultCategory).Trim().ToLowerInvariant(),


                        ["chunk_id"] = chunkId.ToString(),
                        ["chunk_index"] = c.ChunkIndex,
                        ["page_start"] = c.PageStart,
                        ["page_end"] = c.PageEnd,

                        ["hash_doc"] = hashHex,
                        ["created_at"] = nowIso,
                        ["updated_at"] = nowIso,

                        ["text"] = c.Text,
                        ["embed_text"] = c.Text
                    }
                });

            }

            await QdrantClient.UpsertPointsAsync(qdrant, rag.QdrantCollection, points, ct);
        }

        // Update documents row
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE documents
SET content_hash=@hash,
    file_size=@size,
    file_mtime=@mtime,
    status='indexed',
    last_ingested_at=now(),
    updated_at=now()
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;";
        var mtime = File.GetLastWriteTimeUtc(absPath);

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            doc_path = relDocPath,
            hash,
            size,
            mtime = DateTime.SpecifyKind(mtime, DateTimeKind.Utc)
        }, cancellationToken: ct));

    }
}


// ============================================================================
// DB job repo
// ============================================================================
static class JobRepo
{
    public static async Task<IngestionJob?> TryDequeueAsync(NpgsqlDataSource ds, string workerId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string sql = @"
WITH cte AS (
  SELECT q.job_id
  FROM ingestion_jobs q
  WHERE q.status='queued'
    AND q.available_at <= now()
    AND NOT EXISTS (
      SELECT 1 FROM ingestion_jobs r
      WHERE r.status='running'
        AND r.tenant_id=q.tenant_id
        AND r.doc_path=q.doc_path
    )
  ORDER BY q.priority ASC, q.created_at ASC
  FOR UPDATE SKIP LOCKED
  LIMIT 1
)
UPDATE ingestion_jobs j
SET status='running',
    locked_by=@worker,
    locked_at=now(),
    started_at=COALESCE(started_at, now()),
    attempts=attempts+1
FROM cte
WHERE j.job_id=cte.job_id
RETURNING
  j.job_id    AS JobId,
  j.tenant_id AS TenantId,
  j.action    AS Action,
  j.doc_path  AS DocPath,
  j.category  AS Category,
  j.payload   AS Payload;";

        var row = await conn.QueryFirstOrDefaultAsync<IngestionJobRow>(
            new CommandDefinition(sql, new { worker = workerId }, transaction: tx, cancellationToken: ct));

        if (row is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var docId = row.DocIdFromPayload ?? IdUtil.DeterministicGuid($"{row.TenantId}:{row.DocPath}");
        await tx.CommitAsync(ct);
        
        var version = row.VersionFromPayload ?? 0;
        return new IngestionJob(row.JobId, row.TenantId, row.Action, row.DocPath, row.Category, docId, version);

    }

    public static async Task MarkDoneAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE ingestion_jobs
SET status='done', finished_at=now(), last_error=NULL,
    locked_by=NULL, locked_at=NULL
WHERE job_id=@job_id;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId }, cancellationToken: ct));
    }

    public static async Task MarkFailedAsync(NpgsqlDataSource ds, Guid jobId, string error, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        // simple retry strategy
        const string sql = @"
UPDATE ingestion_jobs
SET status='failed', finished_at=now(), last_error=@err,
    locked_by=NULL, locked_at=NULL
WHERE job_id=@job_id;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, err = error }, cancellationToken: ct));
    }

    private sealed record IngestionJobRow(Guid JobId, Guid TenantId, string Action, string DocPath, string? Category, string Payload)
    {
        public Guid? DocIdFromPayload
        {
            get
            {
                try
                {
                    using var d = JsonDocument.Parse(Payload);
                    if (d.RootElement.TryGetProperty("docId", out var el) && el.ValueKind == JsonValueKind.String)
                        return Guid.Parse(el.GetString()!);
                }
                catch { }
                return null;
            }
        }

        public int? VersionFromPayload
        {
            get
            {
                try
                {
                    using var d = JsonDocument.Parse(Payload);
                    if (d.RootElement.TryGetProperty("version", out var el) && el.ValueKind == JsonValueKind.Number)
                        return el.GetInt32();
                }
                catch { }
                return null;
            }
        }

    }

    public static async Task<int> RequeueStaleRunningAsync(NpgsqlDataSource ds, TimeSpan staleAfter, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
    UPDATE ingestion_jobs
    SET status='queued',
        locked_at=NULL,
        locked_by=NULL,
        available_at=now(),
        last_error=COALESCE(last_error, 'requeued_stale_running')
    WHERE status='running'
    AND (
            locked_at IS NULL
            OR locked_at < now() - (@stale_seconds * interval '1 second')
        );";

        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            stale_seconds = (int)staleAfter.TotalSeconds
        }, cancellationToken: ct));
    }

    public static async Task MarkCanceledAsync(NpgsqlDataSource ds, Guid jobId, string reason, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
    UPDATE ingestion_jobs
    SET status='canceled', finished_at=now(), last_error=@reason,
        locked_by=NULL, locked_at=NULL
    WHERE job_id=@job_id;";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, reason }, cancellationToken: ct));
    }

}

sealed record IngestionJob(Guid JobId, Guid TenantId, string Action, string DocPath, string? Category, Guid DocId, int Version);


// ============================================================================
// Qdrant client (minimal)
// ============================================================================
static class QdrantClient
{
    public static async Task EnsureCollectionAsync(HttpClient qdrant, string collection, int vectorSize, CancellationToken ct)
    {
        var get = await qdrant.GetAsync($"/collections/{collection}", ct);
        if (get.IsSuccessStatusCode) return;

        if (get.StatusCode != HttpStatusCode.NotFound)
            return; // avoid failing startup hard

        var body = new
        {
            vectors = new { size = vectorSize, distance = "Cosine" }
        };

        var put = await qdrant.PutAsync($"/collections/{collection}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

        if (!put.IsSuccessStatusCode)
            throw new Exception($"Create collection failed: {(int)put.StatusCode} {put.ReasonPhrase}");
    }

    public static async Task DeleteByDocAsync(HttpClient qdrant, string collection, Guid tenantId, Guid docId, CancellationToken ct)
    {
        var filter = new
        {
            must = new object[]
            {
                new { key = "tenant_id", match = new { value = tenantId.ToString() } },
                new { key = "doc_id", match = new { value = docId.ToString() } }
            }
        };
        var body = new { filter };

        var resp = await qdrant.PostAsync($"/collections/{collection}/points/delete?wait=true",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

        // If collection not found, ignore (first run)
        if (resp.StatusCode == HttpStatusCode.NotFound) return;

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Qdrant delete failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    public static async Task UpsertPointsAsync(HttpClient qdrant, string collection, List<object> points, CancellationToken ct)
    {
        var body = new { points };
        var resp = await qdrant.PutAsync($"/collections/{collection}/points?wait=true",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Qdrant upsert failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    public static object[] ParseSearchResults(JsonDocument doc)
    {
        // Qdrant returns: { result: [ { id, score, payload } ] }
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return Array.Empty<object>();

        var list = new List<object>();
        foreach (var item in result.EnumerateArray())
        {
            var score = item.TryGetProperty("score", out var s) ? s.GetDouble() : 0.0;
            var payload = item.TryGetProperty("payload", out var p) ? p : default;

            // project citations-ready fields
            string? docPath = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("doc_path", out var dp) ? dp.GetString() : null;
            int? pageStart = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("page_start", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : null;
            int? pageEnd = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("page_end", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt32() : null;
            int? chunkIndex = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("chunk_index", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : null;
            string? text = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("text", out var t) ? t.GetString() : null;

            list.Add(new
            {
                score,
                docPath,
                pageStart,
                pageEnd,
                chunkIndex,
                text
            });
        }
        return list.ToArray();
    }
}


// ============================================================================
// TEI client (OpenAI-compatible /v1/embeddings)
// ============================================================================
static class TeiClient
{
    public static async Task<int> GetVectorDimAsync(HttpClient tei, string model, CancellationToken ct)
    {
        var vecs = await EmbedAsync(tei, model, new[] { "ping" }, ct);
        return vecs[0].Length;
    }

    public static async Task<float[][]> EmbedAsync(HttpClient tei, string model, string[] inputs, CancellationToken ct)
    {
        // TEI supports OpenAI-compatible route: POST /v1/embeddings :contentReference[oaicite:3]{index=3}
        var body = new
        {
            model,
            input = inputs,
            encoding_format = "float"
        };

        var resp = await tei.PostAsync("/v1/embeddings",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"TEI embeddings failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        // OpenAI format: { data: [ { embedding: [...] } ] }
        var data = doc.RootElement.GetProperty("data");
        var list = new List<float[]>();
        foreach (var item in data.EnumerateArray())
        {
            var emb = item.GetProperty("embedding");
            var v = new float[emb.GetArrayLength()];
            int i = 0;
            foreach (var n in emb.EnumerateArray())
            {
                v[i++] = n.GetSingle();
            }
            list.Add(v);
        }

        return list.ToArray();
    }
}


// ============================================================================
// PDF Extractor + Chunker (word tokens with page mapping)
// ============================================================================
static class PdfExtractor
{
    public static List<WordToken> ExtractWordTokens(string pdfPath)
    {
        var tokens = new List<WordToken>();

        using var doc = PdfPig.PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            var text = page.Text ?? "";
            var words = SplitWords(text);
            foreach (var w in words)
            {
                if (w.Length == 0) continue;
                tokens.Add(new WordToken(w, page.Number)); // page.Number is 1-based
            }
        }

        return tokens;
    }

    private static IEnumerable<string> SplitWords(string s)
        => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}

sealed record WordToken(string Word, int Page);

sealed record Chunk(int ChunkIndex, int PageStart, int PageEnd, string Text);

static class Chunker
{
    public static List<Chunk> MakeChunks(List<WordToken> tokens, int maxWords, int overlapWords, int minWords)
    {
        var chunks = new List<Chunk>();
        int idx = 0;
        int chunkIndex = 0;

        while (idx < tokens.Count)
        {
            int end = Math.Min(idx + maxWords, tokens.Count);
            var slice = tokens.GetRange(idx, end - idx);

            if (slice.Count >= minWords)
            {
                var pageStart = slice.Min(t => t.Page);
                var pageEnd = slice.Max(t => t.Page);
                var text = string.Join(' ', slice.Select(t => t.Word));
                chunks.Add(new Chunk(chunkIndex++, pageStart, pageEnd, text));
            }

            if (end >= tokens.Count) break;

            idx = Math.Max(0, end - overlapWords);
            if (idx == end) idx++; // safety
        }

        return chunks;
    }
}


// ============================================================================
// Utils
// ============================================================================
static class PathUtil
{
    public static string NormalizeRelativePath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var s = input.Replace('\\', '/').Trim();
        while (s.StartsWith("/")) s = s.Substring(1);

        // prevent traversal
        if (s.Contains(".."))
            throw new InvalidOperationException("docPath must be a relative path without '..'");

        return s;
    }
}

static class IdUtil
{
    public static Guid DeterministicGuid(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        Span<byte> g = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(g);
        return new Guid(g);
    }
}

static class DocPathNormalizer
{
    public static string NormalizeToRelative(string docPath, string documentsRoot)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            throw new InvalidOperationException("docPath is required");
        if (string.IsNullOrWhiteSpace(documentsRoot))
            throw new InvalidOperationException("DocumentsRoot is required");

        docPath = docPath.Trim();

        var rootFull = Path.GetFullPath(documentsRoot);

        string rel;
        if (Path.IsPathRooted(docPath))
        {
            var full = Path.GetFullPath(docPath);

            var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!full.StartsWith(rootPrefix, cmp) && !string.Equals(full, rootFull, cmp))
                throw new InvalidOperationException($"docPath must be inside DocumentsRoot. docPath='{full}', root='{rootFull}'");

            rel = Path.GetRelativePath(rootFull, full);
        }
        else
        {
            rel = docPath;
        }

        rel = rel.Replace('\\', '/').TrimStart('/');

        if (rel.Contains(".."))
            throw new InvalidOperationException("docPath must be a relative path without '..'");

        // re-canonicalize for safety
        var combined = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));

        var cmp2 = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var rootPrefix2 = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix2, cmp2) && !string.Equals(combined, rootFull, cmp2))
            throw new InvalidOperationException("Invalid docPath (path traversal).");

        return Path.GetRelativePath(rootFull, combined).Replace('\\', '/').TrimStart('/');
    }

    public static string ToAbsoluteFromRelative(string relDocPath, string documentsRoot)
    {
        var rootFull = Path.GetFullPath(documentsRoot);
        return Path.GetFullPath(Path.Combine(rootFull, relDocPath.Replace('/', Path.DirectorySeparatorChar)));
    }
}

