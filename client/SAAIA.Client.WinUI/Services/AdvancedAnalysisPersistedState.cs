using System.Text.Json;

namespace SAAIA.Client.WinUI.Services;

internal sealed record AdvancedAnalysisPersistedState(
    Guid JobId,
    Guid HandoffId,
    Guid SessionId,
    string Status,
    int Revision)
{
    public bool IsTerminal => Status is "succeeded" or "failed" or "canceled";
}

internal static class AdvancedAnalysisPersistedStateParser
{
    private const string CurrentSchemaVersion =
        "saaia.advanced-analysis-client-state.v1";

    private static readonly HashSet<string> ValidStatuses = new(
        ["queued", "running", "succeeded", "failed", "canceled"],
        StringComparer.Ordinal);

    internal static bool TryParse(
        string? sourcesJson,
        Guid expectedSessionId,
        out AdvancedAnalysisPersistedState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(sourcesJson) || expectedSessionId == Guid.Empty)
            return false;

        try
        {
            using var document = JsonDocument.Parse(sourcesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "advancedAnalysis",
                    out var advanced)
                || advanced.ValueKind != JsonValueKind.Object
                || !TryReadString(advanced, "schemaVersion", out var schemaVersion)
                || !string.Equals(
                    schemaVersion,
                    CurrentSchemaVersion,
                    StringComparison.Ordinal)
                || !TryReadGuid(advanced, "jobId", out var jobId)
                || !TryReadGuid(advanced, "handoffId", out var handoffId)
                || !TryReadGuid(advanced, "sessionId", out var sessionId)
                || sessionId != expectedSessionId
                || !TryReadString(advanced, "status", out var status)
                || !ValidStatuses.Contains(status)
                || !advanced.TryGetProperty("revision", out var revisionElement)
                || !revisionElement.TryGetInt32(out var revision)
                || revision < 1)
            {
                return false;
            }

            state = new AdvancedAnalysisPersistedState(
                jobId,
                handoffId,
                sessionId,
                status,
                revision);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadGuid(
        JsonElement element,
        string propertyName,
        out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
               && Guid.TryParse(property.GetString(), out value)
               && value != Guid.Empty;
    }

    private static bool TryReadString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
