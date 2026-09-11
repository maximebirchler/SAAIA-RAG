using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string[] BuildPlanningRetrievalQueries(string query)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = query;

        var raw = CollapseWhitespace(query);
        var queries = new List<string>();
        var requestedTitle = TryExtractRequestedItemTitle(query);
        var normalizedLookup = NormalizeLooseLookup(normalized);
        var signalTerms = ExtractPlanningRetrievalTerms(normalizedLookup)
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .Where(static term => !IsInitialSourceBackedPlanningProbeModifierToken(term))
            .Where(static term => !IsNavigationDiscoveryNoiseTerm(term))
            .Take(5)
            .ToArray();

        var language = DetectRetrievalExpansionLanguage(query);
        var hasExplicitStructuredPlanningAxes = DetectRequestedDayAxisLabels(query, language).Count > 0
            || DetectRequestedPlanningSlotAxisLabels(query, language).Count > 0;
        var addedStructuredPlanningQueries = false;
        if (ShouldGateStructuredSourceBackedPlanningCoverage(query) && hasExplicitStructuredPlanningAxes)
        {
            foreach (var retrievalQuery in BuildStructuredPlanningCandidateDiscoveryRetrievalQueries(query).Take(10))
                AddDistinctQuery(queries, retrievalQuery);
            addedStructuredPlanningQueries = queries.Count > 0;
        }

        if (!addedStructuredPlanningQueries)
        {
            AddDistinctQuery(queries, normalized);
            if (!string.IsNullOrWhiteSpace(raw)
                && !string.Equals(raw, normalized, StringComparison.OrdinalIgnoreCase)
                && (!string.IsNullOrWhiteSpace(requestedTitle) || signalTerms.Length > 0))
            {
                AddDistinctQuery(queries, raw);
            }
        }

        if (!string.IsNullOrWhiteSpace(requestedTitle))
        {
            AddDistinctQuery(queries, requestedTitle);
            AddDistinctQuery(queries, $"\"{requestedTitle}\"");
        }

        if (signalTerms.Length > 0)
        {
            var signalQuery = string.Join(' ', signalTerms);
            AddDistinctQuery(queries, signalQuery);
            foreach (var suffix in BuildPlanningExpansionSuffixes(query))
                AddDistinctQuery(queries, $"{signalQuery} {suffix}");
        }

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(LooksLikeSourceBackedVerificationChecklistRequest(query) ? 6 : 16)
            .ToArray();
    }

    private static string[] BuildPlanningExplorationRetrievalQueries(string query)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = CollapseWhitespace(query);

        var queries = new List<string>();
        var normalizedLookup = NormalizeLooseLookup(normalized);
        var signalTerms = ExtractPlanningRetrievalTerms(normalizedLookup)
            .Where(static term => term.Length >= 4)
            .ToArray();
        var subjectTerms = signalTerms
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .Take(5)
            .ToArray();
        var slotTerms = ExtractPlanningSlotRetrievalTerms(query)
            .Take(5)
            .ToArray();

        var supportTerms = BuildPlanningExplorationSupportTermsForRetrieval(query)
            .Take(5)
            .ToArray();
        var constraintTerms = ExtractPlanningConstraintRetrievalTerms(normalizedLookup)
            .Take(4)
            .ToArray();
        var language = DetectRetrievalExpansionLanguage(query);

        var initialInventoryTerms = BuildStructuredPlanningInventoryTermsForRetrieval(language, query);

        foreach (var slot in slotTerms.Take(6))
        {
            foreach (var inventory in initialInventoryTerms.Take(1))
            {
                AddDistinctQuery(queries, $"{slot} {inventory}");
                AddDistinctQuery(queries, $"{inventory} {slot}");
            }
        }

        foreach (var subject in subjectTerms.Take(1))
        {
            foreach (var inventory in initialInventoryTerms.Take(1))
            {
                AddDistinctQuery(queries, $"{subject} {inventory}");
                AddDistinctQuery(queries, $"{inventory} {subject}");
            }
        }

        foreach (var retrievalQuery in BuildStructuredPlanningCandidateDiscoveryRetrievalQueries(query))
            AddDistinctQuery(queries, retrievalQuery);

        foreach (var subject in subjectTerms)
        {
            foreach (var support in supportTerms)
                AddDistinctQuery(queries, $"{subject} {support}");

            foreach (var constraint in constraintTerms)
                AddDistinctQuery(queries, $"{subject} {constraint}");
        }

        foreach (var slot in slotTerms.Take(5))
        {
            foreach (var support in supportTerms.Take(2))
                AddDistinctQuery(queries, $"{slot} {support}");
        }

        foreach (var subject in subjectTerms)
        {
            foreach (var slot in slotTerms)
                AddDistinctQuery(queries, $"{subject} {slot}");
        }

        foreach (var subject in subjectTerms.Take(4))
        {
            foreach (var slot in slotTerms.Take(4))
            {
                foreach (var support in supportTerms.Take(2))
                    AddDistinctQuery(queries, $"{subject} {slot} {support}");
            }
        }

        foreach (var slot in slotTerms)
            AddDistinctQuery(queries, slot);

        foreach (var subject in subjectTerms)
            AddDistinctQuery(queries, subject);

        if (subjectTerms.Length > 1)
            AddDistinctQuery(queries, string.Join(' ', subjectTerms));

        foreach (var retrievalQuery in BuildSourceBackedActionRetrievalQueries(query))
            AddDistinctQuery(queries, retrievalQuery);

        if (!UsesSourceBackedPlanningCoverage(query))
        {
            foreach (var retrievalQuery in BuildNavigationDiscoveryRetrievalQueries(query).Take(8))
                AddDistinctQuery(queries, retrievalQuery);
        }

        foreach (var retrievalQuery in BuildPlanningRetrievalQueries(query))
            AddDistinctQuery(queries, retrievalQuery);

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ShouldGateStructuredSourceBackedPlanningCoverage(query) ? 40 : 24)
            .ToArray();
    }

    private static IEnumerable<string> BuildStructuredPlanningCandidateDiscoveryRetrievalQueries(string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            yield break;

        var language = DetectRetrievalExpansionLanguage(query);
        var normalized = NormalizeLexicalLookup(query);
        var dayAxis = DetectRequestedDayAxisLabels(query, language);
        var periodAxis = DetectRequestedPlanningSlotAxisLabels(query, language);
        var wantsSeveralCandidates = dayAxis.Count > 1 || periodAxis.Count > 1 || LooksLikeWeeklyPlanningRequest(query);
        var inventoryTerms = BuildStructuredPlanningInventoryTermsForRetrieval(language, query).ToArray();
        var slotTerms = periodAxis
            .Concat(dayAxis)
            .Concat(ExtractPlanningSlotRetrievalTerms(query))
            .SelectMany(ExpandPlanningSlotRetrievalTermVariants)
            .Select(NormalizeLexicalLookup)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        var subjectTerms = ExtractPlanningRetrievalTerms(normalized)
            .Where(static term => term.Length >= 4 && !IsGenericPlanningCoverageTerm(term))
            .Where(static term => !IsWeakRouterRagQueryToken(term))
            .Where(static term => !IsInitialSourceBackedPlanningProbeModifierToken(term))
            .Where(static term => !IsNavigationDiscoveryNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();

        if (wantsSeveralCandidates)
        {
            var requestedSlotTerms = periodAxis
                .Concat(ExtractPlanningSlotRetrievalTerms(query))
                .SelectMany(ExpandPlanningSlotRetrievalTermVariants)
                .Select(NormalizeLexicalLookup)
                .Where(static term => term.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var slotQueryTerms = requestedSlotTerms
                .Take(8)
                .ToArray();
            foreach (var subject in subjectTerms.Take(1))
            {
                foreach (var inventory in inventoryTerms.Take(1))
                {
                    yield return $"{subject} {inventory}";
                    yield return $"{inventory} {subject}";
                }
            }

            foreach (var subject in subjectTerms.Take(1))
            {
                foreach (var slot in slotQueryTerms.Take(4))
                {
                    yield return $"{subject} {slot}";
                    yield return $"{slot} {subject}";
                }
            }

            foreach (var inventory in inventoryTerms.Take(2))
            {
                foreach (var slot in slotQueryTerms)
                {
                    yield return $"{slot} {inventory}";
                    yield return $"{inventory} {slot}";
                }
            }
        }

        foreach (var subject in subjectTerms.Take(5))
        {
            foreach (var inventory in inventoryTerms.Take(4))
            {
                yield return $"{subject} {inventory}";
                yield return $"{inventory} {subject}";
            }
        }

        foreach (var slot in slotTerms.Take(6))
        {
            foreach (var inventory in inventoryTerms.Take(3))
            {
                yield return $"{slot} {inventory}";
                yield return $"{inventory} {slot}";
            }
        }

        foreach (var term in subjectTerms.Concat(slotTerms).Concat(inventoryTerms))
            yield return term;

        if (wantsSeveralCandidates)
        {
            var planningTerms = language switch
            {
                "en" => new[] { "weekly plan", "varied options" },
                "es" => new[] { "plan semanal", "opciones variadas" },
                "pt" => new[] { "plano semanal", "opcoes variadas" },
                "de" => new[] { "wochenplan", "verschiedene optionen" },
                "it" => new[] { "piano settimanale", "opzioni varie" },
                _ => new[] { "planning semaine", "options variees" }
            };

            foreach (var planningTerm in planningTerms)
            {
                yield return planningTerm;
                foreach (var subject in subjectTerms.Take(3))
                    yield return $"{planningTerm} {subject}";
            }
        }

    }

    private static string[] BuildStructuredPlanningSlotBalancingInventoryRetrievalQueries(string query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return Array.Empty<string>();

        var language = DetectRetrievalExpansionLanguage(query);
        var inventoryTerms = BuildStructuredPlanningInventoryTermsForRetrieval(language, query)
            .Take(2)
            .ToArray();
        if (inventoryTerms.Length == 0)
            return Array.Empty<string>();

        var slots = DetectRequestedPlanningSlotAxisLabels(query, language)
            .Concat(ExtractPlanningSlotRetrievalTerms(query))
            .SelectMany(ExpandPlanningSlotRetrievalTermVariants)
            .Select(NormalizeLexicalLookup)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        if (slots.Length == 0)
            return Array.Empty<string>();

        var queries = new List<string>();
        foreach (var slot in slots)
        {
            foreach (var inventory in inventoryTerms)
            {
                AddDistinctQuery(queries, $"{slot} {inventory}");
                AddDistinctQuery(queries, $"{inventory} {slot}");
            }
        }

        return queries
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
    }

    private static string[] BuildStructuredPlanningCandidateInventoryRetrievalQueries(string query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return Array.Empty<string>();

        var language = DetectRetrievalExpansionLanguage(query);
        var inventoryTerms = BuildStructuredPlanningInventoryTermsForRetrieval(language, query)
            .Take(4)
            .ToArray();
        if (inventoryTerms.Length == 0)
            return Array.Empty<string>();

        var normalized = NormalizeLexicalLookup(query);
        var subjectTerms = ExtractPlanningRetrievalTerms(normalized)
            .Where(static term => term.Length >= 4 && !IsGenericPlanningCoverageTerm(term))
            .Where(static term => !IsWeakRouterRagQueryToken(term))
            .Where(static term => !IsInitialSourceBackedPlanningProbeModifierToken(term))
            .Where(static term => !IsNavigationDiscoveryNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();

        var queries = new List<string>();
        foreach (var inventory in inventoryTerms)
            AddDistinctQuery(queries, inventory);

        foreach (var subject in subjectTerms)
        {
            foreach (var inventory in inventoryTerms.Take(3))
            {
                AddDistinctQuery(queries, $"{subject} {inventory}");
                AddDistinctQuery(queries, $"{inventory} {subject}");
            }
        }

        return queries
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(18)
            .ToArray();
    }

    private static IEnumerable<string> BuildGenericStructuredPlanningInventoryTerms(string language, string? query = null)
    {
        language = NormalizeLanguageCode(language);
        var genericTerms = language switch
        {
            "en" => new[] { "options", "examples", "proposals", "preparations" },
            "es" => new[] { "opciones", "ejemplos", "propuestas", "preparaciones" },
            "pt" => new[] { "opcoes", "exemplos", "propostas", "preparacoes" },
            "de" => new[] { "optionen", "beispiele", "vorschlaege", "vorbereitungen" },
            "it" => new[] { "opzioni", "esempi", "proposte", "preparazioni" },
            _ => new[] { "options", "exemples", "propositions", "preparations" }
        };
        return ShouldGateStructuredSourceBackedPlanningCoverage(query)
            ? genericTerms.Where(static term => !LooksLikeDecorativeStructuredAxisPlannerQuery(NormalizeLexicalLookup(term)))
            : genericTerms;
    }

    private static IEnumerable<string> BuildStructuredPlanningInventoryTermsForRetrieval(string language, string? query = null)
    {
        var genericTerms = BuildGenericStructuredPlanningInventoryTerms(language, query)
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var term in genericTerms)
            yield return term;
    }

    private static IEnumerable<string> BuildConcreteStructuredPlanningInventoryTermsForRetrieval(string language, string? query = null)
    {
        yield break;
    }

    private static IEnumerable<string> BuildDocumentCandidateListDiscoveryQueries(string candidateNoun, string language)
    {
        candidateNoun = CollapseWhitespace(candidateNoun);
        if (string.IsNullOrWhiteSpace(candidateNoun))
            yield break;

        language = NormalizeLanguageCode(language);
        var listTerms = language switch
        {
            "en" => new[] { "index", "list", "contents", "table of contents", "catalog" },
            "es" => new[] { "indice", "lista", "contenido", "tabla de contenido", "catalogo" },
            "pt" => new[] { "indice", "lista", "conteudo", "sumario", "catalogo" },
            "de" => new[] { "index", "liste", "inhalt", "inhaltsverzeichnis", "katalog" },
            "it" => new[] { "indice", "lista", "contenuto", "sommario", "catalogo" },
            _ => new[] { "index", "liste", "sommaire", "table des matieres", "catalogue" }
        };

        foreach (var listTerm in listTerms)
        {
            yield return $"{listTerm} {candidateNoun}";
            yield return $"{candidateNoun} {listTerm}";
        }
    }

    private static IEnumerable<string> ExpandPlanningSlotRetrievalTermVariants(string? term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        yield return normalized;
    }

    private static string SelectPreferredPlanningSlotRetrievalTerm(string? term)
    {
        var variants = ExpandPlanningSlotRetrievalTermVariants(term)
            .Select(NormalizeLexicalLookup)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (variants.Length == 0)
            return string.Empty;

        return variants[0];
    }

    private static IEnumerable<string> ExtractPlanningSlotRetrievalTerms(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var segments = new List<string>();
        var commaRaw = CollapseWhitespace(query ?? string.Empty).Replace('/', ',').Replace(';', ',');
        var commaNormalized = normalized.Replace('/', ',').Replace(';', ',');
        foreach (Match match in Regex.Matches(
                     commaNormalized,
                     @"\b(?:avec|incluant|inclure|inclut|including|include|mettant|mettre|mets|contenant|contient|with)\b\s+(?<items>[^.?!]{0,180})",
                     RegexOptions.CultureInvariant))
        {
            var items = match.Groups["items"].Value;
            if (!string.IsNullOrWhiteSpace(items))
                segments.Add(items);
        }

        if (segments.Count == 0 && commaRaw.Contains(',', StringComparison.Ordinal))
        {
            var rawSegments = commaRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var startIndex = rawSegments.Length > 1 && LooksLikePlanningListLeadIn(rawSegments[0])
                ? 1
                : 0;
            segments.AddRange(rawSegments
                .Skip(startIndex)
                .Select(NormalizeLexicalLookup)
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        }
        else if (segments.Count == 0 && commaNormalized.Contains(',', StringComparison.Ordinal))
        {
            segments.AddRange(commaNormalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var rawSegment in segments)
        {
            var segment = Regex.Replace(
                rawSegment,
                @"\b(?:avec|with|sources?|documents?|disponibles?|available|utiles?|useful)\b.*$",
                " ",
                RegexOptions.CultureInvariant);
            foreach (var rawPart in Regex.Split(
                         segment,
                         @"\s*(?:,|\bet\b|\band\b|\by\b|\be\b|\bou\b|\bor\b)\s*",
                         RegexOptions.CultureInvariant))
            {
                var term = CollapseWhitespace(Regex.Replace(
                    rawPart,
                    @"\b(?:du|de|des|depuis|from|to|au|jusqu)\b.*$",
                    " ",
                    RegexOptions.CultureInvariant));
                term = CollapseWhitespace(Regex.Replace(
                    term,
                    @"^(?:un|une|des|du|de\s+la|le|la|les|l|the|a|an|some)\s+",
                    string.Empty,
                    RegexOptions.CultureInvariant));
                term = CollapseWhitespace(Regex.Replace(
                    term,
                    @"\b(?:chaque|tous\s+les|toutes\s+les|each|every|cada|ogni|jeder|jede)\s+(?:jour|jours|day|days|dia|dias|tag|tage|giorno|giorni)\b.*$",
                    string.Empty,
                    RegexOptions.CultureInvariant));

                if (IsUsefulGenericPlanningSlotTerm(term) && emitted.Add(term))
                    yield return term;
            }
        }
    }

    private static bool IsUsefulGenericPlanningSlotTerm(string? term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 4 || normalized.Length > 48)
            return false;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:sources?|documents?|disponibles?|available|utiles?|useful|seulement|uniquement|only|just|solely|exclusivement|exclusively|chaque|jour|jours|days?|lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (IsInitialSourceBackedPlanningProbeModifierToken(normalized)
            || IsNavigationDiscoveryNoiseTerm(normalized)
            || IsWeakRouterRagQueryToken(normalized))
        {
            return false;
        }

        return Regex.IsMatch(normalized, @"\p{L}", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikePlanningListLeadIn(string? segment)
    {
        var normalized = NormalizeLexicalLookup(segment);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var tokenCount = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{2,}", RegexOptions.CultureInvariant).Count;
        if (tokenCount < 5)
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:je|j|tu|vous|nous|me|moi|i|you|we|need|besoin|cherche|chercher|want|veux|voudrais|plan|planning|programme|schedule|semaine|week|hebdomadaire|weekly|faire|fasse|preparer|prepare|create|build)\b",
            RegexOptions.CultureInvariant);
    }

    private static IEnumerable<string> ExtractPlanningRetrievalTerms(string normalizedQuery)
    {
        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "aide", "aider", "avec", "avoir", "cette", "comment", "dans", "faire", "facile", "idee",
            "peux", "pour", "propose", "proposes", "quoi", "sais", "vais", "veux", "voudrais",
            "cherche", "chercher", "trouve", "trouver", "trouves", "recherche", "rechercher",
            "demande", "demandes", "souhaite", "souhaites", "souhaiter",
            "besoin", "besoins", "asse", "asses", "fasse", "fasses", "mettre", "mettant",
            "utile", "utiles", "source", "sources", "semaine", "hebdo", "hebdomadaire",
            "about", "find", "help", "make", "prepare", "recommend", "suggest", "what", "with",
            "can", "could", "give", "need", "want", "search", "looking", "look", "asked", "request",
            "include", "includes", "useful", "source", "sources", "week", "weekly",
            "ayuda", "ayudar", "ayudame", "puede", "puedes",
            "podrias", "propone", "recomienda", "ajuda", "ajudar", "pode", "podes", "recomenda",
            "kannst", "konntest", "helfen", "vorschlag", "empfiehl", "aiuta", "aiutami", "puoi",
            "consiglia"
        };

        return Regex.Matches(normalizedQuery, @"[\p{L}\p{N}]{4,}")
            .Select(m => m.Value)
            .Where(term => !stopWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(8);
    }

    private static bool LooksLikeSourceBackedAdaptationRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var mentionsSource = Regex.IsMatch(
            normalized,
            @"\b(?:pdf|document|documents|source|sources|extrait|extraits|base|connaissance|knowledge)\b",
            RegexOptions.CultureInvariant);
        var mentionsAdaptation = Regex.IsMatch(
            normalized,
            @"\b(?:adaptation|adaptations|adapte|adapter|adaptee|adaptees|adapted|adapt|extrapole|extrapoler|derive|derived)\b",
            RegexOptions.CultureInvariant);

        return mentionsSource && mentionsAdaptation;
    }

    private static bool LooksLikeRankingDocumentaryRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var hasRankingOperator = Regex.IsMatch(
            normalized,
            @"\b(?:quel|quelle|quels|quelles|which|what|cual|qual|welche|welcher|welches|quale)\b.{0,120}\b(?:plus|moins|meilleur|meilleure|meilleurs|meilleures|pire|pires|most|least|best|worst|mas|mais|menos|mejor|melhor|beste|bester|bestes|peggiore|migliore)\b|\b(?:le|la|les|the|el|los|las|o|a|os|as|der|die|das|il|lo|gli)\s+(?:plus|moins|meilleur|meilleure|meilleurs|meilleures|pire|pires|most|least|best|worst|mas|mais|menos|mejor|melhor|beste|bester|bestes|peggiore|migliore)\b",
            RegexOptions.CultureInvariant);
        if (!hasRankingOperator)
            return false;

        return ExtractQuerySignalTerms(normalized).Any()
            || Regex.IsMatch(normalized, @"\b(?:technique|technical|tecnico|tecnica|technisch|complique|complexe|complex|difficile|difficult|schwierig)\b", RegexOptions.CultureInvariant);
    }

    private static string[] BuildSourceBackedActionRetrievalQueries(string query)
    {
        var normalized = NormalizeRagQueryForRetrieval(query);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = CollapseWhitespace(query);

        var queries = new List<string>();
        var raw = CollapseWhitespace(query);
        var hasDelimitedUserDemand = TryExtractDelimitedUserDemandTopic(query, out _);
        var collectionTarget = string.Empty;
        var hasCollectionTarget = !LooksLikeSourceBackedPairingRecommendationRequest(query)
            && (TryExtractGenericCollectionTarget(normalized, out collectionTarget)
                || TryExtractGenericCollectionTarget(query, out collectionTarget));
        if (hasCollectionTarget && !string.IsNullOrWhiteSpace(collectionTarget))
        {
            AddDistinctQuery(queries, collectionTarget);
            foreach (var structureTerm in BuildBroadDiscoveryStructureTerms(query).Take(4))
                AddDistinctQuery(queries, $"{collectionTarget} {structureTerm}");
        }
        if (hasDelimitedUserDemand && !string.IsNullOrWhiteSpace(normalized))
            AddDistinctQuery(queries, normalized);
        if (!hasCollectionTarget && !string.IsNullOrWhiteSpace(raw))
            AddDistinctQuery(queries, raw);
        if (!hasDelimitedUserDemand && !hasCollectionTarget)
        {
            if (!string.IsNullOrWhiteSpace(raw) && !string.Equals(raw, normalized, StringComparison.OrdinalIgnoreCase))
                AddDistinctQuery(queries, normalized);
            else if (!string.IsNullOrWhiteSpace(normalized))
                AddDistinctQuery(queries, normalized);
        }

        var retrievalSeed = hasCollectionTarget && !string.IsNullOrWhiteSpace(collectionTarget)
            ? collectionTarget
            : normalized;
        foreach (var variant in BuildTypoTolerantQueryVariants(retrievalSeed))
            AddDistinctQuery(queries, variant);
        foreach (var variant in BuildGenericRetrievalSemanticQueries(retrievalSeed))
            AddDistinctQuery(queries, variant);

        var normalizedLookup = NormalizeLexicalLookup(retrievalSeed);
        var termSeeds = new List<string> { normalizedLookup };
        foreach (var variant in BuildTypoTolerantQueryVariants(normalizedLookup))
            termSeeds.Add(NormalizeLexicalLookup(variant));

        var terms = termSeeds
            .SelectMany(ExtractQuerySignalTerms)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .SelectMany(BuildRetrievalTermVariants)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        var fullQueryTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(query))
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .SelectMany(BuildRetrievalTermVariants)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        if (!hasCollectionTarget && fullQueryTerms.Length > 1)
            AddDistinctQuery(queries, string.Join(' ', fullQueryTerms.Take(6)));

        if (terms.Length > 0)
            AddDistinctQuery(queries, string.Join(' ', terms));

        var softChoiceOptionKindQueries = BuildSoftChoiceOptionKindRetrievalQueries(query).ToArray();
        foreach (var optionKindQuery in softChoiceOptionKindQueries)
            AddDistinctQuery(queries, optionKindQuery);

        if (LooksLikeSourceBackedPairingRecommendationRequest(query))
        {
            var pairingOptionKindTerms = ExtractPairingRequestedOptionKindTerms(query)
                .SelectMany(BuildRetrievalTermVariants)
                .Where(static term => term.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .Take(5)
                .ToArray();
            var pairingTargetTerms = ExtractPairingTargetAnchorTerms(query)
                .SelectMany(BuildRetrievalTermVariants)
                .Where(static term => term.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToArray();

            foreach (var kindTerm in pairingOptionKindTerms)
            {
                AddDistinctQuery(queries, kindTerm);
                foreach (var targetTerm in pairingTargetTerms.Take(2))
                    AddDistinctQuery(queries, $"{kindTerm} {targetTerm}");
            }
        }

        var subjectTerms = terms
            .Where(static term => term.Length >= 5)
            .Where(static term => !Regex.IsMatch(term, @"\b(?:alleger|all[eÃ©]ger|lighten|reduce|reduire|adapter|adaptation)\b", RegexOptions.CultureInvariant))
            .Take(5)
            .ToArray();
        if (subjectTerms.Length > 0)
            AddDistinctQuery(queries, string.Join(' ', subjectTerms));

        var softChoiceKindTerms = ExtractSoftChoiceRequestedOptionKindTerms(query)
            .SelectMany(BuildRetrievalTermVariants)
            .ToHashSet(StringComparer.Ordinal);
        var softChoicePhraseSingletonTermsToSuppress = softChoiceOptionKindQueries
            .Select(NormalizeLexicalLookup)
            .SelectMany(ExtractQuerySignalTerms)
            .SelectMany(BuildRetrievalTermVariants)
            .Where(term => !softChoiceKindTerms.Contains(term))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var term in subjectTerms.Take(6))
        {
            if (softChoicePhraseSingletonTermsToSuppress.Contains(term))
                continue;

            AddDistinctQuery(queries, term);
        }

        foreach (var discoveryQuery in BuildBroadSourceBackedDiscoveryRetrievalQueries(query).Take(12))
            AddDistinctQuery(queries, discoveryQuery);

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ResolveSourceBackedActionRetrievalQueryLimit(query))
            .ToArray();
    }

    private static string[] BuildSourceBackedEvidenceExpansionRetrievalQueries(string query)
    {
        if (UsesSourceBackedPlanningCoverage(query))
        {
            return BuildPlanningExplorationRetrievalQueries(query);
        }

        var queries = new List<string>();
        foreach (var optionKindQuery in BuildSoftChoiceOptionKindRetrievalQueries(query).Take(5))
        {
            AddDistinctQuery(queries, optionKindQuery);
            if (LooksLikeSourceBackedOptionRequest(query))
            {
                foreach (var suffix in BuildCandidateExpansionSuffixes(query).Take(3))
                    AddDistinctQuery(queries, $"{optionKindQuery} {suffix}");
            }
        }

        foreach (var retrievalQuery in BuildSourceBackedActionRetrievalQueries(query))
            AddDistinctQuery(queries, retrievalQuery);
        foreach (var retrievalQuery in BuildBroadSourceBackedDiscoveryRetrievalQueries(query).Take(14))
            AddDistinctQuery(queries, retrievalQuery);

        var normalized = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(query));
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = NormalizeLexicalLookup(query);

        var signalTerms = ExtractQuerySignalTerms(normalized)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .SelectMany(BuildRetrievalTermVariants)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();

        foreach (var term in signalTerms.Take(8))
            AddDistinctQuery(queries, term);

        if (signalTerms.Length > 1)
            AddDistinctQuery(queries, string.Join(' ', signalTerms.Take(6)));

        if (LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeSourceBackedOptionRequest(query))
        {
            foreach (var term in signalTerms.Take(5))
            {
                foreach (var suffix in BuildCandidateExpansionSuffixes(query))
                    AddDistinctQuery(queries, $"{term} {suffix}");
            }
        }

        foreach (var optionKindQuery in BuildSoftChoiceOptionKindRetrievalQueries(query).Take(5))
        {
            AddDistinctQuery(queries, optionKindQuery);
            if (LooksLikeSourceBackedOptionRequest(query))
            {
                foreach (var suffix in BuildCandidateExpansionSuffixes(query).Take(3))
                    AddDistinctQuery(queries, $"{optionKindQuery} {suffix}");
            }
        }

        if (LooksLikeSourceBackedPairingRecommendationRequest(query))
        {
            var kindTerms = ExtractPairingRequestedOptionKindTerms(query)
                .SelectMany(BuildRetrievalTermVariants)
                .Where(static term => term.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .Take(5)
                .ToArray();
            var targetTerms = ExtractPairingTargetAnchorTerms(query)
                .SelectMany(BuildRetrievalTermVariants)
                .Where(static term => term.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToArray();
            foreach (var kindTerm in kindTerms)
            {
                AddDistinctQuery(queries, kindTerm);
                foreach (var targetTerm in targetTerms.Take(3))
                    AddDistinctQuery(queries, $"{kindTerm} {targetTerm}");
            }
        }

        return queries
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
    }
}
