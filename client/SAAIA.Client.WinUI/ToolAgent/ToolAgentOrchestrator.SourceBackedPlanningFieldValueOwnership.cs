using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool CandidateTitleAppearsInsideStructuredFieldValueSequenceBeforeProcess(
        string normalizedTitle,
        string normalizedProof)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedProof))
            return false;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        foreach (Match match in Regex.Matches(normalizedProof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant))
        {
            var before = normalizedProof[Math.Max(0, match.Index - 220)..match.Index];
            if (!TitleMatchLooksEmbeddedInPrecedingStructuredField(before))
                continue;

            var afterStart = match.Index + match.Length;
            var after = normalizedProof[afterStart..Math.Min(normalizedProof.Length, afterStart + 240)];
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

    private static bool CandidateTitleAppearsInsideExplicitDataListBeforeProcess(
        string normalizedTitle,
        string normalizedProof)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedProof))
            return false;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        var titleMatches = Regex.Matches(normalizedProof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();
        if (titleMatches.Length == 0)
            return false;

        var fieldMatches = EnumerateStructuredDataListLabelsBeforeProcess(normalizedProof)
            .ToArray();
        if (fieldMatches.Length == 0)
            return false;

        foreach (var fieldMatch in fieldMatches)
        {
            foreach (var titleMatch in titleMatches.Where(match => match.Index > fieldMatch.Index))
            {
                var fieldEnd = fieldMatch.Index + fieldMatch.Length;
                if (titleMatch.Index < fieldEnd)
                    continue;

                var before = normalizedProof[fieldMatch.Index..titleMatch.Index];
                if (before.Length > 360)
                    continue;
                var betweenFieldAndTitle = normalizedProof[fieldEnd..titleMatch.Index];
                if (Regex.IsMatch(betweenFieldAndTitle, @"\b(?:preparation|procedure|instructions?|method|methode|steps?|etapes?|technique|operation|workflow)\b", RegexOptions.CultureInvariant))
                    continue;
                if (Regex.IsMatch(betweenFieldAndTitle, @"[.!?]", RegexOptions.CultureInvariant))
                    continue;
                if (!Regex.IsMatch(before, @"(?:[:;,]|\u2022|(?:^|\s)[*-]\s*)", RegexOptions.CultureInvariant))
                    continue;

                var afterStart = titleMatch.Index + titleMatch.Length;
                var after = normalizedProof[afterStart..Math.Min(normalizedProof.Length, afterStart + 260)];
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
        }

        return false;
    }

    private static bool CandidateTitleFirstOccurrenceAppearsInsideExplicitDataListBeforeProcess(
        string normalizedTitle,
        string normalizedProof)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedProof))
            return false;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        var titleMatch = Regex.Match(normalizedProof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant);
        if (!titleMatch.Success)
            return false;

        var fieldMatches = EnumerateStructuredDataListLabelsBeforeProcess(normalizedProof)
            .Where(match => match.Index < titleMatch.Index)
            .OrderByDescending(static match => match.Index)
            .ToArray();
        foreach (var fieldMatch in fieldMatches)
        {
            var fieldEnd = fieldMatch.Index + fieldMatch.Length;
            if (titleMatch.Index < fieldEnd)
                continue;

            var before = normalizedProof[fieldMatch.Index..titleMatch.Index];
            if (before.Length > 360)
                continue;
            var betweenFieldAndTitle = normalizedProof[fieldEnd..titleMatch.Index];
            if (Regex.IsMatch(betweenFieldAndTitle, @"\b(?:preparation|procedure|instructions?|method|methode|steps?|etapes?|technique|operation|workflow)\b", RegexOptions.CultureInvariant))
                continue;
            if (Regex.IsMatch(betweenFieldAndTitle, @"[.!?]", RegexOptions.CultureInvariant))
                continue;
            if (!Regex.IsMatch(before, @"(?:[:;,]|\u2022|(?:^|\s)[*-]\s*)", RegexOptions.CultureInvariant))
                continue;

            var afterStart = titleMatch.Index + titleMatch.Length;
            var after = normalizedProof[afterStart..Math.Min(normalizedProof.Length, afterStart + 260)];
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

    private static IEnumerable<Match> EnumerateStructuredDataListLabelsBeforeProcess(string normalizedProof)
    {
        foreach (Match match in Regex.Matches(
                     normalizedProof,
                     @"\b(?:(?:components?|composants?|constituents?|constituants?|inputs?|materials?|materiel|requirements?|exigences?|items?|elements?|values?|valeurs?|parameters?|parametres?|quantit(?:y|ies)|quantites?)\b\s*(?:[:*\-]|\u2022)?|(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}{2,24}\s*(?:[:*\-]|\u2022)?)",
                     RegexOptions.CultureInvariant))
        {
            yield return match;
        }

        foreach (Match match in Regex.Matches(
                     normalizedProof,
                     @"(?:^|[.!?]\s+)\s*\p{L}[\p{L}'\-]{2,32}\s*(?:[:*]|\u2022)\s+",
                     RegexOptions.CultureInvariant))
        {
            if (GenericColonLabelIntroducesDataListBeforeProcess(normalizedProof, match))
                yield return match;
        }
    }

    private static bool GenericColonLabelIntroducesDataListBeforeProcess(string normalizedProof, Match fieldMatch)
    {
        var afterStart = fieldMatch.Index + fieldMatch.Length;
        if (afterStart >= normalizedProof.Length)
            return false;

        var after = normalizedProof[afterStart..Math.Min(normalizedProof.Length, afterStart + 520)];
        var processMatch = Regex.Match(
            after,
            @"\b(?:preparation|procedure|instructions?|method|methode|steps?|etapes?|technique|operation|workflow|checklist|validation|review|revue)\b",
            RegexOptions.CultureInvariant);
        if (!processMatch.Success)
            return false;

        var listWindow = after[..processMatch.Index];
        if (listWindow.Length < 12)
            return false;

        return Regex.IsMatch(listWindow, @"[,;]|\u2022|(?:^|\s)[*-]\s*", RegexOptions.CultureInvariant)
            && ExtractPlanningAnswerSupportTerms(listWindow)
                .Where(static term => term.Length >= 3)
                .Take(4)
                .Count() >= 3;
    }

    private static bool TitleAfterLooksLikeOwnStructuredFieldThenProcessSection(string normalizedAfterTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedAfterTitle))
            return false;

        var fieldMatch = Regex.Match(
            normalizedAfterTitle,
            @"\b(?:components?|composants?|constituents?|constituants?|inputs?|materials?|materiel|requirements?|exigences?|items?|elements?|values?|valeurs?|parameters?|parametres?|quantit(?:y|ies)|quantites?)\b",
            RegexOptions.CultureInvariant);
        if (!fieldMatch.Success || fieldMatch.Index > 90)
            return false;

        var processMatch = Regex.Match(
            normalizedAfterTitle,
            @"\b(?:preparation|procedure|instructions?|method|methode|steps?|etapes?|technique|operation|workflow|checklist|validation|review|revue)\b",
            RegexOptions.CultureInvariant);
        return processMatch.Success && processMatch.Index > fieldMatch.Index;
    }

    private static bool ContentCardTitleVariantOwnsStructuredProcessSection(
        SourceBackedOptionCandidate candidate,
        string normalizedTitle,
        string normalizedProof)
    {
        if (candidate.Hit.MatchedContentCards is not { Count: > 0 } cards
            || string.IsNullOrWhiteSpace(normalizedTitle)
            || string.IsNullOrWhiteSpace(normalizedProof)
            || !cards.Any(card =>
                ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card)
                && ExtractSourceBackedCardTitleVariants(card.Title)
                    .Select(title => NormalizeLexicalLookup(CleanSourceBackedOptionTitle(title)))
                    .Any(title => string.Equals(title, normalizedTitle, StringComparison.Ordinal))))
        {
            return false;
        }

        if (CandidateTitleAppearsInsideExplicitDataListBeforeProcess(normalizedTitle, normalizedProof))
            return false;

        if (CandidateTitleAppearsInsideRawStructuredFieldListBeforeProcess(
                CleanSourceBackedOptionTitle(candidate.Title),
                BuildStructuredPlanningFieldValueProofText(candidate)))
        {
            return false;
        }

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        foreach (Match match in Regex.Matches(normalizedProof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant))
        {
            var before = normalizedProof[Math.Max(0, match.Index - 180)..match.Index];
            if (TitleMatchLooksEmbeddedInPrecedingStructuredField(before))
                continue;

            var afterStart = match.Index + match.Length;
            var after = normalizedProof[afterStart..Math.Min(normalizedProof.Length, afterStart + 320)];
            var processMatch = Regex.Match(
                after,
                @"\b(?:preparation|procedure|instructions?|method|methode|steps?|etapes?|technique|operation|workflow|checklist|validation|review|revue)\b",
                RegexOptions.CultureInvariant);
            if (!processMatch.Success || processMatch.Index > 120)
                continue;

            if (TitleAfterLooksLikeOwnStructuredFieldThenProcessSection(after)
                || Regex.IsMatch(
                    after,
                    @"\b(?:quantit(?:y|ies)|quantites?|values?|valeurs?|materials?|materiel|components?|composants?|requirements?|exigences?|items?|elements?)\b",
                    RegexOptions.CultureInvariant)
                || CountNumericFactMarkers(after) >= 1)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CandidateTitleAppearsInsideRawStructuredFieldListBeforeProcess(
        string rawTitle,
        string rawProof)
    {
        rawTitle = CollapseWhitespace(rawTitle);
        rawProof = CollapseWhitespace(rawProof);
        if (string.IsNullOrWhiteSpace(rawTitle)
            || string.IsNullOrWhiteSpace(rawProof)
            || rawProof.Length < rawTitle.Length + 16)
        {
            return false;
        }

        var titlePattern = Regex.Escape(rawTitle).Replace("\\ ", @"\s+");
        foreach (Match match in Regex.Matches(rawProof, @"\b" + titlePattern + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var before = rawProof[Math.Max(0, match.Index - 240)..match.Index];
            var sentenceBoundary = Math.Max(
                Math.Max(before.LastIndexOf(".", StringComparison.Ordinal), before.LastIndexOf("!", StringComparison.Ordinal)),
                before.LastIndexOf("?", StringComparison.Ordinal));
            var localBefore = sentenceBoundary >= 0 ? before[(sentenceBoundary + 1)..] : before;
            if (!Regex.IsMatch(
                    localBefore,
                    @"\b(?:components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|items?|[eÃ©]l[eÃ©]ments?|values?|valeurs?|parameters?|param[eÃ¨]tres?|quantit(?:y|ies)|quantit[eÃ©]s?)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            if (Regex.IsMatch(
                    localBefore,
                    @"\b(?:preparation|pr[eÃ©]paration|procedure|proc[eÃ©]dure|instructions?|method|m[eÃ©]thode|steps?|[eÃ©]tapes?|technique|operation|workflow)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            if (!Regex.IsMatch(
                    localBefore,
                    @"(?:[,;]\s*|(?:^|\s)[*-]\s*)[^.!?]{0,140}$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            var afterStart = match.Index + match.Length;
            var after = rawProof[afterStart..Math.Min(rawProof.Length, afterStart + 260)];
            if (Regex.IsMatch(
                    after,
                    @"\b(?:preparation|pr[eÃ©]paration|procedure|proc[eÃ©]dure|instructions?|method|m[eÃ©]thode|steps?|[eÃ©]tapes?|technique|operation|workflow|checklist|validation|review|revue)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CandidateTitleHasLeadingOccurrenceBeforeStructuredFields(
        string? normalizedTitle,
        string normalizedProof)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedProof))
            return false;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        var titleMatches = Regex.Matches(normalizedProof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();
        if (titleMatches.Length == 0)
            return false;

        var firstStructuredField = Regex.Match(
            normalizedProof,
            @"\b(?:components?|composants?|constituents?|constituants?|inputs?|materials?|materiel|requirements?|exigences?|items?|elements?|values?|valeurs?|parameters?|parametres?|quantit(?:y|ies)|quantites?|preparation|procedure|instructions?|method|methode|steps?|etapes?|technique|operation|workflow|checklist|validation|review|revue)\b",
            RegexOptions.CultureInvariant);
        if (!firstStructuredField.Success)
            return titleMatches.Any(static match => match.Index <= 120);

        return titleMatches.Any(match => match.Index < firstStructuredField.Index);
    }

    private static bool CandidateTitleOwnsLocalStructuredPlanningSection(
        string normalizedTitle,
        string normalizedProof)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedProof))
            return false;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        foreach (Match match in Regex.Matches(normalizedProof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant))
        {
            var afterTitleStart = match.Index + match.Length;
            var afterTitle = normalizedProof[afterTitleStart..Math.Min(normalizedProof.Length, afterTitleStart + 260)];
            if (!Regex.IsMatch(
                    afterTitle,
                    @"\b(?:components?|composants?|constituents?|constituants?|inputs?|materials?|materiel|requirements?|exigences?|items?|elements?|values?|valeurs?|parameters?|parametres?|quantit(?:y|ies)|quantites?|preparation|procedure|instructions?|method|methode|steps?|etapes?|technique|operation|workflow|checklist|validation|review|revue)\b",
                    RegexOptions.CultureInvariant))
            {
                continue;
            }

            var before = normalizedProof[Math.Max(0, match.Index - 180)..match.Index];
            if (!TitleMatchLooksEmbeddedInPrecedingStructuredField(before))
                return true;
        }

        return false;
    }

    private static bool TitleMatchLooksEmbeddedInPrecedingStructuredField(string normalizedBeforeTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedBeforeTitle))
            return false;

        var before = normalizedBeforeTitle.Length > 180
            ? normalizedBeforeTitle[^180..]
            : normalizedBeforeTitle;
        var fieldMatch = Regex.Match(
            before,
            @"\b(?:components?|composants?|constituents?|constituants?|inputs?|materials?|materiel|requirements?|exigences?|items?|elements?|values?|valeurs?|parameters?|parametres?|quantit(?:y|ies)|quantites?)\b(?<tail>.{0,300})$",
            RegexOptions.CultureInvariant);
        if (!fieldMatch.Success)
            return false;

        var tail = fieldMatch.Groups["tail"].Value;
        if (Regex.IsMatch(
                tail,
                @"\b(?:preparation|procedure|instructions?|method|methode|steps?|etapes?|technique|operation|workflow)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(tail, @"[.!?]\s*$", RegexOptions.CultureInvariant))
            return false;

        return true;
    }

    private static bool ContentCardTitleAnchorsCandidateBeforeStructuredFields(
        SourceBackedOptionCandidate candidate,
        string normalizedTitle,
        string normalizedProof,
        int titleIndex)
    {
        if (candidate.Hit.MatchedContentCards is not { Count: > 0 } cards
            || string.IsNullOrWhiteSpace(normalizedTitle)
            || string.IsNullOrWhiteSpace(normalizedProof)
            || titleIndex < 0
            || titleIndex > 180)
        {
            return false;
        }

        var hasMatchingTitleAnchor = cards.Any(card =>
            ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card)
            && ExtractSourceBackedCardTitleVariants(card.Title)
                .Select(title => NormalizeLexicalLookup(CleanSourceBackedOptionTitle(title)))
                .Any(title => string.Equals(title, normalizedTitle, StringComparison.Ordinal)));
        if (!hasMatchingTitleAnchor)
            return false;

        var firstFieldLabel = Regex.Match(
            normalizedProof,
            @"\b(?:components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|items?|[eÃ©]l[eÃ©]ments?|values?|valeurs?|parameters?|param[eÃ¨]tres?|quantit(?:y|ies)|quantit[eÃ©]s?|preparation|pr[eÃ©]paration|procedure|proc[eÃ©]dure|instructions?|method|m[eÃ©]thode|steps?|[eÃ©]tapes?|technique|operation|workflow)\b",
            RegexOptions.CultureInvariant);
        var afterTitleIndex = titleIndex + normalizedTitle.Length;
        if (!firstFieldLabel.Success
            || firstFieldLabel.Index <= afterTitleIndex
            || afterTitleIndex > normalizedProof.Length)
        {
            return false;
        }

        var bridgeToFirstField = normalizedProof[afterTitleIndex..firstFieldLabel.Index];
        if (LooksLikeStructuredPlanningNavigationOrIndexNoise(bridgeToFirstField)
            || StructuredPlanningBridgeContainsCompetingCandidateTitle(
                candidate,
                normalizedTitle,
                normalizedProof,
                afterTitleIndex,
                firstFieldLabel.Index))
        {
            return false;
        }

        var titleWindow = normalizedProof[titleIndex..Math.Min(normalizedProof.Length, titleIndex + normalizedTitle.Length + 520)];
        return !LooksLikeStructuredPlanningNavigationOrIndexNoise(titleWindow)
            && CountStructuredFieldLabelFamilies(titleWindow) >= 2;
    }

    private static bool StructuredPlanningBridgeContainsCompetingCandidateTitle(
        SourceBackedOptionCandidate candidate,
        string normalizedTitle,
        string normalizedProof,
        int startIndex,
        int endIndex)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || string.IsNullOrWhiteSpace(normalizedProof)
            || startIndex < 0
            || endIndex <= startIndex
            || startIndex >= normalizedProof.Length)
        {
            return false;
        }

        endIndex = Math.Min(endIndex, normalizedProof.Length);
        var bridge = normalizedProof[startIndex..endIndex];
        if (bridge.Length < 4)
            return false;

        var proofText = BuildStructuredPlanningFieldValueProofText(candidate);
        var competingTitles = ExtractPlanItemTitleCandidatesV2(proofText)
            .Concat(ExtractSourceBackedTitleCandidates(candidate.Hit))
            .Concat((candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
                .SelectMany(card => ExtractSourceBackedCardTitleVariants(card.Title)))
            .Select(CleanSourceBackedOptionTitle)
            .Where(IsUsableSourceBackedOptionTitle)
            .Select(NormalizeLexicalLookup)
            .Where(title => !string.IsNullOrWhiteSpace(title)
                && !string.Equals(title, normalizedTitle, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var competingTitle in competingTitles)
        {
            if (competingTitle.Length < 6)
                continue;

            var competingPattern = Regex.Escape(competingTitle).Replace("\\ ", @"\s+");
            if (Regex.IsMatch(bridge, @"\b" + competingPattern + @"\b", RegexOptions.CultureInvariant))
                return true;
        }

        return false;
    }

    private static bool ContentCardEvidenceNamesDifferentStructuredPlanningItem(
        SourceBackedOptionCandidate candidate,
        string normalizedCandidateTitle)
    {
        if (candidate.Hit.MatchedContentCards is not { Count: > 0 } cards)
            return false;

        foreach (var card in cards)
        {
            var normalizedCardTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(card.Title));
            if (!string.Equals(normalizedCardTitle, normalizedCandidateTitle, StringComparison.Ordinal))
                continue;

            foreach (var fact in card.Evidence?.Facts ?? Array.Empty<RagHitEvidenceFactSummary>())
            {
                foreach (var factTitle in EnumerateCheapContentCardEvidenceItemTitles(fact))
                {
                    if (!string.Equals(factTitle, normalizedCandidateTitle, StringComparison.Ordinal))
                        return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateCheapContentCardEvidenceItemTitles(RagHitEvidenceFactSummary fact)
    {
        foreach (var value in new[] { fact.Label })
        {
            var title = NormalizeCheapContentCardEvidenceItemTitle(value);
            if (!string.IsNullOrWhiteSpace(title))
                yield return title;
        }

        var sourceText = CollapseWhitespace(fact.SourceText ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sourceText))
            yield break;

        var clippedSourceText = sourceText.Length > 700 ? sourceText[..700] : sourceText;
        foreach (var value in ExtractPlanItemTitleCandidatesV2(clippedSourceText).Take(12))
        {
            var title = NormalizeCheapContentCardEvidenceItemTitle(value);
            if (!string.IsNullOrWhiteSpace(title))
                yield return title;
        }
    }

    private static string? NormalizeCheapContentCardEvidenceItemTitle(string? value)
    {
        var cleaned = CleanSourceBackedOptionTitle(value);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        var normalized = NormalizeLexicalLookup(cleaned);
        if (string.IsNullOrWhiteSpace(normalized)
            || LooksLikePlanItemNoise(cleaned)
            || LooksLikeProcedureSentenceTitle(normalized)
            || IsGenericStructuredPlanningEvidenceBridgeTerm(normalized)
            || !IsUsefulSourceBackedDisplayTitle(cleaned))
        {
            return null;
        }

        var terms = ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 3)
            .Take(8)
            .ToArray();
        if (terms.Length is < 1 or > 7)
            return null;

        return normalized;
    }
}
