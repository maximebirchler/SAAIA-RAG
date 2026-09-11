using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool LooksLikeUsableSourceBackedOptionCandidate(RagHitSummary hit)
    {
        var profile = ClassifyRagHitEvidenceProfile(hit);
        var backendRole = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        if (!string.IsNullOrWhiteSpace(backendRole)
            && backendRole != "actionable_item"
            && !IsPrimaryQueryTopConcreteCardHit(hit))
        {
            return false;
        }

        if (profile.Role is "navigation" or "fragment" or "low_confidence")
            return false;

        if (!HasConcreteFinalSourceBackedEvidence(hit))
            return false;

        var text = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        var evidence = NormalizeLexicalLookup($"{GetRagHitPrimaryEvidenceText(hit)} {hit.ContextualSnippet}");
        if (Regex.IsMatch(evidence, @"\b(?:a\s*pplication|application|communaute|community|carnets?|noter|notez|commenter|partagez|partager|share|rating|account)\b", RegexOptions.CultureInvariant)
            && ComputeStructuredProcedureVisibleEvidenceCueScore(hit) <= 0
            && CountProcedureStepMarkers(text) <= 0)
        {
            return false;
        }

        if (ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 5)
            return true;
        if (ComputeProcedureCompletenessCueScore(hit) >= 7)
            return true;
        if (profile.ActionabilityScore >= 9)
            return true;
        if (ExtractBestVisibleDurationMinutes(hit).HasValue && ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 3)
            return true;
        if (ExtractBestVisibleDurationMinutes(hit).HasValue && HasSourceBackedProfileTitle(hit))
            return true;
        if (ExtractBestVisibleDurationMinutes(hit).HasValue && CountProcedureStepMarkers(text) >= 2)
            return true;

        var title = ExtractSourceBackedOptionTitle(hit, query: null);
        if (!string.IsNullOrWhiteSpace(title)
            && IsUsableSourceBackedOptionTitle(title)
            && (hit.Score >= 0.5
                || ComputeEvidenceShapeScore(hit, GetRagHitLookupText(hit), includeBackendHints: true) >= 3))
        {
            return true;
        }

        return false;
    }

    private static bool HasConcreteFinalSourceBackedEvidence(RagHitSummary hit)
    {
        if (BackendSelectionHintsPreferUsableEvidence(hit))
            return true;

        if (BackendSelectionHintsPreferNavigation(hit) || BackendSelectionHintsPreferLowSignal(hit))
            return false;

        var contentEvidence = CollapseWhitespace(GetRagHitPrimaryContentText(hit));
        var structuredEvidence = CollapseWhitespace(GetRagHitStructuredEvidenceText(hit));
        var lookupText = CollapseWhitespace(GetRagHitLookupText(hit));
        if (string.IsNullOrWhiteSpace(contentEvidence)
            && string.IsNullOrWhiteSpace(structuredEvidence)
            && string.IsNullOrWhiteSpace(lookupText))
        {
            return false;
        }

        var normalizedEvidence = NormalizeLexicalLookup($"{contentEvidence} {structuredEvidence}");
        var normalizedLookup = NormalizeLexicalLookup(lookupText);
        var cardTitles = (hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            .Select(static card => NormalizeLexicalLookup(CleanSourceBackedOptionTitle(card.Title)))
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(normalizedEvidence)
            && cardTitles.Length > 0
            && cardTitles.Contains(normalizedEvidence)
            && CountNumericFactMarkers(normalizedEvidence) == 0)
        {
            return false;
        }

        var hasConcreteContentCardEvidence = hit.MatchedContentCards?.Any(HasConcreteContentCardEvidence) == true;
        var normalizedContentEvidence = NormalizeLexicalLookup(contentEvidence);
        var contentRepeatsOnlyCardTitle = cardTitles.Any(title =>
            string.Equals(normalizedContentEvidence, title, StringComparison.Ordinal)
            || string.Equals(normalizedContentEvidence, $"{title} {title}", StringComparison.Ordinal));
        if (!hasConcreteContentCardEvidence
            && cardTitles.Length > 0
            && !string.IsNullOrWhiteSpace(normalizedContentEvidence)
            && contentRepeatsOnlyCardTitle
            && CountNumericFactMarkers(normalizedContentEvidence) == 0)
        {
            return false;
        }

        if (hit.MatchedContentCards is { Count: > 0 } cards
            && cards.Any(static card =>
                card.RawEvidence.HasValue
                || card.Evidence is { QuantityFacts.Count: > 0 }
                || card.Evidence?.Facts is { Count: > 0 }))
        {
            return true;
        }

        if (ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 2)
            return true;
        if (ComputeProcedureCompletenessCueScore(hit) >= 5)
            return true;
        if (CountProcedureStepMarkers(NormalizeStructuredScanText(lookupText)) > 0)
            return true;
        if (ExtractBestVisibleDurationMinutes(hit).HasValue)
            return true;
        if (CountNumericFactMarkers(NormalizeStructuredScanText($"{contentEvidence} {structuredEvidence}")) > 0)
            return true;
        if (!string.IsNullOrWhiteSpace(hit.FullText) && hit.FullText.Length >= 120)
            return true;
        if (CollapseWhitespace($"{contentEvidence} {structuredEvidence}").Length >= 90)
            return true;
        if (!string.IsNullOrWhiteSpace(normalizedLookup)
            && Regex.IsMatch(
                normalizedLookup,
                @"\b(?:preparation|operation|workflow|execution|procedure|etapes?|steps?|method|methode|components?|materiel|materials?|requirements?|values?|valeurs?|quantities?|quantites?)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeLowValueSourceBackedOptionCandidate(
        SourceBackedOptionCandidate candidate,
        string normalizedQuery)
    {
        var title = CleanSourceBackedOptionTitle(candidate.Title);
        var hasConcreteTitle = IsUsableSourceBackedOptionTitle(title)
            && !LooksLikeWeakSourceBackedOptionTitle(title);

        var normalizedTitle = NormalizeLexicalLookup(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return true;

        if (!hasConcreteTitle
            && !SourceBackedContextCandidateHasAnchoredFuzzyLocalStructuredProof(candidate, normalizedTitle))
        {
            return true;
        }

        var evidence = NormalizeLexicalLookup($"{GetRagHitPrimaryEvidenceText(candidate.Hit)} {candidate.Hit.ContextualSnippet}");
        if (LooksLikeNavigationOnlyHit(candidate.Hit)
            && !LooksLikeResolvedRouteTargetHit(candidate.Hit)
            && !QueryAnchorTermsMatchHit(ExtractQuerySignalTerms(normalizedQuery).Take(6).ToArray(), candidate.Hit))
        {
            return true;
        }

        if (LooksLikeLowValueMarketingOrDocumentHeading(normalizedTitle)
            && ComputeStructuredProcedureVisibleEvidenceCueScore(candidate.Hit) <= 0
            && ComputeProcedureCompletenessCueScore(candidate.Hit) < 7)
        {
            return true;
        }

        if (LooksLikeNoisyCandidateSupportCue(title)
            && !HasSourceBackedProfileTitle(candidate.Hit)
            && ComputeStructuredProcedureVisibleEvidenceCueScore(candidate.Hit) < 1
            && ComputeProcedureCompletenessCueScore(candidate.Hit) < 1)
        {
            return true;
        }

        if (LooksLikeNoisyCandidateSupportCue(evidence)
            && !HasSourceBackedProfileTitle(candidate.Hit)
            && ComputeStructuredProcedureVisibleEvidenceCueScore(candidate.Hit) < 3
            && ComputeProcedureCompletenessCueScore(candidate.Hit) < 5)
        {
            return true;
        }

        return false;
    }

    private static string BuildSourceBackedOptionCandidateKey(SourceBackedOptionCandidate candidate)
        => $"{NormalizeLexicalLookup(candidate.Title)}|{candidate.Hit.DocPath}|{candidate.Hit.PageStart}|{candidate.Hit.PageEnd}";

    private static bool IsPrimaryQueryTopConcreteCardCandidate(SourceBackedOptionCandidate candidate)
        => IsPrimaryQueryTopConcreteCardHit(candidate.Hit);

    private static bool IsPrimaryQueryTopConcreteCardHit(RagHitSummary hit)
    {
        if (hit.RetrievalQueryIndex != 0 || hit.RetrievalHitRank is null or > 1)
            return false;

        return hit.MatchedContentCards?.Any(IsConcreteSourceBackedOptionCard) == true;
    }

    private static bool IsConcreteSourceBackedOptionCard(RagHitContentCardSummary card)
    {
        var title = CleanSourceBackedOptionTitle(card.Title);
        return IsUsableSourceBackedOptionTitle(title);
    }

    private const int SourceBackedProfileTitleCacheMaxEntries = 4096;
    private static readonly object SourceBackedProfileTitleCacheGate = new();
    private static readonly Dictionary<RagHitSummary, bool> SourceBackedProfileTitleCache = new();

    private static bool HasSourceBackedProfileTitle(RagHitSummary hit)
    {
        lock (SourceBackedProfileTitleCacheGate)
        {
            if (SourceBackedProfileTitleCache.TryGetValue(hit, out var cached))
                return cached;
        }

        var result = ExtractProfileTitleCandidates(hit.ContextualSnippet ?? string.Empty)
            .Take(10)
            .Select(CleanSourceBackedOptionTitle)
            .Any(IsUsableSourceBackedOptionTitle);

        lock (SourceBackedProfileTitleCacheGate)
        {
            if (SourceBackedProfileTitleCache.Count >= SourceBackedProfileTitleCacheMaxEntries)
                SourceBackedProfileTitleCache.Clear();

            SourceBackedProfileTitleCache[hit] = result;
        }

        return result;
    }

    private static bool QueryAnchorTermsMatchHit(IReadOnlyList<string> anchorTerms, RagHitSummary hit)
    {
        if (anchorTerms.Count == 0)
            return false;

        var evidence = NormalizeLexicalLookup(GetRagHitPrimaryEvidenceText(hit));
        var title = NormalizeLexicalLookup($"{hit.DocName} {hit.SectionTitle} {hit.HeadingPath} {ExtractSourceBackedOptionTitle(hit, null)}");
        return anchorTerms.Any(term =>
            evidence.Contains(term, StringComparison.Ordinal)
            || title.Contains(term, StringComparison.Ordinal));
    }

    private static readonly HashSet<string> SourceBackedOptionConstraintTerms = new(StringComparer.Ordinal)
    {
        "total", "moins", "minutes", "minute", "rapide", "rapides", "complete", "complet",
        "combined", "overall", "option", "options", "suggestion", "suggestions", "selection",
        "facile", "faciles", "easy", "simple", "simples", "rapido", "rapida", "rapidas",
        "rapidos", "schnell", "einfach", "veloce", "veloci"
    };

    private static readonly HashSet<string> SourceBackedPairingAnchorNoiseTerms = new(StringComparer.Ordinal)
    {
        "quel", "quelle", "quels", "quelles", "quoi", "which", "what", "cual", "qual",
        "welche", "welcher", "welches", "quale", "propose", "proposes", "suggest",
        "suggestion", "suggestions", "recommend", "recommends", "recommendation",
        "recommande", "recommander", "conseille", "conseiller", "irait", "vont",
        "bien", "avec", "with", "goes", "pair", "pairs", "pairing", "accompagne",
        "accompagner", "associe", "associer", "compatible", "compatibles", "con",
        "acompanha", "acompanhar", "combina", "combinar", "passt", "kombinieren",
        "abbinare", "abbina"
    };

    private static string BuildSourceBackedFallbackOptionTitle(RagHitSummary hit, string language)
    {
        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
        return string.IsNullOrWhiteSpace(docLabel)
            ? $"Source {SourceBackedPagePrefix(language)}{hit.PageStart}"
            : $"{docLabel} {SourceBackedPagePrefix(language)}{hit.PageStart}";
    }

    private static IReadOnlyList<string> ExtractSourceBackedCardTitleVariants(string? value)
    {
        var raw = CollapseWhitespace(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        var hasStrongSeparator = Regex.IsMatch(raw, @"[\r\n|;\u2022]|//", RegexOptions.CultureInvariant);
        var fragments = hasStrongSeparator
            ? Regex.Split(raw, @"\s*(?:[\r\n]+|\|\||\||//|;|\u2022)\s*", RegexOptions.CultureInvariant)
            : new[] { raw };

        var titles = fragments
            .Select(CleanSourceBackedOptionTitle)
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Where(IsUsableSourceBackedOptionTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (titles.Count == 0 && !hasStrongSeparator)
        {
            var wholeTitle = CleanSourceBackedOptionTitle(raw);
            if (IsUsableSourceBackedOptionTitle(wholeTitle))
                titles.Add(wholeTitle);
        }

        return titles;
    }

    private static IReadOnlyList<string> ExtractNamedEntityLikeQueryTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        return Regex.Matches(query, @"\b[\p{Lu}][\p{L}\p{N}]{4,}\b", RegexOptions.CultureInvariant)
            .Select(match => NormalizeLexicalLookup(match.Value))
            .Where(static term => term.Length >= 5)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
    }

    private static string ExtractSourceBackedOptionTitle(RagHitSummary hit, string? query)
    {
        var profileCandidates = ExtractProfileTitleCandidates(hit.ContextualSnippet ?? string.Empty)
            .Select(CleanSourceBackedOptionTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Where(IsUsableSourceBackedOptionTitle)
            .Select((title, index) => new
            {
                Title = title,
                Index = index,
                Score = ComputeSourceBackedOptionTitleScore(title, hit, query)
            })
            .Where(item => item.Score > -30)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .ToArray();
        if (profileCandidates.Length > 0)
            return profileCandidates[0].Title;

        var candidates = ExtractSourceBackedTitleCandidates(hit)
            .Concat(ExtractPlanItemTitleCandidatesV2(GetPlanExtractionText(hit)))
            .Select(CleanSourceBackedOptionTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Where(IsUsableSourceBackedOptionTitle)
            .Select((title, index) => new
            {
                Title = title,
                Index = index,
                Score = ComputeSourceBackedOptionTitleScore(title, hit, query)
            })
            .Where(item => item.Score > -20)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .ToArray();

        return candidates.FirstOrDefault()?.Title ?? string.Empty;
    }
}
