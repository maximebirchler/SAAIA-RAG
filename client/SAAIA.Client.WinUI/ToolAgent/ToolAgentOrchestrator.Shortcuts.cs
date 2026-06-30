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

        if (LooksLikeVagueVerificationScopeQuestion(effectiveUserMessage))
        {
            var answer = BuildVagueVerificationScopeClarification(interactionLanguage);
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            RememberPendingClarification("verification_scope", displayUserMessage, null, interactionLanguage);
            return (true, answer, null, "clarification", Array.Empty<string>());
        }

        if (LooksLikeHelpOnlyAdminReindexDisplayText(effectiveUserMessage))
        {
            var answer = LocalizedStrings.HelpOnlyCommandUseHelp(interactionLanguage);
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer, null, "meta.help", Array.Empty<string>());
        }

        var recentAdminStatus = await TryHandleRecentAdminOperationStatusAsync(
            effectiveUserMessage,
            interactionLanguage,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (recentAdminStatus.handled)
            return (true, recentAdminStatus.finalAnswer, null, recentAdminStatus.routerIntent, recentAdminStatus.toolNames);

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
            ResetFocusedDocumentIfNoDocumentItems(res);
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
            _mem.PromoteCategoriesToWorkspace(_mem.LastPresentedCategories);
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

        if (LooksLikeDirectTreeRequest(effectiveUserMessage))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var res = await _api.DocumentsTreeAsync(path: null, categoryRef: null, depth: 20, format: "markdown", ct).ConfigureAwait(false);
            RememberDeterministicRenderFromJson("tree", res, "inventory.tree", displayUserMessage);
            _mem.LastInventoryAction = "tree";
            _mem.LastSummaryStatusSnapshot = null;

            var answer = RenderDeterministicInventoryFromData("tree", res, interactionLanguage).Trim();
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer, null, "inventory.tree", new[] { "documents.tree", "inventory.rendered" });
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
                : null;

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
                UpdateLastListedDocumentsFromSummaryStatusSnapshot();
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
            ResetFocusedDocumentIfNoDocumentItems(res);
            RememberDeterministicRenderFromJson("list", res, "inventory.list", displayUserMessage);
            _mem.LastInventoryAction = "list_documents_by_category";
            if (category is not null)
                _mem.LastResolvedCategory = category;
            var answer = RenderDeterministicInventoryFromData("list", res, interactionLanguage).Trim();
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer, null, "inventory.list", new[] { "documents.list", "inventory.rendered" });
        }

        if (!ShouldSkipExactItemPreRouterShortcut(effectiveUserMessage)
            && TryExtractDocumentContentSearchTopic(effectiveUserMessage, out var documentContentTopic))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseRag(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var categoryScope = ResolveRagCategoryScope(effectiveUserMessage);
            var primaryQuery = BuildDocumentContentSearchPrimaryQuery(documentContentTopic);
            if (string.IsNullOrWhiteSpace(primaryQuery))
                primaryQuery = CollapseWhitespace(documentContentTopic);
            var shouldPreferExplainedDocumentAnswer = LooksLikeDocumentContentSelectionExplanationRequest(effectiveUserMessage);
            var singleArgs = CreateJsonArgs(new
            {
                query = primaryQuery,
                topK = shouldPreferExplainedDocumentAnswer ? 8 : 4,
                category = categoryScope,
                mode = "balanced"
            });
            var ragResult = await TryExecRagSearchOrEmptyAsync(singleArgs, ct).ConfigureAwait(false);
            var toolName = "rag.search";
            if (!HasRagHits(ragResult))
            {
                var naturalQuery = BuildDocumentContentSearchNaturalQuery(effectiveUserMessage, primaryQuery);
                if (!string.IsNullOrWhiteSpace(naturalQuery))
                {
                    ragResult = await TryExecRagSearchRawOrEmptyAsync(naturalQuery, 8, categoryScope, "balanced", ct).ConfigureAwait(false);
                }
            }

            if (ShouldExpandDocumentContentSearch(effectiveUserMessage, ragResult))
            {
                var queries = BuildDocumentContentSearchQueries(effectiveUserMessage, documentContentTopic)
                    .Where(query => !string.Equals(NormalizeRagQueryForRetrieval(query), primaryQuery, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (queries.Count > 0)
                {
                    var args = CreateJsonArgs(new
                    {
                        queries,
                        topK = shouldPreferExplainedDocumentAnswer ? 6 : 4,
                        category = categoryScope,
                        mode = "balanced"
                    });
                    var expandedResult = await TryExecRagMultiSearchOrEmptyAsync(args, ct).ConfigureAwait(false);
                    if (IsBetterDocumentContentSearchCoverage(ragResult, expandedResult))
                    {
                        ragResult = expandedResult;
                        toolName = "rag.multi_search";
                    }
                }

                if (!HasRagHits(ragResult) || ShouldExpandDocumentContentSearch(effectiveUserMessage, ragResult))
                {
                    var expandedQueries = await TryBuildTranslatedDocumentContentSearchQueriesAsync(
                        documentContentTopic,
                        interactionLanguage,
                        ct).ConfigureAwait(false);
                    foreach (var query in expandedQueries)
                        AddDistinctQuery(queries, query);

                    if (queries.Count > 0)
                    {
                        var expandedArgs = CreateJsonArgs(new
                        {
                            queries = queries.Take(8).ToArray(),
                            topK = shouldPreferExplainedDocumentAnswer ? 6 : 4,
                            category = categoryScope,
                            mode = "balanced"
                        });
                        var translatedResult = await TryExecRagMultiSearchOrEmptyAsync(expandedArgs, ct).ConfigureAwait(false);
                        if (IsBetterDocumentContentSearchCoverage(ragResult, translatedResult))
                        {
                            ragResult = translatedResult;
                            toolName = "rag.multi_search";
                        }
                    }
                }
            }

            var toolResults = new ToolResults();
            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = toolName,
                Result = ragResult
            });

            var sources = DeriveSourcesFromRagHits(toolResults);
            var answer = string.Empty;
            if (ShouldUseWriterForDocumentContentSearchAnswer(effectiveUserMessage, ragResult))
            {
                var writerPlan = new RouterPlan
                {
                    Mode = "auto",
                    Language = interactionLanguage,
                    Intent = "rag.answer",
                    ResponseFormat = "auto",
                    ToolCalls =
                    [
                        new RouterPlan.ToolCall
                        {
                            Name = toolName,
                            Args = CreateJsonArgs(new
                            {
                                query = primaryQuery,
                                topK = shouldPreferExplainedDocumentAnswer ? 8 : 4,
                                category = categoryScope,
                                mode = "balanced"
                            })
                        }
                    ]
                };

                var (writerAnswer, writerSources) = await AnswerAsync(
                    chatHistory,
                    effectiveUserMessage,
                    writerPlan,
                    toolResults,
                    ct,
                    onDelta,
                    onProgress).ConfigureAwait(false);
                answer = (writerAnswer ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(answer) && writerSources is { Count: > 0 })
                    sources = writerSources;
            }

            if (string.IsNullOrWhiteSpace(answer))
                answer = BuildDocumentContentSearchAnswer(toolResults, documentContentTopic, interactionLanguage);
            _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(sources);
            _mem.LastToolNames = new List<string> { toolName };
            _lastAnswerSource = "shortcut:rag.document_content_search";
            onProgress?.Invoke(string.Empty);
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);

            var sourcesPayload = sources.Count > 0 ? BuildSourcesPayload(sources) : null;
            return (true, answer, sourcesPayload, "rag.search", new[] { toolName });
        }

        if (TryExtractExactDocumentSearchQuery(effectiveUserMessage, out var exactDocumentSearchQuery))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var rawRes = await _api.DocumentsListAsync(categoryPath: null, categoryRef: null, q: exactDocumentSearchQuery, limit: 20, offset: 0, ct: ct).ConfigureAwait(false);
            var res = CreateCanonicalDocumentsListJsonCore(rawRes, scopePath: null, searchQuery: exactDocumentSearchQuery);
            ResetFocusedDocumentIfNoDocumentItems(res);
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
            onProgress?.Invoke(DeterministicAgentText.AdminRescanQueued(interactionLanguage, null));

            try
            {
                var res = await _api.AdminCatalogRescanNowAsync(ct).ConfigureAwait(false);
                var answer = BuildAdminRescanCompletedAnswer(res, interactionLanguage);
                RememberCompletedAdminRescan(res);
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


        if (TryExtractFollowUpCategoryRef(effectiveUserMessage, out var directCategoryRef))
        {
            if (LooksLikeDirectDocumentsByCategoryRequest(effectiveUserMessage))
            {
                onPhase?.Invoke(DeterministicAgentText.PhaseTools(interactionLanguage));
                onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

                var category = ResolveCategorySnapshotFromReference(directCategoryRef);
                var rawRes = await _api.DocumentsListAsync(category?.CategoryPath, category?.CategoryRef ?? directCategoryRef, null, 80, 0, ct).ConfigureAwait(false);
                var res = CreateCanonicalDocumentsListJsonCore(rawRes, category?.CategoryPath ?? directCategoryRef);
                ResetFocusedDocumentIfNoDocumentItems(res);
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
            UpdateLastListedDocumentsFromSummaryStatusSnapshot();

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
                ResetFocusedDocumentIfNoDocumentItems(res);
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
            @"\b(?:pourquoi|comment|peux\s+tu|peux-tu|pouvez\s+vous|explique(?: moi)?|de quoi parle|vue d ensemble|qu est ce que .* signifie|que signifie|what is|what does|why|can you|could you|would you|how\s+(?:do|does|did|can|could|would|to|is|are)|overview|useful|important|business\s+questions?|source\s+grounded|source-grounded|which\s+(?:pdfs?|documents?|sources?)|about this category|about this document|explain|meaning|worum geht|warum|wie\s+(?:funktioniert|kann|ist)|erklar(?:e|en)?|de que trata|por que|como\s+(?:funciona|puedo|se)|explica(?:me)?|que significa|do que trata|porque|como\s+(?:funciona|posso)|explica(?:r)?|o que significa|di cosa parla|perche|come\s+(?:funziona|posso)|spiega)\b",
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

    private static bool LooksLikeDirectTreeRequest(string? message)
        => MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptCatalogTree);

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


    private static bool LooksLikeRecentAdminOperationStatusFollowUp(string? message)
    {
        var s = NormalizeShortcutToken(message);
        if (string.IsNullOrWhiteSpace(s))
            return false;

        return Regex.IsMatch(s, @"\b(?:c est fait|c est fini|c est termine|fini|termine|ou en est|statut|status|done yet|is it done|is it finished|still running|toujours en cours|toujours en train|est ce termine|est ce fini)\b", ShortcutRegexOptions);
    }

    private static bool LooksLikeHelpOnlyAdminReindexDisplayText(string? message)
    {
        var rawMessage = (message ?? string.Empty).Trim();
        if (rawMessage.Length == 0)
            return false;

        var normalizedExact = NormalizeExactPromptText(rawMessage);
        if (normalizedExact.Length == 0 || ContainsConversationalContentCue(normalizedExact))
            return false;

        if (MatchesCanonicalDynamicDisplayPrompt(rawMessage, ClientUiText.BuildPromptAdminReindexDisplay))
            return true;

        if (TryMatchCanonicalDynamicPrompt(rawMessage, ClientUiText.BuildPromptAdminReindex, out _))
            return true;

        var normalizedShortcut = NormalizeShortcutToken(rawMessage);
        if (normalizedShortcut.Length == 0)
            return false;

        return Regex.IsMatch(
            normalizedShortcut,
            @"^(?:cible de reindexation|action aide reindexer le document|help action reindex document|reindex target|reindex the document|relance l ingestion du document|objetivo de reindexacion|accion de ayuda reindexar documento|reindexa el documento|destino da reindexacao|acao da ajuda reindexar documento|reindexa o documento|neuindexierungsziel|hilfeaktion dokument neu indexieren|reindiziere das dokument|destinazione reindicizzazione|azione guida reindicizza documento|reindicizza il documento)\b",
            ShortcutRegexOptions);
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
            || LooksLikeDirectTreeRequest(s)
            || LooksLikeDirectAdminRescanRequest(s))
        {
            return false;
        }

        if (LooksLikeDocumentaryContentQuestionBeyondCatalogCommand(s))
            return false;

        return LooksLikeMalformedCategoriesCommand(s)
            || LooksLikeMalformedCatalogStatsCommand(s)
            || LooksLikeMalformedTreeCommand(s)
            || LooksLikeMalformedCategoryScopedCommand(s)
            || LooksLikeMalformedSummaryStatusCommand(s)
            || LooksLikeMalformedAdminCatalogRescanCommand(s);
    }

    private static bool LooksLikeDocumentaryContentQuestionBeyondCatalogCommand(string normalizedMessage)
    {
        if (string.IsNullOrWhiteSpace(normalizedMessage))
            return false;

        var mentionsDocumentarySource = Regex.IsMatch(
            normalizedMessage,
            @"\b(?:pdf|document|documents|doc|docs|source|sources|corpus|file|files|fichier|fichiers)\b",
            ShortcutRegexOptions);
        if (!mentionsDocumentarySource)
            return false;

        return Regex.IsMatch(
            normalizedMessage,
            @"\b(?:parle|parlent|contient|contiennent|traite|traitent|about|cover|covers|overview|useful|important|business|meilleur|meilleure|meilleurs|meilleures|best|pire|pires|worst|tester|test|robustesse|robustness|preuve|preuves|evidence|evidences|limite|limites|risk|risque|risques|compare|comparer|comparaison|resume|resumer|synthese|synthese|explique|expliquer)\b",
            ShortcutRegexOptions);
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

    private static bool LooksLikeMalformedTreeCommand(string normalizedMessage)
    {
        var hasTree = Regex.IsMatch(normalizedMessage, @"(?:tree|arborescence|arbre|árbol|baum|albero)", ShortcutRegexOptions);
        if (!hasTree)
            return false;

        return Regex.IsMatch(normalizedMessage, @"(?:show|display|give|list|donne|montre|affiche|dame|muestrame|mostra|zeige|gib)", ShortcutRegexOptions);
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

    private static bool LooksLikeMalformedAdminCatalogRescanCommand(string normalizedMessage)
    {
        return Regex.IsMatch(normalizedMessage, @"\b(?:rescan|rescann|re scan|scan)\b", ShortcutRegexOptions)
            && Regex.IsMatch(normalizedMessage, @"\b(?:catalogue|catalog|catalogo|katalog)\b", ShortcutRegexOptions);
    }

    private static bool LooksLikeDirectAdminRescanRequest(string? message)
        => MatchesCanonicalStaticPrompt(message, ClientUiText.BuildPromptAdminRescan);

    private static bool LooksLikeDirectAdminReindexRequest(string? message)
        => false;

    private static bool TryExtractExactCategoryDocumentsRef(string? message, out string categoryRef)
        => TryMatchCanonicalDynamicPrompt(message, ClientUiText.BuildPromptCategoryDocuments, out categoryRef);

    private static bool TryExtractExactCategoryStatsRef(string? message, out string categoryRef)
        => TryMatchCanonicalDynamicPrompt(message, ClientUiText.BuildPromptCategoryStats, out categoryRef);

    private static bool TryExtractExactDocumentSearchQuery(string? message, out string query)
        => TryMatchCanonicalDynamicPrompt(message, ClientUiText.BuildPromptSearchDocuments, out query);

    private static bool TryExtractDocumentContentSearchTopic(string? message, out string topic)
    {
        topic = string.Empty;
        var raw = CollapseWhitespace(message ?? string.Empty);
        if (raw.Length == 0)
            return false;

        var asksForDocuments = Regex.IsMatch(
            raw,
            @"(?i)\b(?:trouve\w*|cherche\w*|liste\w*|donne\w*|montre\w*|affiche\w*|find|search|list|show|give|busc\w*|procuro|procur\w*|such\w*|zeig\w*|mostr\w*|cerc\w*)\b.{0,180}\b(?:documents?|sources?|fichiers?|files?|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                raw,
                @"(?i)\b(?:documents?|sources?|fichiers?|files?|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\s+(?:qui\s+|que\s+|die\s+|che\s+)?(?:parle\w*|mentionne\w*|traite\w*|contien\w*|about|regarding|concerning|habl\w*|mencion\w*|trat\w*|contien\w*|fal\w*|mencion\w*|trat\w*|contem|enth\w*|sprech\w*|erwaehn\w*|erw[a\u00e4]hn\w*|parl\w*|menzion\w*|riguard\w*)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                raw,
                @"(?i)\b(?:quels?|quelles?|which|what|qu[e\u00e9]|quais?|welche|quali)\s+(?:documents?|sources?|fichiers?|files?|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\b",
                RegexOptions.CultureInvariant);

        if (!asksForDocuments)
            return false;

        var asksAboutContent = Regex.IsMatch(
            raw,
            @"(?i)\b(?:parle\w*|mentionne\w*|traite\w*|contien\w*|about|regarding|concerning|habl\w*|mencion\w*|trat\w*|contien\w*|fal\w*|contem|sprech\w*|erwaehn\w*|erw[a\u00e4]hn\w*|dar[u\u00fc]ber|parl\w*|menzion\w*|riguard\w*)\b",
            RegexOptions.CultureInvariant);
        if (!asksAboutContent)
            return false;

        topic = TryExtractDocumentContentSearchTopicAnchor(raw);
        if (string.IsNullOrWhiteSpace(topic))
            topic = NormalizeRagQueryForRetrieval(raw);

        return !string.IsNullOrWhiteSpace(topic)
            && !string.Equals(topic, raw, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldExpandDocumentContentSearch(string? message, JsonElement currentResult)
    {
        if (!HasRagHits(currentResult))
            return true;

        var hits = EnumerateRagHitSummaries(currentResult)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (hits.Count == 0)
            return true;

        var distinctDocs = CountDistinctDocumentContentSearchSources(hits);
        if (LooksLikeDocumentContentSelectionExplanationRequest(message))
            return distinctDocs < 3 || hits.Count < 5;

        return LooksLikeBroadSynthesisRequestShape(message) && distinctDocs < 3;
    }

    private static bool ShouldUseWriterForDocumentContentSearchAnswer(string? message, JsonElement currentResult)
    {
        if (!HasRagHits(currentResult))
            return false;

        if (!LooksLikeDocumentContentSelectionExplanationRequest(message))
            return false;

        var hits = EnumerateRagHitSummaries(currentResult)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (hits.Count == 0)
            return false;

        return CountDistinctDocumentContentSearchSources(hits) >= 1;
    }

    private static IEnumerable<RagHitSummary> EnumerateRagHitSummaries(JsonElement result)
    {
        if (!result.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var hit in hits.EnumerateArray())
        {
            if (hit.ValueKind == JsonValueKind.Object)
                yield return BuildRagHitSummary(hit);
        }
    }

    private static bool IsBetterDocumentContentSearchCoverage(JsonElement currentResult, JsonElement candidateResult)
    {
        if (!HasRagHits(candidateResult))
            return false;
        if (!HasRagHits(currentResult))
            return true;

        var currentHits = EnumerateRagHitSummaries(currentResult)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        var candidateHits = EnumerateRagHitSummaries(candidateResult)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();

        var currentDocs = CountDistinctDocumentContentSearchSources(currentHits);
        var candidateDocs = CountDistinctDocumentContentSearchSources(candidateHits);
        if (candidateDocs != currentDocs)
            return candidateDocs > currentDocs;

        if (candidateHits.Count != currentHits.Count)
            return candidateHits.Count > currentHits.Count;

        var currentRichness = currentHits.Sum(ComputeSourceBackedEvidenceRichnessScore);
        var candidateRichness = candidateHits.Sum(ComputeSourceBackedEvidenceRichnessScore);
        return candidateRichness > currentRichness;
    }

    private static int CountDistinctDocumentContentSearchSources(IEnumerable<RagHitSummary> hits)
        => hits
            .Select(static hit => string.IsNullOrWhiteSpace(hit.SourceHash)
                ? (string.IsNullOrWhiteSpace(hit.DocPath) ? hit.DocName : hit.DocPath)
                : hit.SourceHash)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static bool LooksLikeDocumentContentSelectionExplanationRequest(string? message)
    {
        var normalized = NormalizeLexicalLookup(message);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:references?|r[eé]f[eé]rences?|referencias?|refer[eê]ncias?|referenzen?|riferimenti)\b",
                RegexOptions.CultureInvariant)
            && !Regex.IsMatch(
                normalized,
                @"\b(?:documents?|docs?|sources?|fichiers?|pdfs?|pages?|cite|citer|citation|citations|citar|cita|citacion|citacao|zitieren|zitat|citare|citazione)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var hasDocumentScope =
            ContainsDocumentSourceNoun(normalized)
            || Regex.IsMatch(
                normalized,
                @"\b(?:pages?|pdfs?)\b",
                RegexOptions.CultureInvariant)
            || (Regex.IsMatch(
                    normalized,
                    @"\b(?:cite|citer|citation|citations|citar|cita|citacion|citacao|zitieren|zitat|citare|citazione)\b",
                    RegexOptions.CultureInvariant)
                && Regex.IsMatch(
                    normalized,
                    @"\b(?:references?|r[eé]f[eé]rences?|referencias?|refer[eê]ncias?|referenzen?|riferimenti)\b",
                    RegexOptions.CultureInvariant));
        if (!hasDocumentScope)
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:why|useful|relevant|important|best|recommend|recommendation|worth|because|reason|reasons|select|selection|prioriti[sz]e|cite|citation|reference|references|pourquoi|utile|utiles|pertinent|pertinents|importants?|recommande|recommandes|raison|raisons|choisir|selection|selectionner|prioriser|citer|citation|reference|references|porque|por\s+que|util|uteis|relevante|relevantes|importante|importantes|recomienda|recomendar|seleccion|seleccionar|priorizar|citar|cita|citacion|referencia|porque|selecionar|selecao|priorizar|citacao|referencia|warum|nutzlich|nuetzlich|relevant|wichtig|empfehl|auswahl|auswaehlen|auswahlen|priorisieren|zitieren|zitat|referenz|perche|utile|utili|rilevante|rilevanti|importante|importanti|consigli|selezione|selezionare|priorizzare|citare|citazione|riferimento)\b",
            RegexOptions.CultureInvariant);
    }

    private static string TryExtractDocumentContentSearchTopicAnchor(string raw)
    {
        var prefix = Regex.Replace(
            CollapseWhitespace(raw),
            @"(?is)[\.\?!\u00bf\u00a1]*\s*(?:quels?|quelles?|which|what|qu[e\u00e9]|quais?|welche|quali)\s+(?:documents?|sources?|fichiers?|files?|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

        if (string.IsNullOrWhiteSpace(prefix))
            prefix = raw;

        var mentionedCheckMatch = Regex.Match(
            prefix,
            @"(?i)\b(?:si|whether|se|ob)\s+(?<topic>[^?.!;]+?)\s+(?:est|sont|is|are|es|esta|est[a\u00e1]|est[a\u00e3]o|ist|sind|[e\u00e8])\s+(?:mentionn\w*|mentioned|mencion\w*|erwaehn\w*|erw[a\u00e4]hn\w*|menzion\w*)\b",
            RegexOptions.CultureInvariant);
        if (mentionedCheckMatch.Success)
        {
            var cleaned = CleanupDocumentContentSearchTopic(mentionedCheckMatch.Groups["topic"].Value);
            if (!string.IsNullOrWhiteSpace(cleaned) && !ContainsDocumentSourceNoun(cleaned))
                return cleaned;
        }

        var intentMatch = Regex.Match(
            prefix,
            @"(?i)\b(?:je\s+cherche|je\s+veux|j['\u2019]aimerais|i\s+(?:am\s+)?looking\s+for|i\s+need|busco|estoy\s+buscando|procuro|estou\s+a\s+procurar|ich\s+suche|cerco)\b\s*(?<topic>[^?.!\u00bf\u00a1]+)",
            RegexOptions.CultureInvariant);
        if (intentMatch.Success)
        {
            var cleaned = CleanupDocumentContentSearchTopic(intentMatch.Groups["topic"].Value);
            if (!string.IsNullOrWhiteSpace(cleaned) && !ContainsDocumentSourceNoun(cleaned))
                return cleaned;
        }

        var aboutMatch = Regex.Match(
            prefix,
            @"(?i)\b(?:about|regarding|concerning|sur|a\s+propos\s+de|sobre|zu|ueber|[u\u00fc]ber|su|riguardo\s+a)\s+(?<topic>[^?.!\u00bf\u00a1]+)",
            RegexOptions.CultureInvariant);
        if (aboutMatch.Success)
        {
            var cleaned = CleanupDocumentContentSearchTopic(aboutMatch.Groups["topic"].Value);
            if (!string.IsNullOrWhiteSpace(cleaned) && !ContainsDocumentSourceNoun(cleaned))
                return cleaned;
        }

        return string.Empty;
    }

    private static List<string> BuildDocumentContentSearchQueries(string raw, string topic)
    {
        var queries = new List<string>();
        AddDistinctQuery(queries, BuildDocumentContentSearchPrimaryQuery(topic));

        var signalTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(topic))
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsDocumentContentSearchNoiseTerm(term))
            .Take(6)
            .ToArray();
        if (signalTerms.Length > 0)
            AddDistinctQuery(queries, string.Join(' ', signalTerms));

        foreach (var pair in BuildDocumentContentSearchTermPairs(signalTerms).Take(10))
            AddDistinctQuery(queries, pair);

        return queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static string BuildDocumentContentSearchPrimaryQuery(string topic)
        => CollapseWhitespace(NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(topic)));

    private static string BuildDocumentContentSearchNaturalQuery(string raw, string primaryQuery)
    {
        var query = CollapseWhitespace(NormalizeLexicalLookup(raw));
        query = Regex.Replace(query, @"(?i)\b(?:about|regarding|concerning)\b", "on", RegexOptions.CultureInvariant);
        if (query.Length is < 8 or > 240)
            return string.Empty;

        var normalizedPrimary = NormalizeRagQueryForRetrieval(primaryQuery);
        if (!string.IsNullOrWhiteSpace(normalizedPrimary)
            && string.Equals(query, normalizedPrimary, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return query;
    }

    private static IEnumerable<string> BuildDocumentContentSearchTermPairs(IReadOnlyList<string> signalTerms)
    {
        if (signalTerms.Count < 2)
            yield break;

        var max = Math.Min(signalTerms.Count, 6);
        for (var i = 0; i < max; i++)
        {
            for (var j = i + 1; j < max; j++)
            {
                yield return $"{signalTerms[i]} {signalTerms[j]}";
            }
        }
    }

    private static bool IsDocumentContentSearchNoiseTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return normalized is "document" or "documents" or "source" or "sources" or "fichier" or "fichiers"
            or "file" or "files" or "documento" or "documentos" or "fuente" or "fuentes"
            or "fonte" or "fontes" or "dokument" or "dokumente" or "quelle" or "quellen"
            or "documenti" or "fonti" or "conseil" or "conseils" or "advice" or "tips"
            or "consejo" or "consejos" or "conselho" or "conselhos" or "hinweise"
            or "consiglio" or "consigli" or "cherche" or "looking" or "busco" or "procuro"
            or "suche" or "cerco";
    }

    private async Task<IReadOnlyList<string>> TryBuildTranslatedDocumentContentSearchQueriesAsync(
        string topic,
        string language,
        CancellationToken ct)
    {
        topic = CollapseWhitespace(topic);
        if (string.IsNullOrWhiteSpace(topic) || topic.Length > 180)
            return Array.Empty<string>();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));
            var system = """
You generate retrieval query variants for a private document search system.
Return only a JSON object with this shape: {"queries":["..."]}.
Generate concise search queries that preserve the original meaning.
Always include the original wording plus natural translations into French, English, Spanish, Portuguese, German and Italian.
Do not skip a language because the source language is already English.
Do not add explanations, categories, document names, or facts not present in the input.
Keep each query under 90 characters.
""";
            var user = JsonSerializer.Serialize(new
            {
                sourceLanguage = NormalizeLanguageCode(language),
                topic
            });
            var raw = await _llm.CompleteAsync(new[] { ("system", system), ("user", user) }, forceJson: true, timeout.Token).ConfigureAwait(false);
            return ParseDocumentContentSearchQueryVariants(raw, topic);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ParseDocumentContentSearchQueryVariants(string? raw, string originalTopic)
    {
        var queries = new List<string>();
        AddDistinctQuery(queries, originalTopic);
        if (string.IsNullOrWhiteSpace(raw))
            return queries;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("queries", out var nested))
                root = nested;

            if (root.ValueKind != JsonValueKind.Array)
                return queries;

            foreach (var item in root.EnumerateArray())
            {
                AddDocumentContentSearchQueryVariant(queries, item);
            }
        }
        catch
        {
            return queries;
        }

        return queries.Take(12).ToArray();
    }

    private static void AddDocumentContentSearchQueryVariant(List<string> queries, JsonElement item)
    {
        switch (item.ValueKind)
        {
            case JsonValueKind.String:
            {
                var query = CollapseWhitespace(item.GetString() ?? string.Empty);
                if (query.Length is >= 3 and <= 120)
                    AddDistinctQuery(queries, query);
                break;
            }
            case JsonValueKind.Object:
            {
                foreach (var property in item.EnumerateObject())
                    AddDocumentContentSearchQueryVariant(queries, property.Value);
                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var nested in item.EnumerateArray())
                    AddDocumentContentSearchQueryVariant(queries, nested);
                break;
            }
        }
    }

    private static bool ContainsDocumentSourceNoun(string value)
        => Regex.IsMatch(
            value ?? string.Empty,
            @"(?i)\b(?:documents?|sources?|fichiers?|files?|documentos?|fuentes?|fontes?|dokumente?|quellen?|documenti|fonti)\b",
            RegexOptions.CultureInvariant);

    private static string CleanupDocumentContentSearchTopic(string value)
    {
        var topic = CleanupStandaloneTopic(value);
        topic = Regex.Replace(
            topic,
            @"(?i)^(?:des?\s+|les?\s+|the\s+|some\s+|unos?\s+|unas?\s+|os\s+|as\s+|uma?\s+|ein(?:e|en|em|er|es)?\s+|gli\s+|le\s+|i\s+)?(?:conseils?|advice|tips?|consejos?|conselhos?|hinweise|consigli|informazioni|infos?)\s*(?:avec|sur|about|regarding|concerning|sobre|zu|ueber|[u\u00fc]ber|su|riguardo\s+a)?\s*",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();
        topic = Regex.Replace(
            topic,
            @"(?i)\b(?:cela|ceci|this|that|eso|esto|isso|isto|dar[u\u00fc]ber|ne)\s*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim(' ', '.', '?', '!', ':', ';', ',', '"', '\'');
        return topic;
    }

    private static string BuildDocumentContentSearchAnswer(ToolResults toolResults, string topic, string language)
    {
        var hits = EnumerateRagHitSummaries(toolResults).ToList();
        if (hits.Count == 0)
        {
            return NormalizeLanguageCode(language) switch
            {
                "en" => $"I did not find any indexed document content about {topic}.",
                "es" => $"No he encontrado contenido indexado sobre {topic}.",
                "pt" => $"Nao encontrei conteudo indexado sobre {topic}.",
                "de" => $"Ich habe keine indexierten Dokumentinhalte zu {topic} gefunden.",
                "it" => $"Non ho trovato contenuti indicizzati su {topic}.",
                _ => $"Je n'ai trouvé aucun contenu indexé sur {topic}."
            };
        }

        var docs = hits
            .GroupBy(h => string.IsNullOrWhiteSpace(h.DocPath) ? h.DocName : h.DocPath, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var pages = g
                    .Select(h => h.PageStart)
                    .Where(p => p > 0)
                    .Distinct()
                    .OrderBy(p => p)
                    .Take(5)
                    .ToList();
                return new
                {
                    Label = string.IsNullOrWhiteSpace(first.DocName) ? Path.GetFileName(first.DocPath) : first.DocName,
                    Pages = pages,
                    Reason = BuildDocumentContentSearchDocReason(g, language)
                };
            })
            .Take(8)
            .ToList();

        var header = NormalizeLanguageCode(language) switch
        {
            "en" => $"I found {docs.Count} document(s) with indexed content about {topic}:",
            "es" => $"He encontrado {docs.Count} documento(s) con contenido indexado sobre {topic}:",
            "pt" => $"Encontrei {docs.Count} documento(s) com conteudo indexado sobre {topic}:",
            "de" => $"Ich habe {docs.Count} Dokument(e) mit indexiertem Inhalt zu {topic} gefunden:",
            "it" => $"Ho trovato {docs.Count} documento/i con contenuti indicizzati su {topic}:",
            _ => $"J'ai trouvé {docs.Count} document(s) avec du contenu indexé sur {topic} :"
        };

        var lines = docs.Select(d =>
        {
            var pages = d.Pages.Count == 0
                ? string.Empty
                : $" ({SourceBackedPagePrefix(language)}{string.Join(", ", d.Pages)})";
            var reason = string.IsNullOrWhiteSpace(d.Reason)
                ? string.Empty
                : $" - {d.Reason}";
            return $"- {d.Label}{pages}{reason}";
        });

        return header + "\n" + string.Join("\n", lines);
    }

    private static string BuildDocumentContentSearchDocReason(IEnumerable<RagHitSummary> hits, string language)
    {
        var hit = hits
            .OrderByDescending(ComputeSourceBackedEvidenceRichnessScore)
            .ThenByDescending(static h => h.Score)
            .FirstOrDefault();
        if (hit is null)
            return string.Empty;

        var descriptor = hit.MatchedContentCards?
            .Select(static card => CleanSourceBackedOptionTitle(card.Title))
            .FirstOrDefault(static title => !LooksLikeWeakSourceBackedOptionTitle(title));

        if (string.IsNullOrWhiteSpace(descriptor))
        {
            descriptor = new[] { hit.SectionTitle, hit.HeadingPath }
                .Select(static value => CleanSourceBackedOptionTitle(value))
                .FirstOrDefault(static title => !LooksLikeWeakSourceBackedOptionTitle(title));
        }

        if (string.IsNullOrWhiteSpace(descriptor))
            return string.Empty;

        return NormalizeLanguageCode(language) switch
        {
            "en" => $"matched section: {descriptor}",
            "es" => $"seccion encontrada: {descriptor}",
            "pt" => $"secao encontrada: {descriptor}",
            "de" => $"gefundener Abschnitt: {descriptor}",
            "it" => $"sezione trovata: {descriptor}",
            _ => $"section trouvée : {descriptor}"
        };
    }

    private static bool TryExtractExactAdminReindexDocumentRef(string? message, out string documentRef)
    {
        documentRef = string.Empty;
        return false;
    }

    private static bool MatchesCanonicalDynamicDisplayPrompt(string? message, Func<string?, string, string> promptBuilder)
    {
        var rawMessage = (message ?? string.Empty).Trim();
        if (rawMessage.Length == 0)
            return false;

        var normalizedMessage = NormalizeExactPromptText(rawMessage);
        var token = NormalizeExactPromptText("__VALUE__");
        foreach (var language in ClientUiText.SupportedLanguageCodes())
        {
            var template = NormalizeExactPromptText(promptBuilder(language, "__VALUE__").Trim());
            var tokenIndex = template.IndexOf(token, StringComparison.Ordinal);
            if (tokenIndex < 0)
                continue;

            var prefix = template[..tokenIndex].TrimEnd();
            var suffix = template[(tokenIndex + token.Length)..].TrimStart();
            if (!normalizedMessage.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrWhiteSpace(suffix) && !normalizedMessage.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var valueLength = normalizedMessage.Length - prefix.Length - suffix.Length;
            if (valueLength > 0)
                return true;
        }

        return false;
    }

    private static bool MatchesCanonicalStaticPrompt(string? message, Func<string?, string> promptBuilder)
    {
        var rawMessage = (message ?? string.Empty).Trim();
        var normalizedMessage = NormalizeShortcutToken(rawMessage);
        if (normalizedMessage.Length == 0 || ContainsConversationalContentCue(normalizedMessage))
            return false;

        var canonicalMessage = NormalizeExactPromptText(rawMessage);
        foreach (var language in ClientUiText.SupportedLanguageCodes())
        {
            var template = promptBuilder(language).Trim();
            if (string.Equals(rawMessage, template, StringComparison.Ordinal)
                || string.Equals(canonicalMessage, NormalizeExactPromptText(template), StringComparison.Ordinal))
            {
                return true;
            }
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
        var canonicalMessage = NormalizeExactPromptText(rawMessage);

        foreach (var language in ClientUiText.SupportedLanguageCodes())
        {
            var template = promptBuilder(language, token).Trim();
            var tokenIndex = template.IndexOf(token, StringComparison.Ordinal);
            if (tokenIndex < 0)
                continue;

            var prefix = template[..tokenIndex];
            var suffix = template[(tokenIndex + token.Length)..];
            if (prefix.Length > 0 && rawMessage.StartsWith(prefix, StringComparison.Ordinal)
                && (suffix.Length == 0 || rawMessage.EndsWith(suffix, StringComparison.Ordinal)))
            {
                var length = rawMessage.Length - prefix.Length - suffix.Length;
                if (length > 0)
                {
                    var captured = rawMessage.Substring(prefix.Length, length).Trim();
                    if (captured.Length > 0)
                    {
                        value = captured;
                        return true;
                    }
                }
            }

            var canonicalTemplate = NormalizeExactPromptText(template);
            var canonicalTokenIndex = canonicalTemplate.IndexOf(token, StringComparison.Ordinal);
            if (canonicalTokenIndex < 0)
                continue;

            var canonicalPrefix = canonicalTemplate[..canonicalTokenIndex];
            var canonicalSuffix = canonicalTemplate[(canonicalTokenIndex + token.Length)..];
            if (canonicalPrefix.Length > 0 && !canonicalMessage.StartsWith(canonicalPrefix, StringComparison.Ordinal))
                continue;
            if (canonicalSuffix.Length > 0 && !canonicalMessage.EndsWith(canonicalSuffix, StringComparison.Ordinal))
                continue;

            var canonicalLength = canonicalMessage.Length - canonicalPrefix.Length - canonicalSuffix.Length;
            if (canonicalLength <= 0)
                continue;

            var canonicalCaptured = canonicalMessage.Substring(canonicalPrefix.Length, canonicalLength).Trim();
            if (canonicalCaptured.Length == 0)
                continue;

            value = canonicalCaptured;
            return true;
        }

        return false;
    }

    private static string NormalizeExactPromptText(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace(' ', ' ')
            .Replace("…", "...")
            .Trim();

        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static string NormalizeShortcutToken(string? value)
    {
        var normalized = StripDiacritics(value ?? string.Empty).Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}/_-]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }
}
