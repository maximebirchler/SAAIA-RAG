using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static int ComputeSourceBackedOptionTitleScore(string title, RagHitSummary hit, string? query)
    {
        var normalizedTitle = NormalizeLexicalLookup(title);
        var normalizedSection = NormalizeLexicalLookup($"{hit.SectionTitle} {hit.HeadingPath}");
        var normalizedQuery = NormalizeLexicalLookup(query);
        var score = 0;

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
            score += ComputeTypoTolerantRagHitLexicalRelevance(normalizedQuery, normalizedTitle) * 2;

        var wordCount = ExtractQuerySignalTerms(normalizedTitle).Count();
        if (wordCount is >= 2 and <= 7)
            score += 8;
        if (Regex.IsMatch(title, @"[\p{Lu}\u00c0-\u017f]", RegexOptions.CultureInvariant))
            score += 3;
        if (!string.IsNullOrWhiteSpace(normalizedSection) && normalizedSection.Contains(normalizedTitle, StringComparison.Ordinal))
            score -= 12;
        if (Regex.IsMatch(normalizedTitle, @"\b(?:document|section|categories?|modes?|preparation|operation|workflow|execution|pages?)\b", RegexOptions.CultureInvariant))
            score -= 16;
        if (LooksLikeGenericStructuredInventoryTitle(normalizedTitle))
            score -= 70;
        if (LooksLikeWeakSourceBackedOptionTitle(title))
            score -= 50;
        if (ContentCardEvidenceNamesDifferentStructuredPlanningItem(
                new SourceBackedOptionCandidate(hit, title, Score: 0, VisibleMinutes: null),
                normalizedTitle))
        {
            score -= 100;
        }
        if (LooksLikeReferenceAttributionSourceTitle(new SourceBackedOptionCandidate(hit, title, Score: 0, VisibleMinutes: null)))
            score -= 90;

        return score;
    }

    private static int ComputeSourceBackedOptionHitScore(
        RagHitSummary hit,
        string title,
        string? query,
        int? requestedMaxMinutes,
        int? visibleMinutes)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var evidence = NormalizeLexicalLookup(GetRagHitPrimaryEvidenceText(hit));
        var score = ComputeTypoTolerantRagHitLexicalRelevance(normalizedQuery, evidence);
        score += ComputeProcedureCompletenessCueScore(hit);
        score += ComputeStructuredProcedureEvidenceCueScore(hit);
        score += ComputeStructuredProcedureVisibleEvidenceCueScore(hit);
        score += ComputeSourceBackedOptionTitleScore(title, hit, query);

        if (requestedMaxMinutes.HasValue)
        {
            if (visibleMinutes.HasValue && visibleMinutes.Value <= requestedMaxMinutes.Value)
                score += 18;
            else if (visibleMinutes.HasValue)
                score -= 60;
            else
                score -= 3;
        }

        var queryTerms = ExtractQuerySignalTerms(normalizedQuery)
            .Where(static term => term.Length >= 4)
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Take(8)
            .ToArray();
        if (queryTerms.Length > 0 && QueryAnchorTermsMatchHit(queryTerms, hit))
            score += 12;

        if (hit.RetrievalQueryIndex == 0)
        {
            score += hit.RetrievalHitRank switch
            {
                0 => 18,
                1 => 12,
                2 => 8,
                _ => 4
            };
        }

        if (LooksLikeMidProcedureFragment(hit))
            score -= 8;

        return score;
    }

    private static string FormatSourceBackedOptionEvidence(RagHitSummary hit)
    {
        var evidence = CleanReadableProcedureArtifacts(FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 220));
        if (string.IsNullOrWhiteSpace(evidence))
            return string.Empty;

        var title = ExtractSourceBackedOptionTitle(hit, query: null);
        return string.Equals(NormalizeLexicalLookup(evidence), NormalizeLexicalLookup(title), StringComparison.Ordinal)
            ? string.Empty
            : evidence;
    }
}
