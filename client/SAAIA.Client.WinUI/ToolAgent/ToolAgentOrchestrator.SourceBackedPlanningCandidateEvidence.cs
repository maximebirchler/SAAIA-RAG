using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool SourceBackedPlanningCandidateMatchesDominantTopLevel(
        SourceBackedOptionCandidate candidate,
        string? dominantTopLevelScope)
    {
        if (string.IsNullOrWhiteSpace(dominantTopLevelScope))
            return true;

        var candidateTopLevel = ExtractTopLevelCategoryScope(candidate.Hit);
        return !string.IsNullOrWhiteSpace(candidateTopLevel)
            && string.Equals(candidateTopLevel, dominantTopLevelScope, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDirectSourceBackedPlanningCandidateEvidence(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        return PrimaryPageEvidenceSupportsSourceBackedPlanningCandidateTitle(candidate);
    }

    private static bool SourceBackedContextCandidateHasAnchoredFuzzyLocalStructuredProof(
        SourceBackedOptionCandidate candidate,
        string normalizedTitle)
    {
        if (!string.Equals(candidate.Hit.Retriever, "documents.context", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return false;
        }

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length is < 4 or > 8)
            return false;

        var titleIsAnchoredByContextSurface = EnumeratePlanningCandidateRawTitleSurfaces(candidate.Hit)
            .Concat(candidate.Hit.MatchedContentCards?.Select(static card => card.Title) ?? Enumerable.Empty<string>())
            .Select(CleanSourceBackedOptionTitle)
            .Select(NormalizeLexicalLookup)
            .Any(surface => string.Equals(surface, normalizedTitle, StringComparison.Ordinal));
        if (!titleIsAnchoredByContextSurface)
            return false;

        return PrimaryPageEvidenceContainsExactPlanningCandidateTitle(candidate);
    }

    private static bool SourceBackedContextTitleCanBypassNoisyStructuredPlanningTitle(
        RagHitSummary hit,
        string? title)
    {
        var cleaned = CleanSourceBackedOptionTitle(title);
        var normalizedTitle = NormalizeLexicalLookup(cleaned);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        return SourceBackedContextCandidateHasAnchoredFuzzyLocalStructuredProof(
            new SourceBackedOptionCandidate(hit, cleaned, Score: 0, VisibleMinutes: null),
            normalizedTitle);
    }

    private static bool PrimaryPageEvidenceContainsExactPlanningCandidateTitle(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var directEvidence = CollapseWhitespace(BuildPageLocalSourceBackedPlanningProofText(candidate.Hit));
        return directEvidence.Length >= 8
            && PrimaryEvidenceContainsLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, directEvidence);
    }

    private static bool PrimaryPageEvidenceSupportsSourceBackedPlanningCandidateTitle(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var directEvidenceText = BuildPageLocalSourceBackedPlanningProofText(candidate.Hit);
        var directEvidence = NormalizeLexicalLookup(directEvidenceText);
        if (directEvidence.Length < 8)
            return false;

        if (directEvidence.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        var compactTitle = Regex.Replace(normalizedTitle, @"\s+", string.Empty, RegexOptions.CultureInvariant);
        if (compactTitle.Length >= 10)
        {
            var compactEvidence = Regex.Replace(directEvidence, @"\s+", string.Empty, RegexOptions.CultureInvariant);
            if (compactEvidence.Contains(compactTitle, StringComparison.Ordinal))
                return true;
        }

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length == 0 || titleTerms.Length > 8)
            return false;

        var matchedTerms = titleTerms.Count(term => directEvidence.Contains(term, StringComparison.Ordinal));
        if (PrimaryPageEvidenceSupportsClippedStructuredPlanningTitle(
                candidate,
                normalizedTitle,
                directEvidenceText,
                directEvidence,
                titleTerms))
        {
            return true;
        }

        if (titleTerms.Length <= 4)
            return matchedTerms == titleTerms.Length;

        return matchedTerms >= Math.Max(4, (int)Math.Ceiling(titleTerms.Length * 0.85));
    }

    private static bool PrimaryPageEvidenceSupportsClippedStructuredPlanningTitle(
        SourceBackedOptionCandidate candidate,
        string normalizedTitle,
        string directEvidenceText,
        string normalizedDirectEvidence,
        IReadOnlyList<string> titleTerms)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || string.IsNullOrWhiteSpace(normalizedDirectEvidence)
            || titleTerms.Count is < 5 or > 8)
        {
            return false;
        }

        var titleIsAnchoredByPageSurface = EnumeratePlanningCandidateRawTitleSurfaces(candidate.Hit)
            .Concat(candidate.Hit.MatchedContentCards?.Select(static card => card.Title) ?? Enumerable.Empty<string>())
            .Select(CleanSourceBackedOptionTitle)
            .Select(NormalizeLexicalLookup)
            .Any(surface => string.Equals(surface, normalizedTitle, StringComparison.Ordinal));
        if (!titleIsAnchoredByPageSurface)
            return false;

        for (var prefixTermCount = Math.Min(titleTerms.Count - 1, 6); prefixTermCount >= 4; prefixTermCount--)
        {
            var prefix = string.Join(' ', titleTerms.Take(prefixTermCount));
            if (prefix.Length < 18)
                continue;

            if (!NormalizedLookupContainsWholePhrase(normalizedDirectEvidence, prefix))
                continue;

            return PrimaryEvidenceContainsLocalStructuredPlanningProof(candidate.Hit, prefix, directEvidenceText)
                || PrimaryEvidenceContainsRelaxedLocalStructuredPlanningProof(candidate.Hit, prefix, directEvidenceText)
                || PrimaryEvidenceContainsDenseLocalStructuredPlanningProof(candidate.Hit, prefix, directEvidenceText)
                || TitleWindowContainsStrongLocalStructuredPlanningProof(prefix, directEvidenceText);
        }

        return false;
    }

    private static string BuildPrimarySourceBackedPlanningEvidenceText(RagHitSummary hit)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(hit.Excerpt))
            parts.Add(hit.Excerpt);
        if (!string.IsNullOrWhiteSpace(hit.FullText))
            parts.Add(hit.FullText);
        if (!string.IsNullOrWhiteSpace(hit.ContextualSnippet))
            parts.Add(hit.ContextualSnippet);
        foreach (var card in hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            var cardEvidence = BuildSourceBackedCardStrictEvidenceSnippet(card);
            if (!string.IsNullOrWhiteSpace(cardEvidence))
                parts.Add(cardEvidence);
        }

        return CollapseWhitespace(string.Join(' ', parts.Where(static value => !string.IsNullOrWhiteSpace(value))));
    }

    private static string BuildPageLocalSourceBackedPlanningProofText(RagHitSummary hit)
    {
        if (LooksLikeNavigationOnlyHit(hit) && !LooksLikeResolvedRouteTargetHit(hit))
            return string.Empty;

        var parts = new List<string>();
        var excerpt = CollapseWhitespace(hit.Excerpt ?? string.Empty);
        var fullText = CollapseWhitespace(hit.FullText ?? string.Empty);
        var contextualSnippet = CollapseWhitespace(hit.ContextualSnippet ?? string.Empty);

        if (LooksLikeFinalSourceBackedPlanningProofFragment(excerpt))
            parts.Add(excerpt);
        if (!string.Equals(fullText, excerpt, StringComparison.OrdinalIgnoreCase)
            && LooksLikeFinalSourceBackedPlanningProofFragment(fullText))
        {
            parts.Add(fullText);
        }
        if (!string.Equals(contextualSnippet, excerpt, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contextualSnippet, fullText, StringComparison.OrdinalIgnoreCase)
            && LooksLikeFinalSourceBackedPlanningProofFragment(contextualSnippet))
        {
            parts.Add(contextualSnippet);
        }

        foreach (var card in hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            var cardEvidence = BuildSourceBackedCardStrictEvidenceSnippet(card);
            if (LooksLikeFinalSourceBackedPlanningProofFragment(cardEvidence)
                && ContentCardEvidenceIsGroundedInPrimaryPageText(hit, card))
            {
                parts.Add(cardEvidence);
            }
        }

        if (parts.Count == 0
            && !LooksLikeFinalSourceBackedPlanningOrientationSurface(hit)
            && BackendSelectionHintsPreferUsableEvidence(hit)
            && !LooksLikeStructuredPlanningNavigationOrIndexNoise($"{excerpt} {fullText} {contextualSnippet}"))
        {
            if (!string.IsNullOrWhiteSpace(excerpt))
                parts.Add(excerpt);
            if (!string.IsNullOrWhiteSpace(fullText)
                && !string.Equals(fullText, excerpt, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(fullText);
            }
            if (!string.IsNullOrWhiteSpace(contextualSnippet)
                && !string.Equals(contextualSnippet, excerpt, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(contextualSnippet, fullText, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(contextualSnippet);
            }
        }

        return CollapseWhitespace(string.Join(' ', parts.Where(static value => !string.IsNullOrWhiteSpace(value))));
    }

    private static bool ContentCardEvidenceIsGroundedInPrimaryPageText(
        RagHitSummary hit,
        RagHitContentCardSummary card)
    {
        if (!SourceBackedContentCardHasExplicitPageAnchor(card))
            return false;

        var normalizedTitles = ExtractSourceBackedCardTitleVariants(card.Title)
            .Select(title => NormalizeLexicalLookup(CleanSourceBackedOptionTitle(title)))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedTitles.Length == 0)
            return false;

        var rawPageEvidence = BuildPageLocalStructuredPlanningEvidenceText(hit);
        var normalizedPageEvidence = NormalizeLexicalLookup(rawPageEvidence);
        if (normalizedPageEvidence.Length < 24)
            return false;

        if (normalizedTitles.Any(title =>
                normalizedPageEvidence.Contains(title, StringComparison.Ordinal)
                || PrimaryContentTermsTightlySupportPlanningTitle(title, normalizedPageEvidence)))
        {
            return true;
        }

        return normalizedTitles.Any(title => ContentCardSourceEvidenceBodyTermsAreSupportedByPrimaryPageText(
            hit,
            card,
            title,
            rawPageEvidence,
            normalizedPageEvidence));
    }

    private static string BuildFinalSourceBackedPlanningProofText(RagHitSummary hit)
    {
        if (LooksLikeNavigationOnlyHit(hit) && !LooksLikeResolvedRouteTargetHit(hit))
            return string.Empty;

        var parts = new List<string>();
        var excerpt = CollapseWhitespace(hit.Excerpt ?? string.Empty);
        var fullText = CollapseWhitespace(hit.FullText ?? string.Empty);

        if (LooksLikeFinalSourceBackedPlanningProofFragment(excerpt))
            parts.Add(excerpt);
        if (!string.Equals(fullText, excerpt, StringComparison.OrdinalIgnoreCase)
            && LooksLikeFinalSourceBackedPlanningProofFragment(fullText))
        {
            parts.Add(fullText);
        }

        foreach (var card in hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            var cardEvidence = BuildSourceBackedCardStrictEvidenceSnippet(card);
            if (LooksLikeFinalSourceBackedPlanningProofFragment(cardEvidence))
                parts.Add(cardEvidence);
        }

        if (parts.Count == 0
            && !LooksLikeFinalSourceBackedPlanningOrientationSurface(hit)
            && BackendSelectionHintsPreferUsableEvidence(hit)
            && !LooksLikeStructuredPlanningNavigationOrIndexNoise(BuildPrimarySourceBackedPlanningEvidenceText(hit)))
        {
            var primaryEvidence = BuildPrimarySourceBackedPlanningEvidenceText(hit);
            if (!string.IsNullOrWhiteSpace(primaryEvidence))
                parts.Add(primaryEvidence);
        }

        return CollapseWhitespace(string.Join(' ', parts.Where(static value => !string.IsNullOrWhiteSpace(value))));
    }

    private static bool ShouldRejectSourceBackedPlanningOrientationSurfaceCandidate(SourceBackedOptionCandidate candidate)
        => LooksLikeFinalSourceBackedPlanningOrientationSurface(candidate.Hit)
           && !HasStrictStructuredPlanningCandidateEvidence(candidate)
           && !HasDirectSourceBackedPlanningCandidateEvidence(candidate);

    private static bool LooksLikeFinalSourceBackedPlanningOrientationSurface(RagHitSummary hit)
    {
        var value = CollapseWhitespace(
            $"{hit.SectionTitle} {hit.HeadingPath} {hit.Excerpt} {hit.FullText} {hit.ContextualSnippet}");
        if (value.Length < 24)
            return false;

        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasOrientationCue = Regex.IsMatch(
            normalized,
            @"\b(?:table\s+des\s+matieres|sommaire|contents?|table\s+of\s+contents|index|catalogue|catalog|liste|list|sections?\s+principales?|premiers?\s+extraits?|matched\s+profile\s+title|source\s+de\s+verite|source\s+of\s+truth)\b",
            RegexOptions.CultureInvariant);
        if (!hasOrientationCue)
            return false;

        return !LooksLikeConcreteFinalSourceBackedPlanningProofOnNavigationSurface(normalized);
    }

    private static bool LooksLikeFinalSourceBackedPlanningProofFragment(string? text)
    {
        var value = CollapseWhitespace(text ?? string.Empty);
        if (value.Length < 24)
            return false;

        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:sections?\s+principales?|premiers?\s+extraits?|matched\s+profile\s+title|document\s+.+\s+\d+\s+pages?|ce\s+document\s+(?:couvre|contient|inclut)|this\s+document\s+(?:covers|contains|includes)|source\s+de\s+verite|source\s+of\s+truth)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var looksLikeNavigationOrIndexSurface = Regex.IsMatch(
            normalized,
            @"\b(?:table\s+des\s+matieres|sommaire|contents?|index|catalogue|catalog|liste|list|sections?\s+principales?|premiers?\s+extraits?)\b",
            RegexOptions.CultureInvariant);
        if (looksLikeNavigationOrIndexSurface
            && !LooksLikeConcreteFinalSourceBackedPlanningProofOnNavigationSurface(normalized))
        {
            return false;
        }

        if (!looksLikeNavigationOrIndexSurface
            && Regex.IsMatch(
                normalized,
                @"\b(?:components?|preparation|prÃ©paration|etapes?|steps?|methode|method|procedure|instructions?|quantites?|quantities?|materiel|materials?|requirements?|components?|operation|workflow|actions?|tasks?|criteria|criteres|conditions?|parameters?)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (looksLikeNavigationOrIndexSurface)
            return LooksLikeConcreteFinalSourceBackedPlanningProofOnNavigationSurface(normalized);

        if (CountNumericFactMarkers(NormalizeStructuredScanText(value)) >= 2)
            return true;

        if (CountProcedureStepMarkers(NormalizeStructuredScanText(value)) > 0)
            return true;

        return false;
    }

    private static bool LooksLikeConcreteFinalSourceBackedPlanningProofOnNavigationSurface(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return HasStrongLocalStructuredPlanningProofText(normalized);
    }
}
