using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    internal static bool ShouldBypassWriterForDeterministicInventory(string? intent, IEnumerable<string> toolNames, string? inventoryRenderedText)
    {
        if (string.IsNullOrWhiteSpace(inventoryRenderedText))
            return false;

        if (IsInventoryIntent(intent))
            return true;

        var names = toolNames?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        return names.Count > 0 && names.All(IsInventoryLikeToolName);
    }

    private static bool ShouldBypassWriterForDeterministicInventory(RouterPlan plan, ToolResults writerToolResults, string? inventoryRenderedText)
        => ShouldBypassWriterForDeterministicInventory(
            plan.Intent,
            writerToolResults.Items.Where(x => string.IsNullOrWhiteSpace(x.Error)).Select(x => x.ToolName),
            inventoryRenderedText);

    private static bool IsInventoryIntent(string? intent)
    {
        var normalized = NormalizeRouterIntent(intent);
        return normalized.StartsWith("inventory.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInventoryLikeToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        return toolName is
            "inventory.rendered"
            or "documents.list"
            or "documents.search"
            or "documents.tree"
            or "documents.categories"
            or "documents.stats"
            or "documents.count"
            or "documents.empty_count"
            or "documents.empty_list"
            or "documents.extraction_quality"
            or "documents.extraction_pages"
            or "summary.status.count"
            or "summary.status.list"
            or "admin.summary.missing"
            or "summary.present.count"
            or "summary.present.list"
            or "diagnostic.performance";
    }

    private static bool HasOnlyInventoryTools(ToolResults toolResults)
    {
        var successful = toolResults.Items.Where(x => string.IsNullOrWhiteSpace(x.Error)).ToList();
        if (successful.Count == 0)
            return false;

        return successful.All(x => IsInventoryLikeToolName(x.ToolName));
    }

    private static string? TryRenderInventoryFallbackText(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "inventory.rendered" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        var kind = item.Result.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String
            ? (kindEl.GetString() ?? string.Empty)
            : string.Empty;

        if (!item.Result.TryGetProperty("data", out var data))
            return null;

        var fallback = RenderDeterministicInventoryFromData(kind, data, language);
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    private static string? TryExtractInventoryRenderedDataJson(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "inventory.rendered" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        if (!item.Result.TryGetProperty("data", out var data))
            return null;

        return data.GetRawText();
    }
}
