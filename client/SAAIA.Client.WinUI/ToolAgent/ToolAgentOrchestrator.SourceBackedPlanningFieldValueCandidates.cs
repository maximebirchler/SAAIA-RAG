using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool ContentCardsCarryStructuredPlanningEvidenceForTitle(
        SourceBackedOptionCandidate candidate,
        string normalizedTitle)
        => !string.IsNullOrWhiteSpace(normalizedTitle)
           && (candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
           .Any(card => card.Evidence is not null
               && ContentCardCarriesStructuredPlanningEvidence(card, normalizedTitle)
               && ContentCardSourceEvidenceCanAnchorStructuredPlanningTitle(card, normalizedTitle));

    private static bool LooksLikeUnattachedShortSectionStructuredPlanningCandidate(SourceBackedOptionCandidate candidate)
    {
        var rawTitle = CollapseWhitespace(CleanSourceBackedOptionTitle(candidate.Title));
        var normalizedTitle = NormalizeLexicalLookup(rawTitle);
        if (string.IsNullOrWhiteSpace(rawTitle)
            || string.IsNullOrWhiteSpace(normalizedTitle)
            || rawTitle.Length > 42)
        {
            return false;
        }

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Where(static term => term.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length is < 1 or > 2)
            return false;

        var proofText = CollapseWhitespace(BuildStructuredPlanningFieldValueProofText(candidate));
        var rawPageEvidence = BuildPageLocalStructuredPlanningEvidenceText(candidate.Hit);
        var normalizedPageEvidence = NormalizeLexicalLookup(rawPageEvidence);
        if (TitleWindowContainsStrongLocalStructuredPlanningProof(normalizedTitle, proofText)
            || HasPageAnchoredContentCardStructuredPlanningProof(candidate.Hit, normalizedTitle)
            || PrimaryEvidenceContainsDenseLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, proofText)
            || PrimaryEvidenceContainsRelaxedLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, proofText))
        {
            return false;
        }

        foreach (var card in candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            if (ContentCardCarriesStructuredPlanningEvidence(card, normalizedTitle)
                && ContentCardSourceEvidenceCanAnchorStructuredPlanningTitle(card, normalizedTitle))
            {
                return false;
            }
        }

        var competingLocalTitles = ExtractPlanItemTitleCandidatesV2(rawPageEvidence)
            .Concat(ExtractSourceBackedTitleCandidates(candidate.Hit))
            .Concat((candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
                .Select(static card => card.Title))
            .Select(CleanSourceBackedOptionTitle)
            .Where(IsUsableSourceBackedOptionTitle)
            .Where(static title => !LooksLikeNoisyStructuredPlanningCandidateTitle(title))
            .Where(LooksLikeConcreteStructuredPlanningCandidateTitle)
            .Select(NormalizeLexicalLookup)
            .Where(title => !string.IsNullOrWhiteSpace(title)
                && !string.Equals(title, normalizedTitle, StringComparison.Ordinal)
                && !NormalizedLookupContainsWholePhrase(title, normalizedTitle))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (competingLocalTitles.Length > 0
            && (LooksLikeStructuredPlanningNavigationOrIndexNoise(rawPageEvidence)
                || PageEvidenceHasStructuredBodyProofCue(rawPageEvidence)
                || PageEvidenceHasStructuredCardProofCue(rawPageEvidence)
                || PrimaryEvidenceHasConcreteStructuredPlanningCues(candidate.Hit, rawPageEvidence)))
        {
            return true;
        }

        var hasUnprovenMatchingCardTitle = (candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Any(card =>
            {
                var cardTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(card.Title));
                return string.Equals(cardTitle, normalizedTitle, StringComparison.Ordinal)
                    && ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card)
                    && !ContentCardCarriesStructuredPlanningEvidence(card, normalizedTitle);
            });
        if (hasUnprovenMatchingCardTitle)
            return true;

        if (normalizedPageEvidence.Length < normalizedTitle.Length + 48
            || !normalizedPageEvidence.Contains(normalizedTitle, StringComparison.Ordinal))
        {
            return false;
        }

        var titleLooksLikePageSurface = EnumeratePlanningCandidateRawTitleSurfaces(candidate.Hit)
            .Select(CleanSourceBackedOptionTitle)
            .Select(NormalizeLexicalLookup)
            .Any(surface => string.Equals(surface, normalizedTitle, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(surface)
                    && surface.Contains(normalizedTitle, StringComparison.Ordinal)));
        if (!titleLooksLikePageSurface)
            return false;

        return HasStrongLocalStructuredPlanningProofText(rawPageEvidence)
            || PageEvidenceHasStructuredBodyProofCue(rawPageEvidence)
            || PageEvidenceHasStructuredCardProofCue(rawPageEvidence)
            || PrimaryEvidenceHasConcreteStructuredPlanningCues(candidate.Hit, rawPageEvidence)
            || PrimaryEvidenceHasDenseStructuredPlanningCues(candidate.Hit, rawPageEvidence);
    }

    private static bool LooksLikeGenericStructuredFieldLabelCandidate(SourceBackedOptionCandidate candidate)
    {
        var rawTitle = CollapseWhitespace(CleanSourceBackedOptionTitle(candidate.Title));
        var normalizedTitle = NormalizeLexicalLookup(rawTitle);
        if (string.IsNullOrWhiteSpace(rawTitle) || string.IsNullOrWhiteSpace(normalizedTitle))
            return true;
        if (rawTitle.Length > 64)
            return false;
        if (Regex.IsMatch(
                rawTitle,
                @"^[\p{L}][\p{L}'\u2019\-]{3,32}\s*(?:[:*]|\u2022)\s+\p{L}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }
        if (LooksLikeDelimitedUnitLeadStructuredSurfaceTitle(candidate, rawTitle))
            return true;

        var proofText = CollapseWhitespace(BuildPageLocalSourceBackedPlanningProofText(candidate.Hit));
        if (proofText.Length < rawTitle.Length + 8)
            return false;

        var escapedTitle = Regex.Escape(rawTitle).Replace("\\ ", @"\s+");
        var leadingField = Regex.Match(
            proofText,
            @"^\s*" + escapedTitle + @"\s*(?:[:*]|\u2022)\s*(?<tail>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!leadingField.Success)
            return false;

        var tail = CollapseWhitespace(leadingField.Groups["tail"].Value);
        if (tail.Length < 8)
            return true;

        var normalizedTail = NormalizeStructuredScanText(tail);
        return LooksLikeGenericStructuredLabelValueSequence(tail, normalizedTail)
            || Regex.IsMatch(
                normalizedTail,
                @"\b(?:preparation|pr[eÃƒÂ©]paration|procedure|proc[eÃƒÂ©]dure|instructions?|method|m[eÃƒÂ©]thode|steps?|[eÃƒÂ©]tapes?|operation|workflow|technique)\b",
                RegexOptions.CultureInvariant)
            || CountMeasuredValueMarkers(normalizedTail) >= 1
            || CountNumericFactMarkers(normalizedTail) >= 2;
    }

    private static bool LooksLikeDelimitedUnitLeadStructuredSurfaceTitle(
        SourceBackedOptionCandidate candidate,
        string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle) || rawTitle.Length > 90)
            return false;

        var match = Regex.Match(
            rawTitle,
            @"^(?<lead>[^:*•\u2022]{2,52})\s*(?:[:*•]|\u2022)\s*(?<tail>\S.{3,})$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var normalizedTitle = NormalizeLexicalLookup(rawTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var leadTerms = ExtractPlanningAnswerSupportTerms(NormalizeLexicalLookup(match.Groups["lead"].Value))
            .Where(static term => term.Length >= 2)
            .Take(6)
            .ToArray();
        var tailTerms = ExtractPlanningAnswerSupportTerms(NormalizeLexicalLookup(match.Groups["tail"].Value))
            .Where(static term => term.Length >= 3)
            .Take(6)
            .ToArray();
        if (leadTerms.Length is < 1 or > 5 || tailTerms.Length < 2)
            return false;

        return (candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Any(card =>
            {
                var normalizedCardTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(card.Title));
                var normalizedKind = NormalizeLexicalLookup(card.Kind);
                return string.Equals(normalizedCardTitle, normalizedTitle, StringComparison.Ordinal)
                    && normalizedKind.Contains("unit", StringComparison.Ordinal);
            });
    }

    private static bool LooksLikeStructuredPlanningFieldValueCandidate(
        SourceBackedOptionCandidate candidate,
        string? normalizedTitle = null)
        => !string.IsNullOrWhiteSpace(ExplainStructuredPlanningFieldValueCandidateRejection(candidate, normalizedTitle));

    private static string? ExplainStructuredPlanningFieldValueCandidateRejection(
        SourceBackedOptionCandidate candidate,
        string? normalizedTitle = null)
    {
        var rawTitle = CollapseWhitespace(candidate.Title);
        var normalized = normalizedTitle;
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = NormalizeLexicalLookup(rawTitle);

        if (rawTitle.IndexOfAny(new[] { ',', ';' }) >= 0
            && LooksLikeDelimitedStructuredPlanningFieldValueCandidate(candidate))
        {
            return "delimited_field_value";
        }

        if (!string.IsNullOrWhiteSpace(normalized)
            && normalized.Length <= 54
            && Regex.IsMatch(normalized, @"\b(?:et|and|avec|with|con|com|und|e|y|&)\b|[,;/]", RegexOptions.CultureInvariant)
            && LooksLikeShortConnectorStructuredPlanningFieldValueCandidate(candidate))
        {
            return "short_connector_field_value";
        }

        if (!string.IsNullOrWhiteSpace(normalized)
            && normalized.Length <= 42
            && Regex.IsMatch(normalized, @"^\d+(?:[,.]\d+)?\s+\p{L}", RegexOptions.CultureInvariant)
            && LooksLikeBareQuantityStructuredPlanningFieldValueCandidate(candidate))
        {
            return "bare_quantity_field_value";
        }

        var normalizedFieldValueProof = NormalizeLexicalLookup(BuildStructuredPlanningFieldValueProofText(candidate));
        var normalizedPrimaryPageProof = NormalizeLexicalLookup(BuildPageLocalStructuredPlanningEvidenceText(candidate.Hit));
        var hasLeadingPrimaryTitle = CandidateTitleHasLeadingOccurrenceBeforeStructuredFields(normalized, normalizedPrimaryPageProof);
        var hasSupportingFieldValueShape = LooksLikeSupportingStructuredPlanningFieldValueCandidate(candidate);
        var hasEmbeddedFieldValueShape = LooksLikeEmbeddedStructuredPlanningFieldValueCandidate(candidate);
        var hasPageAnchoredCardProof = !string.IsNullOrWhiteSpace(normalized)
            && HasPageAnchoredContentCardStructuredPlanningProof(candidate.Hit, normalized);
        var firstOccurrenceInPrimaryDataList = !string.IsNullOrWhiteSpace(normalized)
            && CandidateTitleFirstOccurrenceAppearsInsideExplicitDataListBeforeProcess(normalized, normalizedPrimaryPageProof);

        if (!string.IsNullOrWhiteSpace(normalized)
            && CandidateTitleOwnsLocalStructuredPlanningSection(normalized, normalizedPrimaryPageProof)
            && !firstOccurrenceInPrimaryDataList
            && !hasEmbeddedFieldValueShape
            && (!hasSupportingFieldValueShape
                || hasLeadingPrimaryTitle
                || hasPageAnchoredCardProof
                || TitleWindowContainsStrongLocalStructuredPlanningProof(
                    normalized,
                    BuildStructuredPlanningFieldValueProofText(candidate))))
        {
            return null;
        }

        var appearsInsideStructuredFieldList = !string.IsNullOrWhiteSpace(normalized)
            && CandidateTitleAppearsInsideRawStructuredFieldListBeforeProcess(
                CleanSourceBackedOptionTitle(rawTitle),
                BuildStructuredPlanningFieldValueProofText(candidate));
        if (!appearsInsideStructuredFieldList && !string.IsNullOrWhiteSpace(normalized))
        {
            appearsInsideStructuredFieldList =
                CandidateTitleAppearsInsideExplicitDataListBeforeProcess(normalized, normalizedPrimaryPageProof)
                || CandidateTitleAppearsInsideExplicitDataListBeforeProcess(normalized, normalizedFieldValueProof);
        }

        if (appearsInsideStructuredFieldList
            && !ContentCardTitleVariantOwnsStructuredProcessSection(
                candidate,
                normalized,
                normalizedFieldValueProof))
        {
            return "supporting_field_value";
        }

        if (!string.IsNullOrWhiteSpace(normalized)
            && !hasLeadingPrimaryTitle
            && (ContentCardEvidenceNamesDifferentStructuredPlanningItem(candidate, normalized)
                || !hasPageAnchoredCardProof)
            && CandidateTitleAppearsInsideExplicitDataListBeforeProcess(
                normalized,
                normalizedPrimaryPageProof))
        {
            return "supporting_field_value";
        }

        if (!string.IsNullOrWhiteSpace(normalized)
            && !hasLeadingPrimaryTitle
            && CandidateTitleAppearsInsideExplicitDataListBeforeProcess(
                normalized,
                normalizedPrimaryPageProof))
        {
            return "supporting_field_value";
        }

        if (!string.IsNullOrWhiteSpace(normalized)
            && !hasLeadingPrimaryTitle
            && (ContentCardEvidenceNamesDifferentStructuredPlanningItem(candidate, normalized)
                || !HasPageAnchoredContentCardStructuredPlanningProof(candidate.Hit, normalized))
            && CandidateTitleAppearsInsideExplicitDataListBeforeProcess(
                normalized,
                normalizedFieldValueProof))
        {
            return "supporting_field_value";
        }

        if (!string.IsNullOrWhiteSpace(normalized)
            && !ContentCardEvidenceNamesDifferentStructuredPlanningItem(candidate, normalized)
            && hasPageAnchoredCardProof
            && !hasSupportingFieldValueShape
            && !hasEmbeddedFieldValueShape)
        {
            return null;
        }

        if (!LooksLikeInlinePlanningSupportFragmentTitle(normalized)
            && !CandidateTitleHasNearbyStructuredFieldValueCue(candidate, normalized))
        {
            return null;
        }

        if (hasSupportingFieldValueShape)
            return "supporting_field_value";
        if (hasEmbeddedFieldValueShape)
            return "embedded_field_value";

        return null;
    }

    private static bool LooksLikeInlinePlanningSupportFragmentTitle(string? normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        return Regex.IsMatch(
            normalizedTitle,
            @"\b(?:pour|to)\s+(?:accompagner|accompany|servir|serve|utiliser|use|complete|completer)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool CandidateTitleHasNearbyStructuredFieldValueCue(
        SourceBackedOptionCandidate candidate,
        string? normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var proof = NormalizeLexicalLookup(BuildStructuredPlanningFieldValueProofText(candidate));
        if (proof.Length < normalizedTitle.Length + 16)
            return false;

        var start = 0;
        while (start < proof.Length)
        {
            var titleIndex = proof.IndexOf(normalizedTitle, start, StringComparison.Ordinal);
            if (titleIndex < 0)
                return false;

            var before = proof[..titleIndex];
            var localBefore = before.Length > 320 ? before[^320..] : before;
            if (Regex.IsMatch(
                    localBefore,
                    @"(?:[,;:]|\b(?:components?|components?|components?|composants?|materials?|mat[eÃƒÂ©]riel|requirements?|exigences?|supplies?|tools?|outils?|items?|[eÃƒÂ©]l[eÃƒÂ©]ments?|values?|valeurs?|parameters?|param[eÃƒÂ¨]tres?|quantit(?:y|ies)|quantit[eÃƒÂ©]s?|pour\s+\d+|for\s+\d+)\b).{0,300}$",
                    RegexOptions.CultureInvariant))
            {
                return true;
            }

            start = titleIndex + Math.Max(1, normalizedTitle.Length);
        }

        return false;
    }

    private static string BuildStructuredPlanningFieldValueProofText(SourceBackedOptionCandidate candidate)
        => CollapseWhitespace(string.Join(' ', new[]
        {
            BuildPageLocalSourceBackedPlanningProofText(candidate.Hit),
            BuildPrimarySourceBackedPlanningEvidenceText(candidate.Hit)
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));

}
