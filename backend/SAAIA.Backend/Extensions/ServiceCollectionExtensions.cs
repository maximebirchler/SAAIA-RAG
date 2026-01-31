using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Serialization;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Bootstrap;
using SAAIA.Backend.Chat;

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

        // Stabilise DocumentsRoot si relatif (par rapport au ContentRootPath)
        var contentRoot = env.ContentRootPath;
        services.PostConfigure<IngestionOptions>(opt =>
        {
            if (!string.IsNullOrWhiteSpace(opt.DocumentsRoot) && !Path.IsPathRooted(opt.DocumentsRoot))
                opt.DocumentsRoot = Path.GetFullPath(Path.Combine(contentRoot, opt.DocumentsRoot));
        });

        // ---------- Rate limiting (par API key) ----------
        // CDC v2.6 : "Rate limiting par API key"
        var headerName = config.GetValue<string>("Auth:ApiKeyHeaderName") ?? "X-Api-Key";
        var rl = config.GetSection("RateLimiting").Get<RateLimitOptions>() ?? new RateLimitOptions();
        var window = TimeSpan.FromSeconds(Math.Max(1, rl.WindowSeconds));

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var path = ctx.Request.Path.Value ?? "";
                if (path.StartsWith("/health") || path.StartsWith("/ready") || path.StartsWith("/swagger") || path.StartsWith("/ui"))
                    return RateLimitPartition.GetNoLimiter("public");

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
        // NOTE: llm streaming => Timeout infini (géré via CancellationToken)
        services.AddHttpClient("llm", c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient("ollama", c => c.Timeout = Timeout.InfiniteTimeSpan);

        // Qdrant + TEI : timeout “raisonnable”.
        services.AddHttpClient("qdrant", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient("tei", c => c.Timeout = TimeSpan.FromMinutes(5));

        // ---------- Chat (SSE) ----------
        services.AddSingleton<ChatLimiter>();
        services.AddSingleton<RagRetriever>();
        services.AddSingleton<ChatPromptBuilder>();
        services.AddSingleton<LlmClient>();

        // ---------- Worker ----------
        services.AddHostedService<IngestionWorker>();
        services.AddHostedService<IngestionScanner>();
        services.AddHostedService<FileWatcherService>();

        // Warmup LLM
        services.AddHostedService<LlmWarmupService>();

        return services;
    }
}
