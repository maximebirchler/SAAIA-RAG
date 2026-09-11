using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildSourceBackedPageLocalStructuredPlanningEvidenceWindow(
        RagHitSummary hit,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return string.Empty;

        var rawEvidence = BuildPageLocalStructuredPlanningEvidenceText(hit);
        if (string.IsNullOrWhiteSpace(rawEvidence))
            return string.Empty;

        var normalizedEvidence = NormalizeLexicalLookup(rawEvidence);
        if (normalizedEvidence.Length < 24)
            return string.Empty;

        var index = normalizedEvidence.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (index < 0)
            return string.Empty;

        var start = Math.Max(0, index - 120);
        var end = Math.Min(normalizedEvidence.Length, index + normalizedTitle.Length + 720);
        return normalizedEvidence[start..end];
    }

    private static bool SourceBackedContentCardHasExplicitPageAnchor(RagHitContentCardSummary card)
        => card.PageStart.HasValue
           || card.PageEnd.HasValue
           || (card.Evidence?.Facts?.Any(static fact => fact.PageStart.HasValue || fact.PageEnd.HasValue) ?? false);

    private static bool SourceBackedContentCardOrContextHasExplicitPageAnchor(
        RagHitSummary hit,
        RagHitContentCardSummary card)
        => SourceBackedContentCardHasExplicitPageAnchor(card)
           || (ContentCardKindLooksLikePageEmbeddedSourceSurface(card)
               && (hit.PageStart > 0 || hit.PageEnd > 0))
           || string.Equals(hit.Retriever, "documents.context", StringComparison.OrdinalIgnoreCase);

    private static bool ContentCardKindLooksLikePageEmbeddedSourceSurface(RagHitContentCardSummary card)
    {
        var kind = NormalizeLexicalLookup(card.Kind);
        return kind.Contains("page_embedded", StringComparison.Ordinal)
            || kind.Contains("embedded_title", StringComparison.Ordinal);
    }

    private static bool ContentCardHasPageLocalStructuredPlanningProof(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string title)
    {
        var normalizedTitle = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var rawPageEvidence = BuildPageLocalStructuredPlanningEvidenceText(hit);
        var normalizedPageEvidence = NormalizeLexicalLookup(rawPageEvidence);
        if (ContentCardTitleLooksLikeStructuredFieldValueInPrimaryPage(
                hit,
                card,
                normalizedTitle,
                rawPageEvidence,
                normalizedPageEvidence))
        {
            return false;
        }

        if (PrimaryEvidenceContainsLocalStructuredPlanningProof(hit, normalizedTitle, rawPageEvidence))
            return true;

        if (HasPageAnchoredContentCardStructuredPlanningProof(hit, card, normalizedTitle))
            return true;

        if (SourceBackedContextHitCanBridgeClippedStructuredPlanningTitle(
                hit,
                card,
                normalizedTitle,
                rawPageEvidence,
                normalizedPageEvidence))
        {
            return true;
        }

        if (ContentCardSignalsAnchorTitleToStructuredPageBody(
                card,
                normalizedTitle,
                rawPageEvidence,
                normalizedPageEvidence))
        {
            return true;
        }

        if (ContentCardHasSelfContainedStructuredPlanningProof(card, normalizedTitle)
            && SourceBackedContentCardOrContextHasExplicitPageAnchor(hit, card)
            && ContentCardSourceEvidenceBodyTermsAreSupportedByPrimaryPageText(
                hit,
                card,
                normalizedTitle,
                rawPageEvidence,
                normalizedPageEvidence))
        {
            return true;
        }

        if (!SourceBackedContentCardOrContextHasExplicitPageAnchor(hit, card))
            return false;
        if (!HasConcreteContentCardEvidenceForTitle(card, normalizedTitle, requireExactTitle: true))
            return false;

        if (!ContentCardEvidenceIsSupportedByPrimaryPageText(hit, card, normalizedTitle, requireStrictPageLocalSupport: true))
            return false;

        foreach (var sourceText in EnumerateContentCardSourceEvidenceTexts(card))
        {
            var normalizedSourceText = NormalizeLexicalLookup(sourceText);
            if (normalizedSourceText.Length < 16
                || !normalizedSourceText.Contains(normalizedTitle, StringComparison.Ordinal))
            {
                continue;
            }

            if (ContentCardSourceTextIsAnchoredInPrimaryPageText(
                    normalizedPageEvidence: NormalizeLexicalLookup(rawPageEvidence),
                    normalizedSourceText,
                    normalizedTitle,
                    requireStrictPageLocalSupport: true)
                && PrimaryEvidenceContainsLocalStructuredPlanningProof(hit, normalizedTitle, sourceText))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPageAnchoredContentCardStructuredPlanningProof(
        RagHitSummary hit,
        string normalizedTitle)
    {
        if (hit.MatchedContentCards is not { Count: > 0 })
            return false;

        foreach (var card in hit.MatchedContentCards)
        {
            if (HasPageAnchoredContentCardStructuredPlanningProof(hit, card, normalizedTitle))
                return true;
        }

        return false;
    }

    private static bool HasPageAnchoredContentCardStructuredPlanningProof(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || !SourceBackedContentCardOrContextHasExplicitPageAnchor(hit, card)
            || !LooksLikeConcreteStructuredPlanningCandidateNormalizedTitle(normalizedTitle)
            || LooksLikeGenericStructuredInventoryTitle(normalizedTitle))
        {
            return false;
        }

        var rawPageEvidence = BuildPageLocalStructuredPlanningEvidenceText(hit);
        var normalizedPageEvidence = NormalizeLexicalLookup(rawPageEvidence);
        if (normalizedPageEvidence.Length < 24)
            return false;

        if (ContentCardTitleLooksLikeStructuredFieldValueInPrimaryPage(
                hit,
                card,
                normalizedTitle,
                rawPageEvidence,
                normalizedPageEvidence))
        {
            return false;
        }

        var titleIsLocallyVisible = normalizedPageEvidence.Contains(normalizedTitle, StringComparison.Ordinal)
            || PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, normalizedPageEvidence);
        if (!titleIsLocallyVisible
            && SourceBackedContextHitCanBridgeClippedStructuredPlanningTitle(
                hit,
                card,
                normalizedTitle,
                rawPageEvidence,
                normalizedPageEvidence))
        {
            return true;
        }

        if (!titleIsLocallyVisible
            && ContentCardSignalsAnchorTitleToStructuredPageBody(
                card,
                normalizedTitle,
                rawPageEvidence,
                normalizedPageEvidence))
        {
            return true;
        }

        if (ContentCardEvidenceLooksLikeNavigationOnly(card))
            return false;

        if (!titleIsLocallyVisible
            && ContentCardCanBridgeClippedStructuredPlanningTitle(
                hit,
                card,
                normalizedTitle,
                rawPageEvidence,
                normalizedPageEvidence))
        {
            return true;
        }

        var hasAnchoredStructuredBodyProof = ContentCardHasPageAnchoredStructuredBodyProof(
            hit,
            card,
            normalizedTitle,
            rawPageEvidence,
            normalizedPageEvidence);
        var hasAnchoredCardTitle = ContentCardCanAnchorStructuredPlanningTitleWithoutVisiblePageTitle(
            hit,
            card,
            normalizedTitle,
            rawPageEvidence,
            normalizedPageEvidence);
        if (!titleIsLocallyVisible && !hasAnchoredCardTitle && !hasAnchoredStructuredBodyProof)
            return false;

        var hasConcreteCardEvidence = ContentCardCarriesStructuredPlanningEvidence(card, normalizedTitle);
        var hasCardEvidenceAnchoredInPageBody = ContentCardSourceEvidenceBodyTermsAreSupportedByPrimaryPageText(
            hit,
            card,
            normalizedTitle,
            rawPageEvidence,
            normalizedPageEvidence);
        var hasTitleAttachedLocalStructuredProof =
            PrimaryEvidenceContainsRelaxedLocalStructuredPlanningProof(hit, normalizedTitle, rawPageEvidence)
            || PrimaryEvidenceContainsDenseLocalStructuredPlanningProof(hit, normalizedTitle, rawPageEvidence)
            || TitleWindowContainsStrongLocalStructuredPlanningProof(normalizedTitle, rawPageEvidence);
        var hasLocalStructuredProof = HasStrongLocalStructuredPlanningProofText(rawPageEvidence)
            || hasTitleAttachedLocalStructuredProof
            || PageEvidenceHasStructuredCardProofCue(rawPageEvidence)
            || hasCardEvidenceAnchoredInPageBody
            || hasAnchoredStructuredBodyProof
            || hasAnchoredCardTitle;
        if (!hasLocalStructuredProof)
            return false;

        if (hasConcreteCardEvidence)
            return true;
        if (hasAnchoredCardTitle)
            return true;
        if (hasAnchoredStructuredBodyProof)
            return true;
        if (!hasTitleAttachedLocalStructuredProof)
            return false;

        var kind = NormalizeLexicalLookup(card.Kind);
        return kind.Contains("exact", StringComparison.Ordinal)
            || kind.Contains("lead", StringComparison.Ordinal)
            || kind.Contains("embedded", StringComparison.Ordinal)
            || kind.Contains("section", StringComparison.Ordinal);
    }

    private static bool ContentCardCanAnchorStructuredPlanningTitleWithoutVisiblePageTitle(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string normalizedTitle,
        string rawPageEvidence,
        string normalizedPageEvidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || normalizedPageEvidence.Length < 24
            || !SourceBackedContentCardOrContextHasExplicitPageAnchor(hit, card)
            || !ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card)
            || ContentCardEvidenceLooksLikeNavigationOnly(card)
            || !ContentCardCarriesStructuredPlanningEvidence(card, normalizedTitle)
            || !ContentCardSourceEvidenceCanAnchorStructuredPlanningTitle(card, normalizedTitle)
            || LooksLikeGenericStructuredInventoryTitle(normalizedTitle)
            || LooksLikeNoisyStructuredPlanningCandidateTitle(card.Title))
        {
            return false;
        }

        var cardAnchorText = NormalizeLexicalLookup(string.Join(' ', new[]
        {
            card.Title,
            BuildSourceBackedCardStrictEvidenceSnippet(card),
            BuildSourceBackedCardEvidenceSnippet(card),
            string.Join(' ', card.Signals ?? Array.Empty<string>())
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));
        if (!cardAnchorText.Contains(normalizedTitle, StringComparison.Ordinal)
            && !PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, cardAnchorText))
        {
            return false;
        }

        return ContentCardSourceEvidenceBodyTermsAreSupportedByPrimaryPageText(
                hit,
                card,
                normalizedTitle,
                rawPageEvidence,
                normalizedPageEvidence);
    }

    private static bool ContentCardSignalsAnchorTitleToStructuredPageBody(
        RagHitContentCardSummary card,
        string normalizedTitle,
        string rawPageEvidence,
        string normalizedPageEvidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || string.IsNullOrWhiteSpace(rawPageEvidence)
            || normalizedPageEvidence.Length < 48
            || !SourceBackedContentCardHasExplicitPageAnchor(card)
            || !ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card)
            || !HasStrongLocalStructuredPlanningProofText(rawPageEvidence))
        {
            return false;
        }

        var cardAnchorText = NormalizeLexicalLookup(string.Join(' ', new[]
        {
            card.Title,
            BuildSourceBackedCardStrictEvidenceSnippet(card),
            BuildSourceBackedCardEvidenceSnippet(card),
            string.Join(' ', card.Signals ?? Array.Empty<string>())
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));
        if (!cardAnchorText.Contains(normalizedTitle, StringComparison.Ordinal))
            return false;

        var signalTerms = ExtractContentCardStructuredPlanningSignalBridgeTerms(card, normalizedTitle);
        if (signalTerms.Length < 2)
            return false;

        return CountContentCardSignalBridgePageHits(signalTerms, normalizedPageEvidence) >= 2;
    }

    private static string[] ExtractContentCardStructuredPlanningSignalBridgeTerms(
        RagHitContentCardSummary card,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return Array.Empty<string>();

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .ToHashSet(StringComparer.Ordinal);
        return (card.Signals ?? Array.Empty<string>())
            .SelectMany(signal => ExtractPlanningAnswerSupportTerms(NormalizeLexicalLookup(signal)))
            .Where(static term => term.Length >= 4)
            .Where(term => !titleTerms.Contains(term))
            .Where(static term => !IsGenericStructuredPlanningEvidenceBridgeTerm(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static int CountContentCardSignalBridgePageHits(
        IReadOnlyList<string> signalTerms,
        string normalizedPageEvidence)
        => string.IsNullOrWhiteSpace(normalizedPageEvidence)
            ? 0
            : signalTerms.Count(term => normalizedPageEvidence.Contains(term, StringComparison.Ordinal));

    private static bool ContentCardHasPageAnchoredStructuredBodyProof(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string normalizedTitle,
        string rawPageEvidence,
        string normalizedPageEvidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || normalizedPageEvidence.Length < 48
            || !SourceBackedContentCardOrContextHasExplicitPageAnchor(hit, card)
            || !ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card)
            || ContentCardEvidenceLooksLikeNavigationOnly(card)
            || !LooksLikeConcreteStructuredPlanningCandidateNormalizedTitle(normalizedTitle)
            || LooksLikeGenericStructuredInventoryTitle(normalizedTitle)
            || LooksLikeNoisyStructuredPlanningCandidateTitle(card.Title))
        {
            return false;
        }

        if (ContentCardTitleLooksLikeStructuredFieldValueInPrimaryPage(
                hit,
                card,
                normalizedTitle,
                rawPageEvidence,
                normalizedPageEvidence))
        {
            return false;
        }

        if (!ContentCardCarriesStructuredPlanningBodyEvidence(card))
            return false;

        return ContentCardStructuredEvidenceBodyTermsAreSupportedByPrimaryPageText(
            hit,
            card,
            rawPageEvidence,
            normalizedPageEvidence);
    }

    private static bool ContentCardCarriesStructuredPlanningBodyEvidence(RagHitContentCardSummary card)
    {
        if (ContentCardEvidenceLooksLikeNavigationOnly(card))
            return false;

        if (!HasConcreteContentCardEvidence(card))
            return false;

        var strictEvidence = BuildSourceBackedCardStrictEvidenceSnippet(card);
        if (CollapseWhitespace(strictEvidence).Length < 24)
            return false;

        var normalized = NormalizeStructuredScanText(strictEvidence);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return HasStrongLocalStructuredPlanningProofText(strictEvidence)
            || PageEvidenceHasStructuredCardProofCue(strictEvidence)
            || CountStructuredFieldLabelFamilies(normalized) >= 2
            || (CountMeasuredValueMarkers(normalized) >= 1
                && Regex.IsMatch(
                    normalized,
                    @"\b(?:preparation|procedure|instructions?|method|methode|steps?|etapes?|operation|workflow|checklist|validation|review|revue)\b",
                    RegexOptions.CultureInvariant));
    }

    private static bool ContentCardStructuredEvidenceBodyTermsAreSupportedByPrimaryPageText(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string rawPageEvidence,
        string normalizedPageEvidence)
    {
        if (string.IsNullOrWhiteSpace(rawPageEvidence)
            || normalizedPageEvidence.Length < 48
            || !SourceBackedContentCardOrContextHasExplicitPageAnchor(hit, card)
            || !ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card)
            || !ContentCardCarriesStructuredPlanningBodyEvidence(card))
        {
            return false;
        }

        foreach (var sourceText in EnumerateContentCardSourceEvidenceTexts(card))
        {
            var normalizedSourceText = NormalizeLexicalLookup(sourceText);
            if (normalizedSourceText.Length < 32)
                continue;

            if (LooksLikeStructuredPlanningNavigationOrIndexNoise(sourceText)
                || Regex.IsMatch(
                    normalizedSourceText,
                    @"\b(?:\p{L}+[_\s-]?index|title[_\s-]?anchor|navigation|sommaire|table\s+of\s+contents|table\s+des\s+matieres|index)\b",
                    RegexOptions.CultureInvariant))
            {
                continue;
            }

            if (!HasStrongLocalStructuredPlanningProofText(sourceText)
                && !PageEvidenceHasStructuredCardProofCue(sourceText)
                && !PrimaryEvidenceHasConcreteStructuredPlanningCues(hit, sourceText)
                && CountStructuredFieldLabelFamilies(NormalizeStructuredScanText(sourceText)) < 2)
            {
                continue;
            }

            var bodyTerms = ExtractPlanningAnswerSupportTerms(normalizedSourceText)
                .Where(static term => term.Length >= 3)
                .Where(static term => !IsGenericStructuredPlanningEvidenceBridgeTerm(term))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (bodyTerms.Length < 4)
                continue;

            var matched = bodyTerms.Count(term => normalizedPageEvidence.Contains(term, StringComparison.Ordinal));
            var required = bodyTerms.Length <= 6
                ? Math.Min(4, bodyTerms.Length)
                : Math.Max(4, (int)Math.Ceiling(bodyTerms.Length * 0.55d));
            if (matched >= required)
                return true;
        }

        return false;
    }
}
