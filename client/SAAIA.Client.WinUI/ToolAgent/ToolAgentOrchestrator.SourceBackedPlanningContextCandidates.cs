using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool LooksLikePlanningFrameOrAdviceCandidate(SourceBackedOptionCandidate candidate, bool hasStrictEvidence)
    {
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        var titleTerms = ExtractQuerySignalTerms(normalizedTitle)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var isShortTitle = titleTerms.Length <= 6 || normalizedTitle.Length <= 90;

        var looksLikeFrame = Regex.IsMatch(
            normalizedTitle,
            @"\b(?:guide|guides|conseils?|tips?|astuces?|principes?|principles?|organisation|organization|organiser|organize|planning|planification|calendrier|schedule|horaires?|timing|overview|vue\s+d\s+ensemble|introduction|summary|resume|r[eÃ©]sum[eÃ©]|methode|m[eÃ©]thode|method|cadre|framework|recommandations?\s+generales?|recommendations?\s+generales?|bonnes?\s+pratiques?|best\s+practices?|faq|glossaire|glossary|sommaire|table\s+des\s+matieres|contents?|index)\b",
            RegexOptions.CultureInvariant);
        if (!looksLikeFrame || !isShortTitle)
            return false;

        if (!hasStrictEvidence)
            return true;

        var normalizedEvidence = NormalizeStructuredScanText(
            $"{GetRagHitPrimaryEvidenceText(candidate.Hit)} {GetRagHitStructuredEvidenceText(candidate.Hit)} {candidate.Hit.ContextualSnippet}");
        var hasConcreteCardEvidence = candidate.Hit.MatchedContentCards?.Any(HasConcreteContentCardEvidence) == true;
        var hasActionableEvidence = CountProcedureStepMarkers(normalizedEvidence) >= 2
            || CountBulletListMarkers(normalizedEvidence) >= 3
            || CountNumericFactMarkers(normalizedEvidence) >= 2;

        return !hasConcreteCardEvidence && !hasActionableEvidence;
    }

    private static bool LooksLikeLeadingConnectorStructuredPlanningFragment(string? title)
    {
        var normalizedTitle = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        var terms = ExtractQuerySignalTerms(normalizedTitle)
            .Where(static term => term.Length >= 2)
            .Take(8)
            .ToArray();
        if (terms.Length is < 1 or > 6)
            return false;

        return Regex.IsMatch(
            normalizedTitle,
            @"^(?:a|au|aux|avec|chez|dans|de|des|du|d|en|et|pour|sans|sous|sur|with|for|in|into|from|to|under|over)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeGenericStructuredInventoryTitle(string normalizedTitle)
    {
        var lexicalTitle = CollapseWhitespace(Regex.Replace(normalizedTitle, @"[^\p{L}\p{N}]+", " ")).Trim();
        if (string.IsNullOrWhiteSpace(lexicalTitle))
            return true;

        if (Regex.IsMatch(
                lexicalTitle,
                @"^(?:matched\s+(?:profile|quoted)(?:\s+title)?|profil\s+documentaire|document\s+profile|sections?|rubriques?|chapitres?|chapters?|parts?|parties?|degre\s+de\s+difficulte|niveau\s+de\s+difficulte|difficulty\s+(?:level|rating)|level\s+of\s+difficulty|quantites?\s+donnees?|quantit(?:y|ies)\s+(?:given|provided)|given\s+quantit(?:y|ies)|provided\s+quantit(?:y|ies)|valeurs?\s+donnees?|values?\s+(?:given|provided))$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                lexicalTitle,
                @"^(?:tous|toutes|all|todos|todas|alle)\s+(?:les\s+)?(?:[\p{L}\p{N}]{3,})(?:\s+[\p{L}\p{N}]{3,}){0,2}$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
            lexicalTitle,
            @"^(?:(?:\d+\s+)?(?:a\s+){0,2}voir\s+dans\s+son\s+\p{L}{3,}|options?|items?|elements?|rubriques?|entries?|candidates?|candidats?|examples?|exemples?|proposals?|propositions?|suggestions?|liste|list|catalog(?:ue)?|index|sommaire|contents?|table\s+des\s+matieres|mise\s+en\s+place)$",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                lexicalTitle,
                @"^(?:\d+\s+)?(?:a\s+){0,2}voir\s*dans\s*son\s*\p{L}{3,}",
                RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeGenericInventorySurfaceDerivedPlanningCandidate(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(candidate.Title));
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Where(static term => term.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length == 0 || titleTerms.Length > 4)
            return false;

        var normalizedEvidence = NormalizeLexicalLookup(BuildPageLocalStructuredPlanningEvidenceText(candidate.Hit));
        if (PrimaryPageEvidenceSupportsSourceBackedPlanningCandidateTitle(candidate)
            && (CandidateTitleOwnsLocalStructuredPlanningSection(normalizedTitle, normalizedEvidence)
                || HasPageAnchoredContentCardStructuredPlanningProof(candidate.Hit, normalizedTitle)))
        {
            return false;
        }

        if (LooksLikeTaxonomyPathWithoutConcreteStructuredItemBody(candidate, normalizedTitle))
            return true;

        foreach (var surface in EnumeratePlanningCandidateRawTitleSurfaces(candidate.Hit))
        {
            var normalizedSurface = NormalizeLexicalLookup(surface);
            if (string.IsNullOrWhiteSpace(normalizedSurface)
                || string.Equals(normalizedSurface, normalizedTitle, StringComparison.Ordinal)
                || !normalizedSurface.Contains(normalizedTitle, StringComparison.Ordinal))
            {
                continue;
            }

            if (LooksLikeGenericStructuredInventoryTitle(normalizedSurface)
                || Regex.IsMatch(
                    normalizedSurface,
                    @"^(?:options?|easy\s+options?|suggestions?|ideas?|idees?|examples?|exemples?|candidates?|candidats?|items?|elements?|rubriques?|entries?)\b",
                    RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeTaxonomyPathWithoutConcreteStructuredItemBody(
        SourceBackedOptionCandidate candidate,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var rawEvidence = CollapseWhitespace(BuildPageLocalStructuredPlanningEvidenceText(candidate.Hit));
        var normalizedEvidence = NormalizeLexicalLookup(rawEvidence);
        if (normalizedEvidence.Length < normalizedTitle.Length + 24)
            return false;

        var titleIndex = normalizedEvidence.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (titleIndex is < 0 or > 80)
            return false;

        if (PrimaryPageEvidenceSupportsSourceBackedPlanningCandidateTitle(candidate)
            && ContentCardsCarryStructuredPlanningEvidenceForTitle(candidate, normalizedTitle)
            && (CountNumericFactMarkers(normalizedEvidence) > 0
                || Regex.IsMatch(
                    normalizedEvidence,
                    @"\b(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\b",
                    RegexOptions.CultureInvariant)))
        {
            return false;
        }

        var afterTitle = normalizedEvidence[
            (titleIndex + normalizedTitle.Length)..Math.Min(normalizedEvidence.Length, titleIndex + normalizedTitle.Length + 360)];
        var hasTaxonomyCue = Regex.IsMatch(
            afterTitle,
            @"\b(?:categories?|cat[eÃ©]gories?|classes?|types?|kinds?|families|familles|topics?|rubriques?|sections?|modes?|methods?|m[eÃ©]thodes?|taxonom(?:y|ie))\b",
            RegexOptions.CultureInvariant);
        if (!hasTaxonomyCue)
            return false;

        var hasConcreteItemBody = Regex.IsMatch(
                rawEvidence,
                @"\b(?:components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|procedure|proc[eÃ©]dure|preparation|pr[eÃ©]paration|method|m[eÃ©]thode|steps?|[eÃ©]tapes?|operation|workflow)\s*[:*â€¢]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || CountBulletListMarkers(rawEvidence) >= 2
            || CountProcedureStepMarkers(normalizedEvidence) >= 1;
        return !hasConcreteItemBody;
    }

    private static IEnumerable<string> EnumeratePlanningCandidateRawTitleSurfaces(RagHitSummary hit)
    {
        if (!string.IsNullOrWhiteSpace(hit.SectionTitle))
            yield return hit.SectionTitle!;
        if (!string.IsNullOrWhiteSpace(hit.HeadingPath))
            yield return hit.HeadingPath!;

        foreach (var card in hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            if (!string.IsNullOrWhiteSpace(card.Title))
                yield return card.Title;
        }
    }

    private static bool LooksLikeGenericPlanningContextCandidate(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        if (Regex.IsMatch(
                normalizedTitle,
                @"\b(?:et|and|or|ou|a|de|du|des|entre|vers|avant|apres|aprÃ¨s|after|before|between|from|to|until|jusqu|bis|hasta|ate|fino)$",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var titleTerms = ExtractQuerySignalTerms(normalizedTitle)
            .Where(static term => term.Length >= 4)
            .ToArray();
        if (titleTerms.Length <= 6
            && LooksLikeGenericCadenceOrTimingStatement(normalizedTitle))
        {
            return true;
        }

        if (titleTerms.Length <= 4
            && Regex.IsMatch(
                normalizedTitle,
                @"\b(?:pause|break|sieste|rest|repos)\b",
                RegexOptions.CultureInvariant)
            && !Regex.IsMatch(
                normalizedTitle,
                @"\b(?:procedure|process|maintenance|inspection|controle|control|verification|audit|test|review|revue|validation)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (titleTerms.Length <= 4
            && Regex.IsMatch(
                normalizedTitle,
                @"\b(?:a\s+cot[eÃƒÂ©]s?|a-cot[eÃƒÂ©]s?|side\s+items?|asides?)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var hasSelfContainedCardProof = (candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Any(card => ContentCardHasSelfContainedStructuredPlanningProof(card, normalizedTitle));
        var hasConcreteEvidence = hasSelfContainedCardProof
            || HasConcreteFinalSourceBackedEvidence(candidate.Hit)
            && (ComputeStructuredProcedureVisibleEvidenceCueScore(candidate.Hit) >= 3
                || ComputeProcedureCompletenessCueScore(candidate.Hit) >= 5
                || candidate.Hit.MatchedContentCards?.Any(HasConcreteContentCardEvidence) == true);

        var isMostlyScheduleContext = Regex.IsMatch(
            normalizedTitle,
            @"\b(?:par\s+(?:semaine|mois|jour)|per\s+(?:week|month|day)|cada\s+(?:semana|mes|dia)|por\s+(?:semana|mes|dia)|pro\s+(?:woche|monat|tag)|morning|afternoon|evening|night|matin|midi|soir|nuit|apres\s+midi|aprÃ¨s\s+midi|pause|break|sieste|horaire|horaires|schedule|calendrier|timing|minuit|midnight|vers|entre|avant|after|before)\b",
            RegexOptions.CultureInvariant);

        if (isMostlyScheduleContext && !hasConcreteEvidence)
            return true;

        if (isMostlyScheduleContext
            && !hasSelfContainedCardProof
            && titleTerms.Length <= 5
            && CountNumericFactMarkers(NormalizeStructuredScanText($"{candidate.Title} {GetRagHitPrimaryEvidenceText(candidate.Hit)}")) == 0
            && ComputeProcedureCompletenessCueScore(candidate.Hit) < 7)
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikePageContextLabelPlanningCandidate(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(candidate.Title));
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var contextLabels = new[] { candidate.Hit.SectionTitle, candidate.Hit.HeadingPath }
            .Select(CleanSourceBackedOptionTitle)
            .Select(NormalizeLexicalLookup)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!contextLabels.Contains(normalizedTitle, StringComparer.Ordinal))
            return false;

        var distinctConcreteCardTitles = (candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Select(static card => CleanSourceBackedOptionTitle(card.Title))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Where(static title => LooksLikeConcreteStructuredPlanningCandidateTitle(title))
            .Select(NormalizeLexicalLookup)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctConcreteCardTitles.Length > 0
            && !distinctConcreteCardTitles.Contains(normalizedTitle, StringComparer.Ordinal))
        {
            return true;
        }

        if (HasPageAnchoredContentCardStructuredPlanningProof(candidate.Hit, normalizedTitle))
            return false;

        var compactTitle = BuildCompactStructuredPlanningLookup(normalizedTitle);
        if (compactTitle.Length < 8)
            return false;

        foreach (var text in EnumeratePlanExtractionTexts(candidate.Hit))
        {
            var leadingTitle = CleanSourceBackedOptionTitle(ExtractPlanItemTitleV2(text));
            var normalizedLeadingTitle = NormalizeLexicalLookup(leadingTitle);
            if (string.IsNullOrWhiteSpace(normalizedLeadingTitle)
                || string.Equals(normalizedLeadingTitle, normalizedTitle, StringComparison.Ordinal)
                || !LooksLikeConcreteStructuredPlanningCandidateTitle(leadingTitle))
            {
                continue;
            }

            var compactText = BuildCompactStructuredPlanningLookup(text);
            var compactLeadingTitle = BuildCompactStructuredPlanningLookup(normalizedLeadingTitle);
            if (compactText.Length == 0 || compactLeadingTitle.Length < 8)
                continue;

            var leadingIndex = compactText.IndexOf(compactLeadingTitle, StringComparison.Ordinal);
            var contextIndex = compactText.IndexOf(compactTitle, StringComparison.Ordinal);
            if (leadingIndex < 0 || contextIndex < 0 || contextIndex <= leadingIndex)
                continue;

            var gap = contextIndex - (leadingIndex + compactLeadingTitle.Length);
            if (gap is >= 0 and <= 80)
                return true;
        }

        return false;
    }

    private static bool LooksLikeGenericCadenceOrTimingStatement(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        if (Regex.IsMatch(
                normalizedTitle,
                @"\b(?:un|une|deux|trois|one|two|three|four|\d+)\b.{0,42}\b(?:par\s+(?:semaine|mois|jour)|per\s+(?:week|month|day)|cada\s+(?:semana|mes|dia)|por\s+(?:semana|mes|dia)|pro\s+(?:woche|monat|tag))\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
                normalizedTitle,
                @"\b(?:leger|light|late|tardif|matin|midi|soir|nuit|morning|afternoon|evening|night|minuit|midnight)\b.{0,36}\b(?:entre|between|before|after|avant|apres|aprÃ¨s|vers|until|jusqu)\b|\b(?:entre|between|before|after|avant|apres|aprÃ¨s|vers|until|jusqu)\b.{0,36}\b(?:matin|midi|soir|nuit|morning|afternoon|evening|night|minuit|midnight|\d+\s*(?:h|heure|hour))\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }
}
