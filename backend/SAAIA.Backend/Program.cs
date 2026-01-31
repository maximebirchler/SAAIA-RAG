using SAAIA.Backend;
using SAAIA.Backend.Security;

var builder = WebApplication.CreateBuilder(args);

// Local overrides (non versionné)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ✅ CONFIG SIGNÉE (obligatoire)
// - charge deployment.config.json uniquement si la signature Ed25519 est valide
// - ajoute ensuite ces valeurs au pipeline de configuration (priorité haute)
SignedConfigLoader.AddSignedDeploymentConfig(builder);

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
