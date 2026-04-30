using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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
    private const int RouterCanonicalHintsLimit = 6;
    private const int SerializedTailContentMaxChars = 420;
    private const int RagWriterMaxHits = 8;
    private const int RagWriterMaxExcerptChars = 240;
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
    private string _lastAnswerSource = "unknown";

    // limite "sécurité perf" (spec : max 5 RAG/calls par requête)
    private const int MaxToolCalls = 8;
    private const int MaxRagToolCalls = 5;

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
            if (LocalizedStrings.TryDetectStylePreferenceChange(userMessage, out var requestedStyle))
            {
                _mem.LastStyle = LocalizedStrings.NormalizeStyle(requestedStyle);
                UserPrefsStore.SaveStyle(_mem.LastStyle);
            }

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

        var pendingClarification = PrepareUserMessageForPendingClarification(chatHistory, userMessage);
        var effectiveUserMessage = pendingClarification.EffectiveUserMessage;
        var interactionLanguage = ResolveInteractionLanguage(effectiveUserMessage);
        if (LocalizedStrings.TryDetectStylePreferenceChange(effectiveUserMessage, out var explicitStyle))
        {
            _mem.LastStyle = LocalizedStrings.NormalizeStyle(explicitStyle);
            UserPrefsStore.SaveStyle(_mem.LastStyle);

            var ack = LocalizedStrings.StyleChanged(_mem.LastStyle, interactionLanguage);
            await EmitDeterministicTextAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return FinalizeAndReturn(swTotalPipeline, userMessage, ack, null, "meta.set_style", Array.Empty<string>(), Array.Empty<string>());
        }

        if (LocalizedStrings.TryDetectModePreferenceChange(effectiveUserMessage, out var explicitMode))
        {
            var normalizedMode = AppSettings.NormalizeActiveMode(explicitMode);
            _mem.LastMode = normalizedMode;
            if (_settings is not null)
                _settings.ActiveMode = normalizedMode;

            var ack = LocalizedStrings.ModeChanged(normalizedMode, interactionLanguage);
            await EmitDeterministicTextAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return FinalizeAndReturn(swTotalPipeline, userMessage, ack, null, "meta.set_mode", Array.Empty<string>(), Array.Empty<string>());
        }

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
            _lastAnswerSource = $"shortcut:{shortcut.routerIntent}";
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
        if (string.Equals(AppSettings.NormalizeActiveMode(_settings?.ActiveMode), "strict", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(plan.Mode, "strict", StringComparison.OrdinalIgnoreCase))
            plan.Mode = "strict";
        _mem.LastLanguage = plan.Language;
        _mem.LastMode = _settings is null ? plan.Mode : AppSettings.NormalizeActiveMode(_settings.ActiveMode);
        _mem.LastRouterIntent = plan.Intent;
        _mem.LastReasoningTracePublic = plan.ReasoningTracePublic?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
        _mem.LastRiskFlags = plan.RiskFlags?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        _mem.LastPlannerMemoryUpdate = string.IsNullOrWhiteSpace(plan.MemoryUpdate) ? null : plan.MemoryUpdate.Trim();
        _mem.LastRouterConfidence = plan.RouterConfidence;
        _lastResponseFormat = string.IsNullOrWhiteSpace(plan.ResponseFormat) ? "auto" : plan.ResponseFormat.Trim().ToLowerInvariant();
        _lastEffectiveMode = string.IsNullOrWhiteSpace(plan.Mode) ? "auto" : plan.Mode.Trim().ToLowerInvariant();

        // Clear stale deterministic render when the current turn is NOT inventory.
        // Prevents old inventory data from leaking into non-inventory turns.
        if (!IsInventoryIntent(plan.Intent))
            _mem.LastDeterministicRender = null;

        if (string.Equals(plan.Intent, "meta.set_style", StringComparison.OrdinalIgnoreCase)
            && LocalizedStrings.TryDetectStylePreferenceChange(effectiveUserMessage, out var routedStyle))
        {
            _mem.LastStyle = LocalizedStrings.NormalizeStyle(routedStyle);
            UserPrefsStore.SaveStyle(_mem.LastStyle);

            var ack = LocalizedStrings.StyleChanged(_mem.LastStyle, plan.Language);
            await EmitDeterministicTextAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return FinalizeAndReturn(swTotalPipeline, userMessage, ack, null, "meta.set_style", Array.Empty<string>(), _mem.LastReasoningTracePublic);
        }

        if (string.Equals(plan.Intent, "meta.set_mode", StringComparison.OrdinalIgnoreCase)
            && LocalizedStrings.TryDetectModePreferenceChange(effectiveUserMessage, out var routedMode))
        {
            var normalizedMode = AppSettings.NormalizeActiveMode(routedMode);
            _mem.LastMode = normalizedMode;
            if (_settings is not null)
                _settings.ActiveMode = normalizedMode;

            var ack = LocalizedStrings.ModeChanged(normalizedMode, plan.Language);
            await EmitDeterministicTextAsync(ack, onDelta, ct).ConfigureAwait(false);
            onProgress?.Invoke(string.Empty);
            return FinalizeAndReturn(swTotalPipeline, userMessage, ack, null, "meta.set_mode", Array.Empty<string>(), _mem.LastReasoningTracePublic);
        }

        var routerTrace = _mem.LastReasoningTracePublic.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(routerTrace))
            onProgress?.Invoke(routerTrace);

        var standaloneTopicRag = await TryHandleStandaloneTopicRagAsync(
            chatHistory,
            userMessage,
            effectiveUserMessage,
            plan,
            ct,
            onPhase,
            onDelta,
            onProgress,
            swTotalPipeline).ConfigureAwait(false);
        if (standaloneTopicRag.handled)
            return (standaloneTopicRag.finalAnswer, standaloneTopicRag.sourcesPayload);

        if (repairMessage && string.Equals(plan.Intent, "meta.rewrite_last", StringComparison.OrdinalIgnoreCase) && !plan.NeedClarification && plan.ToolCalls.Count == 0)
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
            _lastAnswerSource = $"documentary_probe:{documentaryProbe.routerIntent}";
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

        _lastAnswerSource = _lastUsedInventoryRendered
            ? $"router+tools+inventory_bypass:{plan.Intent}"
            : $"router+tools+writer:{plan.Intent}";
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

            // optionnel : petite note si certains fichiers n'ont pas pu être résolus
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
        else if (toolResults.Items.Any(x => x.ToolName == "diagnostic.performance" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "diagnostic_performance";
            data = BuildDiagnosticPerformanceInventoryData(toolResults);
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

    private static object? BuildDiagnosticPerformanceInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "diagnostic.performance" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var summary = item.Result.TryGetProperty("memorySummary", out var memorySummary) && memorySummary.ValueKind == JsonValueKind.Object
                ? memorySummary
                : item.Result;

            var workspace = summary.TryGetProperty("workspace", out var workspaceEl) && workspaceEl.ValueKind == JsonValueKind.Object
                ? workspaceEl
                : default;
            var session = summary.TryGetProperty("session", out var sessionEl) && sessionEl.ValueKind == JsonValueKind.Object
                ? sessionEl
                : default;
            var execution = summary.TryGetProperty("execution", out var executionEl) && executionEl.ValueKind == JsonValueKind.Object
                ? executionEl
                : default;
            var persistence = summary.TryGetProperty("persistence", out var persistenceEl) && persistenceEl.ValueKind == JsonValueKind.Object
                ? persistenceEl
                : default;
            var resetPolicy = summary.TryGetProperty("resetPolicy", out var resetEl) && resetEl.ValueKind == JsonValueKind.Object
                ? resetEl
                : default;

            return new
            {
                profile = TryGetString(summary, "profile") ?? "unknown",
                schemaVersion = TryGetInt(summary, "schemaVersion") ?? 0,
                cdcAlignment = TryGetString(summary, "cdcAlignment") ?? "v3.1",
                routerMs = TryGetInt(item.Result, "routerMs") ?? 0,
                toolsMs = TryGetInt(item.Result, "toolsMs") ?? 0,
                writerMs = TryGetInt(item.Result, "writerMs") ?? 0,
                totalMs = TryGetInt(item.Result, "totalMs") ?? 0,
                workspace = new
                {
                    catalogCategoriesCount = TryGetInt(workspace, "catalogCategoriesCount") ?? 0,
                    knownDocumentsCount = TryGetInt(workspace, "knownDocumentsCount") ?? 0,
                    hasCapabilitiesSnapshot = TryGetBoolProp(workspace, "hasCapabilitiesSnapshot") ?? false
                },
                session = new
                {
                    hasFocusedDocument = TryGetBoolProp(session, "hasFocusedDocument") ?? false,
                    lastListedDocumentsCount = TryGetInt(session, "lastListedDocumentsCount") ?? 0,
                    hasResolvedCategory = TryGetBoolProp(session, "hasResolvedCategory") ?? false,
                    hasPendingClarification = TryGetBoolProp(session, "hasPendingClarification") ?? false
                },
                execution = new
                {
                    mode = TryGetString(execution, "mode") ?? "auto",
                    hasRouterIntent = TryGetBoolProp(execution, "hasRouterIntent") ?? false,
                    toolNamesCount = TryGetInt(execution, "toolNamesCount") ?? 0,
                    hasAdminOperation = TryGetBoolProp(execution, "hasAdminOperation") ?? false
                },
                persistence = new
                {
                    language = TryGetBoolProp(persistence, "language") ?? false,
                    style = TryGetBoolProp(persistence, "style") ?? false,
                    mode = TryGetBoolProp(persistence, "mode") ?? false,
                    focusedDocument = TryGetBoolProp(persistence, "focusedDocument") ?? false,
                    resolvedCategory = TryGetBoolProp(persistence, "resolvedCategory") ?? false
                },
                resetPolicy = new
                {
                    preservesM1Lite = TryGetBoolProp(resetPolicy, "preservesM1Lite") ?? false,
                    preservesPreferences = TryGetBoolProp(resetPolicy, "preservesPreferences") ?? false,
                    clearsM3 = TryGetBoolProp(resetPolicy, "clearsM3") ?? false,
                    clearsM6 = TryGetBoolProp(resetPolicy, "clearsM6") ?? false,
                    resetsModeToAuto = TryGetBoolProp(resetPolicy, "resetsModeToAuto") ?? false
                }
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
        var label = SanitizeOpenTokenLabel((src.Label ?? string.Empty).Trim());
        payload = new { sources = new[] { new { docPath = dp, pageStart = src.PageStart, pageEnd = src.PageEnd, label } } };

        var heading = DeterministicAgentText.SourceHeading(language);
        return $"{heading}:\n1. [[open|{dp}|{Math.Max(1, src.PageStart)}|{label}]]";
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
            label = SanitizeOpenTokenLabel(label);

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

        if (LooksLikeCuisineActionRequest(s))
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

    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleStandaloneTopicRagAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string displayUserMessage,
        string effectiveUserMessage,
        RouterPlan plan,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress,
        Stopwatch swTotalPipeline)
    {
        if (!ShouldForceRagForStandaloneTopic(effectiveUserMessage, plan))
            return (false, string.Empty, null);

        var retrievalQuery = NormalizeRagQueryForRetrieval(effectiveUserMessage);
        if (string.IsNullOrWhiteSpace(retrievalQuery))
            return (false, string.Empty, null);

        try
        {
            onPhase?.Invoke(DeterministicAgentText.PhaseRag(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressCollectInformation(plan.Language));

            if (LooksLikeCuisineMealPlanningRequest(effectiveUserMessage))
            {
                var multiArgs = CreateJsonArgs(new
                {
                    queries = BuildCuisineMealPlanningQueries(effectiveUserMessage),
                    topK = 4,
                    category = "Cuisine",
                    mode = "balanced"
                });
                var multiResult = await ExecRagMultiSearchAsync(multiArgs, ct).ConfigureAwait(false);
                if (HasRagHits(multiResult))
                {
                    var mealToolResults = new ToolResults();
                    mealToolResults.Items.Add(new ToolResults.Item
                    {
                        ToolName = "rag.multi_search",
                        Result = multiResult
                    });

                    var mealAnswer = BuildCuisineMealPlanningAnswer(mealToolResults, plan.Language);
                    if (!string.IsNullOrWhiteSpace(mealAnswer))
                    {
                        var mealSources = DeriveSourcesFromRagHits(mealToolResults);
                        object? mealSourcesPayload = null;
                        if (mealSources.Count > 0)
                        {
                            _mem.LastSourcesUsed = mealSources;
                            mealAnswer = InjectInlineSources(mealAnswer, mealSources, plan.Language);
                            mealSourcesPayload = BuildSourcesPayload(mealSources);
                        }

                        _lastAnswerSource = "standalone_topic_rag:cuisine_meal_planning";
                        _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { ("rag.multi_search", 0, true) };
                        _lastToolsMs = 0;
                        _lastWriterMs = 0;
                        _mem.LastToolNames = new List<string> { "rag.multi_search" };
                        onProgress?.Invoke(string.Empty);

                        var finalizedMealPlan = FinalizeAndReturn(swTotalPipeline, displayUserMessage, mealAnswer, mealSourcesPayload, "rag.answer", _mem.LastToolNames, _mem.LastReasoningTracePublic);
                        return (true, finalizedMealPlan.finalAnswer, mealSourcesPayload);
                    }
                }
            }

            var args = CreateJsonArgs(new
            {
                query = retrievalQuery,
                topK = 8,
                mode = "balanced"
            });
            var ragResult = await ExecRagSearchAsync(args, ct).ConfigureAwait(false);
            if (!HasRagHits(ragResult))
                return (false, string.Empty, null);

            var ragPlan = new RouterPlan
            {
                Intent = "rag.answer",
                Language = plan.Language,
                Mode = plan.Mode,
                ResponseFormat = "auto",
                ToolCalls = new List<RouterPlan.ToolCall>
                {
                    new()
                    {
                        Name = "rag.search",
                        Args = args
                    }
                },
                ReasoningTracePublic = plan.ReasoningTracePublic ?? new List<string>(),
                RiskFlags = plan.RiskFlags ?? new List<string>(),
                RouterConfidence = plan.RouterConfidence
            };

            var toolResults = new ToolResults();
            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.search",
                Result = ragResult
            });

            onPhase?.Invoke(DeterministicAgentText.PhaseWriting(plan.Language));
            onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(plan.Language));

            var (answer, sources) = await AnswerAsync(chatHistory, effectiveUserMessage, ragPlan, toolResults, ct, onDelta, onProgress).ConfigureAwait(false);
            answer = (answer ?? string.Empty).Replace("**", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(answer) || LooksLikeNoRagDataAnswer(answer))
                answer = BuildRagEvidenceFallbackAnswer(toolResults, retrievalQuery, plan.Language);

            object? sourcesPayload = null;
            if (sources is { Count: > 0 })
            {
                _mem.LastSourcesUsed = sources;
                answer = InjectInlineSources(answer, sources, plan.Language);
                sourcesPayload = BuildSourcesPayload(sources);
            }

            _lastAnswerSource = $"standalone_topic_rag:{ragPlan.Intent}";
            _lastToolDurations = new List<(string tool, long durationMs, bool ok)> { ("rag.search", 0, true) };
            _lastToolsMs = 0;
            _lastWriterMs = 0;
            _mem.LastToolNames = new List<string> { "rag.search" };
            onProgress?.Invoke(string.Empty);

            var finalized = FinalizeAndReturn(swTotalPipeline, displayUserMessage, answer, sourcesPayload, ragPlan.Intent, _mem.LastToolNames, _mem.LastReasoningTracePublic);
            return (true, finalized.finalAnswer, sourcesPayload);
        }
        catch
        {
            return (false, string.Empty, null);
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
        var manifestJson = ToolManifest.BuildConversationManifestJson();
        var toolbook = ToolManifest.ConversationToolbookText;
        var repairHint = DocumentRefResolver.IsRepairMessage(userMessage);
        var resolverHint = DocumentRefResolver.Analyze(userMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);

        var memoryCtx = BuildRouterMemoryContext(resolverHint);

        var detectedMessageLanguage = ResolveInteractionLanguage(userMessage);
        var system = PromptCatalog.BuildRouterSystemPrompt(manifestJson, toolbook) + $@"

Additional runtime rules:
- Last answer language (informational only): {_mem.LastLanguage}
- Current message language hint: {detectedMessageLanguage}
- Disallow meta.set_language for this turn: {(disallowMetaSetLanguage ? "true" : "false")}
- The current message looks like a repair/correction turn: {(repairHint ? "true" : "false")}
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

            using var legacyDoc = JsonDocument.Parse(planJson);
            if (!plan.RouterConfidence.HasValue
                && legacyDoc.RootElement.TryGetProperty("confidence", out var confidenceEl)
                && confidenceEl.ValueKind == JsonValueKind.Number
                && confidenceEl.TryGetDouble(out var legacyConfidence))
            {
                plan.RouterConfidence = legacyConfidence;
            }

            return SanitizeRouterPlan(plan, detectedMessageLanguage, disallowMetaSetLanguage);
        }
        catch
        {
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general" };
        }
    }

    private Dictionary<string, object?> BuildRouterMemoryContext(DocumentRefResolver.AnalysisResult resolverHint)
    {
        return new Dictionary<string, object?>
        {
            ["profile"] = _mem.MemoryProfile,
            ["lastLanguage"] = _mem.LastLanguage,
            ["lastUserDetectedLanguage"] = _mem.LastUserDetectedLanguage,
            ["lastAnswerLanguage"] = _mem.LastAnswerLanguage,
            ["lastList"] = new Dictionary<string, object?>
            {
                ["offset"] = _mem.LastListOffset,
                ["limit"] = _mem.LastListLimit,
                ["categoryPath"] = _mem.LastListCategoryPath,
                ["q"] = _mem.LastListQuery,
                ["total"] = _mem.LastListTotal
            },
            ["lastFocusedDocument"] = _mem.LastFocusedDocument is null ? null : new Dictionary<string, object?>
            {
                ["docId"] = _mem.LastFocusedDocument.DocId,
                ["docPath"] = _mem.LastFocusedDocument.DocPath,
                ["docName"] = _mem.LastFocusedDocument.DocName,
                ["category"] = _mem.LastFocusedDocument.Category,
                ["categoryPath"] = _mem.LastFocusedDocument.CategoryPath,
                ["pdfRef"] = _mem.LastFocusedDocument.PdfRef
            },
            ["lastTurn"] = new Dictionary<string, object?>
            {
                ["user"] = _mem.LastUserMessage,
                ["assistant"] = _mem.LastAssistantAnswer,
                ["routerIntent"] = _mem.LastRouterIntent,
                ["toolNames"] = _mem.LastToolNames,
                ["reasoningTracePublic"] = _mem.LastReasoningTracePublic,
                ["routerConfidence"] = _mem.LastRouterConfidence,
                ["riskFlags"] = _mem.LastRiskFlags,
                ["mode"] = _mem.LastMode,
                ["memoryUpdate"] = _mem.LastPlannerMemoryUpdate
            },
            ["pendingClarification"] = _mem.PendingClarification is null ? null : new Dictionary<string, object?>
            {
                ["kind"] = _mem.PendingClarification.Kind,
                ["originalUserMessage"] = _mem.PendingClarification.OriginalUserMessage,
                ["hint"] = _mem.PendingClarification.Hint,
                ["language"] = _mem.PendingClarification.Language,
                ["createdAtUtc"] = _mem.PendingClarification.CreatedAtUtc
            },
            ["adminSession"] = new Dictionary<string, object?>
            {
                ["hasAdminKey"] = _api.HasAdminKey
            },
            ["lastResolvedCategory"] = _mem.LastResolvedCategory is null ? null : new Dictionary<string, object?>
            {
                ["categoryRef"] = _mem.LastResolvedCategory.CategoryRef,
                ["categoryPath"] = _mem.LastResolvedCategory.CategoryPath,
                ["displayName"] = _mem.LastResolvedCategory.DisplayName,
                ["ordinal"] = _mem.LastResolvedCategory.Ordinal,
                ["totalDocuments"] = _mem.LastResolvedCategory.TotalDocuments,
                ["aliases"] = _mem.LastResolvedCategory.Aliases
            },
            ["lastPresentedCategories"] = _mem.LastPresentedCategories?.Select(x => new Dictionary<string, object?>
            {
                ["categoryRef"] = x.CategoryRef,
                ["categoryPath"] = x.CategoryPath,
                ["displayName"] = x.DisplayName,
                ["ordinal"] = x.Ordinal,
                ["totalDocuments"] = x.TotalDocuments,
                ["aliases"] = x.Aliases
            }).ToList(),
            ["m1Lite"] = new Dictionary<string, object?>
            {
                ["canonicalCategories"] = BuildRouterCanonicalCategoryHints(),
                ["canonicalDocuments"] = BuildRouterCanonicalDocumentHints()
            },
            ["lastSummaryStatus"] = _mem.LastSummaryStatusSnapshot is null ? null : new Dictionary<string, object?>
            {
                ["categoryPath"] = _mem.LastSummaryStatusSnapshot.CategoryPath,
                ["categoryRef"] = _mem.LastSummaryStatusSnapshot.CategoryRef,
                ["mode"] = _mem.LastSummaryStatusSnapshot.Mode,
                ["total"] = _mem.LastSummaryStatusSnapshot.Total,
                ["missingStored"] = _mem.LastSummaryStatusSnapshot.MissingStored,
                ["staleStored"] = _mem.LastSummaryStatusSnapshot.StaleStored,
                ["itemsCount"] = _mem.LastSummaryStatusSnapshot.Items?.Count ?? 0
            },
            ["resolverHint"] = new Dictionary<string, object?>
            {
                ["isContentRequest"] = resolverHint.IsContentRequest,
                ["wantsAbout"] = resolverHint.WantsAbout,
                ["wantsSummary"] = resolverHint.WantsSummary,
                ["wantsStoredSummaryCheck"] = resolverHint.WantsStoredSummaryCheck,
                ["wantsStoredSummaryStore"] = resolverHint.WantsStoredSummaryStore,
                ["resolvedDocRef"] = resolverHint.ResolvedDocRef,
                ["needsClarification"] = resolverHint.NeedsClarification,
                ["clarificationKind"] = resolverHint.ClarificationKind
            }
        };
    }

    private List<Dictionary<string, object?>> BuildRouterCanonicalCategoryHints()
        => (_mem.CatalogSnapshotCache?.Categories ?? new List<ToolMemory.CategorySnapshot>())
            .Where(x => !string.IsNullOrWhiteSpace(x.DisplayName) || !string.IsNullOrWhiteSpace(x.CategoryPath))
            .OrderBy(x => x.Ordinal == 0 ? int.MaxValue : x.Ordinal)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(RouterCanonicalHintsLimit)
            .Select(x => new Dictionary<string, object?>
            {
                ["categoryRef"] = x.CategoryRef,
                ["categoryPath"] = x.CategoryPath,
                ["displayName"] = x.DisplayName,
                ["ordinal"] = x.Ordinal,
                ["aliases"] = x.Aliases?.Take(4).ToList()
            })
            .ToList();

    private List<Dictionary<string, object?>> BuildRouterCanonicalDocumentHints()
        => (_mem.WorkspaceKnownDocuments ?? new List<ToolMemory.DocumentItem>())
            .Where(x => !string.IsNullOrWhiteSpace(x.DocName) || !string.IsNullOrWhiteSpace(x.DocPath))
            .OrderBy(x => x.DocName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DocPath, StringComparer.OrdinalIgnoreCase)
            .Take(RouterCanonicalHintsLimit)
            .Select(x => new Dictionary<string, object?>
            {
                ["docId"] = x.DocId,
                ["docPath"] = x.DocPath,
                ["docName"] = x.DocName,
                ["category"] = x.Category,
                ["categoryPath"] = x.CategoryPath
            })
            .ToList();

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
                var structuredError = ClassifyToolExecutionError(ex, ToolManifest.IsAdminTool(call.Name));
                var effectiveError = string.IsNullOrWhiteSpace(structuredError) ? ex.Message : structuredError;
                var resultError = string.IsNullOrWhiteSpace(structuredError) ? "tool_failed" : structuredError;
                results.Items.Add(new ToolResults.Item
                {
                    ToolName = call.Name,
                    Error = effectiveError,
                    DurationMs = sw.ElapsedMilliseconds,
                    Result = JsonDocument.Parse($"{{\"error\":\"{resultError}\"}}").RootElement
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
            : PromptCatalog.BuildWriterSystemPrompt(
                plan.Language,
                plan.Mode,
                LocalizedStrings.NormalizeStyle(_mem.LastStyle),
                allowGeneralChat: plan.ToolCalls.Count == 0 || string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase));

        var writerToolResults = BuildWriterToolResults(plan, toolResults);
        _lastWriterToolNames = writerToolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _lastUsedInventoryRendered = _lastUsedInventoryRendered || _lastWriterToolNames.Any(x => string.Equals(x, "inventory.rendered", StringComparison.OrdinalIgnoreCase));
        var inventoryRenderedText = TryRenderInventoryFallbackText(writerToolResults, plan.Language);
        var inventoryRenderedDataJson = TryExtractInventoryRenderedDataJson(writerToolResults);

        if (ShouldBypassWriterForDeterministicInventory(plan, writerToolResults, inventoryRenderedText))
        {
            var deterministicAnswer = (inventoryRenderedText ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(deterministicAnswer))
            {
                _lastAnswerSource = $"writer_bypass_deterministic_inventory:{plan.Intent}";
                return (deterministicAnswer, null);
            }
        }

        if (ShouldUseCuisineExtractiveAnswer(userMessage, writerToolResults) || LooksLikeCuisineActionRequest(userMessage))
        {
            var deterministicAnswer = BuildCuisineExtractiveAnswer(writerToolResults, userMessage, plan.Language);
            var deterministicSources = DeriveSourcesFromRagHits(writerToolResults);
            if (!string.IsNullOrWhiteSpace(deterministicAnswer))
            {
                _lastAnswerSource = $"writer_bypass_cuisine_extractive:{plan.Intent}";
                return (deterministicAnswer, deterministicSources);
            }
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
            if (ShouldUseCuisineExtractiveAnswer(userMessage, toolResults) || LooksLikeCuisineActionRequest(userMessage))
                finalAnswer = BuildCuisineExtractiveAnswer(toolResults, userMessage, plan.Language);
            else if (sources.Count > 0 && LooksLikeNoRagDataAnswer(finalAnswer))
                finalAnswer = BuildRagEvidenceFallbackAnswer(toolResults, userMessage, plan.Language);
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

        var strict = string.Equals(plan.Mode, "strict", StringComparison.OrdinalIgnoreCase)
            || string.Equals(AppSettings.NormalizeActiveMode(_settings?.ActiveMode), "strict", StringComparison.OrdinalIgnoreCase);
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
            DocumentSummaryRequestKind.SummaryCheckOnly => "summary.exists",
            DocumentSummaryRequestKind.SummaryStore => "admin.summary.submit",
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
        if (intent == "summary.exists")
        {
            requestKind = DocumentSummaryRequestKind.SummaryCheckOnly;
            return true;
        }

        if (intent == "admin.summary.submit")
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

        var ragCalls = 0;
        foreach (var call in toolCalls)
        {
            if (call is null || string.IsNullOrWhiteSpace(call.Name))
                continue;

            var normalizedName = NormalizeToolName(call.Name);
            if (!ToolManifest.IsKnownTool(normalizedName))
                continue;

            if (IsRagToolName(normalizedName))
            {
                ragCalls++;
                if (ragCalls > MaxRagToolCalls)
                    continue;
            }

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
                categoryRef = GetStringArg(args, "categoryRef"),
                changedSince = NormalizeChangedSinceArg(GetStringArg(args, "changedSince")),
                q = GetStringArg(args, "q") ?? GetStringArg(args, "query"),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 80, 1, 500),
                offset = NormalizeIntArg(GetIntArg(args, "offset"), 0, 0, 100000)
            },
            "documents.search" => new
            {
                q = (GetStringArg(args, "q") ?? GetStringArg(args, "query") ?? string.Empty).Trim(),
                categoryPath = NormalizeCategoryPathArg(GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category")),
                categoryRef = GetStringArg(args, "categoryRef"),
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
            "admin.audit" => new
            {
                action = GetStringArg(args, "action"),
                target = GetStringArg(args, "target"),
                since = NormalizeChangedSinceArg(GetStringArg(args, "since")),
                until = NormalizeChangedSinceArg(GetStringArg(args, "until")),
                limit = NormalizeIntArg(GetIntArg(args, "limit"), 100, 1, 1000),
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

    private static string? NormalizeChangedSinceArg(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTimeOffset.TryParse(raw.Trim(), out var parsed)
            ? parsed.ToUniversalTime().ToString("O")
            : null;
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
        plan.RouterConfidence = ClampConfidence(plan.RouterConfidence);
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
        plan.RiskFlags = plan.RiskFlags
            ?.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList() ?? new List<string>();
        plan.ToolCalls = SanitizeToolCalls(plan.ToolCalls).ToList();

        plan.ToolCalls = plan.ToolCalls
            .Where(call => !ToolManifest.IsAdminTool(call.Name))
            .Take(MaxToolCalls)
            .ToList();

        if (string.IsNullOrWhiteSpace(plan.Intent))
            plan.Intent = InferIntentFromToolCalls(plan.ToolCalls) ?? "chat.general";

        if (disallowMetaSetLanguage && string.Equals(plan.Intent, "meta.set_language", StringComparison.OrdinalIgnoreCase))
            plan.Intent = "chat.general";

        if (IsAdminOnlyIntent(plan.Intent))
        {
            plan.Intent = "meta.help";
            plan.NeedClarification = false;
            plan.ClarificationQuestions.Clear();
            plan.ToolCalls.Clear();
            plan.MemoryUpdate = null;
        }

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
            "set_style" or "change_style" or "meta.change_style" => "meta.set_style",
            "set_mode" or "change_mode" or "meta.change_mode" => "meta.set_mode",
            "translate_last" or "translate.last" or "meta.translate_last" => "meta.translate_last_answer",
            "repair" or "rewrite_last" or "meta.rewrite_last" => "meta.rewrite_last",
            "count_documents" or "documents.count" => "inventory.count",
            "list_documents" or "documents.list" => "inventory.list",
            "documents.search" or "inventory.find" or "find_documents" => "inventory.find",
            "inventory.changed_since" or "changed_since" or "documents.changed_since" => "inventory.changed_since",
            "categories" or "documents.categories" => "inventory.categories",
            "tree" or "documents.tree" or "inventory.tree_sub" => "inventory.tree",
            "stats" or "documents.stats" => "inventory.stats",
            "inventory.health" or "catalog.health" => "inventory.health",
            "summary.status" or "summary_status" or "inventory.summary_status" or "admin.summary.missing" or "summary.status.count" or "summary.status.list" or "summary.present.count" or "summary.present.list" => "inventory.summary_status",
            "document.about" or "document_about" or "rag.about_doc" => "rag.summarize_doc",
            "summary.exists" or "check_summary" => "summary.exists",
            "summary.store" or "refresh_summary" => "admin.summary.submit",
            _ => normalized
        };
    }

    private static bool IsRagToolName(string toolName)
        => toolName.StartsWith("rag.", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldForceRagForStandaloneTopic(string effectiveUserMessage, RouterPlan plan)
    {
        if (plan is null)
            return false;
        if (!string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase))
            return false;
        if (plan.ToolCalls.Count > 0 || plan.NeedClarification)
            return false;

        return LooksLikeStandaloneDocumentaryTopic(effectiveUserMessage)
            || LooksLikeCuisineActionRequest(effectiveUserMessage);
    }

    private static bool LooksLikeCuisineActionRequest(string? userMessage)
    {
        var s = CollapseWhitespace(userMessage ?? string.Empty);
        if (s.Length < 6)
            return false;

        if (!Regex.IsMatch(
                s,
                @"(?i)\b(?:recette|recettes|cuisine|cuisiner|repas|menu|menus|sauce|sauces|entrecote|entrecôte|steak|viande|poisson|poulet|dessert|semaine|batch\s+cooking|meal|meals|recipe|recipes|dinner|week|cocina|receta|recetas|comida|salsa|carne|cozinha|receita|receitas|refeicao|refeição|molho|kueche|küche|rezept|rezepte|essen|fleisch|cucina|ricetta|ricette|pasto)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return Regex.IsMatch(
            s,
            @"(?i)\b(?:aide|aider|propose|proposer|trouve|trouver|faire|vais|veux|peux|peux-tu|pourrais|conseille|conseiller|choisir|planifie|organise|help|suggest|recommend|cook|make|plan|prepare|find|ayuda|ayudar|propone|recomienda|cozinhar|ajuda|vorschlag|empfiehl|aiutami|consiglia)\b",
            RegexOptions.CultureInvariant);
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
            "documents.list" => HasChangedSinceArg(first.Args) ? "inventory.changed_since" : "inventory.list",
            "documents.search" => "inventory.find",
            "summary.exists" or "summary.get" => "summary.exists",
            "rag.search" or "rag.multi_search" => "rag.answer",
            "rag.summarize_live" => "rag.summarize_doc",
            "admin.summary.request" or "admin.summary.generate" or "admin.summary.submit" => "admin.summary.submit",
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

    internal static JsonElement ApplySpecificDocumentQueryGuard(JsonElement rawData, string? searchQuery)
    {
        var query = (searchQuery ?? string.Empty).Trim();
        if (!LooksLikeSpecificDocumentReferenceQuery(query))
            return rawData;

        if (!rawData.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return rawData;

        var filtered = new List<JsonElement>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var docName = TryGetString(entry, "docName");
            var docPath = TryGetString(entry, "docPath");
            if (MatchesSpecificDocumentReferenceQuery(query, docName, docPath))
                filtered.Add(entry.Clone());
        }

        var normalized = new
        {
            scopePath = TryGetString(rawData, "scopePath"),
            searchQuery = query,
            limit = TryGetInt(rawData, "limit") ?? filtered.Count,
            offset = TryGetInt(rawData, "offset") ?? 0,
            total = filtered.Count,
            endOfList = true,
            dropped = TryGetInt(rawData, "dropped") ?? 0,
            items = filtered
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(normalized));
        return doc.RootElement.Clone();
    }

    internal static bool LooksLikeSpecificDocumentReferenceQuery(string? searchQuery)
    {
        var query = (searchQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
            return false;

        return Regex.IsMatch(query, @"(?i)\.pdf\b")
            || query.Contains('/')
            || query.Contains('\\')
            || Regex.IsMatch(query, @"(?i)\bPDF\s*0*\d{1,4}\b");
    }

    internal static bool MatchesSpecificDocumentReferenceQuery(string searchQuery, string? docName, string? docPath)
    {
        var query = (searchQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalizedQueryPath = query.Replace('\\', '/').Trim().Trim('/');
        var normalizedQueryText = NormalizeDocumentLookupText(query);
        var normalizedQueryCompact = NormalizeDocumentLookupCompact(query);
        var queryFileName = Path.GetFileName(normalizedQueryPath);
        var queryFileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalizedQueryPath);
        var normalizedQueryFileName = NormalizeDocumentLookupText(queryFileName);
        var normalizedQueryFileNameWithoutExtension = NormalizeDocumentLookupText(queryFileNameWithoutExtension);

        IEnumerable<string> EnumerateCandidates()
        {
            if (!string.IsNullOrWhiteSpace(docName))
                yield return docName!;
            if (!string.IsNullOrWhiteSpace(docPath))
            {
                yield return docPath!;
                var fileName = Path.GetFileName(docPath!);
                if (!string.IsNullOrWhiteSpace(fileName))
                    yield return fileName;
            }
        }

        foreach (var raw in EnumerateCandidates().Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = raw.Trim();
            var normalizedCandidatePath = candidate.Replace('\\', '/').Trim().Trim('/');
            var normalizedCandidateText = NormalizeDocumentLookupText(candidate);
            var normalizedCandidateCompact = NormalizeDocumentLookupCompact(candidate);
            var candidateFileName = Path.GetFileName(normalizedCandidatePath);
            var candidateFileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalizedCandidatePath);
            var normalizedCandidateFileName = NormalizeDocumentLookupText(candidateFileName);
            var normalizedCandidateFileNameWithoutExtension = NormalizeDocumentLookupText(candidateFileNameWithoutExtension);

            if (string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedCandidatePath, normalizedQueryPath, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(normalizedQueryCompact) && string.Equals(normalizedCandidateCompact, normalizedQueryCompact, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(normalizedQueryText) && string.Equals(normalizedCandidateText, normalizedQueryText, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(normalizedQueryFileName) && string.Equals(normalizedCandidateFileName, normalizedQueryFileName, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(normalizedQueryFileNameWithoutExtension) && string.Equals(normalizedCandidateFileNameWithoutExtension, normalizedQueryFileNameWithoutExtension, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
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
        var tail = hist
            .TakeLast(maxTurns)
            .Select(m => new
            {
                role = m.role,
                content = TruncateForPrompt(m.content, SerializedTailContentMaxChars)
            })
            .ToList();
        return JsonSerializer.Serialize(tail);
    }

    private static string TruncateForPrompt(string? value, int maxChars)
    {
        var s = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (maxChars <= 0 || s.Length <= maxChars)
            return s;

        return s[..maxChars].TrimEnd() + "...";
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
