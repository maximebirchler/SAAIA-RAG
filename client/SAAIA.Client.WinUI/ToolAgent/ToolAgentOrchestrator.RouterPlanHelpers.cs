using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string NormalizePlanMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "standard" or "strict" ? normalized : "auto";
    }

    private static string NormalizeResponseFormat(string? responseFormat)
    {
        var normalized = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "about" or "summary" ? normalized : "auto";
    }

    private static double? ClampConfidence(double? confidence)
    {
        if (confidence is null)
            return null;
        if (double.IsNaN(confidence.Value) || double.IsInfinity(confidence.Value))
            return null;
        return Math.Max(0d, Math.Min(1d, confidence.Value));
    }

    private static string? InferIntentFromToolCalls(IReadOnlyList<RouterPlan.ToolCall>? toolCalls)
    {
        var first = toolCalls?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Name));
        if (first is null)
            return null;

        return first.Name switch
        {
            "documents.count" => "inventory.count",
            "documents.categories" => "inventory.categories",
            "documents.tree" => "inventory.tree",
            "documents.navigation" => "rag.answer",
            "documents.stats" => "inventory.stats",
            "summary.status.count" or "summary.status.list" or "summary.present.count" or "summary.present.list" or "admin.summary.missing" => "inventory.summary_status",
            "documents.list" => HasChangedSinceArg(first.Args) ? "inventory.changed_since" : "inventory.list",
            "documents.search" => "inventory.find",
            "summary.exists" or "summary.get" => "summary.exists",
            "rag.search" or "rag.multi_search" => "rag.answer",
            "rag.summarize_live" => "rag.summarize_doc",
            "admin.summary.request" or "admin.summary.generate" => "admin.summary.generate",
            "diagnostic.performance" => "diagnostic.performance",
            "export.create" => "export.create",
            _ => null
        };
    }

    private static bool IsAdminOnlyIntent(string? intent)
    {
        var normalized = NormalizeRouterIntent(intent);
        return normalized.StartsWith("admin.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "inventory.summary_status", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "inventory.health", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasChangedSinceArg(JsonElement args)
        => !string.IsNullOrWhiteSpace(GetStringArg(args, "changedSince"));

    private static bool IsGroundedToolForCritic(string? toolName)
    {
        return toolName switch
        {
            "documents.list" or "documents.search" or "documents.get" or "documents.count" or "documents.categories" or "documents.tree" or "documents.navigation" or "documents.stats" or "documents.empty_count" or "documents.empty_list" or "documents.extraction_quality" or "documents.extraction_pages" or
            "inventory.rendered" or
            "rag.search" or "rag.multi_search" or "rag.summarize_live" or
            "summary.get" or "summary.exists" or "summary.search" or "summary.status.count" or "summary.status.list" or "summary.present.count" or "summary.present.list" or
            "sources.resolve" => true,
            _ => false
        };
    }

    private static bool IsAboutResponseFormat(string? responseFormat)
    {
        var normalized = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "about" or "brief" or "short" or "overview";
    }
}
