using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private async Task WriteDevelopmentTransportFailureAsync(Guid jobId, string role,
        string requestJson, string errorCode, string phase, int attempts, long elapsed)
    {
        if (string.IsNullOrWhiteSpace(_options.DevelopmentTraceDirectory)) return;
        // The provider token is already cancelled. Finish this local, opt-in
        // capture under its own small deadline, after conservative settlement.
        // This records the prepared request, not proof of provider receipt.
        using var captureDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var absentResponse = JsonDocument.Parse("null");
        try
        {
            await WriteDevelopmentTraceAsync(jobId, role, requestJson, absentResponse.RootElement,
                string.Empty, null, null, attempts, elapsed, captureDeadline.Token,
                "transport.failure", errorCode, phase).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AdvancedAnalysisProviderException or OperationCanceledException
            or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Collector failure must remain observable without replacing the
            // original transport failure or changing its budget settlement.
            _runtimeState.CaptureFailed("advanced_development_transport_trace_incomplete");
        }
    }

    private async Task WriteDevelopmentHttpRejectionAsync(Guid jobId, string role,
        string serializedPayload, HttpResponseMessage response, string errorCode,
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
            await WriteDevelopmentTraceAsync(jobId, role, serializedPayload, document.RootElement, string.Empty,
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

    // Check with the backend's current identity, including its container caps.
    // Directory.CreateDirectory alone does not prove that an existing sink is writable.
    internal async Task<string?> ProbeStrictDevelopmentTraceSinkAsync(
        Guid? jobId, string role, CancellationToken cancellationToken)
    {
        if (!_options.DevelopmentTraceStrict || string.IsNullOrWhiteSpace(_options.DevelopmentTraceDirectory)) return null;
        using var probe = BackendDiagnostics.Start("llm.exact_capture_preflight", _logger);
        if (jobId is { } id) probe.Set("job_id", id);
        probe.Set("role", role);
        probe.Set("strict", true);
        probe.Set("provider_http_attempts", 0);
        probe.Set("budget_reservation_created", false);
        if (!Path.IsPathFullyQualified(_options.DevelopmentTraceDirectory))
        {
            probe.Complete("rejected", "advanced_development_trace_directory_invalid");
            return "advanced_development_trace_directory_invalid";
        }
        string? path = null;
        string? failure = null;
        var acquired = false;
        var created = false;
        var removed = false;
        var bytesWritten = 0;
        try
        {
            await _traceWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            Directory.CreateDirectory(_options.DevelopmentTraceDirectory);
            path = Path.Combine(_options.DevelopmentTraceDirectory, $".saaia-exact-sink-probe-{Guid.NewGuid():N}.tmp");
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, options: FileOptions.Asynchronous);
            created = true;
            await stream.WriteAsync(new byte[1], cancellationToken).ConfigureAwait(false);
            bytesWritten = 1;
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            probe.Complete("cancelled", "advanced_llm_canceled");
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failure = "advanced_development_trace_write_failed";
        }
        finally
        {
            if (created && path is not null)
            {
                try { File.Delete(path); removed = true; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                { failure = "advanced_development_trace_write_failed"; }
            }
            probe.Set("probe_bytes", bytesWritten);
            probe.Set("probe_file_created", created);
            probe.Set("probe_file_removed", removed);
            if (acquired) _traceWriteGate.Release();
        }
        probe.Complete(failure is null ? "success" : "rejected", failure);
        return failure;
    }

    private async Task WriteDevelopmentTraceAsync(Guid jobId, string role,
        string requestJson, JsonElement responseEnvelope,
        string completionJson, string? observedModelId, decimal? chargedCostUsd,
        int httpAttemptCount, long elapsedMilliseconds, CancellationToken cancellationToken,
        string completionOrigin = "message.content", string? normalizationError = null,
        string? transportPhase = null)
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
            var traceJson = JsonSerializer.Serialize(new
            {
                schema = completionOrigin == "message.content" ? "advanced-development-trace.v1" : "advanced-development-trace.v2", jobId, role,
                capturedAtUtc = DateTimeOffset.UtcNow, modelId = ModelId, observedModelId,
                traceId = System.Diagnostics.Activity.Current?.TraceId.ToHexString(),
                spanId = System.Diagnostics.Activity.Current?.SpanId.ToHexString(),
                httpAttemptCount, elapsedMilliseconds, chargedCostUsd,
                requestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson))),
                requestJson, responseEnvelopeJson = responseEnvelope.GetRawText(), completionJson,
                completionOrigin, normalizationError, transportPhase
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
