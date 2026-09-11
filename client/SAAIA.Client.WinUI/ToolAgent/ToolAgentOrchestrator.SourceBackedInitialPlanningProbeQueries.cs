using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string[] BuildInitialSourceBackedPlanningProbeQueries(
        string effectiveUserMessage,
        IEnumerable<string>? routerQueries = null)
    {
        var candidates = new List<string>();
        var routerCandidateKeys = new HashSet<string>(StringComparer.Ordinal);
        var pinnedIntentQueries = new List<string>();
        var intentProbe = BuildInitialSourceBackedPlanningIntentProbeQuery(effectiveUserMessage);
        AddInitialSourceBackedPlanningProbeQuery(pinnedIntentQueries, intentProbe);
        foreach (var query in pinnedIntentQueries)
            AddDistinctQuery(candidates, query);
        var pinnedIntentKeys = pinnedIntentQueries
            .Select(NormalizeLexicalLookup)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        var preferredCandidateKeys = new HashSet<string>(pinnedIntentKeys, StringComparer.Ordinal);

        if (routerQueries is not null)
        {
            foreach (var query in routerQueries)
            {
                var beforeCount = candidates.Count;
                AddInitialSourceBackedPlanningProbeQuery(candidates, query);
                if (candidates.Count > beforeCount)
                {
                    var key = NormalizeLexicalLookup(candidates[^1]);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        routerCandidateKeys.Add(key);
                        preferredCandidateKeys.Add(key);
                    }
                }
            }
        }

        foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage))
            AddInitialSourceBackedPlanningProbeQuery(candidates, query);

        foreach (var query in BuildPlanningRetrievalQueries(effectiveUserMessage))
            AddInitialSourceBackedPlanningProbeQuery(candidates, query);

        if (candidates.Count == 0)
            AddDistinctQuery(candidates, NormalizeRagQueryForRetrieval(effectiveUserMessage));

        var selectedQueries = candidates
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .GroupBy(static query => NormalizeInitialSourceBackedPlanningProbeFamilyKey(query), StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(query => ScoreInitialSourceBackedPlanningProbeQuery(query, effectiveUserMessage, preferredCandidateKeys))
                .ThenBy(static query => query.Length)
                .First())
            .OrderBy(query => ScoreInitialSourceBackedPlanningProbeQuery(query, effectiveUserMessage, preferredCandidateKeys))
            .ThenBy(static query => query.Length)
            .ToArray();

        return pinnedIntentQueries
            .Concat(selectedQueries.Where(query => !pinnedIntentKeys.Contains(NormalizeLexicalLookup(query))))
            .Take(MaxInitialSourceBackedPlanningProbeQueries)
            .ToArray();
    }

    private static string BuildInitialSourceBackedPlanningIntentProbeQuery(string? effectiveUserMessage)
    {
        var normalized = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = NormalizeLexicalLookup(effectiveUserMessage);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var language = DetectRetrievalExpansionLanguage(effectiveUserMessage);
        var terms = new List<string>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        void AddTerm(string? value)
        {
            var normalizedTerm = NormalizeLexicalLookup(value);
            if (string.IsNullOrWhiteSpace(normalizedTerm))
                return;

            var tokens = Regex.Matches(normalizedTerm, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(static match => match.Value)
                .Where(static token => !IsSourceBackedPlanningIntentProbeNoiseToken(token))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (tokens.Length == 0)
                return;

            var cleaned = CollapseWhitespace(string.Join(' ', tokens));
            if (cleaned.Length is < 3 or > 54
                || LooksLikeNavigationDiscoveryProbeQuery(cleaned)
                || IsInitialSourceBackedPlanningProbeModifierToken(cleaned))
            {
                return;
            }

            if (emitted.Add(cleaned))
                terms.Add(cleaned);
        }

        var dayAxis = DetectRequestedDayAxisLabels(effectiveUserMessage, language);
        var slotAxis = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language)
            .Concat(ExtractPlanningSlotRetrievalTerms(effectiveUserMessage))
            .Select(SelectPreferredPlanningSlotRetrievalTerm)
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var axisTokens = dayAxis
            .Concat(slotAxis)
            .SelectMany(static term => Regex.Matches(NormalizeLexicalLookup(term), @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(static match => match.Value))
            .ToHashSet(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(
                     normalized,
                     @"\b(?:plan|planning|programme|schedule|calendar|calendrier|semaine|hebdomadaire|week|weekly|semana|semanal|woche|wochenplan|settimana|settimanale)\b",
                     RegexOptions.CultureInvariant))
        {
            AddTerm(match.Value);
        }

        foreach (var term in Regex.Matches(normalized, @"[\p{L}\p{Nd}]{4,}", RegexOptions.CultureInvariant)
                     .Cast<Match>()
                     .Select(static match => match.Value)
                     .Where(static term => term.Length >= 4)
                     .Where(term => !axisTokens.Contains(term))
                     .Where(static term => !IsGenericPlanningCoverageTerm(term))
                     .Where(static term => !IsSourceBackedPlanningIntentProbeNoiseToken(term))
                     .Where(static term => !IsInitialSourceBackedPlanningProbeModifierToken(term))
                     .Where(static term => !IsNavigationDiscoveryNoiseTerm(term))
                     .Distinct(StringComparer.Ordinal)
                     .Take(6))
        {
            AddTerm(term);
        }

        var compactDayAxis = dayAxis.Count > 2
            ? new[] { dayAxis[0], dayAxis[^1] }
            : dayAxis.ToArray();
        foreach (var day in compactDayAxis)
            AddTerm(day);

        foreach (var slot in slotAxis.Take(8))
        {
            AddTerm(slot);
        }

        if (terms.Count < 2)
            return string.Empty;

        var selected = new List<string>();
        foreach (var term in terms)
        {
            var candidate = selected.Count == 0
                ? term
                : string.Join(' ', selected.Concat(new[] { term }));
            if (candidate.Length > 90)
                continue;

            selected.Add(term);
        }

        return selected.Count >= 2
            ? CollapseWhitespace(string.Join(' ', selected))
            : string.Empty;
    }

    private static bool IsSourceBackedPlanningIntentProbeNoiseToken(string token)
    {
        var normalized = NormalizeLexicalLookup(token);
        return string.IsNullOrWhiteSpace(normalized)
            || IsWeakRouterRagQueryToken(normalized)
            || normalized is
                "uniquement" or "seulement" or "only" or "strictement" or "juste" or
                "utilise" or "utiliser" or "utilises" or "using" or
                "evite" or "eviter" or "evites" or "avoid" or "avoids" or
                "doublon" or "doublons" or "duplicate" or "duplicates" or "duplique" or "dupplique" or
                "inutile" or "inutiles" or "unneeded" or "unnecessary" or
                "donne" or "donner" or "donnes" or "provide" or
                "format" or "clair" or "claire" or "clear" or "friendly" or "lisible" or "readable" or
                "user" or "markdown" or "tableau" or "table" or "final" or "finale" or "reponse" or "answer";
    }

    private static void AddInitialSourceBackedPlanningProbeQuery(List<string> queries, string? query)
    {
        var normalizedQuery = CleanInitialSourceBackedPlanningProbeQuery(CollapseWhitespace(query ?? string.Empty));
        if (string.IsNullOrWhiteSpace(normalizedQuery)
                || IsLowValueRouterRagQuery(normalizedQuery)
                || LooksLikeNavigationDiscoveryProbeQuery(normalizedQuery))
        {
            return;
        }

        AddDistinctQuery(queries, normalizedQuery);
    }

    private static string CleanInitialSourceBackedPlanningProbeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var normalized = NormalizeLexicalLookup(query);
        var rawTokenCount = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant).Count;
        var tokens = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakRouterRagQueryToken(token))
            .Where(static token => !IsInitialSourceBackedPlanningProbeModifierToken(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (tokens.Length >= 2)
            return string.Join(' ', tokens);
        if (tokens.Length == 1 && rawTokenCount > 1)
            return tokens[0];
        if (tokens.Length == 0)
            return string.Empty;

        return CollapseWhitespace(query);
    }

    private static int ScoreInitialSourceBackedPlanningProbeQuery(
        string query,
        string effectiveUserMessage,
        ISet<string>? preferredNormalizedQueries = null)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return int.MaxValue;

        var score = 0;
        if (preferredNormalizedQueries?.Contains(normalizedQuery) == true)
            score -= query.Length <= 90 ? 80 : 20;

        if (query.Length > 140)
            score += 220;
        else if (query.Length > 100)
            score += 120;
        else if (query.Length > 72)
            score += 45;

        var tokens = Regex.Matches(normalizedQuery, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakRouterRagQueryToken(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var modifierTokenCount = Regex.Matches(normalizedQuery, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => IsInitialSourceBackedPlanningProbeModifierToken(token))
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (tokens.Length <= 1)
            score += 30;
        else if (tokens.Length > 10)
            score += 90 + ((tokens.Length - 10) * 4);
        else if (tokens.Length > 6)
            score += 25;
        if (modifierTokenCount > 0)
            score += Math.Min(40, modifierTokenCount * 12);

        var normalizedUserMessage = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        if (!string.IsNullOrWhiteSpace(normalizedUserMessage)
            && string.Equals(normalizedQuery, normalizedUserMessage, StringComparison.Ordinal)
            && query.Length > 72)
        {
            score += 180;
        }

        var userSignalTerms = ExtractQuerySignalTerms(normalizedUserMessage)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        var overlap = userSignalTerms.Count(term => normalizedQuery.Contains(term, StringComparison.Ordinal));
        score += overlap == 0 ? 20 : -Math.Min(18, overlap * 3);

        return score;
    }

    private static string NormalizeInitialSourceBackedPlanningProbeFamilyKey(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var tokens = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakRouterRagQueryToken(token))
            .Where(static token => !IsInitialSourceBackedPlanningProbeModifierToken(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return tokens.Length == 0
            ? normalized
            : string.Join(' ', tokens);
    }

    private static bool IsInitialSourceBackedPlanningProbeModifierToken(string token)
        => token is
            "option" or "options" or "idee" or "idees" or "exemple" or "exemples"
            or "suggestion" or "suggestions" or "candidat" or "candidats" or "candidate" or "candidates"
            or "proposition" or "propositions" or "preparation" or "preparations"
            or "element" or "elements" or "item" or "items" or "contenu" or "contenus"
            or "detail" or "details" or "etape" or "etapes" or "source" or "sources";

    private static bool LooksLikeNavigationDiscoveryProbeQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var navigationTerms = new[]
        {
            "sommaire",
            "table des matieres",
            "contents",
            "table of contents",
            "table matieres",
            "index",
            "catalogue",
            "catalog",
            "liste",
            "list",
            "sections",
            "sections principales",
            "indice",
            "contenido",
            "tabla de contenido",
            "sumario",
            "visao geral",
            "inhaltsverzeichnis",
            "inhalt",
            "uebersicht",
            "sommario",
            "panoramica",
            "overview"
        };

        if (Regex.IsMatch(normalized, @"\btable\b.*\bmatieres?\b|\bmatieres?\b.*\btable\b", RegexOptions.CultureInvariant))
            return true;

        return navigationTerms.Any(term =>
            string.Equals(normalized, term, StringComparison.Ordinal)
            || normalized.Contains(term, StringComparison.Ordinal));
    }
}