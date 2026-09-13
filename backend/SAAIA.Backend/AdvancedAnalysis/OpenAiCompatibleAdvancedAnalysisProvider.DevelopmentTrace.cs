using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private async Task WriteDevelopmentHttpRejectionAsync(Guid jobId, string role,
        Dictionary<string, object?> payload, HttpResponseMessage response, string errorCode,
        int attempts, long elapsed, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.DevelopmentTraceDirectory)) return;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[64_001]; var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break; length += read;
        }
        var body = length > 64_000 ? "{\"providerErrorBodyOmitted\":\"limit_exceeded\"}"
            : Encoding.UTF8.GetString(buffer, 0, length);
        if (_apiKey is not null && body.Contains(_apiKey, StringComparison.Ordinal))
            body = "{\"providerErrorBodyOmitted\":\"secret_detected\"}";
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException) { document = JsonDocument.Parse(JsonSerializer.Serialize(new { providerErrorText = body }, JsonOptions)); }
        using (document)
            await WriteDevelopmentTraceAsync(jobId, role, payload, document.RootElement, string.Empty,
                null, 0m, attempts, elapsed, cancellationToken, "http.error", errorCode).ConfigureAwait(false);
    }

    private void ValidateDevelopmentTraceDirectory()
    {
        if (string.IsNullOrWhiteSpace(_options.DevelopmentTraceDirectory)) return;
        if (!Path.IsPathFullyQualified(_options.DevelopmentTraceDirectory))
            throw new AdvancedAnalysisProviderException("advanced_development_trace_directory_invalid");
        try { Directory.CreateDirectory(_options.DevelopmentTraceDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AdvancedAnalysisProviderException("advanced_development_trace_directory_unavailable");
        }
    }

    private async Task WriteDevelopmentTraceAsync(Guid jobId, string role,
        Dictionary<string, object?> requestPayload, JsonElement responseEnvelope,
        string completionJson, string? observedModelId, decimal? chargedCostUsd,
        int httpAttemptCount, long elapsedMilliseconds, CancellationToken cancellationToken,
        string completionOrigin = "message.content", string? normalizationError = null)
    {
        if (string.IsNullOrWhiteSpace(_options.DevelopmentTraceDirectory)) return;
        var requestJson = JsonSerializer.Serialize(requestPayload, JsonOptions);
        var traceJson = JsonSerializer.Serialize(new
        {
            schema = completionOrigin == "message.content" ? "advanced-development-trace.v1" : "advanced-development-trace.v2", jobId, role,
            capturedAtUtc = DateTimeOffset.UtcNow, modelId = ModelId, observedModelId,
            httpAttemptCount, elapsedMilliseconds, chargedCostUsd,
            requestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson))),
            requestJson, responseEnvelopeJson = responseEnvelope.GetRawText(), completionJson,
            completionOrigin, normalizationError
        }, JsonOptions);
        if (traceJson.Length > 3_000_000)
            throw new AdvancedAnalysisProviderException("advanced_development_trace_limit_exceeded");
        var path = Path.Combine(_options.DevelopmentTraceDirectory,
            $"{jobId:N}-{role}-{Guid.NewGuid():N}.json");
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(traceJson.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AdvancedAnalysisProviderException("advanced_development_trace_write_failed");
        }
    }
}
