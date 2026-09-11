using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static int ComputeRagHitLexicalRelevance(string query, string? excerpt)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var normalizedExcerpt = NormalizeLexicalLookup(excerpt);
        var score = 0;

        foreach (var term in ExtractQuerySignalTerms(normalizedQuery))
        {
            if (normalizedExcerpt.Contains(term, StringComparison.Ordinal))
                score += term.Length >= 7 ? 4 : 2;
        }

        return score;
    }

    private static bool LooksLikeShortTechnicalEvidenceTopic(string? query)
    {
        var topic = NormalizeRagQueryForRetrieval(BuildRagEvidenceSelectionQuery(query ?? string.Empty));
        var normalized = NormalizeLexicalLookup(topic);
        if (normalized.Length is < 3 or > 120)
            return false;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:planning|planifier|choose|choisir|propose|suggest|recommend|selection|options?)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var terms = ExtractQuerySignalTerms(normalized).ToArray();
        if (terms.Length is 0 or > 6)
            return false;

        if (LooksLikeCompactTechnicalIdentifier(topic))
            return true;

        return Regex.IsMatch(
            normalized,
            @"\b(?:proprietes?|properties?|property|caracteristiques?|characteristics?|specifications?|specs?|technical|technique|table|valeurs?|values?|mesures?|measurement|dimensions?|density|densite|temperature|thermique|thermal|electri(?:que|ques|cal)|electric|dielectric|resistance|pression|pressure|voltage|tension|puissance|power|unites?|units?|normes?|standards?)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeCompactTechnicalIdentifier(string? value)
    {
        var topic = NormalizeRagQueryForRetrieval(BuildRagEvidenceSelectionQuery(value ?? string.Empty));
        var normalized = NormalizeLexicalLookup(topic);
        if (normalized.Length is < 3 or > 120)
            return false;

        var terms = ExtractQuerySignalTerms(normalized).ToArray();
        if (terms.Length is 0 or > 4)
            return false;

        var acronymLikeTerms = Regex.Matches(
            topic,
            @"\b(?=[A-Z0-9_-]*[A-Z])(?=[A-Z0-9_-]*[A-Z0-9])[A-Z0-9]{2,}(?:[-_][A-Z0-9]{2,})*\b",
            RegexOptions.CultureInvariant).Count;
        return acronymLikeTerms >= 2;
    }

    private static int ComputeDirectTechnicalEvidenceScore(string query, RagHitSummary hit)
    {
        if (!LooksLikeShortTechnicalEvidenceTopic(query))
            return 0;

        var topic = NormalizeRagQueryForRetrieval(BuildRagEvidenceSelectionQuery(query));
        var normalizedTopic = NormalizeLexicalLookup(topic);
        var terms = BuildShortTechnicalEvidenceSelectionTerms(normalizedTopic);
        if (terms.Length == 0)
            return 0;

        var visible = NormalizeLexicalLookup(GetRagHitVisibleTechnicalEvidenceText(hit));
        var primary = NormalizeLexicalLookup(GetRagHitPrimaryEvidenceText(hit));
        var lookup = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        var structured = NormalizeLexicalLookup(GetRagHitStructuredEvidenceText(hit));
        var titleText = NormalizeLexicalLookup($"{hit.SectionTitle} {hit.HeadingPath}");
        var cardTitleText = NormalizeLexicalLookup(string.Join(' ', hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>()));
        var directEvidence = NormalizeLexicalLookup($"{titleText} {visible}");

        var primaryMatches = terms.Count(term => visible.Contains(term, StringComparison.Ordinal));
        var lookupMatches = terms.Count(term => lookup.Contains(term, StringComparison.Ordinal));
        var structuredMatches = terms.Count(term => structured.Contains(term, StringComparison.Ordinal));
        var titleMatches = terms.Count(term => titleText.Contains(term, StringComparison.Ordinal));
        var cardTitleMatches = terms.Count(term => cardTitleText.Contains(term, StringComparison.Ordinal));
        var directPhraseScore = ComputeShortTechnicalPhraseEvidenceScore(normalizedTopic, directEvidence);
        var structuredPhraseScore = ComputeShortTechnicalPhraseEvidenceScore(normalizedTopic, NormalizeLexicalLookup($"{structured} {cardTitleText}"));
        if (primaryMatches == 0
            && lookupMatches == 0
            && structuredMatches == 0
            && titleMatches == 0
            && cardTitleMatches == 0
            && directPhraseScore <= 0
            && structuredPhraseScore <= 0)
        {
            return 0;
        }

        var score = 0;
        score += titleMatches * 9;
        score += primaryMatches * 5;
        score += lookupMatches;
        score += structuredMatches * 2;
        score += cardTitleMatches;
        if (directPhraseScore > 0)
            score += directPhraseScore + 24;
        else if (structuredPhraseScore > 0)
            score += Math.Min(16, structuredPhraseScore / 5);

        var significantTerms = ExtractQuerySignalTerms(normalizedTopic).ToArray();
        if (significantTerms.Length > 0)
        {
            var directCoverage = significantTerms.Count(term =>
                DirectTechnicalEvidenceTermMatches(term, titleText)
                || DirectTechnicalEvidenceTermMatches(term, visible));
            if (directCoverage == significantTerms.Length)
                score += 16;
            else if (directCoverage >= Math.Min(2, significantTerms.Length))
                score += 8;
        }

        if (hit.HasTable)
            score += structuredMatches > 0 || titleMatches > 0 ? 18 : 8;
        if (HasContentCardEvidenceFacts(hit) && (directPhraseScore > 0 || primaryMatches > 0))
            score += Math.Min(8, ExtractContentCardEvidenceFacts(new[] { hit }).Length);
        if (Regex.IsMatch(titleText, @"\b(?:properties?|property|proprietes?|caracteristiques?|characteristics?|specifications?|technical\s+data|data\s+sheet|table|valeurs?|values?)\b", RegexOptions.CultureInvariant))
            score += 8;

        var role = NormalizeLexicalLookup(hit.SelectionHintRole);
        if (role is "navigation" or "fragment" or "low_confidence")
            score -= 12;
        if (NormalizeLexicalLookup(hit.ContentRole) == "navigation")
            score -= 10;
        if (NormalizeLexicalLookup(hit.ContentRole) == "mixed_navigation_content")
            score -= 2;
        if (hit.NavigationScore.GetValueOrDefault() >= 0.65)
            score -= 8;
        else if (hit.NavigationScore.GetValueOrDefault() >= 0.60)
            score -= 3;
        if (hit.SelectionHintNavigationScore.GetValueOrDefault() >= 70)
            score -= 6;
        if (hit.OcrApplied)
            score -= 6;
        if (hit.ManualReviewRecommended || hit.DocumentManualReviewRecommended || hit.PageManualReviewRecommended)
            score -= 6;
        if (!hit.HasTable && !HasContentCardEvidenceFacts(hit) && CollapseWhitespace(GetRagHitPrimaryContentText(hit)).Length < 80)
            score -= 4;

        return score;
    }

    private static bool HasShortTechnicalPhraseEvidence(string query, RagHitSummary hit)
    {
        if (!LooksLikeShortTechnicalEvidenceTopic(query))
            return false;

        var topic = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(BuildRagEvidenceSelectionQuery(query)));
        var evidence = NormalizeLexicalLookup(GetRagHitVisibleTechnicalEvidenceText(hit));
        return ComputeShortTechnicalPhraseEvidenceScore(topic, evidence) > 0;
    }

    private static int ComputeShortTechnicalPhraseEvidenceScore(string normalizedTopic, string normalizedEvidence)
    {
        var terms = BuildShortTechnicalEvidenceSelectionTerms(normalizedTopic).ToHashSet(StringComparer.Ordinal);
        var hasProperty = terms.Overlaps(new[] { "propriete", "proprietes", "property", "properties", "caracteristique", "caracteristiques", "characteristic", "characteristics" });
        var hasElectrical = terms.Overlaps(new[] { "electrique", "electriques", "electric", "electrical", "dielectric" });
        var hasThermal = terms.Overlaps(new[] { "thermique", "thermiques", "thermal", "temperature", "temperatures" });

        if (hasProperty && hasElectrical)
        {
            return Regex.IsMatch(
                normalizedEvidence,
                @"\b(?:electrical\s+properties|electric\s+properties|dielectric\s+properties|dielectric\s+strength|volume\s+resistivity|surface\s+resistance|electrical\s+insulat(?:ing|ion)|proprietes?\s+electriques?|caracteristiques?\s+electriques?)\b",
                RegexOptions.CultureInvariant)
                ? 80
                : -12;
        }

        if (hasProperty && hasThermal)
        {
            return Regex.IsMatch(
                normalizedEvidence,
                @"\b(?:thermal\s+properties|temperature\s+properties|melt(?:ing)?\s+point|thermal\s+conductivity|proprietes?\s+thermiques?|caracteristiques?\s+thermiques?)\b",
                RegexOptions.CultureInvariant)
                ? 80
                : -12;
        }

        return 0;
    }

    private static string[] BuildShortTechnicalEvidenceSelectionTerms(string normalizedTopic)
    {
        var terms = new List<string>();
        foreach (var term in ExtractQuerySignalTerms(normalizedTopic))
        {
            terms.Add(term);
            if (Regex.IsMatch(term, @"^(?:propriete|proprietes|property|properties|caracteristique|caracteristiques|characteristic|characteristics)$", RegexOptions.CultureInvariant))
                terms.AddRange(new[] { "property", "properties", "propriete", "proprietes", "characteristic", "characteristics", "caracteristique", "caracteristiques" });
            if (Regex.IsMatch(term, @"^(?:electrique|electriques|electric|electrical|dielectric)$", RegexOptions.CultureInvariant))
                terms.AddRange(new[] { "electric", "electrical", "electrique", "electriques", "dielectric" });
            if (Regex.IsMatch(term, @"^(?:thermique|thermiques|thermal|temperature|temperatures)$", RegexOptions.CultureInvariant))
                terms.AddRange(new[] { "thermal", "thermique", "thermiques", "temperature", "temperatures" });
            if (Regex.IsMatch(term, @"^(?:valeur|valeurs|value|values)$", RegexOptions.CultureInvariant))
                terms.AddRange(new[] { "value", "values", "valeur", "valeurs" });
            if (Regex.IsMatch(term, @"^(?:specification|specifications|spec|specs|technique|technical)$", RegexOptions.CultureInvariant))
                terms.AddRange(new[] { "specification", "specifications", "technical", "technique", "data" });
        }

        return terms
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Select(NormalizeLexicalLookup)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToArray();
    }

    private static bool DirectTechnicalEvidenceTermMatches(string term, string normalizedEvidence)
    {
        if (normalizedEvidence.Contains(term, StringComparison.Ordinal))
            return true;

        return term switch
        {
            "propriete" or "proprietes" or "property" or "properties" =>
                Regex.IsMatch(normalizedEvidence, @"\b(?:property|properties|proprietes?|characteristics?|caracteristiques?)\b", RegexOptions.CultureInvariant),
            "electrique" or "electriques" or "electric" or "electrical" or "dielectric" =>
                Regex.IsMatch(normalizedEvidence, @"\b(?:electric|electrical|electriques?|dielectric)\b", RegexOptions.CultureInvariant),
            "thermique" or "thermiques" or "thermal" or "temperature" or "temperatures" =>
                Regex.IsMatch(normalizedEvidence, @"\b(?:thermal|thermiques?|temperatures?)\b", RegexOptions.CultureInvariant),
            _ => false
        };
    }

    private static bool ShouldWarnNoExplicitPairing(string query, IReadOnlyList<RagHitSummary> hits)
    {
        if (hits.Count == 0)
            return false;

        if (LooksLikeShortTechnicalEvidenceTopic(query)
            && hits.Any(hit => HasShortTechnicalPhraseEvidence(query, hit)))
        {
            return false;
        }

        var pairingQuery = LooksLikeShortTechnicalEvidenceTopic(query)
            ? NormalizeRagQueryForRetrieval(BuildRagEvidenceSelectionQuery(query))
            : query;
        var terms = ExtractQuerySignalTerms(NormalizeLexicalLookup(pairingQuery))
            .Where(static term => term.Length >= 5)
            .Take(5)
            .ToArray();
        if (terms.Length < 2)
            return false;

        var evidence = NormalizeLexicalLookup(string.Join(' ', hits.Take(3).Select(GetRagHitLookupText)));
        if (string.IsNullOrWhiteSpace(evidence))
            return false;

        var covered = LooksLikeShortTechnicalEvidenceTopic(query)
            ? terms.Count(term => DirectTechnicalEvidenceTermMatches(term, evidence))
            : terms.Count(term => evidence.Contains(term, StringComparison.Ordinal));
        return covered < Math.Min(2, terms.Length);
    }

    private static string GetRagHitLookupText(RagHitSummary hit)
        => $"{hit.SectionTitle} {hit.HeadingPath} {hit.Excerpt} {hit.FullText} {hit.ContextualSnippet} {hit.RetrievalQuery} {GetRagHitStructuredEvidenceText(hit)}";

    private static string GetRagHitVisibleTechnicalEvidenceText(RagHitSummary hit)
        => $"{hit.SectionTitle} {hit.HeadingPath} {hit.Excerpt} {hit.FullText}";

    private static string GetRagHitComparativeEntityLookupText(RagHitSummary hit)
    {
        var cardText = hit.MatchedContentCards is { Count: > 0 }
            ? string.Join(' ', hit.MatchedContentCards.SelectMany(static card =>
                new[] { card.Title }.Concat(card.Signals ?? Array.Empty<string>())))
            : string.Empty;

        return $"{hit.DocName} {hit.DocPath} {hit.SectionTitle} {hit.HeadingPath} {hit.Excerpt} {hit.FullText} {hit.ContextualSnippet} {cardText}";
    }

    private static string GetRagHitPrimaryEvidenceText(RagHitSummary hit)
        => $"{hit.DocName} {hit.DocPath} {hit.SectionTitle} {hit.HeadingPath} {hit.Excerpt} {hit.FullText} {GetRagHitStructuredEvidenceText(hit)}";

    private static string GetRagHitStructuredEvidenceText(RagHitSummary hit)
    {
        var cardText = hit.MatchedContentCards is { Count: > 0 }
            ? string.Join(' ', hit.MatchedContentCards.SelectMany(static card =>
                new[] { card.Title, card.Kind }.Concat(card.Signals ?? Array.Empty<string>())))
            : string.Empty;
        return CollapseWhitespace($"{cardText} {BuildRagHitContentCardEvidenceText(hit)}");
    }

    private static string GetRagHitPrimaryContentText(RagHitSummary hit)
        => $"{hit.Excerpt} {hit.FullText}";

    private static RagHitEvidenceProfile ClassifyRagHitEvidenceProfile(RagHitSummary hit, string? query = null)
    {
        var lookup = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        var content = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        var titleText = NormalizeLexicalLookup(
            $"{hit.SectionTitle} {hit.HeadingPath} {string.Join(' ', hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>())}");

        var navigationScore = 0;
        var backendContentRole = NormalizeLexicalLookup(hit.ContentRole);
        if (backendContentRole == "navigation") navigationScore += 10;
        if (backendContentRole == "mixed_navigation_content") navigationScore += 2;
        if (hit.NavigationScore.HasValue && hit.NavigationScore.Value >= 0.82) navigationScore += 8;
        if (hit.ContentDensityScore.HasValue && hit.ContentDensityScore.Value < 0.25) navigationScore += 2;
        if (LooksLikeNavigationOnlyHit(hit)) navigationScore += 10;
        if (LooksLikePageReferenceOnlyHit(hit)) navigationScore += 6;
        if (Regex.IsMatch(lookup, @"\b(?:sommaire|contents|index|table\s+of\s+contents|catalogue|copyright|isbn|edition)\b", RegexOptions.CultureInvariant))
            navigationScore += 4;

        var fragmentScore = 0;
        if (Regex.IsMatch(CollapseWhitespace(hit.Excerpt ?? string.Empty), @"^(?:\.{2,}|\u2026)", RegexOptions.CultureInvariant))
            fragmentScore += 5;
        if (LooksLikeMidProcedureFragment(hit)) fragmentScore += 7;
        if (LooksLikeTruncatedEvidenceLead(hit.Excerpt)) fragmentScore += 4;
        if (content.Length is > 0 and < 80) fragmentScore += 3;
        if (Regex.IsMatch(content, @"\b(?:de|du|des|d|a|avec|sans|et|puis|jusqu|pour)$", RegexOptions.CultureInvariant))
            fragmentScore += 3;

        var actionabilityScore = 0;
        actionabilityScore += Math.Max(0, ComputeStructuredProcedureVisibleEvidenceCueScore(hit));
        actionabilityScore += Math.Max(0, ComputeProcedureCompletenessCueScore(hit));
        if (ExtractBestVisibleDurationMinutes(hit).HasValue) actionabilityScore += 3;
        if (CountProcedureStepMarkers(lookup) >= 2) actionabilityScore += 4;
        if (hit.ExactMatchHit) actionabilityScore += 3;
        foreach (var card in hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            var cardTitle = NormalizeLexicalLookup(card.Title);
            var kind = NormalizeLexicalLookup(card.Kind);
            if (!string.IsNullOrWhiteSpace(cardTitle) && IsUsefulSourceBackedDisplayTitle(card.Title))
                actionabilityScore += 2;
            if (kind.Contains("unit", StringComparison.Ordinal) || kind.Contains("exact", StringComparison.Ordinal) || kind.Contains("lead", StringComparison.Ordinal))
                actionabilityScore += 3;
        }

        var supportScore = ComputeRagHitLexicalRelevance(query ?? string.Empty, lookup);
        if (Regex.IsMatch(lookup, @"\b(?:conseil|conseils|note|notes|warning|attention|caution|recommendation|recommandation|guidance|astuce|tips?|hinweis|avviso)\b", RegexOptions.CultureInvariant))
            supportScore += 4;
        if (!string.IsNullOrWhiteSpace(hit.ContextualSnippet)) supportScore += 2;
        if (!string.IsNullOrWhiteSpace(titleText)) supportScore += 2;

        var qualityPenalty = 0;
        if (hit.ManualReviewRecommended || hit.OcrRecommended) qualityPenalty += 5;
        if (hit.ExtractionConfidence.HasValue && hit.ExtractionConfidence.Value < 0.55) qualityPenalty += 4;
        if (Regex.IsMatch(
                NormalizeLexicalLookup($"{hit.QualityStatus} {hit.DocumentQualityStatus} {hit.PageQualityStatus} {hit.TextStatus}"),
                @"\b(?:low|poor|empty|failed|manual|review)\b",
                RegexOptions.CultureInvariant))
        {
            qualityPenalty += 3;
        }

        var role = "supporting_context";
        if (navigationScore >= 4
            && hit.PageStart <= 2
            && hit.MatchedContentCards is not { Count: > 0 })
            role = "navigation";
        else if (navigationScore >= Math.Max(7, actionabilityScore + 2))
            role = "navigation";
        else if (fragmentScore >= Math.Max(7, actionabilityScore + 2))
            role = "fragment";
        else if (qualityPenalty >= 7 && actionabilityScore < 7)
            role = "low_confidence";
        else if (actionabilityScore >= 9 && actionabilityScore >= fragmentScore + 2)
            role = "actionable_item";
        else if (supportScore >= 5)
            role = "advisory";

        if (HasBackendSelectionHints(hit))
        {
            var backendRole = NormalizeRagEvidenceRole(hit.SelectionHintRole)
                              ?? InferBackendEvidenceRoleFromScores(hit)
                              ?? role;
            return new RagHitEvidenceProfile(
                backendRole,
                hit.SelectionHintActionabilityScore ?? actionabilityScore,
                hit.SelectionHintSupportScore ?? supportScore,
                hit.SelectionHintFragmentScore ?? fragmentScore,
                hit.SelectionHintNavigationScore ?? navigationScore,
                hit.SelectionHintQualityPenalty ?? qualityPenalty);
        }

        return new RagHitEvidenceProfile(role, actionabilityScore, supportScore, fragmentScore, navigationScore, qualityPenalty);
    }

    private static string? NormalizeRagEvidenceRole(string? value)
    {
        var role = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(role))
            return null;

        return role switch
        {
            "actionable" or "actionable_item" or "actionableitem" => "actionable_item",
            "support" or "supporting" or "supporting_context" or "supportingcontext" => "supporting_context",
            "advice" or "advisory" => "advisory",
            "fragment" or "partial_fragment" or "partialfragment" => "fragment",
            "navigation" or "nav" => "navigation",
            "low_confidence" or "lowconfidence" or "low_quality" or "lowquality" => "low_confidence",
            _ => role
        };
    }

    private static bool HasBackendSelectionHints(RagHitSummary hit)
        => !string.IsNullOrWhiteSpace(hit.SelectionHintRole)
           || hit.SelectionHintActionabilityScore.HasValue
           || hit.SelectionHintSupportScore.HasValue
           || hit.SelectionHintFragmentScore.HasValue
           || hit.SelectionHintNavigationScore.HasValue
           || hit.SelectionHintQualityPenalty.HasValue;

    private static bool BackendSelectionHintsPreferUsableEvidence(RagHitSummary hit)
    {
        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        if (role == "actionable_item")
            return true;
        if (role is "supporting_context" or "advisory")
            return HasConcretePageGroundedEvidence(hit)
                   && (hit.SelectionHintSupportScore.GetValueOrDefault() >= 6
                       || hit.SelectionHintActionabilityScore.GetValueOrDefault() >= 5);
        if (role is "navigation" or "fragment")
            return false;

        var actionability = hit.SelectionHintActionabilityScore ?? 0;
        var support = hit.SelectionHintSupportScore ?? 0;
        var navigation = hit.SelectionHintNavigationScore ?? 0;
        var fragment = hit.SelectionHintFragmentScore ?? 0;
        var qualityPenalty = hit.SelectionHintQualityPenalty ?? 0;
        var positive = Math.Max(actionability, support);

        return positive >= 8
               && navigation < 8
               && fragment < 8
               && qualityPenalty < 10
               && HasConcretePageGroundedEvidence(hit);
    }

    private static bool HasConcretePageGroundedEvidence(RagHitSummary hit)
    {
        if (hit.PageStart <= 0)
            return false;
        if (hit.MatchedContentCards?.Any(HasConcreteContentCardEvidence) == true)
            return true;

        var evidence = CollapseWhitespace(GetBestRagEvidenceText(hit));
        if (evidence.Length < 80)
            return false;
        if (LooksLikePureRouteNavigationText(evidence) || LooksLikeNoisyCandidateSupportCue(evidence))
            return false;

        return true;
    }

    private static bool BackendSelectionHintsPreferNavigation(RagHitSummary hit)
    {
        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        if (role == "navigation")
            return true;
        if (role is "actionable_item" or "supporting_context" or "advisory")
            return false;

        var navigation = hit.SelectionHintNavigationScore ?? 0;
        var actionability = hit.SelectionHintActionabilityScore ?? 0;
        var support = hit.SelectionHintSupportScore ?? 0;
        return navigation >= 8
               && navigation >= actionability + 2
               && navigation >= support + 2;
    }

    private static bool BackendSelectionHintsPreferLowSignal(RagHitSummary hit)
    {
        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        if (role is "navigation" or "fragment" or "low_confidence")
            return true;
        if (role is "actionable_item" or "supporting_context" or "advisory")
            return false;

        var fragment = hit.SelectionHintFragmentScore ?? 0;
        var navigation = hit.SelectionHintNavigationScore ?? 0;
        var actionability = hit.SelectionHintActionabilityScore ?? 0;
        var support = hit.SelectionHintSupportScore ?? 0;
        var qualityPenalty = hit.SelectionHintQualityPenalty ?? 0;
        var positive = Math.Max(actionability, support);

        return (fragment >= 8 && fragment >= positive + 2)
               || (navigation >= 8 && navigation >= positive + 2)
               || (qualityPenalty >= 12 && positive < 5);
    }

    private static string? InferBackendEvidenceRoleFromScores(RagHitSummary hit)
    {
        if (BackendSelectionHintsPreferNavigation(hit))
            return "navigation";
        if (BackendSelectionHintsPreferLowSignal(hit))
            return (hit.SelectionHintQualityPenalty ?? 0) >= 12 ? "low_confidence" : "fragment";

        var actionability = hit.SelectionHintActionabilityScore ?? 0;
        var support = hit.SelectionHintSupportScore ?? 0;
        if (actionability >= 8 && actionability >= support)
            return "actionable_item";
        if (support >= 8)
            return "supporting_context";
        if (support >= 5)
            return "advisory";

        return null;
    }

    private static int ComputeBackendSelectionPriority(RagHitSummary hit)
    {
        if (!HasBackendSelectionHints(hit))
            return 0;

        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        var roleScore = role switch
        {
            "actionable_item" => 100,
            "advisory" => 75,
            "supporting_context" => 65,
            "low_confidence" => 5,
            "fragment" => -65,
            "navigation" => -100,
            null => 0,
            _ => 35
        };

        var evidenceBonus = hit.MatchedContentCards?
            .Take(5)
            .Sum(static card => card.Evidence is not null ? 4 : 0) ?? 0;

        return roleScore
               + ((hit.SelectionHintActionabilityScore ?? 0) * 4)
               + ((hit.SelectionHintSupportScore ?? 0) * 3)
               - ((hit.SelectionHintFragmentScore ?? 0) * 4)
               - ((hit.SelectionHintNavigationScore ?? 0) * 5)
               - ((hit.SelectionHintQualityPenalty ?? 0) * 2)
               + evidenceBonus;
    }

    private static object BuildRagSelectionHintsPayload(RagHitSummary hit, string? query)
    {
        var profile = ClassifyRagHitEvidenceProfile(hit, query);
        return new
        {
            evidenceRole = profile.Role,
            actionabilityScore = profile.ActionabilityScore,
            supportScore = profile.SupportScore,
            fragmentScore = profile.FragmentScore,
            navigationScore = profile.NavigationScore,
            qualityPenalty = profile.QualityPenalty,
            contentRole = string.IsNullOrWhiteSpace(hit.ContentRole) ? null : hit.ContentRole,
            retrievalNavigationScore = hit.NavigationScore,
            contentDensityScore = hit.ContentDensityScore,
            navigationReason = string.IsNullOrWhiteSpace(hit.NavigationReason) ? null : hit.NavigationReason
        };
    }

    private static readonly HashSet<string> QuerySignalStopWords = new(StringComparer.Ordinal)
    {
        "aide", "aider", "avec", "avoir", "cette", "comment", "dans", "faire", "facile", "idee",
        "peux", "pour", "propose", "proposes", "quoi", "semaine", "vais", "veux", "voudrais",
        "donne", "donner", "juste", "liste", "lister",
        "about", "find", "help", "make", "plan", "prepare", "recommend", "suggest", "what", "with",
        "list", "listing",
        "can", "could", "give", "ayuda", "ayudar", "ayudame", "puede", "puedes", "podrias", "propone",
        "recomienda", "ajuda", "ajudar", "pode", "podes", "recomenda", "kannst", "konntest", "helfen",
        "vorschlag", "empfiehl", "aiuta", "aiutami", "puoi", "consiglia", "planejamento",
        "planificacion", "organise", "organize", "organiser", "partir", "plusieurs", "multiple",
        "multiples", "several", "many", "option", "options", "utile", "utiles", "useful",
        "available", "disponible", "disponibles", "uniquement", "seulement", "only", "solely",
        "exclusivement", "exclusively", "chaque", "jour", "jours", "days", "format", "clair",
        "clear", "user", "friendly", "lundi", "mardi", "mercredi", "jeudi", "vendredi",
        "samedi", "dimanche", "monday", "tuesday", "wednesday", "thursday", "friday",
        "saturday", "sunday"
    };

    private static IEnumerable<string> ExtractQuerySignalTerms(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            yield break;

        HashSet<string>? seen = null;
        var emitted = 0;
        var tokenStart = -1;
        for (var i = 0; i <= normalizedQuery.Length; i++)
        {
            if (i < normalizedQuery.Length && char.IsLetterOrDigit(normalizedQuery[i]))
            {
                if (tokenStart < 0)
                    tokenStart = i;
                continue;
            }

            if (tokenStart < 0)
                continue;

            var tokenLength = i - tokenStart;
            if (tokenLength >= 4)
            {
                var term = normalizedQuery.Substring(tokenStart, tokenLength);
                if (!QuerySignalStopWords.Contains(term)
                    && (seen ??= new HashSet<string>(StringComparer.Ordinal)).Add(term))
                {
                    yield return term;
                    emitted++;
                    if (emitted >= 8)
                        yield break;
                }
            }

            tokenStart = -1;
        }
    }

    private static string NormalizeLexicalLookup(string? value)
    {
        var text = CollapseWhitespace(value ?? string.Empty)
            .ToLowerInvariant()
            .Replace("\u0153", "oe", StringComparison.Ordinal)
            .Replace("\u00e6", "ae", StringComparison.Ordinal)
            .Replace("\u00df", "ss", StringComparison.Ordinal);

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return RepairSplitOcrBrokenTitleWords(sb.ToString().Normalize(NormalizationForm.FormC));
    }

    private static string NormalizeLooseLookup(string? value)
        => NormalizeLexicalLookup(value);

}
