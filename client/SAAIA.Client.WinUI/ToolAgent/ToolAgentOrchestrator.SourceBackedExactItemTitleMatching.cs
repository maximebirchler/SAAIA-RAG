using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static bool RagHitsContainRequestedTitle(IReadOnlyList<RagHitSummary> hits, string requestedTitle)
    {
        if (string.IsNullOrWhiteSpace(requestedTitle))
            return false;

        foreach (var hit in hits)
        {
            if (RagHitContainsRequestedTitle(hit, requestedTitle))
                return true;
        }

        return false;
    }

    private static bool RagHitContainsRequestedTitle(RagHitSummary hit, string requestedTitle)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);

        if (RagHitContainsRequestedTitle(hit, normalizedTitle, titleTerms))
            return true;

        foreach (var variant in BuildTypoTolerantQueryVariants(normalizedTitle))
        {
            var normalizedVariant = NormalizeLexicalLookup(variant);
            if (string.IsNullOrWhiteSpace(normalizedVariant))
                continue;

            var variantTerms = ExtractRequestedTitleSignalTerms(normalizedVariant);

            if (RagHitContainsRequestedTitle(hit, normalizedVariant, variantTerms))
                return true;
        }

        return false;
    }

    private static bool RagHitHasUsableRequestedTitleAnchor(string requestedTitle, RagHitSummary hit)
    {
        if (LooksLikeNavigationOnlyHit(hit) || string.IsNullOrWhiteSpace(requestedTitle))
            return false;

        if (ComputeBestSourceBackedDisplayTitleScore(requestedTitle, hit) >= 40)
            return true;

        if (RagHitContainsRequestedTitlePhraseAnchor(hit, requestedTitle))
            return true;

        if (RagHitHasCoherentRequestedTitleContent(hit, requestedTitle))
            return true;

        return ComputeExactItemAnchorStrengthScore(requestedTitle, hit) >= 80;
    }

    private static bool HasStrongExactItemTitleAnchor(string requestedTitle, RagHitSummary hit)
    {
        if (string.IsNullOrWhiteSpace(requestedTitle))
            return false;

        return ComputeBestSourceBackedDisplayTitleScore(requestedTitle, hit) >= 40
               || RagHitContainsRequestedTitlePhraseAnchor(hit, requestedTitle)
               || ComputeExactVisibleTitleMatchScore(requestedTitle, hit) >= 40;
    }

    private static bool ExactItemEvidenceStartsWithRequestedTitle(string requestedTitle, RagHitSummary hit)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        foreach (var candidate in new[]
                 {
                     hit.Excerpt,
                     hit.FullText,
                     string.IsNullOrWhiteSpace(hit.ContextualSnippet) ? null : StripContextualMetadataForEvidence(hit.ContextualSnippet!)
                 })
        {
            var normalized = NormalizeLexicalLookup(candidate);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (TextStartsWithRequestedTitlePhrase(normalized, normalizedTitle))
                return true;

            foreach (var variant in BuildTypoTolerantQueryVariants(normalizedTitle))
            {
                var normalizedVariant = NormalizeLexicalLookup(variant);
                if (!string.IsNullOrWhiteSpace(normalizedVariant)
                    && TextStartsWithRequestedTitlePhrase(normalized, normalizedVariant))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsUsableExactItemNavigationOverride(string? requestedTitle, RagHitSummary hit)
    {
        if (string.IsNullOrWhiteSpace(requestedTitle))
            return false;
        var hasNavigationPenalty = LooksLikeNavigationOnlyHit(hit)
                                   || BackendSelectionHintsPreferNavigation(hit)
                                   || NormalizeLexicalLookup(hit.ContentRole).Contains("navigation", StringComparison.Ordinal);
        if (!hasNavigationPenalty)
            return false;
        if (LooksLikePageReferenceOnlyHit(hit) || LooksLikeExactItemReferenceOnlyHit(requestedTitle!, hit))
            return false;

        if (!ExactItemEvidenceStartsWithRequestedTitle(requestedTitle!, hit)
            && !HasStrongExactItemTitleAnchor(requestedTitle!, hit))
            return false;

        var evidence = GetFocusedExactItemEvidenceText(requestedTitle!, hit);
        var structuredCueScore = Math.Max(0, ComputeExactItemCardCompletenessCueScore(hit))
                                 + Math.Max(0, ComputeProcedureCompletenessCueScore(hit))
                                 + Math.Max(0, ComputeStructuredProcedureVisibleEvidenceCueScore(hit));
        if (structuredCueScore >= 4)
            return true;
        if (ExtractVisibleDurationsAndQuantities(evidence).Length > 0)
            return true;
        if (ExtractContentCardEvidenceFacts(new[] { hit }).Length > 0)
            return true;

        var normalizedEvidence = NormalizeLexicalLookup(evidence);
        return CountProcedureStepMarkers(normalizedEvidence) > 0
               || Regex.IsMatch(
                   normalizedEvidence,
                   @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?|preparation|pr[e\u00e9]paration|operation|workflow|execution|procedure|etapes?|[e\u00e9]tapes?|temps|time|pour\s+\d{1,3}\s+\p{L}|\d+\s*(?:g|kg|mg|ml|cl|l|min(?:ute)?s?|h|heures?|hours?))\b",
                   RegexOptions.CultureInvariant);
    }

    private static bool RagHitHasCoherentRequestedTitleContent(RagHitSummary hit, string requestedTitle)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        var titleTerms = ExtractRequestedTitleSignalTerms(normalizedTitle);
        if (titleTerms.Length == 0)
            return false;

        var content = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(content))
            return false;

        if (!titleTerms.All(term => content.Contains(term, StringComparison.Ordinal)))
            return false;

        if (titleTerms.Length <= 2)
        {
            var lead = CollapseWhitespace(hit.Excerpt ?? string.Empty);
            if (lead.Length > 360)
                lead = lead[..360];
            var normalizedLead = NormalizeLexicalLookup(lead);
            return IndexOfRequestedTitlePhrase(normalizedLead, normalizedTitle) is >= 0 and <= 160
                && ComputeExactVisibleTitleMatchScore(requestedTitle, hit) >= 40;
        }

        return ComputeExactItemCardCompletenessCueScore(hit) >= 4
            || ComputeProcedureCompletenessCueScore(hit) >= 4
            || CountProcedureStepMarkers(content) >= 2;
    }

    private static bool RagHitContainsRequestedTitlePhraseAnchor(RagHitSummary hit, string requestedTitle)
    {
        var normalizedTitle = NormalizeLexicalLookup(requestedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var titleAnchors = NormalizeLexicalLookup(string.Join(' ', new[]
        {
            string.Join(' ', hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>()),
            string.Join(' ', ExtractProfileTitleCandidates(hit.ContextualSnippet ?? string.Empty)),
            hit.SectionTitle ?? string.Empty,
            hit.HeadingPath ?? string.Empty
        }));

        if (ContainsRequestedTitlePhrase(titleAnchors, normalizedTitle))
            return true;

        var lead = CollapseWhitespace(hit.Excerpt ?? string.Empty);
        if (lead.Length > 360)
            lead = lead[..360];
        var normalizedLead = NormalizeLexicalLookup(lead);
        var leadIndex = IndexOfRequestedTitlePhrase(normalizedLead, normalizedTitle);
        if (leadIndex == 0 && TextStartsWithRequestedTitlePhrase(normalizedLead, normalizedTitle))
            return true;
        if (leadIndex is >= 0 and <= 160 && ComputeExactVisibleTitleMatchScore(requestedTitle, hit) >= 40)
            return true;

        var contextualLead = CollapseWhitespace(StripContextualMetadataForEvidence(hit.ContextualSnippet ?? string.Empty));
        if (contextualLead.Length > 360)
            contextualLead = contextualLead[..360];
        var normalizedContextualLead = NormalizeLexicalLookup(contextualLead);
        var contextualLeadIndex = IndexOfRequestedTitlePhrase(normalizedContextualLead, normalizedTitle);
        if (contextualLeadIndex == 0 && TextStartsWithRequestedTitlePhrase(normalizedContextualLead, normalizedTitle))
            return true;
        if (contextualLeadIndex is >= 0 and <= 160 && ComputeExactVisibleTitleMatchScore(requestedTitle, hit) >= 40)
            return true;

        foreach (var variant in BuildTypoTolerantQueryVariants(normalizedTitle))
        {
            var normalizedVariant = NormalizeLexicalLookup(variant);
            if (ContainsRequestedTitlePhrase(titleAnchors, normalizedVariant))
            {
                return true;
            }

            leadIndex = IndexOfRequestedTitlePhrase(normalizedLead, normalizedVariant);
            if (leadIndex is >= 0 and <= 160 && ComputeExactVisibleTitleMatchScore(normalizedVariant, hit) >= 40)
                return true;
        }

        return false;
    }

    private static bool TextStartsWithRequestedTitlePhrase(string normalizedText, string normalizedTitle)
        => !string.IsNullOrWhiteSpace(normalizedText)
           && !string.IsNullOrWhiteSpace(normalizedTitle)
           && (normalizedText.Equals(normalizedTitle, StringComparison.Ordinal)
               || normalizedText.StartsWith(normalizedTitle + " ", StringComparison.Ordinal));

    private static bool ContainsRequestedTitlePhrase(string haystack, string normalizedTitle)
        => IndexOfRequestedTitlePhrase(haystack, normalizedTitle) >= 0;

    private static int IndexOfRequestedTitlePhrase(string haystack, string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(normalizedTitle))
            return -1;

        return haystack.IndexOf(normalizedTitle, StringComparison.Ordinal);
    }

    private static bool RagHitContainsRequestedTitle(RagHitSummary hit, string normalizedTitle, IReadOnlyList<string> titleTerms)
    {
        if (LooksLikeNavigationOnlyHit(hit))
            return false;

        var haystack = NormalizeLexicalLookup($"{hit.DocName} {hit.DocPath} {hit.SectionTitle} {hit.HeadingPath} {hit.Excerpt} {hit.FullText} {hit.ContextualSnippet}");
        if (haystack.Contains(normalizedTitle, StringComparison.Ordinal))
            return true;

        return titleTerms.Count > 0 && titleTerms.All(term => haystack.Contains(term, StringComparison.Ordinal));
    }

    private static string[] ExtractRequestedTitleSignalTerms(string normalizedTitle)
    {
        var normalized = NormalizeLexicalLookup(normalizedTitle);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "an", "and", "au", "aux", "avec", "con", "da", "das", "de", "dei", "del", "della",
            "der", "des", "di", "do", "dos", "du", "e", "el", "en", "et", "for", "il", "in", "la",
            "las", "le", "les", "lo", "of", "on", "os", "per", "pour", "sur", "the", "to", "un",
            "una", "une", "und", "y"
        };

        var tokens = Regex.Matches(normalized, @"[\p{L}\p{N}]{1,}")
            .Select(match => match.Value)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        if (tokens.Length == 0)
            return Array.Empty<string>();

        var terms = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (stopWords.Contains(token))
                continue;

            if (token.Length >= 4 || token.Any(char.IsDigit))
            {
                terms.Add(token);
                continue;
            }

            var isShortDisambiguator = token.Length is 2 or 3
                && (i == tokens.Length - 1 || tokens.Length <= 3);
            if (isShortDisambiguator)
                terms.Add(token);
        }

        return terms
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static bool LooksLikeNavigationOnlyHit(RagHitSummary hit)
    {
        if (BackendSelectionHintsPreferUsableEvidence(hit))
            return false;
        if (LooksLikeResolvedRouteTargetHit(hit))
            return false;
        if (BackendSelectionHintsPreferNavigation(hit))
            return true;

        var text = hit.Excerpt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = NormalizeLexicalLookup(text);
        var hasStructuredEvidenceCue = Regex.IsMatch(
            normalized,
            @"\b(?:preparation|operation|workflow|execution|procedure|procedures?|process|instruction|instructions|etape|etapes|step|steps|quantity|quantite|value|valeur|\d+\s*(?:g|kg|mg|ml|cl|l|min(?:ute)?s?|h|heures?|hours?|c|celsius))\b",
            RegexOptions.CultureInvariant);
        var hasNavigationCue = Regex.IsMatch(
            normalized,
            @"\b(?:sommaire|table des matieres|contents|index|inhaltsverzeichnis|indice|reperes de contenu|reperes contenus?|repere de contenu|sections principales|premiers extraits|table of contents|content overview)\b",
            RegexOptions.CultureInvariant);
        if (hasNavigationCue)
        {
            if (!hasStructuredEvidenceCue)
                return true;
        }

        var leaderCount = Regex.Matches(text, @"\.{3,}\s*\d{1,4}\b", RegexOptions.CultureInvariant).Count;
        if (leaderCount >= 3)
            return true;

        var compactListCount = Regex.Matches(text, @"[\p{L}\)]\d{1,3}(?:[â€¢\u2022]|\s*[A-Z\u00c0-\u017f])", RegexOptions.CultureInvariant).Count;
        var titlePageRefs = Regex.Matches(
            normalized,
            @"\b[\p{L}][\p{L}'\-\s]{4,48}\s+\d{1,3}\b",
            RegexOptions.CultureInvariant).Count;
        var shortTitlePageSequence = Regex.IsMatch(
            normalized,
            @"\b[\p{L}'\-]{4,}(?:\s+[\p{L}'\-]{2,}){0,5}\s+\d{2,3}\s+[\p{L}'\-]{4,}",
            RegexOptions.CultureInvariant);
        return (compactListCount >= 6 || titlePageRefs >= 8 || (shortTitlePageSequence && normalized.Length < 420)) && !hasStructuredEvidenceCue
               || (hasNavigationCue && titlePageRefs >= 3);
    }

    private static bool ShouldExposeHitForSourceBackedEvidenceDiscovery(RagHitSummary hit)
    {
        if (LooksLikeResolvedRouteTargetHit(hit))
            return true;
        if (LooksLikeNavigationOnlyHit(hit))
            return IsRouteDiscoveryAnchorHit(hit);

        return !LooksLikeLowSignalContentCandidateHit(hit);
    }

    private static bool LooksLikeResolvedRouteTargetHit(RagHitSummary hit)
    {
        var contentRole = NormalizeLexicalLookup(hit.ContentRole);
        var mixedNavigationContent = contentRole.Contains("mixednavigationcontent", StringComparison.Ordinal)
                                     || (contentRole.Contains("navigation", StringComparison.Ordinal)
                                         && contentRole.Contains("content", StringComparison.Ordinal)
                                         && hit.NavigationScore.GetValueOrDefault() >= 0.75
                                         && hit.ContentDensityScore.GetValueOrDefault() >= 0.30);
        if (!LooksLikeTrustedResolvedRouteRetriever(hit) && !mixedNavigationContent)
            return false;
        if (string.IsNullOrWhiteSpace(hit.DocPath) && string.IsNullOrWhiteSpace(hit.DocName))
            return false;
        if (hit.PageStart <= 0)
            return false;

        var evidence = CollapseWhitespace(GetBestRagEvidenceText(hit));
        if (LooksLikeRouteTargetStructuredEvidence(evidence))
            return true;

        if (contentRole.Contains("content", StringComparison.Ordinal)
            && evidence.Length >= 80
            && !LooksLikePureRouteNavigationText(evidence))
        {
            return true;
        }

        if (hit.MatchedContentCards is { Count: > 0 } cards)
        {
            if (cards.Any(HasConcreteContentCardEvidence))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasConcreteContentCardEvidence(RagHitContentCardSummary card)
        => card.RawEvidence.HasValue
           || card.Evidence is { QuantityFacts.Count: > 0 }
           || card.Evidence?.Facts is { Count: > 0 };

    private static bool IsRouteDiscoveryAnchorHit(RagHitSummary hit)
    {
        if (!LooksLikeRouteBasedRetrievalHit(hit) && !HasRouteContentCardCue(hit))
            return false;
        if (string.IsNullOrWhiteSpace(hit.DocPath) && string.IsNullOrWhiteSpace(hit.DocName))
            return false;
        if (hit.PageStart <= 0)
            return false;

        var cue = ExtractRouteDiscoveryTitleCue(hit);
        if (!string.IsNullOrWhiteSpace(cue))
            return true;

        if (!string.IsNullOrWhiteSpace(hit.SectionTitle) || !string.IsNullOrWhiteSpace(hit.HeadingPath))
            return true;

        return CollapseWhitespace(GetBestRagEvidenceText(hit)).Length >= 40;
    }

    private static bool LooksLikeRouteBasedRetrievalHit(RagHitSummary hit)
        => ContainsRouteRetrievalCue(hit.Retriever)
           || ContainsRouteRetrievalCue(hit.EmbeddingBasis);

    private static bool LooksLikeTrustedResolvedRouteRetriever(RagHitSummary hit)
        => ContainsTrustedResolvedRouteCue(hit.Retriever)
           || ContainsTrustedResolvedRouteCue(hit.EmbeddingBasis);

    private static bool ContainsRouteRetrievalCue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("navigation_route", StringComparison.OrdinalIgnoreCase)
               || value.Contains("title_anchor_route", StringComparison.OrdinalIgnoreCase)
               || value.Contains("direct_title_token_route", StringComparison.OrdinalIgnoreCase)
               || value.Contains("local_title_token_route", StringComparison.OrdinalIgnoreCase)
               || value.Contains("explicit_document_title_route", StringComparison.OrdinalIgnoreCase)
               || value.Contains("fuzzy_title_lead", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsTrustedResolvedRouteCue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("navigation_route", StringComparison.OrdinalIgnoreCase)
               || value.Contains("title_anchor_route", StringComparison.OrdinalIgnoreCase)
               || value.Contains("explicit_document_title_route", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRouteContentCardCue(RagHitSummary hit)
        => hit.MatchedContentCards is { Count: > 0 } cards
           && cards.Any(static card =>
               ContainsRouteRetrievalCue(card.Kind)
               || card.Signals?.Any(ContainsRouteRetrievalCue) == true);

    private static bool LooksLikeRouteTargetStructuredEvidence(string? evidence)
    {
        var normalized = NormalizeLexicalLookup(evidence);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:preparation|preparer|procedure|procedures?|operation|workflow|instruction|instructions|etape|etapes|steps?|duration|duree|temps|quantity|quantite|valeur|value|\d+\s*(?:g|kg|mg|ml|cl|l|min(?:ute)?s?|h|heures?|hours?|c|celsius))\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikePureRouteNavigationText(string? evidence)
    {
        var normalized = NormalizeLexicalLookup(evidence);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var hasNavigationCue = Regex.IsMatch(
            normalized,
            @"\b(?:sommaire|table des matieres|table of contents|contents|index|sections principales|content overview|premiers extraits)\b",
            RegexOptions.CultureInvariant);
        return hasNavigationCue && !LooksLikeRouteTargetStructuredEvidence(normalized);
    }

    private static string ExtractRouteDiscoveryTitleCue(RagHitSummary hit)
        => ExtractRouteDiscoveryTitleCues(hit).FirstOrDefault() ?? string.Empty;

    private static IReadOnlyList<string> ExtractRouteDiscoveryTitleCues(RagHitSummary hit)
    {
        var cues = new List<string>();
        void AddCue(string? raw)
        {
            var title = CleanNavigationRouteAnchorTitle(raw);
            if (!IsUsableSourceBackedOptionTitle(title))
                return;

            if (!cues.Contains(title, StringComparer.OrdinalIgnoreCase))
                cues.Add(title);
        }

        if (hit.MatchedContentCards is { Count: > 0 } cards)
        {
            foreach (var card in cards.Where(static card =>
                         ContainsRouteRetrievalCue(card.Kind)
                         || card.Signals?.Any(ContainsRouteRetrievalCue) == true))
            {
                AddCue(card.Title);
            }
        }

        foreach (var text in new[] { hit.ContextualSnippet, hit.FullText, hit.Excerpt })
        {
            foreach (Match match in Regex.Matches(
                         text ?? string.Empty,
                         @"(?im)\bMatched\s+(?:navigation_route|title_anchor_route|direct_title_token_route|local_title_token_route|explicit_document_title_route|fuzzy_title_lead)\s*:\s*(?<title>[^\r\n|]+)",
                         RegexOptions.CultureInvariant))
            {
                AddCue(match.Groups["title"].Value);
            }

            foreach (var title in ExtractNavigationTitlePageCandidates(text))
                AddCue(title);
        }

        AddCue(hit.SectionTitle);
        AddCue(hit.HeadingPath);
        return cues.Take(12).ToArray();
    }

    private static IEnumerable<string> ExtractNavigationTitlePageCandidates(string? text)
    {
        var normalized = CollapseWhitespace(text ?? string.Empty);
        if (normalized.Length < 8)
            yield break;

        var hasNavigationCue = Regex.IsMatch(
            NormalizeLexicalLookup(normalized),
            @"\b(?:sommaire|table des matieres|table of contents|contents|index|sections principales|overview|contenu|indice|inhaltsverzeichnis|sommario)\b",
            RegexOptions.CultureInvariant);
        if (!hasNavigationCue)
            yield break;

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in ExtractNavigationTitlePageCandidatesFromLines(text)
                     .Concat(ExtractNavigationTitlePageCandidatesFromCollapsed(normalized)))
        {
            var cleaned = CleanNavigationIndexCandidateTitle(title);
            if (string.IsNullOrWhiteSpace(cleaned)
                || LooksLikeNavigationIndexHeadingTitle(cleaned)
                || !emitted.Add(cleaned))
                continue;

            yield return cleaned;
        }
    }

    private static IEnumerable<string> ExtractNavigationTitlePageCandidatesFromLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (var rawLine in Regex.Split(text, @"[\r\n]+", RegexOptions.CultureInvariant))
        {
            var line = CollapseWhitespace(rawLine);
            if (line.Length < 6)
                continue;

            foreach (Match match in Regex.Matches(
                         line,
                         @"(?i)^\s*(?:[\u2022\-*]\s*)?(?<title>[\p{L}][\p{L}\p{N}'\u2019&/(),.\- ]{3,90}?)\s*(?:\.{2,}|-{2,}|\s{2,})\s*(?:p(?:age)?\.?\s*)?(?<page>\d{1,4})(?:\s*[-\u2013]\s*\d{1,4})?\s*$",
                         RegexOptions.CultureInvariant))
            {
                yield return match.Groups["title"].Value;
            }

            foreach (Match match in Regex.Matches(
                         line,
                         @"(?i)^\s*(?:p(?:age)?\.?\s*)?(?<page>\d{1,4})(?:\s*[-\u2013]\s*\d{1,4})?\s*[:.\-\u2013]?\s*(?<title>[\p{L}][\p{L}\p{N}'\u2019&/(),.\- ]{3,90}?)\s*$",
                         RegexOptions.CultureInvariant))
            {
                yield return match.Groups["title"].Value;
            }
        }
    }

    private static IEnumerable<string> ExtractNavigationTitlePageCandidatesFromCollapsed(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        var pageMatches = Regex.Matches(
            normalized,
            @"(?i)(?<![\p{L}\p{N}])(?:p(?:age)?\.?\s*)?\d{1,4}(?:\s*[-\u2013]\s*\d{1,4})?(?![\p{L}\p{N}])",
            RegexOptions.CultureInvariant);
        if (pageMatches.Count == 0)
            yield break;

        var previousEnd = 0;
        foreach (Match match in pageMatches)
        {
            var length = match.Index - previousEnd;
            if (length > 0)
                yield return normalized.Substring(previousEnd, length);

            previousEnd = match.Index + match.Length;
        }
    }

    private static string CleanNavigationIndexCandidateTitle(string? value)
    {
        var title = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', '.', ',', ';', ':', '"', '\'', '\u2022', '\u00b7', '-', '\u2013');
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        title = Regex.Replace(
            title,
            @"(?i)^(?:sommaire|table\s+des\s+matieres|table\s+of\s+contents|contents|index|sections\s+principales|overview|contenu|indice|inhaltsverzeichnis|sommario)\b\s*[:.\-\u2013]*\s*",
            string.Empty,
            RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"(?i)^(?:chapitre|chapter|section|partie|part|annexe|appendix)\s+\d{1,4}\s*[:.\-\u2013]*\s*",
            string.Empty,
            RegexOptions.CultureInvariant);
        title = Regex.Replace(
            title,
            @"(?i)^(?:\d+|[ivxlcdm]{1,6})[.)]\s+",
            string.Empty,
            RegexOptions.CultureInvariant);

        return CleanNavigationRouteAnchorTitle(title);
    }

    private static string CleanNavigationRouteAnchorTitle(string? value)
    {
        var title = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', '.', ',', ';', ':', '"', '\'', '\u2022', '\u00b7', '-', '\u2013');
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        title = Regex.Replace(title, @"^(?:\d+|[ivxlcdm]{1,6})[.)]\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"\s+", " ", RegexOptions.CultureInvariant).Trim(' ', '-', ':', '.', ',', ';');
        return title.Length <= 90 ? title : title[..90].TrimEnd();
    }

    private static bool LooksLikeNavigationIndexHeadingTitle(string? value)
    {
        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return Regex.IsMatch(
            normalized,
            @"^(?:sommaire|table des matieres|table of contents|contents|index|page|pages|chapter|chapitre|section|partie|part|table|liste|list|overview|contenu|indice|inhaltsverzeichnis|sommario)$",
            RegexOptions.CultureInvariant);
    }
}
