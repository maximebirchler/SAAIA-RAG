using SAAIA.Backend;
using SAAIA.Backend.Security;

var builder = WebApplication.CreateBuilder(args);

// Local overrides (prod): chargé AVANT la config signée (donc la config signée garde la priorité)
if (!builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
}

// ✅ CONFIG SIGNÉE (obligatoire)
SignedConfigLoader.AddSignedDeploymentConfig(builder);

// Local overrides (dev): chargé APRÈS la config signée (pour pouvoir overrider bootstrap/DB localement)
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
}

// Services
builder.Services.AddSaaiaServices(builder.Configuration, builder.Environment);

var app = builder.Build();

// Pipeline (swagger, static ui, rate limiting, auth middleware)
app.UseSaaiaPipeline();

// Startup: migrate + bootstrap (avant de servir)
await app.ApplyMigrationsAndBootstrapAsync(app.Lifetime.ApplicationStopping);

// Endpoints
app.MapSaaiaEndpoints();

app.Run();
