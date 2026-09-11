using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    internal static string? ClassifyToolExecutionError(Exception ex, bool isAdminTool)
    {
        if (ex is HttpRequestException httpEx)
        {
            var statusCode = httpEx.StatusCode;
            if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return isAdminTool ? "admin_invalid_or_forbidden" : "tool_failed";
        }

        var message = ex.Message ?? string.Empty;
        if (isAdminTool)
        {
            if (Regex.IsMatch(message, @"(^|\D)401(\D|$)", RegexOptions.CultureInvariant)
                || Regex.IsMatch(message, @"(^|\D)403(\D|$)", RegexOptions.CultureInvariant)
                || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
            {
                return "admin_invalid_or_forbidden";
            }
        }

        return null;
    }

    private static string? TryGetStructuredToolError(ToolResults.Item item)
    {
        if (!string.IsNullOrWhiteSpace(item.Error))
            return item.Error!.Trim();

        if (item.Result.ValueKind == JsonValueKind.Object
            && item.Result.TryGetProperty("error", out var errorEl)
            && errorEl.ValueKind == JsonValueKind.String)
        {
            return (errorEl.GetString() ?? string.Empty).Trim();
        }

        return null;
    }

    private static bool IsRagBusyError(string? error)
    {
        var normalized = (error ?? string.Empty).Trim();
        return string.Equals(normalized, "rag_search_busy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "transient_rate_limit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "backend_busy", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryBuildToolFailureAnswer(RouterPlan plan, ToolResults toolResults, string language)
    {
        var successful = toolResults.Items.Where(x => string.IsNullOrWhiteSpace(TryGetStructuredToolError(x))).ToList();
        if (successful.Count > 0)
            return null;

        var errors = toolResults.Items
            .Select(TryGetStructuredToolError)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (errors.Count == 0)
            return null;

        if (errors.Any(IsRagBusyError))
        {
            return DeterministicAgentText.RagSearchBusy(language);
        }

        if (errors.Any(x => string.Equals(x, "admin_required", StringComparison.OrdinalIgnoreCase)))
        {
            return DeterministicAgentText.ToolFailureAdminRequired(language);
        }

        if (errors.Any(x => string.Equals(x, "admin_invalid_or_forbidden", StringComparison.OrdinalIgnoreCase)))
        {
            return DeterministicAgentText.ToolFailureAdminInvalidOrForbidden(language);
        }

        if (errors.Any(x => string.Equals(x, "unknown_tool", StringComparison.OrdinalIgnoreCase)))
        {
            return DeterministicAgentText.ToolFailureUnknownPlan(language);
        }

        if (errors.Any(x => string.Equals(x, "tool_failed", StringComparison.OrdinalIgnoreCase)))
        {
            return DeterministicAgentText.ToolFailureToolFailed(language);
        }

        return null;
    }
}
