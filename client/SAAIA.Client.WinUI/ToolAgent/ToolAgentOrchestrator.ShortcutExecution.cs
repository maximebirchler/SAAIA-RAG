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

        if (LooksLikeMissingStandardIdentifierQuestion(effectiveUserMessage))
        {
            var answer = BuildMissingStandardIdentifierClarification(interactionLanguage);
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            RememberPendingClarification(
                "missing_standard_identifier",
                displayUserMessage,
                "standard_reference_and_project_scope",
                interactionLanguage);
            return (true, answer, null, "clarification", Array.Empty<string>());
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
            if (LooksLikeExactDocumentPassageLocalizationRequest(effectiveUserMessage))
            {
                var advancedAnswer = DeterministicAgentText.AdvancedSourceLocalizationRequired(interactionLanguage);
                _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
                _mem.LastToolNames = new List<string>();
                await EmitDeterministicTextAsync(advancedAnswer, onDelta, ct).ConfigureAwait(false);
                return (
                    true,
                    advancedAnswer,
                    null,
                    "advanced_analysis_required",
                    Array.Empty<string>());
            }

            onPhase?.Invoke(DeterministicAgentText.PhaseRag(interactionLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(interactionLanguage));

            var categoryScope = ResolveKnownDocumentContentSearchCategoryScope(effectiveUserMessage);
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

            ragResult = FilterDocumentContentSearchResult(ragResult, documentContentTopic);
            var hadUsableInitialHit = HasRagHits(ragResult);

            if (ShouldExpandDocumentContentSearch(effectiveUserMessage, ragResult))
            {
                var queries = BuildDocumentContentSearchQueries(effectiveUserMessage, documentContentTopic)
                    .Where(query => !string.Equals(NormalizeRagQueryForRetrieval(query), primaryQuery, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (hadUsableInitialHit && queries.Count > 0)
                {
                    var args = CreateJsonArgs(new
                    {
                        queries,
                        topK = shouldPreferExplainedDocumentAnswer ? 6 : 4,
                        category = categoryScope,
                        mode = "balanced"
                    });
                    var expandedResult = FilterDocumentContentSearchResult(
                        await TryExecRagMultiSearchOrEmptyAsync(args, ct).ConfigureAwait(false),
                        documentContentTopic);
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
                    var translatedSearchQueries = new List<string>();
                    if (hadUsableInitialHit)
                    {
                        foreach (var query in queries)
                            AddDistinctQuery(translatedSearchQueries, query);
                    }
                    foreach (var query in expandedQueries)
                    {
                        if (!hadUsableInitialHit
                            && (string.Equals(
                                    NormalizeRagQueryForRetrieval(query),
                                    NormalizeRagQueryForRetrieval(documentContentTopic),
                                    StringComparison.OrdinalIgnoreCase)
                                || string.Equals(
                                    NormalizeRagQueryForRetrieval(query),
                                    primaryQuery,
                                    StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        AddDistinctQuery(translatedSearchQueries, query);
                    }

                    if (translatedSearchQueries.Count > 0)
                    {
                        var translatedQueryLimit = hadUsableInitialHit ? 8 : 2;
                        var expandedArgs = CreateJsonArgs(new
                        {
                            queries = translatedSearchQueries.Take(translatedQueryLimit).ToArray(),
                            topK = shouldPreferExplainedDocumentAnswer ? 6 : 4,
                            category = categoryScope,
                            mode = "balanced"
                        });
                        var translatedResult = FilterDocumentContentSearchResult(
                            await TryExecRagMultiSearchOrEmptyAsync(expandedArgs, ct).ConfigureAwait(false),
                            documentContentTopic);
                        if (IsBetterDocumentContentSearchCoverage(ragResult, translatedResult))
                        {
                            ragResult = translatedResult;
                            toolName = "rag.multi_search";
                        }
                    }
                }
            }

            ragResult = FilterDocumentContentSearchResult(ragResult, documentContentTopic);

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
}
