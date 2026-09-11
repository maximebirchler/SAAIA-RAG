using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record AnswerWriterContext(
        ToolResults WriterToolResults,
        ToolResults WriterEvidenceToolResults,
        bool UseCleanSourceBrief,
        string WriterSystem,
        string UserPrompt,
        (string role, string content)[] WriterMessages,
        string? CandidateAdjudicationJson);

    private async Task<AnswerWriterContext> BuildAnswerWriterContextAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        string writerUserMessage,
        RouterPlan plan,
        ToolResults toolResults,
        ToolResults writerToolResults,
        ToolResults writerEvidenceToolResults,
        WriterPromptBudget writerPromptBudget,
        string? inventoryRenderedDataJson,
        bool useGeneralChatPrompt,
        string system,
        bool shouldUseBroadSourceBackedSynthesis,
        bool shouldAvoidRawSourceBackedFallback,
        bool shouldRouteSourceBackedAnswerThroughWriter,
        bool shouldRequireWriterForBroadDocumentaryFinal,
        CancellationToken ct)
    {
        var useCleanSourceBrief = ShouldUseCleanSourceBackedWriterPrompt(plan, writerToolResults, writerUserMessage);
        ClientLog.Info(
            "ToolAgent answer writer selected: " +
            $"intent={plan.Intent}|broad={shouldUseBroadSourceBackedSynthesis}|avoidRaw={shouldAvoidRawSourceBackedFallback}|" +
            $"route={shouldRouteSourceBackedAnswerThroughWriter}|requireBroadFinal={shouldRequireWriterForBroadDocumentaryFinal}|cleanBrief={useCleanSourceBrief}");
        EmitRagTrace(
            "writer.selected",
            ("intent", plan.Intent),
            ("broad", shouldUseBroadSourceBackedSynthesis),
            ("avoid_raw", shouldAvoidRawSourceBackedFallback),
            ("route", shouldRouteSourceBackedAnswerThroughWriter),
            ("require_broad_final", shouldRequireWriterForBroadDocumentaryFinal),
            ("clean_brief", useCleanSourceBrief));
        var writerSystem = useGeneralChatPrompt
            ? system
            : useCleanSourceBrief
                ? BuildCompactSourceBackedWriterSystemPrompt(
                    plan.Language,
                    plan.Mode,
                    LocalizedStrings.NormalizeStyle(_mem.LastStyle))
                : system;
        var candidateAdjudicationJson = await TryBuildSourceBackedCandidateAdjudicationForWriterAsync(
            writerEvidenceToolResults,
            writerUserMessage,
            plan.Language,
            writerPromptBudget,
            ct).ConfigureAwait(false);
        if (await TryExpandSourceBackedEvidenceAfterCandidateAdjudicationAsync(
                toolResults,
                plan,
                writerUserMessage,
                plan.Language,
                candidateAdjudicationJson,
                ct).ConfigureAwait(false))
        {
            writerToolResults = BuildWriterToolResultsForRuntime(plan, toolResults, writerUserMessage, writerPromptBudget);
            _lastWriterToolNames = writerToolResults.Items.Select(x => x.ToolName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var refreshedPlanningGuardSelection = ResolveStructuredPlanningWriterGuardToolResults(
                toolResults,
                writerToolResults,
                writerUserMessage,
                plan.Language);
            writerEvidenceToolResults = refreshedPlanningGuardSelection.ToolResults;
            EmitRagTrace(
                "writer.context.refreshed_after_candidate_adjudication_expansion",
                ("writer_tools", writerToolResults.Items.Count),
                ("writer_tool_names", _lastWriterToolNames.ToArray()),
                ("evidence_basis", refreshedPlanningGuardSelection.Basis),
                ("evidence_tool_items", writerEvidenceToolResults.Items.Count));
            candidateAdjudicationJson = await TryBuildSourceBackedCandidateAdjudicationForWriterAsync(
                writerEvidenceToolResults,
                writerUserMessage,
                plan.Language,
                writerPromptBudget,
                ct).ConfigureAwait(false);
        }

        var structuredPlanningWriter = ShouldGateStructuredSourceBackedPlanningCoverage(writerUserMessage);
        var evidenceInventoryChars = ResolveWriterEvidenceInventoryPromptChars(writerPromptBudget, structuredPlanningWriter);
        var sourceReferenceIndexChars = Math.Min(evidenceInventoryChars, structuredPlanningWriter ? 2400 : 1200);
        var candidateAdjudicationForWriterPrompt = BuildCompactSourceBackedCandidateAdjudicationForRepairPrompt(
            candidateAdjudicationJson,
            writerUserMessage,
            plan.Language,
            structuredPlanningWriter);
        var privateSourceEvidenceInventory = BuildSourceBackedCandidateLeadsForWriter(
            writerEvidenceToolResults,
            writerUserMessage,
            plan.Language,
            maxItems: ResolveEvidenceInventoryItemLimitForPromptChars(evidenceInventoryChars, writerUserMessage),
            maxChars: evidenceInventoryChars);
        EmitRagTrace(
            "writer.evidence_inventory.roster",
            ("trace_path", "rag.writer.evidence_inventory"),
            ("trace_step", "build_roster"),
            ("basis_tool_items", writerEvidenceToolResults.Items.Count),
            ("roster_chars", privateSourceEvidenceInventory.Length),
            ("evidence_items", CountEvidenceInventoryRows(privateSourceEvidenceInventory)),
            ("candidate_rejected", CountEvidenceInventoryRows(privateSourceEvidenceInventory, "candidate_rejected")),
            ("source_pages", CountEvidenceInventoryRows(privateSourceEvidenceInventory, "source_page")),
            ("candidate_adjudication_prompt_chars", candidateAdjudicationForWriterPrompt.Length),
            ("source_reference_index_chars", sourceReferenceIndexChars));
        var toolResultsPromptBlock = BuildWriterToolResultsPromptBlock(
            writerToolResults,
            writerUserMessage,
            plan.Language,
            useCleanSourceBrief,
            sourceReferenceIndexChars: sourceReferenceIndexChars,
            sourceReferenceToolResults: writerEvidenceToolResults);

        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 10)}

USER_MESSAGE:
{writerUserMessage}

PRIVATE_USER_FOLLOWUP_CONTEXT:
{BuildSourceBackedWriterFollowupContextNote(userMessage, plan.Language)}

ANSWER_SHAPE_GUIDANCE:
{BuildAnswerShapeGuidanceForWriter(writerUserMessage, plan.Language)}

STRUCTURED_PLANNING_WRITER_CONTRACT:
{BuildStructuredPlanningWriterContractForWriter(writerUserMessage, plan.Language)}

PRIVATE_SOURCE_COVERAGE_NOTE:
{TruncateForPrompt(BuildSourceBackedCoverageHintsForWriter(writerToolResults, writerUserMessage, plan.Language), writerPromptBudget.CoverageNoteChars)}

PRIVATE_SOURCE_WRITING_BRIEF:
{TruncateForPrompt(BuildSourceBackedWritingBriefForWriter(writerToolResults, writerUserMessage, plan.Language), writerPromptBudget.WritingBriefChars)}

PRIVATE_SOURCE_RESEARCH_MAP:
{TruncateForPrompt(BuildSourceBackedResearchMapForWriter(toolResults, _mem.LastSourcesUsed, writerUserMessage, plan.Language), writerPromptBudget.ResearchMapChars)}

PRIVATE_SOURCE_CANDIDATE_ADJUDICATION (json):
{candidateAdjudicationForWriterPrompt}

PRIVATE_SOURCE_EVIDENCE_INVENTORY:
{TruncateForPrompt(privateSourceEvidenceInventory, evidenceInventoryChars)}

{toolResultsPromptBlock}

AUTHORITATIVE_INVENTORY_DATA (json):
{inventoryRenderedDataJson ?? "null"}
";

        var writerMessages = new[]
        {
            ("system", writerSystem),
            ("user", user)
        };

        return new AnswerWriterContext(
            writerToolResults,
            writerEvidenceToolResults,
            useCleanSourceBrief,
            writerSystem,
            user,
            writerMessages,
            candidateAdjudicationJson);
    }
}
