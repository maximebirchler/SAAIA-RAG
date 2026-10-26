using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Bootstrap;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Middleware;

namespace SAAIA.Backend;

public static class WebApplicationExtensions
{
    public static WebApplication UseSaaiaPipeline(this WebApplication app)
    {
        // Correlation surrounds every downstream response, including errors, static files and Swagger.
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseMiddleware<ErrorHandlingMiddleware>();

        // Swagger
        app.UseSwagger();
        app.UseSwaggerUI();

        // Dev UI (static files) => /ui
        if (app.Environment.IsDevelopment())
        {
            app.UseDefaultFiles(new DefaultFilesOptions { RequestPath = "/ui" });
            app.UseStaticFiles(new StaticFileOptions { RequestPath = "/ui" });
        }

        // Rate limiting (global)
        app.UseRateLimiter();

        // Auth middleware
        app.UseMiddleware<ApiKeyAuthMiddleware>();

        return app;
    }

    public static async Task ApplyMigrationsAndBootstrapAsync(this WebApplication app, CancellationToken ct)
    {
        using var scope = app.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var bootstrap = scope.ServiceProvider.GetRequiredService<IOptions<BootstrapOptions>>();
        var auth = scope.ServiceProvider.GetRequiredService<IOptions<ApiKeyAuthOptions>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SAAIA.Startup");
        using var startup = BackendDiagnostics.Start("startup.database_bootstrap", logger);

        try
        {
            var migrationsDir = Path.Combine(env.ContentRootPath, "Db", "Migrations");
            await DbMigrator.ApplyMigrationsAsync(db.ConnectionString, migrationsDir, ct, logger);
            using var bootstrapProbe = BackendDiagnostics.Start("startup.bootstrap", logger);
            try
            {
                await Bootstrapper.EnsureBootstrapAsync(ds, bootstrap, auth, ct);
                bootstrapProbe.Complete();
            }
            catch (Exception ex) { bootstrapProbe.Fail(ex); throw; }
            startup.Complete();
        }
        catch (Exception ex) { startup.Fail(ex); throw; }
    }

    public static WebApplication MapSaaiaEndpoints(this WebApplication app)
    {
        HealthEndpoints.Map(app);
        ReadyEndpoints.Map(app);

        // Ingestion (public enqueue)
        IngestionEndpoints.Map(app);

        // Admin ingestion + documents
        IngestionAdminEndpoints.Map(app);

        // IMPORTANT (spec v2.8.x): documents catalog (user-safe) + admin docs endpoints
        AuthCapabilitiesEndpoints.Map(app);
        DocumentsEndpoints.Map(app);
        VisualAssetEndpoints.Map(app);
        SourcesEndpoints.Map(app);
        SummaryEndpoints.Map(app);

        // Admin
        AdminEndpoints.Map(app);
        AdminCatalogEndpoints.Map(app);
        AdminRuntimeEndpoints.Map(app);
        AdminKeysEndpoints.Map(app);
        AdminAuditEndpoints.Map(app);

        RagEndpoints.Map(app);
        ChatStoreEndpoints.Map(app);
        AdvancedAnalysisEndpoints.Map(app);

        return app;
    }
}
