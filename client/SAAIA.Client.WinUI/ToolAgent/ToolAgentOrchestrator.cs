using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    private readonly ApiClient _api;
    private readonly ILlmClient _llm;
    private readonly ToolMemory _mem;
    private readonly AppSettings? _settings;
    private long _lastRouterMs;
    private long _lastToolsMs;
    private long _lastWriterMs;
    private long _lastTotalMs;
    private long _lastCriticMs;
    private string? _lastCriticStatus;
    private string? _lastCriticWarning;
    private bool _lastCriticRevisedAnswer;
    private bool _lastCriticEligible;
    private string? _lastCriticSkipReason;
    private bool _lastUsedGeneralChatPrompt;
    private bool _lastUsedInventoryRendered;
    private bool _lastUsedSummaryFlow;
    private string _lastResponseFormat = "auto";
    private string _lastEffectiveMode = "auto";
    private List<string> _lastWriterToolNames = new();
    private List<(string tool, long durationMs, bool ok)> _lastToolDurations = new();

    // limite “sécurité perf” (spec : max 5 RAG/calls par requête)
    private const int MaxToolCalls = 8;

    private enum DocumentSummaryRequestKind
    {
        About,
        SummaryReadOrLive,
        SummaryReadStoredExact,
        SummaryCheckOnly,
        SummaryStore
    }

    internal ToolAgentOrchestrator(ApiClient api, ILlmClient llm, ToolMemory mem, AppSettings? settings = null)
    {
        _api = api;
        _llm = llm;
        _mem = mem;
        _settings = settings;
    }

    private async Task<string> CompleteWithRetryAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
    {
        try
        {
            return await _llm.CompleteAsync(messages, forceJson, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            return await _llm.CompleteAsync(messages, forceJson, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> StreamOrCompleteWithRetryAsync(
        IReadOnlyList<(string role, string content)> messages,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        if (onDelta is null)
            return await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);

        var streamed = new StringBuilder();
        try
        {
            await _llm.StreamAsync(messages, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta(delta);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (streamed.Length == 0)
                return await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);
        }

        var finalAnswer = streamed.ToString();
        if (string.IsNullOrWhiteSpace(finalAnswer))
            finalAnswer = await CompleteWithRetryAsync(messages, forceJson: false, ct).ConfigureAwait(false);

        return finalAnswer;
    }

    /// <summary>
    /// Exécute le pipeline Router → Tools → Answer.
    /// </summary>
    /// <param name="onPhase">Callback UX (status bar) : "Routeur…", "Recherche documents…", "Rédaction…", etc.</param>
    public async Task<(string finalAnswer, object? sourcesPayload)> RunAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        CancellationToken ct,
        Action<string>? onPhase = null,
        Action<string>? onDelta = null,
        Action<string>? onProgress = null)
    {
        var swTotalPipeline = Stopwatch.StartNew();
        ResetLastTurnDiagnostics();

        if (TryDetectExplicitLanguageSwitch(userMessage, out var requestedLanguage))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseWriting(requestedLanguage));
            onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(requestedLanguage));

            var translated = await TryTranslateLastAnswerOneShotAsync(requestedLanguage, ct, onDelta).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                onProgress?.Invoke(string.Empty);
                return FinalizeAndReturn(
                    swTotalPipeline,
                    userMessage,
                    translated.Trim(),
                    null,
                    "meta.translate_last_answer",
                    new[] { "meta.translate_last_answer" },
                    Array.Empty<string>());
            }

            var ack = LocalizedStrings.NoPreviousAnswerToTranslate(requestedLanguage);
            await EmitDeterministicTextAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return FinalizeAndReturn(swTotalPipeline, userMessage, ack, null, "meta.translate_last_answer", Array.Empty<string>(), Array.Empty<string>());
        }

        var pendingClarification = PrepareUserMessageForPendingClarification(userMessage);
        var effectiveUserMessage = pendingClarification.EffectiveUserMessage;
        var interactionLanguage = ResolveInteractionLanguage(effectiveUserMessage);
        var docResolution = pendingClarification.AnalysisOverride
            ?? DocumentRefResolver.Analyze(effectiveUserMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);
        var repairMessage = DocumentRefResolver.IsRepairMessage(effectiveUserMessage);

        var shortcut = await TryHandleDeterministicShortcutAsync(
            chatHistory,
            userMessage,
            effectiveUserMessage,
            interactionLanguage,
            docResolution,
            ct,
            onPhase,
            onDelta,
            onProgress,
            swTotalPipeline).ConfigureAwait(false);
        if (shortcut.handled)
        {
            onProgress?.Invoke(string.Empty);
            return FinalizeAndReturn(swTotalPipeline, userMessage, shortcut.finalAnswer, shortcut.sourcesPayload, shortcut.routerIntent, shortcut.toolNames, Array.Empty<string>());
        }

        onPhase?.Invoke(DeterministicAgentText.PhaseRouter(interactionLanguage));
        onProgress?.Invoke(LocalizedStrings.Get("phase.interpreting", interactionLanguage));

        var swRouter = Stopwatch.StartNew();
        var plan = await RouterAsync(chatHistory, effectiveUserMessage, ct, disallowMetaSetLanguage: false).ConfigureAwait(false);
        if (string.Equals(plan.Intent, "meta.set_language", StringComparison.OrdinalIgnoreCase)
            && !TryDetectExplicitLanguageSwitch(userMessage, out _))
        {
            plan = await RouterAsync(chatHistory, effectiveUserMessage, ct, disallowMetaSetLanguage: true).ConfigureAwait(false);
        }
        swRouter.Stop();
        _lastRouterMs = swRouter.ElapsedMilliseconds;

        plan.Language = NormalizeLanguageCode(interactionLanguage);
        if ((_settings?.StrictMode ?? false) && !string.Equals(plan.Mode, "strict", StringComparison.OrdinalIgnoreCase))
            plan.Mode = "strict";
        _mem.LastLanguage = plan.Language;
        _mem.LastRouterIntent = plan.Intent;
        _mem.LastReasoningTracePublic = plan.ReasoningTracePublic?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
        _mem.LastPlannerMemoryUpdate = string.IsNullOrWhiteSpace(plan.MemoryUpdate) ? null : plan.MemoryUpdate.Trim();
        _mem.LastRouterConfidence = plan.Confidence;
        _lastResponseFormat = string.IsNullOrWhiteSpace(plan.ResponseFormat) ? "auto" : plan.ResponseFormat.Trim().ToLowerInvariant();
        _lastEffectiveMode = string.IsNullOrWhiteSpace(plan.Mode) ? "auto" : plan.Mode.Trim().ToLowerInvariant();

        var routerTrace = _mem.LastReasoningTracePublic.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(routerTrace))
            onProgress?.Invoke(routerTrace);

        if (repairMessage && string.Equals(plan.Intent, "meta.repair_last", StringComparison.OrdinalIgnoreCase) && !plan.NeedClarification && plan.ToolCalls.Count == 0)
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseClarification(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressCorrectPreviousInterpretation(plan.Language));

            var repairAnswer = await GenerateRepairResponseAsync(chatHistory, effectiveUserMessage, plan.Language, ct, onDelta).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return FinalizeAndReturn(swTotalPipeline, userMessage, repairAnswer, null, plan.Intent, Array.Empty<string>(), _mem.LastReasoningTracePublic);
        }

        if ((plan.NeedClarification && plan.ClarificationQuestions.Count == 0) || (!plan.NeedClarification && docResolution.NeedsClarification && !string.IsNullOrWhiteSpace(docResolution.ClarificationKind)))
        {
            var clarification = await GenerateClarificationResponseAsync(
                chatHistory,
                userMessage,
                plan.Language,
                docResolution.ClarificationKind ?? "generic",
                docResolution.ClarificationHint,
                ct,
                onDelta).ConfigureAwait(false);

            RememberPendingClarification(docResolution.ClarificationKind ?? "generic", userMessage, docResolution.ClarificationHint, plan.Language);
            onProgress?.Invoke(string.Empty);
            return FinalizeAndReturn(swTotalPipeline, userMessage, clarification, null, "clarification", Array.Empty<string>(), _mem.LastReasoningTracePublic, clearPendingClarification: false);
        }

        var documentaryProbe = await TryHandleDocumentaryProbeAsync(
            effectiveUserMessage,
            plan,
            ct,
            onPhase,
            onDelta,
            onProgress).ConfigureAwait(false);
        if (documentaryProbe.handled)
        {
            return FinalizeAndReturn(swTotalPipeline, userMessage, documentaryProbe.finalAnswer, documentaryProbe.sourcesPayload, documentaryProbe.routerIntent, documentaryProbe.toolNames, _mem.LastReasoningTracePublic, clearPendingClarification: documentaryProbe.clearPendingClarification);
        }

        if (plan.NeedClarification && plan.ClarificationQuestions.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var q in plan.ClarificationQuestions.Take(2))
                sb.AppendLine($"- {q}");
            var clarification = sb.ToString().Trim();
            await EmitDeterministicTextAsync(clarification, onDelta, ct).ConfigureAwait(false);
            RememberPendingClarification(docResolution.ClarificationKind ?? "generic", userMessage, docResolution.ClarificationHint, plan.Language);
            onProgress?.Invoke(string.Empty);
            return FinalizeAndReturn(swTotalPipeline, userMessage, clarification, null, plan.Intent, Array.Empty<string>(), _mem.LastReasoningTracePublic, clearPendingClarification: false);
        }

        var summaryHandled = await TryHandleRouterDrivenDocumentSummaryFlowAsync(
            chatHistory,
            userMessage,
            effectiveUserMessage,
            plan,
            docResolution,
            ct,
            onPhase,
            onDelta,
            onProgress,
            swTotalPipeline).ConfigureAwait(false);
        if (summaryHandled.handled)
            return (summaryHandled.finalAnswer, summaryHandled.sourcesPayload);

        onPhase?.Invoke(DeterministicAgentText.PhaseTools(plan.Language));
        onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(plan.Language));

        var localItems = ExecuteLocalTools(plan, chatHistory);
        if (localItems.Count > 0)
            plan.ToolCalls = plan.ToolCalls.Where(c => !string.Equals(c.Name, "meta.list_questions", StringComparison.OrdinalIgnoreCase)).ToList();

        var swTools = Stopwatch.StartNew();
        var toolResults = await ExecuteToolsAsync(plan, effectiveUserMessage, ct, onPhase, onProgress).ConfigureAwait(false);
        swTools.Stop();
        _lastToolsMs = swTools.ElapsedMilliseconds;
        _lastToolDurations = toolResults.Items.Select(x => (x.ToolName, x.DurationMs, string.IsNullOrWhiteSpace(x.Error))).ToList();
        _mem.LastToolNames = toolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (localItems.Count > 0)
            toolResults.Items.InsertRange(0, localItems);

        CaptureStructuredConversationState(plan, toolResults);

        var inventoryRendered = TryBuildInventoryRenderedItem(toolResults, plan.Language, ct);
        _lastUsedInventoryRendered = inventoryRendered is not null;
        if (inventoryRendered is not null)
        {
            toolResults.Items.Add(inventoryRendered);
        }

        onPhase?.Invoke(DeterministicAgentText.PhaseWriting(plan.Language));
        onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(plan.Language));
        var swWriter = Stopwatch.StartNew();
        var (answer, sources) = await AnswerAsync(chatHistory, effectiveUserMessage, plan, toolResults, ct, onDelta, onProgress).ConfigureAwait(false);
        swWriter.Stop();
        _lastWriterMs = swWriter.ElapsedMilliseconds;

        answer = (answer ?? string.Empty).Replace("**", string.Empty).Trim();

        if (sources is { Count: > 0 })
            _mem.LastSourcesUsed = sources;

        if (sources is { Count: > 0 })
            answer = InjectInlineSources(answer, sources, plan.Language);

        object? sourcesPayload = null;
        if (sources is { Count: > 0 })
        {
            sourcesPayload = new
            {
                sources = sources.Select(x => new { docPath = x.DocPath, pageStart = x.PageStart, pageEnd = x.PageEnd, label = x.Label }).ToList()
            };
        }

        onProgress?.Invoke(string.Empty);
        return FinalizeAndReturn(swTotalPipeline, userMessage, answer, sourcesPayload, plan.Intent, _mem.LastToolNames, _mem.LastReasoningTracePublic);
    }

    private static List<ToolResults.Item> ExecuteLocalTools(RouterPlan plan, IReadOnlyList<(string role, string content)> chatHistory)
    {
        var items = new List<ToolResults.Item>();

        if (plan.ToolCalls.Any(c => string.Equals(c.Name, "meta.list_questions", StringComparison.OrdinalIgnoreCase)))
        {
            var questions = chatHistory
                .Where(m => string.Equals(m.role, "user", StringComparison.OrdinalIgnoreCase))
                .Select(m => (m.content ?? string.Empty).Trim())
                .Where(s => s.Length > 0)
                .TakeLast(50)
                .ToList();

            var payload = new { questions };
            items.Add(new ToolResults.Item
            {
                ToolName = "meta.list_questions",
                Result = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement,
                DurationMs = 0
            });
        }

        return items;
    }

    private string BuildDocumentsListAnswer(ToolResults toolResults, string language)
    {
        // We always format deterministically from tool results to avoid hallucinated paths.
        var item = toolResults.Items.LastOrDefault(x => x.ToolName is "documents.list" or "documents.search");
        if (item is null)
            return LocalizedStrings.NoDocumentsFound(language);

        try
        {
            var (docs, _, _, _, endOfList, dropped) = DocumentListHelper.Sanitize(item.Result, _mem);
            var list = DocumentListHelper.BuildUserText(docs, endOfList, dropped);
            if (!string.IsNullOrWhiteSpace(list))
            {
                var scopePath = TryInferDocumentsScopePath(item.Result);
                return $"{DeterministicAgentText.DocumentsListHeader(language, scopePath)}{Environment.NewLine}{list}".TrimEnd();
            }

            return LocalizedStrings.NoDocumentsFound(language);
        }
        catch
        {
            return LocalizedStrings.DocumentListError(language);
        }
    }

    private string BuildDocumentsTreeAnswer(ToolResults toolResults, string language)
    {
        var treeItem = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.tree");
        if (treeItem is not null)
        {
            try
            {
                if (treeItem.Result.ValueKind == JsonValueKind.Object
                    && treeItem.Result.TryGetProperty("markdown", out var md)
                    && md.ValueKind == JsonValueKind.String)
                {
                    var markdown = (md.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(markdown))
                        return markdown;
                }
            }
            catch
            {
            }
        }

        // Fallback for older results built from list/search.
        var item = toolResults.Items.LastOrDefault(x => x.ToolName is "documents.list" or "documents.search");
        if (item is null)
            return LocalizedStrings.NoDocumentsFound(language);

        try
        {
            var (docs, _, _, _, _, dropped) = DocumentListHelper.Sanitize(item.Result, _mem);

            if (docs.Count == 0)
                return LocalizedStrings.NoDocumentsFound(language);

            var tree = DocumentTreeHelper.BuildMarkdownTree(docs);

            // optionnel : petite note si certains fichiers n’ont pas pu être résolus
            if (dropped > 0)
            {
                tree += $"\n\n{DeterministicAgentText.TreeSkippedLocalUnresolved(language, dropped)}";
            }

            return tree;
        }
        catch
        {
            return LocalizedStrings.DocumentTreeError(language);
        }
    }

    private ToolResults.Item? TryBuildInventoryRenderedItem(ToolResults toolResults, string language, CancellationToken ct)
    {
        _ = ct;

        string kind = string.Empty;
        object? data = null;

        if (toolResults.Items.Any(x => x.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "tree";
            data = BuildTreeInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => (x.ToolName is "documents.list" or "documents.search") && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "list";
            data = BuildListInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.stats" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "stats";
            data = BuildStatsInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.categories" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "categories";
            data = BuildCategoriesInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => (x.ToolName is "summary.status.list" or "summary.present.list") && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "summary_status_list";
            data = BuildSummaryStatusListInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => (x.ToolName is "summary.status.count" or "summary.present.count") && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "summary_status_count";
            data = BuildSummaryStatusCountInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.count" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "count";
            data = BuildCountInventoryData(toolResults, "documents.count");
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.empty_count" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "empty_count";
            data = BuildCountInventoryData(toolResults, "documents.empty_count");
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.empty_list" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "empty_list";
            data = BuildEmptyFoldersInventoryData(toolResults);
        }

        if (string.IsNullOrWhiteSpace(kind) || data is null)
            return null;

        var payload = new
        {
            kind,
            language,
            authoritative = true,
            data
        };

        if (data is not null)
        {
            _mem.LastDeterministicRender = new ToolMemory.DeterministicRenderState
            {
                Kind = kind,
                DataJson = JsonSerializer.Serialize(data),
                RouterIntent = _mem.LastRouterIntent,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        return new ToolResults.Item
        {
            ToolName = "inventory.rendered",
            Result = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement,
            DurationMs = 0
        };
    }

    private object? BuildListInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "documents.list" or "documents.search") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null)
            return null;

        try
        {
            var (docs, limit, offset, total, endOfList, dropped) = DocumentListHelper.Sanitize(item.Result, _mem);
            return new
            {
                scopePath = TryInferDocumentsScopePath(item.Result),
                limit,
                offset,
                total,
                endOfList,
                dropped,
                items = docs.Select(d => new
                {
                    pdfRef = d.PdfRef,
                    docId = d.DocId,
                    docPath = d.DocPath,
                    docName = d.DocName,
                    category = d.Category,
                    categoryPath = d.CategoryPath,
                    pages = d.Pages
                }).ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildTreeInventoryData(ToolResults toolResults)
    {
        var treeItem = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(x.Error));
        if (treeItem is null || treeItem.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var markdown = treeItem.Result.TryGetProperty("markdown", out var md) && md.ValueKind == JsonValueKind.String
                ? (md.GetString() ?? string.Empty).Trim()
                : string.Empty;

            return new
            {
                path = TryGetString(treeItem.Result, "path") ?? string.Empty,
                depth = TryGetInt(treeItem.Result, "depth"),
                limit = TryGetInt(treeItem.Result, "limit"),
                offset = TryGetInt(treeItem.Result, "offset"),
                totalNodes = TryGetInt(treeItem.Result, "totalNodes"),
                totalDocuments = TryGetInt(treeItem.Result, "totalDocuments"),
                markdown
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildStatsInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.stats" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var rootFolders = new List<object>();
            if (item.Result.TryGetProperty("rootFolders", out var rf) && rf.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in rf.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object)
                        continue;

                    rootFolders.Add(new
                    {
                        path = TryGetString(x, "path") ?? string.Empty,
                        name = TryGetString(x, "name") ?? string.Empty,
                        totalDocuments = TryGetInt(x, "totalDocuments") ?? 0,
                        directDocuments = TryGetInt(x, "directDocuments") ?? 0,
                        subfolderCount = TryGetInt(x, "subfolderCount") ?? 0
                    });
                }
            }

            var foldersByDepth = new List<object>();
            if (item.Result.TryGetProperty("foldersByDepth", out var fd) && fd.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in fd.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object)
                        continue;

                    foldersByDepth.Add(new
                    {
                        depth = TryGetInt(x, "depth") ?? 0,
                        folderCount = TryGetInt(x, "folderCount") ?? 0
                    });
                }
            }

            return new
            {
                scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
                totalDocuments = TryGetInt(item.Result, "totalDocuments") ?? 0,
                maxDepth = TryGetInt(item.Result, "maxDepth") ?? 0,
                totalNonEmptyFolders = TryGetInt(item.Result, "totalNonEmptyFolders") ?? 0,
                topLevelFolderCount = TryGetInt(item.Result, "topLevelFolderCount") ?? 0,
                leafFolderCount = TryGetInt(item.Result, "leafFolderCount") ?? 0,
                includesEmptyFolders = TryGetBoolProp(item.Result, "includesEmptyFolders") ?? false,
                emptyFoldersKnown = TryGetBoolProp(item.Result, "emptyFoldersKnown") ?? false,
                foldersByDepth,
                rootFolders
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildCategoriesInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.categories" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var rows = new List<object>();
            if (item.Result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in items.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var aliases = new List<string>();
                    if (entry.TryGetProperty("aliases", out var aliasArray) && aliasArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var alias in aliasArray.EnumerateArray())
                        {
                            if (alias.ValueKind != JsonValueKind.String)
                                continue;

                            var value = alias.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(value))
                                aliases.Add(value.Trim());
                        }
                    }

                    rows.Add(new
                    {
                        ordinal = TryGetInt(entry, "ordinal") ?? TryGetInt(entry, "displayOrder") ?? 0,
                        displayOrder = TryGetInt(entry, "displayOrder") ?? TryGetInt(entry, "ordinal") ?? 0,
                        path = TryGetString(entry, "path") ?? string.Empty,
                        name = TryGetString(entry, "name") ?? string.Empty,
                        depth = TryGetInt(entry, "depth") ?? 0,
                        totalDocuments = TryGetInt(entry, "totalDocuments") ?? 0,
                        directDocuments = TryGetInt(entry, "directDocuments") ?? 0,
                        subfolderCount = TryGetInt(entry, "subfolderCount") ?? 0,
                        aliases = aliases
                    });
                }
            }

            return new
            {
                scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
                total = TryGetInt(item.Result, "total") ?? rows.Count,
                limit = TryGetInt(item.Result, "limit") ?? rows.Count,
                offset = TryGetInt(item.Result, "offset") ?? 0,
                items = rows
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildSummaryStatusCountInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.count" or "summary.present.count") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        return new
        {
            total = TryGetInt(item.Result, "total") ?? 0,
            missingStored = TryGetInt(item.Result, "missingStored") ?? 0,
            staleStored = TryGetInt(item.Result, "staleStored") ?? 0,
            scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
            level = TryGetString(item.Result, "level") ?? "medium",
            mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.count", StringComparison.OrdinalIgnoreCase) ? "present" : "missing")
        };
    }

    private static object? BuildSummaryStatusListInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.list" or "summary.present.list") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        var rows = new List<object>();
        if (item.Result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                rows.Add(new
                {
                    docId = TryGetString(entry, "DocId") ?? TryGetString(entry, "docId") ?? string.Empty,
                    docPath = TryGetString(entry, "DocPath") ?? TryGetString(entry, "docPath") ?? string.Empty,
                    docName = TryGetString(entry, "DocName") ?? TryGetString(entry, "docName") ?? string.Empty,
                    category = TryGetString(entry, "Category") ?? TryGetString(entry, "category") ?? string.Empty,
                    summaryState = TryGetString(entry, "SummaryState") ?? TryGetString(entry, "summaryState") ?? "missing"
                });
            }
        }

        return new
        {
            total = TryGetInt(item.Result, "total") ?? rows.Count,
            missingStored = TryGetInt(item.Result, "missingStored") ?? 0,
            staleStored = TryGetInt(item.Result, "staleStored") ?? 0,
            limit = TryGetInt(item.Result, "limit") ?? rows.Count,
            offset = TryGetInt(item.Result, "offset") ?? 0,
            scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
            level = TryGetString(item.Result, "level") ?? "medium",
            mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.list", StringComparison.OrdinalIgnoreCase) ? "present" : "missing"),
            items = rows
        };
    }

    private static object? BuildCountInventoryData(ToolResults toolResults, string toolName)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == toolName && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        return new
        {
            total = TryGetInt(item.Result, "total") ?? 0
        };
    }

    private static object? BuildEmptyFoldersInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.empty_list" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var paths = new List<object>();
            if (item.Result.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in arr.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var path = TryGetString(entry, "path") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(new { path });
                }
            }

            return new
            {
                total = paths.Count,
                items = paths
            };
        }
        catch
        {
            return null;
        }
    }

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
        var label = (src.Label ?? string.Empty).Trim().Replace("|", " ").Replace("]", ")");
        payload = new { sources = new[] { new { docPath = dp, pageStart = src.PageStart, pageEnd = src.PageEnd, label } } };

        var heading = DeterministicAgentText.SourceHeading(language);
        return $"{heading}:\n1. [[open|{dp}|{Math.Max(1, src.PageStart)}|{label}]]";
    }

    private async Task<string> RenderSummaryForDisplayAsync(string summaryText, string language, string mode, Action<string>? onDelta, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
            return string.Empty;

        if (onDelta is null)
            return summaryText.Trim();

        var system = $@"
You are SAAIA assistant.
Rewrite the provided summary faithfully.
Language: {language}
Mode: {mode}
Rules:
- Keep all concrete facts already present.
- Do not invent any additional information.
- Do not mention internal processing.
- Never output a partial URL, partial file path or visibly truncated token such as ""www"" when the source value is incomplete.
- If a value is incomplete in the source summary, omit it instead of guessing or truncating it.
- If mode=about: keep 2 to 4 short sentences maximum.
- If mode=summary: keep 2 to 4 compact paragraphs maximum.
- Return plain text only.
";

        var user = $@"SOURCE_SUMMARY:
{summaryText}";
        var streamed = new StringBuilder();

        try
        {
            await _llm.StreamAsync(new[]
            {
                ("system", system),
                ("user", user)
            }, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta(delta);
            }, ct).ConfigureAwait(false);

            var rendered = streamed.ToString().Replace("**", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(rendered))
                return rendered;
        }
        catch
        {
            if (streamed.Length == 0)
            {
                await EmitDeterministicTextAsync(summaryText.Trim(), onDelta, ct).ConfigureAwait(false);
                return summaryText.Trim();
            }
        }

        if (streamed.Length == 0)
        {
            await EmitDeterministicTextAsync(summaryText.Trim(), onDelta, ct).ConfigureAwait(false);
            return summaryText.Trim();
        }

        return streamed.ToString().Replace("**", string.Empty).Trim();
    }

    private string BuildQuestionsListAnswer(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName == "meta.list_questions");
            if (item is null) return string.Empty;

            if (item.Result.ValueKind != JsonValueKind.Object || !item.Result.TryGetProperty("questions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var i = 1;
            var sb = new StringBuilder();
            foreach (var q in arr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String) continue;
                var s = (q.GetString() ?? "").Trim();
                if (s.Length == 0) continue;
                sb.AppendLine($"{i}. {s}");
                i++;
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private (string answer, object? sourcesPayload) TryBuildSummaryAnswer(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName is "summary.get" or "rag.summarize_live");
            if (item is null || item.Result.ValueKind != JsonValueKind.Object)
                return (string.Empty, null);

            if (!item.Result.TryGetProperty("summaryText", out var st) || st.ValueKind != JsonValueKind.String)
                return (string.Empty, null);

            var answer = (st.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(answer))
                return (string.Empty, null);

            var anchors = new List<object>();
            if (item.Result.TryGetProperty("anchors", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var docPath = a.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? string.Empty) : string.Empty;
                    var pageStart = a.TryGetProperty("pageStart", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : 1;
                    var pageEnd = a.TryGetProperty("pageEnd", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt32() : pageStart;
                    var label = a.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? (lb.GetString() ?? string.Empty) : string.Empty;
                    if (!string.IsNullOrWhiteSpace(docPath))
                        anchors.Add(new { docPath, pageStart, pageEnd, label });
                }
            }

            if (anchors.Count == 0
                && item.Result.TryGetProperty("docPath", out var dp2) && dp2.ValueKind == JsonValueKind.String)
            {
                var docPath = (dp2.GetString() ?? string.Empty).Trim();
                var label = item.Result.TryGetProperty("docName", out var dn) && dn.ValueKind == JsonValueKind.String
                    ? (dn.GetString() ?? string.Empty)
                    : Path.GetFileName(docPath);
                if (!string.IsNullOrWhiteSpace(docPath))
                    anchors.Add(new { docPath, pageStart = 1, pageEnd = 1, label });
            }

            object? payload = anchors.Count > 0 ? new { sources = anchors } : null;
            return (answer, payload);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    private sealed record StoredSummaryHit(string SummaryText, object? SourcesPayload, string SourceLanguage);

    private async Task<(string finalAnswer, object? sourcesPayload)> GetStoredSummaryForDisplayAsync(
        string docRef,
        string targetLanguage,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        var hit = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
        if (hit is null || string.IsNullOrWhiteSpace(hit.SummaryText))
            return (string.Empty, null);

        var sourceLanguage = NormalizeLanguageCode(hit.SourceLanguage);
        var requestedLanguage = NormalizeLanguageCode(targetLanguage);
        if (string.IsNullOrWhiteSpace(requestedLanguage) || string.Equals(requestedLanguage, sourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            await EmitDeterministicTextAsync(hit.SummaryText, onDelta, ct).ConfigureAwait(false);
            return (hit.SummaryText, hit.SourcesPayload);
        }

        var cacheKey = $"{docRef}|{requestedLanguage}";
        if (_mem.SummaryTranslationCache.TryGetValue(cacheKey, out var cachedTranslation) && !string.IsNullOrWhiteSpace(cachedTranslation))
        {
            await EmitDeterministicTextAsync(cachedTranslation, onDelta, ct).ConfigureAwait(false);
            return (cachedTranslation, hit.SourcesPayload);
        }

        var translated = await TranslateStoredSummaryAsync(hit.SummaryText, sourceLanguage, requestedLanguage, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            _mem.SummaryTranslationCache[cacheKey] = translated;
            return (translated, hit.SourcesPayload);
        }

        await EmitDeterministicTextAsync(hit.SummaryText, onDelta, ct).ConfigureAwait(false);
        return (hit.SummaryText, hit.SourcesPayload);
    }

    private async Task<string> TranslateStoredSummaryAsync(
        string summaryText,
        string sourceLanguage,
        string targetLanguage,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
            return string.Empty;

        if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            await EmitDeterministicTextAsync(summaryText, onDelta, ct).ConfigureAwait(false);
            return summaryText.Trim();
        }

        var streamed = new StringBuilder();
        try
        {
            var system = $@"You are SAAIA assistant.
Translate the stored summary faithfully.
Source language: {sourceLanguage}
Target language: {targetLanguage}
Rules:
- Preserve all concrete facts.
- Preserve the structure and level of detail.
- Do not shorten the text.
- Do not add any information.
- Never output a partial URL, partial file path or visibly truncated token such as ""www"".
- If a value is incomplete in the source text, omit it instead of guessing or truncating it.
- Return plain text only.";

            await _llm.StreamAsync(new[]
            {
                ("system", system),
                ("user", summaryText)
            }, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta?.Invoke(delta);
            }, ct).ConfigureAwait(false);

            var translated = streamed.ToString().Replace("**", string.Empty).Trim();
            return string.IsNullOrWhiteSpace(translated) ? summaryText.Trim() : translated;
        }
        catch
        {
            if (streamed.Length == 0 && onDelta is not null)
                await EmitDeterministicTextAsync(summaryText.Trim(), onDelta, ct).ConfigureAwait(false);
            return streamed.Length == 0 ? summaryText.Trim() : streamed.ToString().Replace("**", string.Empty).Trim();
        }
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunKnownDocumentSummaryFlowAsync(
        string userMessage,
        string docRef,
        DocumentSummaryRequestKind requestKind,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!string.IsNullOrWhiteSpace(docRef))
            _mem.LastRequestedDocumentRef = docRef.Trim();

        var detectedLanguage = NormalizeLanguageCode(ResolveInteractionLanguage(userMessage));
        var language = !string.IsNullOrWhiteSpace(detectedLanguage)
            ? detectedLanguage
            : NormalizeLanguageCode(_mem.LastLanguage);
        _mem.LastLanguage = language;

        return requestKind switch
        {
            DocumentSummaryRequestKind.About => await RunDocumentAboutRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false),
            DocumentSummaryRequestKind.SummaryReadStoredExact => await RunDocumentStoredSummaryReadRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false),
            DocumentSummaryRequestKind.SummaryCheckOnly => await RunDocumentSummaryCheckRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false),
            DocumentSummaryRequestKind.SummaryStore => await RunDocumentSummaryStoreRequestAsync(docRef, language, userMessage, ct, onDelta, onProgress).ConfigureAwait(false),
            _ => await RunDocumentSummaryRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false)
        };
    }

    private DocumentSummaryRequestKind ResolveDocumentSummaryRequestKind(string userMessage, DocumentRefResolver.AnalysisResult analysis)
    {
        if (analysis.WantsStoredSummaryStore)
            return DocumentSummaryRequestKind.SummaryStore;

        if (IsExplicitStoredSummaryReadRequest(userMessage, analysis))
            return DocumentSummaryRequestKind.SummaryReadStoredExact;

        if (analysis.WantsStoredSummaryCheck)
            return DocumentSummaryRequestKind.SummaryCheckOnly;

        if (analysis.WantsAbout && !analysis.WantsSummary)
            return DocumentSummaryRequestKind.About;

        return DocumentSummaryRequestKind.SummaryReadOrLive;
    }

    private static bool IsStoredSummaryAvailabilityQuestion(string userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s, @"\b(?:verify|check|confirm|exists?|available|availability)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\b(?:v[ée]rif(?:ie|ier)|disponible|existe|existence)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\b(?:est-ce\s+que|is\s+there|does\s+the\s+document\s+have|has\s+the\s+document\s+got)\b", RegexOptions.IgnoreCase);
    }

    private static bool IsExplicitStoredSummaryReadRequest(string userMessage, DocumentRefResolver.AnalysisResult analysis)
    {
        if (analysis.WantsStoredSummaryStore)
            return false;

        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        var hasStoredCue = Regex.IsMatch(s, @"\b(?:stock[ée]?|stored|saved|cached|enregistr[ée]?|sauvegard[ée]?)\b", RegexOptions.IgnoreCase);
        if (!hasStoredCue)
            return false;

        if (IsStoredSummaryAvailabilityQuestion(s))
            return false;

        var hasReadCue = Regex.IsMatch(s, @"\b(?:donne|give|show|display|montre|affiche|read|get|load|lis|return|renvoie)\b", RegexOptions.IgnoreCase);
        return hasReadCue || analysis.WantsSummary;
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentStoredSummaryReadRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressCheckStoredSummaryAvailable(language));

        var cached = await GetStoredSummaryForDisplayAsync(docRef, language, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached.finalAnswer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressReturnStoredSummary(language));
            return cached;
        }

        var missing = LocalizedStrings.SummaryNotStored(language);
        await EmitDeterministicTextAsync(missing, onDelta, ct).ConfigureAwait(false);
        return (missing, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentAboutRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressRetrieveRepresentativePassages(language));

        var liveArgs = CreateJsonArgs(new
        {
            docRef,
            level = "short",
            strategy = "about",
            language,
            maxWords = 90,
            maxChunks = 5,
            maxBatches = 1,
            maxCharsPerBatch = 2800
        });

        var live = await ExecRagSummarizeLiveAsync(liveArgs, ct).ConfigureAwait(false);
        var fast = TryBuildSummaryAnswer(BuildSingleToolResult("rag.summarize_live", live));
        if (!string.IsNullOrWhiteSpace(fast.answer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressComposeShortOverview(language));
            var rendered = await RenderSummaryForDisplayAsync(fast.answer, language, "about", onDelta, ct).ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(rendered) ? fast.answer : rendered, fast.sourcesPayload);
        }

        var fallback = LocalizedStrings.ShortOverviewUnavailable(language);
        await EmitDeterministicTextAsync(fallback, onDelta, ct).ConfigureAwait(false);
        return (fallback, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentSummaryCheckRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressCheckStoredSummaryAvailable(language));

        var cached = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
        if (cached is not null && !string.IsNullOrWhiteSpace(cached.SummaryText))
        {
            var yes = LocalizedStrings.SummaryAlreadyStored(language);
            await EmitDeterministicTextAsync(yes, onDelta, ct).ConfigureAwait(false);
            return (yes, cached.SourcesPayload);
        }

        var missing = LocalizedStrings.SummaryNotStored(language);
        await EmitDeterministicTextAsync(missing, onDelta, ct).ConfigureAwait(false);
        return (missing, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentSummaryRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressCheckExistingStoredSummary(language));
        var cached = await GetStoredSummaryForDisplayAsync(docRef, language, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached.finalAnswer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressReturnStoredSummary(language));
            return cached;
        }

        onProgress?.Invoke(DeterministicAgentText.ProgressBuildLiveSummaryFromDocument(language));
        var liveArgs = CreateJsonArgs(new
        {
            docRef,
            level = "medium",
            strategy = "summary",
            language,
            maxWords = 220,
            maxChunks = 18,
            maxBatches = 4,
            maxCharsPerBatch = 6500
        });

        var live = await ExecRagSummarizeLiveAsync(liveArgs, ct).ConfigureAwait(false);
        var fast = TryBuildSummaryAnswer(BuildSingleToolResult("rag.summarize_live", live));
        if (!string.IsNullOrWhiteSpace(fast.answer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressWriteFinalSummary(language));
            var renderedLive = await RenderSummaryForDisplayAsync(fast.answer, language, "summary", onDelta, ct).ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(renderedLive) ? fast.answer : renderedLive, fast.sourcesPayload);
        }

        var fallback = LocalizedStrings.SummaryUnavailable(language);
        await EmitDeterministicTextAsync(fallback, onDelta, ct).ConfigureAwait(false);
        return (fallback, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentSummaryStoreRequestAsync(
        string docRef,
        string language,
        string userMessage,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!_api.HasAdminKey)
        {
            var denied = LocalizedStrings.SummaryStoreRequiresAdmin(language);
            await EmitDeterministicTextAsync(denied, onDelta, ct).ConfigureAwait(false);
            return (denied, null);
        }

        var forceRefresh = WantsSummaryRefresh(userMessage);

        onProgress?.Invoke(DeterministicAgentText.ProgressCheckReusableSummaryCache(language));

        var cached = await GetStoredSummaryForDisplayAsync(docRef, language, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached.finalAnswer) && !forceRefresh)
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressReusableSummaryAlreadyAvailable(language));
            return cached;
        }

        onProgress?.Invoke(DeterministicAgentText.ProgressGenerateAndStoreReusableSummary(language));

        var storedByAdmin = await TryGenerateAndStoreAdminSummaryAsync(docRef, language, ct).ConfigureAwait(false);
        if (storedByAdmin is not null && !string.IsNullOrWhiteSpace(storedByAdmin.SummaryText))
        {
            if (string.Equals(NormalizeLanguageCode(storedByAdmin.SourceLanguage), NormalizeLanguageCode(language), StringComparison.OrdinalIgnoreCase))
            {
                await EmitDeterministicTextAsync(storedByAdmin.SummaryText, onDelta, ct).ConfigureAwait(false);
                return (storedByAdmin.SummaryText, storedByAdmin.SourcesPayload);
            }

            var translated = await TranslateStoredSummaryAsync(storedByAdmin.SummaryText, NormalizeLanguageCode(storedByAdmin.SourceLanguage), NormalizeLanguageCode(language), onDelta, ct).ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(translated) ? storedByAdmin.SummaryText : translated, storedByAdmin.SourcesPayload);
        }

        var failed = LocalizedStrings.SummaryStoreFailed(language);
        await EmitDeterministicTextAsync(failed, onDelta, ct).ConfigureAwait(false);
        return (failed, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> TryGetStoredSummaryAnswerAsync(string docRef, CancellationToken ct)
    {
        var hit = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
        return hit is null || string.IsNullOrWhiteSpace(hit.SummaryText)
            ? (string.Empty, null)
            : (hit.SummaryText, hit.SourcesPayload);
    }

    private async Task<StoredSummaryHit?> TryGetStoredSummaryHitAsync(string docRef, CancellationToken ct)
    {
        try
        {
            var args = CreateJsonArgs(new { docRef, level = "medium" });
            var exists = await ExecSummaryExistsAsync(args, ct).ConfigureAwait(false);
            if (!TryGetBoolProp(exists, "exists").GetValueOrDefault())
                return null;

            var summary = await ExecSummaryGetAsync(args, ct).ConfigureAwait(false);
            var fast = TryBuildSummaryAnswer(BuildSingleToolResult("summary.get", summary));
            if (string.IsNullOrWhiteSpace(fast.answer))
                return null;

            var sourceLanguage = TryGetString(summary, "docLanguage") ?? TryGetString(summary, "DocLanguage") ?? string.Empty;
            return new StoredSummaryHit(fast.answer, fast.sourcesPayload, sourceLanguage);
        }
        catch
        {
            return null;
        }
    }

    private async Task<StoredSummaryHit?> TryGenerateAndStoreAdminSummaryAsync(
        string docRef,
        string language,
        CancellationToken ct)
    {
        string? jobId = null;

        try
        {
            ClientLog.Info($"summary.admin.generate:start docRef={docRef}");
            var generateArgs = CreateJsonArgs(new { docRef, level = "medium", force = false });
            var generate = await ExecAdminSummaryGenerateAsync(generateArgs, ct).ConfigureAwait(false);
            jobId = TryGetString(generate, "jobId");
            ClientLog.Info($"summary.admin.generate:queued docRef={docRef} jobId={jobId}");
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"summary.admin.generate:failed docRef={docRef} error={ex.Message}");
            return null;
        }

        try
        {
            var liveArgs = CreateJsonArgs(new
            {
                docRef,
                level = "long",
                strategy = "store",
                language,
                maxWords = 2400,
                maxChunks = 120,
                maxBatches = 18,
                maxCharsPerBatch = 12000
            });

            var live = await ExecRagSummarizeLiveAsync(liveArgs, ct).ConfigureAwait(false);
            var liveSummary = TryBuildSummaryAnswer(BuildSingleToolResult("rag.summarize_live", live));
            if (string.IsNullOrWhiteSpace(liveSummary.answer))
            {
                ClientLog.Warn($"summary.admin.live:empty docRef={docRef} jobId={jobId}");
                return null;
            }

            var submitArgs = CreateJsonArgs(new
            {
                jobId,
                docRef,
                level = "medium",
                docLanguage = language,
                summaryText = liveSummary.answer,
                meta = new
                {
                    generationMode = "client_admin_cache",
                    cachedForUsers = true,
                    generatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                }
            });

            var submit = await ExecAdminSummarySubmitAsync(submitArgs, ct).ConfigureAwait(false);
            ClientLog.Info($"summary.admin.submit:done docRef={docRef} jobId={jobId} stored={TryGetBoolProp(submit, "stored").GetValueOrDefault()}");

            var cached = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
            if (cached is not null && !string.IsNullOrWhiteSpace(cached.SummaryText))
            {
                ClientLog.Info($"summary.admin.verify:hit docRef={docRef} jobId={jobId}");
                return cached;
            }

            ClientLog.Warn($"summary.admin.verify:miss docRef={docRef} jobId={jobId}");
            return new StoredSummaryHit(liveSummary.answer, liveSummary.sourcesPayload, language);
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"summary.admin.submit:failed docRef={docRef} jobId={jobId} error={ex.Message}");
            return null;
        }
    }

    private static ToolResults BuildSingleToolResult(string toolName, JsonElement result)
    {
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = result
        });
        return toolResults;
    }

    private static JsonElement CreateJsonArgs(object payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
    private static bool WantsSummaryRefresh(string userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s, @"\b(?:refresh|regenerate|rebuild|update)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(s, @"\b(?:regenere|regénère|met\s+a\s+jour|mise\s+a\s+jour|recr[eé]e)\b", RegexOptions.IgnoreCase);
    }

    private static string PrefixSummaryMessage(string prefix, string summaryText)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
            return prefix;
        if (string.IsNullOrWhiteSpace(prefix))
            return summaryText;
        return $"{prefix.Trim()}\n\n{summaryText.Trim()}";
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

        var total = TryGetInt(item.Result, "total") ?? 0;
        var mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.count", StringComparison.OrdinalIgnoreCase) ? "present" : "missing");
        if (string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase))
        {
            return total <= 0
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.StoredSummariesCount(total, language);
        }

        return total <= 0
            ? DeterministicAgentText.NoMissingSummaries(language)
            : DeterministicAgentText.MissingSummariesCount(total, language);
    }

    private string TryBuildSummaryStatusListAnswer(ToolResults toolResults, string language)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.list" or "summary.present.list") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (!item.Result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var rows = new List<(string path, string state)>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = TryGetString(entry, "DocPath") ?? TryGetString(entry, "docPath") ?? string.Empty;
            var state = TryGetString(entry, "SummaryState") ?? TryGetString(entry, "summaryState") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(path))
                rows.Add((path, state));
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
                ? " [stale]"
                : string.Empty;
            sb.AppendLine($"{i + 1}. {rows[i].path}{suffix}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string InjectInlineSources(string answer, List<ToolMemory.SourceRef> sources, string language)
    {
        if (sources is null || sources.Count == 0) return answer;

        // If the LLM already emitted clickable tokens, do not add more.
        if (answer.Contains("[[open|", StringComparison.OrdinalIgnoreCase))
            return answer;

        var heading = DeterministicAgentText.SourceHeading(language);

        var sb = new StringBuilder();
        sb.AppendLine(answer.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"{heading}:");

        for (var i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            var dp = (s.DocPath ?? "").Replace('\\', '/').TrimStart('/');
            var mainCat = "";
            var slash = dp.IndexOf('/');
            if (slash > 0) mainCat = dp.Substring(0, slash);

            var label = (s.Label ?? "").Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                var fn = Path.GetFileName(dp);
                label = string.IsNullOrWhiteSpace(mainCat) ? fn : $"{fn} ({mainCat})";
            }

            // Keep label minimal + safe.
            label = label.Replace("|", " ").Replace("]", ")");

            sb.AppendLine($"{i + 1}. [[open|{dp}|{Math.Max(1, s.PageStart)}|{label}]]");
        }

        return sb.ToString().TrimEnd();
    }
    private bool ShouldRunDocumentaryProbe(string effectiveUserMessage, RouterPlan plan)
    {
        if (plan is null)
            return false;
        if (!string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase))
            return false;
        if (plan.ToolCalls.Count > 0 || plan.NeedClarification)
            return false;

        var s = (effectiveUserMessage ?? string.Empty).Trim();
        if (s.Length < 12)
            return false;

        if (Regex.IsMatch(s, @"^(?:hi|hello|bonjour|salut|merci|thanks?|ok|okay)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        return Regex.IsMatch(s, @"\b(?:qu['’]est\s*ce\s+que\s+tu\s+peux\s+me\s+dire|que\s+peux\s*tu\s+me\s+dire|parle\s*[- ]?moi|au\s+sujet\s+de|a\s+propos\s+de|à\s+propos\s+de|what\s+can\s+you\s+tell\s+me|tell\s+me\s+about|about\s+the|regarding|concerning)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || s.Contains("?", StringComparison.Ordinal);
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload, string? routerIntent, IReadOnlyList<string> toolNames, bool clearPendingClarification)> TryHandleDocumentaryProbeAsync(
        string effectiveUserMessage,
        RouterPlan plan,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!ShouldRunDocumentaryProbe(effectiveUserMessage, plan))
            return (false, string.Empty, null, null, Array.Empty<string>(), true);

        try
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(plan.Language));

            var probeCategory = string.IsNullOrWhiteSpace(_mem.LastResolvedCategory?.CategoryPath)
                ? null
                : _mem.LastResolvedCategory!.CategoryPath;
            var rag = await _api.RagSearchAsync(effectiveUserMessage, probeCategory, 5, "balanced", ct).ConfigureAwait(false);
            var hits = (rag.Items ?? new List<RagItem>())
                .Where(x => !string.IsNullOrWhiteSpace(x.DocPath) || !string.IsNullOrWhiteSpace(x.DocName))
                .GroupBy(x => string.IsNullOrWhiteSpace(x.DocPath) ? (x.DocName ?? string.Empty) : x.DocPath!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .OrderByDescending(x => x.Score)
                .Take(3)
                .ToList();

            if (hits.Count == 0)
                return (false, string.Empty, null, null, Array.Empty<string>(), true);

            var answer = BuildDocumentaryProbeClarification(hits, plan.Language);
            var sourcesPayload = new
            {
                intent = "rag_probe",
                sources = hits.Select(x => new
                {
                    docPath = (x.DocPath ?? string.Empty).Replace('\\', '/'),
                    docName = x.DocName ?? string.Empty,
                    pageStart = x.PageStart ?? 1,
                    pageEnd = x.PageEnd ?? x.PageStart ?? 1,
                    label = $"{(x.DocName ?? x.DocPath ?? "document")} (p.{(x.PageStart ?? 1)})",
                    snippet = string.IsNullOrWhiteSpace(x.Text)
                        ? string.Empty
                        : (x.Text!.Length > 220 ? x.Text[..220] + "…" : x.Text)
                }).ToList()
            };

            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            RememberPendingClarification("rag_probe", effectiveUserMessage, "documentary_probe", plan.Language);
            onProgress?.Invoke(string.Empty);
            return (true, answer, sourcesPayload, "rag.followup", new[] { "rag.search" }, false);
        }
        catch
        {
            return (false, string.Empty, null, null, Array.Empty<string>(), true);
        }
    }

    private static string BuildDocumentaryProbeClarification(IReadOnlyList<RagItem> hits, string language)
    {
        language = NormalizeLanguageCode(language);
        var first = hits[0];
        var firstLabel = string.IsNullOrWhiteSpace(first.DocName) ? (first.DocPath ?? "document") : first.DocName!;
        if (hits.Count == 1)
        {
            return language switch
            {
                "en" => $"I found a likely matching document: {firstLabel}. Do you want me to search in this one?",
                "es" => $"He encontrado un documento que parece coincidir: {firstLabel}. ¿Quieres que busque en ese documento?",
                "pt" => $"Encontrei um documento que parece corresponder: {firstLabel}. Queres que eu pesquise nesse documento?",
                "de" => $"Ich habe ein wahrscheinlich passendes Dokument gefunden: {firstLabel}. Soll ich in diesem Dokument suchen?",
                "it" => $"Ho trovato un documento che sembra corrispondere: {firstLabel}. Vuoi che cerchi in questo documento?",
                _ => $"J'ai trouvé un document qui semble correspondre : {firstLabel}. Veux-tu que je cherche dans celui-ci ?"
            };
        }

        var labels = hits.Select(x => string.IsNullOrWhiteSpace(x.DocName) ? (x.DocPath ?? "document") : x.DocName!).Take(3).ToList();
        var joined = string.Join(language == "fr" ? " ; " : "; ", labels);
        return language switch
        {
            "en" => $"I found several possible matches: {joined}. Which one should I use?",
            "es" => $"He encontrado varias coincidencias posibles: {joined}. ¿Cuál debo usar?",
            "pt" => $"Encontrei várias correspondências possíveis: {joined}. Qual devo usar?",
            "de" => $"Ich habe mehrere mögliche Treffer gefunden: {joined}. Welchen soll ich verwenden?",
            "it" => $"Ho trovato diverse corrispondenze possibili: {joined}. Quale devo usare?",
            _ => $"J'ai trouvé plusieurs correspondances possibles : {joined}. Laquelle veux-tu que j'utilise ?"
        };
    }

private string GuessLanguage(string userMessage)
{
    return ResolveInteractionLanguage(userMessage);
}
private async Task<RouterPlan> RouterAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        CancellationToken ct,
        bool disallowMetaSetLanguage)
    {
        var manifestJson = ToolManifest.BuildManifestJson();
        var toolbook = ToolManifest.ToolbookText;
        var repairHint = DocumentRefResolver.IsRepairMessage(userMessage);
        var resolverHint = DocumentRefResolver.Analyze(userMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);

        // Contexte mémoire minimal (évite heuristiques hardcodées)
        var memoryCtx = new
        {
            lastLanguage = _mem.LastLanguage,
            lastUserDetectedLanguage = _mem.LastUserDetectedLanguage,
            lastAnswerLanguage = _mem.LastAnswerLanguage,
            lastList = new
            {
                offset = _mem.LastListOffset,
                limit = _mem.LastListLimit,
                categoryPath = _mem.LastListCategoryPath,
                q = _mem.LastListQuery,
                total = _mem.LastListTotal
            },
            lastFocusedDocument = _mem.LastFocusedDocument is null ? null : new
            {
                docId = _mem.LastFocusedDocument.DocId,
                docPath = _mem.LastFocusedDocument.DocPath,
                docName = _mem.LastFocusedDocument.DocName,
                category = _mem.LastFocusedDocument.Category,
                categoryPath = _mem.LastFocusedDocument.CategoryPath,
                pdfRef = _mem.LastFocusedDocument.PdfRef
            },
            lastTurn = new
            {
                user = _mem.LastUserMessage,
                assistant = _mem.LastAssistantAnswer,
                routerIntent = _mem.LastRouterIntent,
                toolNames = _mem.LastToolNames,
                reasoningTracePublic = _mem.LastReasoningTracePublic,
                confidence = _mem.LastRouterConfidence,
                memoryUpdate = _mem.LastPlannerMemoryUpdate
            },
            pendingClarification = _mem.PendingClarification is null ? null : new
            {
                kind = _mem.PendingClarification.Kind,
                originalUserMessage = _mem.PendingClarification.OriginalUserMessage,
                hint = _mem.PendingClarification.Hint,
                language = _mem.PendingClarification.Language,
                createdAtUtc = _mem.PendingClarification.CreatedAtUtc
            },
            adminSession = new
            {
                hasAdminKey = _api.HasAdminKey
            },
            lastResolvedCategory = _mem.LastResolvedCategory is null ? null : new
            {
                categoryRef = _mem.LastResolvedCategory.CategoryRef,
                categoryPath = _mem.LastResolvedCategory.CategoryPath,
                displayName = _mem.LastResolvedCategory.DisplayName,
                ordinal = _mem.LastResolvedCategory.Ordinal,
                totalDocuments = _mem.LastResolvedCategory.TotalDocuments,
                aliases = _mem.LastResolvedCategory.Aliases
            },
            lastPresentedCategories = _mem.LastPresentedCategories?.Select(x => new
            {
                categoryRef = x.CategoryRef,
                categoryPath = x.CategoryPath,
                displayName = x.DisplayName,
                ordinal = x.Ordinal,
                totalDocuments = x.TotalDocuments,
                aliases = x.Aliases
            }).ToList(),
            lastSummaryStatus = _mem.LastSummaryStatusSnapshot is null ? null : new
            {
                categoryPath = _mem.LastSummaryStatusSnapshot.CategoryPath,
                categoryRef = _mem.LastSummaryStatusSnapshot.CategoryRef,
                mode = _mem.LastSummaryStatusSnapshot.Mode,
                total = _mem.LastSummaryStatusSnapshot.Total,
                missingStored = _mem.LastSummaryStatusSnapshot.MissingStored,
                staleStored = _mem.LastSummaryStatusSnapshot.StaleStored,
                itemsCount = _mem.LastSummaryStatusSnapshot.Items?.Count ?? 0
            },
            resolverHint = new
            {
                isContentRequest = resolverHint.IsContentRequest,
                wantsAbout = resolverHint.WantsAbout,
                wantsSummary = resolverHint.WantsSummary,
                wantsStoredSummaryCheck = resolverHint.WantsStoredSummaryCheck,
                wantsStoredSummaryStore = resolverHint.WantsStoredSummaryStore,
                resolvedDocRef = resolverHint.ResolvedDocRef,
                needsClarification = resolverHint.NeedsClarification,
                clarificationKind = resolverHint.ClarificationKind
            }
        };

        var detectedMessageLanguage = ResolveInteractionLanguage(userMessage);
        var system = PromptCatalog.BuildRouterSystemPrompt(manifestJson, toolbook) + $@"

Additional runtime rules:
- Last answer language (informational only): {_mem.LastLanguage}
- Current message language hint: {detectedMessageLanguage}
- Disallow meta.set_language for this turn: {(disallowMetaSetLanguage ? "true" : "false")}
- The current message looks like a repair/correction turn: {(repairHint ? "true" : "false")}
- Admin session available right now: {(_api.HasAdminKey ? "true" : "false")}
- If document resolution hint says clarification is needed, prefer a short clarification over a blind tool call.
";

        var user = $@"
MEMORY (json):
{JsonSerializer.Serialize(memoryCtx)}

CHAT_TAIL (for context):
{SerializeTail(chatHistory, maxTurns: 8)}

USER_MESSAGE:
{userMessage}
";

        string raw;
        try
        {
            raw = await CompleteWithRetryAsync(new[]
            {
                ("system", system),
                ("user", user)
            }, forceJson: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general" };
        }

        if (!TryExtractJsonObject(raw, out var planJson))
        {
            // Fallback safe: conversationnel sans tools
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general" };
        }

        try
        {
            var plan = JsonSerializer.Deserialize<RouterPlan>(planJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new RouterPlan();

            return SanitizeRouterPlan(plan, detectedMessageLanguage, disallowMetaSetLanguage);
        }
        catch
        {
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general" };
        }
    }

    private static string DescribeToolAction(string toolName, string userMessage, string language, JsonElement args)
    {
        _ = userMessage;
        var docRef = GetStringArg(args, "docRef") ?? GetStringArg(args, "pdfRef") ?? string.Empty;

        return toolName switch
        {
            "summary.exists" => DeterministicAgentText.ProgressCheckStoredSummaryForDocument(docRef, language),
            "summary.get" => DeterministicAgentText.ProgressLoadStoredSummaryForDocument(docRef, language),
            "rag.summarize_live" => DeterministicAgentText.ProgressBuildLiveSummaryForDocument(docRef, language),
            _ => DeterministicAgentText.ToolAction(toolName, language)
        };
    }

    private async Task<ToolResults> ExecuteToolsAsync(RouterPlan plan, string userMessage, CancellationToken ct, Action<string>? onPhase, Action<string>? onProgress)
    {
        var results = new ToolResults();

        foreach (var call in plan.ToolCalls)
        {
            onPhase?.Invoke(PhaseLabelForTool(call.Name, plan.Language));
            onProgress?.Invoke(DescribeToolAction(call.Name, userMessage, plan.Language, call.Args));

            var sw = Stopwatch.StartNew();
            try
            {
                if (!ToolManifest.IsKnownTool(call.Name))
                {
                    results.Items.Add(new ToolResults.Item
                    {
                        ToolName = call.Name,
                        Error = "unknown_tool",
                        DurationMs = sw.ElapsedMilliseconds,
                        Result = JsonDocument.Parse("{\"error\":\"unknown_tool\"}").RootElement
                    });
                    continue;
                }

                if (ToolManifest.IsAdminTool(call.Name) && !_api.HasAdminKey)
                {
                    results.Items.Add(new ToolResults.Item
                    {
                        ToolName = call.Name,
                        Error = "admin_required",
                        DurationMs = sw.ElapsedMilliseconds,
                        Result = JsonDocument.Parse("{\"error\":\"admin_required\"}").RootElement
                    });
                    continue;
                }
                var handlers = GetOrCreateToolHandlers();
                if (!handlers.TryGetValue(call.Name, out var handler))
                {
                    results.Items.Add(new ToolResults.Item
                    {
                        ToolName = call.Name,
                        Error = "unknown_tool",
                        DurationMs = sw.ElapsedMilliseconds,
                        Result = JsonDocument.Parse("{\"error\":\"unknown_tool\"}").RootElement
                    });
                    continue;
                }

                JsonElement res = await handler(call.Args, ct).ConfigureAwait(false);

                results.Items.Add(new ToolResults.Item
                {
                    ToolName = call.Name,
                    Result = res,
                    DurationMs = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                results.Items.Add(new ToolResults.Item
                {
                    ToolName = call.Name,
                    Error = ex.Message,
                    DurationMs = sw.ElapsedMilliseconds,
                    Result = JsonDocument.Parse("{\"error\":\"tool_failed\"}").RootElement
                });
            }
        }

        return results;
    }

    private async Task<(string answer, List<ToolMemory.SourceRef>? sources)> AnswerAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults toolResults,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        var writerTrace = plan.ReasoningTracePublic?.Skip(1).FirstOrDefault();
        onProgress?.Invoke(!string.IsNullOrWhiteSpace(writerTrace)
            ? writerTrace
            : DeterministicAgentText.ProgressDraftFinalAnswer(plan.Language));

        var useGeneralChatPrompt = plan.ToolCalls.Count == 0 && (string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase) || string.Equals(plan.Intent, "meta.help", StringComparison.OrdinalIgnoreCase));
        _lastUsedGeneralChatPrompt = useGeneralChatPrompt;
        var system = useGeneralChatPrompt
            ? PromptCatalog.BuildGeneralChatSystemPrompt(plan.Language)
            : PromptCatalog.BuildWriterSystemPrompt(plan.Language, plan.Mode, allowGeneralChat: plan.ToolCalls.Count == 0 || string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase));

        var writerToolResults = BuildWriterToolResults(plan, toolResults);
        _lastWriterToolNames = writerToolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _lastUsedInventoryRendered = _lastUsedInventoryRendered || _lastWriterToolNames.Any(x => string.Equals(x, "inventory.rendered", StringComparison.OrdinalIgnoreCase));
        var inventoryRenderedText = TryRenderInventoryFallbackText(writerToolResults, plan.Language);
        var inventoryRenderedDataJson = TryExtractInventoryRenderedDataJson(writerToolResults);

        if (ShouldBypassWriterForDeterministicInventory(plan, writerToolResults, inventoryRenderedText))
        {
            var deterministicAnswer = (inventoryRenderedText ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(deterministicAnswer))
                return (deterministicAnswer, null);
        }

        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 10)}

USER_MESSAGE:
{userMessage}

TOOL_RESULTS (json):
{SerializeToolResults(writerToolResults)}

AUTHORITATIVE_INVENTORY_DATA (json):
{inventoryRenderedDataJson ?? "null"}
";

        var writerMessages = new[]
        {
            ("system", system),
            ("user", user)
        };

        var finalAnswer = await StreamOrCompleteWithRetryAsync(writerMessages, onDelta, ct).ConfigureAwait(false);

        finalAnswer = (finalAnswer ?? string.Empty).Replace("**", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(finalAnswer) && !string.IsNullOrWhiteSpace(inventoryRenderedText))
            finalAnswer = inventoryRenderedText.Trim();
        if (string.IsNullOrWhiteSpace(finalAnswer))
            finalAnswer = DeterministicAgentText.AnswerNotEnoughUsableInfo(plan.Language);

        if (plan.ToolCalls.Count == 0)
        {
            finalAnswer = await EnsureAnswerMatchesRequestedLanguageAsync(finalAnswer, plan.Language, ct).ConfigureAwait(false);
        }

        List<ToolMemory.SourceRef>? sources = null;
        var usedRagSearch = toolResults.Items.Any(x => x.ToolName is "rag.search" or "rag.multi_search");
        var usedSourcesResolve = toolResults.Items.Any(x => x.ToolName == "sources.resolve");

        if (usedRagSearch)
        {
            sources = DeriveSourcesFromRagHits(toolResults);
        }
        else if (usedSourcesResolve)
        {
            var resolved = TryBuildSourceFromResolveResult(toolResults);
            if (resolved is not null)
                sources = new List<ToolMemory.SourceRef> { resolved };
        }

        if (ShouldRunCriticPass(plan, toolResults, useGeneralChatPrompt))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressCheckAlignmentWithSources(plan.Language));

            finalAnswer = await RunCriticPassAsync(chatHistory, userMessage, plan, toolResults, finalAnswer, ct).ConfigureAwait(false);
        }

        return (finalAnswer, sources);
    }

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

    private bool ShouldRunCriticPass(RouterPlan plan, ToolResults toolResults, bool useGeneralChatPrompt)
    {
        _lastCriticEligible = false;
        _lastCriticSkipReason = null;

        if (useGeneralChatPrompt)
        {
            _lastCriticStatus = "skipped";
            _lastCriticSkipReason = "general_chat";
            return false;
        }

        var strict = string.Equals(plan.Mode, "strict", StringComparison.OrdinalIgnoreCase) || (_settings?.StrictMode ?? false);
        if (!strict)
        {
            _lastCriticStatus = "skipped";
            _lastCriticSkipReason = "mode_not_strict";
            return false;
        }

        var eligible = toolResults.Items.Any(x => string.IsNullOrWhiteSpace(x.Error) && IsGroundedToolForCritic(x.ToolName));
        _lastCriticEligible = eligible;
        if (!eligible)
        {
            _lastCriticStatus = "skipped";
            _lastCriticSkipReason = "no_grounded_tools";
            return false;
        }

        return true;
    }

    private async Task<string> RunCriticPassAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults toolResults,
        string draftAnswer,
        CancellationToken ct)
    {
        var system = PromptCatalog.BuildCriticSystemPrompt(plan.Language);
        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 8)}

USER_MESSAGE:
{userMessage}

DRAFT_ANSWER:
{draftAnswer}

TOOL_RESULTS (json):
{SerializeToolResults(toolResults)}
";

        _lastCriticMs = 0;
        _lastCriticStatus = "skipped";
        _lastCriticWarning = null;
        _lastCriticRevisedAnswer = false;

        var swCritic = Stopwatch.StartNew();
        try
        {
            var raw = await _llm.CompleteAsync(new[]
            {
                ("system", system),
                ("user", user)
            }, forceJson: true, ct).ConfigureAwait(false);
            swCritic.Stop();
            _lastCriticMs = swCritic.ElapsedMilliseconds;

            if (TryParseCriticEnvelope(raw, out var status, out var revised, out var warning))
            {
                _lastCriticStatus = string.IsNullOrWhiteSpace(status) ? "ok" : status!.Trim().ToLowerInvariant();
                _lastCriticWarning = string.IsNullOrWhiteSpace(warning) ? null : warning!.Trim();

                if (string.Equals(status, "revise", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(revised))
                {
                    _lastCriticRevisedAnswer = true;
                    return revised.Trim().Replace("**", string.Empty);
                }

                return draftAnswer;
            }

            _lastCriticStatus = "invalid";
        }
        catch
        {
            if (swCritic.IsRunning)
                swCritic.Stop();
            _lastCriticMs = swCritic.ElapsedMilliseconds;
            _lastCriticStatus = "error";
        }

        return draftAnswer;
    }

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleRouterDrivenDocumentSummaryFlowAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string displayUserMessage,
        string semanticUserMessage,
        RouterPlan plan,
        DocumentRefResolver.AnalysisResult docResolution,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress,
        Stopwatch swTotalPipeline)
    {
        if (!TryDetermineSummaryRequestKind(semanticUserMessage, plan, docResolution, out var requestKind))
            return (false, string.Empty, null);

        var docRef = ResolveDocumentReferenceForSummaryPlan(plan, docResolution);
        if (string.IsNullOrWhiteSpace(docRef))
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseClarification(plan.Language));
            var clarification = await GenerateClarificationResponseAsync(
                chatHistory,
                displayUserMessage,
                plan.Language,
                "doc_reference",
                docResolution.ClarificationHint ?? "document_reference",
                ct,
                onDelta).ConfigureAwait(false);

            RememberPendingClarification("doc_reference", displayUserMessage, docResolution.ClarificationHint ?? "document_reference", plan.Language);
            RememberTurnState(displayUserMessage, clarification, "clarification", Array.Empty<string>(), _mem.LastReasoningTracePublic);
            onProgress?.Invoke(string.Empty);
            _lastToolsMs = 0;
            _lastWriterMs = 0;
            swTotalPipeline.Stop();
            _lastTotalMs = swTotalPipeline.ElapsedMilliseconds;
            return (true, clarification, null);
        }

        onPhase?.Invoke(DeterministicAgentText.PhaseSummary(plan.Language));
        var swSummary = Stopwatch.StartNew();
        var summaryAnswer = await RunKnownDocumentSummaryFlowAsync(semanticUserMessage, docRef, requestKind, ct, onDelta, onProgress).ConfigureAwait(false);
        swSummary.Stop();

        var rememberedIntent = requestKind switch
        {
            DocumentSummaryRequestKind.About => "rag.summarize_doc",
            DocumentSummaryRequestKind.SummaryReadStoredExact => "summary.get",
            DocumentSummaryRequestKind.SummaryCheckOnly => "summary.check",
            DocumentSummaryRequestKind.SummaryStore => "admin.summary.store",
            _ => "rag.summarize_doc"
        };

        ClearPendingClarification();
        RememberTurnState(displayUserMessage, summaryAnswer.finalAnswer, rememberedIntent, new[] { "summary.flow" }, _mem.LastReasoningTracePublic);
        _lastUsedSummaryFlow = true;
        _lastWriterToolNames = new List<string> { "summary.flow" };
        _lastToolsMs = swSummary.ElapsedMilliseconds;
        _lastWriterMs = 0;
        _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { ("summary.flow", swSummary.ElapsedMilliseconds, true) };

        swTotalPipeline.Stop();
        _lastTotalMs = swTotalPipeline.ElapsedMilliseconds;
        return (true, summaryAnswer.finalAnswer, summaryAnswer.sourcesPayload);
    }

    private bool TryDetermineSummaryRequestKind(string userMessage, RouterPlan plan, DocumentRefResolver.AnalysisResult docResolution, out DocumentSummaryRequestKind requestKind)
    {
        requestKind = default;

        var intent = NormalizeRouterIntent(plan.Intent);
        if (intent == "summary.check")
        {
            requestKind = DocumentSummaryRequestKind.SummaryCheckOnly;
            return true;
        }

        if (intent == "admin.summary.store")
        {
            requestKind = DocumentSummaryRequestKind.SummaryStore;
            return true;
        }

        if (intent is "document.about" or "document_about" or "rag.about_doc")
        {
            requestKind = DocumentSummaryRequestKind.About;
            return true;
        }

        if (intent == "rag.summarize_doc")
        {
            requestKind = IsAboutResponseFormat(plan.ResponseFormat) || (docResolution.WantsAbout && !docResolution.WantsSummary)
                ? DocumentSummaryRequestKind.About
                : DocumentSummaryRequestKind.SummaryReadOrLive;
            return true;
        }

        var hasSummaryTool = plan.ToolCalls.Any(call => call.Name is "summary.exists" or "summary.get" or "rag.summarize_live" or "admin.summary.generate" or "admin.summary.submit" or "admin.summary.request");
        if (!hasSummaryTool)
            return false;

        if (docResolution.WantsStoredSummaryStore)
        {
            requestKind = DocumentSummaryRequestKind.SummaryStore;
            return true;
        }

        if (IsExplicitStoredSummaryReadRequest(userMessage, docResolution))
        {
            requestKind = DocumentSummaryRequestKind.SummaryReadStoredExact;
            return true;
        }

        if (docResolution.WantsStoredSummaryCheck)
        {
            requestKind = DocumentSummaryRequestKind.SummaryCheckOnly;
            return true;
        }

        requestKind = IsAboutResponseFormat(plan.ResponseFormat) || (docResolution.WantsAbout && !docResolution.WantsSummary)
            ? DocumentSummaryRequestKind.About
            : DocumentSummaryRequestKind.SummaryReadOrLive;
        return true;
    }

    private string? ResolveDocumentReferenceForSummaryPlan(RouterPlan plan, DocumentRefResolver.AnalysisResult docResolution)
    {
        foreach (var call in plan.ToolCalls)
        {
            var docRef = GetStringArg(call.Args, "docRef")
                         ?? GetStringArg(call.Args, "pdfRef")
                         ?? GetStringArg(call.Args, "docId")
                         ?? GetStringArg(call.Args, "docPath");
            if (!string.IsNullOrWhiteSpace(docRef))
                return docRef;
        }

        if (!string.IsNullOrWhiteSpace(docResolution.ResolvedDocRef))
            return docResolution.ResolvedDocRef;

        if (_mem.LastFocusedDocument is not null)
        {
            if (!string.IsNullOrWhiteSpace(_mem.LastFocusedDocument.DocId))
                return _mem.LastFocusedDocument.DocId;
            if (!string.IsNullOrWhiteSpace(_mem.LastFocusedDocument.DocPath))
                return _mem.LastFocusedDocument.DocPath;
            if (!string.IsNullOrWhiteSpace(_mem.LastFocusedDocument.DocName))
                return _mem.LastFocusedDocument.DocName;
        }

        return null;
    }

    private static IEnumerable<RouterPlan.ToolCall> SanitizeToolCalls(IReadOnlyList<RouterPlan.ToolCall>? toolCalls)
    {
        if (toolCalls is null)
            yield break;

        foreach (var call in toolCalls)
        {
            if (call is null || string.IsNullOrWhiteSpace(call.Name))
                continue;

            var normalizedName = NormalizeToolName(call.Name);
            if (!ToolManifest.IsKnownTool(normalizedName))
                continue;

            yield return new RouterPlan.ToolCall
            {
                Name = normalizedName,
                Args = NormalizeToolArgs(normalizedName, call.Args)
            };
        }
    }

    private static string NormalizeToolName(string? toolName)
    {
        var normalized = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "documents.catalog" => "documents.list",
            "documents.find" => "documents.search",
            "diagnostic.latency" => "diagnostic.performance",
            "support.zip" or "support.export" => "support.bundle",
            "summary.read" => "summary.get",
            "summary.missing" or "summaries.missing" => "summary.status.list",
            "summary.missing_count" or "summaries.missing_count" => "summary.status.count",
            _ => normalized
        };
    }

    private static JsonElement NormalizeToolArgs(string toolName, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return JsonDocument.Parse("{}").RootElement.Clone();

        object payload = toolName switch
        {
            "documents.list" => new
            {
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                q = GetStringArg(args, "q") ?? GetStringArg(args, "query"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 80, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.search" => new
            {
                q = (GetStringArg(args, "q") ?? GetStringArg(args, "query") ?? string.Empty).Trim(),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 80, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.count" => new
            {
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef"),
                q = GetStringArg(args, "q") ?? GetStringArg(args, "query")
            },
            "documents.categories" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.tree" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef"),
                depth = NormalizeIntArg(GetIntArg(args, "depth"), 8, 1, 20),
                format = NormalizeTreeFormat(GetStringArg(args, "format")),
                limit = NormalizeNullableIntArg(GetIntArg(args, "limit"), 1, 5000),
                offset = NormalizeNullableIntArg(GetIntArg(args, "offset"), 0, 100000)
            },
            "documents.stats" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                categoryRef = GetStringArg(args, "categoryRef")
            },
            "documents.empty_count" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath"))
            },
            "documents.empty_list" => new
            {
                path = NormalizeCategoryPathArg(GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath")),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 200, 1, 2000),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "sources.resolve" => new
            {
                @ref = (GetStringArg(args, "ref") ?? GetStringArg(args, "pdfRef") ?? string.Empty).Trim()
            },
            "rag.search" => new
            {
                query = (GetStringArg(args, "query") ?? string.Empty).Trim(),
                topK = NormalizeIntArg(GetIntArg(args, "topK"), 8, 1, 20),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category")),
                mode = NormalizeRagMode(GetStringArg(args, "mode"))
            },
            "rag.multi_search" => new
            {
                queries = GetStringArrayArg(args, "queries")?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(5).ToArray() ?? Array.Empty<string>(),
                topK = NormalizeIntArg(GetIntArg(args, "topK"), 8, 1, 20),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category")),
                mode = NormalizeRagMode(GetStringArg(args, "mode"))
            },
            "rag.summarize_live" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty,
                level = NormalizeLiveSummaryLevel(GetStringArg(args, "level")),
                strategy = NormalizeSummaryStrategy(GetStringArg(args, "strategy")),
                language = NormalizeSummaryLanguage(GetStringArg(args, "language")),
                maxWords = NormalizeNullableIntArg(GetIntArg(args, "maxWords"), 20, 1200),
                maxChunks = NormalizeNullableIntArg(GetIntArg(args, "maxChunks"), 1, 40),
                maxBatches = NormalizeNullableIntArg(GetIntArg(args, "maxBatches"), 1, 8),
                maxCharsPerBatch = NormalizeNullableIntArg(GetIntArg(args, "maxCharsPerBatch"), 1000, 12000)
            },
            "summary.get" or "summary.exists" or "admin.summary.request" or "admin.summary.generate" or "admin.summary.delete" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty,
                level = "medium",
                force = GetBoolArg(args, "force") ?? false
            },
            "summary.search" => new
            {
                q = (GetStringArg(args, "q") ?? string.Empty).Trim(),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 20, 1, 200),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "summary.status.count" => new
            {
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef")
            },
            "summary.status.list" or "admin.summary.missing" => new
            {
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef")
            },
            "admin.summary.submit" => new
            {
                jobId = GetStringArg(args, "jobId"),
                docRef = GetDocRefFromArgs(args) ?? string.Empty,
                level = "medium",
                docLanguage = NormalizeSummaryLanguage(GetStringArg(args, "docLanguage") ?? GetStringArg(args, "language")),
                sourceHash = GetStringArg(args, "sourceHash") ?? string.Empty,
                summaryText = GetStringArg(args, "summaryText") ?? GetStringArg(args, "content") ?? string.Empty,
                meta = TryGetObjectArg(args, "meta")
            },
            "admin.summary.status" or "admin.jobs.cancel" => new
            {
                jobId = GetStringArg(args, "jobId") ?? string.Empty
            },
            "admin.ingestion.reindex" => new
            {
                docRef = GetDocRefFromArgs(args) ?? string.Empty
            },
            "admin.jobs.list" => new
            {
                type = GetStringArg(args, "type"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "diagnostic.performance" => new
            {
                lastN = NormalizeNullableIntArg(GetIntArg(args, "lastN"), 1, 50)
            },
            "export.create" => new
            {
                format = NormalizeExportFormat(GetStringArg(args, "format")),
                fileName = (GetStringArg(args, "fileName") ?? GetStringArg(args, "title") ?? "export").Trim(),
                content = GetStringArg(args, "content") ?? string.Empty
            },
            "support.bundle" => new
            {
                include = GetStringArrayArg(args, "include")?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>()
            },
            "rag.debug.scroll" => new
            {
                docRef = GetDocRefFromArgs(args),
                docPath = GetStringArg(args, "docPath"),
                cursor = GetStringArg(args, "cursor"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 500)
            },
            _ => JsonSerializer.Deserialize<object>(args.GetRawText()) ?? new { }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
    }

    private static int NormalizeIntArg(int? value, int fallback, int min, int max)
        => Math.Clamp(value ?? fallback, min, max);

    private static int? NormalizeNullableIntArg(int? value, int min, int max)
        => value.HasValue ? Math.Clamp(value.Value, min, max) : null;

    private static string? NormalizeCategoryPathArg(string? categoryPath)
    {
        var normalized = (categoryPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeTreeFormat(string? format)
        => string.Equals((format ?? string.Empty).Trim(), "json", StringComparison.OrdinalIgnoreCase) ? "json" : "markdown";

    private static string NormalizeRagMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "balanced" or "standard" or "strict" ? normalized : "balanced";
    }

    private static string NormalizeLiveSummaryLevel(string? level)
    {
        var normalized = (level ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "short" or "medium" or "long" ? normalized : "medium";
    }

    private static string NormalizeSummaryStrategy(string? strategy)
    {
        var normalized = (strategy ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "about" or "summary" or "store" ? normalized : "summary";
    }

    private static string NormalizeSummaryLanguage(string? language)
    {
        var normalized = (language ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "auto" or "fr" or "en" or "es" or "pt" or "de" or "it" ? normalized : "auto";
    }

    private static string NormalizeExportFormat(string? format)
    {
        var normalized = (format ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "txt" or "md" or "csv" or "docx" ? normalized : "txt";
    }

    private static string? GetDocRefFromArgs(JsonElement args)
        => GetStringArg(args, "docRef")
           ?? GetStringArg(args, "docId")
           ?? GetStringArg(args, "docPath")
           ?? GetStringArg(args, "ref")
           ?? GetStringArg(args, "pdfRef");

    private static string? GetNestedStringArg(JsonElement args, string parent, string child)
    {
        if (!args.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        return GetStringArg(p, child);
    }

    private static object? TryGetObjectArg(JsonElement args, string propertyName)
    {
        if (!args.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return JsonSerializer.Deserialize<object>(value.GetRawText());
    }

    private RouterPlan SanitizeRouterPlan(RouterPlan plan, string detectedMessageLanguage, bool disallowMetaSetLanguage)
    {
        plan ??= new RouterPlan();

        plan.Mode = NormalizePlanMode(plan.Mode);
        plan.Language = NormalizeLanguageCode(string.IsNullOrWhiteSpace(plan.Language) ? detectedMessageLanguage : plan.Language);
        plan.Intent = NormalizeRouterIntent(plan.Intent);
        plan.ResponseFormat = NormalizeResponseFormat(plan.ResponseFormat);
        plan.MemoryUpdate = string.IsNullOrWhiteSpace(plan.MemoryUpdate) ? null : plan.MemoryUpdate.Trim();
        plan.Confidence = ClampConfidence(plan.Confidence);
        plan.ClarificationQuestions = plan.ClarificationQuestions
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList() ?? new List<string>();
        plan.ReasoningTracePublic = plan.ReasoningTracePublic
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Take(3)
            .ToList() ?? new List<string>();
        plan.ToolCalls = SanitizeToolCalls(plan.ToolCalls)
            .Take(MaxToolCalls)
            .ToList();

        if (string.IsNullOrWhiteSpace(plan.Intent))
            plan.Intent = InferIntentFromToolCalls(plan.ToolCalls) ?? "chat.general";

        if (disallowMetaSetLanguage && string.Equals(plan.Intent, "meta.set_language", StringComparison.OrdinalIgnoreCase))
            plan.Intent = "chat.general";

        if (plan.NeedClarification && plan.ClarificationQuestions.Count == 0)
            plan.ClarificationQuestions = new List<string>();

        return plan;
    }

    private static string NormalizeRouterIntent(string? intent)
    {
        var normalized = (intent ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return normalized switch
        {
            "general" or "chat" or "conversation" => "chat.general",
            "set_language" or "change_language" or "meta.change_language" => "meta.set_language",
            "repair" or "rewrite_last" or "meta.rewrite_last" => "meta.repair_last",
            "count_documents" or "documents.count" => "inventory.count",
            "list_documents" or "documents.list" or "documents.search" or "inventory.find" => "inventory.list",
            "categories" or "documents.categories" => "inventory.categories",
            "tree" or "documents.tree" or "inventory.tree_sub" => "inventory.tree",
            "stats" or "documents.stats" => "inventory.stats",
            "summary.status" or "summary_status" or "inventory.summary_status" or "admin.summary.missing" or "summary.status.count" or "summary.status.list" or "summary.present.count" or "summary.present.list" => "inventory.summary_status",
            "document.about" or "document_about" or "rag.about_doc" => "rag.summarize_doc",
            "summary.exists" or "check_summary" => "summary.check",
            "summary.store" or "refresh_summary" => "admin.summary.store",
            _ => normalized
        };
    }

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
            "documents.stats" => "inventory.stats",
            "summary.status.count" or "summary.status.list" or "summary.present.count" or "summary.present.list" or "admin.summary.missing" => "inventory.summary_status",
            "documents.list" or "documents.search" => "inventory.list",
            "summary.exists" or "summary.get" => "summary.check",
            "rag.search" or "rag.multi_search" => "rag.answer",
            "rag.summarize_live" => "rag.summarize_doc",
            "admin.summary.request" or "admin.summary.generate" or "admin.summary.submit" => "admin.summary.store",
            "diagnostic.performance" => "diagnostic.performance",
            "export.create" => "export.create",
            _ => null
        };
    }

    private static bool IsGroundedToolForCritic(string? toolName)
    {
        return toolName switch
        {
            "documents.list" or "documents.search" or "documents.get" or "documents.count" or "documents.categories" or "documents.tree" or "documents.stats" or "documents.empty_count" or "documents.empty_list" or
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

        if (errors.Any(x => string.Equals(x, "admin_required", StringComparison.OrdinalIgnoreCase)))
        {
            return DeterministicAgentText.ToolFailureAdminRequired(language);
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

    private (string finalAnswer, object? sourcesPayload) FinalizeAndReturn(
        Stopwatch swTotalPipeline,
        string rememberedUserMessage,
        string finalAnswer,
        object? sourcesPayload,
        string? routerIntent,
        IEnumerable<string>? toolNames,
        IEnumerable<string>? reasoningTracePublic,
        bool clearPendingClarification = true)
    {
        if (clearPendingClarification)
            ClearPendingClarification();

        if (IsInventoryIntent(routerIntent)
            && _mem.LastDeterministicRender is not null
            && string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.SourceUserMessage))
        {
            _mem.LastDeterministicRender.SourceUserMessage = rememberedUserMessage;
        }

        RememberTurnState(rememberedUserMessage, finalAnswer, routerIntent, toolNames, reasoningTracePublic);
        swTotalPipeline.Stop();
        _lastTotalMs = swTotalPipeline.ElapsedMilliseconds;
        return (finalAnswer, sourcesPayload);
    }

    private void ResetLastTurnDiagnostics()
    {
        _lastRouterMs = 0;
        _lastToolsMs = 0;
        _lastWriterMs = 0;
        _lastTotalMs = 0;
        _lastCriticMs = 0;
        _lastCriticStatus = null;
        _lastCriticWarning = null;
        _lastCriticRevisedAnswer = false;
        _lastCriticEligible = false;
        _lastCriticSkipReason = null;
        _lastUsedGeneralChatPrompt = false;
        _lastUsedInventoryRendered = false;
        _lastUsedSummaryFlow = false;
        _lastResponseFormat = "auto";
        _lastEffectiveMode = "auto";
        _lastWriterToolNames = new List<string>();
        _lastToolDurations = new List<(string tool, long durationMs, bool ok)>();
    }

    private Dictionary<string, object?> BuildAgentRuntimeSnapshot()
    {
        return new Dictionary<string, object?>
        {
            ["supported"] = true,
            ["routerMs"] = _lastRouterMs,
            ["toolsMs"] = _lastToolsMs,
            ["writerMs"] = _lastWriterMs,
            ["totalMs"] = _lastTotalMs,
            ["language"] = _mem.LastLanguage,
            ["critic"] = new Dictionary<string, object?>
            {
                ["enabled"] = _lastCriticMs > 0 || !string.IsNullOrWhiteSpace(_lastCriticStatus) || _lastCriticEligible,
                ["eligible"] = _lastCriticEligible,
                ["durationMs"] = _lastCriticMs,
                ["status"] = _lastCriticStatus,
                ["revisedAnswer"] = _lastCriticRevisedAnswer,
                ["warning"] = _lastCriticWarning,
                ["skipReason"] = _lastCriticSkipReason
            },
            ["tools"] = _lastToolDurations.Select(x => new Dictionary<string, object?>
            {
                ["tool"] = x.tool,
                ["durationMs"] = x.durationMs,
                ["ok"] = x.ok
            }).ToList(),
            ["turn"] = new Dictionary<string, object?>
            {
                ["intent"] = _mem.LastRouterIntent,
                ["toolNames"] = _mem.LastToolNames,
                ["writerToolNames"] = _lastWriterToolNames,
                ["memoryUpdate"] = _mem.LastPlannerMemoryUpdate,
                ["confidence"] = _mem.LastRouterConfidence,
                ["mode"] = _lastEffectiveMode,
                ["responseFormat"] = _lastResponseFormat
            },
            ["session"] = new Dictionary<string, object?>
            {
                ["hasAdminKey"] = _api.HasAdminKey
            },
            ["qa"] = new Dictionary<string, object?>
            {
                ["usedGeneralChatPrompt"] = _lastUsedGeneralChatPrompt,
                ["usedInventoryRendered"] = _lastUsedInventoryRendered,
                ["usedSummaryFlow"] = _lastUsedSummaryFlow
            },
            ["memory"] = new Dictionary<string, object?>
            {
                ["hasPendingClarification"] = _mem.PendingClarification is not null,
                ["pendingClarificationKind"] = _mem.PendingClarification?.Kind,
                ["lastFocusedDocument"] = _mem.LastFocusedDocument is null ? null : new Dictionary<string, object?>
                {
                    ["docId"] = _mem.LastFocusedDocument.DocId,
                    ["docPath"] = _mem.LastFocusedDocument.DocPath,
                    ["docName"] = _mem.LastFocusedDocument.DocName,
                    ["categoryPath"] = _mem.LastFocusedDocument.CategoryPath,
                    ["pdfRef"] = _mem.LastFocusedDocument.PdfRef
                },
                ["lastListedDocumentsCount"] = _mem.LastListedDocuments?.Count ?? 0,
                ["pdfMapSize"] = _mem.PdfMap?.Count ?? 0,
                ["lastSourcesCount"] = _mem.LastSourcesUsed?.Count ?? 0
            }
        };
    }

    private sealed record PendingClarificationPreparation(string EffectiveUserMessage, DocumentRefResolver.AnalysisResult? AnalysisOverride, bool Consumed);

    private PendingClarificationPreparation PrepareUserMessageForPendingClarification(string? userMessage)
    {
        var safeUserMessage = userMessage ?? string.Empty;
        var pending = _mem.PendingClarification;
        if (pending is null || string.IsNullOrWhiteSpace(pending.OriginalUserMessage))
            return new PendingClarificationPreparation(safeUserMessage, null, false);

        var current = safeUserMessage.Trim();
        if (current.Length == 0)
            return new PendingClarificationPreparation(safeUserMessage, null, false);

        if (DocumentRefResolver.IsRepairMessage(current) || TryDetectExplicitLanguageSwitch(current, out _))
        {
            ClearPendingClarification();
            return new PendingClarificationPreparation(safeUserMessage, null, false);
        }

        var expectsDocumentAnswer = string.Equals(pending.Kind, "doc_reference", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pending.Kind, "document_reference", StringComparison.OrdinalIgnoreCase);
        var expectsTreeScope = string.Equals(pending.Kind, "tree_scope", StringComparison.OrdinalIgnoreCase);
        var expectsRagProbeRefinement = string.Equals(pending.Kind, "rag_probe", StringComparison.OrdinalIgnoreCase);

        var isExpectedAnswer = expectsDocumentAnswer
            ? DocumentRefResolver.LooksLikeDocumentReferenceAnswer(current, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef)
            : expectsTreeScope
                ? DocumentRefResolver.LooksLikeTreeScopeAnswer(current)
                : expectsRagProbeRefinement && current.Length >= 2;

        if (!isExpectedAnswer)
        {
            if (LooksLikeClarificationMetaAck(current))
                return new PendingClarificationPreparation(safeUserMessage, null, false);

            ClearPendingClarification();
            return new PendingClarificationPreparation(safeUserMessage, null, false);
        }

        var effectiveUserMessage = expectsRagProbeRefinement
            ? $@"PREVIOUS_DOCUMENTARY_REQUEST:
{pending.OriginalUserMessage}

RETRIEVAL_REFINEMENT:
{current}"
            : $@"PREVIOUS_AMBIGUOUS_REQUEST:
{pending.OriginalUserMessage}

CLARIFICATION_ANSWER:
{current}";

        var analysisOverride = expectsRagProbeRefinement
            ? null
            : DocumentRefResolver.Analyze(effectiveUserMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);
        ClearPendingClarification();
        return new PendingClarificationPreparation(effectiveUserMessage, analysisOverride, true);
    }

    private void RememberPendingClarification(string kind, string originalUserMessage, string? hint, string? language)
    {
        _mem.PendingClarification = new ToolMemory.PendingClarificationState
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? "generic" : kind.Trim(),
            OriginalUserMessage = originalUserMessage ?? string.Empty,
            Hint = hint,
            Language = language,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private void ClearPendingClarification()
        => _mem.PendingClarification = null;

    private static bool LooksLikeClarificationMetaAck(string? userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s, @"^(?:yes|yeah|yep|oui|ok|okay|d['’]accord|exactly|exact|precisely|exactement|pr[ée]cis[eé]ment|correct|c['’]est\s+ça|that['’]?s\s+right)$", RegexOptions.IgnoreCase);
    }

    private async Task<string> GenerateClarificationResponseAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        string language,
        string clarificationKind,
        string? hint,
        CancellationToken ct,
        Action<string>? onDelta)
    {
        var system = PromptCatalog.BuildClarificationSystemPrompt(language, clarificationKind, hint);
        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 8)}

USER_MESSAGE:
{userMessage}
";

        return await CompleteTextResponseAsync(system, user, ct, onDelta).ConfigureAwait(false);
    }

    private async Task<string> EnsureAnswerMatchesRequestedLanguageAsync(string answer, string requestedLanguage, CancellationToken ct)
    {
        var trimmed = (answer ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return trimmed;

        var targetLanguage = NormalizeLanguageCode(requestedLanguage);
        var detectedLanguageRaw = DetectMessageLanguage(trimmed);
        if (string.IsNullOrWhiteSpace(targetLanguage) || string.IsNullOrWhiteSpace(detectedLanguageRaw))
            return trimmed;

        var detectedLanguage = NormalizeLanguageCode(detectedLanguageRaw);
        if (string.Equals(detectedLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
            return trimmed;

        try
        {
            var system = $@"You are SAAIA assistant.
Translate the provided assistant answer faithfully.
Target language: {targetLanguage}
Rules:
- Keep the same meaning and tone.
- Do not add or remove facts.
- Return plain text only.";

            var translated = await _llm.CompleteAsync(new[]
            {
                ("system", system),
                ("user", trimmed)
            }, forceJson: false, ct).ConfigureAwait(false);

            translated = (translated ?? string.Empty).Replace("**", string.Empty).Trim();
            return string.IsNullOrWhiteSpace(translated) ? trimmed : translated;
        }
        catch
        {
            return trimmed;
        }
    }

    private async Task<string> GenerateRepairResponseAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        string language,
        CancellationToken ct,
        Action<string>? onDelta)
    {
        var system = PromptCatalog.BuildRepairSystemPrompt(language);
        var user = $@"
LAST_USER_MESSAGE:
{_mem.LastUserMessage}

LAST_ASSISTANT_ANSWER:
{_mem.LastAssistantAnswer}

LAST_ROUTER_INTENT:
{_mem.LastRouterIntent}

LAST_TOOLS:
{string.Join(", ", _mem.LastToolNames ?? new List<string>())}

LAST_ROUTER_CONFIDENCE:
{(_mem.LastRouterConfidence.HasValue ? _mem.LastRouterConfidence.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : string.Empty)}

LAST_REASONING_TRACE_PUBLIC:
{string.Join(" | ", _mem.LastReasoningTracePublic ?? new List<string>())}

LAST_MEMORY_UPDATE:
{_mem.LastPlannerMemoryUpdate}

CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 8)}

CURRENT_USER_MESSAGE:
{userMessage}
";

        return await CompleteTextResponseAsync(system, user, ct, onDelta).ConfigureAwait(false);
    }

    private async Task<string> CompleteTextResponseAsync(string system, string user, CancellationToken ct, Action<string>? onDelta)
    {
        if (onDelta is not null)
        {
            var streamed = new StringBuilder();
            await _llm.StreamAsync(new[] { ("system", system), ("user", user) }, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta(delta);
            }, ct).ConfigureAwait(false);

            var txt = streamed.ToString().Replace("**", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(txt))
                return txt;
        }

        var raw = await _llm.CompleteAsync(new[] { ("system", system), ("user", user) }, forceJson: false, ct).ConfigureAwait(false);
        return (raw ?? string.Empty).Replace("**", string.Empty).Trim();
    }

    private void RememberTurnState(string userMessage, string assistantAnswer, string? routerIntent, IEnumerable<string>? toolNames, IEnumerable<string>? reasoningTracePublic)
    {
        _mem.LastUserMessage = userMessage;
        _mem.LastAssistantAnswer = assistantAnswer;
        _mem.LastRouterIntent = routerIntent;
        _mem.LastToolNames = toolNames?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        _mem.LastReasoningTracePublic = reasoningTracePublic?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();

        var hasExplicitLanguageSwitch = TryDetectExplicitLanguageSwitch(userMessage, out var requestedLanguage);
        var isOneShotTranslation = string.Equals(routerIntent, "meta.translate_last_answer", StringComparison.OrdinalIgnoreCase)
            || hasExplicitLanguageSwitch;

        var answerLanguage = isOneShotTranslation && !string.IsNullOrWhiteSpace(requestedLanguage)
            ? requestedLanguage
            : DetectMessageLanguage(assistantAnswer);

        if (!string.IsNullOrWhiteSpace(answerLanguage))
        {
            _mem.LastAnswerLanguage = answerLanguage;
            if (!isOneShotTranslation)
                _mem.LastLanguage = answerLanguage;
        }

        if (!isOneShotTranslation)
        {
            var userLanguage = DetectMessageLanguage(userMessage);
            if (string.IsNullOrWhiteSpace(userLanguage))
                userLanguage = "fr";

            _mem.LastUserDetectedLanguage = userLanguage;
        }
    }


    private static bool TryParseAnswerEnvelope(string json, out string? finalAnswer, out List<ToolMemory.SourceRef>? sources)
    {
        finalAnswer = null;
        sources = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("finalAnswer", out var a) && a.ValueKind == JsonValueKind.String)
                finalAnswer = a.GetString();
            else
                finalAnswer = "";

            if (root.TryGetProperty("sources", out var sArr) && sArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<ToolMemory.SourceRef>();
                foreach (var s in sArr.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;

                    var docPath = s.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(docPath)) continue;

                    var ps = s.TryGetProperty("pageStart", out var p1) && p1.ValueKind == JsonValueKind.Number ? p1.GetInt32() : 1;
                    var pe = s.TryGetProperty("pageEnd", out var p2) && p2.ValueKind == JsonValueKind.Number ? p2.GetInt32() : ps;
                    var label = s.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? (lb.GetString() ?? "") : "";

                    list.Add(new ToolMemory.SourceRef
                    {
                        DocPath = docPath,
                        PageStart = ps,
                        PageEnd = pe,
                        Label = label
                    });
                }

                sources = list;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ToolMemory.SourceRef? TryBuildSourceFromResolveResult(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName == "sources.resolve");
            if (item is null) return null;

            var res = item.Result;
            if (res.ValueKind != JsonValueKind.Object) return null;
            if (!res.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.Object) return null;

            var docPath = src.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(docPath)) return null;

            var ps = src.TryGetProperty("pageStart", out var p1) && p1.ValueKind == JsonValueKind.Number ? p1.GetInt32() : 1;
            var pe = src.TryGetProperty("pageEnd", out var p2) && p2.ValueKind == JsonValueKind.Number ? p2.GetInt32() : ps;
            var label = src.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? (lb.GetString() ?? "") : "";

            return new ToolMemory.SourceRef
            {
                DocPath = docPath,
                PageStart = ps,
                PageEnd = pe,
                Label = string.IsNullOrWhiteSpace(label) ? $"{docPath} (p.{ps})" : label
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromRagHits(ToolResults toolResults)
    {
        try
        {
            // Accept both rag.search and rag.multi_search results.
            var candidates = toolResults.Items
                .Where(x => x.ToolName is "rag.search" or "rag.multi_search")
                .Select(x => x.Result)
                .ToList();

            var sources = new List<ToolMemory.SourceRef>();
            foreach (var res in candidates)
            {
                if (res.ValueKind != JsonValueKind.Object) continue;
                if (!res.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) continue;

                foreach (var h in hits.EnumerateArray().Take(8))
                {
                    if (h.ValueKind != JsonValueKind.Object) continue;
                    var docPath = h.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(docPath)) continue;
                    var ps = h.TryGetProperty("pageStart", out var p1) && p1.ValueKind == JsonValueKind.Number ? p1.GetInt32() : 1;
                    var pe = h.TryGetProperty("pageEnd", out var p2) && p2.ValueKind == JsonValueKind.Number ? p2.GetInt32() : ps;

                    var label = h.TryGetProperty("docName", out var dn) && dn.ValueKind == JsonValueKind.String ? (dn.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(label)) label = Path.GetFileName(docPath);

                    sources.Add(new ToolMemory.SourceRef
                    {
                        DocPath = docPath.Replace('\\', '/'),
                        PageStart = ps,
                        PageEnd = pe,
                        Label = $"{label} (p.{ps}{(pe != ps ? $"–{pe}" : "")})"
                    });
                }
            }

            // Dedup by docPath + page
            return sources
                .GroupBy(s => $"{s.DocPath}|{s.PageStart}|{s.PageEnd}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
        catch
        {
            return new List<ToolMemory.SourceRef>();
        }
    }

    private static JsonElement NormalizeRagHits(JsonElement raw)
    {
        try
        {
            if (raw.ValueKind != JsonValueKind.Object)
                return JsonDocument.Parse("{\"hits\":[]}").RootElement;

            // Already normalized
            if (raw.TryGetProperty("hits", out var hits0) && hits0.ValueKind == JsonValueKind.Array)
                return raw;

            // Backend often returns { items: [...] }
            if (!raw.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return JsonDocument.Parse("{\"hits\":[]}").RootElement;

            var list = new List<object>();
            foreach (var it in items.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;

                var docPath = TryGetString(it, "docPath") ?? TryGetString(it, "DocPath") ?? "";
                docPath = (docPath ?? "").Trim().Replace('\\', '/').TrimStart('/');

                var docName = TryGetString(it, "docName") ?? TryGetString(it, "DocName") ?? Path.GetFileName(docPath);
                var score = TryGetDouble(it, "score") ?? 0.0;

                var ps = TryGetInt(it, "pageStart") ?? TryGetInt(it, "page") ?? 1;
                var pe = TryGetInt(it, "pageEnd") ?? ps;

                var text = TryGetString(it, "text") ?? TryGetString(it, "excerpt") ?? "";
                if (text.Length > 320) text = text.Substring(0, 320) + "…";

                list.Add(new
                {
                    docPath,
                    docName,
                    pageStart = ps,
                    pageEnd = pe,
                    excerpt = text,
                    score
                });
            }

            var payload = new { hits = list };
            return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
        }
        catch
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

    private static string? TryGetString(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static int? TryGetInt(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static double? TryGetDouble(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d2)) return d2;
        return null;
    }

    private static string BuildStatsFallbackAnswer(JsonElement result, string language)
    {
        language = NormalizeLanguageCode(language);
        var totalDocuments = TryGetInt(result, "totalDocuments") ?? 0;
        var maxDepth = TryGetInt(result, "maxDepth") ?? 0;
        var totalFolders = TryGetInt(result, "totalNonEmptyFolders") ?? 0;
        var scopePath = NormalizeCategoryPathArg(TryGetString(result, "scopePath"));
        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.StatsTitle(language, scopePath));
        sb.AppendLine(DeterministicAgentText.StatsIndexedDocuments(totalDocuments, language));
        sb.AppendLine(DeterministicAgentText.StatsTotalFolders(totalFolders, language));
        var emptyFolders = TryGetInt(result, "totalEmptyFolders")
                           ?? TryGetInt(result, "emptyFolderCount")
                           ?? TryGetInt(result, "emptyFolders")
                           ?? TryGetInt(result, "emptyCount");
        if (emptyFolders.HasValue)
            sb.AppendLine(DeterministicAgentText.StatsEmptyFolders(emptyFolders.Value, language));
        AppendFolderDepthLines(sb, result, language);
        sb.AppendLine(DeterministicAgentText.StatsMainStructure(language));

        var appendedRootLine = false;
        if (result.TryGetProperty("rootFolders", out var rf) && rf.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in rf.EnumerateArray())
            {
                var name = TryGetString(x, "name") ?? TryGetString(x, "path") ?? string.Empty;
                var total = TryGetInt(x, "totalDocuments") ?? 0;
                var direct = TryGetInt(x, "directDocuments") ?? 0;
                var sub = TryGetInt(x, "subfolderCount") ?? 0;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                sb.AppendLine(DeterministicAgentText.RootFolderLine(name, total, direct, sub, language));
                appendedRootLine = true;
            }
        }

        if (!appendedRootLine)
            sb.AppendLine(DeterministicAgentText.NoSubfoldersInScope(language));

        return sb.ToString().TrimEnd();
    }

    private static void AppendFolderDepthLines(StringBuilder sb, JsonElement result, string language)
    {
        if (!result.TryGetProperty("foldersByDepth", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            var topLevel = TryGetInt(result, "topLevelFolderCount");
            if (topLevel.HasValue)
            {
                sb.AppendLine(DeterministicAgentText.FirstLevelFolders(topLevel.Value, language));
            }
            return;
        }

        var separator = NormalizeLanguageCode(language) == "fr" ? " : " : ": ";
        foreach (var item in arr.EnumerateArray().OrderBy(x => TryGetInt(x, "depth") ?? 0))
        {
            var depth = TryGetInt(item, "depth") ?? 0;
            var count = TryGetInt(item, "folderCount") ?? 0;
            if (depth <= 0)
                continue;

            sb.AppendLine($"- {DeterministicAgentText.FolderDepthLabel(depth, language)}{separator}{count}");
        }
    }


    private static bool? TryGetBoolProp(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private async Task<(bool ok, string answer, object? sourcesPayload)> TryReplayLastInventoryAnswerWithWriterAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        string language,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        _ = chatHistory;
        _ = userMessage;

        if (_mem.LastDeterministicRender is null
            || string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.Kind)
            || string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.DataJson)
            || !IsInventoryIntent(_mem.LastDeterministicRender.RouterIntent ?? _mem.LastRouterIntent))
        {
            return (false, string.Empty, null);
        }

        try
        {
            var answer = TryRenderLastDeterministicAnswer(language);
            if (string.IsNullOrWhiteSpace(answer))
                return (false, string.Empty, null);

            onPhase?.Invoke(DeterministicAgentText.PhaseWriting(language));
            onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(language));
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer.Trim(), null);
        }
        catch
        {
            return (false, string.Empty, null);
        }
    }

    internal static string RenderDeterministicInventoryFromData(string kind, JsonElement data, string language)
    {
        return (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "list" => RenderDocumentsListFromReplayData(data, language),
            "tree" => RenderDocumentsTreeFromReplayData(data, language),
            "stats" => BuildStatsFallbackAnswer(data, language),
            "categories" => RenderCategoriesFromReplayData(data, language),
            "count" => DeterministicAgentText.DocumentsCount(TryGetInt(data, "total") ?? 0, language),
            "empty_count" => DeterministicAgentText.EmptyFoldersCount(TryGetInt(data, "total") ?? 0, language),
            "empty_list" => RenderEmptyFoldersListFromReplayData(data, language),
            "summary_status_count" => RenderSummaryStatusCountFromReplayData(data, language),
            "summary_status_list" => RenderSummaryStatusListFromReplayData(data, language),
            _ => string.Empty
        };
    }

    private static string RenderCategoriesFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return DeterministicAgentText.CategoriesCount(TryGetInt(data, "total") ?? 0, language);

        var rows = new List<string>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var ordinal = TryGetInt(entry, "ordinal") ?? TryGetInt(entry, "displayOrder") ?? 0;
            var name = TryGetString(entry, "name") ?? string.Empty;
            var totalDocuments = TryGetInt(entry, "totalDocuments") ?? 0;
            rows.Add($"{ordinal}. {name} ({totalDocuments})");
        }

        if (rows.Count == 0)
            return DeterministicAgentText.CategoriesCount(TryGetInt(data, "total") ?? 0, language);

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.CategoriesListHeader(language));
        foreach (var row in rows)
            sb.AppendLine(row);

        return sb.ToString().TrimEnd();
    }

    private static string RenderDocumentsTreeFromReplayData(JsonElement data, string language)
    {
        var markdown = TryGetString(data, "markdown") ?? string.Empty;
        return string.IsNullOrWhiteSpace(markdown) ? LocalizedStrings.NoDocumentsFound(language) : markdown.Trim();
    }

    private static string RenderDocumentsListFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return LocalizedStrings.NoDocumentsFound(language);

        var lines = new List<string>();
        var i = 1;
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var docPath = (TryGetString(entry, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var docName = TryGetString(entry, "docName") ?? string.Empty;
            var categoryPath = TryGetString(entry, "categoryPath") ?? string.Empty;
            var mainCat = string.IsNullOrWhiteSpace(categoryPath) ? string.Empty : categoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var label = string.IsNullOrWhiteSpace(mainCat) ? docName : $"{docName} ({mainCat})";
            label = (label ?? string.Empty).Replace("|", " ").Replace("]", ")");

            if (!string.IsNullOrWhiteSpace(docPath) && !string.IsNullOrWhiteSpace(label))
                lines.Add($"{i++}. [[open|{docPath}|1|{label}]]");
        }

        var explicitScopePath = NormalizeCategoryPathArg(TryGetString(data, "scopePath"));
        var searchQuery = (TryGetString(data, "searchQuery") ?? string.Empty).Trim();
        var header = !string.IsNullOrWhiteSpace(explicitScopePath)
            ? DeterministicAgentText.DocumentsListHeader(language, explicitScopePath)
            : (!string.IsNullOrWhiteSpace(searchQuery)
                ? DeterministicAgentText.DocumentsSearchHeader(language, searchQuery)
                : DeterministicAgentText.DocumentsListHeader(language));

        return lines.Count == 0
            ? LocalizedStrings.NoDocumentsFound(language)
            : $"{header}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}".TrimEnd();
    }

    private static string? TryInferDocumentsScopePath(JsonElement data)
    {
        var explicitScope = NormalizeCategoryPathArg(TryGetString(data, "scopePath"));
        if (!string.IsNullOrWhiteSpace(explicitScope))
            return explicitScope;

        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        string? unique = null;
        foreach (var entry in items.EnumerateArray())
        {
            var categoryPath = NormalizeCategoryPathArg(TryGetString(entry, "categoryPath"));
            if (string.IsNullOrWhiteSpace(categoryPath))
                return null;
            if (unique is null)
            {
                unique = categoryPath;
                continue;
            }

            if (!string.Equals(unique, categoryPath, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return unique;
    }

    private static string RenderEmptyFoldersListFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return DeterministicAgentText.NoEmptyFoldersFound(language);

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

    private static string RenderSummaryStatusCountFromReplayData(JsonElement data, string language)
    {
        var total = TryGetInt(data, "total") ?? 0;
        var mode = TryGetString(data, "mode") ?? "missing";
        if (string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase))
        {
            return total <= 0
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.StoredSummariesCount(total, language);
        }

        return total <= 0
            ? DeterministicAgentText.NoMissingSummaries(language)
            : DeterministicAgentText.MissingSummariesCount(total, language);
    }

    private static string RenderSummaryStatusListFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return string.Equals(TryGetString(data, "mode"), "present", StringComparison.OrdinalIgnoreCase)
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.NoMissingSummaries(language);

        var rows = new List<(string path, string state, string label)>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = (TryGetString(entry, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var state = TryGetString(entry, "summaryState") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var label = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
            label = (label ?? string.Empty).Replace("|", " ").Replace("]", ")");
            rows.Add((path, state, label));
        }

        var mode = TryGetString(data, "mode") ?? "missing";
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
                ? " [stale]"
                : string.Empty;
            sb.AppendLine($"{i + 1}. [[open|{rows[i].path}|1|{rows[i].label}]]{suffix}");
        }

        return sb.ToString().TrimEnd();
    }

    // ---------------- Tools exec helpers ----------------

    private async Task<JsonElement> ExecDocumentsListAsync(JsonElement args, CancellationToken ct)
    {
        var (categoryPath, categoryRef) = ResolveCategoryScopeArgs(args);
        var q = args.TryGetProperty("q", out var qj) && qj.ValueKind != JsonValueKind.Null ? qj.GetString() : null;
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsListAsync(categoryPath, categoryRef, q, limit, offset, ct);

        _mem.LastListCategoryPath = categoryPath;
        _mem.LastListQuery = q;

        // Sanitize against filesystem + update PDFxx mapping (robust against moves/renames)
        DocumentListHelper.Sanitize(res, _mem);

        return res;
    }

    private async Task<JsonElement> ExecDocumentsSearchAsync(JsonElement args, CancellationToken ct)
    {
        var q = args.GetProperty("q").GetString() ?? "";
        var (categoryPath, categoryRef) = ResolveCategoryScopeArgs(args);
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsSearchAsync(q, categoryPath, categoryRef, limit, offset, ct);

        _mem.LastListCategoryPath = categoryPath;
        _mem.LastListQuery = q;

        DocumentListHelper.Sanitize(res, _mem);

        return res;
    }

    private async Task<JsonElement> ExecDocumentsGetAsync(JsonElement args, CancellationToken ct)
    {
        return await ExecDocumentsGetResolvedAsync(args, ct);
    }

    private async Task<JsonElement> ExecRagSearchAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.GetProperty("query").GetString() ?? "";
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var categoryPath = GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category");
        var category = ExtractTopLevelCategoryForRag(categoryPath);
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";

        var raw = await _api.RagSearchToolAsync(query, topK, category, mode, ct);
        return NormalizeRagHits(raw);
    }

    private async Task<JsonElement> ExecRagMultiSearchAsync(JsonElement args, CancellationToken ct)
    {
        // args: { queries: string[], topK: int, category: string|null, mode: ... }
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var categoryPath = GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category");
        var category = ExtractTopLevelCategoryForRag(categoryPath);
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";

        var queries = new List<string>();
        if (args.TryGetProperty("queries", out var qArr) && qArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qArr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String) continue;
                var s = (q.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(s)) queries.Add(s);
            }
        }

        // fallback: single query
        if (queries.Count == 0 && args.TryGetProperty("query", out var q1) && q1.ValueKind == JsonValueKind.String)
        {
            var s = (q1.GetString() ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(s)) queries.Add(s);
        }

        if (queries.Count == 0)
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;

        var merged = new List<JsonElement>();
        foreach (var q in queries.Take(5))
        {
            var raw = await _api.RagSearchToolAsync(q, topK, category, mode, ct);
            var norm = NormalizeRagHits(raw);
            if (norm.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hits.EnumerateArray())
                    merged.Add(h);
            }
        }

        // Dedup by docPath + pageStart + pageEnd (diversity-friendly)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniq = new List<JsonElement>();
        foreach (var h in merged)
        {
            var dp = h.TryGetProperty("docPath", out var dpEl) ? (dpEl.GetString() ?? "") : "";
            var p1 = h.TryGetProperty("pageStart", out var p1El) && p1El.ValueKind == JsonValueKind.Number ? p1El.GetInt32() : 1;
            var p2 = h.TryGetProperty("pageEnd", out var p2El) && p2El.ValueKind == JsonValueKind.Number ? p2El.GetInt32() : p1;
            var key = $"{dp}|{p1}|{p2}";
            if (!seen.Add(key)) continue;
            uniq.Add(h);
        }

        // Sort by score desc when present
        uniq = uniq
            .OrderByDescending(h => h.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0.0)
            .Take(Math.Max(10, topK * 2))
            .ToList();

        var payload = new
        {
            hits = uniq,
            meta = new { queries = queries.Take(5).ToArray(), mode = (mode ?? "balanced"), category }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }
private JsonElement ExecExportCreate(JsonElement args)
    {
        var format = args.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? (f.GetString() ?? "txt") : "txt";
        var fileName = args.TryGetProperty("fileName", out var n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "export") : args.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "export") : "export";
        var content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "") : "";

        var path = ExportService.Create(format, fileName, content);
        var payload = new { savedPath = path };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecSupportBundleAsync(JsonElement args, CancellationToken ct)
    {
        if (_settings is null)
            return JsonDocument.Parse("{\"error\":\"missing_settings\"}").RootElement;

        var include = GetStringArrayArg(args, "include");
        var runtimeSnapshot = BuildAgentRuntimeSnapshot();
        var zip = await SupportBundleBuilder.BuildAsync(_settings, runtimeSnapshot, include).ConfigureAwait(false);
        var payload = new
        {
            zipPath = zip,
            included = include is { Count: > 0 } ? include.ToArray() : new[] { "diagnostics/agent-runtime" }
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecRagDebugScrollAsync(JsonElement args, CancellationToken ct)
    {
        var cursor = args.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var limit = args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 100;
        string? docPath = null;
        if (args.TryGetProperty("docRef", out var dref) && dref.ValueKind == JsonValueKind.String)
        {
            var resolved = await ResolveDocRefAsync(dref.GetString() ?? string.Empty, ct).ConfigureAwait(false);
            docPath = resolved?.DocPath;
        }
        else if (args.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String)
        {
            docPath = dp.GetString();
        }

        try
        {
            var raw = await _api.RagDebugScrollAsync(cursor, limit, docPath, ct).ConfigureAwait(false);
            return raw;
        }
        catch
        {
            return JsonDocument.Parse("{\"items\":[],\"nextCursor\":null,\"error\":\"not_supported\"}").RootElement;
        }
    }
    // ---------------- Utility ----------------

    private static ToolResults BuildWriterToolResults(RouterPlan plan, ToolResults toolResults)
    {
        var inventoryRendered = toolResults.Items.LastOrDefault(x => x.ToolName == "inventory.rendered" && string.IsNullOrWhiteSpace(x.Error));
        if (inventoryRendered is null)
            return toolResults;

        var inventoryIntent = IsInventoryIntent(plan.Intent);
        var inventoryOnly = HasOnlyInventoryTools(toolResults);

        if (!inventoryIntent && !inventoryOnly)
            return toolResults;

        var filtered = new ToolResults();
        filtered.Items.Add(new ToolResults.Item
        {
            ToolName = inventoryRendered.ToolName,
            Result = inventoryRendered.Result,
            Error = inventoryRendered.Error,
            DurationMs = inventoryRendered.DurationMs
        });

        var diagnostic = toolResults.Items.LastOrDefault(x => x.ToolName == "diagnostic.performance" && string.IsNullOrWhiteSpace(x.Error));
        if (diagnostic is not null)
        {
            filtered.Items.Add(new ToolResults.Item
            {
                ToolName = diagnostic.ToolName,
                Result = diagnostic.Result,
                Error = diagnostic.Error,
                DurationMs = diagnostic.DurationMs
            });
        }

        return filtered;
    }

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
            or "summary.status.count"
            or "summary.status.list"
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

    private static string SerializeToolResults(ToolResults tr)
    {
        var o = tr.Items.Select(x => new
        {
            tool = x.ToolName,
            error = x.Error,
            durationMs = x.DurationMs,
            result = x.Result
        }).ToList();

        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string SerializeTail(IReadOnlyList<(string role, string content)> hist, int maxTurns)
    {
        var tail = hist.TakeLast(maxTurns).Select(m => new { role = m.role, content = m.content }).ToList();
        return JsonSerializer.Serialize(tail);
    }

    private static bool TryExtractJsonObject(string raw, out string json)
    {
        json = (raw ?? "").Trim();

        // enlever ```json ... ``` si présent
        if (json.StartsWith("```"))
        {
            var i = json.IndexOf('\n');
            if (i >= 0) json = json.Substring(i + 1);
            json = json.Replace("```", "").Trim();
        }

        // trouver premier '{' et dernier '}'
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start) return false;

        json = json.Substring(start, end - start + 1);
        return true;
    }

    private static bool TryExtractFinalAnswerFromRaw(string raw, out string finalAnswer)
    {
        finalAnswer = "";
        raw ??= "";

        // 1) Try to parse a JSON substring first
        if (TryExtractJsonObject(raw, out var json) && TryParseAnswerEnvelope(json, out var fa, out _))
        {
            finalAnswer = (fa ?? "").Trim();
            return !string.IsNullOrWhiteSpace(finalAnswer);
        }

        // 2) Regex fallback: "finalAnswer":"..."
        var m = Regex.Match(raw, "\\\"finalAnswer\\\"\\s*:\\s*\\\"(?<v>(?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.Singleline);
        if (m.Success)
        {
            var v = m.Groups["v"].Value;
            try
            {
                // Use JSON parser to unescape
                finalAnswer = JsonSerializer.Deserialize<string>("\"" + v + "\"") ?? "";
                finalAnswer = finalAnswer.Trim();
                return !string.IsNullOrWhiteSpace(finalAnswer);
            }
            catch
            {
                // best-effort
                finalAnswer = v.Replace("\\n", "\n").Replace("\\t", "\t").Trim();
                return !string.IsNullOrWhiteSpace(finalAnswer);
            }
        }

        return false;
    }

    private static string BuildJsonEnvelopeError(string? lang)
    {
        return DeterministicAgentText.JsonEnvelopeError(lang);
    }

    private static string PhaseLabelForTool(string toolName, string language)
        => DeterministicAgentText.ToolPhase(toolName, language);
}
