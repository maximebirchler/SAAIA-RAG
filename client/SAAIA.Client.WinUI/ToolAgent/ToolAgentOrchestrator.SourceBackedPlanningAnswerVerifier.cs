using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool LooksLikeUnsupportedSourceBackedPlanningAnswer(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language)
    {
        if (string.IsNullOrWhiteSpace(answer)
            || string.IsNullOrWhiteSpace(query)
            || (!LooksLikeAnyDocumentaryPlanningRequest(query)
                && !ShouldGateStructuredSourceBackedPlanningCoverage(query)))
        {
            return false;
        }

        var analysis = AnalyzeSourceBackedPlanningAnswerSupport(answer, toolResults, query, language);
        return ShouldRejectUnsupportedPlanningAnswerForFinal(analysis, query);
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromSupportedPlanningAnswerItems(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language)
        => AnalyzeSourceBackedPlanningAnswerSupport(answer, toolResults, query, language).Sources.ToList();

    private static bool TryGetSupportedStructuredPlanningSources(
        string? answer,
        ToolResults toolResults,
        string? query,
        string language,
        out List<ToolMemory.SourceRef> sources,
        out PlanningAnswerSupportAnalysis analysis)
    {
        sources = new List<ToolMemory.SourceRef>();
        analysis = PlanningAnswerSupportAnalysis.Empty;

        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        analysis = AnalyzeSourceBackedPlanningAnswerSupport(answer, toolResults, query, language);
        if (ShouldRejectUnsupportedPlanningAnswerForFinal(analysis, query)
            || analysis.Sources.Count == 0)
        {
            return false;
        }

        sources = analysis.Sources.ToList();
        return true;
    }

    private static bool TryGetVisibleSourceCitedStructuredPlanningSources(
        string? answer,
        ToolResults toolResults,
        string? query,
        out List<ToolMemory.SourceRef> sources,
        string language = "fr")
    {
        sources = new List<ToolMemory.SourceRef>();
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var candidateAnswer = RemoveTrailingModelEmittedSourceList(answer).Trim();
        if (string.IsNullOrWhiteSpace(candidateAnswer)
            || LooksLikeWriterControlLeak(candidateAnswer)
            || !LooksLikeConcreteStructuredPlanningAnswer(candidateAnswer))
        {
            return false;
        }

        if (LooksLikeCitationOnlyStructuredPlanningAnswer(candidateAnswer, query))
            return false;

        if (ContainsUnresolvedStructuredPlanningEvidenceIds(candidateAnswer)
            || LooksLikeUnsupportedStructuredPlanningRandomization(candidateAnswer, query)
            || LooksLikeOverCitedStructuredPlanningAnswer(candidateAnswer, query))
        {
            return false;
        }

        var sourcePool = BuildCanonicalStructuredPlanningSupportSourcePool(
            toolResults,
            query,
            language);
        if (sourcePool.Count == 0)
            return false;

        var citedSources = ReconcileRequiredVisibleSourcesWithFinalAnswer(
            answer,
            sourcePool,
            query);
        if (citedSources.Count == 0)
            return false;

        var distinctCitedSources = citedSources
            .GroupBy(BuildVisibleSourceCitationDiversityKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();

        if (!HasEnoughConcreteVisibleStructuredPlanningItems(candidateAnswer, query))
            return false;

        if (!HasEnoughVisibleCitedSourceDiversityForStructuredPlanning(candidateAnswer, query, distinctCitedSources))
            return false;

        sources = distinctCitedSources
            .Take(Math.Clamp(Math.Max(ResolveSourceBackedPlanningTargetItemCount(query), distinctCitedSources.Count), 1, 32))
            .ToList();
        return true;
    }

    private static string BuildVisibleSourceCitationDiversityKey(ToolMemory.SourceRef source)
    {
        var visiblePage = Math.Max(1, source.PageStart);
        var path = NormalizeVisibleSourcePathIdentity(source.DocPath);
        if (!string.IsNullOrWhiteSpace(path))
            return $"path:{path}|p:{visiblePage}";

        var docName = NormalizeLexicalLookup(source.DocName);
        if (!string.IsNullOrWhiteSpace(docName))
            return $"name:{docName}|p:{visiblePage}";

        var docId = CollapseWhitespace(source.DocId ?? string.Empty).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(docId))
            return $"id:{docId}|p:{visiblePage}";

        var label = NormalizeLexicalLookup(source.Label);
        return string.IsNullOrWhiteSpace(label)
            ? $"unknown|p:{visiblePage}"
            : $"label:{label}|p:{visiblePage}";
    }

    private static bool HasEnoughVisibleCitedSourceDiversityForStructuredPlanning(
        string answer,
        string? query,
        IReadOnlyList<ToolMemory.SourceRef> citedSources)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return true;

        if (!HasMinimumRequestedStructureForVisibleCitedStructuredPlanning(answer, query))
            return false;

        var distinctSourceCount = citedSources
            .GroupBy(BuildVisibleSourceRefDedupeKey, StringComparer.OrdinalIgnoreCase)
            .Count();
        if (distinctSourceCount <= 0)
            return false;

        var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(query);
        if (RequiresCompleteStructuredPlanningGrid(query)
            && HasAllRequestedVisibleDayMarkers(answer, query))
        {
            if (ContainsStructuredPlanningCompletionPlaceholder(answer))
            {
                var partialConcreteItemCount = CountConcreteVisibleStructuredPlanningItems(answer);
                if (partialConcreteItemCount < ResolveMinimumPartialStructuredPlanningItemCount(query))
                    return false;

                return distinctSourceCount >= ResolveMinimumPartialStructuredPlanningDistinctSourceCount(
                    query,
                    partialConcreteItemCount);
            }

            return distinctSourceCount >= targetItemCount
                && CountConcreteVisibleStructuredPlanningItems(answer) >= targetItemCount
                && !ContainsStructuredPlanningCompletionPlaceholder(answer);
        }

        if (targetItemCount < 8)
            return true;

        var concreteItemCount = CountConcreteVisibleStructuredPlanningItems(answer);
        var looksLikeFilledLargeStructure = concreteItemCount >= Math.Max(8, targetItemCount);
        if (!looksLikeFilledLargeStructure)
            return distinctSourceCount >= Math.Min(3, targetItemCount);

        var minimumDistinctSources = Math.Clamp(targetItemCount / 2, 3, 10);
        return distinctSourceCount >= minimumDistinctSources;
    }

    private static bool HasEnoughConcreteVisibleStructuredPlanningItems(string? answer, string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return true;

        var concreteItemCount = CountConcreteVisibleStructuredPlanningItems(answer);
        if (concreteItemCount <= 0)
            return false;

        var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(query);
        if (RequiresCompleteStructuredPlanningGrid(query)
            && HasAllRequestedVisibleDayMarkers(answer, query))
        {
            if (ContainsStructuredPlanningCompletionPlaceholder(answer))
                return concreteItemCount >= ResolveMinimumPartialStructuredPlanningItemCount(query);

            return concreteItemCount >= targetItemCount
                && !ContainsStructuredPlanningCompletionPlaceholder(answer);
        }

        if (targetItemCount < 8)
            return true;

        var minimumConcreteItems = Math.Min(
            targetItemCount,
            Math.Max(3, (int)Math.Ceiling(targetItemCount * 0.40d)));
        return concreteItemCount >= minimumConcreteItems;
    }

    private static int ResolveMinimumPartialStructuredPlanningItemCount(string? query)
    {
        var targetItemCount = Math.Max(1, ResolveSourceBackedPlanningTargetItemCount(query));
        if (targetItemCount < 8)
            return 1;

        return Math.Min(
            targetItemCount,
            Math.Max(3, (int)Math.Ceiling(targetItemCount * 0.40d)));
    }

    private static int ResolveMinimumPartialStructuredPlanningDistinctSourceCount(
        string? query,
        int concreteItemCount)
        => Math.Min(
            Math.Max(1, concreteItemCount),
            ResolveMinimumPartialStructuredPlanningItemCount(query));

    private static int CountConcreteVisibleStructuredPlanningItems(string? answer)
        => string.IsNullOrWhiteSpace(answer)
            ? 0
            : ExtractConcretePlanningAnswerItems(RemoveTrailingModelEmittedSourceList(answer)).Count;

    private static int CountVisibleSourceCitationMentionsForStructuredPlanning(string? answer)
    {
        var text = RemoveTrailingModelEmittedSourceList(answer ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var sourceMentions = Regex.Matches(
            text,
            @"(?:\(\s*sources?\s*:\s*[^)\r\n]{1,180}\bp\.?\s*\d+(?:\s*[-â€“]\s*\d+)?[^)\r\n]*\)|\[\s*sources?\s*:\s*[^\]\r\n]{1,180}\bp\.?\s*\d+(?:\s*[-â€“]\s*\d+)?[^\]\r\n]*\])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        var evidenceIdMentions = Regex.Matches(
            text,
            @"(?<![A-Za-z0-9])\[E\d{1,3}\](?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        return sourceMentions + evidenceIdMentions;
    }

    private static bool HasMinimumRequestedStructureForVisibleCitedStructuredPlanning(string answer, string? query)
    {
        var requestedDayCount = DetectRequestedDayAxisLabels(query, "en").Count;
        if (requestedDayCount < 2)
            return true;

        var targetItemCount = ResolveSourceBackedPlanningTargetItemCount(query);
        if (targetItemCount < 8)
            return true;

        var requiredVisibleDayMarkers = Math.Min(2, requestedDayCount);
        return CountDistinctVisibleWeekdayMarkers(answer) >= requiredVisibleDayMarkers;
    }

    private static int CountDistinctVisibleWeekdayMarkers(string? text)
    {
        var normalized = NormalizeLexicalLookup(RemoveTrailingModelEmittedSourceList(text ?? string.Empty));
        if (string.IsNullOrWhiteSpace(normalized))
            return 0;

        var weekdayAliases = new[]
        {
            new[] { "lundi", "monday", "lunes", "segunda", "montag", "lunedi" },
            new[] { "mardi", "tuesday", "martes", "terca", "dienstag", "martedi" },
            new[] { "mercredi", "wednesday", "miercoles", "quarta", "mittwoch", "mercoledi" },
            new[] { "jeudi", "thursday", "jueves", "quinta", "donnerstag", "giovedi" },
            new[] { "vendredi", "vrendredi", "friday", "viernes", "sexta", "freitag", "venerdi" },
            new[] { "samedi", "saturday", "sabado", "samstag", "sabato" },
            new[] { "dimanche", "sunday", "domingo", "sonntag", "domenica" }
        };

        return weekdayAliases.Count(group => group.Any(alias => Regex.IsMatch(
            normalized,
            @"\b" + Regex.Escape(alias) + @"\b",
            RegexOptions.CultureInvariant)));
    }

    private static bool HasAllRequestedVisibleDayMarkers(string? answer, string? query)
    {
        var requestedDayCount = ResolveRequestedStructuredPlanningDayAxisCount(query);
        return requestedDayCount > 0
            && CountDistinctVisibleWeekdayMarkers(answer) >= requestedDayCount;
    }

    private static int ResolveRequestedStructuredPlanningDayAxisCount(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0;

        return new[] { "fr", "en", "es", "pt", "de", "it" }
            .Select(language => DetectRequestedDayAxisLabels(query, language).Count)
            .DefaultIfEmpty(0)
            .Max();
    }
}
