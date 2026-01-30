using System.Net;
using Microsoft.AspNetCore.Http.Json;
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

        // Stabilise DocumentsRoot si relatif (par rapport au ContentRootPath)
        var contentRoot = env.ContentRootPath;
        services.PostConfigure<IngestionOptions>(opt =>
        {
            if (!string.IsNullOrWhiteSpace(opt.DocumentsRoot) && !Path.IsPathRooted(opt.DocumentsRoot))
                opt.DocumentsRoot = Path.GetFullPath(Path.Combine(contentRoot, opt.DocumentsRoot));
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

        // Compat
        services.AddHttpClient("ollama", c => c.Timeout = Timeout.InfiniteTimeSpan);

        // Qdrant + TEI : on met un timeout “raisonnable”.
        // (On a aussi des CancelAfter côté worker pour éviter les hangs)
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
