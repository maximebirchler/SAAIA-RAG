using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services;
using System.Diagnostics;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private void CaptureStructuredConversationState(RouterPlan plan, ToolResults toolResults)
    {
        if (plan is null || toolResults is null)
            return;

        var successful = toolResults.Items.Where(x => string.IsNullOrWhiteSpace(x.Error)).ToList();
        if (successful.Count == 0)
            return;

        var categories = successful.LastOrDefault(x => string.Equals(x.ToolName, "documents.categories", StringComparison.OrdinalIgnoreCase));
        if (categories is not null)
        {
            _mem.LastPresentedCategories = ParsePresentedCategories(categories.Result);
            _mem.PromoteCategoriesToWorkspace(_mem.LastPresentedCategories);
            _mem.LastInventoryAction = "categories";
        }

        var list = successful.LastOrDefault(x => x.ToolName is "documents.list" or "documents.search");
        if (list is not null)
        {
            _mem.LastInventoryAction = HasCategoryScope(plan, list.ToolName) ? "list_documents_by_category" : "list_documents";
            _mem.LastSummaryStatusSnapshot = null;
            var snapshot = BuildCategorySnapshotForTool(plan, list.ToolName, list.Result);
            if (snapshot is not null)
                _mem.LastResolvedCategory = snapshot;
        }

        var stats = successful.LastOrDefault(x => string.Equals(x.ToolName, "documents.stats", StringComparison.OrdinalIgnoreCase));
        if (stats is not null)
        {
            _mem.LastInventoryAction = HasCategoryScope(plan, stats.ToolName) ? "category_stats" : "catalog_stats";
            _mem.LastSummaryStatusSnapshot = null;
            var snapshot = BuildCategorySnapshotForTool(plan, stats.ToolName, stats.Result);
            if (snapshot is not null)
                _mem.LastResolvedCategory = snapshot;
        }

        var summaryCount = successful.LastOrDefault(x => x.ToolName is "summary.status.count" or "summary.present.count");
        if (summaryCount is not null)
        {
            _mem.LastInventoryAction = "summary_status_count";
            _mem.LastSummaryStatusSnapshot = BuildSummaryStatusSnapshot(summaryCount.Result, plan, summaryCount.ToolName);
        }

        var summaryList = successful.LastOrDefault(x => x.ToolName is "summary.status.list" or "summary.present.list" or "admin.summary.missing");
        if (summaryList is not null)
        {
            _mem.LastInventoryAction = "summary_status_list";
            _mem.LastSummaryStatusSnapshot = BuildSummaryStatusSnapshot(summaryList.Result, plan, summaryList.ToolName);
        }
    }

    private ToolMemory.CategorySnapshot? BuildCategorySnapshotForTool(RouterPlan plan, string toolName, JsonElement result)
    {
        var call = plan.ToolCalls.LastOrDefault(x => string.Equals(x.Name, toolName, StringComparison.OrdinalIgnoreCase));
        var categoryRef = call is null ? null : GetStringArg(call.Args, "categoryRef");
        var categoryPath = NormalizeCategoryPathArg(call is null
                           ? null
                           : GetStringArg(call.Args, "categoryPath")
                             ?? GetStringArg(call.Args, "path")
                             ?? GetStringArg(call.Args, "category"));

        if (string.IsNullOrWhiteSpace(categoryPath) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var first = items.EnumerateArray().FirstOrDefault();
            categoryPath = TryGetString(first, "categoryPath") ?? categoryPath;
        }

        var snapshot = ResolveCategorySnapshotFromReference(categoryRef, categoryPath);
        if (snapshot is not null)
            _mem.PromoteCategoriesToWorkspace(new[] { snapshot });
        return snapshot;
    }

    private ToolMemory.SummaryStatusSnapshot BuildSummaryStatusSnapshot(JsonElement result, RouterPlan plan, string toolName)
    {
        var call = plan.ToolCalls.LastOrDefault(x => string.Equals(x.Name, toolName, StringComparison.OrdinalIgnoreCase));
        var categoryRef = call is null ? null : GetStringArg(call.Args, "categoryRef");
        var categoryPath = NormalizeCategoryPathArg(call is null
                           ? null
                           : GetStringArg(call.Args, "categoryPath")
                             ?? GetStringArg(call.Args, "category"));

        var totals = result.TryGetProperty("totals", out var totalsObj) && totalsObj.ValueKind == JsonValueKind.Object
            ? totalsObj
            : default;

        var snapshot = new ToolMemory.SummaryStatusSnapshot
        {
            CategoryPath = categoryPath,
            CategoryRef = categoryRef,
            Mode = (TryGetString(result, "mode") ?? (toolName is "summary.present.count" or "summary.present.list" ? "present" : "missing")).Trim().ToLowerInvariant(),
            Total = TryGetInt(result, "total") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "total") : null) ?? 0,
            MissingStored = TryGetInt(result, "missingStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "missingStored") : null) ?? 0,
            StaleStored = TryGetInt(result, "staleStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "staleStored") : null) ?? 0,
            ProfileMissing = TryGetInt(result, "profileMissing") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "profileMissing") : null) ?? 0,
            Items = new List<ToolMemory.SummaryStatusItem>()
        };

        if (result.TryGetProperty("scopePath", out var scopePath) && scopePath.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(scopePath.GetString()))
            snapshot.CategoryPath = NormalizeCategoryPathArg(scopePath.GetString());

        JsonElement items;
        var hasItems = result.TryGetProperty("items", out items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            hasItems = result.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array;

        if (hasItems)
        {
            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                snapshot.Items.Add(BuildSummaryStatusItemSnapshot(entry));
            }
        }

        return snapshot;
    }

    private void UpdateSummaryStatusSnapshotFromJson(JsonElement result)
    {
        var current = _mem.LastSummaryStatusSnapshot;
        var totals = result.TryGetProperty("totals", out var totalsObj) && totalsObj.ValueKind == JsonValueKind.Object
            ? totalsObj
            : default;

        var snapshot = new ToolMemory.SummaryStatusSnapshot
        {
            CategoryPath = current?.CategoryPath,
            CategoryRef = current?.CategoryRef,
            Mode = (TryGetString(result, "mode") ?? current?.Mode ?? "missing").Trim().ToLowerInvariant(),
            Total = TryGetInt(result, "total") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "total") : null) ?? current?.Total ?? 0,
            MissingStored = TryGetInt(result, "missingStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "missingStored") : null) ?? current?.MissingStored ?? 0,
            StaleStored = TryGetInt(result, "staleStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "staleStored") : null) ?? current?.StaleStored ?? 0,
            ProfileMissing = TryGetInt(result, "profileMissing") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "profileMissing") : null) ?? current?.ProfileMissing ?? 0,
            Items = new List<ToolMemory.SummaryStatusItem>()
        };

        if (result.TryGetProperty("scopePath", out var scopePath) && scopePath.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(scopePath.GetString()))
            snapshot.CategoryPath = NormalizeCategoryPathArg(scopePath.GetString());

        JsonElement items;
        var hasItems = result.TryGetProperty("items", out items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            hasItems = result.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array;

        if (hasItems)
        {
            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                snapshot.Items.Add(BuildSummaryStatusItemSnapshot(entry));
            }
        }

        _mem.LastSummaryStatusSnapshot = snapshot;
    }

    private ToolMemory.CategorySnapshot? ResolveCategorySnapshotFromReference(string? categoryRef, string? categoryPath = null)
    {
        var normalizedRef = NormalizeShortcutToken(categoryRef);
        var normalizedPath = NormalizeShortcutToken(categoryPath);

        foreach (var category in EnumerateKnownCategories())
        {
            if (!string.IsNullOrWhiteSpace(normalizedRef))
            {
                if (NormalizeShortcutToken(category.CategoryRef) == normalizedRef
                    || NormalizeShortcutToken(category.DisplayName) == normalizedRef
                    || NormalizeShortcutToken(category.CategoryPath) == normalizedRef
                    || NormalizeShortcutToken(category.Ordinal.ToString(CultureInfo.InvariantCulture)) == normalizedRef
                    || category.Aliases.Any(x => NormalizeShortcutToken(x) == normalizedRef))
                {
                    return CloneCategorySnapshot(category);
                }
            }

            if (!string.IsNullOrWhiteSpace(normalizedPath)
                && (NormalizeShortcutToken(category.CategoryPath) == normalizedPath || NormalizeShortcutToken(category.DisplayName) == normalizedPath))
            {
                return CloneCategorySnapshot(category);
            }
        }

        if (string.IsNullOrWhiteSpace(categoryRef) && string.IsNullOrWhiteSpace(categoryPath))
            return null;

        var display = !string.IsNullOrWhiteSpace(categoryPath)
            ? categoryPath!.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? categoryPath
            : categoryRef ?? string.Empty;

        return new ToolMemory.CategorySnapshot
        {
            CategoryRef = categoryRef ?? categoryPath ?? string.Empty,
            CategoryPath = categoryPath ?? string.Empty,
            DisplayName = display,
            Ordinal = int.TryParse(categoryRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal) ? ordinal : 0,
            TotalDocuments = 0,
            Aliases = new List<string>()
        };
    }

    private static ToolMemory.CategorySnapshot CloneCategorySnapshot(ToolMemory.CategorySnapshot source)
        => new()
        {
            CategoryRef = source.CategoryRef,
            CategoryPath = source.CategoryPath,
            DisplayName = source.DisplayName,
            Ordinal = source.Ordinal,
            TotalDocuments = source.TotalDocuments,
            Aliases = source.Aliases?.ToList() ?? new List<string>()
        };

    private void RememberDeterministicRenderFromJson(string kind, JsonElement data, string routerIntent, string? sourceUserMessage)
    {
        _mem.LastDeterministicRender = new ToolMemory.DeterministicRenderState
        {
            Kind = kind,
            DataJson = data.GetRawText(),
            RouterIntent = routerIntent,
            SourceUserMessage = sourceUserMessage,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private JsonElement CreateCanonicalDocumentsListJson(JsonElement rawData)
        => CreateCanonicalDocumentsListJsonCore(rawData, null, null);

    private JsonElement CreateCanonicalDocumentsListJsonCore(JsonElement rawData, string? scopePath, string? searchQuery = null)
    {
        var (docs, limit, offset, total, endOfList, dropped) = DocumentListHelper.Sanitize(rawData, _mem);
        var normalizedScopePath = NormalizeCategoryPathArg(scopePath) ?? NormalizeCategoryPathArg(TryGetString(rawData, "scopePath"));
        var normalizedSearchQuery = string.IsNullOrWhiteSpace(searchQuery) ? TryGetString(rawData, "searchQuery") : searchQuery.Trim();

        if (LooksLikeExactDocumentReference(normalizedSearchQuery))
            docs = FilterExactDocumentMatches(docs, normalizedSearchQuery!);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            scopePath = normalizedScopePath,
            searchQuery = normalizedSearchQuery,
            limit,
            offset,
            total = docs.Count,
            endOfList,
            dropped,
            items = docs.Select(x => new
            {
                docId = x.DocId,
                docPath = x.DocPath,
                docName = x.DocName,
                category = x.Category,
                categoryPath = x.CategoryPath,
                pdfRef = x.PdfRef,
                pages = x.Pages
            }).ToList()
        }));

        return doc.RootElement.Clone();
    }

    private static bool LooksLikeExactDocumentReference(string? searchQuery)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
            return false;

        var q = searchQuery.Trim();
        return q.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || q.Contains('/')
            || q.Contains('\\');
    }

    private static List<ToolMemory.DocumentItem> FilterExactDocumentMatches(List<ToolMemory.DocumentItem> docs, string searchQuery)
    {
        if (docs.Count == 0)
            return docs;

        var normalizedQuery = searchQuery.Trim().Replace('\\', '/').TrimStart('/');
        var byPath = normalizedQuery.Contains('/');
        var queryFileName = Path.GetFileName(normalizedQuery);

        return docs
            .Where(x => byPath
                ? string.Equals((x.DocPath ?? string.Empty).Replace('\\', '/').TrimStart('/'), normalizedQuery, StringComparison.OrdinalIgnoreCase)
                : string.Equals(Path.GetFileName(x.DocName ?? string.Empty), queryFileName, StringComparison.OrdinalIgnoreCase)
                  || string.Equals(Path.GetFileName(x.DocPath ?? string.Empty), queryFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private string TryRenderLastDeterministicAnswer(string language)
    {
        if (_mem.LastDeterministicRender is null
            || string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.Kind)
            || string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.DataJson))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(_mem.LastDeterministicRender.DataJson);
            return RenderDeterministicInventoryFromData(_mem.LastDeterministicRender.Kind, doc.RootElement, language).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static JsonElement CreateSummaryStatusJson(ToolMemory.SummaryStatusSnapshot snapshot)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            mode = snapshot.Mode,
            total = snapshot.Total,
            missingStored = snapshot.MissingStored,
            staleStored = snapshot.StaleStored,
            profileMissing = snapshot.ProfileMissing,
            items = snapshot.Items.Select(x => new
            {
                docId = x.DocId,
                docPath = x.DocPath,
                docName = x.DocName,
                category = x.Category,
                categoryRef = x.CategoryRef,
                categoryPath = x.CategoryPath,
                summaryState = x.SummaryState,
                capabilityBProfileState = x.CapabilityBProfileState,
                capabilityBHasBackofficeProfile = x.CapabilityBHasBackofficeProfile,
                capabilityBReasons = x.CapabilityBReasons,
                hasActiveSummaryJob = x.HasActiveSummaryJob,
                activeSummaryJobId = x.ActiveSummaryJobId,
                activeSummaryJobType = x.ActiveSummaryJobType,
                activeSummaryJobStatus = x.ActiveSummaryJobStatus,
                activeSummaryJobExecutionMode = x.ActiveSummaryJobExecutionMode,
                activeSummaryJobRuntimeCapabilityKey = x.ActiveSummaryJobRuntimeCapabilityKey,
                activeSummaryJobRuntimeCapabilityStatus = x.ActiveSummaryJobRuntimeCapabilityStatus,
                activeSummaryJobEnqueueSource = x.ActiveSummaryJobEnqueueSource,
                activeSummaryJobCampaignId = x.ActiveSummaryJobCampaignId,
                capabilityBReadyToEnqueue = x.CapabilityBReadyToEnqueue,
                capabilityBRecommendedAction = x.CapabilityBRecommendedAction,
                capabilityBPolicyBlocked = x.CapabilityBPolicyBlocked,
                capabilityBPolicyBlockReason = x.CapabilityBPolicyBlockReason,
                capabilityBPriorityScore = x.CapabilityBPriorityScore,
                capabilityBLastJobStatus = x.CapabilityBLastJobStatus,
                capabilityBLastJobFinishedAt = x.CapabilityBLastJobFinishedAt,
                capabilityBLastJobError = x.CapabilityBLastJobError
            }).ToList()
        }));
        return doc.RootElement.Clone();
    }

    private static ToolMemory.SummaryStatusItem BuildSummaryStatusItemSnapshot(JsonElement entry)
        => new()
        {
            DocId = TryGetString(entry, "DocId") ?? TryGetString(entry, "docId") ?? string.Empty,
            DocPath = TryGetString(entry, "DocPath") ?? TryGetString(entry, "docPath") ?? string.Empty,
            DocName = TryGetString(entry, "DocName") ?? TryGetString(entry, "docName") ?? TryGetString(entry, "canonicalName") ?? string.Empty,
            Category = TryGetString(entry, "Category") ?? TryGetString(entry, "category") ?? TryGetString(entry, "categoryCanonicalName") ?? string.Empty,
            CategoryRef = TryGetString(entry, "CategoryRef") ?? TryGetString(entry, "categoryRef"),
            CategoryPath = TryGetString(entry, "CategoryPath") ?? TryGetString(entry, "categoryPath"),
            SummaryState = TryGetString(entry, "SummaryState") ?? TryGetString(entry, "summaryState") ?? "missing",
            CapabilityBProfileState = TryGetString(entry, "CapabilityBProfileState") ?? TryGetString(entry, "capabilityBProfileState"),
            CapabilityBHasBackofficeProfile = TryGetBool(entry, "CapabilityBHasBackofficeProfile") ?? TryGetBool(entry, "capabilityBHasBackofficeProfile") ?? false,
            CapabilityBReasons = ReadStringArray(entry, "CapabilityBReasons").Concat(ReadStringArray(entry, "capabilityBReasons")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            HasActiveSummaryJob = TryGetBool(entry, "HasActiveSummaryJob") ?? TryGetBool(entry, "hasActiveSummaryJob") ?? false,
            ActiveSummaryJobId = TryGetString(entry, "ActiveSummaryJobId") ?? TryGetString(entry, "activeSummaryJobId"),
            ActiveSummaryJobType = TryGetString(entry, "ActiveSummaryJobType") ?? TryGetString(entry, "activeSummaryJobType"),
            ActiveSummaryJobStatus = TryGetString(entry, "ActiveSummaryJobStatus") ?? TryGetString(entry, "activeSummaryJobStatus"),
            ActiveSummaryJobExecutionMode = TryGetString(entry, "ActiveSummaryJobExecutionMode") ?? TryGetString(entry, "activeSummaryJobExecutionMode"),
            ActiveSummaryJobRuntimeCapabilityKey = TryGetString(entry, "ActiveSummaryJobRuntimeCapabilityKey") ?? TryGetString(entry, "activeSummaryJobRuntimeCapabilityKey"),
            ActiveSummaryJobRuntimeCapabilityStatus = TryGetString(entry, "ActiveSummaryJobRuntimeCapabilityStatus") ?? TryGetString(entry, "activeSummaryJobRuntimeCapabilityStatus"),
            ActiveSummaryJobEnqueueSource = TryGetString(entry, "ActiveSummaryJobEnqueueSource") ?? TryGetString(entry, "activeSummaryJobEnqueueSource"),
            ActiveSummaryJobCampaignId = TryGetString(entry, "ActiveSummaryJobCampaignId") ?? TryGetString(entry, "activeSummaryJobCampaignId"),
            CapabilityBReadyToEnqueue = TryGetBool(entry, "CapabilityBReadyToEnqueue") ?? TryGetBool(entry, "capabilityBReadyToEnqueue") ?? false,
            CapabilityBRecommendedAction = TryGetString(entry, "CapabilityBRecommendedAction") ?? TryGetString(entry, "capabilityBRecommendedAction"),
            CapabilityBPolicyBlocked = TryGetBool(entry, "CapabilityBPolicyBlocked") ?? TryGetBool(entry, "capabilityBPolicyBlocked") ?? false,
            CapabilityBPolicyBlockReason = TryGetString(entry, "CapabilityBPolicyBlockReason") ?? TryGetString(entry, "capabilityBPolicyBlockReason"),
            CapabilityBPriorityScore = TryGetDouble(entry, "CapabilityBPriorityScore") ?? TryGetDouble(entry, "capabilityBPriorityScore"),
            CapabilityBLastJobStatus = TryGetString(entry, "CapabilityBLastJobStatus") ?? TryGetString(entry, "capabilityBLastJobStatus"),
            CapabilityBLastJobFinishedAt = TryGetString(entry, "CapabilityBLastJobFinishedAt") ?? TryGetString(entry, "capabilityBLastJobFinishedAt"),
            CapabilityBLastJobError = TryGetString(entry, "CapabilityBLastJobError") ?? TryGetString(entry, "capabilityBLastJobError")
        };

    private static List<string> ReadStringArray(JsonElement entry, string propertyName)
    {
        if (!entry.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return arr.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ToolMemory.CategorySnapshot> ParsePresentedCategories(JsonElement result)
    {
        var list = new List<ToolMemory.CategorySnapshot>();
        JsonElement items;
        var hasItems = result.TryGetProperty("items", out items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            hasItems = result.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            return list;

        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var categoryPath = TryGetString(entry, "categoryPath") ?? TryGetString(entry, "path") ?? string.Empty;
            var displayName = TryGetString(entry, "canonicalName") ?? TryGetString(entry, "name") ?? categoryPath;
            var aliases = new List<string>();
            if (entry.TryGetProperty("aliases", out var aliasArray) && aliasArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var alias in aliasArray.EnumerateArray())
                {
                    if (alias.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alias.GetString()))
                        aliases.Add(alias.GetString()!.Trim());
                }
            }

            list.Add(new ToolMemory.CategorySnapshot
            {
                CategoryRef = TryGetString(entry, "categoryRef") ?? displayName,
                CategoryPath = categoryPath,
                DisplayName = displayName,
                Ordinal = TryGetInt(entry, "ordinal") ?? TryGetInt(entry, "displayOrder") ?? 0,
                TotalDocuments = TryGetInt(entry, "totalDocuments") ?? TryGetInt(entry, "documentCount") ?? 0,
                Aliases = aliases
            });
        }

        return list;
    }


    private static bool HasCategoryScope(RouterPlan plan, string toolName)
    {
        var call = plan.ToolCalls.LastOrDefault(x => string.Equals(x.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (call is null)
            return false;

        return !string.IsNullOrWhiteSpace(GetStringArg(call.Args, "categoryRef"))
            || !string.IsNullOrWhiteSpace(GetStringArg(call.Args, "categoryPath"))
            || !string.IsNullOrWhiteSpace(GetStringArg(call.Args, "path"))
            || !string.IsNullOrWhiteSpace(GetStringArg(call.Args, "category"));
    }
}
