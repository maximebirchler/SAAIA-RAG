using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static readonly HashSet<string> SourceBackedActionRetrievalNoiseTerms = new(StringComparer.Ordinal)
    {
        "adaptation", "adaptations", "adapte", "adapter", "adaptee", "adaptees", "bien", "cela",
        "cette", "comment", "como", "dans", "document", "documents", "extrait", "extraits", "faire", "how", "peux",
        "pdf", "source", "sources", "vient", "viennent", "what", "which", "source", "sources",
        "document", "documents", "adapt", "adapted", "derived", "please", "from", "retrouve", "retrouver", "retrouves",
        "choisir", "choix", "choose", "select", "selection", "option", "options", "recommend", "recommends", "recommendation",
        "recommande", "recommander", "conseille", "conseiller", "suggest", "suggestion", "suggestions",
        "orienter", "orientation", "utilisateur", "user", "users", "demande",
        "demandes", "demander", "asked", "asks", "question", "questions", "client", "customer", "customers",
        "prepare", "preparer", "repond", "reponds", "repondez", "reponse", "answer", "answers",
        "respond", "responds", "reply", "replies", "backed", "grounded", "sourced", "sourcee", "sourcees",
        "explique", "expliquer", "expliquez", "explained", "explain", "explains", "explica", "explicar",
        "erklaere", "erklaren", "erklaert", "spiega", "spiegare", "partir", "part", "available",
        "disponible", "disponibles", "utile", "utiles", "useful", "plusieurs", "multiple", "multiples",
        "several", "many", "organiser", "organize", "organise"
    };

    private static IEnumerable<string> BuildSoftChoiceOptionKindRetrievalQueries(string? query)
    {
        if (!LooksLikeSoftChoiceRecommendationRequest(query)
            && !LooksLikeSourceBackedOptionRequest(query))
            yield break;

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in ExtractSoftChoiceRequestedOptionKindTerms(query)
                     .Concat(ExtractGenericSourceBackedOptionKindTerms(query))
                     .SelectMany(BuildRetrievalTermVariants)
                     .Where(static term => term.Length >= 4)
                     .Distinct(StringComparer.Ordinal)
                     .Take(8))
        {
            if (emitted.Add(kind))
                yield return kind;
        }

        if (!LooksLikeSoftChoiceRecommendationRequest(query))
            yield break;

        var raw = CollapseWhitespace(query ?? string.Empty);
        foreach (Match match in Regex.Matches(
                     raw,
                     @"(?i)\b(?:quel|quelle|quels|quelles|which|what|cual|cu[aÃ¡]l|qual|welche|welcher|welches|quale)\s+(?<kind>[\p{L}'\u2019-]{3,30})(?:\s+(?<qualifier>[\p{L}'\u2019-]{3,30}))?",
                     RegexOptions.CultureInvariant))
        {
            var kind = CollapseWhitespace(match.Groups["kind"].Value);
            if (string.IsNullOrWhiteSpace(kind) || IsSourceBackedActionRetrievalNoiseTerm(kind))
                continue;

            var qualifier = CollapseWhitespace(match.Groups["qualifier"].Value);
            if (!string.IsNullOrWhiteSpace(qualifier)
                && !IsSourceBackedActionRetrievalNoiseTerm(qualifier)
                && !Regex.IsMatch(NormalizeLexicalLookup(qualifier), @"\b(?:choisir|choix|choose|select|recommend|suggest|propose)\b", RegexOptions.CultureInvariant))
            {
                var qualifiedKind = $"{kind} {qualifier}";
                if (emitted.Add(qualifiedKind))
                    yield return qualifiedKind;
            }

            if (emitted.Add(kind))
                yield return kind;
        }
    }

    private static IEnumerable<string> BuildTypoTolerantQueryVariants(string? query)
    {
        var normalized = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        var variants = new List<string>();
        void Add(string value)
        {
            value = CollapseWhitespace(value);
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))
                return;
            if (!variants.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
                variants.Add(value);
        }

        Add(Regex.Replace(normalized, @"(?i)ngn", "gn", RegexOptions.CultureInvariant));
        Add(Regex.Replace(normalized, @"(?i)\b([\p{L}]{3,})tt([\p{L}]{2,})\b", "$1t$2", RegexOptions.CultureInvariant));
        Add(Regex.Replace(normalized, @"(?i)ze\b", "se", RegexOptions.CultureInvariant));

        foreach (var variant in variants.Take(3))
            yield return variant;
    }

    private static bool IsSourceBackedActionRetrievalNoiseTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return SourceBackedActionRetrievalNoiseTerms.Contains(normalized);
    }

    private static bool LooksLikeComparativeDocumentaryRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasComparativeOperator = Regex.IsMatch(
                normalized,
                @"\b(?:compare|comparer|comparaison|comparatif|comparative|difference|differences|versus|compara|comparar|comparacion|comparacao|vergleiche|vergleichen|vergleich|confronta|confrontare|confronto|paragona|paragonare)\b|\b(?:met|mets|mettre|mise|mises|mettons|mettez)\s+en\s+parallele\b|\b(?:put|place|set)\b.{0,20}\bside\s+by\s+side\b|\bside\s+by\s+side\b",
                RegexOptions.CultureInvariant);
        var hasRankingOperator = Regex.IsMatch(
                normalized,
                @"\b(?:quel|quelle|quels|quelles|which|what|cual|cuÃ¡l|qual|welche|welcher|welches|quale)\b.{0,120}\b(?:plus|moins|meilleur|meilleure|meilleurs|meilleures|pire|pires|most|least|best|worst|mas|mais|menos|mejor|melhor|beste|bester|bestes|peggiore|migliore)\b|\b(?:le|la|les|the|el|los|las|o|a|os|as|der|die|das|il|lo|gli)\s+(?:plus|moins|meilleur|meilleure|meilleurs|meilleures|pire|pires|most|least|best|worst|mas|mais|menos|mejor|melhor|beste|bester|bestes|peggiore|migliore)\b",
                RegexOptions.CultureInvariant);

        if (!hasComparativeOperator && !hasRankingOperator)
            return false;

        return BuildComparativeRetrievalQueries(query ?? string.Empty).Length > 1
            || ExtractQuerySignalTerms(normalized).Any();
    }

    private static string[] BuildComparativeRetrievalQueries(string query)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = CollapseWhitespace(query);

        var raw = CollapseWhitespace(query);
        var queries = new List<string>();
        var explicitFileReferences = ExtractExplicitDocumentFileReferenceQueries(raw).Take(4).ToArray();
        foreach (var fileReference in explicitFileReferences)
            AddDistinctQuery(queries, fileReference);

        var focus = TryBuildComparativeFocusQuery(normalized);
        AddDistinctQuery(queries, focus);
        if (explicitFileReferences.Length <= 1)
        {
            foreach (var fileReference in explicitFileReferences)
                AddDistinctQuery(queries, QuoteLookupTitle(fileReference));
        }
        AddDistinctQuery(queries, normalized);
        if (!string.IsNullOrWhiteSpace(raw) && !string.Equals(raw, normalized, StringComparison.OrdinalIgnoreCase))
            AddDistinctQuery(queries, raw);

        if (!string.IsNullOrWhiteSpace(focus))
        {
            AddDistinctQuery(queries, $"{focus} details");
            AddDistinctQuery(queries, $"{focus} procedure");
        }

        var signalTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(normalized))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .Take(5)
            .ToArray();
        if (signalTerms.Length > 0)
            AddDistinctQuery(queries, string.Join(' ', signalTerms));

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ResolveComparativeRetrievalQueryLimit(query))
            .ToArray();
    }

    private static string NormalizeComparativeSupplementalRetrievalQuery(string? query, string comparativeQuery)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var focus = TryBuildComparativeFocusQuery(comparativeQuery);
        if (string.IsNullOrWhiteSpace(focus))
            return normalized;

        var normalizedQuery = NormalizeLexicalLookup(normalized);
        var focusTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(focus))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .ToArray();
        if (focusTerms.Length == 0 || focusTerms.Any(term => normalizedQuery.Contains(term, StringComparison.Ordinal)))
            return normalized;

        var supplementalTerms = ExtractQuerySignalTerms(normalizedQuery)
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .ToArray();
        if (supplementalTerms.Length == 0)
            return CollapseWhitespace($"{focus} {normalized}");

        return CollapseWhitespace($"{focus} {normalized}");
    }

    private static string? TryBuildComparativeFocusQuery(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var candidate = normalized;
        var afterCompareVerb = Regex.Match(
            candidate,
            @"\b(?:compare|comparer|comparaison|comparatif|comparative|difference|differences|different|differents|differentes|versus|compara|comparar|comparacion|comparacao|vergleiche|vergleichen|vergleich|confronta|confrontare|confronto|paragona|paragonare)\b\s+(?<rest>.+)$",
            RegexOptions.CultureInvariant);
        if (afterCompareVerb.Success)
            candidate = afterCompareVerb.Groups["rest"].Value;

        var explicitTopic = Regex.Match(
            candidate,
            @"\b(?:sujet\s+suivant|theme\s+suivant|following\s+topic|following\s+subject|tema\s+siguiente|tema\s+seguinte|seguinte\s+tema|folgendes\s+thema|argomento\s+seguente)\b\s*:?\s*(?<topic>.+)$",
            RegexOptions.CultureInvariant);
        if (explicitTopic.Success)
            candidate = explicitTopic.Groups["topic"].Value;

        candidate = Regex.Split(
                candidate,
                @"\b(?:et|and|y|e|und)\s+(?:celle|celui|celles|ceux|celui-ci|celle-ci|celui-la|celle-la|the\s+one|that|quella|quello|quellas|quellos|diese|dieser|dieses)\b",
                RegexOptions.CultureInvariant)
            .FirstOrDefault()
            ?? candidate;

        var terms = Regex.Matches(candidate, @"[\p{L}\p{N}]{2,}", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Where(term => term.Length >= 3 || term.Any(char.IsDigit))
            .Where(term => !(term.All(char.IsDigit) && term.Length <= 3))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        if (terms.Length == 0)
            return null;

        return string.Join(' ', terms);
    }

    private static void AddDistinctQuery(List<string> queries, string? query)
    {
        var value = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!queries.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            queries.Add(value);
    }

    private static bool IsComparativeRetrievalNoiseTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "aide", "aider", "avec", "avoir", "cette", "celles", "celle", "celui", "ceux", "comment",
            "dans", "des", "document", "documents", "est", "etre", "faire", "fiche", "fichier", "fichiers",
            "la", "le", "les", "livre", "livres", "manuel", "moins", "page", "pages", "peux", "plus",
            "pour", "quel", "quelle", "quelles", "quels", "quoi", "source", "sources", "sur", "un", "une",
            "the", "that", "this", "those", "these", "book", "books", "document", "documents", "file",
            "files", "manual", "page", "pages", "source", "sources", "about", "with", "from", "is",
            "are", "which", "what", "most", "least", "best", "worst",
            "comparaison", "comparatif", "comparative", "compare", "comparer", "difference", "differences",
            "different", "differents", "differentes", "versus", "entre", "against", "between", "francais",
            "francaise", "francaises", "french", "international", "internationale", "top", "best",
            "meilleur", "meilleure", "meilleurs", "meilleures", "compara", "comparar", "comparacion",
            "comparacao", "libro", "libros", "documento", "documentos", "fonte", "fontes", "quelle",
            "quello", "quella", "confronta", "confrontare", "confronto", "paragona", "paragonare",
            "vergleiche", "vergleichen", "vergleich", "buch", "buecher", "dokument", "dokumente",
            "quelle", "quellen", "seite", "seiten"
        };

        return stopWords.Contains(normalized);
    }

}
