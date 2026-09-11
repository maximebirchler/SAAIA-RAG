using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool ContentCardTitleLooksLikeStructuredFieldValueInPrimaryPage(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string normalizedTitle,
        string rawPageEvidence,
        string normalizedPageEvidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || normalizedTitle.Length > 90
            || string.IsNullOrWhiteSpace(rawPageEvidence)
            || normalizedPageEvidence.Length < normalizedTitle.Length + 16
            || !SourceBackedContentCardOrContextHasExplicitPageAnchor(hit, card))
        {
            return false;
        }

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        var titleMatches = Regex.Matches(normalizedPageEvidence, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();
        if (titleMatches.Length == 0)
            return false;

        if (CandidateTitleAppearsInsideExplicitDataListBeforeProcess(normalizedTitle, normalizedPageEvidence))
            return true;
        if (CandidateTitleOwnsLocalStructuredPlanningSection(normalizedTitle, normalizedPageEvidence))
            return false;

        var firstFieldLabel = Regex.Match(
            normalizedPageEvidence,
            @"\b(?:components?|composants?|constituents?|constituants?|inputs?|materials?|materiel|requirements?|exigences?|supplies?|tools?|outils?|items?|elements?|values?|valeurs?|parameters?|parametres?|quantit(?:y|ies)|quantites?)\b",
            RegexOptions.CultureInvariant);
        if (!firstFieldLabel.Success)
            return false;

        if (titleMatches.Any(match => match.Index < firstFieldLabel.Index))
            return false;

        foreach (var titleMatch in titleMatches)
        {
            var before = normalizedPageEvidence[Math.Max(0, titleMatch.Index - 260)..titleMatch.Index];
            if (!TitleMatchLooksEmbeddedInPrecedingStructuredField(before))
                continue;

            var afterStart = titleMatch.Index + titleMatch.Length;
            var after = normalizedPageEvidence[afterStart..Math.Min(normalizedPageEvidence.Length, afterStart + 280)];
            if (TitleAfterLooksLikeOwnStructuredFieldThenProcessSection(after))
                continue;

            if (Regex.IsMatch(
                    after,
                    @"\b(?:preparation|procedure|instructions?|method|methode|steps?|etapes?|technique|operation|workflow|checklist|validation|review|revue)\b",
                    RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContentCardCanBridgeClippedStructuredPlanningTitle(
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

        if (!ContentCardSourceEvidenceCanAnchorStructuredPlanningTitle(card, normalizedTitle))
            return false;

        if (LooksLikeStructuredPlanningNavigationOrIndexNoise(rawPageEvidence)
            && !HasStrongLocalStructuredPlanningProofText(rawPageEvidence))
        {
            return false;
        }

        return PageEvidenceHasStructuredBodyProofCue(rawPageEvidence)
            || PageEvidenceHasStructuredCardProofCue(rawPageEvidence)
            || (PrimaryEvidenceHasConcreteStructuredPlanningCues(hit, rawPageEvidence)
                && HasStrongLocalStructuredPlanningProofText(rawPageEvidence));
    }

    private static bool SourceBackedContextHitCanBridgeClippedStructuredPlanningTitle(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string normalizedTitle,
        string rawPageEvidence,
        string normalizedPageEvidence)
    {
        if (!string.Equals(hit.Retriever, "documents.context", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(normalizedTitle)
            || normalizedPageEvidence.Length < 48
            || !ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card)
            || ContentCardEvidenceLooksLikeNavigationOnly(card)
            || LooksLikeGenericStructuredInventoryTitle(normalizedTitle)
            || LooksLikeNoisyStructuredPlanningCandidateTitle(card.Title))
        {
            return false;
        }

        var anchorText = NormalizeLexicalLookup(string.Join(' ', new[]
        {
            card.Title,
            hit.SectionTitle,
            hit.HeadingPath,
            string.Join(' ', card.Signals ?? Array.Empty<string>())
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));
        if (!anchorText.Contains(normalizedTitle, StringComparison.Ordinal))
            return false;

        return PageEvidenceSupportsClippedStructuredPlanningTitlePrefix(
            hit,
            normalizedTitle,
            rawPageEvidence,
            normalizedPageEvidence);
    }

    private static bool PageEvidenceSupportsClippedStructuredPlanningTitlePrefix(
        RagHitSummary hit,
        string normalizedTitle,
        string rawPageEvidence,
        string normalizedPageEvidence)
    {
        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length is < 5 or > 8)
            return false;

        for (var prefixTermCount = Math.Min(titleTerms.Length - 1, 6); prefixTermCount >= 4; prefixTermCount--)
        {
            var prefix = string.Join(' ', titleTerms.Take(prefixTermCount));
            if (prefix.Length < 18
                || !NormalizedLookupContainsWholePhrase(normalizedPageEvidence, prefix))
            {
                continue;
            }

            return PrimaryEvidenceContainsLocalStructuredPlanningProof(hit, prefix, rawPageEvidence)
                || PrimaryEvidenceContainsRelaxedLocalStructuredPlanningProof(hit, prefix, rawPageEvidence)
                || PrimaryEvidenceContainsDenseLocalStructuredPlanningProof(hit, prefix, rawPageEvidence)
                || TitleWindowContainsStrongLocalStructuredPlanningProof(prefix, rawPageEvidence);
        }

        return false;
    }

    private static bool ContentCardSourceEvidenceCanAnchorStructuredPlanningTitle(
        RagHitContentCardSummary card,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        if (ContentCardEvidenceLooksLikeNavigationOnly(card))
            return false;

        var hasSupportingSourceText = false;
        foreach (var sourceText in EnumerateContentCardSourceEvidenceTexts(card))
        {
            var normalizedSourceText = NormalizeLexicalLookup(sourceText);
            if (normalizedSourceText.Length < 8)
                continue;

            var sourceSupportsTitle = normalizedSourceText.Contains(normalizedTitle, StringComparison.Ordinal)
                || PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, normalizedSourceText);
            if (sourceSupportsTitle)
            {
                hasSupportingSourceText = true;
                continue;
            }

            if (HasStrongLocalStructuredPlanningProofText(sourceText)
                || PageEvidenceHasStructuredCardProofCue(sourceText))
            {
                return false;
            }
        }

        return hasSupportingSourceText
            || card.Signals?.Any(signal =>
            {
                var normalizedSignal = NormalizeLexicalLookup(signal);
                return normalizedSignal.Contains(normalizedTitle, StringComparison.Ordinal)
                    || PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, normalizedSignal);
            }) == true;
    }

    private static bool RagHitHasSelfContainedStructuredPlanningCardProof(RagHitSummary hit)
        => (hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Any(card =>
            {
                var title = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(card.Title));
                return !string.IsNullOrWhiteSpace(title)
                    && IsUsableSourceBackedOptionTitle(card.Title)
                    && !LooksLikeNoisyStructuredPlanningCandidateTitle(card.Title)
                    && ContentCardHasSelfContainedStructuredPlanningProof(card, title);
            });

    private static bool ContentCardHasSelfContainedStructuredPlanningProof(
        RagHitContentCardSummary card,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || ContentCardEvidenceLooksLikeNavigationOnly(card)
            || !ContentCardCarriesStructuredPlanningEvidence(card, normalizedTitle)
            || !ContentCardSourceEvidenceCanAnchorStructuredPlanningTitle(card, normalizedTitle))
        {
            return false;
        }

        var strictEvidence = BuildSourceBackedCardStrictEvidenceSnippet(card);
        var normalizedEvidence = NormalizeLexicalLookup(strictEvidence);
        if (normalizedEvidence.Length < 24)
            return false;

        if (!normalizedEvidence.Contains(normalizedTitle, StringComparison.Ordinal)
            && !PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, normalizedEvidence))
        {
            return false;
        }

        if (!HasStrongLocalStructuredPlanningProofText(strictEvidence)
            && !PageEvidenceHasStructuredCardProofCue(strictEvidence))
        {
            return false;
        }

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .ToHashSet(StringComparer.Ordinal);
        var bodyTerms = ExtractPlanningAnswerSupportTerms(normalizedEvidence)
            .Where(term => !titleTerms.Contains(term))
            .Where(static term => !IsGenericStructuredPlanningEvidenceBridgeTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        return bodyTerms.Length >= 3
            || CountStructuredFieldLabelFamilies(NormalizeStructuredScanText(strictEvidence)) >= 2
            || CountMeasuredValueMarkers(NormalizeStructuredScanText(strictEvidence)) >= 1;
    }

    private static bool ContentCardEvidenceLooksLikeNavigationOnly(RagHitContentCardSummary card)
    {
        var evidence = card.Evidence;
        if (evidence is null)
            return false;

        foreach (var fact in evidence.Facts ?? Array.Empty<RagHitEvidenceFactSummary>())
        {
            var kind = NormalizeLexicalLookup(fact.Kind);
            var text = NormalizeLexicalLookup(CollapseWhitespace(string.Join(' ', new[] { fact.Label, fact.Value, fact.SourceText })));
            if (Regex.IsMatch(kind, @"\b(?:index|navigation|toc|table\s+of\s+contents|sommaire)\b", RegexOptions.CultureInvariant))
                return true;
            if (Regex.IsMatch(text, @"\b(?:index|navigation|toc|table\s+of\s+contents|table\s+des\s+matieres|sommaire)\b", RegexOptions.CultureInvariant)
                && !HasStrongLocalStructuredPlanningProofText(text))
            {
                return true;
            }
        }

        foreach (var fact in evidence.QuantityFacts ?? Array.Empty<RagHitQuantityFactSummary>())
        {
            var text = NormalizeLexicalLookup(CollapseWhitespace(string.Join(' ', new[] { fact.Label, fact.Unit, fact.SourceText })));
            if (Regex.IsMatch(text, @"\b(?:index|navigation|toc|table\s+of\s+contents|table\s+des\s+matieres|sommaire|page)\b", RegexOptions.CultureInvariant)
                && !HasStrongLocalStructuredPlanningProofText(text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContentCardKindLooksLikeStructuredPlanningTitleAnchor(RagHitContentCardSummary card)
    {
        var kind = NormalizeLexicalLookup(card.Kind);
        return kind.Contains("page_embedded", StringComparison.Ordinal)
            || kind.Contains("embedded_title", StringComparison.Ordinal)
            || kind.Contains("unit", StringComparison.Ordinal)
            || kind.Contains("title", StringComparison.Ordinal)
            || kind.Contains("section", StringComparison.Ordinal);
    }

    private static bool PageEvidenceHasStructuredBodyProofCue(string? text)
    {
        var value = CollapseWhitespace(text ?? string.Empty);
        if (value.Length < 32)
            return false;

        var normalized = NormalizeStructuredScanText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasComponentCue = Regex.IsMatch(
            normalized,
            @"\b(?:components?)\s*:",
            RegexOptions.CultureInvariant);
        var hasPreparationCue = Regex.IsMatch(
            normalized,
            @"(?:^|\s)(?:pr[eÃ©]paration)\s*:",
            RegexOptions.CultureInvariant);
        if (!hasComponentCue || !hasPreparationCue)
            return false;

        var hasMeasuredOrServingCue = CountMeasuredValueMarkers(normalized) >= 1
            || CountNumericFactMarkers(normalized) >= 1
            || Regex.IsMatch(
                normalized,
                @"\b(?:pour\s+\d{1,3}\s+(?:portions?|personnes?|pieces?|pi[eÃ¨]ces?)|\d+\s*(?:min|minutes?|h|heures?))\b",
                RegexOptions.CultureInvariant);

        return hasMeasuredOrServingCue
            && !LooksLikeStructuredPlanningNavigationOrIndexNoise(value);
    }

    private static bool ContentCardCarriesStructuredPlanningEvidence(
        RagHitContentCardSummary card,
        string normalizedTitle)
    {
        var evidence = card.Evidence;
        if (evidence is null)
            return card.RawEvidence.HasValue;

        var evidenceText = NormalizeLexicalLookup(BuildSourceBackedCardEvidenceSnippet(card));
        var titleSupported = evidenceText.Contains(normalizedTitle, StringComparison.Ordinal)
            || PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, evidenceText)
            || (card.Signals?.Any(signal =>
            {
                var normalizedSignal = NormalizeLexicalLookup(signal);
                return normalizedSignal.Contains(normalizedTitle, StringComparison.Ordinal)
                    || PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, normalizedSignal);
            }) == true);
        if (!titleSupported)
            return false;

        return evidence.ScaleBasis is not null
            || evidence.QuantityFacts.Count > 0
            || evidence.Facts?.Count > 0
            || card.RawEvidence.HasValue;
    }

    private static bool PageEvidenceHasStructuredCardProofCue(string? text)
    {
        var value = CollapseWhitespace(text ?? string.Empty);
        if (value.Length < 24)
            return false;

        var normalized = NormalizeStructuredScanText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasStructuredCardCue = Regex.IsMatch(
            normalized,
            @"\b(?:components?|preparation|pr[eÃ©]paration|temps\s+de\s+(?:preparation|pr[eÃ©]paration|operation)|operation\s*:|categories?\s+de\s+options?|modes?\s+de\s+preparation|pour\s+\d{1,3}\s+(?:portions?|personnes?|pieces?|pi[eÃ¨]ces?))\b",
            RegexOptions.CultureInvariant);
        if (!hasStructuredCardCue)
            return false;

        var hasMeasuredOrServingCue = CountMeasuredValueMarkers(normalized) >= 1
            || CountNumericFactMarkers(normalized) >= 1
            || Regex.IsMatch(
                normalized,
                @"\b(?:pour\s+\d{1,3}\s+(?:portions?|personnes?|pieces?|pi[eÃ¨]ces?)|\d+\s*(?:min|minutes?|h|heures?))\b",
                RegexOptions.CultureInvariant);

        return hasMeasuredOrServingCue
            && !LooksLikeStructuredPlanningNavigationOrIndexNoise(value);
    }

    private static bool HasConcreteContentCardEvidenceForTitle(
        RagHitContentCardSummary card,
        string normalizedTitle,
        bool requireExactTitle = false)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || !HasConcreteContentCardEvidence(card))
            return false;

        var strictEvidence = BuildSourceBackedCardStrictEvidenceSnippet(card);
        var normalizedEvidence = NormalizeLexicalLookup(strictEvidence);
        if (normalizedEvidence.Length < 16)
            return false;

        var sourceEvidenceTexts = EnumerateContentCardSourceEvidenceTexts(card)
            .Select(CollapseWhitespace)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (sourceEvidenceTexts.Length > 0)
        {
            var sourceTextProvesTitle = sourceEvidenceTexts.Any(sourceText =>
            {
                var normalizedSourceText = NormalizeLexicalLookup(sourceText);
                if (normalizedSourceText.Length < 16)
                    return false;

                var titleMatchesSourceText = normalizedSourceText.Contains(normalizedTitle, StringComparison.Ordinal)
                                             || (!requireExactTitle && PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, normalizedSourceText));
                return titleMatchesSourceText && HasStrongLocalStructuredPlanningProofText(sourceText);
            });
            if (!sourceTextProvesTitle)
                return false;
        }

        if (!normalizedEvidence.Contains(normalizedTitle, StringComparison.Ordinal)
            && (requireExactTitle || !PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, normalizedEvidence)))
        {
            return false;
        }

        return HasStrongLocalStructuredPlanningProofText(strictEvidence);
    }

    private static bool ContentCardEvidenceIsSupportedByPrimaryPageText(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string normalizedTitle,
        bool requireStrictPageLocalSupport = false)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var rawPageEvidence = BuildPageLocalStructuredPlanningEvidenceText(hit);
        var normalizedPageEvidence = NormalizeLexicalLookup(rawPageEvidence);
        if (normalizedPageEvidence.Length < 24)
            return false;

        if (PrimaryEvidenceContainsLocalStructuredPlanningProof(hit, normalizedTitle, rawPageEvidence))
            return true;

        if (!SourceBackedContentCardHasExplicitPageAnchor(card))
            return false;

        foreach (var sourceText in EnumerateContentCardSourceEvidenceTexts(card))
        {
            var normalizedSourceText = NormalizeLexicalLookup(sourceText);
            if (normalizedSourceText.Length < 24
                || !normalizedSourceText.Contains(normalizedTitle, StringComparison.Ordinal))
            {
                continue;
            }

            if (!ContentCardSourceTextIsAnchoredInPrimaryPageText(
                    normalizedPageEvidence,
                    normalizedSourceText,
                    normalizedTitle,
                    requireStrictPageLocalSupport))
            {
                continue;
            }

            if (PrimaryEvidenceContainsLocalStructuredPlanningProof(hit, normalizedTitle, sourceText))
                return true;
        }

        return false;
    }

    private static bool ContentCardSourceEvidenceBodyTermsAreSupportedByPrimaryPageText(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string normalizedTitle,
        string rawPageEvidence,
        string normalizedPageEvidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || string.IsNullOrWhiteSpace(rawPageEvidence)
            || normalizedPageEvidence.Length < 48
            || !SourceBackedContentCardOrContextHasExplicitPageAnchor(hit, card)
            || !ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card)
            || !ContentCardCarriesStructuredPlanningEvidence(card, normalizedTitle))
        {
            return false;
        }

        var cardTitleAnchorsTitle = string.Equals(
            NormalizeLexicalLookup(CleanSourceBackedOptionTitle(card.Title)),
            normalizedTitle,
            StringComparison.Ordinal);
        if (!cardTitleAnchorsTitle && !ContentCardSourceEvidenceCanAnchorStructuredPlanningTitle(card, normalizedTitle))
            return false;

        foreach (var sourceText in EnumerateContentCardSourceEvidenceTexts(card))
        {
            var normalizedSourceText = NormalizeLexicalLookup(sourceText);
            if (normalizedSourceText.Length < 48)
                continue;

            if (LooksLikeStructuredPlanningNavigationOrIndexNoise(sourceText)
                || Regex.IsMatch(
                    normalizedSourceText,
                    @"\b(?:\p{L}+[_\s-]?index|title[_\s-]?anchor|navigation|sommaire|table\s+of\s+contents|table\s+des\s+matieres|index)\b",
                    RegexOptions.CultureInvariant))
            {
                continue;
            }

            var sourceSupportsTitle = normalizedSourceText.Contains(normalizedTitle, StringComparison.Ordinal)
                || PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, normalizedSourceText)
                || cardTitleAnchorsTitle;
            if (!sourceSupportsTitle)
                continue;

            if (!HasStrongLocalStructuredPlanningProofText(sourceText)
                && !PageEvidenceHasStructuredCardProofCue(sourceText)
                && !PrimaryEvidenceHasConcreteStructuredPlanningCues(hit, sourceText))
            {
                continue;
            }

            var sourceTerms = ExtractPlanningAnswerSupportTerms(normalizedSourceText)
                .Where(static term => term.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
                .ToHashSet(StringComparer.Ordinal);
            var bodyTerms = sourceTerms
                .Where(term => !titleTerms.Contains(term))
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

    private static bool IsGenericStructuredPlanningEvidenceBridgeTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return true;

        return IsGenericPlanningCoverageTerm(term)
            || Regex.IsMatch(
                term,
                @"^(?:components?|composants?|materials?|materiel|requirements?|exigences?|preparation|procedure|instructions?|method|methode|steps?|etapes?|operation|workflow|quantity|quantities|quantite|quantites|value|values|valeur|valeurs|page|source|document|title|titre)$",
                RegexOptions.CultureInvariant);
    }

    private static string BuildPageLocalStructuredPlanningEvidenceText(RagHitSummary hit)
        => CollapseWhitespace(string.Join(' ', new[]
        {
            hit.Excerpt,
            hit.FullText,
            BuildStrictStructuredPlanningContextualEvidenceText(hit.ContextualSnippet)
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));

    private static string BuildStrictStructuredPlanningContextualEvidenceText(string? contextualSnippet)
    {
        var raw = CollapseWhitespace(contextualSnippet ?? string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        if (!raw.Contains("Matched profile title:", StringComparison.OrdinalIgnoreCase))
            return raw;

        var evidenceBlocks = new[]
        {
            ExtractContextualSnippetBlock(contextualSnippet ?? string.Empty, "Excerpt:"),
            ExtractContextualSnippetBlock(contextualSnippet ?? string.Empty, "Context:"),
            ExtractContextualSnippetBlock(contextualSnippet ?? string.Empty, "PreviousContext:"),
            ExtractContextualSnippetBlock(contextualSnippet ?? string.Empty, "NextContext:")
        };
        return CollapseWhitespace(string.Join(' ', evidenceBlocks.Where(static value => !string.IsNullOrWhiteSpace(value))));
    }

    private static bool ContentCardSourceTextIsAnchoredInPrimaryPageText(
        string normalizedPageEvidence,
        string normalizedSourceText,
        string normalizedTitle,
        bool requireStrictPageLocalSupport = false)
    {
        if (string.IsNullOrWhiteSpace(normalizedPageEvidence)
            || string.IsNullOrWhiteSpace(normalizedSourceText)
            || string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return false;
        }

        if (normalizedPageEvidence.Contains(normalizedSourceText, StringComparison.Ordinal))
            return true;

        var titleIndex = normalizedPageEvidence.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (titleIndex < 0)
            return false;

        var windowStart = Math.Max(0, titleIndex - 120);
        var windowEnd = Math.Min(normalizedPageEvidence.Length, titleIndex + normalizedTitle.Length + 520);
        var pageWindow = normalizedPageEvidence[windowStart..windowEnd];
        if (!pageWindow.Contains(normalizedTitle, StringComparison.Ordinal))
            return false;

        if (requireStrictPageLocalSupport
            && !HasStrongLocalStructuredPlanningProofText(pageWindow))
        {
            return false;
        }

        var sourceTerms = ExtractPlanningAnswerSupportTerms(normalizedSourceText)
            .Where(static term => term.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sourceTerms.Length == 0)
            return false;

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .ToHashSet(StringComparer.Ordinal);
        var nonTitleTerms = sourceTerms
            .Where(term => !titleTerms.Contains(term))
            .ToArray();
        if (nonTitleTerms.Length == 0)
            return pageWindow.Contains(normalizedTitle, StringComparison.Ordinal);

        var matched = nonTitleTerms.Count(term => pageWindow.Contains(term, StringComparison.Ordinal));
        if (requireStrictPageLocalSupport)
        {
            var requiredMatches = Math.Max(3, (int)Math.Ceiling(nonTitleTerms.Length * 0.75));
            return matched >= Math.Min(requiredMatches, nonTitleTerms.Length);
        }

        if (matched >= Math.Min(4, nonTitleTerms.Length))
            return true;

        return nonTitleTerms.Length <= 5
            && matched >= 2
            && matched / (double)nonTitleTerms.Length >= 0.50;
    }

    private static IEnumerable<string> EnumerateContentCardSourceEvidenceTexts(RagHitContentCardSummary card)
    {
        var evidence = card.Evidence;
        if (evidence is null)
            yield break;

        foreach (var fact in evidence.QuantityFacts ?? Array.Empty<RagHitQuantityFactSummary>())
        {
            if (!string.IsNullOrWhiteSpace(fact.SourceText))
                yield return fact.SourceText;
        }

        foreach (var fact in evidence.Facts ?? Array.Empty<RagHitEvidenceFactSummary>())
        {
            if (!string.IsNullOrWhiteSpace(fact.SourceText))
                yield return fact.SourceText;
        }
    }

    private static string BuildSourceBackedCardContextSnippet(RagHitSummary hit, RagHitContentCardSummary card, string cardEvidence)
    {
        return CollapseWhitespace(string.Join(' ', new[]
        {
            $"Matched profile title: {card.Title}",
            string.IsNullOrWhiteSpace(hit.DocName) ? null : $"Document: {hit.DocName}",
            string.IsNullOrWhiteSpace(hit.SectionTitle) ? null : $"Section: {hit.SectionTitle}",
            string.IsNullOrWhiteSpace(cardEvidence) ? null : $"Evidence: {cardEvidence}"
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));
    }

    private static string BuildSourceBackedCardEvidenceSnippet(RagHitContentCardSummary card)
    {
        var parts = new List<string> { card.Title };

        var evidence = card.Evidence;
        if (evidence is null)
            return CollapseWhitespace(card.Title);

        if (card.Signals is { Count: > 0 })
            parts.AddRange(card.Signals.Take(8));

        if (evidence is not null)
        {
            if (evidence.ScaleBasis is { Count: > 0 } basis)
            {
                parts.Add(CollapseWhitespace(string.Join(' ', new[]
                {
                    basis.Count.ToString(CultureInfo.InvariantCulture),
                    basis.Label
                }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
            }

            foreach (var fact in evidence.QuantityFacts.Take(6))
            {
                parts.Add(CollapseWhitespace(string.Join(' ', new[]
                {
                    fact.Value.ToString("0.###", CultureInfo.InvariantCulture),
                    fact.Unit,
                    fact.Label,
                    fact.SourceText
                }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
            }

            foreach (var fact in (evidence.Facts ?? []).Take(8))
            {
                parts.Add(CollapseWhitespace(string.Join(' ', new[]
                {
                    fact.Kind,
                    fact.Label,
                    fact.Value,
                    fact.Unit,
                    fact.SourceText
                }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
            }

            parts.AddRange(evidence.NonScalableReasons.Take(4));
        }

        return CollapseWhitespace(string.Join(' ', parts.Where(static value => !string.IsNullOrWhiteSpace(value))));
    }

    private static string BuildSourceBackedCardStrictEvidenceSnippet(RagHitContentCardSummary card)
    {
        var evidence = card.Evidence;
        if (evidence is null)
            return string.Empty;

        var parts = new List<string>();

        if (evidence.ScaleBasis is { } basis)
        {
            var basisText = CollapseWhitespace(string.Join(' ', new[]
            {
                basis.Label
            }.Where(static value => !string.IsNullOrWhiteSpace(value))));
            if (!string.IsNullOrWhiteSpace(basisText))
                parts.Add(basisText);
        }

        foreach (var fact in evidence.QuantityFacts.Take(8))
        {
            var factText = CollapseWhitespace(string.Join(' ', new[]
            {
                fact.Label,
                fact.SourceText
            }.Where(static value => !string.IsNullOrWhiteSpace(value))));
            if (!string.IsNullOrWhiteSpace(factText))
                parts.Add(factText);
        }

        foreach (var fact in (evidence.Facts ?? []).Take(12))
        {
            var factText = CollapseWhitespace(string.Join(' ', new[]
            {
                fact.Label,
                fact.Value,
                fact.SourceText
            }.Where(static value => !string.IsNullOrWhiteSpace(value))));
            if (!string.IsNullOrWhiteSpace(factText))
                parts.Add(factText);
        }

        return CollapseWhitespace(string.Join(' ', parts));
    }
}
