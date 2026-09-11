using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool LooksLikeDelimitedStructuredPlanningFieldValueCandidate(SourceBackedOptionCandidate candidate)
    {
        var rawTitle = CollapseWhitespace(candidate.Title);
        if (string.IsNullOrWhiteSpace(rawTitle)
            || rawTitle.IndexOfAny(new[] { ',', ';' }) < 0)
        {
            return false;
        }

        var normalizedTitle = NormalizeLexicalLookup(rawTitle);
        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Where(static term => term.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length is < 2 or > 4)
            return false;

        var segments = Regex.Split(rawTitle, @"\s*[,;]\s*")
            .Select(NormalizeLexicalLookup)
            .Where(static segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        if (segments.Length < 2
            || segments.Any(static segment => ExtractPlanningAnswerSupportTerms(segment).Count(term => term.Length >= 3) > 2))
        {
            return false;
        }

        var proof = NormalizeLexicalLookup(BuildStructuredPlanningFieldValueProofText(candidate));
        if (proof.Length < normalizedTitle.Length + 8)
            return false;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        if (Regex.IsMatch(
            proof,
            @"\b(?:components?|components?|components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|quantit(?:y|ies)|quantit[eÃ©]s?|values?|valeurs?|parameters?|param[eÃ¨]tres?|items?|[eÃ©]l[eÃ©]ments?)\b.{0,140}\b" + titlePattern + @"\b",
            RegexOptions.CultureInvariant))
        {
            return true;
        }

        return LooksLikeInlineDelimitedStructuredPlanningFieldValueCandidate(proof, normalizedTitle);
    }

    private static bool LooksLikeShortConnectorStructuredPlanningFieldValueCandidate(SourceBackedOptionCandidate candidate)
    {
        var cleanedTitle = CleanSourceBackedOptionTitle(candidate.Title);
        var normalizedTitle = NormalizeLexicalLookup(cleanedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || normalizedTitle.Length > 54
            || !Regex.IsMatch(normalizedTitle, @"\b(?:et|and|avec|with|con|com|und|e|y|&)\b|[,;/]", RegexOptions.CultureInvariant))
        {
            return false;
        }

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Where(static term => term.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length is < 2 or > 4
            || titleTerms.Any(IsGenericPlanningCoverageTerm))
        {
            return false;
        }

        var proof = NormalizeLexicalLookup(BuildStructuredPlanningFieldValueProofText(candidate));
        if (proof.Length < normalizedTitle.Length + 18)
            return false;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        var titleMatches = Regex.Matches(proof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();
        if (titleMatches.Length == 0)
            return false;

        var firstFieldLabelMatch = Regex.Match(
            proof,
            @"\b(?:components?|components?|components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|items?|[eÃ©]l[eÃ©]ments?|values?|valeurs?|parameters?|param[eÃ¨]tres?|quantit(?:y|ies)|quantit[eÃ©]s?)\b",
            RegexOptions.CultureInvariant);
        if (firstFieldLabelMatch.Success
            && titleMatches.All(match => match.Index < firstFieldLabelMatch.Index))
        {
            return false;
        }
        if (firstFieldLabelMatch.Success
            && titleTerms.Length >= 3
            && titleMatches.Any(match => match.Index < firstFieldLabelMatch.Index))
        {
            return false;
        }

        return Regex.IsMatch(
            proof,
            @"\b(?:components?|components?|components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|items?|[eÃ©]l[eÃ©]ments?|values?|valeurs?|parameters?|param[eÃ¨]tres?|quantit(?:y|ies)|quantit[eÃ©]s?)\b.{0,180}\b" + titlePattern + @"\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeBareQuantityStructuredPlanningFieldValueCandidate(SourceBackedOptionCandidate candidate)
    {
        var cleanedTitle = CleanSourceBackedOptionTitle(candidate.Title);
        var normalizedTitle = NormalizeLexicalLookup(cleanedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle)
            || normalizedTitle.Length > 42
            || !Regex.IsMatch(normalizedTitle, @"^\d+(?:[,.]\d+)?\s+\p{L}{2,18}(?:\s+\p{L}{2,18}){0,2}$", RegexOptions.CultureInvariant))
        {
            return false;
        }

        var proofText = CollapseWhitespace(BuildStructuredPlanningFieldValueProofText(candidate));
        var proof = NormalizeLexicalLookup(proofText);
        if (proof.Length < normalizedTitle.Length + 16)
            return false;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        var match = Regex.Match(proof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var hasAttachedItemProof =
            TitleWindowContainsStrongLocalStructuredPlanningProof(normalizedTitle, proofText)
            || PrimaryEvidenceContainsDenseLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, proofText)
            || PrimaryEvidenceContainsRelaxedLocalStructuredPlanningProof(candidate.Hit, normalizedTitle, proofText);
        if (!hasAttachedItemProof)
            return true;

        var before = proof[..match.Index];
        var localBefore = before.Length > 320 ? before[^320..] : before;
        return Regex.IsMatch(
            localBefore,
            @"\b(?:components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|items?|[eÃ©]l[eÃ©]ments?|values?|valeurs?|parameters?|param[eÃ¨]tres?|quantit(?:y|ies)|quantit[eÃ©]s?)\b.{0,300}$",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeSupportingStructuredPlanningFieldValueCandidate(SourceBackedOptionCandidate candidate)
    {
        var cleanedTitle = CleanSourceBackedOptionTitle(candidate.Title);
        var normalizedTitle = NormalizeLexicalLookup(cleanedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        if (normalizedTitle.Length > 90)
            return false;

        if (Regex.IsMatch(
                normalizedTitle,
                @"\b(?:pour|to)\s+(?:accompagner|accompany|servir|serve|utiliser|use|complete|completer)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var proofText = CollapseWhitespace(BuildStructuredPlanningFieldValueProofText(candidate));
        var normalizedProof = NormalizeLexicalLookup(proofText);
        if (normalizedProof.Length < normalizedTitle.Length + 16)
            return false;

        if (ContentCardEvidenceNamesDifferentStructuredPlanningItem(candidate, normalizedTitle))
            return true;

        if (ContentCardTitleVariantOwnsStructuredProcessSection(candidate, normalizedTitle, normalizedProof))
            return false;

        if (CandidateTitleOwnsLocalStructuredPlanningSection(normalizedTitle, normalizedProof))
            return false;

        if (CandidateTitleAppearsInsideStructuredFieldValueSequenceBeforeProcess(normalizedTitle, normalizedProof))
            return true;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        var titleMatches = Regex.Matches(normalizedProof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();
        if (titleMatches.Length == 0)
            return false;

        var firstTitleIndex = titleMatches[0].Index;
        if (ContentCardTitleAnchorsCandidateBeforeStructuredFields(candidate, normalizedTitle, normalizedProof, firstTitleIndex))
            return false;

        var fieldLabelMatches = Regex.Matches(
                normalizedProof,
                @"\b(?:components?|composants?|constituents?|constituants?|inputs?|materials?|materiel|requirements?|exigences?|supplies?|tools?|outils?|items?|elements?|values?|valeurs?|parameters?|parametres?|quantit(?:y|ies)|quantites?)\b",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();
        if (fieldLabelMatches.Length == 0)
            return false;

        var hasTitleBeforeAnyField = titleMatches.Any(match => match.Index < fieldLabelMatches[0].Index);
        if (hasTitleBeforeAnyField)
            return false;

        foreach (var titleMatch in titleMatches)
        {
            var precedingField = fieldLabelMatches
                .Where(field => field.Index < titleMatch.Index)
                .OrderByDescending(static field => field.Index)
                .FirstOrDefault();
            if (precedingField is null)
                continue;

            var distance = titleMatch.Index - precedingField.Index;
            if (distance <= 0 || distance > 260)
                continue;

            var localAfterStart = titleMatch.Index + titleMatch.Length;
            var localAfter = normalizedProof[localAfterStart..Math.Min(normalizedProof.Length, localAfterStart + 180)];
            if (Regex.IsMatch(localAfter, @"^(?:\s|[.,;:)\/-]){0,16}(?:et|and|ou|or|avec|with|plus|,|;|\d)", RegexOptions.CultureInvariant)
                || Regex.IsMatch(localAfter, @"\b(?:preparation|procedure|instructions?|method|methode|steps?|etapes?|operation|workflow)\b", RegexOptions.CultureInvariant)
                || Regex.IsMatch(normalizedTitle, @"^(?:\d+(?:[,.]\d+)?\s+)?\p{L}{2,18}(?:\s+(?:ou|or|and|et|avec|with)\s+\p{L}{2,18}){1,3}(?:\s+\p{L}{2,18}){0,3}$", RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeEmbeddedStructuredPlanningFieldValueCandidate(SourceBackedOptionCandidate candidate)
    {
        var cleanedTitle = CleanSourceBackedOptionTitle(candidate.Title);
        var normalizedTitle = NormalizeLexicalLookup(cleanedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        var titleTerms = ExtractPlanningAnswerSupportTerms(normalizedTitle)
            .Where(static term => term.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (titleTerms.Length == 0 || titleTerms.Length > 3)
            return false;

        var proofText = CollapseWhitespace(BuildStructuredPlanningFieldValueProofText(candidate));
        var normalizedProof = NormalizeLexicalLookup(proofText);
        if (normalizedProof.Length < normalizedTitle.Length + 16)
            return false;

        if (ContentCardTitleVariantOwnsStructuredProcessSection(candidate, normalizedTitle, normalizedProof))
            return false;

        if (CandidateTitleOwnsLocalStructuredPlanningSection(normalizedTitle, normalizedProof))
            return false;

        if (CandidateTitleAppearsInsideStructuredFieldValueSequenceBeforeProcess(normalizedTitle, normalizedProof))
            return true;

        var titleIndex = normalizedProof.IndexOf(normalizedTitle, StringComparison.Ordinal);
        if (titleIndex < 0)
            return false;

        if (ContentCardTitleAnchorsCandidateBeforeStructuredFields(candidate, normalizedTitle, normalizedProof, titleIndex))
            return false;

        var fieldCueBeforeCandidate = normalizedProof[..titleIndex];
        var localFieldCueBeforeCandidate = fieldCueBeforeCandidate.Length > 140
            ? fieldCueBeforeCandidate[^140..]
            : fieldCueBeforeCandidate;
        if (!Regex.IsMatch(
                localFieldCueBeforeCandidate,
                @"(?:[,;:]|\b(?:components?|components?|components?|composants?|materials?|mat[eÃƒÂ©]riel|requirements?|exigences?|items?|[eÃƒÂ©]l[eÃƒÂ©]ments?|pour\s+\d+|for\s+\d+)\b).{0,120}$",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var leadingTitles = ExtractPlanItemTitleCandidatesV2(proofText)
            .Concat(ExtractSourceBackedTitleCandidates(candidate.Hit))
            .Select(CleanSourceBackedOptionTitle)
            .Where(IsUsableSourceBackedOptionTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var leadingTitle in leadingTitles)
        {
            var normalizedLeadingTitle = NormalizeLexicalLookup(leadingTitle);
            if (string.IsNullOrWhiteSpace(normalizedLeadingTitle)
                || string.Equals(normalizedLeadingTitle, normalizedTitle, StringComparison.Ordinal))
            {
                continue;
            }

            var leadingIndex = normalizedProof.IndexOf(normalizedLeadingTitle, StringComparison.Ordinal);
            if (leadingIndex is >= 0 and <= 80
                && titleIndex > leadingIndex + normalizedLeadingTitle.Length + 4)
            {
                return true;
            }
        }

        var before = normalizedProof[..titleIndex];
        var after = normalizedProof[(titleIndex + normalizedTitle.Length)..];
        var localBefore = before.Length > 140 ? before[^140..] : before;
        var localAfter = after.Length > 180 ? after[..180] : after;
        var precededByFieldOrListCue = Regex.IsMatch(
            localBefore,
            @"(?:[,;:â€¢]|\b(?:components?|components?|components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|items?|[eÃ©]l[eÃ©]ments?|pour\s+\d+|for\s+\d+)\b).{0,120}$",
            RegexOptions.CultureInvariant);
        if (!precededByFieldOrListCue)
            return false;

        return Regex.IsMatch(
            localAfter,
            @"^(?:\s|[.,;:)â€¢-]){0,16}.{0,160}\b(?:preparation|pr[eÃ©]paration|procedure|proc[eÃ©]dure|instructions?|method|m[eÃ©]thode|steps?|[eÃ©]tapes?|technique|operation|workflow)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeInlineDelimitedStructuredPlanningFieldValueCandidate(
        string normalizedProof,
        string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedProof) || string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var titlePattern = Regex.Escape(normalizedTitle).Replace("\\ ", @"\s+");
        var match = Regex.Match(normalizedProof, @"\b" + titlePattern + @"\b", RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var before = normalizedProof[..match.Index];
        var after = normalizedProof[(match.Index + match.Length)..];
        var localBefore = before.Length > 100 ? before[^100..] : before;
        var localAfter = after.Length > 160 ? after[..160] : after;
        var looksEmbeddedInList = Regex.IsMatch(
            localBefore,
            @"(?:[,;:]|\b(?:components?|components?|components?|composants?|materials?|mat[eÃ©]riel|requirements?|exigences?|items?|[eÃ©]l[eÃ©]ments?)\b).{0,90}$",
            RegexOptions.CultureInvariant);
        if (!looksEmbeddedInList)
            return false;

        return Regex.IsMatch(
            localAfter,
            @"^(?:\s|[.,;:)]){0,12}.{0,140}\b(?:preparation|pr[eÃ©]paration|procedure|proc[eÃ©]dure|instructions?|method|m[eÃ©]thode|steps?|[eÃ©]tapes?|notes?|conditions?|criteria|criteres|crit[eÃ¨]res|validation)\b",
            RegexOptions.CultureInvariant);
    }
}
