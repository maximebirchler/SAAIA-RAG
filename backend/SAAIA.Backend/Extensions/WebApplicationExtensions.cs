using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Bootstrap;
using SAAIA.Backend.Db;
using SAAIA.Backend.Endpoints;

namespace SAAIA.Backend;

public static class WebApplicationExtensions
{
    public static WebApplication UseSaaiaPipeline(this WebApplication app)
    {
        // Swagger
        app.UseSwagger();
        app.UseSwaggerUI();

        // Dev UI (static files) => /ui
        if (app.Environment.IsDevelopment())
        {
            app.UseDefaultFiles(new DefaultFilesOptions { RequestPath = "/ui" });
            app.UseStaticFiles(new StaticFileOptions { RequestPath = "/ui" });
        }

        // Map UnauthorizedAccessException -> 403 (utilisé par AdminAuth)
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (UnauthorizedAccessException)
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsync("Forbidden.");
                }
            }
        });

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

        var migrationsDir = Path.Combine(env.ContentRootPath, "Db", "Migrations");
        await DbMigrator.ApplyMigrationsAsync(db.ConnectionString, migrationsDir, ct);

        await Bootstrapper.EnsureBootstrapAsync(ds, bootstrap, auth, ct);
    }

    public static WebApplication MapSaaiaEndpoints(this WebApplication app)
    {
        HealthEndpoints.Map(app);
        ReadyEndpoints.Map(app);
        IngestionEndpoints.Map(app);

        // Admin
        AdminEndpoints.Map(app);
        AdminKeysEndpoints.Map(app);

        RagEndpoints.Map(app);

        return app;
    }
}
