using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool LooksLikeLowSignalAppFeatureHit(RagHitSummary hit, string query)
    {
        var queryLookup = NormalizeLexicalLookup(query);
        if (Regex.IsMatch(queryLookup, @"\b(?:liste\s+d['\u2019]elements|itemized\s+list|liste\s+detaillee|detailed\s+list)\b", RegexOptions.CultureInvariant))
            return false;

        var text = NormalizeLexicalLookup(GetBestRagEvidenceText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var looksLikeAppFeatureCopy = Regex.IsMatch(
            text,
            @"\b(?:liste\s+d['\u2019]elements\s+a\s+partir|itemized\s+list\s+from|application|appli|app|interface|workflow|fonctionnalite|feature)\b",
            RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                text,
                @"\b(?:documents?|sources?|corpus|base\s+de\s+connaissances?|knowledge\s+base|categories?|categories|dossiers?|folders?|fichiers?|files?)\b",
                RegexOptions.CultureInvariant);
        return looksLikeAppFeatureCopy
            && !Regex.IsMatch(text, @"\b(?:preparation|operation|workflow|execution|procedure|method|methode|etapes?|steps?|requirements?|values?|valeurs?|quantities?|quantites?|\d+\s*(?:g|kg|mg|ml|cl|l|min|h|mm|cm|m))\b", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeLowSignalContentCandidateHit(RagHitSummary hit)
    {
        if (BackendSelectionHintsPreferUsableEvidence(hit))
            return false;
        if (BackendSelectionHintsPreferLowSignal(hit))
            return true;

        var primaryText = GetRagHitPrimaryContentText(hit);
        var text = NormalizeStructuredScanText(primaryText);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (hit.PageStart <= 2 && LooksLikeGenericFrontMatterShape(primaryText))
            return true;

        var hasBodyStructure = Regex.IsMatch(
            text,
            @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|preparation|operation|workflow|execution|procedure|etapes?|steps?|method|methode|(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}|\d+\s*(?:g|kg|mg|ml|cl|l|min(?:ute)?s?|h|heures?|hours?))\b",
            RegexOptions.CultureInvariant);
        var hasStrongStructuredEvidence = ComputeProcedureCompletenessCueScore(hit) >= 5
            || ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 3
            || CountProcedureStepMarkers(text) > 0;
        if (hasBodyStructure && hasStrongStructuredEvidence)
            return false;

        if (Regex.IsMatch(text, @"^(?:si|lorsque|quand|when|if)\b", RegexOptions.CultureInvariant))
            return true;

        var hasCoverOrCatalogCue = Regex.IsMatch(
            text,
            @"\b(?:sommaire|index|contents|table\s+of\s+contents|catalogue|catalog|guide|introduction|avant-propos|preface|foreword|copyright|isbn|edition|publisher|front\s+matter)\b",
            RegexOptions.CultureInvariant);
        if (hasCoverOrCatalogCue)
            return true;

        return false;
    }

    private static bool LooksLikeGenericFrontMatterShape(string? text)
    {
        var raw = CollapseWhitespace(text ?? string.Empty);
        if (raw.Length < 80)
            return false;

        var normalized = NormalizeLexicalLookup(raw);
        var lead = normalized.Length <= 700 ? normalized : normalized[..700];
        var hasMarketingLead = Regex.IsMatch(lead, @"\b\d{2,4}\b", RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                lead,
                @"\b(?:conseils?|tips?|guides?|simple|simples|accessible|accessibles|abordable|abordables|budget|rapide|rapides|quick|easy|pratique|pratiques)\b",
                RegexOptions.CultureInvariant)
            && !Regex.IsMatch(
                lead,
                @"\b(?:preparation|operation|workflow|execution|procedure|etapes?|steps?|method|methode)\b",
                RegexOptions.CultureInvariant);
        if (raw.Length <= 2200 && hasMarketingLead)
            return true;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:sommaire|index|contents|table\s+of\s+contents|catalogue|catalog|copyright|isbn|edition|publisher|preface|foreword|avant-propos|introduction|www\.)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var letters = raw.Where(char.IsLetter).ToArray();
        if (letters.Length < 24)
            return false;

        var upperRatio = letters.Count(char.IsUpper) / (double)letters.Length;
        var digitRatio = raw.Count(char.IsDigit) / (double)Math.Max(1, raw.Length);
        var punctuationRatio = raw.Count(ch => char.IsPunctuation(ch) || char.IsSymbol(ch)) / (double)Math.Max(1, raw.Length);
        var hasNumberedHeadline = Regex.IsMatch(
            raw,
            @"(?:^|\s)\d{2,4}\s+[\p{Lu}\p{Lt}][\p{Lu}\p{Lt}\s'\-]{6,}",
            RegexOptions.CultureInvariant);
        var hasDenseUppercaseHeadline = Regex.IsMatch(
            raw,
            @"(?:\b[\p{Lu}\p{Lt}]{4,}\b[\s+\-]*){3,}",
            RegexOptions.CultureInvariant);
        var hasGluedCaseBoundary = Regex.IsMatch(
            raw,
            @"[\p{Ll}]{4,}[\p{Lu}\p{Lt}]{4,}|[\p{Lu}\p{Lt}]{4,}[\p{Ll}]{4,}[\p{Lu}\p{Lt}]{4,}",
            RegexOptions.CultureInvariant);
        var hasMarketingCue = Regex.IsMatch(
            normalized,
            @"\b(?:conseils?|tips?|guide|guides?|simple|simples|accessible|accessibles|abordable|abordables|budget|rapide|rapides|quick|easy|pratique|pratiques)\b",
            RegexOptions.CultureInvariant);
        if (raw.Length <= 1600
            && (hasNumberedHeadline || hasDenseUppercaseHeadline || hasGluedCaseBoundary)
            && (hasMarketingCue || upperRatio >= 0.38))
        {
            return true;
        }

        return raw.Length <= 1400
            && upperRatio >= 0.48
            && digitRatio <= 0.18
            && punctuationRatio <= 0.22;
    }

    private static bool HasRecoverableStructuredExactItemEvidence(string requestedTitle, RagHitSummary hit)
    {
        if (string.IsNullOrWhiteSpace(requestedTitle))
            return false;

        if (LooksLikePageReferenceOnlyHit(hit) || LooksLikeExactItemReferenceOnlyHit(requestedTitle, hit))
            return false;

        var navigationOverride = IsUsableExactItemNavigationOverride(requestedTitle, hit);
        if (LooksLikeNavigationOnlyHit(hit) && !navigationOverride)
            return false;

        if (LooksLikeLowSignalContentCandidateHit(hit) && !navigationOverride)
            return false;

        if (HasContentCardEvidenceFacts(hit)
            || HasContentCardQuantityEvidence(new[] { hit })
            || HasContentCardScalableQuantityEvidence(hit))
        {
            return true;
        }

        if (ComputeExactItemCardCompletenessCueScore(hit) >= 4)
            return true;

        if (ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 3)
            return true;

        var focusedEvidence = GetFocusedExactItemEvidenceText(requestedTitle, hit);
        var structuredEvidence = GetFocusedExactItemStructuredEvidenceText(requestedTitle, hit);
        if (ExtractItemizedQuantityFacts(structuredEvidence).Length >= 2)
            return true;

        if (ExtractVisibleDurationsAndQuantities(focusedEvidence).Length >= 2)
            return true;

        if (ExtractProcedureSteps(focusedEvidence).Length >= 2)
            return true;

        var normalizedFocusedEvidence = NormalizeStructuredScanText(focusedEvidence);
        if (CountProcedureStepMarkers(normalizedFocusedEvidence) >= 2)
            return true;

        return hit.PageEnd > hit.PageStart
            && Regex.IsMatch(
                normalizedFocusedEvidence,
                @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|preparation|operation|workflow|execution|procedure|etapes?|steps?|\d+\s*(?:g|kg|mg|ml|cl|l|min(?:ute)?s?|h|heures?|hours?))\b",
                RegexOptions.CultureInvariant);
    }

    private static IReadOnlyList<RagHitSummary> AddComplementaryStructuredHitsForExactItem(
        IReadOnlyList<RagHitSummary> exactHits,
        IEnumerable<RagHitSummary> allHits)
    {
        if (exactHits.Count == 0)
            return exactHits;

        var merged = exactHits.ToList();
        foreach (var candidate in allHits)
        {
            if (merged.Any(hit => SameRagHitRange(hit, candidate)))
                continue;

            var isAdjacentOrOverlapping = exactHits.Any(anchor =>
                string.Equals(anchor.DocPath, candidate.DocPath, StringComparison.OrdinalIgnoreCase)
                && candidate.PageStart <= anchor.PageEnd + 2
                && candidate.PageEnd >= Math.Max(1, anchor.PageStart - 2));
            if (!isAdjacentOrOverlapping)
                continue;

            if (ComputeExactItemCardCompletenessCueScore(candidate) < 8)
                continue;

            merged.Add(candidate);
        }

        return merged;
    }

    private static bool SameRagHitRange(RagHitSummary left, RagHitSummary right)
        => string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase)
            && left.PageStart == right.PageStart
            && left.PageEnd == right.PageEnd;

    private static int ComputeExactItemCardCompletenessCueScore(RagHitSummary hit)
    {
        var score = ComputeProcedureCompletenessCueScore(hit)
            + ComputeStructuredProcedureEvidenceCueScore(hit)
            + ComputeStructuredProcedureVisibleEvidenceCueScore(hit);
        if (ContainsStructuredItemHeading(GetRagHitPrimaryContentText(hit)))
            score += 4;
        if (LooksLikeMidProcedureFragment(hit))
            score -= 8;
        return score;
    }
}
