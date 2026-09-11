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
// ---------- Security hardening (prod) ----------
// P2.1c (robuste): POLITIQUE signée + secret injecté.
// - La config signée porte la POLICY: Rag:RequireQdrantAuthInProd (+ Rag:QdrantAuthMode).
// - Le secret peut être fourni soit directement (Rag:QdrantApiKey), soit via référence (Rag:QdrantApiKeyRef).
//   Exemple de ref (dans deployment.config.json signé): "ENV:QDRANT_API_KEY" ou "FILE:/run/secrets/qdrant_api_key".
if (builder.Environment.IsProduction())
{
    var require = true;
    var requireStr = builder.Configuration["Rag:RequireQdrantAuthInProd"];
    if (!string.IsNullOrWhiteSpace(requireStr) && bool.TryParse(requireStr, out var requireParsed))
        require = requireParsed;
    if (require)
    {
        var explicitKey = builder.Configuration["Rag:QdrantApiKey"];
        var keyRef = builder.Configuration["Rag:QdrantApiKeyRef"];
        var effective = SecretRefResolver.Resolve(explicitKey, keyRef, builder.Environment.ContentRootPath, out _);

        if (string.IsNullOrWhiteSpace(effective))
            throw new InvalidOperationException("Production requires Qdrant auth: set Rag:QdrantApiKey or Rag:QdrantApiKeyRef in the signed deployment config.");
    }
}

builder.Services.AddSaaiaServices(builder.Configuration, builder.Environment);

var app = builder.Build();

// Pipeline (swagger, static ui, rate limiting, auth middleware)
app.UseSaaiaPipeline();

// Startup: migrate + bootstrap (avant de servir)
await app.ApplyMigrationsAndBootstrapAsync(app.Lifetime.ApplicationStopping);

// Endpoints
app.MapSaaiaEndpoints();

app.Run();
