using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<(string answer, List<ToolMemory.SourceRef>? sources)> AnswerAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults toolResults,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        ClientLog.Info(
            "ToolAgent answer stage start: " +
            $"intent={plan.Intent}|mode={plan.Mode}|lang={plan.Language}|toolItems={toolResults.Items.Count}|" +
            $"inputTools={string.Join(",", toolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase))}|chars={userMessage.Length}");
        EmitRagTrace(
            "answer.stage.start",
            ("intent", plan.Intent),
            ("mode", plan.Mode),
            ("language", plan.Language),
            ("tool_items", toolResults.Items.Count),
            ("input_tools", toolResults.Items.Select(static x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            ("chars", userMessage.Length));
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

        var writerUserMessage = ResolveSourceBackedWriterUserMessage(userMessage);
        var writerPromptBudget = ResolveWriterPromptBudget();
        var writerToolResults = BuildWriterToolResultsForRuntime(plan, toolResults, writerUserMessage, writerPromptBudget);
        _lastWriterToolNames = writerToolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _lastUsedInventoryRendered = _lastUsedInventoryRendered || _lastWriterToolNames.Any(x => string.Equals(x, "inventory.rendered", StringComparison.OrdinalIgnoreCase));
        ClientLog.Info(
            "ToolAgent answer writer context: " +
            $"general={useGeneralChatPrompt}|writerTools={writerToolResults.Items.Count}|writerToolNames={string.Join(",", _lastWriterToolNames)}|" +
            $"budgetToolsChars={writerPromptBudget.ToolResultsChars}|budgetBriefChars={writerPromptBudget.WritingBriefChars}|budgetInventoryChars={writerPromptBudget.EvidenceInventoryChars}");
        EmitRagTrace(
            "writer.context",
            ("general", useGeneralChatPrompt),
            ("writer_tools", writerToolResults.Items.Count),
            ("writer_tool_names", _lastWriterToolNames.ToArray()),
            ("budget_tool_chars", writerPromptBudget.ToolResultsChars),
            ("budget_brief_chars", writerPromptBudget.WritingBriefChars),
            ("budget_inventory_chars", writerPromptBudget.EvidenceInventoryChars));
        var inventoryRenderedText = TryRenderInventoryFallbackText(writerToolResults, plan.Language);
        var inventoryRenderedDataJson = TryExtractInventoryRenderedDataJson(writerToolResults);
        var earlyWriterBypass = TryBuildEarlyWriterBypassAnswer(
            plan,
            toolResults,
            writerToolResults,
            userMessage,
            writerUserMessage,
            inventoryRenderedText);
        if (earlyWriterBypass.handled)
            return (earlyWriterBypass.answer, earlyWriterBypass.sources);
        var requestedItemTitle = earlyWriterBypass.requestedItemTitle;

        var shouldUseSourceBackedOptionAnswer = ShouldUseSourceBackedOptionAnswer(requestedItemTitle, writerUserMessage)
            && !ShouldAvoidDeterministicSourceBackedOptionFallback(writerUserMessage);
        var shouldUseSourceBackedCountdownAnswer = LooksLikeSourceBackedCountdownPlanningRequest(writerUserMessage);
        var shouldUseSourceBackedExtractiveAnswer = ShouldUseSourceBackedExtractiveAnswer(writerUserMessage, writerToolResults);
        var shouldUseSourceBackedActionAnswer = LooksLikeSourceBackedActionRequest(writerUserMessage)
            && ShouldPreferSourceBackedAnswerOverBackendClarification(writerToolResults, writerUserMessage);
        var shouldUseSourceBackedPairingAnswer = LooksLikeSourceBackedPairingRecommendationRequest(writerUserMessage);
        var shouldRouteSourceBackedAnswerThroughWriter =
            ShouldRouteSourceBackedAnswerThroughWriter(writerToolResults, writerUserMessage, plan.Language);
        var shouldRequireWriterForBroadDocumentaryFinal =
            ShouldRequireWriterForBroadDocumentaryFinal(writerToolResults, writerUserMessage, plan.Language);
        var shouldAvoidRawSourceBackedFallback =
            ShouldAvoidRawSourceBackedFallback(writerUserMessage)
            || shouldRouteSourceBackedAnswerThroughWriter
            || shouldRequireWriterForBroadDocumentaryFinal;
        var shouldUseBroadSourceBackedSynthesis =
            ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, writerUserMessage)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, writerUserMessage)
            || shouldRouteSourceBackedAnswerThroughWriter
            || shouldRequireWriterForBroadDocumentaryFinal;
        var planningGuardSw = Stopwatch.StartNew();
        EmitRagTrace(
            "writer.guard.coverage.start",
            ("raw_tool_items", toolResults.Items.Count),
            ("writer_tool_items", writerToolResults.Items.Count),
            ("query", writerUserMessage));
        var planningGuardSelection = ResolveStructuredPlanningWriterGuardToolResults(
            toolResults,
            writerToolResults,
            writerUserMessage,
            plan.Language);
        EmitRagTrace(
            "writer.guard.coverage.selection",
            ("basis", planningGuardSelection.Basis),
            ("has_raw_coverage", planningGuardSelection.RawCoverage is not null),
            ("has_writer_coverage", planningGuardSelection.WriterCoverage is not null),
            ("ms", planningGuardSw.ElapsedMilliseconds));
        var structuredPlanningGuardToolResults = planningGuardSelection.ToolResults;
        var writerEvidenceToolResults = structuredPlanningGuardToolResults;
        EmitRagTrace(
            "writer.evidence_inventory.selection",
            ("trace_path", "rag.writer.evidence_inventory"),
            ("trace_step", "select_tool_results"),
            ("basis", planningGuardSelection.Basis),
            ("tool_items", writerEvidenceToolResults.Items.Count));
        if (planningGuardSelection.RawCoverage is { } rawPlanningCoverage
            && planningGuardSelection.WriterCoverage is { } writerPlanningCoverage)
        {
            EmitRagTrace(
                "writer.guard.coverage",
                ("raw_adequate", rawPlanningCoverage.IsAdequate),
                ("raw_candidates", rawPlanningCoverage.CandidateCount),
                ("raw_distinct_pages", rawPlanningCoverage.DistinctSourcePages),
                ("writer_adequate", writerPlanningCoverage.IsAdequate),
                ("writer_candidates", writerPlanningCoverage.CandidateCount),
                ("writer_distinct_pages", writerPlanningCoverage.DistinctSourcePages),
                ("guard_basis", planningGuardSelection.Basis));
        }

        var insufficientGuardSw = Stopwatch.StartNew();
        EmitRagTrace(
            "writer.guard.insufficient_check.start",
            ("basis", planningGuardSelection.Basis),
            ("tool_items", structuredPlanningGuardToolResults.Items.Count));
        var insufficientStructuredPlanningAnswer = TryBuildInsufficientStructuredPlanningBeforeWriterAnswer(
            structuredPlanningGuardToolResults,
            writerUserMessage,
            plan.Language);
        EmitRagTrace(
            "writer.guard.insufficient_check.end",
            ("blocked", !string.IsNullOrWhiteSpace(insufficientStructuredPlanningAnswer)),
            ("answer_chars", insufficientStructuredPlanningAnswer?.Length ?? 0),
            ("ms", insufficientGuardSw.ElapsedMilliseconds));
        if (!string.IsNullOrWhiteSpace(insufficientStructuredPlanningAnswer))
        {
            ClientLog.Info(
                "ToolAgent structured planning writer guard blocked unsupported synthesis before writer: " +
                $"intent={plan.Intent} " +
                $"broad={shouldUseBroadSourceBackedSynthesis} " +
                $"route={shouldRouteSourceBackedAnswerThroughWriter} " +
                $"requireFinal={shouldRequireWriterForBroadDocumentaryFinal}");
            LogSourceBackedPlanningTrace(
                "writer-guard-blocked-before-writer",
                structuredPlanningGuardToolResults,
                writerUserMessage,
                plan.Language);
            _lastAnswerSource = $"writer_bypass_insufficient_structured_planning:{plan.Intent}";
            EmitRagTrace(
                "writer.guard",
                ("decision", "blocked_before_writer"),
                ("reason", "insufficient_structured_planning"),
                ("answer_source", _lastAnswerSource));
            return (insufficientStructuredPlanningAnswer, null);
        }

        var wouldHaveUsedSourceBackedDeterministicWriterBypass =
            !shouldUseBroadSourceBackedSynthesis
            && !shouldAvoidRawSourceBackedFallback
            && (shouldUseSourceBackedExtractiveAnswer || shouldUseSourceBackedActionAnswer || shouldUseSourceBackedCountdownAnswer || shouldUseSourceBackedPairingAnswer || shouldUseSourceBackedOptionAnswer);
        if (wouldHaveUsedSourceBackedDeterministicWriterBypass)
        {
            EmitRagTrace(
                "writer.bypass.skipped",
                ("reason", "canonical_writer_required_no_deterministic_bypass"),
                ("would_have_used_extract", shouldUseSourceBackedExtractiveAnswer),
                ("would_have_used_action", shouldUseSourceBackedActionAnswer),
                ("would_have_used_countdown", shouldUseSourceBackedCountdownAnswer),
                ("would_have_used_pairing", shouldUseSourceBackedPairingAnswer),
                ("would_have_used_option", shouldUseSourceBackedOptionAnswer));
        }

        var writerContext = await BuildAnswerWriterContextAsync(
                chatHistory,
                userMessage,
                writerUserMessage,
                plan,
                toolResults,
                writerToolResults,
                writerEvidenceToolResults,
                writerPromptBudget,
                inventoryRenderedDataJson,
                useGeneralChatPrompt,
                system,
                shouldUseBroadSourceBackedSynthesis,
                shouldAvoidRawSourceBackedFallback,
                shouldRouteSourceBackedAnswerThroughWriter,
                shouldRequireWriterForBroadDocumentaryFinal,
                ct)
            .ConfigureAwait(false);
        writerToolResults = writerContext.WriterToolResults;
        writerEvidenceToolResults = writerContext.WriterEvidenceToolResults;
        var useCleanSourceBrief = writerContext.UseCleanSourceBrief;
        var writerSystem = writerContext.WriterSystem;
        var user = writerContext.UserPrompt;
        var writerMessages = writerContext.WriterMessages;
        var candidateAdjudicationJson = writerContext.CandidateAdjudicationJson;

        var writerLlmResult = await RunAnswerWriterLlmAsync(
                chatHistory,
                userMessage,
                writerUserMessage,
                plan,
                writerToolResults,
                writerEvidenceToolResults,
                writerPromptBudget,
                useCleanSourceBrief,
                writerSystem,
                user,
                writerMessages,
                ct,
                onDelta)
            .ConfigureAwait(false);
        var finalAnswer = writerLlmResult.FinalAnswer;
        var usedWriterContextOverflowFallback = writerLlmResult.UsedWriterContextOverflowFallback;

        var swWriterPost = Stopwatch.StartNew();
        EmitRagTrace(
            "writer.post.start",
            ("intent", plan.Intent),
            ("answer_chars", finalAnswer?.Length ?? 0),
            ("overflow_fallback", usedWriterContextOverflowFallback));

        finalAnswer = (finalAnswer ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(finalAnswer) && !string.IsNullOrWhiteSpace(inventoryRenderedText))
            finalAnswer = inventoryRenderedText.Trim();
        if (string.IsNullOrWhiteSpace(finalAnswer))
            finalAnswer = DeterministicAgentText.AnswerNotEnoughUsableInfo(plan.Language);

        var writerPostLanguageSw = Stopwatch.StartNew();
        EmitRagTrace(
            "writer.post.language_check.start",
            ("language", plan.Language),
            ("answer_chars", finalAnswer?.Length ?? 0));
        finalAnswer = await EnsureAnswerMatchesRequestedLanguageAsync(finalAnswer ?? string.Empty, plan.Language, ct).ConfigureAwait(false);
        writerPostLanguageSw.Stop();
        EmitRagTrace(
            "writer.post.language_check.end",
            ("answer_chars", finalAnswer?.Length ?? 0),
            ("elapsed_ms", writerPostLanguageSw.ElapsedMilliseconds));
        finalAnswer = ReplaceWriterEvidenceIdReferencesWithCitations(
            finalAnswer,
            writerEvidenceToolResults,
            writerUserMessage,
            plan.Language);

        List<ToolMemory.SourceRef>? sources = null;
        var usedRagSearch = toolResults.Items.Any(x => x.ToolName is "rag.search" or "rag.multi_search");
        var usedSourcesResolve = toolResults.Items.Any(x => x.ToolName == "sources.resolve");
        var usedSummarySearch = toolResults.Items.Any(x => x.ToolName == "summary.search");
        var sourceToolResults = usedWriterContextOverflowFallback
            && writerEvidenceToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search")
            ? writerEvidenceToolResults
            : toolResults;
        var structuredSourceBackedPlanningFinalResolved = false;
        var finalPlanningCoverageQuery = ResolveSourceBackedFallbackIntentQuery(userMessage);
        if (string.IsNullOrWhiteSpace(finalPlanningCoverageQuery))
            finalPlanningCoverageQuery = userMessage;

        var postWriterRagResult = await RunAnswerPostWriterRagSourceGuardsAsync(
                chatHistory,
                userMessage,
                plan,
                toolResults,
                writerToolResults,
                sourceToolResults,
                finalPlanningCoverageQuery,
                finalAnswer,
                sources,
                usedRagSearch,
                usedSourcesResolve,
                usedSummarySearch,
                shouldUseSourceBackedOptionAnswer,
                candidateAdjudicationJson,
                ct)
            .ConfigureAwait(false);
        finalAnswer = postWriterRagResult.FinalAnswer;
        sources = postWriterRagResult.Sources;
        structuredSourceBackedPlanningFinalResolved = postWriterRagResult.StructuredSourceBackedPlanningFinalResolved;

        var postCriticResult = await RunAnswerPostCriticSourceAlignmentAsync(
                chatHistory,
                userMessage,
                plan,
                toolResults,
                writerToolResults,
                sourceToolResults,
                finalPlanningCoverageQuery,
                finalAnswer,
                sources,
                structuredSourceBackedPlanningFinalResolved,
                usedRagSearch,
                useGeneralChatPrompt,
                shouldAvoidRawSourceBackedFallback,
                ct,
                onProgress)
            .ConfigureAwait(false);
        finalAnswer = postCriticResult.FinalAnswer;
        sources = postCriticResult.Sources;

        EmitRagTrace(
            "writer.absolute_finalizer.skipped",
            ("reason", "canonical_source_contract_owns_finalization"),
            ("used_rag", usedRagSearch),
            ("answer_chars", finalAnswer?.Length ?? 0),
            ("sources", sources?.Count ?? 0),
            ("query", finalPlanningCoverageQuery));

        EmitRagTrace(
            "writer.post.end",
            ("intent", plan.Intent),
            ("answer_chars", finalAnswer?.Length ?? 0),
            ("sources", sources?.Count ?? 0),
            ("answer_source", _lastAnswerSource),
            ("ms", swWriterPost.ElapsedMilliseconds));
        return (finalAnswer ?? string.Empty, sources);
    }
}
