#if DEBUG || SAAIA_TEST_HOOKS
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
    internal static string BuildSourceBackedExtractiveHeaderForTests(string language, bool noExplicitPairing)
        => BuildSourceBackedExtractiveHeader(language, noExplicitPairing);

    internal static string BuildSourceBackedPlanningAnswerForTests(ToolResults toolResults, string language, string? query = null)
        => BuildSourceBackedPlanningAnswer(toolResults, language, query: query);

    internal static string[] BuildSourceBackedPlanningDraftSourceKeysForTests(ToolResults toolResults, string language, string? query = null)
    {
        var draft = BuildSourceBackedPlanningDraft(toolResults, language, query: query);
        return draft.Sources
            .Select(static source => $"{source.DocPath}|{source.PageStart}|{source.PageEnd}")
            .ToArray();
    }

    internal static (
        bool Applied,
        string Answer,
        int SourceCount,
        int ItemCount,
        int SupportedItemCount,
        int UnsupportedItemCount,
        int CandidateCount) TryBuildSupportedStructuredPlanningAnswerForTests(
            ToolResults toolResults,
            string language,
            string? query,
            bool allowPartialStructuredPlanningRebuild = false)
    {
        var applied = TryBuildSupportedStructuredPlanningAnswer(
            toolResults,
            language,
            query,
            out var answer,
            out var sources,
            out var analysis,
            allowPartialStructuredPlanningRebuild);

        return (
            applied,
            answer,
            sources.Count,
            analysis.ItemCount,
            analysis.SupportedItemCount,
            analysis.UnsupportedItemCount,
            analysis.CandidateCount);
    }

    internal static bool ShouldTreatStructuredPlanningRejectionAsTerminalForTests(string? answerSource, string? query)
        => ShouldTreatStructuredPlanningRejectionAsTerminal(answerSource, query);

    internal static string ExtractPlanItemTitleV2ForTests(string text)
        => ExtractPlanItemTitleV2(text);

    internal static bool ExactItemTextMatchesRequestOrStructureForTests(string requestedTitle, string text)
        => ExactItemTextMatchesRequestOrStructure(requestedTitle, text);

    internal static string TrimAfterLikelyExactItemBoundaryForTests(string text)
        => TrimAfterLikelyExactItemBoundary(text);

    internal static string FormatSourceBackedPlanningDisplayTitleForTests(string title)
        => HumanizeSourceBackedDisplayTitle(CleanSourceBackedOptionTitle(title));

    internal static bool LooksLikeNoisyStructuredPlanningCandidateTitleForTests(string title)
        => LooksLikeNoisyStructuredPlanningCandidateTitle(title);

    internal static string[] BuildPlanningExplorationRetrievalQueriesForTests(string query)
        => BuildPlanningExplorationRetrievalQueries(query);

    internal static string[] BuildSourceBackedCandidateDiscoveryRetrievalQueriesForTests(string query)
        => BuildSourceBackedCandidateDiscoveryRetrievalQueries(query);

    internal static string[] BuildInitialSourceBackedPlanningProbeQueriesForTests(string query)
        => BuildInitialSourceBackedPlanningProbeQueries(query);

    internal static string NormalizeInitialSourceBackedPlanningProbeFamilyKeyForTests(string query)
        => NormalizeInitialSourceBackedPlanningProbeFamilyKey(query);

    internal static bool ShouldExpandSourceBackedPlanningRetrievalForTests(ToolResults toolResults, string query, string language)
        => ShouldExpandSourceBackedPlanningRetrieval(toolResults, query, language);

    internal static bool ShouldRespectLlmRouterGeneralWithoutToolsForTests(RouterPlan plan)
        => ShouldRespectLlmRouterGeneralWithoutTools(plan);

    internal static string[] SourceBackedPlanningCandidateTitlesForTests(ToolResults toolResults, string query, string language, int maxItems = 32)
        => SelectSourceBackedPlanningCandidates(toolResults, query, maxItems, language)
            .Select(static candidate => candidate.Title)
            .ToArray();

    internal static string[] StrictSourceBackedOptionTitlesForTests(ToolResults toolResults, string query)
        => EnumerateRagHitSummaries(toolResults)
            .SelectMany(hit => ExtractStrictSourceBackedOptionTitles(hit, query))
            .ToArray();

    internal static string[] BuildSourceBackedPlanningTraceLinesForTests(ToolResults toolResults, string query, string language = "fr")
        => BuildSourceBackedPlanningTraceLines(toolResults, query, language);

    internal static string BuildWriterEvidenceRosterForCitationMappingForTests(
        ToolResults toolResults,
        string query,
        string language = "fr",
        bool allowVisibleSourceFallback = true,
        bool allowDiagnosticRows = true)
        => BuildWriterEvidenceRosterForCitationMapping(
            toolResults,
            query,
            language,
            allowVisibleSourceFallback,
            allowDiagnosticRows);

    internal static (string[] CandidateTitles, string[] InventoryPreview) BuildSourceBackedResearchInventorySnapshotForTests(
        ToolResults toolResults,
        string query,
        string language = "fr")
    {
        var analysis = AnalyzeSourceBackedEvidenceSufficiency(toolResults, query, language);
        var snapshot = BuildSourceBackedResearchInventorySnapshot(toolResults, query, language, analysis);
        return (snapshot.CandidateTitles, snapshot.InventoryPreview);
    }

    internal static string BuildRagTraceLineForTests(string eventName, params (string Key, object? Value)[] fields)
        => BuildRagTraceLine(eventName, "test-trace", 7, 123, fields);

    internal static bool IsBetterSourceBackedPlanningCoverageForTests(
        ToolResults current,
        ToolResults candidate,
        string query,
        string language)
        => IsBetterSourceBackedPlanningCoverage(current, candidate, query, language);

    internal static bool LooksLikeUnsupportedSourceBackedPlanningAnswerForTests(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language)
        => LooksLikeUnsupportedSourceBackedPlanningAnswer(answer, toolResults, query, language);

    internal static (int ItemCount, int SupportedItemCount, int UnsupportedItemCount, int CandidateCount, int SourceCount)
        AnalyzeSourceBackedPlanningAnswerSupportStatsForTests(
            string? answer,
            ToolResults toolResults,
            string? query,
            string language)
    {
        var analysis = AnalyzeSourceBackedPlanningAnswerSupport(answer, toolResults, query, language);
        return (
            analysis.ItemCount,
            analysis.SupportedItemCount,
            analysis.UnsupportedItemCount,
            analysis.CandidateCount,
            analysis.Sources.Count);
    }

    internal static (
        bool Accepted,
        int SourceCount,
        string[] SourceKeys) TryGetVisibleSourceCitedStructuredPlanningSourcesForTests(
            string? answer,
            ToolResults toolResults,
            string? query)
    {
        var accepted = TryGetVisibleSourceCitedStructuredPlanningSources(
            answer,
            toolResults,
            query,
            out var sources);
        return (
            accepted,
            sources.Count,
            sources
                .Select(static source => $"{source.DocPath}|{source.PageStart}|{source.PageEnd}")
                .ToArray());
    }

    internal static string ReplaceWriterEvidenceIdReferencesWithCitationsForTests(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language,
        bool stripExistingInlineSourceCitations = false,
        bool removeUnresolvedEvidenceIds = false)
        => ReplaceWriterEvidenceIdReferencesWithCitations(
            answer,
            toolResults,
            query,
            language,
            stripExistingInlineSourceCitations,
            removeUnresolvedEvidenceIds);

    internal static bool LooksLikeCitationOnlyStructuredPlanningAnswerForTests(string? answer, string? query)
        => LooksLikeCitationOnlyStructuredPlanningAnswer(answer, query);

    internal static string BuildStructuredPlanningRepairFeedbackForTests(
        string? previousAnswer,
        ToolResults toolResults,
        string? query,
        string language)
        => BuildStructuredPlanningRepairFeedback(previousAnswer, toolResults, query, language);

    internal static string[] ExtractConcretePlanningAnswerItemsForTests(string answer)
        => ExtractConcretePlanningAnswerItems(answer).ToArray();

    internal static bool ShouldRejectUnsupportedPlanningAnswerForFinalForTests(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language)
        => ShouldRejectUnsupportedPlanningAnswerForFinal(
            AnalyzeSourceBackedPlanningAnswerSupport(answer, toolResults, query, language),
            query);

    internal static (
        bool Applied,
        string Answer,
        int SourceCount,
        string[] SourceKeys,
        string Resolution,
        int ItemCount,
        int SupportedItemCount,
            int UnsupportedItemCount,
            int CandidateCount) FinalizeSourceBackedPlanningResponseForTests(
            string? answer,
            ToolResults toolResults,
            string? query,
            string language,
            bool allowDeterministicStructuredPlanningRebuild = true)
    {
        var applied = TryFinalizeSourceBackedPlanningResponse(
            answer,
            toolResults,
            query,
            language,
            out var finalAnswer,
            out var sources,
            out var analysis,
            out var resolution,
            allowDeterministicStructuredPlanningRebuild);

        return (
            applied,
            finalAnswer,
            sources.Count,
            sources
                .Select(static source => $"{source.DocPath}|{source.PageStart}|{source.PageEnd}")
                .ToArray(),
            resolution,
            analysis.ItemCount,
            analysis.SupportedItemCount,
            analysis.UnsupportedItemCount,
            analysis.CandidateCount);
    }
}
#endif
