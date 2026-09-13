using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
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
        int httpAttemptCount, long elapsedMilliseconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.DevelopmentTraceDirectory)) return;
        var requestJson = JsonSerializer.Serialize(requestPayload, JsonOptions);
        var traceJson = JsonSerializer.Serialize(new
        {
            schema = "advanced-development-trace.v1", jobId, role,
            capturedAtUtc = DateTimeOffset.UtcNow, modelId = ModelId, observedModelId,
            httpAttemptCount, elapsedMilliseconds, chargedCostUsd,
            requestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson))),
            requestJson, responseEnvelopeJson = responseEnvelope.GetRawText(), completionJson
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
