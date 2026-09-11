#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    internal static string RemoveTrailingModelEmittedSourceListForTests(string answer)
        => RemoveTrailingModelEmittedSourceList(answer);

    internal static bool ShouldUseWriterForBroadSourceBackedPlanningForTests(ToolResults toolResults, string query)
        => ShouldUseWriterForBroadSourceBackedPlanning(toolResults, query);

    internal static bool ShouldUseLlmSourceBackedEvidencePlannerForTests(ToolResults toolResults, string query, string language)
        => ShouldUseLlmSourceBackedEvidencePlanner(query, AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language));

    internal static bool ShouldAllowWriterForPartialSourceBackedPlanningForTests(ToolResults toolResults, string query, string language = "fr")
        => ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, query, language);

    internal static bool ShouldAllowStructuredPlanningWriterRepairFromVisibleSourceInventoryForTests(ToolResults toolResults, string query)
        => ShouldAllowStructuredPlanningWriterRepairFromVisibleSourceInventory(toolResults, query);

    internal static bool IsSourceBackedPlanningCoverageAdequateForTests(ToolResults toolResults, string query, string language = "fr")
        => EvaluateSourceBackedPlanningCoverage(toolResults, query, language).IsAdequate;

    internal static (
        int CandidateCount,
        int DistinctSourcePages,
        int MinimumCandidates,
        int TargetSlots,
        bool HasRequiredAnchor,
        int RichEvidenceCount,
        int EvidenceRichnessScore,
        bool IsAdequate,
        int Score) SourceBackedPlanningCoverageSnapshotForTests(ToolResults toolResults, string query, string language = "fr")
    {
        var coverage = EvaluateSourceBackedPlanningCoverage(toolResults, query, language);
        return (
            coverage.CandidateCount,
            coverage.DistinctSourcePages,
            coverage.MinimumCandidates,
            coverage.TargetSlots,
            coverage.HasRequiredAnchor,
            coverage.RichEvidenceCount,
            coverage.EvidenceRichnessScore,
            coverage.IsAdequate,
            coverage.Score);
    }

    internal static bool HasStructuredSourceBackedPlanningTargetCandidateCoverageForStopForTests(ToolResults toolResults, string query, string language = "fr")
        => HasStructuredSourceBackedPlanningTargetCandidateCoverageForStop(
            AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language),
            query);

    internal static bool ShouldUseWriterForBroadSourceBackedSynthesisForTests(ToolResults toolResults, string query)
        => ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, query);

    internal static bool ShouldPreferWriterForPolishedSourceBackedAnswerForTests(ToolResults toolResults, string query)
        => ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, query);

    internal static bool ShouldRouteSourceBackedAnswerThroughWriterForTests(ToolResults toolResults, string query, string language = "fr")
        => ShouldRouteSourceBackedAnswerThroughWriter(toolResults, query, language);

    internal static bool ShouldAllowSourceBackedWriterRepairForCurrentTurnForTests(string query)
        => ShouldAllowSourceBackedWriterRepairForCurrentTurn(query);

    internal static bool ShouldRequireWriterForBroadDocumentaryFinalForTests(ToolResults toolResults, string query, string language = "fr")
        => ShouldRequireWriterForBroadDocumentaryFinal(toolResults, query, language);

    internal static string TryBuildInsufficientStructuredPlanningBeforeWriterAnswerForTests(ToolResults toolResults, string query, string language = "fr")
        => TryBuildInsufficientStructuredPlanningBeforeWriterAnswer(toolResults, query, language);

    internal static string ResolveStructuredPlanningWriterGuardBasisForTests(
        ToolResults rawToolResults,
        ToolResults writerToolResults,
        string query,
        string language = "fr")
        => ResolveStructuredPlanningWriterGuardToolResults(rawToolResults, writerToolResults, query, language).Basis;

    internal static bool RequiresStructuredSourceBackedPlanningCoverageForTests(string query)
        => RequiresStructuredSourceBackedPlanningCoverage(query);

    internal static bool ShouldGateStructuredSourceBackedPlanningCoverageForTests(string query)
        => ShouldGateStructuredSourceBackedPlanningCoverage(query);

    internal static string BuildSourceBackedStructureHintsForTests(
        ToolResults toolResults,
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        string query,
        string language = "fr")
        => BuildSourceBackedStructureHintsForPrompt(toolResults, lastSourcesUsed, query, language);

    internal static bool ShouldUseAdvisoryEvidenceGuardForBroadSynthesisForTests(ToolResults toolResults, string query)
        => ShouldUseAdvisoryEvidenceGuardForBroadSynthesis(toolResults, query);

    internal static bool ShouldOfferBroadenedSourceSearchForTests(string query)
        => ShouldOfferBroadenedSourceSearch(query);

    internal static bool LooksLikeBroadEmptySourceSearchRequestForTests(string query)
        => LooksLikeBroadEmptySourceSearchRequest(query);

    internal static bool ContainsBroadenedSourceSearchOfferForTests(string answer)
        => ContainsBroadenedSourceSearchOffer(answer);

    internal static bool LooksLikeBroadenedSourceSearchConfirmationForTests(string userMessage)
        => LooksLikeBroadenedSourceSearchConfirmation(userMessage);

    internal static string BuildAnswerShapeGuidanceForWriterForTests(string query, string language)
        => BuildAnswerShapeGuidanceForWriter(query, language);

    internal static string BuildSourceBackedCoverageHintsForWriterForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCoverageHintsForWriter(toolResults, query, language);

    internal static string BuildSourceBackedWritingBriefForWriterForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedWritingBriefForWriter(toolResults, query, language);

    internal static string BuildSourceBackedResearchMapForWriterForTests(
        ToolResults toolResults,
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        string query,
        string language)
        => BuildSourceBackedResearchMapForWriter(toolResults, lastSourcesUsed, query, language);

    internal static string BuildSourceBackedRepairWriterUserPromptForTests(
        IReadOnlyList<(string role, string content)> chatHistory,
        string query,
        string language,
        ToolResults rawToolResults,
        ToolResults writerToolResults)
        => BuildSourceBackedRepairWriterUserPrompt(
            chatHistory,
            query,
            new RouterPlan { Intent = "rag.answer", Language = language, Mode = "auto" },
            rawToolResults,
            writerToolResults,
            Array.Empty<ToolMemory.SourceRef>());

    internal static string BuildCompactSourceBackedWriterSystemPromptForTests(
        string language,
        string mode = "auto",
        string style = "auto",
        bool repairMode = false,
        bool compactRetry = false)
        => BuildCompactSourceBackedWriterSystemPrompt(language, mode, style, repairMode, compactRetry);

    internal static string BuildOverflowRetryWriterUserPromptForTests(
        IReadOnlyList<(string role, string content)> chatHistory,
        string query,
        string language,
        ToolResults toolResults,
        int evidenceInventoryChars = 6800)
        => BuildOverflowRetryWriterUserPrompt(
            chatHistory,
            query,
            new RouterPlan { Intent = "rag.answer", Language = language, Mode = "auto" },
            toolResults,
            BuildSourceBackedWriterFollowupContextNote(query, language),
            evidenceInventoryChars);

    internal static string BuildWriterToolResultsPromptBlockForTests(
        ToolResults writerToolResults,
        string query,
        string language,
        bool useCleanSourceBrief)
        => BuildWriterToolResultsPromptBlock(writerToolResults, query, language, useCleanSourceBrief);

    internal static string BuildSourceBackedCandidateLeadsForWriterForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCandidateLeadsForWriter(toolResults, query, language);

    internal static string BuildSourceBackedCandidateLeadsForWriterForTests(
        ToolResults toolResults,
        string query,
        string language,
        int? maxItems,
        int? maxChars)
        => BuildSourceBackedCandidateLeadsForWriter(toolResults, query, language, maxItems, maxChars);

    internal static int? ResolveEvidenceInventoryItemLimitForPromptCharsForTests(int? maxChars, string query)
        => ResolveEvidenceInventoryItemLimitForPromptChars(maxChars, query);

    internal static bool ShouldRunSourceBackedCandidateAdjudicationForWriterForTests(ToolResults toolResults, string query, string language)
        => ShouldRunSourceBackedCandidateAdjudicationForWriter(toolResults, query, language);

    internal static string BuildSourceBackedCandidateAdjudicationSystemPromptForTests(string language)
        => BuildSourceBackedCandidateAdjudicationSystemPrompt(language);

    internal static string BuildSourceBackedCandidateAdjudicationUserPromptForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCandidateAdjudicationUserPrompt(toolResults, query, language);

    internal static string BuildSourceBackedCandidateAdjudicationUserPromptForTests(
        ToolResults toolResults,
        string query,
        string language,
        int contextTokens,
        int maxOutputTokens)
        => BuildSourceBackedCandidateAdjudicationUserPrompt(
            toolResults,
            query,
            language,
            CreateSourceBackedCandidateAdjudicationPromptBudget(
                CreateWriterPromptBudget(contextTokens, maxOutputTokens)));

    internal static string? NormalizeSourceBackedCandidateAdjudicationJsonForTests(string? raw)
        => NormalizeSourceBackedCandidateAdjudicationJsonForWriter(raw);

    internal static (
        string Decision,
        int UsefulCandidateCount,
        int MissingCount,
        bool RequestsMoreRetrieval,
        string[] MissingSlots) ParseSourceBackedCandidateAdjudicationSignalForTests(
            string? normalizedJson,
            string query,
            string language = "fr")
    {
        if (!TryBuildSourceBackedCandidateAdjudicationSignal(
                normalizedJson,
                query,
                language,
                out var signal))
        {
            return ("none", 0, 0, false, Array.Empty<string>());
        }

        return (
            signal.Decision,
            signal.UsefulCandidateCount,
            signal.MissingCount,
            signal.RequestsMoreRetrieval,
            signal.MissingSlots.ToArray());
    }

    internal static string[] NormalizeLlmAdjudicatedMissingSlotsForRequestForTests(
        IEnumerable<string> rawMissingSlots,
        string query,
        string language = "fr")
        => NormalizeLlmAdjudicatedMissingSlotsForRequest(rawMissingSlots, query, language).ToArray();

    internal static bool LooksLikeRawExcerptDumpPlanningAnswerForTests(string answer, string query)
        => LooksLikeRawExcerptDumpPlanningAnswer(answer, query);

    internal static bool LooksLikeWriterControlLeakForTests(string answer)
        => LooksLikeWriterControlLeak(answer);

    internal static bool LooksLikePoorPlanningFallbackAnswerForTests(string answer, string query)
        => LooksLikePoorPlanningFallbackAnswer(answer, query);

    internal static string BuildSourceBackedOptionAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedOptionAnswer(toolResults, language, query: query);

    internal static string TryBuildMissingBroadCompositionAnchorAnswerForTests(ToolResults toolResults, string query, string language)
        => TryBuildMissingBroadCompositionAnchorAnswer(toolResults, query, language);

    internal static string TryBuildMissingRequiredEvidenceAnswerForTests(ToolResults toolResults, string query, string language)
        => TryBuildMissingRequiredEvidenceAnswer(toolResults, query, language);

    internal static string TryBuildBackendGuidanceClarificationAnswerForTests(ToolResults toolResults, string language, string query = "")
        => TryBuildBackendGuidanceClarificationAnswer(toolResults, query, language);

    internal static bool ShouldPreferSourceBackedAnswerOverBackendClarificationForTests(ToolResults toolResults, string query)
        => ShouldPreferSourceBackedAnswerOverBackendClarification(toolResults, query);

    internal static string BuildRagEvidenceFallbackAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedSafeFallbackAnswer(toolResults, query, language, shouldAvoidRaw: true);

    internal static string BuildSourceBackedSafeFallbackAnswerForTests(ToolResults toolResults, string query, string language, bool shouldAvoidRaw)
        => BuildSourceBackedSafeFallbackAnswer(toolResults, query, language, shouldAvoidRaw);

    internal static string BuildSourceBackedSafeFallbackAfterRejectedWriterForTests(
        ToolResults toolResults,
        string query,
        string writerQuery,
        string language)
        => BuildSourceBackedSafeFallbackAfterRejectedWriter(toolResults, query, writerQuery, language);

    internal static string BuildReadableSourceBackedCandidateListFallbackAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildReadableSourceBackedCandidateListFallbackAnswer(toolResults, query, language);

    internal static string BuildReadablePartialPlanningEvidenceAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildReadablePartialPlanningEvidenceAnswer(
            EnumerateRagHitSummaries(toolResults).ToList(),
            query,
            language);

    internal static string TryBuildSourcePolicyGuardAnswerForTests(ToolResults toolResults, string query, string language)
        => TryBuildSourcePolicyGuardAnswer(toolResults, query, language);

    internal static bool LooksLikeDocumentInstructionPolicyRequestForTests(string query)
        => LooksLikeDocumentInstructionPolicyRequest(query);

    internal static bool LooksLikeDocumentVersionTraceabilityRequestForTests(string query)
        => LooksLikeDocumentVersionTraceabilityRequest(query);

    internal static string BuildMissingExplicitDocumentAnswerForTests(string language, string requestedDocument)
        => BuildMissingExplicitDocumentAnswer(language, requestedDocument, Array.Empty<RagHitSummary>());

    internal static string BuildSourcePolicyRetrievalQueryForTests(string query)
        => BuildSourcePolicyRetrievalQuery(query);

    internal static string[] DeriveSourceBackedOptionSourceLabelsForTests(ToolResults toolResults, string query)
        => DeriveSourcesFromOptionHits(toolResults, query).Select(source => source.Label).ToArray();

    internal static bool LooksLikeSourceBackedOptionRequestForTests(string query)
        => LooksLikeSourceBackedOptionRequest(query);

    internal static bool ShouldAvoidDeterministicSourceBackedOptionFallbackForTests(string query)
        => ShouldAvoidDeterministicSourceBackedOptionFallback(query);

    internal static bool LooksLikeSourceBackedCountdownPlanningRequestForTests(string query)
        => LooksLikeSourceBackedCountdownPlanningRequest(query);

    internal static string BuildSourceBackedCountdownPlanningAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCountdownPlanningAnswer(toolResults, query, language);

    internal static string[] DeriveSourceBackedCountdownSourceLabelsForTests(ToolResults toolResults, string query)
        => DeriveSourcesFromCountdownPlanningHits(toolResults, query).Select(source => source.Label).ToArray();
}
#endif
