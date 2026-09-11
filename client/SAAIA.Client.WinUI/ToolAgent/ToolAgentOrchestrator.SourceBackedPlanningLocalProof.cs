using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool HasConcreteStructuredPlanningCandidateProof(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var finalEvidence = CollapseWhitespace(BuildPageLocalSourceBackedPlanningProofText(candidate.Hit));
        var normalizedFinalEvidence = NormalizeLexicalLookup(finalEvidence);
        if (normalizedFinalEvidence.Length < 24)
            return false;

        if (!normalizedFinalEvidence.Contains(normalizedTitle, StringComparison.Ordinal))
            return false;

        if (LooksLikeGenericCadenceOrTimingStatement(normalizedTitle))
            return false;

        return PrimaryEvidenceContainsLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, finalEvidence)
            || PrimaryEvidenceContainsRelaxedLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, finalEvidence);
    }

    private static bool HasPageLocalStructuredPlanningCandidateSupport(SourceBackedOptionCandidate candidate)
    {
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || LooksLikeGenericCadenceOrTimingStatement(normalizedTitle))
        {
            return false;
        }

        if (HasPageAnchoredContentCardStructuredPlanningProof(candidate.Hit, normalizedTitle))
            return true;

        var directEvidence = CollapseWhitespace(BuildPageLocalSourceBackedPlanningProofText(candidate.Hit));
        if (directEvidence.Length < 24)
            return false;

        var normalizedDirectEvidence = NormalizeLexicalLookup(directEvidence);
        var titleIndex = normalizedDirectEvidence.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (CandidateTitleIsDetachedFromStructuredProofByNavigationOrCompetingTitle(
                candidate,
                normalizedTitle,
                normalizedDirectEvidence))
        {
            return false;
        }

        var hasLocalStructuredProof =
            PrimaryEvidenceContainsLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, directEvidence)
            || PrimaryEvidenceContainsRelaxedLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, directEvidence)
            || PrimaryEvidenceContainsDenseLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, directEvidence)
            || TitleWindowContainsStrongLocalStructuredPlanningProof(normalizedTitle, directEvidence);
        if (hasLocalStructuredProof)
            return true;

        if (!PrimaryPageEvidenceSupportsSourceBackedPlanningCandidateTitle(candidate))
            return false;

        return titleIndex >= 0
            && ContentCardTitleAnchorsCandidateBeforeStructuredFields(
                candidate,
                normalizedTitle,
                normalizedDirectEvidence,
                titleIndex);
    }

    private static bool CandidateTitleIsDetachedFromStructuredProofByNavigationOrCompetingTitle(
        SourceBackedOptionCandidate candidate,
        string normalizedTitle,
        string normalizedProof)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedProof))
            return false;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        var hasDetachedOccurrence = false;
        foreach (Match match in Regex.Matches(normalizedProof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant))
        {
            var afterTitleStart = match.Index + match.Length;
            var afterTitle = normalizedProof[afterTitleStart..Math.Min(normalizedProof.Length, afterTitleStart + 520)];
            var firstFieldLabel = Regex.Match(
                afterTitle,
                @"\b(?:components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|items?|[eÃ©]l[eÃ©]ments?|values?|valeurs?|parameters?|param[eÃ¨]tres?|quantit(?:y|ies)|quantit[eÃ©]s?|preparation|pr[eÃ©]paration|procedure|proc[eÃ©]dure|instructions?|method|m[eÃ©]thode|steps?|[eÃ©]tapes?|technique|operation|workflow)\b",
                RegexOptions.CultureInvariant);
            if (!firstFieldLabel.Success)
                continue;

            var bridgeStart = afterTitleStart;
            var bridgeEnd = afterTitleStart + firstFieldLabel.Index;
            var bridge = normalizedProof[bridgeStart..bridgeEnd];
            if (LooksLikeStructuredPlanningNavigationOrIndexNoise(bridge)
                || StructuredPlanningBridgeContainsCompetingCandidateTitle(
                    candidate,
                    normalizedTitle,
                    normalizedProof,
                    bridgeStart,
                    bridgeEnd))
            {
                hasDetachedOccurrence = true;
                continue;
            }

            return false;
        }

        return hasDetachedOccurrence;
    }

    private static bool TitleWindowContainsStrongLocalStructuredPlanningProof(
        string normalizedTitle,
        string evidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(evidence))
            return false;

        var normalizedEvidence = NormalizeLexicalLookup(evidence);
        if (normalizedEvidence.Length < 32)
            return false;

        var searchIndex = 0;
        while (searchIndex < normalizedEvidence.Length)
        {
            var index = normalizedEvidence.IndexOf(normalizedTitle, searchIndex, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var start = Math.Max(0, index - 40);
            var end = Math.Min(normalizedEvidence.Length, index + normalizedTitle.Length + 420);
            var window = normalizedEvidence[start..end];
            if (window.Contains(normalizedTitle, StringComparison.Ordinal)
                && !LooksLikeStructuredPlanningNavigationOrIndexNoise(window)
                && HasStrongLocalStructuredPlanningProofText(window))
            {
                return true;
            }

            searchIndex = index + normalizedTitle.Length;
        }

        return false;
    }

    private static int CountStructuredFieldLabelFamilies(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        var families = new HashSet<string>(StringComparer.Ordinal);
        if (Regex.IsMatch(
                normalizedText,
                @"\b(?:components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|items?|[eÃ©]l[eÃ©]ments?|values?|valeurs?|parameters?|param[eÃ¨]tres?|quantit(?:y|ies)|quantit[eÃ©]s?)\b",
                RegexOptions.CultureInvariant))
        {
            families.Add("inputs");
        }

        if (Regex.IsMatch(
                normalizedText,
                @"\b(?:preparation|pr[eÃ©]paration|procedure|proc[eÃ©]dure|instructions?|method|m[eÃ©]thode|steps?|[eÃ©]tapes?|technique|operation|workflow|actions?|tasks?|taches?|tÃ¢ches?)\b",
                RegexOptions.CultureInvariant))
        {
            families.Add("process");
        }

        if (Regex.IsMatch(
                normalizedText,
                @"\b(?:constraints?|contraintes?|notes?|observations?|criteria|criteres|crit[eÃ¨]res|conditions?|checklist|controle|contr[oÃ´]le|verification|v[eÃ©]rification|validation|review|revue)\b",
                RegexOptions.CultureInvariant))
        {
            families.Add("checks");
        }

        return families.Count;
    }

    private static bool HasConcreteStructuredPlanningCardProof(
        SourceBackedOptionCandidate candidate,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || LooksLikeGenericCadenceOrTimingStatement(normalizedTitle))
        {
            return false;
        }

        foreach (var card in candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            if (HasPageAnchoredContentCardStructuredPlanningProof(candidate.Hit, card, normalizedTitle))
                return true;

            if (!HasConcreteContentCardEvidenceForTitle(card, normalizedTitle, requireExactTitle: true))
                continue;
            if (!ContentCardEvidenceIsSupportedByPrimaryPageText(candidate.Hit, card, normalizedTitle))
                continue;

            var strictEvidence = BuildSourceBackedCardStrictEvidenceSnippet(card);
            var normalizedEvidence = NormalizeLexicalLookup(strictEvidence);
            if (normalizedEvidence.Length < 16)
                continue;

            if (!normalizedEvidence.Contains(normalizedTitle, StringComparison.Ordinal))
                continue;

            if (PrimaryEvidenceContainsLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, strictEvidence))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PrimaryContentTermsTightlySupportPlanningTitle(string normalizedTitle, string normalizedPrimaryEvidence)
    {
        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length < 2 || titleTerms.Length > 6)
            return false;

        var matchedTerms = titleTerms.Count(term => normalizedPrimaryEvidence.Contains(term, StringComparison.Ordinal));
        return matchedTerms == titleTerms.Length;
    }

    private static bool PrimaryEvidenceContainsLocalStructuredPlanningProof(
        RagHitSummary hit,
        string normalizedTitle,
        string evidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(evidence))
            return false;

        var normalizedEvidence = NormalizeLexicalLookup(evidence);
        if (normalizedEvidence.Length < 24)
            return false;

        var searchIndex = 0;
        while (searchIndex < normalizedEvidence.Length)
        {
            var index = normalizedEvidence.IndexOf(normalizedTitle, searchIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            var start = Math.Max(0, index - 80);
            var end = Math.Min(normalizedEvidence.Length, index + normalizedTitle.Length + 360);
            var window = normalizedEvidence[start..end];
            if (window.Contains(normalizedTitle, StringComparison.Ordinal)
                && StructuredPlanningProofAppearsAttachedToTitle(window, normalizedTitle)
                && PrimaryEvidenceHasConcreteStructuredPlanningCues(hit, window)
                && HasStrongLocalStructuredPlanningProofText(window))
            {
                return true;
            }

            searchIndex = index + normalizedTitle.Length;
        }

        return CompactEvidenceContainsLocalStructuredPlanningProof(hit, normalizedTitle, normalizedEvidence);
    }

    private static bool CompactEvidenceContainsLocalStructuredPlanningProof(
        RagHitSummary hit,
        string normalizedTitle,
        string normalizedEvidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedEvidence))
            return false;

        var compactTitle = BuildCompactStructuredPlanningLookup(normalizedTitle);
        if (compactTitle.Length < 10)
            return FuzzyTitleTermsAppearInLocalStructuredPlanningProof(hit, normalizedTitle, normalizedEvidence);

        var compactEvidence = BuildCompactStructuredPlanningLookup(normalizedEvidence);
        if (!compactEvidence.Contains(compactTitle, StringComparison.Ordinal))
            return FuzzyTitleTermsAppearInLocalStructuredPlanningProof(hit, normalizedTitle, normalizedEvidence);

        if (LooksLikeStructuredPlanningNavigationOrIndexNoise(normalizedEvidence)
            && !HasStrongLocalStructuredPlanningProofText(normalizedEvidence))
        {
            return false;
        }

        return PrimaryEvidenceHasConcreteStructuredPlanningCues(hit, normalizedEvidence)
            && HasStrongLocalStructuredPlanningProofText(normalizedEvidence);
    }

    private static bool FuzzyTitleTermsAppearInLocalStructuredPlanningProof(
        RagHitSummary hit,
        string normalizedTitle,
        string normalizedEvidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedEvidence))
            return false;

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (titleTerms.Length < 2)
            return false;

        var matched = new List<int>(titleTerms.Length);
        foreach (var term in titleTerms)
        {
            var index = normalizedEvidence.IndexOf(term, StringComparison.Ordinal);
            if (index >= 0)
                matched.Add(index);
        }

        var requiredMatches = titleTerms.Length <= 3
            ? titleTerms.Length
            : Math.Max(3, (int)Math.Ceiling(titleTerms.Length * 0.6));
        if (matched.Count < requiredMatches)
            return false;

        var first = matched.Min();
        var last = matched.Max();
        if (last - first > 520)
            return false;

        var start = Math.Max(0, first - 96);
        var end = Math.Min(normalizedEvidence.Length, last + 720);
        var window = normalizedEvidence[start..end];
        if (window.Length < 48)
            return false;

        if (LooksLikeStructuredPlanningNavigationOrIndexNoise(window[..Math.Min(window.Length, 260)])
            && !HasStrongLocalStructuredPlanningProofText(window))
        {
            return false;
        }

        return PrimaryEvidenceHasConcreteStructuredPlanningCues(hit, window)
            && HasStrongLocalStructuredPlanningProofText(window);
    }

    private static string BuildCompactStructuredPlanningLookup(string? value)
        => Regex.Replace(
            NormalizeLexicalLookup(value),
            @"[^\p{L}\p{N}]+",
            string.Empty,
            RegexOptions.CultureInvariant);

    private static bool PrimaryEvidenceContainsRelaxedLocalStructuredPlanningProof(
        RagHitSummary hit,
        string normalizedTitle,
        string evidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(evidence))
            return false;

        var normalizedEvidence = NormalizeLexicalLookup(evidence);
        if (normalizedEvidence.Length < 32)
            return false;

        var searchIndex = 0;
        while (searchIndex < normalizedEvidence.Length)
        {
            var index = normalizedEvidence.IndexOf(normalizedTitle, searchIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            var start = Math.Max(0, index - 40);
            var end = Math.Min(normalizedEvidence.Length, index + normalizedTitle.Length + 760);
            var window = normalizedEvidence[start..end];
            var titleIndex = window.IndexOf(normalizedTitle, StringComparison.Ordinal);
            if (titleIndex < 0)
            {
                searchIndex = index + normalizedTitle.Length;
                continue;
            }

            var afterTitle = window[(titleIndex + normalizedTitle.Length)..];
            if (afterTitle.Length < 18)
            {
                searchIndex = index + normalizedTitle.Length;
                continue;
            }

            var earlyAfterTitle = afterTitle[..Math.Min(afterTitle.Length, 260)];
            if (LooksLikeStructuredPlanningNavigationOrIndexNoise(earlyAfterTitle)
                || Regex.IsMatch(
                    earlyAfterTitle,
                    @"\b(?:sommaire|contents?|table\s+des\s+matieres|table\s+of\s+contents|index|liste\s+des|list\s+of|page\s+\d+|p\.\s*\d+)\b",
                    RegexOptions.CultureInvariant))
            {
                searchIndex = index + normalizedTitle.Length;
                continue;
            }

            if (PrimaryEvidenceHasConcreteStructuredPlanningCues(hit, window)
                && HasStrongLocalStructuredPlanningProofText(window))
            {
                return true;
            }

            searchIndex = index + normalizedTitle.Length;
        }

        return CompactEvidenceContainsLocalStructuredPlanningProof(hit, normalizedTitle, normalizedEvidence);
    }

    private static bool PrimaryEvidenceContainsDenseLocalStructuredPlanningProof(
        RagHitSummary hit,
        string normalizedTitle,
        string evidence)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(evidence))
            return false;

        var normalizedEvidence = NormalizeLexicalLookup(evidence);
        if (normalizedEvidence.Length < 48)
            return false;

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Where(static term => term.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length == 0 || titleTerms.Length > 8)
            return false;

        var searchIndex = 0;
        while (searchIndex < normalizedEvidence.Length)
        {
            var index = normalizedEvidence.IndexOf(normalizedTitle, searchIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            var start = Math.Max(0, index - 48);
            var end = Math.Min(normalizedEvidence.Length, index + normalizedTitle.Length + 720);
            var window = normalizedEvidence[start..end];
            var titleIndex = window.IndexOf(normalizedTitle, StringComparison.Ordinal);
            if (titleIndex < 0)
            {
                searchIndex = index + normalizedTitle.Length;
                continue;
            }

            var beforeTitle = window[..titleIndex];
            var afterTitle = window[(titleIndex + normalizedTitle.Length)..];
            if (afterTitle.Length < 36
                || LooksLikeStructuredPlanningNavigationOrIndexNoise(beforeTitle)
                || LooksLikeStructuredPlanningNavigationOrIndexNoise(afterTitle[..Math.Min(afterTitle.Length, 260)]))
            {
                searchIndex = index + normalizedTitle.Length;
                continue;
            }

            if (!DenseStructuredPlanningProofAppearsAttachedToTitle(afterTitle))
            {
                searchIndex = index + normalizedTitle.Length;
                continue;
            }

            if (PrimaryEvidenceHasDenseStructuredPlanningCues(hit, afterTitle)
                && titleTerms.Count(term => window.Contains(term, StringComparison.Ordinal)) >= Math.Min(titleTerms.Length, 2))
            {
                return true;
            }

            searchIndex = index + normalizedTitle.Length;
        }

        return false;
    }

    private static bool DenseStructuredPlanningProofAppearsAttachedToTitle(string normalizedAfterTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedAfterTitle))
            return false;

        var early = normalizedAfterTitle[..Math.Min(normalizedAfterTitle.Length, 320)];
        if (Regex.IsMatch(
                early,
                @"\b(?:sommaire|contents?|table\s+des\s+matieres|table\s+of\s+contents|index|liste\s+des|list\s+of|page\s+\d+|p\.\s*\d+)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return Regex.IsMatch(
                early,
                @"(?:[:;.]|\s-\s|Ã¢â‚¬Â¢|\d+\s*(?:g|kg|mg|ml|cl|l|min|minutes?|h|heures?|hours?|%|mm|cm|m|units?|pieces?|items?))",
                RegexOptions.CultureInvariant)
            || CountBulletListMarkers(early) >= 1
            || CountNumericFactMarkers(early) >= 1;
    }

    private static bool PrimaryEvidenceHasDenseStructuredPlanningCues(RagHitSummary hit, string localEvidence)
    {
        var normalized = NormalizeStructuredScanText(localEvidence);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (LooksLikeStructuredPlanningNavigationOrIndexNoise(localEvidence))
            return false;

        var hasMeasuredFacts = CountMeasuredValueMarkers(normalized) >= 1
            || CountNumericFactMarkers(normalized) >= 2;
        var hasProcedureShape = CountProcedureStepMarkers(normalized) >= 1
            || CountBulletListMarkers(localEvidence) >= 2;
        var hasActionCue = Regex.IsMatch(
            normalized,
            @"\b(?:ajouter|add|retirer|remove|modifier|modify|adapter|adapt|utiliser|use|inspecter|inspect|record|enregistrer|consigner|noter|note|documenter|document|escalader|escalate|verifier|v[eÃƒÂ©]rifier|verify|check|valider|validate|prepare|preparer|pr[eÃƒÂ©]parer|executer|ex[eÃƒÂ©]cuter|run|start|stop|ouvrir|open|fermer|close|selectionner|s[eÃƒÂ©]lectionner|select|placer|place|mettre|put)\b",
            RegexOptions.CultureInvariant);
        var hasLabelValueSequence = LooksLikeGenericStructuredLabelValueSequence(localEvidence, normalized);
        var hasPageCues = ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 4
            || ComputeProcedureCompletenessCueScore(hit) >= 6;

        return (hasMeasuredFacts && (hasActionCue || hasProcedureShape || hasLabelValueSequence))
            || (hasProcedureShape && (hasActionCue || hasPageCues))
            || (hasLabelValueSequence && (hasMeasuredFacts || hasActionCue));
    }

    private static bool StructuredPlanningProofAppearsAttachedToTitle(
        string normalizedWindow,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedWindow) || string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var titleIndex = normalizedWindow.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (titleIndex < 0)
            return false;

        var afterTitle = normalizedWindow[(titleIndex + normalizedTitle.Length)..];
        if (afterTitle.Length < 12)
            return false;

        var structureMatch = Regex.Match(
            afterTitle,
            @"\b(?:components?|preparation|pr[eÃ©]paration|etapes?|[eÃ©]tapes?|steps?|m[eÃ©]thode|methode|method|procedure|proc[eÃ©]dure|instructions?|quantites?|quantit[eÃ©]s?|quantities?|materiel|mat[eÃ©]riel|materials?|elements?|[eÃ©]l[eÃ©]ments?|components?|composants?|requirements?|exigences?|constraints?|contraintes?|notes?|observations?|criteria|criteres|crit[eÃ¨]res|conditions?|parameters?|param[eÃ¨]tres?|checklist|controle|contr[oÃ´]le|verification|v[eÃ©]rification|validation|review|revue)\b",
            RegexOptions.CultureInvariant);
        if (!structureMatch.Success)
            return false;

        var bridge = afterTitle[..structureMatch.Index];
        if (bridge.Length > 220)
            return false;

        if (LooksLikeStructuredPlanningNavigationOrIndexNoise(bridge))
            return false;

        if (Regex.IsMatch(
                bridge,
                @"\b(?:sommaire|contents?|table\s+des\s+matieres|table\s+of\s+contents|index|liste\s+des|list\s+of|page\s+\d+|p\.\s*\d+)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var compactBridge = CollapseWhitespace(bridge);
        if (compactBridge.Length > 0
            && Regex.IsMatch(compactBridge, @"(?:^|[.;:!?])\s*[a-z0-9][a-z0-9\s'\-]{12,}\s*(?:[.;:!?]|$)", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(compactBridge, @"\b(?:pour|with|avec|aux|a\s+la|et|de|du|des|the|and)\b", RegexOptions.CultureInvariant))
        {
            return false;
        }

        return true;
    }

    private static bool PrimaryEvidenceHasConcreteStructuredPlanningCues(RagHitSummary hit, string primaryEvidence)
    {
        var normalized = NormalizeStructuredScanText(primaryEvidence);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (LooksLikeStructuredPlanningNavigationOrIndexNoise(primaryEvidence)
            && !HasStrongLocalStructuredPlanningProofText(primaryEvidence))
        {
            return false;
        }

        var hasStructureLabel = Regex.IsMatch(
            normalized,
            @"\b(?:components?|preparation|pr[eÃ©]paration|etapes?|[eÃ©]tapes?|steps?|m[eÃ©]thode|methode|method|procedure|proc[eÃ©]dure|instructions?|quantites?|quantit[eÃ©]s?|quantities?|materiel|mat[eÃ©]riel|materials?|elements?|[eÃ©]l[eÃ©]ments?|requirements?|exigences?|constraints?|contraintes?|notes?|observations?|valeurs?|values?|components?|composants?|operation|workflow|actions?|tasks?|taches?|tÃ¢ches?|criteria|criteres|crit[eÃ¨]res|conditions?|parameters?|param[eÃ¨]tres?|checklist|controle|contr[oÃ´]le|verification|v[eÃ©]rification|validation|review|revue)\b",
            RegexOptions.CultureInvariant);
        var hasActionOrMeasure = Regex.IsMatch(
            normalized,
            @"\b(?:\d+\s*(?:g|kg|mg|ml|cl|l|min|minutes?|h|heures?|hours?|%|mm|cm|m|units?|pieces?|items?)|appliquer|apply|ajouter|add|utiliser|use|using|inspecter|inspect|record|enregistrer|consigner|noter|note|documenter|document|escalader|escalate|verifier|v[eÃ©]rifier|verify|check|valider|validate|prepare|preparer|pr[eÃ©]parer|executer|ex[eÃ©]cuter|run|start|stop|ouvrir|open|fermer|close|selectionner|s[eÃ©]lectionner|select|requirements?|exigences?|constraints?|contraintes?|conditions?|criteria|criteres|crit[eÃ¨]res|parameters?|param[eÃ¨]tres?|notes?|observations?|checklist|validation|review|revue|steps?|actions?|tasks?)\b",
            RegexOptions.CultureInvariant);

        if (hasStructureLabel && LooksLikeGenericStructuredLabelValueSequence(primaryEvidence, normalized))
            return true;

        if (hasStructureLabel && hasActionOrMeasure)
            return true;

        if (hasStructureLabel
            && (CountNumericFactMarkers(normalized) >= 1
                || CountProcedureStepMarkers(normalized) >= 1
                || CountBulletListMarkers(primaryEvidence) >= 2))
        {
            return true;
        }

        if ((ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 4
             || ComputeProcedureCompletenessCueScore(hit) >= 6)
            && hasStructureLabel)
        {
            return true;
        }

        return false;
    }

    private const int StructuredPlanningTextPredicateCacheMaxEntries = 8192;
    private static readonly object StructuredPlanningTextPredicateCacheGate = new();
    private static readonly Dictionary<string, bool> StructuredPlanningNavigationNoiseCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> StrongLocalStructuredPlanningProofTextCache = new(StringComparer.Ordinal);

    private static bool LooksLikeStructuredPlanningNavigationOrIndexNoise(string? text)
    {
        var cacheKey = CollapseWhitespace(text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(cacheKey))
            return false;

        lock (StructuredPlanningTextPredicateCacheGate)
        {
            if (StructuredPlanningNavigationNoiseCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var result = LooksLikeStructuredPlanningNavigationOrIndexNoiseUncached(cacheKey);
        lock (StructuredPlanningTextPredicateCacheGate)
        {
            if (StructuredPlanningNavigationNoiseCache.Count >= StructuredPlanningTextPredicateCacheMaxEntries)
                StructuredPlanningNavigationNoiseCache.Clear();

            StructuredPlanningNavigationNoiseCache[cacheKey] = result;
        }

        return result;
    }

    private static bool LooksLikeStructuredPlanningNavigationOrIndexNoiseUncached(string? text)
    {
        var raw = CollapseWhitespace(text ?? string.Empty);
        var normalized = NormalizeLexicalLookup(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(
            normalized,
            @"\b(?:table\s+des\s+matieres|sommaire|contents?|table\s+of\s+contents|index|catalogue|catalog|liste\s+des|list\s+of|sections?\s+principales?|premiers?\s+extraits?|matched\s+profile\s+title|source\s+de\s+verite|source\s+of\s+truth)\b",
            RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(
            normalized,
            @"\b(?:document\s+.+\s+\d+\s+pages?|ce\s+document\s+(?:couvre|contient|inclut)|this\s+document\s+(?:covers|contains|includes))\b",
            RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
                normalized,
                @"\b(?:page\s+\d+\s*[:;-]|p\.\s*\d+\s*[:;-])\b",
                RegexOptions.CultureInvariant)
            && LooksLikePageLocatorNavigationOnlySurface(raw, normalized);
    }

    private static bool LooksLikePageLocatorNavigationOnlySurface(string raw, string normalized)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(
            normalized,
            @"\b(?:sommaire|contents?|table\s+des\s+matieres|table\s+of\s+contents|index|catalogue|catalog|liste\s+des|list\s+of|sections?\s+principales?)\b",
            RegexOptions.CultureInvariant))
        {
            return true;
        }

        var structured = NormalizeStructuredScanText(raw);
        var hasStructuredBody =
            Regex.IsMatch(
                structured,
                @"\b(?:components?|preparation|pr[eÃƒÂ©]paration|procedure|proc[eÃƒÂ©]dure|instructions?|method|m[eÃƒÂ©]thode|steps?|[eÃƒÂ©]tapes?|operation|workflow|materials?|materiel|mat[eÃƒÂ©]riel|requirements?|exigences?|items?|elements?|[eÃƒÂ©]l[eÃƒÂ©]ments?|values?|valeurs?|parameters?|param[eÃƒÂ¨]tres?)\b",
                RegexOptions.CultureInvariant)
            && (LooksLikeGenericStructuredLabelValueSequence(raw, structured)
                || CountProcedureStepMarkers(structured) >= 1
                || CountMeasuredValueMarkers(structured) >= 1
                || CountNumericFactMarkers(structured) >= 2);
        if (hasStructuredBody)
            return false;

        var pageReferenceCount = Regex.Matches(
            normalized,
            @"\b(?:page|p)\.?\s*\d+\b",
            RegexOptions.CultureInvariant).Count;
        if (pageReferenceCount >= 3)
            return true;

        return normalized.Length <= 140;
    }

    private static bool HasStrongLocalStructuredPlanningProofText(string? text)
    {
        var value = CollapseWhitespace(text ?? string.Empty);
        if (value.Length < 24)
            return false;

        lock (StructuredPlanningTextPredicateCacheGate)
        {
            if (StrongLocalStructuredPlanningProofTextCache.TryGetValue(value, out var cached))
                return cached;
        }

        var result = HasStrongLocalStructuredPlanningProofTextUncached(value);
        lock (StructuredPlanningTextPredicateCacheGate)
        {
            if (StrongLocalStructuredPlanningProofTextCache.Count >= StructuredPlanningTextPredicateCacheMaxEntries)
                StrongLocalStructuredPlanningProofTextCache.Clear();

            StrongLocalStructuredPlanningProofTextCache[value] = result;
        }

        return result;
    }

    private static bool HasStrongLocalStructuredPlanningProofTextUncached(string value)
    {
        var normalized = NormalizeStructuredScanText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasStructureLabel = Regex.IsMatch(
            normalized,
            @"\b(?:components?|preparation|pr[eÃ©]paration|etapes?|[eÃ©]tapes?|steps?|m[eÃ©]thode|methode|method|procedure|proc[eÃ©]dure|instructions?|quantites?|quantit[eÃ©]s?|quantities?|materiel|mat[eÃ©]riel|materials?|elements?|[eÃ©]l[eÃ©]ments?|components?|composants?|requirements?|exigences?|constraints?|contraintes?|notes?|observations?|criteria|criteres|crit[eÃ¨]res|conditions?|parameters?|param[eÃ¨]tres?|checklist|controle|contr[oÃ´]le|verification|v[eÃ©]rification|validation|review|revue)\b",
            RegexOptions.CultureInvariant);
        if (!hasStructureLabel)
            return false;

        var hasGenericFieldSequence = LooksLikeGenericStructuredLabelValueSequence(value, normalized);
        var hasActionOrMeasure = Regex.IsMatch(
            normalized,
            @"\b(?:\d+\s*(?:g|kg|mg|ml|cl|l|min|minutes?|h|heure|heures|hours?|%|mm|cm|m|units?|pieces?|items?)|ajouter|add|retirer|remove|modifier|modify|adapter|adapt|utiliser|use|inspecter|inspect|record|enregistrer|consigner|noter|note|documenter|document|escalader|escalate|verifier|v[eÃ©]rifier|verify|check|valider|validate|executer|ex[eÃ©]cuter|run|selectionner|s[eÃ©]lectionner|select|requirements?|exigences?|constraints?|contraintes?|conditions?|criteria|criteres|crit[eÃ¨]res|parameters?|param[eÃ¨]tres?|notes?|observations?|checklist|validation|review|revue)\b",
            RegexOptions.CultureInvariant);
        var hasMeasuredFact = CountMeasuredValueMarkers(normalized) >= 1
            || CountNumericFactMarkers(normalized) >= 2;
        var hasProcedureShape = CountProcedureStepMarkers(normalized) >= 1
            || CountBulletListMarkers(value) >= 2;

        if (!(hasActionOrMeasure || hasMeasuredFact || hasProcedureShape || hasGenericFieldSequence))
            return false;

        if (LooksLikeStructuredPlanningNavigationOrIndexNoise(value)
            && !(hasActionOrMeasure && (hasMeasuredFact || hasProcedureShape)))
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeGenericStructuredLabelValueSequence(string rawText, string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(rawText) || string.IsNullOrWhiteSpace(normalizedText))
            return false;

        var labelValueCount = Regex.Matches(
            rawText,
            @"(?:^|[\s.!?;])[\p{L}][\p{L}'\u2019\-]{3,32}\s*(?:[:*]|\u2022)\s*\S",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        if (labelValueCount >= 2)
            return true;

        if (labelValueCount == 0)
            return false;

        var hasKnownProcessLabel = Regex.IsMatch(
            normalizedText,
            @"\b(?:preparation|pr[eÃƒÂ©]paration|procedure|proc[eÃƒÂ©]dure|instructions?|method|m[eÃƒÂ©]thode|steps?|[eÃƒÂ©]tapes?|operation|workflow|technique)\b",
            RegexOptions.CultureInvariant);
        if (!hasKnownProcessLabel)
            return false;

        return CountMeasuredValueMarkers(normalizedText) >= 1
            || CountNumericFactMarkers(normalizedText) >= 1
            || CountProcedureStepMarkers(normalizedText) >= 1
            || Regex.IsMatch(rawText, @"[:*]\s*[^.!?;:]{3,80}(?:,\s*[^.!?;:]{2,60})+", RegexOptions.CultureInvariant);
    }

    private static bool StructuredPlanningItemTermsAreFullySupported(
        string normalizedItem,
        IReadOnlyList<string> itemTerms,
        SourceBackedOptionCandidate candidate)
    {
        if (!PlanningCandidateHasAnswerSupportAnchor(candidate))
            return false;

        var directEvidence = NormalizeLexicalLookup(BuildPageLocalSourceBackedPlanningProofText(candidate.Hit));
        if (directEvidence.Length < 16)
            return false;

        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        if (string.Equals(normalizedItem, normalizedTitle, StringComparison.Ordinal))
            return true;

        var distinctItemTerms = itemTerms
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctItemTerms.Length == 0)
            return false;

        var missingTerms = distinctItemTerms
            .Where(term => !directEvidence.Contains(term, StringComparison.Ordinal)
                && !normalizedTitle.Contains(term, StringComparison.Ordinal))
            .ToArray();
        if (missingTerms.Length == 0)
            return true;

        return false;
    }

    private static bool PlanningCandidateHasAnswerSupportAnchor(SourceBackedOptionCandidate candidate)
        => PrimaryPageEvidenceContainsExactPlanningCandidateTitle(candidate)
           || HasPageLocalStructuredPlanningCandidateSupport(candidate)
           || PrimaryPageEvidenceSupportsSourceBackedPlanningCandidateTitle(candidate);

    private static IEnumerable<string> EnumeratePlanningCandidateSupportTexts(SourceBackedOptionCandidate candidate)
    {
        yield return candidate.Title;
        yield return candidate.Hit.SectionTitle ?? string.Empty;
        yield return candidate.Hit.HeadingPath ?? string.Empty;
        yield return candidate.Hit.Excerpt ?? string.Empty;
        yield return candidate.Hit.FullText ?? string.Empty;
        yield return candidate.Hit.ContextualSnippet ?? string.Empty;

        foreach (var card in candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            yield return card.Title;
            yield return card.Kind ?? string.Empty;
            foreach (var signal in card.Signals ?? Array.Empty<string>())
                yield return signal;

            if (card.Evidence is null)
                continue;

            foreach (var fact in card.Evidence.Facts ?? Array.Empty<RagHitEvidenceFactSummary>())
            {
                yield return fact.Label;
                yield return fact.Value ?? string.Empty;
                yield return fact.SourceText ?? string.Empty;
            }

            foreach (var quantity in card.Evidence.QuantityFacts ?? Array.Empty<RagHitQuantityFactSummary>())
            {
                yield return quantity.Label;
                yield return quantity.SourceText ?? string.Empty;
            }
        }
    }

    private static IEnumerable<string> ExtractPlanningAnswerSupportTerms(string normalized)
    {
        foreach (var term in ExtractQuerySignalTerms(normalized))
        {
            if (term.Length >= 4 && !IsGenericPlanningAnswerSupportTerm(term))
                yield return term;
        }
    }

    private static bool IsGenericPlanningAnswerSupportTerm(string term)
    {
        if (IsGenericPlanningCoverageTerm(term))
            return true;

        return term is
            "items" or "semaine" or "week" or "weekly" or
            "lundi" or "mardi" or "mercredi" or "jeudi" or "vendredi" or "samedi" or "dimanche" or
            "monday" or "tuesday" or "wednesday" or "thursday" or "friday" or "saturday" or "sunday" or
            "source" or "sources" or "page" or "pages" or "document" or "documents" or
            "option" or "options" or "proposition" or "propositions";
    }
}
