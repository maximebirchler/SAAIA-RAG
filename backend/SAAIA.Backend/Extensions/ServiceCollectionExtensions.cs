using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Bootstrap;
using SAAIA.Backend.CatalogSnapshot;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Security;

namespace SAAIA.Backend;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSaaiaServices(
        this IServiceCollection services,
        IConfiguration config,
        IHostEnvironment env)
    {
        // ---------- JSON ----------
        services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        // ---------- Swagger ----------
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
        services.AddMemoryCache();

        // ---------- Options ----------
        services.Configure<ApiKeyAuthOptions>(config.GetSection("Auth"));
        services.Configure<BootstrapOptions>(config.GetSection("Bootstrap"));
        services.Configure<DatabaseOptions>(config.GetSection("Database"));
        services.Configure<RagOptions>(config.GetSection("Rag"));
        services.Configure<IngestionOptions>(config.GetSection("Ingestion"));
        services.Configure<ChatOptions>(config.GetSection("Chat"));
        services.Configure<RateLimitOptions>(config.GetSection("RateLimiting"));
        services.Configure<OpenTelemetryOptions>(config.GetSection("OpenTelemetry"));
        services.Configure<CatalogSnapshotOptions>(config.GetSection("CatalogSnapshot"));
        services.Configure<RuntimeGovernanceOptions>(config.GetSection("RuntimeGovernance"));

        // ---------- OpenTelemetry (M2.2) ----------
        services.AddSaaiaOpenTelemetry(config, env);

        // Stabilise DocumentsRoot si relatif (par rapport au ContentRootPath)
        var contentRoot = env.ContentRootPath;
        services.PostConfigure<IngestionOptions>(opt =>
        {
            if (!string.IsNullOrWhiteSpace(opt.DocumentsRoot) && !Path.IsPathRooted(opt.DocumentsRoot))
                opt.DocumentsRoot = Path.GetFullPath(Path.Combine(contentRoot, opt.DocumentsRoot));
        });

        // ---------- Rate limiting (par API key) ----------
        // CDC : "Rate limiting par API key"
        var headerName = config.GetValue<string>("Auth:ApiKeyHeaderName") ?? "X-Api-Key";
        var rl = config.GetSection("RateLimiting").Get<RateLimitOptions>() ?? new RateLimitOptions();
        var window = TimeSpan.FromSeconds(Math.Max(1, rl.WindowSeconds));

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // M3.2 DoD : 429 + Retry-After (+ payload JSON minimal)
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                if (http.Response.HasStarted) return;

                var retryAfterSeconds = (int)Math.Ceiling(window.TotalSeconds);
                retryAfterSeconds = Math.Max(1, retryAfterSeconds);

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                http.Response.ContentType = "application/json";

                var payload = new
                {
                    error = "rate_limited",
                    requestId = http.GetRequestId(),
                    retryAfterSeconds
                };

                await http.Response.WriteAsync(JsonSerializer.Serialize(payload), ct);
            };

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var path = ctx.Request.Path.Value ?? "";

                // Ne jamais limiter les endpoints publics/non sensibles.
                if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/ready", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/ui", StringComparison.OrdinalIgnoreCase))
                    return RateLimitPartition.GetNoLimiter("public");

                // Roadmap v2.7 : limiter uniquement RAG + chat-store.
                var isRagOrChat = path.StartsWith("/rag", StringComparison.OrdinalIgnoreCase)
                               || path.StartsWith("/chat", StringComparison.OrdinalIgnoreCase);
                if (!isRagOrChat)
                    return RateLimitPartition.GetNoLimiter("unlimited");

                var key = ctx.Request.Headers[headerName].ToString().Trim();
                if (string.IsNullOrWhiteSpace(key))
                    key = ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, rl.PermitLimit),
                    Window = window,
                    QueueLimit = Math.Max(0, rl.QueueLimit),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                });
            });
        });

        // ---------- Postgres (DataSource pool) ----------
        services.AddSingleton(sp =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var dsb = new NpgsqlDataSourceBuilder(db.ConnectionString);
            return dsb.Build();
        });

        // ---------- HTTP clients ----------
        // Qdrant + TEI : timeout “raisonnable”.
        services.AddHttpClient("qdrant", (sp, c) =>
        {
            c.Timeout = TimeSpan.FromMinutes(5);

            // BaseAddress + auth depuis RagOptions (si défini).
            var rag = sp.GetRequiredService<IOptions<RagOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(rag.QdrantBaseUrl))
                c.BaseAddress = new Uri(rag.QdrantBaseUrl);

            var env = sp.GetRequiredService<IHostEnvironment>();

            // Secret resolution (P2.1c): allow Rag:QdrantApiKeyRef (ENV:/FILE:) in signed config.
            var key = SecretRefResolver.Resolve(rag.QdrantApiKey, rag.QdrantApiKeyRef, env.ContentRootPath, out _);
            if (!string.IsNullOrWhiteSpace(key))
            {
                var mode = (rag.QdrantAuthMode ?? "api-key").Trim().ToLowerInvariant();
                key = key.Trim();

                if (mode is "bearer" or "authorization")
                {
                    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }
                else
                {
                    // Qdrant REST: header "api-key: <key>"
                    c.DefaultRequestHeaders.Remove("api-key");
                    c.DefaultRequestHeaders.TryAddWithoutValidation("api-key", key);
                }
            }
        });
        services.AddHttpClient("tei", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient("llm", (sp, c) =>
        {
            var chat = sp.GetRequiredService<IOptions<ChatOptions>>().Value;
            c.Timeout = TimeSpan.FromSeconds(Math.Max(5, chat.LlmTimeoutSeconds));

            if (!string.IsNullOrWhiteSpace(chat.LlmBaseUrl))
                c.BaseAddress = new Uri(chat.LlmBaseUrl);
        });

        // ---------- Bulkheads / job runtime state ----------
        services.AddSingleton<IngestionBulkheads>();
        services.AddSingleton<IngestionJobCancellationRegistry>();
        services.AddSingleton(sp => new LocalLlmChatClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<ChatOptions>>().Value));
        services.AddSingleton<CapabilityAHypotheticalQuestionService>();
        services.AddSingleton(sp => new CapabilityBBackofficeSummaryService(
            sp.GetRequiredService<LocalLlmChatClient>(),
            sp.GetRequiredService<IOptions<ChatOptions>>().Value));
        services.AddSingleton<RuntimeDiagnosticsService>();
        services.AddSingleton<RuntimeRetrievalKpiService>();
        services.AddSingleton<RuntimeCapabilityBKpiService>();

        // ---------- Worker ----------
        services.AddHostedService<IngestionWorker>();
        services.AddHostedService<IngestionScanner>();
        services.AddHostedService<CapabilityBBackofficeWorker>();
        services.AddHostedService<FileWatcherService>();
        services.AddHostedService<CatalogSnapshotService>();

        return services;
    }
}
