using System;
using System.Net.Http;
using System.Text.Json;
using SAAIA.Client.Core.Diagnostics;
using SAAIA.Client.Core.Sources;

namespace SAAIA.Client.WinUI.Services;

internal static class ClientDiagnostics
{
    internal static ClientDiagnosticJournal Journal { get; } = new()
    {
        EventSink = entry =>
        {
            var previousFailures = ClientLog.FailedWrites;
            ClientLog.Info("CLIENT_PROBE " + JsonSerializer.Serialize(entry, ClientJson.CamelCase));
            if (ClientLog.FailedWrites > previousFailures) throw new System.IO.IOException("client_log_write_failed");
        }
    };
    internal static SourceLocatorRegistry Sources { get; } = new();
    internal static bool IncludeContent => string.Equals(
        Environment.GetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT"), "true", StringComparison.OrdinalIgnoreCase);

    internal static object Snapshot() => new
    {
        schemaVersion = "saaia.client-diagnostics.v1",
        capturedAtUtc = DateTimeOffset.UtcNow,
        contentMode = IncludeContent ? "explicit_content" : "metadata_only",
        historyCapacity = 64,
        eventCapacityPerTurn = 240,
        metadataLimits = new
        {
            fieldsPerCategory = ClientDiagnosticJournal.MaximumMetadataFields,
            measurementsPerEvent = ClientDiagnosticJournal.MaximumMeasurements,
            keyCharacters = ClientDiagnosticJournal.MaximumKeyCharacters,
            valueCharacters = ClientDiagnosticJournal.MaximumMetadataCharacters
        },
        collectorFailures = Journal.CollectorFailures,
        logWriteFailures = ClientLog.FailedWrites,
        turns = Journal.Last(64)
    };

    internal static object? AttachContext(object? sources)
    {
        if (sources is null) return null;
        try
        {
            var serialized = JsonSerializer.SerializeToNode(sources, ClientJson.CamelCase);
            if (serialized is System.Text.Json.Nodes.JsonObject payload)
                payload["clientDiagnostics"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["schemaVersion"] = "saaia.source-context.v1",
                    ["traceId"] = Journal.CurrentTraceId, ["turnId"] = Journal.CurrentTurnId
                };
            return serialized ?? sources;
        }
        catch (Exception exception)
        {
            Journal.Emit("sources.attach_correlation", "degraded", exception.GetType().Name);
            return sources;
        }
    }

    internal static void Report(string operation, string outcome, string? reason = null)
    {
        using var scope = Journal.BeginTurn(operation: "diagnostic_event");
        Journal.Emit(operation, outcome, reason);
        scope.Complete(outcome, reason);
    }

    internal static void ConfigureTurn(AppSettings settings, ILlmProvider? provider = null)
    {
        var metadata = new System.Collections.Generic.Dictionary<string, string>
        {
            ["clientVersion"] = typeof(ClientDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown",
            ["mode"] = settings.ActiveMode ?? "auto",
            ["quality"] = settings.RagQualityPreset ?? "balanced",
            ["localEnabled"] = settings.UseLocalLlm.ToString(),
            ["maximumOutputTokens"] = settings.LlmMaxOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["model"] = provider?.Descriptor.ModelId ?? settings.ModelId ?? "unknown",
            ["provider"] = provider?.Descriptor.Provider ?? "unknown",
            ["profile"] = provider?.Descriptor.RuntimeProfile ?? "unknown",
            ["contentMode"] = IncludeContent ? "explicit_content" : "metadata_only"
        };
        metadata["configurationFingerprint"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata)))).ToLowerInvariant();
        Journal.Configuration(metadata);
    }

    internal static void Propagate(HttpRequestMessage request)
    {
        if (Journal.CurrentTraceParent is { } parent) request.Headers.TryAddWithoutValidation("traceparent", parent);
        if (Journal.CurrentTurnId is { } turn) request.Headers.TryAddWithoutValidation("X-SAAIA-Turn-Id", turn);
        if (Guid.TryParse(Journal.CurrentSessionId, out var session))
            request.Headers.TryAddWithoutValidation("X-SAAIA-Session-Id", session.ToString("D"));
    }
}
