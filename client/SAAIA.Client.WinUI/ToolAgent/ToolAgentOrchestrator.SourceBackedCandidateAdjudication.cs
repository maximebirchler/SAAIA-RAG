using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<string?> TryBuildSourceBackedCandidateAdjudicationForWriterAsync(
        ToolResults writerToolResults,
        string writerUserMessage,
        string language,
        WriterPromptBudget writerPromptBudget,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!ShouldRunSourceBackedCandidateAdjudicationForWriter(writerToolResults, writerUserMessage, language))
        {
            EmitRagTrace(
                "writer.candidate_adjudication.skipped",
                ("reason", "not_applicable"),
                ("tool_items", writerToolResults.Items.Count),
                ("ms", sw.ElapsedMilliseconds));
            return null;
        }

        var system = BuildSourceBackedCandidateAdjudicationSystemPrompt(language);
        var promptBudget = CreateSourceBackedCandidateAdjudicationPromptBudget(writerPromptBudget);
        var user = BuildSourceBackedCandidateAdjudicationUserPrompt(
            writerToolResults,
            writerUserMessage,
            language,
            promptBudget);
        if (!user.Contains("EVIDENCE_ITEM", StringComparison.OrdinalIgnoreCase))
        {
            EmitRagTrace(
                "writer.candidate_adjudication.skipped",
                ("reason", "no_candidate_inventory"),
                ("tool_items", writerToolResults.Items.Count),
                ("ms", sw.ElapsedMilliseconds));
            return null;
        }

        var initialPromptChars = system.Length + user.Length;
        if (initialPromptChars > promptBudget.PromptTargetChars)
        {
            var compactBudget = CreateSourceBackedCandidateAdjudicationPromptBudget(writerPromptBudget, compactRetry: true);
            var compactUser = BuildSourceBackedCandidateAdjudicationUserPrompt(
                writerToolResults,
                writerUserMessage,
                language,
                compactBudget);
            if (compactUser.Contains("EVIDENCE_ITEM", StringComparison.OrdinalIgnoreCase))
            {
                EmitRagTrace(
                    "writer.candidate_adjudication.compacted",
                    ("reason", "prompt_budget"),
                    ("from_prompt_chars", initialPromptChars),
                    ("to_prompt_chars", system.Length + compactUser.Length),
                    ("target_chars", promptBudget.PromptTargetChars),
                    ("tool_results_chars", compactBudget.ToolResultsChars),
                    ("inventory_chars", compactBudget.EvidenceInventoryChars),
                    ("max_evidence_items", compactBudget.MaxEvidenceItems),
                    ("coverage_lines", compactBudget.CoverageTraceLines));
                promptBudget = compactBudget;
                user = compactUser;
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SourceBackedLlmEvidencePlannerTimeoutMs);

        async Task<string?> CompleteCandidateAdjudicationPromptAsync(
            string activeUser,
            SourceBackedCandidateAdjudicationPromptBudget activeBudget,
            string tracePrefix)
        {
            EmitRagTrace(
                $"{tracePrefix}.start",
                ("prompt_chars", system.Length + activeUser.Length),
                ("tool_items", writerToolResults.Items.Count),
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs),
                ("tool_results_chars", activeBudget.ToolResultsChars),
                ("inventory_chars", activeBudget.EvidenceInventoryChars),
                ("max_evidence_items", activeBudget.MaxEvidenceItems),
                ("coverage_lines", activeBudget.CoverageTraceLines),
                ("target_chars", activeBudget.PromptTargetChars));
            var raw = await CompleteWithRetryAsync(
                new[] { ("system", system), ("user", activeUser) },
                forceJson: true,
                timeoutCts.Token).ConfigureAwait(false);
            var normalized = NormalizeSourceBackedCandidateAdjudicationJsonForWriter(raw);
            var ok = !string.IsNullOrWhiteSpace(normalized);
            _lastToolDurations.Add(("rag.candidate_adjudication", sw.ElapsedMilliseconds, ok));
            EmitRagTrace(
                $"{tracePrefix}.end",
                ("ok", ok),
                ("decision", ok ? ExtractSourceBackedCandidateAdjudicationDecision(normalized) : "invalid_json"),
                ("answer_chars", raw?.Length ?? 0),
                ("ms", sw.ElapsedMilliseconds));
            return normalized;
        }

        try
        {
            return await CompleteCandidateAdjudicationPromptAsync(
                user,
                promptBudget,
                "writer.candidate_adjudication").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _lastToolDurations.Add(("rag.candidate_adjudication", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "writer.candidate_adjudication.timeout",
                ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs),
                ("ms", sw.ElapsedMilliseconds));
            return null;
        }
        catch (Exception ex) when (IsLlmContextOverflowException(ex))
        {
            EmitRagTrace(
                "writer.candidate_adjudication.overflow",
                ("error", TruncateForPrompt(ex.Message, 240)),
                ("prompt_chars", system.Length + user.Length),
                ("ms", sw.ElapsedMilliseconds));

            var compactBudget = CreateSourceBackedCandidateAdjudicationPromptBudget(writerPromptBudget, compactRetry: true);
            var compactUser = BuildSourceBackedCandidateAdjudicationUserPrompt(
                writerToolResults,
                writerUserMessage,
                language,
                compactBudget);
            if (!compactUser.Contains("EVIDENCE_ITEM", StringComparison.OrdinalIgnoreCase)
                || string.Equals(compactUser, user, StringComparison.Ordinal))
            {
                _lastToolDurations.Add(("rag.candidate_adjudication", sw.ElapsedMilliseconds, false));
                return null;
            }

            try
            {
                return await CompleteCandidateAdjudicationPromptAsync(
                    compactUser,
                    compactBudget,
                    "writer.candidate_adjudication.compact_retry").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _lastToolDurations.Add(("rag.candidate_adjudication", sw.ElapsedMilliseconds, false));
                EmitRagTrace(
                    "writer.candidate_adjudication.compact_retry.timeout",
                    ("timeout_ms", SourceBackedLlmEvidencePlannerTimeoutMs),
                    ("ms", sw.ElapsedMilliseconds));
                return null;
            }
            catch (Exception retryEx)
            {
                _lastToolDurations.Add(("rag.candidate_adjudication", sw.ElapsedMilliseconds, false));
                EmitRagTrace(
                    "writer.candidate_adjudication.compact_retry.error",
                    ("error", TruncateForPrompt(retryEx.Message, 240)),
                    ("prompt_chars", system.Length + compactUser.Length),
                    ("ms", sw.ElapsedMilliseconds));
                return null;
            }
        }
        catch (Exception ex) when (!IsLlmContextOverflowException(ex))
        {
            _lastToolDurations.Add(("rag.candidate_adjudication", sw.ElapsedMilliseconds, false));
            EmitRagTrace(
                "writer.candidate_adjudication.error",
                ("error", TruncateForPrompt(ex.Message, 240)),
                ("ms", sw.ElapsedMilliseconds));
            return null;
        }
    }

    private async Task<bool> TryExpandSourceBackedEvidenceAfterCandidateAdjudicationAsync(
        ToolResults rawToolResults,
        RouterPlan plan,
        string writerUserMessage,
        string language,
        string? candidateAdjudicationJson,
        CancellationToken ct)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(writerUserMessage);
        if (string.IsNullOrWhiteSpace(intentQuery)
            || (!ShouldGateStructuredSourceBackedPlanningCoverage(intentQuery)
                && !LooksLikeAnyDocumentaryPlanningRequest(intentQuery)
                && !LooksLikeBroadSourceBackedCompositionRequest(intentQuery)
                && !LooksLikeMultipleCandidateSynthesisRequest(intentQuery)))
        {
            return false;
        }

        if (!TryBuildSourceBackedCandidateAdjudicationSignal(
                candidateAdjudicationJson,
                intentQuery,
                language,
                out var signal)
            || !signal.RequestsMoreRetrieval)
        {
            return false;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        EmitRagTrace(
            "writer.candidate_adjudication.retrieval_expansion.skipped",
            ("trace_path", "rag.writer.candidate_adjudication"),
            ("trace_step", "retrieval_expansion_skipped"),
            ("reason", "canonical_pipeline_owns_retrieval"),
            ("decision", signal.Decision),
            ("useful_candidates", signal.UsefulCandidateCount),
            ("missing", signal.MissingCount),
            ("missing_slots", signal.MissingSlots.Take(8).ToArray()));
        return false;
    }
}