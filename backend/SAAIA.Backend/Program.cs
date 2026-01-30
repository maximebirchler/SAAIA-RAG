using SAAIA.Backend;
using SAAIA.Backend.Auth;

var builder = WebApplication.CreateBuilder(args);

// Local overrides (non versionné)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Services
builder.Services.AddSaaiaServices(builder.Configuration, builder.Environment);

var app = builder.Build();

// Pipeline (swagger, static ui, auth middleware)
app.UseSaaiaPipeline();

// Startup: migrate + bootstrap (avant de servir)
await app.ApplyMigrationsAndBootstrapAsync(app.Lifetime.ApplicationStopping);

// Endpoints
app.MapSaaiaEndpoints();

app.Run();
