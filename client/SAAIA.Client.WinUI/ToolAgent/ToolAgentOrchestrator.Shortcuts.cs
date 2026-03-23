using System;
using System.Collections.Generic;
using System.Globalization;
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
    private async Task<(bool handled, string finalAnswer, object? sourcesPayload, string? routerIntent, IReadOnlyList<string> toolNames)> TryHandleDeterministicShortcutAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string displayUserMessage,
        string effectiveUserMessage,
        string interactionLanguage,
        DocumentRefResolver.AnalysisResult docResolution,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress,
        Stopwatch swTotalPipeline)
    {
        _ = chatHistory;
        _ = docResolution;
        _ = swTotalPipeline;

        if (LooksLikeShortCourtesyMessage(effectiveUserMessage, out var courtesyLanguage))
        {
            var answer = BuildCourtesyReply(courtesyLanguage);
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer, null, "chat.general", Array.Empty<string>());
        }

        if (LooksLikeExplicitLiveSummaryFollowUp(effectiveUserMessage)
            && !string.IsNullOrWhiteSpace(_mem.LastRequestedDocumentRef))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseSummary(interactionLanguage));
            var live = await RunKnownDocumentSummaryFlowAsync(
                effectiveUserMessage,
                _mem.LastRequestedDocumentRef!,
                DocumentSummaryRequestKind.SummaryReadOrLive,
                ct,
                onDelta,
                onProgress).ConfigureAwait(false);
            return (true, live.finalAnswer, live.sourcesPayload, "rag.summarize_doc", new[] { "summary.flow" });
        }

        if (LooksLikeReplayLastAnswerRequest(effectiveUserMessage))
        {
            var replay = TryRenderLastDeterministicAnswer(interactionLanguage);
            if (string.IsNullOrWhiteSpace(replay) && !LastAnswerRequiresStructuredReplay())
                replay = (_mem.LastAssistantAnswer ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(replay))
            {
                onPhase?.Invoke(DeterministicAgentText.PhaseWriting(interactionLanguage));
                onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(interactionLanguage));
                await EmitDeterministicTextAsync(replay, onDelta, ct).ConfigureAwait(false);
                return (true, replay, null, _mem.LastDeterministicRender?.RouterIntent ?? _mem.LastRouterIntent, new[] { "inventory.rendered" });
            }
        }

        if (LooksLikeDirectAllDocumentsRequest(effectiveUserMessage))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var rawRes = await _api.DocumentsListAsync(null, null, null, 500, 0, ct).ConfigureAwait(false);
            var res = CreateCanonicalDocumentsListJson(rawRes);
            RememberDeterministicRenderFromJson("list", res, "inventory.list", displayUserMessage);
            _mem.LastInventoryAction = "list_documents";
            _mem.LastSummaryStatusSnapshot = null;

            var answer = RenderDeterministicInventoryFromData("list", res, interactionLanguage).Trim();
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer, null, "inventory.list", new[] { "documents.list", "inventory.rendered" });
        }

        if (LooksLikeDirectCategoriesRequest(effectiveUserMessage))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var res = await _api.DocumentsCategoriesAsync(path: null, categoryRef: null, limit: 100, offset: 0, ct).ConfigureAwait(false);
            RememberDeterministicRenderFromJson("categories", res, "inventory.categories", displayUserMessage);
            _mem.LastPresentedCategories = ParsePresentedCategories(res);
            _mem.LastInventoryAction = "categories";
            _mem.LastSummaryStatusSnapshot = null;

            var answer = RenderDeterministicInventoryFromData("categories", res, interactionLanguage).Trim();
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer, null, "inventory.categories", new[] { "documents.categories", "inventory.rendered" });
        }

        if (LooksLikeDirectCatalogStatsRequest(effectiveUserMessage))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var res = await _api.DocumentsStatsAsync(path: null, categoryRef: null, ct).ConfigureAwait(false);
            RememberDeterministicRenderFromJson("stats", res, "inventory.stats", displayUserMessage);
            _mem.LastInventoryAction = "catalog_stats";
            _mem.LastSummaryStatusSnapshot = null;

            var answer = RenderDeterministicInventoryFromData("stats", res, interactionLanguage).Trim();
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer, null, "inventory.stats", new[] { "documents.stats", "inventory.rendered" });
        }

        if (LooksLikeDirectSummaryStatusRequest(effectiveUserMessage, out var summaryMode, out var summaryState))
        {
            if (!_api.HasAdminKey)
            {
                var denied = DeterministicAgentText.ToolFailureAdminRequired(interactionLanguage);
                await EmitDeterministicTextAsync(denied, onDelta, ct).ConfigureAwait(false);
                return (true, denied, null, "inventory.summary_status", new[] { "summary.status" });
            }

            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var category = TryExtractFollowUpCategoryRef(effectiveUserMessage, out var requestedCategoryRef)
                ? ResolveCategorySnapshotFromReference(requestedCategoryRef)
                : _mem.LastResolvedCategory;

            var isPresentMode = string.Equals(summaryState, "present", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(summaryMode, "count", StringComparison.OrdinalIgnoreCase))
            {
                var res = isPresentMode
                    ? await _api.AdminSummaryPresentCountAsync(category?.CategoryPath, category?.CategoryRef, ct).ConfigureAwait(false)
                    : await _api.AdminSummaryMissingCountAsync(category?.CategoryPath, category?.CategoryRef, ct).ConfigureAwait(false);
                _mem.LastSummaryStatusSnapshot = new ToolMemory.SummaryStatusSnapshot
                {
                    CategoryPath = category?.CategoryPath,
                    CategoryRef = category?.CategoryRef,
                    Mode = isPresentMode ? "present" : "missing"
                };
                UpdateSummaryStatusSnapshotFromJson(res);
                RememberDeterministicRenderFromJson("summary_status_count", res, "inventory.summary_status", displayUserMessage);
                _mem.LastInventoryAction = "summary_status_count";
                var answer = RenderDeterministicInventoryFromData("summary_status_count", res, interactionLanguage).Trim();
                await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
                return (true, answer, null, "inventory.summary_status", new[] { isPresentMode ? "summary.present.count" : "summary.status.count", "inventory.rendered" });
            }
            else
            {
                var limit = 100;
                var res = isPresentMode
                    ? await _api.AdminSummaryPresentAsync(limit, 0, category?.CategoryPath, category?.CategoryRef, ct).ConfigureAwait(false)
                    : await _api.AdminSummaryMissingAsync(limit, 0, category?.CategoryPath, category?.CategoryRef, ct).ConfigureAwait(false);
                _mem.LastSummaryStatusSnapshot = new ToolMemory.SummaryStatusSnapshot
                {
                    CategoryPath = category?.CategoryPath,
                    CategoryRef = category?.CategoryRef,
                    Mode = isPresentMode ? "present" : "missing"
                };
                UpdateSummaryStatusSnapshotFromJson(res);
                RememberDeterministicRenderFromJson("summary_status_list", res, "inventory.summary_status", displayUserMessage);
                _mem.LastInventoryAction = "summary_status_list";
                var answer = RenderDeterministicInventoryFromData("summary_status_list", res, interactionLanguage).Trim();
                await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
                return (true, answer, null, "inventory.summary_status", new[] { isPresentMode ? "summary.present.list" : "summary.status.list", "inventory.rendered" });
            }
        }

        if (TryExtractExactCategoryDocumentsRef(effectiveUserMessage, out var exactCategoryDocumentsRef))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var category = ResolveCategorySnapshotFromReference(exactCategoryDocumentsRef, exactCategoryDocumentsRef);
            var rawRes = await _api.DocumentsListAsync(category?.CategoryPath, category?.CategoryRef ?? exactCategoryDocumentsRef, null, 80, 0, ct).ConfigureAwait(false);
            var res = CreateCanonicalDocumentsListJsonCore(rawRes, category?.CategoryPath ?? exactCategoryDocumentsRef);
            RememberDeterministicRenderFromJson("list", res, "inventory.list", displayUserMessage);
            _mem.LastInventoryAction = "list_documents_by_category";
            if (category is not null)
                _mem.LastResolvedCategory = category;
            var answer = RenderDeterministicInventoryFromData("list", res, interactionLanguage).Trim();
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer, null, "inventory.list", new[] { "documents.list", "inventory.rendered" });
        }

        if (TryExtractExactDocumentSearchQuery(effectiveUserMessage, out var exactDocumentSearchQuery))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var rawRes = await _api.DocumentsListAsync(categoryPath: null, categoryRef: null, q: exactDocumentSearchQuery, limit: 20, offset: 0, ct: ct).ConfigureAwait(false);
            var res = CreateCanonicalDocumentsListJson(rawRes);
            RememberDeterministicRenderFromJson("list", res, "inventory.find", displayUserMessage);
            _mem.LastInventoryAction = "search_documents";
            var answer = RenderDeterministicInventoryFromData("list", res, interactionLanguage).Trim();
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer, null, "inventory.find", new[] { "documents.search", "inventory.rendered" });
        }

        if (TryExtractExactCategoryStatsRef(effectiveUserMessage, out var exactCategoryStatsRef))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var category = ResolveCategorySnapshotFromReference(exactCategoryStatsRef, exactCategoryStatsRef);
            var res = await _api.DocumentsStatsAsync(category?.CategoryPath, category?.CategoryRef ?? exactCategoryStatsRef, ct).ConfigureAwait(false);
            RememberDeterministicRenderFromJson("stats", res, "inventory.stats", displayUserMessage);
            _mem.LastInventoryAction = "category_stats";
            if (category is not null)
                _mem.LastResolvedCategory = category;
            var answer = RenderDeterministicInventoryFromData("stats", res, interactionLanguage).Trim();
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer, null, "inventory.stats", new[] { "documents.stats", "inventory.rendered" });
        }

        if (LooksLikeDirectAdminRescanRequest(effectiveUserMessage))
        {
            if (!_api.HasAdminKey)
            {
                var denied = DeterministicAgentText.ToolFailureAdminRequired(interactionLanguage);
                await EmitDeterministicTextAsync(denied, onDelta, ct).ConfigureAwait(false);
                return (true, denied, null, "admin.catalog.rescan_now", new[] { "admin.catalog.rescan_now" });
            }

            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ToolProgress("admin.catalog.rescan_now", interactionLanguage));

            try
            {
                var res = await _api.AdminCatalogRescanNowAsync(ct).ConfigureAwait(false);
                var answer = DeterministicAgentText.AdminRescanQueued(interactionLanguage, TryGetString(res, "jobId"));
                await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
                return (true, answer, null, "admin.catalog.rescan_now", new[] { "admin.catalog.rescan_now" });
            }
            catch (Exception ex)
            {
                var failure = DeterministicAgentText.ToolFailureToolFailed(interactionLanguage) + " " + ex.Message;
                await EmitDeterministicTextAsync(failure, onDelta, ct).ConfigureAwait(false);
                return (true, failure, null, "admin.catalog.rescan_now", new[] { "admin.catalog.rescan_now" });
            }
        }

        if (TryExtractExactAdminReindexDocumentRef(effectiveUserMessage, out var reindexDocRef))
        {
            if (!_api.HasAdminKey)
            {
                var denied = DeterministicAgentText.ToolFailureAdminRequired(interactionLanguage);
                await EmitDeterministicTextAsync(denied, onDelta, ct).ConfigureAwait(false);
                return (true, denied, null, "admin.ingestion.reindex", new[] { "admin.ingestion.reindex" });
            }

            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ToolProgress("admin.ingestion.reindex", interactionLanguage));

            try
            {
                var resolved = await ResolveDocRefAsync(reindexDocRef, ct).ConfigureAwait(false);
                if (resolved is null)
                {
                    var missing = LocalizedStrings.NoDocumentsFound(interactionLanguage);
                    await EmitDeterministicTextAsync(missing, onDelta, ct).ConfigureAwait(false);
                    return (true, missing, null, "admin.ingestion.reindex", new[] { "admin.ingestion.reindex" });
                }

                var res = await _api.AdminIngestionReindexAsync(resolved.DocPath, ct).ConfigureAwait(false);
                var label = string.IsNullOrWhiteSpace(resolved.DocName) ? reindexDocRef : resolved.DocName;
                var answer = DeterministicAgentText.AdminReindexQueued(interactionLanguage, label, TryGetString(res, "jobId"));
                await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
                return (true, answer, null, "admin.ingestion.reindex", new[] { "admin.ingestion.reindex" });
            }
            catch (Exception ex)
            {
                var failure = DeterministicAgentText.ToolFailureToolFailed(interactionLanguage) + " " + ex.Message;
                await EmitDeterministicTextAsync(failure, onDelta, ct).ConfigureAwait(false);
                return (true, failure, null, "admin.ingestion.reindex", new[] { "admin.ingestion.reindex" });
            }
        }

        if (TryExtractFollowUpCategoryRef(effectiveUserMessage, out var directCategoryRef))
        {
            if (LooksLikeDirectDocumentsByCategoryRequest(effectiveUserMessage))
            {
                onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
                onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

                var category = ResolveCategorySnapshotFromReference(directCategoryRef);
                var rawRes = await _api.DocumentsListAsync(category?.CategoryPath, category?.CategoryRef ?? directCategoryRef, null, 80, 0, ct).ConfigureAwait(false);
                var res = CreateCanonicalDocumentsListJsonCore(rawRes, category?.CategoryPath ?? directCategoryRef);
                RememberDeterministicRenderFromJson("list", res, "inventory.list", displayUserMessage);
                _mem.LastInventoryAction = "list_documents_by_category";
                if (category is not null)
                    _mem.LastResolvedCategory = category;
                var answer = RenderDeterministicInventoryFromData("list", res, interactionLanguage).Trim();
                await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
                return (true, answer, null, "inventory.list", new[] { "documents.list", "inventory.rendered" });
            }

            if (LooksLikeDirectCategoryStatsRequest(effectiveUserMessage))
            {
                onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
                onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

                var category = ResolveCategorySnapshotFromReference(directCategoryRef);
                var res = await _api.DocumentsStatsAsync(category?.CategoryPath, category?.CategoryRef ?? directCategoryRef, ct).ConfigureAwait(false);
                RememberDeterministicRenderFromJson("stats", res, "inventory.stats", displayUserMessage);
                _mem.LastInventoryAction = "category_stats";
                if (category is not null)
                    _mem.LastResolvedCategory = category;
                var answer = RenderDeterministicInventoryFromData("stats", res, interactionLanguage).Trim();
                await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
                return (true, answer, null, "inventory.stats", new[] { "documents.stats", "inventory.rendered" });
            }
        }

        if (LooksLikeSummaryStatusListFollowUp(effectiveUserMessage)
            && _mem.LastSummaryStatusSnapshot is not null
            && _api.HasAdminKey)
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var limit = Math.Clamp(_mem.LastSummaryStatusSnapshot.Total > 0 ? _mem.LastSummaryStatusSnapshot.Total : 100, 1, 500);
            var data = string.Equals(_mem.LastSummaryStatusSnapshot.Mode, "present", StringComparison.OrdinalIgnoreCase)
                ? await _api.AdminSummaryPresentAsync(limit, 0, _mem.LastSummaryStatusSnapshot.CategoryPath, _mem.LastSummaryStatusSnapshot.CategoryRef, ct).ConfigureAwait(false)
                : await _api.AdminSummaryMissingAsync(limit, 0, _mem.LastSummaryStatusSnapshot.CategoryPath, _mem.LastSummaryStatusSnapshot.CategoryRef, ct).ConfigureAwait(false);
            UpdateSummaryStatusSnapshotFromJson(data);

            RememberDeterministicRenderFromJson("summary_status_list", data, "inventory.summary_status", displayUserMessage);
            _mem.LastInventoryAction = "summary_status_list";

            var answer = RenderDeterministicInventoryFromData("summary_status_list", data, interactionLanguage).Trim();
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            var toolName = string.Equals(_mem.LastSummaryStatusSnapshot.Mode, "present", StringComparison.OrdinalIgnoreCase)
                ? "summary.present.list"
                : "summary.status.list";
            return (true, answer, null, "inventory.summary_status", new[] { toolName, "inventory.rendered" });
        }

        if (TryExtractFollowUpCategoryRef(effectiveUserMessage, out var categoryRef))
        {
            if (string.Equals(_mem.LastInventoryAction, "list_documents_by_category", StringComparison.OrdinalIgnoreCase))
            {
                onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
                onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

                var category = ResolveCategorySnapshotFromReference(categoryRef);
                var rawRes = await _api.DocumentsListAsync(category?.CategoryPath, category?.CategoryRef ?? categoryRef, null, 80, 0, ct).ConfigureAwait(false);
                var res = CreateCanonicalDocumentsListJsonCore(rawRes, category?.CategoryPath ?? categoryRef);
                RememberDeterministicRenderFromJson("list", res, "inventory.list", displayUserMessage);
                _mem.LastInventoryAction = "list_documents_by_category";
                if (category is not null)
                    _mem.LastResolvedCategory = category;

                var answer = RenderDeterministicInventoryFromData("list", res, interactionLanguage).Trim();
                await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
                return (true, answer, null, "inventory.list", new[] { "documents.list", "inventory.rendered" });
            }

            if (string.Equals(_mem.LastInventoryAction, "category_stats", StringComparison.OrdinalIgnoreCase))
            {
                onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
                onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

                var category = ResolveCategorySnapshotFromReference(categoryRef);
                var res = await _api.DocumentsStatsAsync(category?.CategoryPath, category?.CategoryRef ?? categoryRef, ct).ConfigureAwait(false);
                RememberDeterministicRenderFromJson("stats", res, "inventory.stats", displayUserMessage);
                _mem.LastInventoryAction = "category_stats";
                if (category is not null)
                    _mem.LastResolvedCategory = category;

                var answer = RenderDeterministicInventoryFromData("stats", res, interactionLanguage).Trim();
                await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
                return (true, answer, null, "inventory.stats", new[] { "documents.stats", "inventory.rendered" });
            }
        }

        if (LooksLikeMalformedGuidedCommandRequest(effectiveUserMessage))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseClarification(interactionLanguage));
            var guidance = LocalizedStrings.GuidedCommandHelpRequired(interactionLanguage);
            await EmitDeterministicTextAsync(guidance, onDelta, ct).ConfigureAwait(false);
            return (true, guidance, null, "meta.help", Array.Empty<string>());
        }

        return (false, string.Empty, null, null, Array.Empty<string>());
    }

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

        return ResolveCategorySnapshotFromReference(categoryRef, categoryPath);
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

                snapshot.Items.Add(new ToolMemory.SummaryStatusItem
                {
                    DocId = TryGetString(entry, "DocId") ?? TryGetString(entry, "docId") ?? string.Empty,
                    DocPath = TryGetString(entry, "DocPath") ?? TryGetString(entry, "docPath") ?? string.Empty,
                    DocName = TryGetString(entry, "DocName") ?? TryGetString(entry, "docName") ?? TryGetString(entry, "canonicalName") ?? string.Empty,
                    Category = TryGetString(entry, "Category") ?? TryGetString(entry, "category") ?? TryGetString(entry, "categoryCanonicalName") ?? string.Empty,
                    SummaryState = TryGetString(entry, "SummaryState") ?? TryGetString(entry, "summaryState") ?? "missing"
                });
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

                snapshot.Items.Add(new ToolMemory.SummaryStatusItem
                {
                    DocId = TryGetString(entry, "DocId") ?? TryGetString(entry, "docId") ?? string.Empty,
                    DocPath = TryGetString(entry, "DocPath") ?? TryGetString(entry, "docPath") ?? string.Empty,
                    DocName = TryGetString(entry, "DocName") ?? TryGetString(entry, "docName") ?? TryGetString(entry, "canonicalName") ?? string.Empty,
                    Category = TryGetString(entry, "Category") ?? TryGetString(entry, "category") ?? TryGetString(entry, "categoryCanonicalName") ?? string.Empty,
                    SummaryState = TryGetString(entry, "SummaryState") ?? TryGetString(entry, "summaryState") ?? "missing"
                });
            }
        }

        _mem.LastSummaryStatusSnapshot = snapshot;
    }

    private ToolMemory.CategorySnapshot? ResolveCategorySnapshotFromReference(string? categoryRef, string? categoryPath = null)
    {
        var normalizedRef = NormalizeShortcutToken(categoryRef);
        var normalizedPath = NormalizeShortcutToken(categoryPath);

        if (_mem.LastPresentedCategories is { Count: > 0 })
        {
            foreach (var category in _mem.LastPresentedCategories)
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
        }

        if (_mem.LastResolvedCategory is not null)
        {
            if (!string.IsNullOrWhiteSpace(normalizedRef)
                && (NormalizeShortcutToken(_mem.LastResolvedCategory.CategoryRef) == normalizedRef
                    || NormalizeShortcutToken(_mem.LastResolvedCategory.DisplayName) == normalizedRef
                    || NormalizeShortcutToken(_mem.LastResolvedCategory.CategoryPath) == normalizedRef
                    || NormalizeShortcutToken(_mem.LastResolvedCategory.Ordinal.ToString(CultureInfo.InvariantCulture)) == normalizedRef
                    || _mem.LastResolvedCategory.Aliases.Any(x => NormalizeShortcutToken(x) == normalizedRef)))
            {
                return CloneCategorySnapshot(_mem.LastResolvedCategory);
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
        => CreateCanonicalDocumentsListJsonCore(rawData, null);

    private JsonElement CreateCanonicalDocumentsListJsonCore(JsonElement rawData, string? scopePath)
    {
        var (docs, limit, offset, total, endOfList, dropped) = DocumentListHelper.Sanitize(rawData, _mem);
        var normalizedScopePath = NormalizeCategoryPathArg(scopePath) ?? NormalizeCategoryPathArg(TryGetString(rawData, "scopePath"));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            scopePath = normalizedScopePath,
            limit,
            offset,
            total,
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
            items = snapshot.Items.Select(x => new
            {
                docId = x.DocId,
                docPath = x.DocPath,
                docName = x.DocName,
                category = x.Category,
                summaryState = x.SummaryState
            }).ToList()
        }));
        return doc.RootElement.Clone();
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

    private async Task<string> TryTranslateLastAnswerOneShotAsync(string language, CancellationToken ct, Action<string>? onDelta)
    {
        if (string.IsNullOrWhiteSpace(_mem.LastAssistantAnswer))
            return string.Empty;

        if (ShouldTranslateFromDeterministicRender())
        {
            var deterministic = TryRenderLastDeterministicAnswer(language);
            if (!string.IsNullOrWhiteSpace(deterministic))
            {
                await EmitDeterministicTextAsync(deterministic, onDelta, ct).ConfigureAwait(false);
                return deterministic;
            }
        }

        var protectedTerms = CollectProtectedTranslationTerms(_mem.LastAssistantAnswer);
        var protectedAnswer = ApplyProtectedTranslationTerms(_mem.LastAssistantAnswer, protectedTerms, out var placeholders);
        var protectedTermsBlock = protectedTerms.Count == 0
            ? string.Empty
            : $@"
Protected canonical values (keep them exactly as written; do not translate, rename or alter them):
- {string.Join("\n- ", protectedTerms)}
";

        var system = $@"
You are SAAIA assistant.
Translate the provided assistant answer faithfully.
Target language: {language}
Rules:
- Keep the same factual content.
- Do not invent, add, remove or merge list entries.
- Preserve tokens like [[open|...]] exactly.
- Preserve placeholders like __KEEP_0001__ exactly and do not translate them.
- Return plain text only.
{protectedTermsBlock}
";

        var user = $@"
ASSISTANT_ANSWER_TO_TRANSLATE:
{protectedAnswer}
";

        var translated = await CompleteTextResponseAsync(system, user, ct, onDelta: null).ConfigureAwait(false);
        translated = RestoreProtectedTranslationTerms(translated, placeholders);
        await EmitDeterministicTextAsync(translated, onDelta, ct).ConfigureAwait(false);
        return translated;
    }

    private bool ShouldTranslateFromDeterministicRender()
    {
        if (_mem.LastDeterministicRender is null || !IsInventoryIntent(_mem.LastDeterministicRender.RouterIntent ?? _mem.LastRouterIntent))
            return false;

        var lastAnswer = (_mem.LastAssistantAnswer ?? string.Empty).Trim();
        if (lastAnswer.Length == 0)
            return false;

        var renderedInLastLanguage = TryRenderLastDeterministicAnswer(_mem.LastAnswerLanguage ?? _mem.LastLanguage);
        if (string.IsNullOrWhiteSpace(renderedInLastLanguage))
            return false;

        return NormalizeReplayComparableText(lastAnswer) == NormalizeReplayComparableText(renderedInLastLanguage);
    }

    private static string NormalizeReplayComparableText(string text)
        => Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ");

    private IReadOnlyList<string> CollectProtectedTranslationTerms(string assistantAnswer)
    {
        var answer = assistantAnswer ?? string.Empty;
        var terms = new HashSet<string>(StringComparer.Ordinal);

        void AddTerm(string? value)
        {
            value = value?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
                return;
            if (value.Contains("\n", StringComparison.Ordinal) || value.Contains("\r", StringComparison.Ordinal))
                return;
            if (answer.IndexOf(value, StringComparison.Ordinal) < 0)
                return;
            terms.Add(value);
        }

        foreach (var category in _mem.LastPresentedCategories)
        {
            AddTerm(category.DisplayName);
            AddTerm(category.CategoryPath);
        }

        if (_mem.LastResolvedCategory is not null)
        {
            AddTerm(_mem.LastResolvedCategory.DisplayName);
            AddTerm(_mem.LastResolvedCategory.CategoryPath);
        }

        foreach (var doc in _mem.LastListedDocuments)
        {
            AddTerm(doc.DocName);
            AddTerm(doc.DocPath);
            AddTerm(doc.CategoryPath);
            AddTerm(doc.Category);
        }

        if (_mem.LastFocusedDocument is not null)
        {
            AddTerm(_mem.LastFocusedDocument.DocName);
            AddTerm(_mem.LastFocusedDocument.DocPath);
            AddTerm(_mem.LastFocusedDocument.CategoryPath);
            AddTerm(_mem.LastFocusedDocument.Category);
        }

        if (_mem.LastDeterministicRender is not null && !string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.DataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(_mem.LastDeterministicRender.DataJson);
                CollectProtectedTermsFromJson(doc.RootElement, AddTerm);
            }
            catch
            {
                // best effort only
            }
        }

        return terms
            .OrderByDescending(x => x.Length)
            .ThenBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectProtectedTermsFromJson(JsonElement element, Action<string?> addTerm)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        if (property.NameEquals("name")
                            || property.NameEquals("path")
                            || property.NameEquals("docName")
                            || property.NameEquals("docPath")
                            || property.NameEquals("category")
                            || property.NameEquals("categoryPath")
                            || property.NameEquals("DocName")
                            || property.NameEquals("DocPath")
                            || property.NameEquals("Category")
                            || property.NameEquals("CategoryPath"))
                        {
                            addTerm(property.Value.GetString());
                        }
                    }
                    else
                    {
                        CollectProtectedTermsFromJson(property.Value, addTerm);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectProtectedTermsFromJson(item, addTerm);
                break;
        }
    }

    private static string ApplyProtectedTranslationTerms(string text, IReadOnlyList<string> terms, out Dictionary<string, string> placeholders)
    {
        placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
        var protectedText = text ?? string.Empty;
        if (terms.Count == 0)
            return protectedText;

        var index = 1;
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term) || protectedText.IndexOf(term, StringComparison.Ordinal) < 0)
                continue;

            var token = $"__KEEP_{index:0000}__";
            placeholders[token] = term;
            protectedText = protectedText.Replace(term, token, StringComparison.Ordinal);
            index++;
        }

        return protectedText;
    }

    private static string RestoreProtectedTranslationTerms(string text, IReadOnlyDictionary<string, string> placeholders)
    {
        var restored = text ?? string.Empty;
        foreach (var pair in placeholders)
            restored = restored.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return restored;
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

    private static readonly RegexOptions ShortcutRegexOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static bool MatchesExplicitShortcut(string normalizedMessage, params string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(normalizedMessage))
            return false;

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(normalizedMessage, pattern, ShortcutRegexOptions))
                return true;
        }

        return false;
    }

    private static bool ContainsConversationalContentCue(string normalizedMessage)
    {
        if (string.IsNullOrWhiteSpace(normalizedMessage))
            return false;

        return Regex.IsMatch(normalizedMessage,
            @"\b(?:pourquoi|comment|explique(?: moi)?|de quoi parle|qu est ce que .* signifie|que signifie|what is|what does|why|how\s+(?:do|does|did|can|could|would|to|is|are)|explain|meaning|about this document|worum geht|warum|wie\s+(?:funktioniert|kann|ist)|erklar(?:e|en)?|de que trata|por que|como\s+(?:funciona|puedo|se)|explica(?:me)?|que significa|do que trata|porque|como\s+(?:funciona|posso)|explica(?:r)?|o que significa|di cosa parla|perche|come\s+(?:funziona|posso)|spiega)\b",
            ShortcutRegexOptions);
    }

    private static bool LooksLikeShortCourtesyMessage(string? message, out string language)
    {
        var s = NormalizeShortcutToken(message);
        language = string.Empty;
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var fr = new HashSet<string>(StringComparer.Ordinal)
        {
            "merci", "merci beaucoup", "ok merci", "super merci", "parfait merci"
        };
        var en = new HashSet<string>(StringComparer.Ordinal)
        {
            "thanks", "thank you", "thanks a lot", "many thanks", "ok thanks"
        };
        var es = new HashSet<string>(StringComparer.Ordinal)
        {
            "gracias", "muchas gracias", "ok gracias"
        };
        var pt = new HashSet<string>(StringComparer.Ordinal)
        {
            "obrigado", "obrigada", "muito obrigado", "muito obrigada"
        };
        var de = new HashSet<string>(StringComparer.Ordinal)
        {
            "danke", "vielen dank", "danke schon", "danke schoen"
        };
        var it = new HashSet<string>(StringComparer.Ordinal)
        {
            "grazie", "grazie mille", "prego"
        };

        if (fr.Contains(s)) { language = "fr"; return true; }
        if (en.Contains(s)) { language = "en"; return true; }
        if (es.Contains(s)) { language = "es"; return true; }
        if (pt.Contains(s)) { language = "pt"; return true; }
        if (de.Contains(s)) { language = "de"; return true; }
        if (it.Contains(s)) { language = "it"; return true; }
        return false;
    }

    private static string BuildCourtesyReply(string language)
        => NormalizeLanguageCode(language) switch
        {
            "en" => "You're welcome.",
            "es" => "De nada.",
            "pt" => "De nada.",
            "de" => "Gern geschehen.",
            "it" => "Prego.",
            _ => "Avec plaisir."
        };

    private static bool LooksLikeExplicitLiveSummaryFollowUp(string? message)
    {
        var s = NormalizeShortcutToken(message);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        return MatchesExplicitShortcut(s,
            @"^(?:fais|faites|donne(?:s)?|montre|affiche)(?:[-\s]+moi)?\s+(?:un\s+)?resume\s+live$",
            @"^(?:make|do|give\s+me|show)\s+(?:a\s+)?live\s+summary$",
            @"^(?:haz|dame|muestrame)\s+(?:un\s+)?resumen\s+en\s+vivo$",
            @"^(?:faz|mostra|da\s+me)\s+(?:um\s+)?resumo\s+ao\s+vivo$",
            @"^(?:mach|zeige|gib\s+mir)\s+(?:eine\s+)?live\s+zusammenfassung$",
            @"^(?:fai|mostra|dammi)\s+(?:un\s+)?riassunto\s+live$",
            @"^(?:resume|summarize|resumen|resumo|riassunto|zusammenfassung)\s+(?:live|en\s+vivo|ao\s+vivo)$");
    }

    private static bool LooksLikeReplayLastAnswerRequest(string? message)
    {
        var s = NormalizeShortcutToken(message);
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s,
            @"\b(?:what did you (?:just )?(?:list|say)|repeat that|show that again|remind me|qu est ce que tu viens de me lister|qu est ce que tu viens de lister|qu est ce que tu viens de dire|rappelle moi|reaffiche|redis moi|was hast du gerade gesagt|wiederhole das|que acabas de listar|repitelo|o que voce acabou de listar|repete isso|cosa hai appena detto|ripeti|combien j ai dit)\b",
            ShortcutRegexOptions);
    }

    private static bool LooksLikeSummaryStatusListFollowUp(string? message)
    {
        var s = NormalizeShortcutToken(message);
        return MatchesExplicitShortcut(s,
            @"^(?:oui\s+)?(?:donne(?:s)?|envoie(?:s)?|envoye|liste|montre|affiche)(?:[-\s]+moi)?\s+la\s+liste(?:\s+des\s+documents)?$",
            @"^(?:je\s+veux|j\s+veux|je\s+voudrais)\s+la\s+liste(?:\s+des\s+documents)?$",
            @"^(?:list|show|display|give\s+me|send\s+me)\s+(?:the\s+)?list(?:\s+of\s+documents)?$",
            @"^(?:si\s+)?(?:lista|dame|muestrame|ensename|enviame)\s+(?:la\s+)?lista(?:\s+de\s+documentos)?$",
            @"^(?:sim\s+)?(?:lista|mostra|envia(?:me)?|manda(?:me)?|da\s+me)\s+(?:a\s+)?lista(?:\s+de\s+documentos)?$",
            @"^(?:ja\s+)?(?:liste|zeige(?:\s+mir)?|gib\s+mir|sende\s+mir)\s+(?:die\s+)?liste(?:\s+der\s+dokumente)?$",
            @"^(?:si\s+)?(?:lista|dammi|mostra(?:mi)?|invia(?:mi)?|manda(?:mi)?)\s+(?:la\s+)?lista(?:\s+dei\s+documenti)?$",
            @"^(?:which\s+ones|what\s+are\s+they|quels\s+sont\s+ils|cu[aá]les\s+son|welche\s+sind\s+das|quali\s+sono)$");
    }

    private static bool LooksLikeDirectDocumentsByCategoryRequest(string? message)
        => TryExtractExactCategoryDocumentsRef(message, out _);

    private static bool LooksLikeDirectCategoryStatsRequest(string? message)
        => TryExtractExactCategoryStatsRef(message, out _);

    private static bool LooksLikeDirectAllDocumentsRequest(string? message)
        => false;

    private static bool LooksLikeDirectCategoriesRequest(string? message)
        => MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptCategories);

    private static bool LooksLikeDirectCatalogStatsRequest(string? message)
        => MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptCatalogStats);

    private bool LastAnswerRequiresStructuredReplay()
    {
        if (_mem.LastDeterministicRender is not null)
            return true;

        if (IsInventoryIntent(_mem.LastRouterIntent))
            return true;

        return _mem.LastToolNames.Any(IsInventoryLikeToolName);
    }

    private static bool LooksLikeDirectSummaryStatusRequest(string? message, out string mode, out string state)
    {
        mode = string.Empty;
        state = string.Empty;

        if (MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptSummaryMissingCount))
        {
            mode = "count";
            state = "missing";
            return true;
        }

        if (MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptSummaryMissingList))
        {
            mode = "list";
            state = "missing";
            return true;
        }

        if (MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptSummaryPresentCount))
        {
            mode = "count";
            state = "present";
            return true;
        }

        if (MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptSummaryPresentList))
        {
            mode = "list";
            state = "present";
            return true;
        }

        return false;
    }

    private bool TryExtractFollowUpCategoryRef(string? message, out string categoryRef)
    {
        categoryRef = string.Empty;
        var s = NormalizeShortcutToken(message);
        if (s.Length == 0)
            return false;

        var match = Regex.Match(s, @"\b(?:categorie|category|categoria|kategorie)\s+(?<ref>[a-z0-9_\-/]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            categoryRef = match.Groups["ref"].Value.Trim();
            return categoryRef.Length > 0;
        }

        if (TryExtractPresentedCategoryByOrdinalToken(s, out categoryRef))
            return true;

        if (_mem.LastResolvedCategory is not null
            && Regex.IsMatch(s, @"\b(?:cette\s+categorie|this\s+category|that\s+category|esta\s+categoria|esa\s+categoria|essa\s+categoria|diese\s+kategorie|questa\s+categoria|ses\s+documents|its\s+documents|sus\s+documentos|seus\s+documentos|ihre\s+dokumente|i\s+suoi\s+documenti|celle\s+de\s+la\s+categorie)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            categoryRef = !string.IsNullOrWhiteSpace(_mem.LastResolvedCategory.CategoryRef)
                ? _mem.LastResolvedCategory.CategoryRef
                : _mem.LastResolvedCategory.DisplayName;
            return !string.IsNullOrWhiteSpace(categoryRef);
        }

        foreach (var category in _mem.LastPresentedCategories)
        {
            if (Regex.IsMatch(s, $@"\b{Regex.Escape(NormalizeShortcutToken(category.DisplayName))}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(s, $@"\b{Regex.Escape(NormalizeShortcutToken(category.CategoryPath))}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || category.Aliases.Any(alias => Regex.IsMatch(s, $@"\b{Regex.Escape(NormalizeShortcutToken(alias))}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            {
                categoryRef = !string.IsNullOrWhiteSpace(category.CategoryRef) ? category.CategoryRef : category.DisplayName;
                return true;
            }
        }

        return false;
    }

    private bool TryExtractPresentedCategoryByOrdinalToken(string normalizedMessage, out string categoryRef)
    {
        categoryRef = string.Empty;
        if (_mem.LastPresentedCategories is null || _mem.LastPresentedCategories.Count == 0)
            return false;

        var ordinalPatterns = new[]
        {
            @"\b(?:pour\s+la|pour\s+le|et\s+la|et\s+le|dans\s+la|dans\s+le|la|le|categorie|category|categoria|kategorie|fur\s+die|fur\s+den|for\s+the|para\s+la|para\s+el|para\s+a|per\s+la|per\s+il)\s+(?<ord>\d{1,2})(?:ere|eme|er|e|o|a)?\b",
            @"\b(?<ord>\d{1,2})(?:ere|eme|er|e|o|a)\b"
        };

        foreach (var pattern in ordinalPatterns)
        {
            var match = Regex.Match(normalizedMessage, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups["ord"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
                continue;

            var category = _mem.LastPresentedCategories.FirstOrDefault(x => x.Ordinal == ordinal);
            if (category is null)
                continue;

            categoryRef = !string.IsNullOrWhiteSpace(category.CategoryRef) ? category.CategoryRef : category.DisplayName;
            return !string.IsNullOrWhiteSpace(categoryRef);
        }

        var ordinalWordMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["premiere"] = 1,
            ["premier"] = 1,
            ["first"] = 1,
            ["primera"] = 1,
            ["primer"] = 1,
            ["primeira"] = 1,
            ["primeiro"] = 1,
            ["erste"] = 1,
            ["ersten"] = 1,
            ["prima"] = 1,
            ["primo"] = 1,
            ["deuxieme"] = 2,
            ["second"] = 2,
            ["seconde"] = 2,
            ["2nde"] = 2,
            ["segunda"] = 2,
            ["segundo"] = 2,
            ["zweite"] = 2,
            ["zweiten"] = 2,
            ["seconda"] = 2,
            ["secondo"] = 2,
            ["third"] = 3,
            ["troisieme"] = 3,
            ["tercera"] = 3,
            ["tercero"] = 3,
            ["terceira"] = 3,
            ["terceiro"] = 3,
            ["dritte"] = 3,
            ["dritten"] = 3,
            ["terza"] = 3,
            ["terzo"] = 3,
            ["quatrieme"] = 4,
            ["fourth"] = 4,
            ["cuarta"] = 4,
            ["cuarto"] = 4,
            ["quarta"] = 4,
            ["quarto"] = 4,
            ["vierte"] = 4,
            ["vierten"] = 4,
            ["cinquieme"] = 5,
            ["fifth"] = 5,
            ["quinta"] = 5,
            ["quinto"] = 5,
            ["funfte"] = 5,
            ["funften"] = 5
        };

        foreach (var pair in ordinalWordMap)
        {
            if (!Regex.IsMatch(normalizedMessage, $@"\b{Regex.Escape(pair.Key)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;

            var category = _mem.LastPresentedCategories.FirstOrDefault(x => x.Ordinal == pair.Value);
            if (category is null)
                continue;

            categoryRef = !string.IsNullOrWhiteSpace(category.CategoryRef) ? category.CategoryRef : category.DisplayName;
            return !string.IsNullOrWhiteSpace(categoryRef);
        }

        return false;
    }

    private static bool LooksLikeMalformedGuidedCommandRequest(string? message)
    {
        var s = NormalizeShortcutToken(message);
        if (s.Length == 0 || ContainsConversationalContentCue(s))
            return false;

        if (LooksLikeDirectCategoriesRequest(s)
            || LooksLikeDirectCatalogStatsRequest(s)
            || LooksLikeDirectDocumentsByCategoryRequest(s)
            || LooksLikeDirectCategoryStatsRequest(s)
            || LooksLikeDirectSummaryStatusRequest(s, out _, out _)
            || LooksLikeDirectAdminRescanRequest(s)
            || LooksLikeDirectAdminReindexRequest(s))
        {
            return false;
        }

        return LooksLikeMalformedCategoriesCommand(s)
            || LooksLikeMalformedCatalogStatsCommand(s)
            || LooksLikeMalformedCategoryScopedCommand(s)
            || LooksLikeMalformedSummaryStatusCommand(s)
            || LooksLikeMalformedAdminCatalogCommand(s);
    }

    private static bool LooksLikeMalformedCategoriesCommand(string normalizedMessage)
    {
        var hasCategory = Regex.IsMatch(normalizedMessage, @"\b(?:categorie|categories|category|categoria|categorias|kategorie|kategorien|hauptordner|top level|top-level)\b", ShortcutRegexOptions);
        if (!hasCategory)
            return false;

        return Regex.IsMatch(normalizedMessage, @"\b(?:liste|list|show|display|give|donne|montre|montres|affiche|quels|quelles|what are|lista|mostra|zeige|gib)\b", ShortcutRegexOptions);
    }

    private static bool LooksLikeMalformedCatalogStatsCommand(string normalizedMessage)
    {
        var hasStats = Regex.IsMatch(normalizedMessage, @"\b(?:stat|stats|statistique|statistiques|statistics|estadisticas|estatisticas|statistiken|statistiche)\b", ShortcutRegexOptions);
        if (!hasStats)
            return false;

        if (Regex.IsMatch(normalizedMessage, @"\b(?:document|documents|documento|documentos|dokument|dokumente|riassunto|summary|resume)\b", ShortcutRegexOptions))
            return false;

        var hasCommandVerb = Regex.IsMatch(normalizedMessage, @"\b(?:liste|list|show|display|give|donne|montre|montres|affiche|dame|muestrame|lista|mostra|zeige|gib)\b", ShortcutRegexOptions);
        return hasCommandVerb || normalizedMessage.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4;
    }

    private static bool LooksLikeMalformedCategoryScopedCommand(string normalizedMessage)
    {
        var hasCategory = Regex.IsMatch(normalizedMessage, @"\b(?:categorie|category|categoria|kategorie)\b", ShortcutRegexOptions);
        if (!hasCategory)
            return false;

        var hasDocuments = Regex.IsMatch(normalizedMessage, @"\b(?:document|documents|fichier|fichiers|file|files|documentos|archivos|dokumente|documenti)\b", ShortcutRegexOptions);
        var hasStats = Regex.IsMatch(normalizedMessage, @"\b(?:stat|stats|statistique|statistiques|statistics|estadisticas|estatisticas|statistiken|statistiche)\b", ShortcutRegexOptions);
        if (!hasDocuments && !hasStats)
            return false;

        var hasCommandVerb = Regex.IsMatch(normalizedMessage, @"\b(?:liste|list|show|display|give|donne|montre|affiche|muestre|muestrame|lista|mostra|zeige|gib)\b", ShortcutRegexOptions);
        return hasCommandVerb || Regex.IsMatch(normalizedMessage, @"^(?:documents?|files?|fichiers?|documentos|dokumente|documenti|stats?|statistics|statistiques)\b", ShortcutRegexOptions);
    }

    private static bool LooksLikeMalformedSummaryStatusCommand(string normalizedMessage)
    {
        var hasSummary = Regex.IsMatch(normalizedMessage, @"\b(?:resume|resumen|resumo|zusammenfassung|riassunto|summary|summaries)\b", ShortcutRegexOptions);
        var hasDocuments = Regex.IsMatch(normalizedMessage, @"\b(?:document|documents|documentos|dokumente|documenti)\b", ShortcutRegexOptions);
        if (!hasSummary || !hasDocuments)
            return false;

        var hasState = Regex.IsMatch(normalizedMessage, @"\b(?:stocke|stored|almacenad|armazenad|gespeichert|salvat|sans|without|sin|sem|missing|manquant|mancant|avec|with|con|com|present|presents|presenti|vorhanden)\b", ShortcutRegexOptions);
        if (!hasState)
            return false;

        return Regex.IsMatch(normalizedMessage, @"\b(?:combien|how many|count|liste|list|show|display|give|donne|montre|affiche|cuantos|quantos|wie viele|quanti)\b", ShortcutRegexOptions);
    }

    private static bool LooksLikeMalformedAdminCatalogCommand(string normalizedMessage)
    {
        return (Regex.IsMatch(normalizedMessage, @"\b(?:rescan|rescann|re scan|scan)\b", ShortcutRegexOptions)
                && Regex.IsMatch(normalizedMessage, @"\b(?:catalogue|catalog|catalogo|katalog)\b", ShortcutRegexOptions))
            || (Regex.IsMatch(normalizedMessage, @"\b(?:reindex|reindexe|reindexer|reindic|reindiz)\b", ShortcutRegexOptions)
                && Regex.IsMatch(normalizedMessage, @"\b(?:document|documents|documento|documentos|dokument|dokumente|file|fichier|fichiers)\b", ShortcutRegexOptions));
    }

    private static bool LooksLikeDirectAdminRescanRequest(string? message)
        => MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptAdminRescan);

    private static bool LooksLikeDirectAdminReindexRequest(string? message)
        => TryExtractExactAdminReindexDocumentRef(message, out _);

    private static bool TryExtractExactCategoryDocumentsRef(string? message, out string categoryRef)
        => TryMatchCanonicalDynamicPrompt(message, ClientUiText.BuildPromptCategoryDocuments, out categoryRef);

    private static bool TryExtractExactCategoryStatsRef(string? message, out string categoryRef)
        => TryMatchCanonicalDynamicPrompt(message, ClientUiText.BuildPromptCategoryStats, out categoryRef);

    private static bool TryExtractExactDocumentSearchQuery(string? message, out string query)
        => TryMatchCanonicalDynamicPrompt(message, ClientUiText.BuildPromptSearchDocuments, out query);

    private static bool TryExtractExactAdminReindexDocumentRef(string? message, out string documentRef)
        => TryMatchCanonicalDynamicPrompt(message, ClientUiText.BuildPromptAdminReindex, out documentRef);

    private static bool MatchesCanonicalStaticPrompt(string? message, Func<string?, string> promptBuilder)
    {
        var rawMessage = (message ?? string.Empty).Trim();
        var normalizedMessage = NormalizeShortcutToken(rawMessage);
        if (normalizedMessage.Length == 0 || ContainsConversationalContentCue(normalizedMessage))
            return false;

        foreach (var language in ClientUiText.SupportedLanguageCodes())
        {
            if (string.Equals(rawMessage, promptBuilder(language).Trim(), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryMatchCanonicalDynamicPrompt(string? message, Func<string?, string, string> promptBuilder, out string value)
    {
        value = string.Empty;
        var rawMessage = (message ?? string.Empty).Trim();
        var normalizedMessage = NormalizeShortcutToken(rawMessage);
        if (normalizedMessage.Length == 0 || ContainsConversationalContentCue(normalizedMessage))
            return false;

        const string token = "__value__";
        foreach (var language in ClientUiText.SupportedLanguageCodes())
        {
            var template = promptBuilder(language, token).Trim();
            var tokenIndex = template.IndexOf(token, StringComparison.Ordinal);
            if (tokenIndex < 0)
                continue;

            var prefix = template[..tokenIndex];
            var suffix = template[(tokenIndex + token.Length)..];
            if (prefix.Length > 0 && !rawMessage.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (suffix.Length > 0 && !rawMessage.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var length = rawMessage.Length - prefix.Length - suffix.Length;
            if (length <= 0)
                continue;

            var captured = rawMessage.Substring(prefix.Length, length).Trim();
            if (captured.Length == 0)
                continue;

            value = captured;
            return true;
        }

        return false;
    }

    private static string NormalizeShortcutToken(string? value)
    {
        var normalized = StripDiacritics(value ?? string.Empty).Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}/_-]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }
}
