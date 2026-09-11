using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record AnswerWriterLlmResult(
        string FinalAnswer,
        bool UsedWriterContextOverflowFallback);

    private async Task<AnswerWriterLlmResult> RunAnswerWriterLlmAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        string writerUserMessage,
        RouterPlan plan,
        ToolResults writerToolResults,
        ToolResults writerEvidenceToolResults,
        WriterPromptBudget writerPromptBudget,
        bool useCleanSourceBrief,
        string writerSystem,
        string user,
        (string role, string content)[] writerMessages,
        CancellationToken ct,
        Action<string>? onDelta)
    {
        var shouldTimeboxWriterLlm = writerToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search");
        var writerLlmTimeoutMs = shouldTimeboxWriterLlm
            ? ResolveSourceBackedPlanningWriterTimeoutMs(writerUserMessage)
            : 0;
        using var writerLlmTimeoutCts = writerLlmTimeoutMs > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (writerLlmTimeoutCts is not null)
            writerLlmTimeoutCts.CancelAfter(writerLlmTimeoutMs);

        try
        {
            ClientLog.Info(
                "ToolAgent answer writer llm call start: " +
                $"intent={plan.Intent}|promptChars={(writerSystem.Length + user.Length)}|cleanBrief={useCleanSourceBrief}");
            EmitRagTrace(
                "writer.llm.start",
                ("intent", plan.Intent),
                ("prompt_chars", writerSystem.Length + user.Length),
                ("clean_brief", useCleanSourceBrief),
                ("timeout_ms", writerLlmTimeoutMs));
            var finalAnswer = await StreamOrCompleteWithRetryAsync(
                    writerMessages,
                    onDelta,
                    writerLlmTimeoutCts?.Token ?? ct)
                .ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent answer writer llm call end: " +
                $"intent={plan.Intent}|answerChars={finalAnswer?.Length ?? 0}");
            EmitRagTrace(
                "writer.llm.end",
                ("intent", plan.Intent),
                ("answer_chars", finalAnswer?.Length ?? 0));
            return new AnswerWriterLlmResult(finalAnswer ?? string.Empty, false);
        }
        catch (OperationCanceledException) when (writerLlmTimeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            EmitRagTrace(
                "writer.llm.timeout",
                ("intent", plan.Intent),
                ("timeout_ms", writerLlmTimeoutMs),
                ("prompt_chars", writerSystem.Length + user.Length));
            var finalAnswer = BuildSourceBackedSafeFallbackAnswer(
                writerToolResults,
                userMessage,
                plan.Language,
                shouldAvoidRaw: true);
            if (string.IsNullOrWhiteSpace(finalAnswer))
            {
                var timeoutIntentQuery = ResolveSourceBackedFallbackIntentQuery(userMessage);
                finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                    plan.Language,
                    userMessage,
                    timeoutIntentQuery,
                    EnumerateRagHitSummaries(writerToolResults).Count(),
                    HasExpandedSourceBackedSearchEvidence(writerToolResults));
            }

            EmitRagTrace(
                "writer.llm.timeout.terminal_fallback",
                ("intent", plan.Intent),
                ("reason", "llm_timeout_no_deterministic_fallback"),
                ("answer_chars", finalAnswer?.Length ?? 0));
            _lastAnswerSource = $"writer_llm_timeout_terminal_fallback:{plan.Intent}";
            return new AnswerWriterLlmResult(finalAnswer ?? string.Empty, false);
        }
        catch (Exception ex) when (IsLlmContextOverflowException(ex)
                                   && writerToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            return await RunAnswerWriterOverflowRetryAsync(
                    chatHistory,
                    userMessage,
                    writerUserMessage,
                    plan,
                    writerToolResults,
                    writerEvidenceToolResults,
                    writerPromptBudget,
                    writerSystem,
                    user,
                    ExceptionDispatchInfo.Capture(ex),
                    ct,
                    onDelta)
                .ConfigureAwait(false);
        }
    }

    private async Task<AnswerWriterLlmResult> RunAnswerWriterOverflowRetryAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        string writerUserMessage,
        RouterPlan plan,
        ToolResults writerToolResults,
        ToolResults writerEvidenceToolResults,
        WriterPromptBudget writerPromptBudget,
        string writerSystem,
        string user,
        ExceptionDispatchInfo overflowException,
        CancellationToken ct,
        Action<string>? onDelta)
    {
        EmitRagTrace(
            "writer.llm.overflow",
            ("intent", plan.Intent),
            ("prompt_chars", writerSystem.Length + user.Length),
            ("error", overflowException.SourceException.Message));
        var finalAnswer = string.Empty;
        var usedWriterContextOverflowFallback = false;
        var overflowSourceToolResults = writerEvidenceToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search")
            ? writerEvidenceToolResults
            : writerToolResults;
        if (overflowSourceToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            var retryUser = BuildOverflowRetryWriterUserPrompt(
                chatHistory,
                writerUserMessage,
                plan,
                overflowSourceToolResults,
                BuildSourceBackedWriterFollowupContextNote(userMessage, plan.Language),
                ResolveOverflowRetryEvidenceInventoryPromptChars(
                    writerPromptBudget,
                    ShouldGateStructuredSourceBackedPlanningCoverage(writerUserMessage)));
            var retryMessages = new[]
            {
                ("system", BuildCompactSourceBackedWriterSystemPrompt(
                    plan.Language,
                    plan.Mode,
                    LocalizedStrings.NormalizeStyle(_mem.LastStyle),
                    repairMode: false,
                    compactRetry: true)),
                ("user", retryUser)
            };

            try
            {
                var retryPromptChars = retryMessages[0].Item2.Length + retryUser.Length;
                EmitRagTrace(
                    "writer.llm.overflow_retry.start",
                    ("intent", plan.Intent),
                    ("prompt_chars", retryPromptChars),
                    ("tool_items", overflowSourceToolResults.Items.Count));
                finalAnswer = await StreamOrCompleteWithRetryAsync(retryMessages, onDelta, ct).ConfigureAwait(false);
                _lastAnswerSource = $"writer_context_overflow_compact_retry:{plan.Intent}";
                EmitRagTrace(
                    "writer.llm.overflow_retry.end",
                    ("intent", plan.Intent),
                    ("answer_chars", finalAnswer?.Length ?? 0));
            }
            catch (Exception retryEx) when (IsLlmContextOverflowException(retryEx))
            {
                EmitRagTrace(
                    "writer.llm.overflow_retry.overflow",
                    ("intent", plan.Intent),
                    ("prompt_chars", retryMessages[0].Item2.Length + retryUser.Length),
                    ("error", retryEx.Message));
                finalAnswer = string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(finalAnswer))
        {
            finalAnswer = BuildSourceBackedSafeFallbackAnswer(
                writerToolResults,
                userMessage,
                plan.Language,
                shouldAvoidRaw: true);
            if (string.IsNullOrWhiteSpace(finalAnswer))
            {
                var overflowIntentQuery = ResolveSourceBackedFallbackIntentQuery(userMessage);
                finalAnswer = BuildBroadEvidenceStillInsufficientAnswer(
                    plan.Language,
                    userMessage,
                    overflowIntentQuery,
                    EnumerateRagHitSummaries(writerToolResults).Count(),
                    HasExpandedSourceBackedSearchEvidence(writerToolResults));
            }
            if (string.IsNullOrWhiteSpace(finalAnswer))
            {
                overflowException.Throw();
                throw new InvalidOperationException("Unreachable after rethrowing writer overflow exception.");
            }

            EmitRagTrace(
                "writer.llm.overflow.terminal_fallback",
                ("intent", plan.Intent),
                ("reason", "context_overflow_no_deterministic_fallback"),
                ("answer_chars", finalAnswer?.Length ?? 0));
            _lastAnswerSource = $"writer_context_overflow_terminal_fallback:{plan.Intent}";
            usedWriterContextOverflowFallback = true;
            if (onDelta is not null)
                onDelta(finalAnswer!);
        }

        return new AnswerWriterLlmResult(finalAnswer ?? string.Empty, usedWriterContextOverflowFallback);
    }
}
