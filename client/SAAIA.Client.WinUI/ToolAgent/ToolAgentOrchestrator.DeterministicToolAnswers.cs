using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private string BuildStatsFallbackAnswerFromResults(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.stats" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        return BuildStatsFallbackAnswer(item.Result, language);
    }

    private static async Task EmitDeterministicTextAsync(string text, Action<string>? onDelta, CancellationToken ct)
    {
        if (onDelta is null)
            return;

        foreach (var chunk in SplitDeterministicTextForDelivery(text))
        {
            ct.ThrowIfCancellationRequested();
            onDelta(chunk);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<string> SplitDeterministicTextForDelivery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        return new[] { text };
    }

    private static string BuildSourceResolveAnswer(ToolMemory.SourceRef src, string language, out object payload)
    {
        var dp = (src.DocPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var page = src.PageStart > 0 ? src.PageStart : 0;
        var label = AppendPageToOpenTokenLabel((src.Label ?? string.Empty).Trim(), page, language);
        var payloadSource = new ToolMemory.SourceRef
        {
            DocId = src.DocId,
            DocPath = dp,
            PageStart = src.PageStart,
            PageEnd = src.PageEnd,
            Label = label,
            SourceHash = src.SourceHash,
            DocLanguage = src.DocLanguage,
            ProfileLanguage = src.ProfileLanguage,
            CategoryRef = src.CategoryRef,
            CategoryPath = src.CategoryPath,
            ChunkId = src.ChunkId,
            ExtractionSource = src.ExtractionSource,
            DocumentQualityStatus = src.DocumentQualityStatus,
            PageQualityStatus = src.PageQualityStatus,
            TextStatus = src.TextStatus,
            ChunkTextStatus = src.ChunkTextStatus,
            ChunkTextSparse = src.ChunkTextSparse,
            ChunkOcrCandidate = src.ChunkOcrCandidate,
            QualityStatus = src.QualityStatus,
            ExtractionConfidence = src.ExtractionConfidence,
            DocumentExtractionConfidence = src.DocumentExtractionConfidence,
            PageExtractionConfidence = src.PageExtractionConfidence,
            ManualReviewRecommended = src.ManualReviewRecommended,
            DocumentManualReviewRecommended = src.DocumentManualReviewRecommended,
            PageManualReviewRecommended = src.PageManualReviewRecommended,
            OcrAttempted = src.OcrAttempted,
            OcrApplied = src.OcrApplied,
            OcrRecommended = src.OcrRecommended,
            QualitySignals = src.QualitySignals.ToList(),
            ChunkQualitySignals = src.ChunkQualitySignals.ToList(),
            MatchedContentCards = src.MatchedContentCards.ToList(),
            SelectionHintEvidenceRole = src.SelectionHintEvidenceRole,
            SelectionHintActionabilityScore = src.SelectionHintActionabilityScore,
            SelectionHintSupportScore = src.SelectionHintSupportScore,
            SelectionHintFragmentScore = src.SelectionHintFragmentScore,
            SelectionHintNavigationScore = src.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = src.SelectionHintQualityPenalty,
            ContentRole = src.ContentRole,
            NavigationReason = src.NavigationReason,
            RetrievalNavigationScore = src.RetrievalNavigationScore,
            ContentDensityScore = src.ContentDensityScore
        };
        payload = BuildSourcesPayload([payloadSource]);

        var heading = DeterministicAgentText.SourceHeading(language);
        return $"{heading}:\n1. [[open|{dp}|{page}|{label}]]";
    }

    private static string SanitizeOpenTokenLabel(string? label)
    {
        var safe = (label ?? string.Empty).Trim();
        if (safe.Length == 0)
            return string.Empty;

        return safe
            .Replace("|", " ")
            .Replace("[", "(")
            .Replace("]", ")");
    }

    private static string AppendPageToOpenTokenLabel(string? label, int? page, string language)
    {
        var safe = SanitizeOpenTokenLabel(label);
        if (string.IsNullOrWhiteSpace(safe))
            return safe;

        if (Regex.IsMatch(safe, @"(?i)\b(?:p\.?|page|s\.)\s*\d+\b", RegexOptions.CultureInvariant))
            return safe;

        if (page is not { } rawPage || rawPage <= 0)
            return safe;

        var safePage = Math.Max(1, rawPage);
        return $"{safe} ({SourceBackedPagePrefix(language)}{safePage})";
    }


    private string TryBuildDocumentsCountAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.count" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var total = TryGetInt(item.Result, "total") ?? 0;
        return DeterministicAgentText.DocumentsCount(total, language);
    }

    private string TryBuildEmptyFoldersCountAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.empty_count" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var total = TryGetInt(item.Result, "total") ?? 0;
        return DeterministicAgentText.EmptyFoldersCount(total, language);
    }

    private string TryBuildEmptyFoldersListAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.empty_list" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (!item.Result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var paths = new List<string>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = TryGetString(entry, "path") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        if (paths.Count == 0)
            return DeterministicAgentText.NoEmptyFoldersFound(language);

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.EmptyFoldersHeader(language));

        for (var i = 0; i < paths.Count; i++)
            sb.AppendLine($"{i + 1}. {paths[i]}");

        return sb.ToString().TrimEnd();
    }

    private string TryBuildSummaryStatusCountAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.count" or "summary.present.count") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var totals = item.Result.TryGetProperty("totals", out var totalsElement) && totalsElement.ValueKind == JsonValueKind.Object
            ? totalsElement
            : default;
        var total = TryGetInt(item.Result, "total") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "total") : null) ?? 0;
        var profileMissing = TryGetInt(item.Result, "profileMissing") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "profileMissing") : null) ?? 0;
        var mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.count", StringComparison.OrdinalIgnoreCase) ? "present" : "missing");
        if (string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase))
        {
            return total <= 0
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.StoredSummariesCount(total, language);
        }

        if (profileMissing > 0 && total > 0)
            return DeterministicAgentText.MissingSummariesCount(total, language)
                + Environment.NewLine
                + DeterministicAgentText.BackofficeProfilesMissingCount(profileMissing, language);

        return total <= 0
            ? DeterministicAgentText.NoMissingSummaries(language)
            : DeterministicAgentText.MissingSummariesCount(total, language);
    }

    private string TryBuildSummaryStatusListAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.list" or "summary.present.list" or "admin.summary.missing") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var hasItems = item.Result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            hasItems = item.Result.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            return string.Empty;

        var rows = new List<(string path, string state, bool profileMissing)>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = TryGetString(entry, "DocPath") ?? TryGetString(entry, "docPath") ?? string.Empty;
            var state = TryGetString(entry, "SummaryState") ?? TryGetString(entry, "summaryState") ?? string.Empty;
            var profileState = TryGetString(entry, "CapabilityBProfileState") ?? TryGetString(entry, "capabilityBProfileState") ?? string.Empty;
            var hasBackofficeProfile = TryGetBool(entry, "CapabilityBHasBackofficeProfile") ?? TryGetBool(entry, "capabilityBHasBackofficeProfile");
            var profileMissing = string.Equals(profileState, "missing", StringComparison.OrdinalIgnoreCase)
                || (hasBackofficeProfile.HasValue && !hasBackofficeProfile.Value && HasReason(entry, "profile_missing"));
            if (!string.IsNullOrWhiteSpace(path))
                rows.Add((path, state, profileMissing));
        }

        var mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.list", StringComparison.OrdinalIgnoreCase) ? "present" : "missing");
        if (rows.Count == 0)
            return string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.NoMissingSummaries(language);

        var sb = new StringBuilder();
        sb.AppendLine(string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)
            ? DeterministicAgentText.StoredSummariesHeader(language)
            : DeterministicAgentText.MissingSummariesHeader(language));
        for (var i = 0; i < rows.Count; i++)
        {
            var suffix = rows[i].state.Equals("stale", StringComparison.OrdinalIgnoreCase)
                ? $" {DeterministicAgentText.SummaryStatusStaleSuffix(language)}"
                : string.Empty;
            if (rows[i].profileMissing)
                suffix += $" [{DeterministicAgentText.BackofficeProfileMissingSuffix(language)}]";
            sb.AppendLine($"{i + 1}. {rows[i].path}{suffix}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderDiagnosticPerformanceFromReplayData(JsonElement data, string language)
    {
        language = NormalizeLanguageCode(language);
        var profile = TryGetString(data, "profile") ?? "unknown";
        var schemaVersion = TryGetInt(data, "schemaVersion") ?? 0;
        var cdcAlignment = TryGetString(data, "cdcAlignment") ?? "v3.1";
        var routerMs = TryGetInt(data, "routerMs") ?? 0;
        var toolsMs = TryGetInt(data, "toolsMs") ?? 0;
        var writerMs = TryGetInt(data, "writerMs") ?? 0;
        var totalMs = TryGetInt(data, "totalMs") ?? 0;

        var workspace = data.TryGetProperty("workspace", out var workspaceEl) && workspaceEl.ValueKind == JsonValueKind.Object
            ? workspaceEl
            : default;
        var session = data.TryGetProperty("session", out var sessionEl) && sessionEl.ValueKind == JsonValueKind.Object
            ? sessionEl
            : default;
        var execution = data.TryGetProperty("execution", out var executionEl) && executionEl.ValueKind == JsonValueKind.Object
            ? executionEl
            : default;
        var persistence = data.TryGetProperty("persistence", out var persistenceEl) && persistenceEl.ValueKind == JsonValueKind.Object
            ? persistenceEl
            : default;
        var resetPolicy = data.TryGetProperty("resetPolicy", out var resetEl) && resetEl.ValueKind == JsonValueKind.Object
            ? resetEl
            : default;

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceHeader(language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceProfile(profile, schemaVersion, cdcAlignment, language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceTimings(routerMs, toolsMs, writerMs, totalMs, language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceWorkspace(
            TryGetInt(workspace, "catalogCategoriesCount") ?? 0,
            TryGetInt(workspace, "knownDocumentsCount") ?? 0,
            TryGetBoolProp(workspace, "hasCapabilitiesSnapshot") ?? false,
            language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceSession(
            TryGetBoolProp(session, "hasFocusedDocument") ?? false,
            TryGetInt(session, "lastListedDocumentsCount") ?? 0,
            TryGetBoolProp(session, "hasResolvedCategory") ?? false,
            TryGetBoolProp(session, "hasPendingClarification") ?? false,
            language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceExecution(
            TryGetString(execution, "mode") ?? "auto",
            TryGetBoolProp(execution, "hasRouterIntent") ?? false,
            TryGetInt(execution, "toolNamesCount") ?? 0,
            TryGetBoolProp(execution, "hasAdminOperation") ?? false,
            language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformancePersistence(
            TryGetBoolProp(persistence, "language") ?? false,
            TryGetBoolProp(persistence, "style") ?? false,
            TryGetBoolProp(persistence, "mode") ?? false,
            TryGetBoolProp(persistence, "focusedDocument") ?? false,
            TryGetBoolProp(persistence, "resolvedCategory") ?? false,
            language));
        sb.AppendLine(DeterministicAgentText.DiagnosticPerformanceReset(
            TryGetBoolProp(resetPolicy, "preservesM1Lite") ?? false,
            TryGetBoolProp(resetPolicy, "preservesPreferences") ?? false,
            TryGetBoolProp(resetPolicy, "clearsM3") ?? false,
            TryGetBoolProp(resetPolicy, "clearsM6") ?? false,
            TryGetBoolProp(resetPolicy, "resetsModeToAuto") ?? false,
            language));
        return sb.ToString().TrimEnd();
    }

}
