using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private async Task WriteDevelopmentHttpRejectionAsync(Guid jobId, string role,
        Dictionary<string, object?> payload, HttpResponseMessage response, string errorCode,
        int attempts, long elapsed, CancellationToken cancellationToken, CancellationToken requestCancellationToken,
        ProviderErrorBody? errorBody = null)
    {
        if (string.IsNullOrWhiteSpace(_options.DevelopmentTraceDirectory)) return;
        errorBody ??= await ReadProviderErrorBodyAsync(response, cancellationToken, requestCancellationToken).ConfigureAwait(false);
        if (errorBody.ReadFailed)
        {
            RecordCaptureFailure("advanced_development_trace_read_failed");
            return;
        }
        var body = errorBody.Truncated ? "{\"providerErrorBodyOmitted\":\"limit_exceeded\"}"
            : errorBody.Text ?? string.Empty;
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
        {
            RecordCaptureFailure("advanced_development_trace_directory_invalid");
            return;
        }
        try { Directory.CreateDirectory(_options.DevelopmentTraceDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            RecordCaptureFailure("advanced_development_trace_directory_unavailable");
        }
    }

    private async Task WriteDevelopmentTraceAsync(Guid jobId, string role,
        Dictionary<string, object?> requestPayload, JsonElement responseEnvelope,
        string completionJson, string? observedModelId, decimal? chargedCostUsd,
        int httpAttemptCount, long elapsedMilliseconds, CancellationToken cancellationToken,
        string completionOrigin = "message.content", string? normalizationError = null)
    {
        if (string.IsNullOrWhiteSpace(_options.DevelopmentTraceDirectory)) return;
        if (!Path.IsPathFullyQualified(_options.DevelopmentTraceDirectory))
        {
            RecordCaptureFailure("advanced_development_trace_directory_invalid");
            return;
        }
        using var probe = BackendDiagnostics.Start("llm.exact_capture", _logger);
        probe.Set("job_id", jobId);
        probe.Set("role", role);
        await _traceWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestJson = JsonSerializer.Serialize(requestPayload, JsonOptions);
            var traceJson = JsonSerializer.Serialize(new
            {
                schema = completionOrigin == "message.content" ? "advanced-development-trace.v1" : "advanced-development-trace.v2", jobId, role,
                capturedAtUtc = DateTimeOffset.UtcNow, modelId = ModelId, observedModelId,
                traceId = System.Diagnostics.Activity.Current?.TraceId.ToHexString(),
                spanId = System.Diagnostics.Activity.Current?.SpanId.ToHexString(),
                httpAttemptCount, elapsedMilliseconds, chargedCostUsd,
                requestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson))),
                requestJson, responseEnvelopeJson = responseEnvelope.GetRawText(), completionJson,
                completionOrigin, normalizationError
            }, JsonOptions);
            if (traceJson.Length > Math.Clamp(_options.DevelopmentTraceMaximumCharacters, 1_024, 16_000_000))
            {
                probe.Complete("degraded", "advanced_development_trace_limit_exceeded");
                RecordCaptureFailure("advanced_development_trace_limit_exceeded");
                return;
            }
            Directory.CreateDirectory(_options.DevelopmentTraceDirectory);
            PruneDevelopmentTraces(Encoding.UTF8.GetByteCount(traceJson));
            // Role is application-controlled, but do not let a future role introduce path separators.
            var safeRole = new string(role.Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(80).ToArray());
            var path = Path.Combine(_options.DevelopmentTraceDirectory,
                $"saaia-exact-v1-{jobId:N}-{safeRole}-{Guid.NewGuid():N}.json");
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(traceJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            _runtimeState.CaptureSucceeded();
            probe.Set("bytes", stream.Position);
            probe.Complete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            probe.Complete("degraded", "advanced_development_trace_write_failed");
            RecordCaptureFailure("advanced_development_trace_write_failed");
        }
        finally { _traceWriteGate.Release(); }
    }

    private void RecordCaptureFailure(string reason)
    {
        _runtimeState.CaptureFailed(reason);
        try { _logger.LogWarning("Exact LLM capture incomplete reason={ReasonCode} strict={Strict}", reason, _options.DevelopmentTraceStrict); }
        catch (Exception) { /* Health above still records a failed collector without breaking a generation. */ }
        if (_options.DevelopmentTraceStrict) throw new AdvancedAnalysisProviderException(reason);
    }

    private void PruneDevelopmentTraces(int incomingBytes)
    {
        var directory = new DirectoryInfo(_options.DevelopmentTraceDirectory);
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(_options.DevelopmentTraceRetentionDays, 1, 365));
        // New captures have an ownership marker. Legacy names require both a full name match and a matching schema/job.
        var files = directory.EnumerateFiles("*.json")
            .Where(IsOwnedDevelopmentTrace)
            .OrderBy(static file => file.LastWriteTimeUtc).ToList();
        long bytes = files.Sum(static file => file.Length);
        var count = files.Count;
        var maximumFiles = Math.Clamp(_options.DevelopmentTraceMaximumFiles, 1, 10_000);
        var maximumBytes = Math.Clamp(_options.DevelopmentTraceMaximumDirectoryBytes, 1_024, 4L * 1024 * 1024 * 1024);
        if (incomingBytes > maximumBytes)
            throw new IOException("Exact capture exceeds collector capacity.");
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc >= cutoff && count < maximumFiles && bytes + incomingBytes <= maximumBytes) break;
            var length = file.Length;
            file.Delete();
            bytes -= length;
            count--;
        }
    }

    private static bool IsOwnedDevelopmentTrace(FileInfo file)
    {
        const string captureName = @"[0-9a-fA-F]{32}-[A-Za-z0-9_-]{0,80}-[0-9a-fA-F]{32}\.json";
        if (System.Text.RegularExpressions.Regex.IsMatch(file.Name, "^saaia-exact-v1-" + captureName + "$")) return true;
        if (!System.Text.RegularExpressions.Regex.IsMatch(file.Name, "^" + captureName + "$")
            || file.Length > 16_000_000) return false;
        try
        {
            using var stream = file.OpenRead();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schema", out var schema)
                && schema.ValueKind == JsonValueKind.String
                && schema.GetString() is "advanced-development-trace.v1" or "advanced-development-trace.v2"
                && root.TryGetProperty("jobId", out var jobId) && jobId.ValueKind == JsonValueKind.String
                && jobId.TryGetGuid(out var id)
                && Guid.TryParseExact(file.Name[..32], "N", out var filenameJobId) && id == filenameJobId;
        }
        catch (JsonException) { return false; }
    }
}
