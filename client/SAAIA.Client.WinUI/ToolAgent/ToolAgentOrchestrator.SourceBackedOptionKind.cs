using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static SourceBackedOptionCandidate ApplySoftChoiceOptionKindScore(
        SourceBackedOptionCandidate candidate,
        IReadOnlyList<string> requestedKindTerms)
    {
        if (requestedKindTerms.Count == 0)
            return candidate;

        var bonus = ComputeSoftChoiceOptionKindMatchScore(requestedKindTerms, candidate);
        return bonus == 0
            ? candidate
            : candidate with { Score = candidate.Score + bonus };
    }

    private static bool LooksLikeCompositeSourceBackedOptionAnchorRequest(string normalizedQuery, bool hasRequestedMaxMinutes)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;
        if (hasRequestedMaxMinutes || normalizedQuery.Contains('+', StringComparison.Ordinal))
            return true;

        return Regex.IsMatch(
            normalizedQuery,
            @"\b(?:total|combine|combiner|combined|ensemble|together|complet|complete|completa|completo)\b",
            RegexOptions.CultureInvariant);
    }

    private static IReadOnlyList<string> ExtractPairingRequestedOptionKindTerms(string? query)
    {
        if (!LooksLikeSourceBackedPairingRecommendationRequest(query))
            return Array.Empty<string>();

        var normalized = NormalizeLexicalLookup(query);
        var terms = new List<string>();
        foreach (Match match in Regex.Matches(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|which|what|cual|cu[aÃ¡]l|qual|welche|welcher|welches|quale)\s+(?<kind>[\p{L}][\p{L}'\u2019-]{2,30})",
            RegexOptions.CultureInvariant))
        {
            var kind = NormalizeLexicalLookup(match.Groups["kind"].Value);
            if (kind.Length >= 4 && !IsSourceBackedActionRetrievalNoiseTerm(kind))
                terms.Add(kind);
        }

        foreach (Match match in Regex.Matches(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|which|what|cual|cu[aÃ¡]l|qual|welche|welcher|welches|quale)\s+(?<kinds>[\p{L}\s'\u2019-]{3,90}?)(?=\s+(?:trouve|trouves|trouv[eÃ©]s|dans|pour|avec|qui|que|would|could|found|in|for|with|para|con|com|mit|per|irait|iraient|vont|goes?|pair|pairs?|compatible)\b|[?.!,;:]|$)",
            RegexOptions.CultureInvariant))
        {
            var kinds = NormalizeLexicalLookup(match.Groups["kinds"].Value);
            foreach (var kind in Regex.Split(kinds, @"\s+(?:et|ou|and|or|y|e|und|oder|o)\s+", RegexOptions.CultureInvariant))
            {
                var cleanKind = NormalizeLexicalLookup(kind);
                if (cleanKind.Length >= 4 && !IsSourceBackedActionRetrievalNoiseTerm(cleanKind))
                    terms.Add(cleanKind);
            }
        }

        return terms
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractSoftChoiceRequestedOptionKindTerms(string? query)
    {
        if (!LooksLikeSoftChoiceRecommendationRequest(query))
            return Array.Empty<string>();

        var normalized = NormalizeLexicalLookup(query);
        var terms = new List<string>();
        foreach (Match match in Regex.Matches(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|which|what|cual|qual|welche|welcher|welches|quale)\s+(?<kind>[\p{L}][\p{L}'\u2019-]{2,30})",
            RegexOptions.CultureInvariant))
        {
            var kind = NormalizeLexicalLookup(match.Groups["kind"].Value);
            if (kind.Length >= 4 && !IsSourceBackedActionRetrievalNoiseTerm(kind))
                terms.Add(kind);
        }

        foreach (Match match in Regex.Matches(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|which|what|cual|cu[aÃ¡]l|qual|welche|welcher|welches|quale)\s+(?<kinds>[\p{L}\s'\u2019-]{3,90}?)(?=\s+(?:trouve|trouves|trouves|dans|pour|avec|qui|que|would|could|found|in|for|with|para|con|com|mit|per)\b|[?.!,;:]|$)",
            RegexOptions.CultureInvariant))
        {
            var kinds = NormalizeLexicalLookup(match.Groups["kinds"].Value);
            foreach (var kind in Regex.Split(kinds, @"\s+(?:et|ou|and|or|y|e|und|oder|o)\s+", RegexOptions.CultureInvariant))
            {
                var cleanKind = NormalizeLexicalLookup(kind);
                if (cleanKind.Length >= 4 && !IsSourceBackedActionRetrievalNoiseTerm(cleanKind))
                    terms.Add(cleanKind);
            }
        }

        return terms
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractGenericSourceBackedOptionKindTerms(string? query)
    {
        if (!LooksLikeSourceBackedOptionRequest(query))
            return Array.Empty<string>();

        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var terms = new List<string>();
        foreach (Match match in Regex.Matches(
            normalized,
            @"\b(?:liste|list|lista|elenco|auflistung)\s+(?:de|des|d|of|di|von|van)?\s*(?<kinds>[\p{L}\s'\u2019-]{3,80}?)(?=\s+(?:disponible|disponibles|available|trouve|trouves|trouver|dans|source|sources|document|documents|dossier|corpus|base)\b|[?.!,;:]|$)",
            RegexOptions.CultureInvariant))
        {
            AddGenericSourceBackedOptionKindTerms(terms, match.Groups["kinds"].Value);
        }

        foreach (Match match in Regex.Matches(
            normalized,
            @"\b(?:options?|suggestions?|idees?|ideas?|examples?|exemples?|candidats?|candidates?)\s+(?:de|des|d|of|di|pour|for|para|per|von)?\s*(?<kinds>[\p{L}\s'\u2019-]{3,80}?)(?=\s+(?:disponible|disponibles|available|trouve|trouves|trouver|dans|source|sources|document|documents|dossier|corpus|base)\b|[?.!,;:]|$)",
            RegexOptions.CultureInvariant))
        {
            AddGenericSourceBackedOptionKindTerms(terms, match.Groups["kinds"].Value);
        }

        return terms
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
    }

    private static void AddGenericSourceBackedOptionKindTerms(List<string> terms, string? rawKinds)
    {
        var normalized = NormalizeLexicalLookup(rawKinds);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        foreach (var rawKind in Regex.Split(normalized, @"\s+(?:et|ou|and|or|y|e|und|oder|o)\s+", RegexOptions.CultureInvariant))
        {
            var kind = NormalizeGenericSourceBackedOptionKindTerm(rawKind);
            if (kind.Length >= 4
                && !IsSourceBackedActionRetrievalNoiseTerm(kind)
                && !IsGenericPlanningCoverageTerm(kind)
                && !IsNavigationDiscoveryNoiseTerm(kind))
            {
                terms.Add(kind);
            }
        }
    }

    private static string NormalizeGenericSourceBackedOptionKindTerm(string? value)
    {
        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        normalized = Regex.Replace(
            normalized,
            @"\b(?:juste|uniquement|seulement|only|available|disponible|disponibles|trouve|trouves|trouver|source|sources|document|documents|dossier|corpus|base|dans|from|with|avec)\b",
            " ",
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(normalized);
    }

    private static bool PairingOptionKindMatchesCandidate(IReadOnlyList<string> optionKindTerms, SourceBackedOptionCandidate candidate)
    {
        if (optionKindTerms.Count == 0)
            return true;

        var haystack = NormalizeLexicalLookup($"{candidate.Title} {candidate.Hit.SectionTitle} {candidate.Hit.HeadingPath} {GetRagHitPrimaryEvidenceText(candidate.Hit)}");
        return optionKindTerms.Any(term =>
            Regex.IsMatch(
                haystack,
                $@"(^|[^\p{{L}}\p{{N}}]){Regex.Escape(term)}(?:s|es)?([^\p{{L}}\p{{N}}]|$)",
                RegexOptions.CultureInvariant));
    }

    private static int ComputeSoftChoiceOptionKindMatchScore(IReadOnlyList<string> requestedKindTerms, SourceBackedOptionCandidate candidate)
    {
        if (requestedKindTerms.Count == 0)
            return 0;

        var titleAndCards = NormalizeLexicalLookup(
            $"{candidate.Title} {string.Join(' ', candidate.Hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>())}");
        var cardSignals = NormalizeLexicalLookup(
            string.Join(' ', candidate.Hit.MatchedContentCards?.SelectMany(static card => card.Signals ?? Array.Empty<string>()) ?? Array.Empty<string>()));
        var structural = NormalizeLexicalLookup($"{candidate.Hit.SectionTitle} {candidate.Hit.HeadingPath}");

        if (requestedKindTerms.Any(term => OptionKindTermMatchesText(titleAndCards, term)))
            return 18;
        if (requestedKindTerms.Any(term => OptionKindTermMatchesText(cardSignals, term)))
            return 16;
        if (requestedKindTerms.Any(term => OptionKindTermMatchesText(structural, term)))
            return 10;
        return 0;
    }

    private static bool SoftChoiceOptionKindMatchesCandidate(IReadOnlyList<string> requestedKindTerms, SourceBackedOptionCandidate candidate)
    {
        if (requestedKindTerms.Count == 0)
            return true;

        var haystack = NormalizeLexicalLookup(string.Join(' ', new[]
        {
            candidate.Title,
            candidate.Hit.SectionTitle,
            candidate.Hit.HeadingPath,
            string.Join(' ', candidate.Hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>()),
            string.Join(' ', candidate.Hit.MatchedContentCards?.SelectMany(static card => card.Signals ?? Array.Empty<string>()) ?? Array.Empty<string>())
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));

        return requestedKindTerms.Any(term => OptionKindTermMatchesText(haystack, term));
    }

    private static bool OptionKindContradictsCandidate(IReadOnlyList<string> requestedKindTerms, SourceBackedOptionCandidate candidate)
    {
        if (requestedKindTerms.Count == 0)
            return false;

        var requested = requestedKindTerms
            .Select(NormalizeLexicalLookup)
            .Where(static term => term.Length >= 4)
            .ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0)
            return false;

        if (SoftChoiceOptionKindMatchesCandidate(requested.ToArray(), candidate))
            return false;

        return false;
    }

    private static bool OptionKindTermMatchesText(string? text, string? term)
    {
        var normalizedText = NormalizeLexicalLookup(text);
        var normalizedTerm = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalizedText) || normalizedTerm.Length < 3)
            return false;

        if (normalizedText.Contains(normalizedTerm, StringComparison.Ordinal))
            return true;

        var singular = normalizedTerm.TrimEnd('s');
        if (singular.Length >= 3 && !string.Equals(singular, normalizedTerm, StringComparison.Ordinal))
        {
            if (normalizedText.Contains(singular, StringComparison.Ordinal))
                return true;
        }

        return Regex.IsMatch(
            normalizedText,
            $@"(^|[^\p{{L}}\p{{N}}]){Regex.Escape(normalizedTerm)}(?:s|es)?([^\p{{L}}\p{{N}}]|$)",
            RegexOptions.CultureInvariant);
    }

    private static bool RetrievalQueryNarrowlyTargetsOptionKind(string? retrievalQuery, string? term)
    {
        var normalizedQuery = NormalizeLexicalLookup(retrievalQuery);
        var normalizedTerm = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalizedQuery) || normalizedTerm.Length < 3)
            return false;

        if (!OptionKindTermMatchesText(normalizedQuery, normalizedTerm))
            return false;

        var terms = ExtractQuerySignalTerms(normalizedQuery)
            .Where(static value => !IsSourceBackedActionRetrievalNoiseTerm(value))
            .ToArray();
        if (terms.Length == 0 || terms.Length > 3)
            return false;

        return terms.Any(value => OptionKindTermMatchesText(value, normalizedTerm));
    }
}
