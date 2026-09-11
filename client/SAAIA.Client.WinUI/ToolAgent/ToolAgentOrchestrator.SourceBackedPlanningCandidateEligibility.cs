using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildSourceBackedPlanningCandidateKey(SourceBackedOptionCandidate candidate)
    {
        var titleKey = NormalizeLexicalLookup(candidate.Title);
        if (!string.IsNullOrWhiteSpace(titleKey))
            return $"{candidate.Hit.DocPath}|{candidate.Hit.PageStart}|{candidate.Hit.PageEnd}|title:{titleKey}";

        var cardId = candidate.Hit.MatchedContentCards?
            .Select(static card => NullIfWhiteSpace(card.ContentCardId))
            .FirstOrDefault(static id => !string.IsNullOrWhiteSpace(id));
        if (!string.IsNullOrWhiteSpace(cardId))
            return $"{candidate.Hit.DocPath}|{candidate.Hit.PageStart}|{candidate.Hit.PageEnd}|card:{cardId}";

        return $"{candidate.Hit.DocPath}|{candidate.Hit.PageStart}|{candidate.Hit.PageEnd}";
    }

    private static string BuildSourceBackedPlanningCandidateLeadKey(SourceBackedOptionCandidate candidate)
    {
        var titleKey = NormalizeLexicalLookup(candidate.Title);
        if (!string.IsNullOrWhiteSpace(titleKey))
            return $"title:{titleKey}";

        var cardTitle = candidate.Hit.MatchedContentCards?
            .Select(static card => NullIfWhiteSpace(card.Title))
            .FirstOrDefault(static title => !string.IsNullOrWhiteSpace(title));
        var cardTitleKey = NormalizeLexicalLookup(cardTitle);
        if (!string.IsNullOrWhiteSpace(cardTitleKey))
            return $"card-title:{cardTitleKey}";

        var cardId = candidate.Hit.MatchedContentCards?
            .Select(static card => NullIfWhiteSpace(card.ContentCardId))
            .FirstOrDefault(static id => !string.IsNullOrWhiteSpace(id));
        if (!string.IsNullOrWhiteSpace(cardId))
            return $"card:{cardId}";

        return BuildSourceBackedPlanningCandidateKey(candidate);
    }

    private static bool IsUsableSourceBackedPlanningCandidate(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        var hasStrictEvidence = HasStrictStructuredPlanningCandidateEvidence(candidate);
        var hasAnchoredContextProof = SourceBackedContextCandidateHasAnchoredFuzzyLocalStructuredProof(candidate, normalizedTitle);
        var looksLikeStructuredFieldValue = LooksLikeStructuredPlanningFieldValueCandidate(candidate, normalizedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || LooksLikePlanItemNoise(candidate.Title)
            || (LooksLikeWeakSourceBackedOptionTitle(candidate.Title) && !hasStrictEvidence)
            || LooksLikeReferenceAttributionSourceTitle(candidate)
            || LooksLikeNonConcreteSourceBackedPlanningCandidate(candidate, hasStrictEvidence)
            || LooksLikeGenericInventorySurfaceDerivedPlanningCandidate(candidate)
            || LooksLikeGenericPlanningContextCandidate(candidate)
            || looksLikeStructuredFieldValue
            || (!hasAnchoredContextProof
                && (LooksLikePageContextLabelPlanningCandidate(candidate)
                    || LooksLikeGenericStructuredFieldLabelCandidate(candidate)
                    || LooksLikePlanningFrameOrAdviceCandidate(candidate, hasStrictEvidence)
                    || LooksLikeWeakSingleTermStructuredPlanningCandidate(candidate, hasStrictEvidence)
                    || LooksLikeUnattachedShortSectionStructuredPlanningCandidate(candidate)))
            || (LooksLikeNoisyStructuredPlanningCandidateTitle(candidate.Title) && !hasAnchoredContextProof)
            || LooksLikeProcedureSentenceTitle(normalizedTitle))
        {
            return false;
        }

        var titleTerms = ExtractQuerySignalTerms(normalizedTitle)
            .Where(static term => term.Length >= 4)
            .ToArray();
        if (titleTerms.Length == 0)
            return hasStrictEvidence;

        return true;
    }

    private static bool LooksLikeNonConcreteSourceBackedPlanningCandidate(
        SourceBackedOptionCandidate candidate,
        bool hasStrictEvidence)
    {
        if (LooksLikeGenericRubricOrTaxonomyOnlySourceHit(candidate.Hit, candidate.Title))
            return true;

        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (!hasStrictEvidence
            && (LooksLikeStandaloneQuantityFragmentTitle(normalizedTitle)
                || LooksLikeShortNumberedStrictPlanningFragmentTitle(normalizedTitle)))
        {
            return true;
        }

        var normalizedEvidence = NormalizeLexicalLookup(CollapseWhitespace(string.Join(
            ' ',
            new[]
            {
                candidate.Title,
                candidate.Hit.SectionTitle,
                candidate.Hit.HeadingPath,
                candidate.Hit.Excerpt,
                candidate.Hit.FullText,
                candidate.Hit.ContextualSnippet,
                BuildPageLocalStructuredPlanningEvidenceText(candidate.Hit)
            }.Where(static part => !string.IsNullOrWhiteSpace(part)))));
        return LooksLikeExplicitlyNonConcreteSourceEvidence(normalizedEvidence);
    }

    private static bool LooksLikeRequestedPlanningSlotAxisLabelCandidate(
        SourceBackedOptionCandidate candidate,
        string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalizedTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(candidate.Title));
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var labels = DetectRequestedPlanningSlotAxisLabels(query, DetectRetrievalExpansionLanguage(query))
            .Concat(ExtractPlanningSlotRetrievalTerms(query))
            .SelectMany(ExpandPlanningSlotRetrievalTermVariants)
            .Select(NormalizeLexicalLookup)
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (labels.Count == 0)
            return false;

        if (labels.Contains(normalizedTitle))
            return true;

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return titleTerms.Length == 1
            && labels.Contains(titleTerms[0])
            && normalizedTitle.Length <= titleTerms[0].Length + 2;
    }

    private static bool LooksLikeWeakSingleTermStructuredPlanningCandidate(
        SourceBackedOptionCandidate candidate,
        bool hasStrictEvidence)
    {
        var normalizedTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(candidate.Title));
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length == 0
            && Regex.IsMatch(normalizedTitle, @"^[\p{L}'\-]{5,18}$", RegexOptions.CultureInvariant)
            && !IsGenericPlanningCoverageTerm(normalizedTitle))
        {
            titleTerms = new[] { normalizedTitle };
        }
        if (titleTerms.Length != 1)
            return false;

        var term = titleTerms[0];
        if (term.Length is < 5 or > 18)
            return false;

        var proofText = CollapseWhitespace(BuildStructuredPlanningFieldValueProofText(candidate));
        var normalizedProof = NormalizeLexicalLookup(proofText);
        if (normalizedProof.Length < normalizedTitle.Length + 32)
            return true;

        if (!normalizedProof.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        if (SingleTermCandidateLooksLikeOperationalActionFragment(normalizedTitle, normalizedProof))
            return true;

        if (SingleTermCandidateLooksLikeLooseContextLabel(candidate, normalizedTitle, proofText, normalizedProof))
            return true;

        if (!hasStrictEvidence)
            return true;

        if (SingleTermCandidateHasTightStructuredTitleAttachment(normalizedTitle, proofText)
            || PrimaryEvidenceContainsDenseLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, proofText)
            || PrimaryEvidenceContainsRelaxedLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, proofText)
            || TitleWindowContainsStrongLocalStructuredPlanningProof(normalizedTitle, proofText)
            || ContentCardsCarryStructuredPlanningEvidenceForTitle(candidate, normalizedTitle))
        {
            return false;
        }

        return true;
    }

    private static bool SingleTermCandidateLooksLikeLooseContextLabel(
        SourceBackedOptionCandidate candidate,
        string normalizedTitle,
        string proofText,
        string normalizedProof)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || string.IsNullOrWhiteSpace(proofText)
            || string.IsNullOrWhiteSpace(normalizedProof))
        {
            return true;
        }

        if (SingleTermCandidateHasTightStructuredTitleAttachment(normalizedTitle, proofText))
            return false;

        var titleIndex = normalizedProof.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (titleIndex <= 0)
            return false;

        var afterTitleStart = titleIndex + normalizedTitle.Length;
        var afterTitle = normalizedProof[afterTitleStart..Math.Min(normalizedProof.Length, afterTitleStart + 220)].TrimStart();
        if (afterTitle.Length < 24)
            return false;

        var looksLikeContextDescription = Regex.IsMatch(
            afterTitle,
            @"^(?:this|that|these|those|ce|cet|cette|ces|the|le|la|les)?\s*(?:section|chapter|chapitre|part|partie|rubrique|category|categorie|cat[eÃƒÂ©]gorie|scope|contexte|context|background|overview|description|presentation|pr[eÃƒÂ©]sentation)\b|\b(?:broad|general|regional|global|context|contexte|background|overview|description)\b",
            RegexOptions.CultureInvariant);
        if (!looksLikeContextDescription)
            return false;

        if (PageEvidenceHasStructuredBodyProofCue(afterTitle))
        {
            return false;
        }

        var beforeTitle = normalizedProof[..titleIndex];
        if (!HasStrongLocalStructuredPlanningProofText(beforeTitle))
            return false;

        return true;
    }

    private static bool SingleTermCandidateLooksLikeOperationalActionFragment(
        string normalizedTitle,
        string normalizedProof)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedProof))
            return true;

        if (!Regex.IsMatch(normalizedTitle, @"^[a-z]{5,18}(?:er|ir|re|ez)$", RegexOptions.CultureInvariant))
            return false;

        var titleIndex = normalizedProof.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (titleIndex < 0)
            return false;

        var afterTitleStart = titleIndex + normalizedTitle.Length;
        var afterTitle = normalizedProof[afterTitleStart..Math.Min(normalizedProof.Length, afterTitleStart + 180)].TrimStart();
        if (afterTitle.Length == 0)
            return false;

        if (Regex.IsMatch(
                afterTitle,
                @"^(?:components?|composants?|materials?|mat[eÃƒÂ©]riel|requirements?|exigences?|items?|[eÃƒÂ©]l[eÃƒÂ©]ments?|values?|valeurs?|parameters?|param[eÃƒÂ¨]tres?|quantit(?:y|ies)|quantit[eÃƒÂ©]s?|preparation|pr[eÃƒÂ©]paration|procedure|proc[eÃƒÂ©]dure|instructions?|method|m[eÃƒÂ©]thode|steps?|[eÃƒÂ©]tapes?|technique|operation|workflow)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return Regex.IsMatch(
                afterTitle,
                @"^(?:[a-z]{5,24}(?:er|ir|re|ez)|le|la|les|l|un|une|des|du|de|dans|avec|sur|sous|pour|apres|apr[eÃƒÂ¨]s|avant|puis|ensuite|si|quand|lorsqu|lorsque|the|a|an|to|in|with|on|under|for|after|before|then|when|if)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                afterTitle,
                @"^\p{L}{4,24}\s+(?:un|une|des|le|la|les|l|the|a|an|to|with|for)\b",
                RegexOptions.CultureInvariant);
    }

    private static bool SingleTermCandidateHasTightStructuredTitleAttachment(
        string normalizedTitle,
        string proofText)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(proofText))
            return false;

        var normalizedProof = NormalizeLexicalLookup(proofText);
        if (normalizedProof.Length < normalizedTitle.Length + 16)
            return false;

        var searchIndex = 0;
        while (searchIndex < normalizedProof.Length)
        {
            var index = normalizedProof.IndexOf(normalizedTitle, searchIndex, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var afterTitle = normalizedProof[(index + normalizedTitle.Length)..Math.Min(normalizedProof.Length, index + normalizedTitle.Length + 140)];
            var structureMatch = Regex.Match(
                afterTitle,
                @"\b(?:components?|composants?|materials?|materiel|requirements?|exigences?|procedure|preparation|instructions?|method|methode|steps?|etapes?|operation|workflow|checklist|validation|review|revue)\b",
                RegexOptions.CultureInvariant);
            if (structureMatch.Success)
            {
                var bridge = CollapseWhitespace(afterTitle[..structureMatch.Index]);
                if (bridge.Length <= 48
                    && !Regex.IsMatch(
                        bridge,
                        @"\b(?:section|chapter|chapitre|overview|context|contexte|document|page|catalogue|catalog|index|sommaire|contents?)\b",
                        RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }

            searchIndex = index + normalizedTitle.Length;
        }

        return false;
    }
}
