using System;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool TryParseCriticEnvelope(string? json, out string? status, out string? finalAnswer, out string? warning)
    {
        status = null;
        finalAnswer = null;
        warning = null;

        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                status = s.GetString();

            if (root.TryGetProperty("finalAnswer", out var fa) && fa.ValueKind == JsonValueKind.String)
                finalAnswer = fa.GetString();

            if (root.TryGetProperty("warning", out var w) && w.ValueKind == JsonValueKind.String)
                warning = w.GetString();

            return true;
        }
        catch
        {
            return false;
        }
    }
}
