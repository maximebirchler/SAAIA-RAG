using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<string> TryAddStructuredPlanningEvidenceIdsWithFocusedWriterAsync(
        string draftAnswer,
        ToolResults writerToolResults,
        string writerUserMessage,
        string language,
            CancellationToken ct)
    {
        var roster = BuildWriterEvidenceRosterForCitationMapping(writerToolResults, writerUserMessage, language);
        if (string.IsNullOrWhiteSpace(roster) || string.Equals(roster.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var promptBudget = ResolveWriterPromptBudget();
        var structuredPlanningWriter = ShouldGateStructuredSourceBackedPlanningCoverage(writerUserMessage);
        var targetSlots = structuredPlanningWriter
            ? Math.Max(1, ResolveSourceBackedPlanningTargetItemCount(writerUserMessage))
            : 0;
        var maxRosterItems = promptBudget.ContextTokens <= 4096
            ? 8
            : promptBudget.ContextTokens <= 8192
                ? 14
                : 24;
        var maxRosterChars = promptBudget.ContextTokens <= 4096
            ? 3600
            : promptBudget.ContextTokens <= 8192
                ? 5600
                : 9000;
        var maxDraftChars = promptBudget.ContextTokens <= 4096
            ? 2600
            : promptBudget.ContextTokens <= 8192
                ? 3600
                : 5000;
        if (structuredPlanningWriter)
        {
            var structuredRosterItemCeiling = promptBudget.ContextTokens <= 4096
                ? 12
                : promptBudget.ContextTokens <= 8192
                    ? 24
                    : 32;
            maxRosterItems = Math.Max(maxRosterItems, Math.Clamp(targetSlots, 8, structuredRosterItemCeiling));
            maxRosterChars = Math.Max(
                maxRosterChars,
                promptBudget.ContextTokens <= 4096
                    ? 4200
                    : promptBudget.ContextTokens <= 8192
                        ? 11000
                        : 12000);
            maxDraftChars = Math.Max(
                maxDraftChars,
                promptBudget.ContextTokens <= 4096
                    ? 3200
                    : promptBudget.ContextTokens <= 8192
                        ? 4600
                        : 6200);
        }
        var promptRoster = LimitEvidenceRosterForPrompt(roster, maxRosterItems, maxRosterChars);
        var sanitizedDraft = StripInlineSourceCitationsForEvidenceIdMapping(draftAnswer);

        var system = $@"
You are a citation mapper for SAAIA.
Target language: {NormalizeLanguageCode(language)}.

Rules:
- Do not invent new facts, sources, items or quantities.
- Return a corrected planning answer: keep the requested day/slot structure, but replace unsupported draft items with useful SOURCE_ROSTER titles when the roster supports them.
- Every filled concrete planning item must end with exactly one EVIDENCE_ITEM id such as [E1].
- A citation id is proof, not item text. Never leave a filled cell as only [E#], a source/page reference, or a file name; each filled cell needs a concrete item/action/value before [E#].
- Choose ids only from SOURCE_ROSTER. Ids not present in SOURCE_ROSTER are invalid.
- Use SOURCE_ROSTER title/evidence/citation fields to decide whether an id supports an item; the final decision is yours, not a code veto.
- If a SOURCE_ROSTER title is itself a concrete item, action or option, you may use that title as the cell item text.
- Do not write source names, file names, page numbers or ""source:"" citations yourself. Output only [E#] ids; SAAIA code will render the final citations.
- If no SOURCE_ROSTER id supports a filled item, keep the requested day/slot label and replace that item text with ""a completer avec une source utile"".
- Do not add a bibliography or explanations.
- Do not expose EVIDENCE_ITEM, SOURCE_ROSTER, candidateKey, pageKey, slotRoute or diagnostics.
";

        var user = $@"
USER_REQUEST:
{writerUserMessage}

SOURCE_ROSTER:
{promptRoster}

DRAFT_PLAN_TO_CITE:
{TruncateForPrompt(sanitizedDraft, maxDraftChars)}

Return only the corrected cited plan. Every filled item needs one valid [E#]. Prefer concrete SOURCE_ROSTER titles over unsupported draft text. Never write source names or pages.
";

        try
        {
            var rosterEvidenceItems = CountEvidenceInventoryRows(roster);
            var promptRosterEvidenceItems = CountEvidenceInventoryRows(promptRoster);
            EmitRagTrace(
                "writer.structured_citation_repair.start",
                ("trace_path", "rag.writer.citation_repair"),
                ("trace_step", "start"),
                ("prompt_chars", system.Length + user.Length),
                ("draft_chars", draftAnswer.Length),
                ("sanitized_draft_chars", sanitizedDraft.Length),
                ("roster_chars", roster.Length),
                ("roster_items", rosterEvidenceItems),
                ("prompt_roster_chars", promptRoster.Length),
                ("prompt_roster_items", promptRosterEvidenceItems),
                ("max_roster_chars", maxRosterChars),
                ("max_roster_items", maxRosterItems),
                ("target_slots", targetSlots),
                ("structured_planning", structuredPlanningWriter));
            var repaired = await StreamOrCompleteWithRetryAsync(
                new[] { ("system", system), ("user", user) },
                onDelta: null,
                ct).ConfigureAwait(false);
            repaired = RemoveTrailingModelEmittedSourceList((repaired ?? string.Empty).Trim());
            repaired = ReplaceWriterEvidenceIdReferencesWithCitations(
                repaired,
                writerToolResults,
                writerUserMessage,
                language,
                stripExistingInlineSourceCitations: true,
                removeUnresolvedEvidenceIds: true);
            if (LooksLikeCitationOnlyStructuredPlanningAnswer(repaired, writerUserMessage))
            {
                EmitRagTrace(
                    "writer.structured_citation_repair.rejected_citation_only",
                    ("answer_chars", repaired.Length),
                    ("citation_mentions", CountVisibleSourceCitationMentionsForStructuredPlanning(repaired)));
                return string.Empty;
            }

            var visible = TryGetVisibleSourceCitedStructuredPlanningSources(
                repaired,
                writerToolResults,
                writerUserMessage,
                out var visibleSources,
                language);
            EmitRagTrace(
                "writer.structured_citation_repair.end",
                ("trace_path", "rag.writer.citation_repair"),
                ("trace_step", "end"),
                ("answer_chars", repaired.Length),
                ("visible_citations", visible),
                ("sources", visibleSources.Count),
                ("citation_mentions", CountVisibleSourceCitationMentionsForStructuredPlanning(repaired)));
            return visible ? repaired : string.Empty;
        }
        catch (Exception ex) when (IsLlmContextOverflowException(ex))
        {
            EmitRagTrace(
                "writer.structured_citation_repair.overflow",
                ("error", ex.Message),
                ("prompt_chars", system.Length + user.Length));
            return string.Empty;
        }
    }
}