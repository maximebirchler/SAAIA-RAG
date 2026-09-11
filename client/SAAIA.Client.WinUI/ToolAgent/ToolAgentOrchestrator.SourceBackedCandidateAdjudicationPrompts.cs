using System;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int SourceBackedCandidateAdjudicationToolResultsChars = 12000;
    private const int SourceBackedCandidateAdjudicationMinimumToolResultsChars = 900;
    private const int SourceBackedCandidateAdjudicationMinimumEvidenceInventoryChars = 1800;
    private static bool ShouldRunSourceBackedCandidateAdjudicationForWriter(
        ToolResults writerToolResults,
        string? query,
        string language)
    {
        if (string.IsNullOrWhiteSpace(query)
            || !writerToolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            return false;
        }

        return ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || ShouldUseWriterForBroadSourceBackedSynthesis(writerToolResults, query)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(writerToolResults, query)
            || ShouldRequireWriterForBroadDocumentaryFinal(writerToolResults, query, language);
    }

    private static string BuildSourceBackedCandidateAdjudicationSystemPrompt(string language)
    {
        language = NormalizeLanguageCode(language);
        return $$"""
You are SAAIA's private source-candidate adjudicator.
Target language for short reasons: {{language}}.

Return one strict JSON object only. Do not answer the user.
Your job is to advise whether retrieved candidates look useful as building blocks for the user's requested answer shape. The final writer remains the final judge.

Rules:
- Work for any source category or domain. Do not assume a particular file type, folder, source category or fixed taxonomy.
- Treat route labels, slot labels, retrieval queries, headings and indexes as discovery hints, not proof.
- A candidate is valid when the title/local evidence/tool result plausibly supports using that candidate for at least one requested slot, criterion, phase, role or answer part.
- If EVIDENCE_ITEM includes codeDecision/codeReason, treat them as diagnostic hints from deterministic code, not as final truth or vetoes. You may override them when the concrete tool results make the source useful and source-backed.
- Mark sourceUseful=false when a source is only navigation, summary-only, duplicated, too vague, too noisy or not needed for the final answer.
- Mark duplicateOf with another candidateKey when two candidates cite the same useful item or the same source/page for the same purpose.
- If evidence is partial, keep useful valid candidates and list the missing slots or criteria instead of rejecting everything.
- Keep reasons short and practical; they are private writer guidance.
""";
    }

    private static string BuildSourceBackedCandidateAdjudicationUserPrompt(
        ToolResults writerToolResults,
        string query,
        string language,
        SourceBackedCandidateAdjudicationPromptBudget? promptBudget = null)
    {
        language = NormalizeLanguageCode(language);
        var budget = promptBudget ?? CreateSourceBackedCandidateAdjudicationPromptBudget(
            CreateWriterPromptBudget(AppSettings.DefaultCtxSize, 900));
        var evidenceInventory = BuildSourceBackedCandidateLeadsForWriter(
            writerToolResults,
            query,
            language,
            maxItems: budget.MaxEvidenceItems,
            maxChars: budget.EvidenceInventoryChars);
        var coverageTrace = BuildSourceBackedLlmPlanningCoverageTraceForPrompt(
            writerToolResults,
            query,
            language,
            budget.CoverageTraceLines);
        var compactedToolResults = ApplyWriterToolResultsBudget(
            writerToolResults,
            query,
            Math.Max(SourceBackedCandidateAdjudicationMinimumToolResultsChars, budget.ToolResultsChars));
        var compactToolResults = TruncateForPrompt(
            SerializeToolResults(compactedToolResults),
            budget.ToolResultsChars);

        return $$"""
Do not answer the user. Privately adjudicate the source candidates before the final writer drafts.

USER_REQUEST:
{{query}}

REQUEST_SHAPE:
{{BuildSourceBackedRequestShapeForPrompt(query, language)}}

PLANNING_COVERAGE_TRACE:
{{coverageTrace}}

EVIDENCE_INVENTORY:
{{evidenceInventory}}

COMPACT_TOOL_RESULTS_EXCERPT:
{{compactToolResults}}

Return JSON matching this schema. The decision value must be exactly one of: "use_candidates", "partial" or "insufficient". Do not copy a pipe-separated schema literal as the value.
{
  "decision": "use_candidates",
  "reason": "short private reason",
  "items": [
    {
      "candidateKey": "exact candidateKey from EVIDENCE_ITEM when present",
      "pageKey": "exact pageKey from EVIDENCE_ITEM when present",
      "title": "candidate title",
      "valid": true,
      "sourceUseful": true,
      "duplicateOf": null,
      "requestedSlots": ["requested slot/criterion/phase/role this candidate can support"],
      "confidence": "high|medium|low",
      "reason": "short private reason"
    }
  ],
  "missing": [
    {
      "slotOrCriterion": "requested slot, phase, role, criterion or answer part",
      "reason": "why the available sources do not support it yet"
    }
  ],
  "notes": ["short private writer guidance"]
}

Decision guidance:
- use_candidates: enough valid, useful, non-duplicated candidates exist for a solid answer.
- partial: some valid candidates exist, but the answer must expose missing or uncertain parts.
- insufficient: no candidate is legitimate enough to support the requested concrete answer.
- candidate_rejected/candidate_not_selected items are intentionally included so you can review what deterministic code would have hidden. Do not promote them automatically; judge them from COMPACT_TOOL_RESULTS_EXCERPT and their evidence fields.
""";
    }
}