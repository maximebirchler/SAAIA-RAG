using System;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SAAIA.Backend;

public static class OpenTelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddSaaiaOpenTelemetry(
        this IServiceCollection services,
        IConfiguration config,
        IHostEnvironment env)
    {
        var opt = config.GetSection("OpenTelemetry").Get<OpenTelemetryOptions>() ?? new OpenTelemetryOptions();
        if (!opt.Enabled)
            return services;

        var serviceName = string.IsNullOrWhiteSpace(opt.ServiceName)
            ? "saaia-backend"
            : opt.ServiceName.Trim();

        var endpointStr = opt.OtlpEndpoint;
        if (string.IsNullOrWhiteSpace(endpointStr))
            endpointStr = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        // fallback "dev-friendly"
        if (string.IsNullOrWhiteSpace(endpointStr))
            endpointStr = "http://localhost:4317";

        var endpoint = new Uri(endpointStr);

        var serviceVersion = typeof(OpenTelemetryServiceCollectionExtensions).Assembly.GetName().Version?.ToString() ?? "unknown";

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion,
                serviceInstanceId: Environment.MachineName)
            .AddAttributes(new KeyValuePair<string, object>[]
            {
                new("deployment.environment", env.EnvironmentName),
                new("service.namespace", "saaia")
            });

        services.AddOpenTelemetry()
            .WithTracing(t =>
            {
                t.SetResourceBuilder(resource)
                 .AddSource(RetrievalTelemetry.ActivitySourceName)
                 .AddAspNetCoreInstrumentation(o =>
                 {
                     o.RecordException = true;

                     // Reduce noise: do not trace lightweight/public endpoints
                     o.Filter = ctx =>
                     {
                         var p = ctx.Request.Path.Value ?? "";
                         if (p.StartsWith("/health", StringComparison.OrdinalIgnoreCase)) return false;
                         if (p.StartsWith("/ready", StringComparison.OrdinalIgnoreCase)) return false;
                         if (p.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)) return false;
                         if (p.StartsWith("/ui", StringComparison.OrdinalIgnoreCase)) return false;
                         return true;
                     };
                 })
                 .AddHttpClientInstrumentation(o =>
                 {
                     o.RecordException = true;

                     // Reduce noise: ignore background Qdrant point-count polling
                     o.FilterHttpRequestMessage = req =>
                     {
                         var u = req.RequestUri?.AbsolutePath ?? "";
                         if (u.EndsWith("/points/count", StringComparison.OrdinalIgnoreCase))
                             return false;
                         return true;
                     };
                 })
                 .AddOtlpExporter(o =>
                 {
                     o.Endpoint = endpoint;
                 });
            })
            .WithMetrics(m =>
            {
                m.SetResourceBuilder(resource)
                 .AddMeter(RetrievalTelemetry.MeterName)
                 .AddAspNetCoreInstrumentation()
                 .AddHttpClientInstrumentation()
                 .AddRuntimeInstrumentation()
                 .AddOtlpExporter(o =>
                 {
                     o.Endpoint = endpoint;
                 });
            });

        return services;
    }
}
