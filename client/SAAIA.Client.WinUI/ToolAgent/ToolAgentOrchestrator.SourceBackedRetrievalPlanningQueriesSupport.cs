using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IReadOnlyList<string> BuildPlanningExpansionSuffixes(string? query)
        => DetectRetrievalExpansionLanguage(query) switch
        {
            "en" => new[] { "options", "examples", "suggestions", "ideas" },
            "es" => new[] { "opciones", "ejemplos", "sugerencias", "ideas" },
            "pt" => new[] { "opcoes", "exemplos", "sugestoes", "ideias" },
            "de" => new[] { "optionen", "beispiele", "vorschlaege", "ideen" },
            "it" => new[] { "opzioni", "esempi", "suggerimenti", "idee" },
            _ => new[] { "options", "exemples", "suggestions", "idees" }
        };

    private static IReadOnlyList<string> BuildCandidateExpansionSuffixes(string? query)
        => DetectRetrievalExpansionLanguage(query) switch
        {
            "en" => new[] { "options", "examples", "candidates" },
            "es" => new[] { "opciones", "ejemplos", "candidatos" },
            "pt" => new[] { "opcoes", "exemplos", "candidatos" },
            "de" => new[] { "optionen", "beispiele", "kandidaten" },
            "it" => new[] { "opzioni", "esempi", "candidati" },
            _ => new[] { "options", "exemples", "candidats" }
        };

    private static IReadOnlyList<string> BuildPlanningExplorationSupportTerms(string? query)
        => DetectRetrievalExpansionLanguage(query) switch
        {
            "en" => new[] { "options", "examples", "ideas", "candidates", "steps" },
            "es" => new[] { "opciones", "ejemplos", "ideas", "candidatos", "pasos" },
            "pt" => new[] { "opcoes", "exemplos", "ideias", "candidatos", "passos" },
            "de" => new[] { "optionen", "beispiele", "ideen", "kandidaten", "schritte" },
            "it" => new[] { "opzioni", "esempi", "idee", "candidati", "passi" },
            _ => new[] { "options", "exemples", "idees", "candidats", "etapes" }
        };

    private static IEnumerable<string> BuildPlanningExplorationSupportTermsForRetrieval(string? query)
    {
        var terms = BuildPlanningExplorationSupportTerms(query);
        return ShouldGateStructuredSourceBackedPlanningCoverage(query)
            ? terms.Where(static term => !LooksLikeDecorativeStructuredAxisPlannerQuery(NormalizeLexicalLookup(term)))
            : terms;
    }

    private static IEnumerable<string> ExtractPlanningConstraintRetrievalTerms(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            yield break;

        var patterns = new[]
        {
            @"\brapides?\b",
            @"\bquick\b",
            @"\bsimple[sz]?\b",
            @"\blegers?\b",
            @"\blight\b",
            @"\bvarie(?:e|es|s)?\b",
            @"\bvaried\b",
            @"\bmoins\s+de\s+\d{1,3}\s+minutes?\b",
            @"\bunder\s+\d{1,3}\s+minutes?\b",
            @"\ben\s+\d{1,3}\s+minutes?\b",
            @"\bin\s+\d{1,3}\s+minutes?\b"
        };

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(normalizedQuery, pattern, RegexOptions.CultureInvariant))
            {
                var value = CollapseWhitespace(match.Value);
                if (value.Length >= 4 && emitted.Add(value))
                    yield return value;
            }
        }
    }

    private static string DetectRetrievalExpansionLanguage(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return "fr";

        var scores = new (string Language, int Score)[]
        {
            ("fr", CountRetrievalLanguageSignals(normalized, @"\b(?:aide|aider|peux|pourrais|semaine|hebdomadaire|lundi|mardi|mercredi|jeudi|vendredi|quoi|veux|voudrais|propose|conseille|plan|planning|liste|options?)\b")),
            ("en", CountRetrievalLanguageSignals(normalized, @"\b(?:help|make|week|weekly|monday|tuesday|wednesday|thursday|friday|what|which|want|would|suggest|recommend|plan|schedule|list|options?)\b")),
            ("es", CountRetrievalLanguageSignals(normalized, @"\b(?:ayuda|ayudame|hacer|semana|lunes|martes|miercoles|jueves|viernes|quiero|puedes|podrias|sugiere|recomienda|plan|lista|opciones)\b")),
            ("pt", CountRetrievalLanguageSignals(normalized, @"\b(?:ajuda|ajudar|fazer|semana|segunda|terca|quarta|quinta|sexta|quero|podes|poderias|sugere|recomenda|controlo|plano|lista|opcoes)\b")),
            ("de", CountRetrievalLanguageSignals(normalized, @"\b(?:hilf|helfen|woche|wochenplan|montag|dienstag|mittwoch|donnerstag|freitag|erstellen|welche|was|mochte|vorschlag|empfiehl|plan|liste|optionen)\b")),
            ("it", CountRetrievalLanguageSignals(normalized, @"\b(?:aiuta|aiutami|fare|settimana|lunedi|martedi|mercoledi|giovedi|venerdi|voglio|puoi|potresti|suggerisci|consiglia|piano|lista|opzioni)\b"))
        };

        var best = scores
            .OrderByDescending(static item => item.Score)
            .First();
        if (best.Score > 0)
            return best.Language;

        return LocalizedStrings.DetectLanguage(query, "fr");
    }

    private static int CountRetrievalLanguageSignals(string normalizedQuery, string pattern)
        => Regex.Matches(normalizedQuery, pattern, RegexOptions.CultureInvariant).Count;

    private static int ResolveSourceBackedActionRetrievalQueryLimit(string? query)
    {
        if (LooksLikeSourceBackedVerificationChecklistRequest(query)
            || LooksLikeCorpusClaimVerificationRequest(query))
        {
            return 5;
        }

        if (LooksLikeSourceBackedPairingRecommendationRequest(query))
            return 12;

        if (LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query))
        {
            return 10;
        }

        if (LooksLikeGenericCollectionOrListRequest(query))
            return 14;

        return 8;
    }

    private static string[] BuildShortTechnicalEvidenceRetrievalQueries(string query)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = CollapseWhitespace(query);

        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(normalized))
            AddDistinctQuery(queries, normalized);

        if (TryExtractDelimitedUserDemandTopic(query, out _))
        {
            var raw = CollapseWhitespace(query);
            if (!string.IsNullOrWhiteSpace(raw))
                AddDistinctQuery(queries, raw);
        }

        foreach (var variant in BuildTypoTolerantQueryVariants(normalized))
            AddDistinctQuery(queries, variant);
        foreach (var variant in BuildGenericRetrievalSemanticQueries(normalized))
            AddDistinctQuery(queries, variant);

        var terms = ExtractQuerySignalTerms(NormalizeLexicalLookup(normalized))
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .SelectMany(BuildRetrievalTermVariants)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        if (terms.Length > 0)
            AddDistinctQuery(queries, string.Join(' ', terms));

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    private static int ResolveComparativeRetrievalQueryLimit(string? query)
        => CountExplicitDocumentFileReferences(query ?? string.Empty) > 1 ? 3 : 8;

    private static IEnumerable<string> BuildRetrievalTermVariants(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        yield return normalized;

        foreach (var variant in BuildGenericRetrievalSemanticVariants(normalized))
            yield return variant;

        if (normalized.Length > 5 && normalized.EndsWith("es", StringComparison.Ordinal))
            yield return normalized[..^2];
        if (normalized.Length > 4 && normalized.EndsWith("s", StringComparison.Ordinal))
            yield return normalized[..^1];
    }
}
