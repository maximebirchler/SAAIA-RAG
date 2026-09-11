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
    private static int ResolveSourceBackedEvidenceExplorationTopK(string? query, string passLabel)
    {
        var baseTopK = UsesSourceBackedPlanningCoverage(query)
            ? NormalizeSourceBackedPlanningTopK(null, query ?? string.Empty)
            : Math.Max(12, NormalizeSourceBackedActionTopK(null, query ?? string.Empty));
        return string.Equals(passLabel, "candidate_discovery", StringComparison.OrdinalIgnoreCase)
               || string.Equals(passLabel, "planning_exploration", StringComparison.OrdinalIgnoreCase)
               || string.Equals(passLabel, "slot_balancing_inventory", StringComparison.OrdinalIgnoreCase)
               || string.Equals(passLabel, "candidate_inventory", StringComparison.OrdinalIgnoreCase)
               || string.Equals(passLabel, "anchor_followup", StringComparison.OrdinalIgnoreCase)
               || string.Equals(passLabel, "anchor_followup_doc_scope", StringComparison.OrdinalIgnoreCase)
               || string.Equals(passLabel, "llm_strategy", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(baseTopK, UsesSourceBackedPlanningCoverage(query) ? 40 : 18)
            : baseTopK;
    }

    private static int ResolveSourceBackedDocumentScopedAnchorFollowupLimit(string? query)
    {
        if (UsesSourceBackedPlanningCoverage(query))
        {
            var targetSlots = ResolveSourceBackedPlanningTargetItemCount(query);
            return Math.Clamp(
                (int)Math.Ceiling(targetSlots / 5d),
                3,
                6);
        }

        return ShouldUseBroadSourceBackedDiscoveryQueries(query)
            ? BroadSourceBackedDocumentScopedAnchorFollowupLimit
            : DefaultSourceBackedDocumentScopedAnchorFollowupLimit;
    }

    private static int ResolveSourceBackedAnchorFollowupRoundLimit(string? query)
    {
        if (!UsesSourceBackedPlanningCoverage(query))
            return MaxSourceBackedAnchorFollowupRounds;

        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(query);
        return Math.Clamp(
            (int)Math.Ceiling(targetSlots / 10d) + 1,
            2,
            3);
    }

    private static int ResolveSourceBackedDocumentScopedExplorationMaxPerPage(string? query, string passLabel)
        => string.Equals(passLabel, "anchor_followup_doc_scope", StringComparison.OrdinalIgnoreCase)
           && UsesSourceBackedPlanningCoverage(query)
            ? 4
            : 2;

    private static IReadOnlyList<string> BuildNavigationDiscoveryRetrievalQueries(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || (!UsesSourceBackedPlanningCoverage(query)
                && !ShouldOfferBroadenedSourceSearch(query)
                && !LooksLikeGenericCollectionOrListRequest(query)
                && !LooksLikeBroadSourceBackedCompositionRequest(query)
                && !LooksLikeSourceBackedOptionRequest(query)))
        {
            return Array.Empty<string>();
        }

        var normalized = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(query));
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = NormalizeLexicalLookup(query);

        var primaryNavTerms = DetectRetrievalExpansionLanguage(query) switch
        {
            "en" => new[] { "table of contents", "contents", "index", "overview" },
            "es" => new[] { "indice", "contenido", "tabla de contenido", "resumen" },
            "pt" => new[] { "indice", "conteudo", "sumario", "visao geral" },
            "de" => new[] { "inhaltsverzeichnis", "inhalt", "index", "uebersicht" },
            "it" => new[] { "indice", "contenuto", "sommario", "panoramica" },
            _ => new[] { "sommaire", "table des matieres", "index", "sections principales" }
        };
        var secondaryNavTerms = BuildCrossLanguageNavigationDiscoveryTerms(primaryNavTerms)
            .Take(12)
            .ToArray();

        var signals = ExtractQuerySignalTerms(normalized)
            .Concat(ExtractPlanningRetrievalTerms(normalized))
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .Where(static term => !IsNavigationDiscoveryNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        var queries = new List<string>();
        foreach (var navTerm in primaryNavTerms)
            AddDistinctQuery(queries, navTerm);

        foreach (var navTerm in secondaryNavTerms.Take(8))
            AddDistinctQuery(queries, navTerm);

        foreach (var signal in signals)
        {
            foreach (var navTerm in primaryNavTerms.Take(3))
            {
                AddDistinctQuery(queries, $"{signal} {navTerm}");
                AddDistinctQuery(queries, $"{navTerm} {signal}");
            }
        }

        foreach (var signal in signals.Take(3))
        {
            foreach (var navTerm in secondaryNavTerms.Take(4))
            {
                AddDistinctQuery(queries, $"{signal} {navTerm}");
                AddDistinctQuery(queries, $"{navTerm} {signal}");
            }
        }

        return queries
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
    }

    private static IEnumerable<string> BuildCrossLanguageNavigationDiscoveryTerms(IEnumerable<string> primaryTerms)
    {
        var emitted = primaryTerms
            .Select(NormalizeLexicalLookup)
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .ToHashSet(StringComparer.Ordinal);

        var terms = new[]
        {
            "sommaire",
            "table des matieres",
            "contents",
            "table of contents",
            "index",
            "sections",
            "sections principales",
            "indice",
            "tabla de contenido",
            "conteudo",
            "sumario",
            "inhaltsverzeichnis",
            "uebersicht",
            "sommario",
            "panoramica"
        };

        foreach (var term in terms)
        {
            var key = NormalizeLexicalLookup(term);
            if (!string.IsNullOrWhiteSpace(key) && emitted.Add(key))
                yield return term;
        }
    }

    private static IReadOnlyList<string> BuildBroadSourceBackedDiscoveryRetrievalQueries(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || !ShouldUseBroadSourceBackedDiscoveryQueries(query))
            return Array.Empty<string>();

        var planningCoverage = UsesSourceBackedPlanningCoverage(query);
        var normalized = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(query));
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = NormalizeLexicalLookup(query);

        var seeds = new List<string>();
        if (TryExtractGenericCollectionTarget(query, out var collectionTarget)
            || TryExtractGenericCollectionTarget(normalized, out collectionTarget))
        {
            AddDistinctQuery(seeds, collectionTarget);
        }

        foreach (var optionKindQuery in BuildSoftChoiceOptionKindRetrievalQueries(query).Take(4))
            AddDistinctQuery(seeds, optionKindQuery);

        foreach (var term in ExtractPairingRequestedOptionKindTerms(query)
                     .Concat(ExtractPairingTargetAnchorTerms(query))
                     .Concat(ExtractPlanningSlotRetrievalTerms(query))
                     .Concat(ExtractPlanningConstraintRetrievalTerms(normalized))
                     .Concat(ExtractQuerySignalTerms(normalized))
                     .SelectMany(BuildRetrievalTermVariants)
                     .Where(static term => term.Length >= 4)
                     .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
                     .Where(static term => !IsGenericPlanningCoverageTerm(term))
                     .Where(static term => !IsNavigationDiscoveryNoiseTerm(term))
                     .Distinct(StringComparer.Ordinal)
                     .Take(8))
        {
            AddDistinctQuery(seeds, term);
        }

        var structureTerms = BuildBroadDiscoveryStructureTerms(query).ToArray();
        var candidateTerms = BuildCandidateExpansionSuffixes(query)
            .Concat(BuildPlanningExpansionSuffixes(query))
            .Concat(BuildPlanningExplorationSupportTerms(query))
            .Concat(structureTerms)
            .Where(term => !planningCoverage
                           || !LooksLikeDecorativeStructuredAxisPlannerQuery(NormalizeLexicalLookup(term)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        var queries = new List<string>();
        foreach (var seed in seeds
                     .Select(CollapseWhitespace)
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(8))
        {
            AddDistinctQuery(queries, seed);
            foreach (var candidateTerm in candidateTerms.Take(10))
                AddDistinctQuery(queries, $"{seed} {candidateTerm}");
        }

        foreach (var structureTerm in structureTerms.Take(6))
            AddDistinctQuery(queries, structureTerm);

        return queries
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
    }

    private static bool ShouldUseBroadSourceBackedDiscoveryQueries(string? query)
        => LooksLikeGenericCollectionOrListRequest(query)
           || LooksLikeAnyDocumentaryPlanningRequest(query)
           || LooksLikeBroadSourceBackedCompositionRequest(query)
           || LooksLikeMultipleCandidateSynthesisRequest(query)
           || LooksLikeSoftChoiceRecommendationRequest(query)
           || LooksLikeSourceBackedPairingRecommendationRequest(query)
           || LooksLikeSourceBackedOptionRequest(query)
           || IsBroadenedSourceSearchConfirmationEnvelope(query);

    private static IReadOnlyList<string> BuildBroadDiscoveryStructureTerms(string? query)
    {
        var primary = DetectRetrievalExpansionLanguage(query) switch
        {
            "en" => new[] { "title", "titles", "sections", "summary", "topics", "keywords", "table of contents", "index" },
            "es" => new[] { "titulo", "titulos", "secciones", "resumen", "temas", "palabras clave", "indice" },
            "pt" => new[] { "titulo", "titulos", "secoes", "resumo", "temas", "palavras chave", "indice", "sumario" },
            "de" => new[] { "titel", "abschnitte", "zusammenfassung", "themen", "schluesselwoerter", "inhaltsverzeichnis", "index" },
            "it" => new[] { "titolo", "titoli", "sezioni", "riassunto", "argomenti", "parole chiave", "indice", "sommario" },
            _ => new[] { "titre", "titres", "sections", "resume", "sujets", "mots cles", "sommaire", "index" }
        };
        return primary
            .Concat(new[]
            {
                "titre",
                "title",
                "titulo",
                "titolo",
                "sections",
                "summary",
                "resume",
                "resumen",
                "sumario",
                "sommaire",
                "table of contents",
                "indice",
                "inhaltsverzeichnis",
                "sommario"
            })
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsNavigationDiscoveryNoiseTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        return normalized is "cherche" or "chercher" or "trouve" or "trouver" or "avoir" or "documents"
            or "document" or "available" or "disponibles" or "disponible" or "search" or "find" or "want"
            or "need" or "besoin" or "propose" or "proposer";
    }
}
