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

    private static string BuildReadableSourceBackedFallbackIfUseful(ToolResults toolResults, string query, string language)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(query);
        var suppressPartialPlanningFallback = ShouldSuppressReadablePartialPlanningFallback(toolResults, query, intentQuery, language);
        if (LooksLikeAnyDocumentaryPlanningRequest(intentQuery))
        {
            if (!suppressPartialPlanningFallback)
            {
                var hits = SelectSourceBackedExtractiveHits(toolResults, intentQuery, maxHits: 8).ToList();
                var planningFallback = BuildReadablePartialPlanningEvidenceAnswer(hits, intentQuery, language);
                if (!string.IsNullOrWhiteSpace(planningFallback))
                    return SuppressBroadenedSearchOfferIfAlreadyConfirmed(planningFallback, query, language);
            }
        }

        if (!suppressPartialPlanningFallback)
        {
            var candidateFallback = BuildReadableSourceBackedCandidateListFallbackAnswer(toolResults, intentQuery, language);
            if (!string.IsNullOrWhiteSpace(candidateFallback))
                return SuppressBroadenedSearchOfferIfAlreadyConfirmed(candidateFallback, query, language);
        }

        if (!LooksLikeAnyDocumentaryPlanningRequest(intentQuery))
        {
            var hits = SelectSourceBackedExtractiveHits(toolResults, intentQuery, maxHits: 8).ToList();
            var planningFallback = BuildReadablePartialPlanningEvidenceAnswer(hits, intentQuery, language);
            if (!string.IsNullOrWhiteSpace(planningFallback))
                return SuppressBroadenedSearchOfferIfAlreadyConfirmed(planningFallback, query, language);
        }

        return string.Empty;
    }

    private static bool ShouldSuppressReadablePartialPlanningFallback(
        ToolResults toolResults,
        string query,
        string intentQuery,
        string language)
    {
        if (!LooksLikeAnyDocumentaryPlanningRequest(intentQuery))
            return false;

        var isConfirmedBroadenedSearch = IsBroadenedSourceSearchConfirmationEnvelope(query);
        var isExpandedSearch = HasExpandedSourceBackedSearchEvidence(toolResults);
        if (!isConfirmedBroadenedSearch && !isExpandedSearch)
            return false;

        var coverage = EvaluateSourceBackedPlanningCoverage(toolResults, intentQuery, language);
        return !coverage.IsAdequate
            && !HasUsefulPartialSourceBackedPlanningCoverage(
                coverage,
                isConfirmedBroadenedSearch,
                isExpandedSearch);
    }

    private static bool ShouldPreferPartialEvidenceFallbackOverOptions(ToolResults toolResults, string query)
    {
        if (!LooksLikeSourceBackedOptionRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query))
        {
            return false;
        }

        var normalized = NormalizeLexicalLookup(query);
        if (!Regex.IsMatch(
                normalized,
                @"\b(?:idee|idees|ideas?|technique|methode|method|approche|approach|organisation|organization|strategie|strategy|planning|parallele|parallel)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var candidates = SelectSourceBackedOptionAnswerCandidates(toolResults, query, minItems: 1).Items.ToList();
        if (candidates.Count == 0)
            return false;

        var titleAnchorTerms = ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 5)
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        if (titleAnchorTerms.Length == 0)
            return false;

        var anyTitleAnchor = candidates.Any(candidate =>
        {
            var title = NormalizeLexicalLookup(
                $"{candidate.Title} {candidate.Hit.SectionTitle} {candidate.Hit.HeadingPath} {string.Join(' ', candidate.Hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>())}");
            return titleAnchorTerms.Any(term => title.Contains(term, StringComparison.Ordinal));
        });
        if (anyTitleAnchor)
            return false;

        return EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Any(hit => ComputeRagHitLexicalRelevance(query, GetRagHitLookupText(hit)) >= 4);
    }

    private static bool ShouldUseFallbackForBroadMethodOptionRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query)
            || LooksLikeWeeklyPlanningRequest(query))
        {
            return false;
        }

        return Regex.IsMatch(
            normalized,
            @"\b(?:idee|idees|ideas?|technique|methode|method|approche|approach|organisation|organization|strategie|strategy|planning|parallele|parallel)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeOverPromotedSourceBackedOptionAnswer(string? answer)
    {
        var normalized = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(normalized, @"\b(?:option|opcion|opcao|opzione)\s+1\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(normalized, @"\b(?:voici|here\s+are|aqui|ecco).{0,40}\b(?:options?|suggestions?|pistes)\b", RegexOptions.CultureInvariant);
    }

    private static bool ShouldReplaceOverPromotedSourceBackedOptionAnswer(
        string? answer,
        ToolResults toolResults,
        string? query)
    {
        if (!LooksLikeOverPromotedSourceBackedOptionAnswer(answer))
            return false;

        if (LooksLikeUsefulPartialSourceBackedAnswer(answer))
            return false;

        var normalizedAnswer = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalizedAnswer))
            return true;

        var anchoredHitCount = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .Take(8)
            .Count(hit => RagHitIdentityAppearsInAnswer(hit, normalizedAnswer));

        return anchoredHitCount == 0;
    }

    private static bool RagHitIdentityAppearsInAnswer(RagHitSummary hit, string normalizedAnswer)
    {
        var identities = new[]
        {
            hit.DocName,
            Path.GetFileName(hit.DocPath),
            Path.GetFileNameWithoutExtension(hit.DocName),
            Path.GetFileNameWithoutExtension(hit.DocPath)
        };

        return identities
            .Select(NormalizeLexicalLookup)
            .Where(static identity => identity.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Any(identity => normalizedAnswer.Contains(identity, StringComparison.Ordinal));
    }

    private static bool LooksLikeUnsupportedBroadOptionComposition(string? query, string? answer)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var normalizedAnswer = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalizedQuery) || string.IsNullOrWhiteSpace(normalizedAnswer))
            return false;

        var broadComposition = Regex.IsMatch(
            normalizedQuery,
            @"\b(?:idee|idees|ideas?|technique|methode|method|approche|approach|organisation|organization|strategie|strategy|planning|parallele|parallel|selection|composition)\b",
            RegexOptions.CultureInvariant);
        if (!broadComposition || LooksLikeSourceBackedPairingRecommendationRequest(query))
            return false;

        return Regex.IsMatch(
            normalizedAnswer,
            @"\b(?:pas\s+trouve\s+de\s+passage\s+qui\s+relie|did\s+not\s+find\s+a\s+passage\s+that\s+explicitly\s+connects|no\s+he\s+encontrado\s+un\s+pasaje|nao\s+encontrei\s+uma\s+passagem|keine\s+stelle\s+gefunden|non\s+ho\s+trovato\s+un\s+passaggio)\b",
            RegexOptions.CultureInvariant);
    }

    private static string TryBuildMissingRequiredEvidenceAnswer(ToolResults toolResults, string query, string language)
    {
        var requiredTerms = ExtractStrictRequiredEvidenceTerms(query);
        if (requiredTerms.Count == 0)
            return string.Empty;

        var usableHits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (usableHits.Count == 0)
            return string.Empty;

        var missingTerms = requiredTerms
            .Where(term => !usableHits.Any(hit => RequiredEvidenceTermMatchesHit(term, hit)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingTerms.Length == 0)
            return string.Empty;

        var termList = string.Join(", ", missingTerms.Select(static term => $"\"{term}\""));
        return SourceBackedLabel(
            language,
            $"Je n'ai pas trouvÃƒÂ© de source directe qui mentionne {termList}. Je prÃƒÂ©fÃƒÂ¨re prÃƒÂ©ciser ou ÃƒÂ©largir la recherche plutÃƒÂ´t que transformer des indices faibles en rÃƒÂ©ponse.",
            $"I did not find a direct source that mentions {termList}. I would rather refine or broaden the search than turn weak clues into an answer.",
            $"No he encontrado una fuente directa que mencione {termList}. Prefiero precisar o ampliar la bÃƒÂºsqueda antes que convertir indicios dÃƒÂ©biles en una respuesta.",
            $"NÃƒÂ£o encontrei uma fonte direta que mencione {termList}. Prefiro precisar ou alargar a pesquisa em vez de transformar indÃƒÂ­cios fracos numa resposta.",
            $"Ich habe keine direkte Quelle gefunden, die {termList} erwÃƒÂ¤hnt. Ich wÃƒÂ¼rde die Suche lieber prÃƒÂ¤zisieren oder erweitern, statt schwache Hinweise in eine Antwort zu verwandeln.",
            $"Non ho trovato una fonte diretta che menzioni {termList}. Preferisco precisare o ampliare la ricerca invece di trasformare indizi deboli in una risposta.");
    }

    private static string TryBuildMissingBroadCompositionAnchorAnswer(ToolResults toolResults, string query, string language)
    {
        if (!LooksLikeSourceBackedOptionRequest(query)
            || LooksLikeWeeklyPlanningRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(query)))
        {
            return string.Empty;
        }

        var normalized = NormalizeLexicalLookup(query);
        if (!Regex.IsMatch(
                normalized,
                @"\b(?:idee|idees|ideas?|technique|methode|method|approche|approach|organisation|organization|strategie|strategy|planning|parallele|parallel|selection|composition)\b",
                RegexOptions.CultureInvariant))
        {
            return string.Empty;
        }

        var anchorTerms = ExtractBroadCompositionAnchorTerms(query);
        if (anchorTerms.Length < 2)
            return string.Empty;

        var usableHits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (usableHits.Count == 0)
            return string.Empty;

        var missingTerms = anchorTerms
            .Where(term => !usableHits.Any(hit => RequiredEvidenceTermMatchesHit(term, hit)))
            .ToArray();
        var specificMissingTerms = missingTerms
            .Where(IsSpecificBroadCompositionAnchorTerm)
            .ToArray();
        if (missingTerms.Length < Math.Min(2, anchorTerms.Length) && specificMissingTerms.Length == 0)
            return string.Empty;

        var displayedTerms = specificMissingTerms.Length > 0 ? specificMissingTerms : missingTerms;
        var termList = string.Join(", ", displayedTerms.Select(static term => $"\"{term}\""));
        var answer = SourceBackedLabel(
            language,
            $"Je n'ai pas encore trouvÃƒÂ© de source claire pour {termList}. Je peux ÃƒÂ©largir la recherche avant de proposer une rÃƒÂ©ponse vraiment exploitable.",
            $"I have not yet found a clear source for {termList}. I can broaden the search before suggesting a truly usable answer.",
            $"TodavÃƒÂ­a no he encontrado una fuente clara para {termList}. Puedo ampliar la bÃƒÂºsqueda antes de proponer una respuesta realmente ÃƒÂºtil.",
            $"Ainda nÃƒÂ£o encontrei uma fonte clara para {termList}. Posso alargar a pesquisa antes de propor uma resposta realmente ÃƒÂºtil.",
            $"Ich habe noch keine klare Quelle fÃƒÂ¼r {termList} gefunden. Ich kann die Suche erweitern, bevor ich eine wirklich brauchbare Antwort vorschlage.",
            $"Non ho ancora trovato una fonte chiara per {termList}. Posso ampliare la ricerca prima di proporre una risposta davvero utilizzabile.");
        return AppendBroadenedSearchOfferIfHelpful(answer, query, language);
    }

    private static bool IsSpecificBroadCompositionAnchorTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (normalized.Length < 6)
            return false;

        return !BroadCompositionGenericAnchorTerms.Contains(normalized);
    }

    private static readonly HashSet<string> BroadCompositionGenericAnchorTerms = new(StringComparer.Ordinal)
    {
        "idee", "idees", "ideas", "technique", "techniques", "methode", "methodes", "method",
        "methods", "approche", "approaches", "organisation", "organization", "strategie",
        "strategy", "planning", "parallele", "parallel", "selection", "composition",
        "execution", "executer", "preparer", "prepare", "preparation", "organiser",
        "organize", "compatible", "compatibles", "sourcee", "sourcees", "source",
        "sources", "documents", "document"
    };

    private static string TryBuildMissingPairingAnchorAnswer(ToolResults toolResults, string query, string language)
    {
        if (!LooksLikeSourceBackedPairingRecommendationRequest(query))
            return string.Empty;

        var targetTerms = ExtractPairingTargetAnchorTerms(query);
        if (targetTerms.Count == 0)
            return string.Empty;

        var usableHits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .ToList();
        if (usableHits.Count == 0)
            return string.Empty;

        var hasTargetAnchor = usableHits.Any(hit => PairingTargetAnchorMatchesHit(targetTerms, hit));
        if (hasTargetAnchor)
            return string.Empty;

        var targetList = string.Join(", ", targetTerms.Select(static term => $"\"{term}\""));
        var optionKindTerms = ExtractPairingRequestedOptionKindTerms(query);
        var optionKindList = optionKindTerms.Count > 0
            ? string.Join(", ", optionKindTerms.Select(static term => $"\"{term}\""))
            : SourceBackedLabel(language, "la demande", "the request", "la solicitud", "o pedido", "die Anfrage", "la richiesta");

        var answer = SourceBackedLabel(
            language,
            $"Je n'ai pas trouvÃƒÂ© de source qui relie clairement {targetList} ÃƒÂ  {optionKindList}. Je peux ÃƒÂ©largir la recherche avant de proposer une recommandation.",
            $"I did not find a source that clearly connects {targetList} to {optionKindList}. I can broaden the search before suggesting a recommendation.",
            $"No he encontrado una fuente que conecte claramente {targetList} con {optionKindList}. Puedo ampliar la bÃƒÂºsqueda antes de proponer una recomendaciÃƒÂ³n.",
            $"NÃƒÂ£o encontrei uma fonte que ligue claramente {targetList} a {optionKindList}. Posso alargar a pesquisa antes de propor uma recomendaÃƒÂ§ÃƒÂ£o.",
            $"Ich habe keine Quelle gefunden, die {targetList} klar mit {optionKindList} verbindet. Ich kann die Suche erweitern, bevor ich eine Empfehlung vorschlage.",
            $"Non ho trovato una fonte che colleghi chiaramente {targetList} a {optionKindList}. Posso ampliare la ricerca prima di proporre una raccomandazione.");
        return AppendBroadenedSearchOfferIfHelpful(answer, query, language);
    }

    private static IReadOnlyList<string> ExtractPairingTargetAnchorTerms(string? query)
    {
        if (!LooksLikeSourceBackedPairingRecommendationRequest(query))
            return Array.Empty<string>();

        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var optionKindTerms = ExtractPairingRequestedOptionKindTerms(query);
        return ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 4)
            .Where(term => !optionKindTerms.Contains(term, StringComparer.Ordinal))
            .Where(static term => !SourceBackedPairingAnchorNoiseTerms.Contains(term))
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
    }

    private static bool PairingTargetAnchorMatchesHit(IReadOnlyList<string> targetTerms, RagHitSummary hit)
        => targetTerms.Count > 0 && targetTerms.All(term => RequiredEvidenceTermMatchesHit(term, hit));

    private static bool LooksLikeMissingPairingAnchorAnswer(string? answer)
    {
        var normalized = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:pas\s+trouve\s+de\s+passage\s+qui\s+relie|did\s+not\s+find\s+a\s+passage\s+that\s+explicitly\s+connects|no\s+he\s+encontrado\s+un\s+pasaje\s+que\s+conecte|nao\s+encontrei\s+uma\s+passagem\s+que\s+ligue|keine\s+stelle\s+gefunden.*verbindet|non\s+ho\s+trovato\s+un\s+passaggio\s+che\s+colleghi)\b",
            RegexOptions.CultureInvariant);
    }

    private static string[] ExtractBroadCompositionAnchorTerms(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return ExtractQuerySignalTerms(normalized)
            .Where(static term => term.Length >= 5)
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractStrictRequiredEvidenceTerms(string? query)
    {
        var text = CollapseWhitespace(query ?? string.Empty);
        var normalized = NormalizeLexicalLookup(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var strictCue = Regex.IsMatch(
            normalized,
            @"\b(?:uniquement|only|solo|apenas|nur|obligatoire|required|mandatory|must|explicitement|explicitly|mentionne|mentionnes|mentions?|parle|parlent|utilise|utilisent|emploie|emploient|uses?|using|contains?|contient|a\s+partir\s+des?\s+(?:pdf|documents?|sources?)|from\s+(?:the\s+)?(?:pdf|documents?|sources?))\b",
            RegexOptions.CultureInvariant);
        if (!strictCue)
            return Array.Empty<string>();

        var terms = new List<string>();
        terms.AddRange(ExtractNamedEntityLikeQueryTerms(text));

        foreach (Match match in Regex.Matches(
            text,
            @"[\u00ab""'](?<term>[^\u00bb""']{3,80})[\u00bb""']",
            RegexOptions.CultureInvariant))
        {
            AddStrictRequiredEvidenceTerm(terms, match.Groups["term"].Value);
        }

        foreach (Match match in Regex.Matches(
            text,
            @"(?i)\b(?:parle(?:nt)?|mentionne(?:nt|s)?|mentions?|contient|contains?|about|sur|sobre|ueber|uber|su)\s+(?:du|de\s+la|de\s+l['\u2019]|des|d['\u2019]|the|le|la|les|l['\u2019])?\s*(?<term>[\p{L}\p{N}'\u2019 \-]{3,80})",
            RegexOptions.CultureInvariant))
        {
            AddStrictRequiredEvidenceTerm(terms, match.Groups["term"].Value);
        }

        foreach (Match match in Regex.Matches(
            text,
            @"(?i)\b(?:utilise(?:nt)?|emploie(?:nt)?|uses?|using)\s+(?:du|de\s+la|de\s+l['\u2019]|des|d['\u2019]|the|le|la|les|l['\u2019])?\s*(?<term>[\p{L}\p{N}'\u2019 \-]{3,80})",
            RegexOptions.CultureInvariant))
        {
            AddStrictRequiredEvidenceTerm(terms, match.Groups["term"].Value);
        }

        return terms
            .Select(NormalizeStrictRequiredEvidenceTerm)
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
    }

    private static void AddStrictRequiredEvidenceTerm(List<string> terms, string value)
    {
        var term = NormalizeStrictRequiredEvidenceTerm(value);
        if (!string.IsNullOrWhiteSpace(term))
            terms.Add(term);
    }

    private static string NormalizeStrictRequiredEvidenceTerm(string? value)
    {
        var term = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', '.', ',', ';', ':', '?', '!', '"', '\'', '\u00ab', '\u00bb');
        term = Regex.Replace(
            term,
            @"(?i)\s+\b(?:avec|with|con|com|mit|per|pour|for|only|uniquement|a\s+partir|from|dans|in|des?\s+pdf|documents?|sources?|options?|ideas?|idees?)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        term = Regex.Replace(
            term,
            @"(?i)^(?:de\s+l['\u2019]|d['\u2019]|de\s+la|du|des|de|the|le|la|les|l['\u2019])\s+",
            string.Empty,
            RegexOptions.CultureInvariant);
        return CollapseWhitespace(term).Trim(' ', '.', ',', ';', ':');
    }

    private static bool RequiredEvidenceTermMatchesHit(string term, RagHitSummary hit)
    {
        var normalizedTerm = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalizedTerm))
            return true;

        var haystack = NormalizeLexicalLookup(
            $"{hit.DocName} {hit.DocPath} {hit.SectionTitle} {hit.HeadingPath} {GetRagHitLookupText(hit)} {string.Join(' ', hit.MatchedContentCards?.Select(static card => card.Title) ?? Array.Empty<string>())}");
        if (haystack.Contains(normalizedTerm, StringComparison.Ordinal))
            return true;

        var terms = ExtractQuerySignalTerms(normalizedTerm)
            .Where(static item => item.Length >= 4)
            .ToArray();
        if (terms.Length == 0)
            return false;

        var matchedTerms = terms.Count(item => haystack.Contains(item, StringComparison.Ordinal));
        if (matchedTerms == terms.Length)
            return true;

        return terms.Length >= 4 && matchedTerms >= terms.Length - 1 && matchedTerms >= 3;
    }

}
