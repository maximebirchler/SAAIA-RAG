sealed class OpenTelemetryOptions
{
    /// <summary>
    /// Active/désactive OpenTelemetry (traces + métriques).
    /// Par défaut: false pour l'export OTLP. Les chronométrages et logs applicatifs restent actifs.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Nom du service exporté (resource.service.name).</summary>
    public string ServiceName { get; set; } = "saaia-backend";

    /// <summary>
    /// Endpoint OTLP (gRPC). Exemple: http://localhost:4317
    /// Si vide, le code tente OTEL_EXPORTER_OTLP_ENDPOINT, sinon fallback http://localhost:4317.
    /// </summary>
    public string? OtlpEndpoint { get; set; } = null;
}
