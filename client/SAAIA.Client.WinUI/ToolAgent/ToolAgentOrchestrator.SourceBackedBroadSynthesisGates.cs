using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static IReadOnlyList<RagHitSummary> FilterHitsToDominantTopLevel(IReadOnlyList<RagHitSummary> hits, string? query)
    {
        if (hits.Count < 4 || ShouldPreserveCrossCategoryRagHits(query))
            return hits;

        var window = hits
            .Take(Math.Min(8, hits.Count))
            .Select((hit, index) => new
            {
                Hit = hit,
                Rank = index,
                TopLevel = ExtractTopLevelDocPath(hit.DocPath)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.TopLevel))
            .ToList();
        if (window.Count < 4)
            return hits;

        var groups = window
            .GroupBy(item => item.TopLevel, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                TopLevel = group.Key,
                Count = group.Count(),
                BestRank = group.Min(item => item.Rank)
            })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.BestRank)
            .ToList();
        if (groups.Count < 2 || groups[0].Count < 3)
            return hits;

        var best = groups[0];
        var secondCount = groups.Count > 1 ? groups[1].Count : 0;
        var share = best.Count / (double)window.Count;
        if (share < 0.55 && best.Count < secondCount + 2)
            return hits;

        var filtered = hits
            .Where(hit => string.Equals(ExtractTopLevelDocPath(hit.DocPath), best.TopLevel, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return filtered.Count >= 2 ? filtered : hits;
    }

    private static bool ShouldPreserveCrossCategoryRagHits(string? query)
    {
        if (LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query))
        {
            return true;
        }

        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:compare|comparer|comparaison|comparatif|comparative|versus|different(?:es?)?|plusieurs|toutes?|tous|global|ensemble|categories?|categories|dossiers?|catalogue|across|all|multiple|various|varios|varias|vÃƒÂ¡rios|verschiedene|alle|tutti|tutte)\b",
            RegexOptions.CultureInvariant);
    }

    private static string ExtractTopLevelDocPath(string? docPath)
    {
        var normalized = (docPath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
            return string.Empty;

        var slash = normalized.IndexOf('/');
        return slash > 0 ? normalized[..slash] : string.Empty;
    }

    private static bool LooksLikeNoRagDataAnswer(string? answer)
    {
        var s = (answer ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        if (LooksLikeNoRagDataAnswerInOtherSupportedLanguage(s))
            return true;

        if (Regex.IsMatch(
                s,
                @"(?i)\b(?:pas\s+assez\s+d['\u2019]informations?|informations?\s+n[ÃƒÂ©e]cessaires?.{0,100}(?:pas|non)\s+disponibles?|not\s+enough\s+information|insufficient\s+(?:data|information|sources)|(?:donn[ÃƒÂ©e]es?|sources?|informations?)\s+(?:insuffisantes?|non\s+disponibles?))\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var noDataPattern =
            @"(?i)\b(?:je\s+n['\u2019]ai\s+pas|aucun(?:e)?|pas\s+de|pas\s+assez\s+d['\u2019]informations?|informations?\s+n[ÃƒÂ©e]cessaires?.{0,80}(?:pas|non)\s+disponibles?|(?:donn[ÃƒÂ©e]es?|sources?|informations?)\s+(?:insuffisantes?|non\s+disponibles?)|no\s+(?:specific\s+)?(?:data|document|source|information)|not\s+enough\s+information|insufficient\s+(?:data|information|sources)|nothing\s+specific)\b.{0,160}\b(?:donn[ÃƒÂ©e]es?|documents?|sources?|information|data|disponibles?|available|suffisantes?)\b";

        return Regex.IsMatch(s, noDataPattern, RegexOptions.CultureInvariant);
    }

    private static bool ShouldFallbackFromNoRagDataAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return true;

        return LooksLikeNoRagDataAnswer(answer)
            && !LooksLikeUsefulPartialSourceBackedAnswer(answer);
    }

    private static bool LooksLikeUsefulPartialSourceBackedAnswer(string? answer)
    {
        var s = (answer ?? string.Empty).Trim();
        if (s.Length < 140)
            return false;

        var hasSourceReference = s.Contains("[[open|", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(
                s,
                @"(?i)\b(?:p\.?|page|pagina|p[aÃƒÂ¡]gina|seite|pagina)\s*\d+\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                s,
                @"(?i)\b[\w.-]+\.[a-z0-9]{2,6}\b",
                RegexOptions.CultureInvariant);
        if (!hasSourceReference)
            return false;

        var bodyBeforeSourceList = Regex.Split(
            s,
            @"(?im)^\s*(?:source|sources|references?|r[eÃƒÂ©]f[eÃƒÂ©]rences?|fuente|fuentes|fonte|fontes|quelle|quellen|fonti)\s*:\s*$",
            RegexOptions.CultureInvariant)[0];
        var bulletCount = Regex.Matches(
            bodyBeforeSourceList,
            @"(?m)^\s*(?:[-*\u2022]|\d+[.)])\s+\S",
            RegexOptions.CultureInvariant).Count;
        var sentenceCount = Regex.Matches(
            bodyBeforeSourceList,
            @"[.!?]\s+",
            RegexOptions.CultureInvariant).Count;

        return bulletCount >= 1 || sentenceCount >= 3;
    }

    private static bool LooksLikeNoRagDataAnswerInOtherSupportedLanguage(string answer)
    {
        var normalized = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:no\s+(?:tengo|hay|encontre|encuentro)|informacion\s+insuficiente|nao\s+(?:tenho|ha|encontrei|encontro)|informacao\s+insuficiente|ich\s+habe\s+(?:keine|nicht\s+genug)|keine\s+(?:daten|quellen|informationen)|non\s+(?:ho|trovo|trovo\s+informazioni)|informazioni\s+insufficienti)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeDegenerateLlmOutput(string? answer)
    {
        var raw = answer ?? string.Empty;
        var s = CollapseWhitespace(raw);
        if (s.Length == 0)
            return false;

        if (Regex.IsMatch(
                s,
                @"(?i)\b(?:crit[e\u00e8]res?\s+attendus?|validation\s+points?|expected\s+answer|expected\s+criteria)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(s, @"https?://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
            && Regex.IsMatch(s, @"(?i)\b(?:sources?|documents?|\.pdf|p\.\s*\d+)\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (s.Length < 160)
            return false;

        var repeatedLine = Regex.Split(raw, @"\r?\n")
            .Select(CollapseWhitespace)
            .Where(line => line.Length >= 18)
            .GroupBy(line => line, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() >= 3);
        if (repeatedLine)
            return true;

        var words = Regex.Matches(NormalizeLexicalLookup(s), @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Value)
            .Where(word => word.Length > 0)
            .ToList();

        if (words.Count < 32)
            return false;

        var uniqueRatio = words.Distinct(StringComparer.Ordinal).Count() / (double)words.Count;
        if (words.Count >= 70 && uniqueRatio < 0.34)
            return true;

        for (var n = 3; n <= 8; n++)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i <= words.Count - n; i++)
            {
                var key = string.Join(' ', words.Skip(i).Take(n));
                counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
            }

            if (counts.Values.Any(count => count >= 4 && count * n >= Math.Max(18, words.Count / 4)))
                return true;
        }

        return false;
    }

    private static bool LooksLikeMissingExactItemWithoutSourceLeads(string? answer)
    {
        if (!LooksLikeMissingExactItemAnswer(answer))
            return false;

        var normalized = NormalizeLexicalLookup(answer);
        return !Regex.IsMatch(
            normalized,
            @"\b(?:pistes\s+proches|closest\s+source-backed\s+leads|pistas\s+cercanas|pistas\s+proximas|naheliegende\s+belegte\s+hinweise|indicazioni\s+vicine)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeMissingExactItemAnswer(string? answer)
    {
        var normalized = NormalizeLexicalLookup(answer);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:je\s+n['\s]?ai\s+pas\s+trouve\s+l['\s]?element\s+exact|i\s+did\s+not\s+find\s+the\s+exact\s+requested\s+item|no\s+he\s+encontrado\s+el\s+elemento\s+exacto|nao\s+encontrei\s+o\s+item\s+exato|ich\s+habe\s+den\s+exakt\s+angefragten\s+eintrag|non\s+ho\s+trovato\s+l['\s]?elemento\s+esatto)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool ShouldUseAdvisoryEvidenceGuardForBroadSynthesis(ToolResults toolResults, string? query)
        => (ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, query)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, query))
           && !LooksLikeStrictCertificationOrExactProofRequest(query);

    private static bool ShouldOfferBroadenedSourceSearch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || IsBroadenedSourceSearchConfirmationEnvelope(query)
            || LooksLikeStrictCertificationOrExactProofRequest(query)
            || LooksLikeCorpusClaimVerificationRequest(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query)
            || LooksLikeSourceBackedVerificationChecklistRequest(query)
            || LooksLikeAmbiguousBareDocumentaryFragment(query))
        {
            return false;
        }

        return LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeComparativeDocumentaryRequest(query)
            || LooksLikeBroadSynthesisRequestShape(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeSourceBackedOptionRequest(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query);
    }

    private static bool LooksLikeBroadEmptySourceSearchRequest(string? query)
    {
        var raw = CollapseWhitespace(query ?? string.Empty).ToLowerInvariant();
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized)
            || IsBroadenedSourceSearchConfirmationEnvelope(query)
            || LooksLikeStrictCertificationOrExactProofRequest(query)
            || LooksLikeCorpusClaimVerificationRequest(query))
        {
            return false;
        }

        var mentionsDocumentScope =
            raw.Contains("document", StringComparison.Ordinal)
            || raw.Contains("source", StringComparison.Ordinal)
            || raw.Contains("corpus", StringComparison.Ordinal)
            || raw.Contains("dossier", StringComparison.Ordinal)
            || raw.Contains("catÃƒÂ©gorie", StringComparison.Ordinal)
            || raw.Contains("categorie", StringComparison.Ordinal)
            || raw.Contains("category", StringComparison.Ordinal)
            || raw.Contains("folder", StringComparison.Ordinal)
            || raw.Contains("arquivo", StringComparison.Ordinal)
            || raw.Contains("archivo", StringComparison.Ordinal)
            || raw.Contains("datei", StringComparison.Ordinal);
        var mentionsBroadNeed =
            raw.Contains("option", StringComparison.Ordinal)
            || raw.Contains("idÃƒÂ©e", StringComparison.Ordinal)
            || raw.Contains("idee", StringComparison.Ordinal)
            || raw.Contains("idea", StringComparison.Ordinal)
            || raw.Contains("suggestion", StringComparison.Ordinal)
            || raw.Contains("alternative", StringComparison.Ordinal)
            || raw.Contains("choix", StringComparison.Ordinal)
            || raw.Contains("liste", StringComparison.Ordinal)
            || raw.Contains("list", StringComparison.Ordinal)
            || raw.Contains("plan", StringComparison.Ordinal)
            || raw.Contains("planning", StringComparison.Ordinal)
            || raw.Contains("schedule", StringComparison.Ordinal)
            || raw.Contains("opcion", StringComparison.Ordinal)
            || raw.Contains("opÃƒÂ§ÃƒÂ£o", StringComparison.Ordinal)
            || raw.Contains("opcao", StringComparison.Ordinal)
            || raw.Contains("opzione", StringComparison.Ordinal)
            || raw.Contains("optionen", StringComparison.Ordinal)
            || raw.Contains("idee", StringComparison.Ordinal)
            || raw.Contains("vorschlag", StringComparison.Ordinal)
            || raw.Contains("suggeriment", StringComparison.Ordinal);
        if (mentionsDocumentScope && mentionsBroadNeed)
            return true;

        var asksForExploration = Regex.IsMatch(
            normalized,
            @"\b(?:propose|proposer|suggere|suggerer|donne|donner|trouve|trouver|cherche|chercher|liste|lister|montre|montrer|suggest|recommend|give|find|show|list|propone|proponer|sugiere|sugerir|da|dar|encuentra|encontrar|lista|listar|propoe|propor|sugere|sugerir|encontra|encontrar|mostra|mostrar|liste|finden|zeigen|vorschlagen|empfehlen|proponi|proporre|suggerisci|suggerire|trova|trovare|mostra|mostrare|elenca|elencare)\b",
            RegexOptions.CultureInvariant);
        if (!asksForExploration)
            return false;

        var asksForMultipleOrSynthesis = Regex.IsMatch(
            normalized,
            @"\b(?:plusieurs|options?|idees?|suggestions?|alternatives?|choix|selection|liste|plan|planning|semaine|several|multiple|options?|ideas?|suggestions?|alternatives?|choices?|selection|plan|schedule|varias|varios|opciones?|ideas?|sugerencias|alternativas|seleccion|plano|varias|varios|opcoes?|ideias?|sugestoes|alternativas|selecao|plano|mehrere|optionen|ideen|vorschlaege|vorschlage|alternativen|auswahl|plan|diverse|opzioni?|idee|suggerimenti|alternative|scelta|piano)\b",
            RegexOptions.CultureInvariant);
        if (!asksForMultipleOrSynthesis)
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:documents?|docs?|sources?|corpus|dossier|category|categorie|cat[e\u00e9]gorie|folder|fichiers?|arquivos?|archivos?|dateien)\b",
            RegexOptions.CultureInvariant);
    }

    private static string AppendBroadenedSearchOfferIfHelpful(string answer, string? query, string language)
    {
        if (string.IsNullOrWhiteSpace(answer)
            || (!ShouldOfferBroadenedSourceSearch(query) && !LooksLikeBroadEmptySourceSearchRequest(query)))
        {
            return answer.TrimEnd();
        }

        var offer = DeterministicAgentText.SourceBackedExpandedSearchOffer(language);
        if (answer.Contains(offer, StringComparison.OrdinalIgnoreCase))
            return answer.TrimEnd();

        return answer.TrimEnd() + Environment.NewLine + Environment.NewLine + offer;
    }

    private static bool ShouldUseWriterForBroadSourceBackedSynthesis(ToolResults toolResults, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query)
            || LooksLikeSourceBackedVerificationChecklistRequest(query))
        {
            return false;
        }

        var coverage = EvaluateBroadSourceBackedSynthesisCoverage(toolResults, query);
        if (coverage.UsableHitCount == 0)
            return false;

        if (LooksLikeComparativeDocumentaryRequest(query))
            return coverage.IsAdequate
                || ShouldAllowWriterForPartialBroadSourceBackedSynthesis(coverage, query);

        if (LooksLikeSourceBackedPairingRecommendationRequest(query))
            return coverage.IsAdequate
                || ShouldAllowWriterForPartialBroadSourceBackedSynthesis(coverage, query);

        if (LooksLikeSoftChoiceRecommendationRequest(query))
            return coverage.IsAdequate
                || ShouldAllowWriterForPartialBroadSourceBackedSynthesis(coverage, query);

        if (LooksLikeGenericCollectionOrListRequest(query))
            return coverage.IsAdequate
                || ShouldAllowWriterForPartialBroadSourceBackedSynthesis(coverage, query);

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
        {
            return RequiresStructuredSourceBackedPlanningCoverage(query)
                ? ShouldAllowWriterForPartialSourceBackedPlanning(toolResults, query)
                : coverage.IsAdequate || ShouldAllowWriterForPartialBroadSourceBackedSynthesis(coverage, query);
        }

        if (LooksLikeBroadSynthesisRequestShape(query))
            return coverage.IsAdequate
                || ShouldAllowWriterForPartialBroadSourceBackedSynthesis(coverage, query);

        if (LooksLikeBroadSourceBackedCompositionRequest(query))
            return coverage.IsAdequate
                || ShouldAllowWriterForPartialBroadSourceBackedSynthesis(coverage, query);

        return LooksLikeUserNeedsSynthesizedDecisionOrPlan(query)
            && (coverage.IsAdequate
                || ShouldAllowWriterForPartialBroadSourceBackedSynthesis(coverage, query));
    }

    private static bool ShouldAllowWriterForPartialBroadSourceBackedSynthesis(
        BroadSourceBackedSynthesisCoverage coverage,
        string? query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || coverage.UsableHitCount <= 0
            || LooksLikeStrictCertificationOrExactProofRequest(query)
            || LooksLikeCorpusClaimVerificationRequest(query)
            || LooksLikeSourceBackedCountdownPlanningRequest(query)
            || LooksLikeSourceBackedVerificationChecklistRequest(query))
        {
            return false;
        }

        if (LooksLikeComparativeDocumentaryRequest(query))
            return coverage.RichEvidenceCount >= 1
                && (coverage.UsableHitCount >= 2 || coverage.DistinctSourcePageCount >= 2);

        if (LooksLikeSourceBackedPairingRecommendationRequest(query))
            return coverage.RichEvidenceCount >= 1
                || coverage.DistinctSourcePageCount >= 2
                || coverage.UsableHitCount >= 2
                || coverage.EvidenceRichnessScore >= 6;

        if (LooksLikeSoftChoiceRecommendationRequest(query))
            return coverage.RichEvidenceCount >= 1
                || coverage.DistinctSourcePageCount >= 2
                || coverage.UsableHitCount >= 2;

        if (LooksLikeGenericCollectionOrListRequest(query))
        {
            var minimumHits = Math.Max(2, ResolveMinimumBroadSourceBackedSynthesisHitCount(query));
            return coverage.RichEvidenceCount >= 1
                || (coverage.UsableHitCount >= Math.Min(2, minimumHits)
                    && coverage.DistinctSourcePageCount >= Math.Min(2, minimumHits))
                || coverage.EvidenceRichnessScore >= 8;
        }

        if (LooksLikeMultipleCandidateSynthesisRequest(query))
        {
            return coverage.RichEvidenceCount >= 1
                || (coverage.UsableHitCount >= 2 && coverage.DistinctSourcePageCount >= 2)
                || coverage.EvidenceRichnessScore >= 8;
        }

        var canUsePartialWriter =
            LooksLikeBroadSynthesisRequestShape(query)
            || LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query);
        if (!canUsePartialWriter)
            return false;

        return coverage.RichEvidenceCount >= 1
            || (coverage.DistinctSourcePageCount >= 2 && coverage.EvidenceRichnessScore >= 4)
            || (coverage.UsableHitCount >= 2 && coverage.DistinctSourcePageCount >= 2)
            || coverage.EvidenceRichnessScore >= 12;
    }

    private static BroadSourceBackedSynthesisCoverage EvaluateBroadSourceBackedSynthesisCoverage(
        ToolResults toolResults,
        string? query)
    {
        var usableHits = EnumerateRagHitSummaries(toolResults)
            .Where(static hit => !LooksLikeNavigationOnlyHit(hit))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(hit))
            .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(ComputeSourceBackedEvidenceRichnessScore)
                .ThenByDescending(static hit => hit.Score)
                .First())
            .Take(12)
            .ToList();
        if (usableHits.Count == 0)
            return new BroadSourceBackedSynthesisCoverage(0, 0, 0, 0, 0, false);

        var distinctDocuments = usableHits
            .Select(static hit => string.IsNullOrWhiteSpace(hit.DocPath) ? hit.DocName : hit.DocPath)
            .Where(static source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var distinctSourcePages = usableHits
            .Select(static hit => BuildRagHitVisiblePageMergeKey(hit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var minimumHits = ResolveMinimumBroadSourceBackedSynthesisHitCount(query);
        var hasRequiredComparativeCoverage = HasRequiredComparativeEntityCoverageForBroadSynthesis(usableHits, query);
        var hasEnoughDiversity = minimumHits <= 1
            || distinctSourcePages >= Math.Min(minimumHits, 2)
            || distinctDocuments >= Math.Min(minimumHits, 2);
        var richEvidenceCount = usableHits.Count(HasRichSourceBackedEvidence);
        var evidenceRichnessScore = usableHits.Sum(ComputeSourceBackedEvidenceRichnessScore);
        var allowsSingleRichEvidence =
            !LooksLikeStrictCertificationOrExactProofRequest(query)
            && !LooksLikeComparativeDocumentaryRequest(query)
            && !LooksLikeSoftChoiceRecommendationRequest(query)
            && !LooksLikeSourceBackedPairingRecommendationRequest(query)
            && !LooksLikeGenericCollectionOrListRequest(query)
            && (LooksLikeBroadSourceBackedCompositionRequest(query)
                || LooksLikeMultipleCandidateSynthesisRequest(query)
                || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query));
        var hasRichSingleEvidenceFallback = allowsSingleRichEvidence
            && richEvidenceCount >= 1
            && hasRequiredComparativeCoverage;

        return new BroadSourceBackedSynthesisCoverage(
            usableHits.Count,
            distinctDocuments,
            distinctSourcePages,
            richEvidenceCount,
            evidenceRichnessScore,
            (usableHits.Count >= minimumHits && hasEnoughDiversity && hasRequiredComparativeCoverage)
            || hasRichSingleEvidenceFallback);
    }

    private static bool HasRichSourceBackedEvidence(RagHitSummary hit)
        => ComputeSourceBackedEvidenceRichnessScore(hit) >= 9;

    private static int ComputeSourceBackedEvidenceRichnessScore(RagHitSummary hit)
    {
        var score = ComputeEvidenceShapeScore(hit, GetBestRagEvidenceText(hit), includeBackendHints: true);
        if (BackendSelectionHintsPreferUsableEvidence(hit))
            score += 2;

        if (hit.MatchedContentCards is { Count: > 0 } cards)
        {
            score += Math.Min(4, cards.Count);
            score += Math.Min(6, cards.Count(static card =>
                card.RawEvidence.HasValue
                || card.Evidence is { QuantityFacts.Count: > 0 }
                || card.Evidence?.Facts is { Count: > 0 }) * 2);
        }

        if (!string.IsNullOrWhiteSpace(hit.FullText) && hit.FullText.Length >= 160)
            score += 2;
        if (!string.IsNullOrWhiteSpace(hit.ContextualSnippet) && hit.ContextualSnippet.Length >= 120)
            score += 1;
        if (!string.IsNullOrWhiteSpace(hit.SectionTitle) || !string.IsNullOrWhiteSpace(hit.HeadingPath))
            score += 1;
        if (hit.ContentDensityScore.HasValue && hit.ContentDensityScore.Value >= 0.45)
            score += 1;

        if (BackendSelectionHintsPreferNavigation(hit) || BackendSelectionHintsPreferLowSignal(hit))
            score -= 4;
        if (hit.ManualReviewRecommended || hit.PageManualReviewRecommended || hit.DocumentManualReviewRecommended)
            score -= 1;
        if (hit.ExtractionConfidence.HasValue && hit.ExtractionConfidence.Value < 0.50)
            score -= 2;

        return Math.Clamp(score, -8, 28);
    }

    private static int ResolveMinimumBroadSourceBackedSynthesisHitCount(string? query)
    {
        if (LooksLikeComparativeDocumentaryRequest(query))
            return 2;

        if (RequiresStructuredSourceBackedPlanningCoverage(query))
        {
            var targetSlots = ResolveSourceBackedPlanningTargetItemCount(query);
            var hasStructuredAxes = DetectRequestedDayAxisLabels(query, "en").Count > 0
                && DetectRequestedPlanningSlotAxisLabels(query, "en").Count > 0;
            return ResolveMinimumSourceBackedPlanningCandidateCount(query, targetSlots, hasStructuredAxes);
        }

        if (LooksLikeGenericCollectionOrListRequest(query))
            return 3;

        if (LooksLikeSourceBackedPairingRecommendationRequest(query))
            return 3;

        if (LooksLikeSoftChoiceRecommendationRequest(query))
            return 3;

        if (LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query))
        {
            return 2;
        }

        return 1;
    }

    private static bool RequiresStructuredSourceBackedPlanningCoverage(string? query)
        => LooksLikeWeeklyPlanningRequest(query)
           || DetectRequestedDayAxisLabels(query, "en").Count > 0;

    private static bool ShouldRequireDeterministicStructuredPlanningAnswer(string? query)
        => false;

    private static bool ShouldGateStructuredSourceBackedPlanningCoverage(string? query)
    {
        if (!RequiresStructuredSourceBackedPlanningCoverage(query))
            return false;

        if (ShouldRequireDeterministicStructuredPlanningAnswer(query))
            return true;

        return LooksLikeAnyDocumentaryPlanningRequest(query)
               || LooksLikeGenericCollectionOrListRequest(query)
               || LooksLikeBroadSourceBackedCompositionRequest(query)
               || LooksLikeMultipleCandidateSynthesisRequest(query)
               || LooksLikeSoftChoiceRecommendationRequest(query)
               || LooksLikeSourceBackedOptionRequest(query)
               || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query)
               || LooksLikeSourceBackedActionRequest(query)
               || LooksLikeDocumentaryContentRequest(query);
    }

    private static bool LooksLikeMultipleCandidateSynthesisRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:quel|quelle|which|what|cual|qual|welche|welcher|welches|quale)\s+(?:option|opcion|opcao|opzione)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return Regex.IsMatch(
            normalized,
            @"\b(?:plusieurs|different(?:es)?|vari(?:e|er|ees?)|liste|lister|idees|suggestions|alternatives|candidats|choix|several|multiple|different|varied|list|options|ideas|suggestions|alternatives|candidates|varias|varios|diferentes|lista|opciones|ideas|sugerencias|alternativas|candidatos|opcoes|ideias|sugestoes|alternativas|candidatos|mehrere|verschiedene|liste|optionen|ideen|vorschlaege|vorschlage|alternativen|kandidaten|diverse|differenti|lista|opzioni|idee|suggerimenti|alternative|candidati)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:\d{1,2}|deux|trois|quatre|cinq|six|seven|two|three|four|five|six|dos|tres|cuatro|cinco|duas|dois|tres|quatro|cinco|zwei|drei|vier|funf|fuenf|sei|due|tre|quattro|cinque)\s+(?:options?|idees?|ideas?|suggestions?|alternatives?|candidats?|candidates?|opciones?|sugerencias?|candidatos?|opcoes?|ideias?|sugestoes?|optionen|ideen|vorschlaege|vorschlage|alternativen|kandidaten|opzioni?|idee|suggerimenti|alternative|candidati)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool HasRequiredComparativeEntityCoverageForBroadSynthesis(
        IReadOnlyList<RagHitSummary> hits,
        string? query)
    {
        if (!LooksLikeComparativeDocumentaryRequest(query))
            return true;

        var entityAnchors = ExtractComparativeEntityAnchorTerms(query);
        if (entityAnchors.Length < 2)
            return hits.Count >= 2;

        return CountComparativeEntityCoverage(hits, entityAnchors) >= Math.Min(entityAnchors.Length, 2);
    }

    private sealed record BroadSourceBackedSynthesisCoverage(
        int UsableHitCount,
        int DistinctDocumentCount,
        int DistinctSourcePageCount,
        int RichEvidenceCount,
        int EvidenceRichnessScore,
        bool IsAdequate);

    private static bool LooksLikeStrictCertificationOrExactProofRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:prouve|preuve|demontre|certifie|certifier|garantis|garantie|compatible|compatibilite|obligatoire|required|mandatory|explicitement|exactement|strictement|sans\s+supposer|valide\s+officiel|officially\s+validated|prove|proof|certify|guarantee|explicitly|exactly)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeExactPassageOrCitationRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:cite|citer|citation|quote|quotation|verbatim|mot\s+pour\s+mot|mot\s+a\s+mot|passage\s+exact|extrait\s+exact|copie|copy|recopie|copier)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeUserNeedsSynthesizedDecisionOrPlan(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:aide|aider|aide\s+moi|besoin\s+d\s+aide|je\s+ne\s+sais\s+pas|quoi\s+faire|propose|proposer|proposes|suggest|suggestion|recommend|recommendation|recommande|recommander|conseille|conseiller|organise|organiser|structure|structurer|choisis|choisir|decision|decider|plan|planning|programme|selection|options?|ayuda|ayudar|necesito|no\s+se|que\s+hacer|propone|proponer|sugiere|sugerir|recomienda|recomendar|aconseja|aconsejar|organiza|organizar|elige|elegir|decision|opciones?|ajuda|ajudar|preciso|nao\s+sei|o\s+que\s+fazer|propoe|propor|sugere|sugerir|recomenda|recomendar|aconselha|aconselhar|organiza|organizar|escolhe|escolher|decisao|opcoes?|hilfe|helfen|brauche|weiss\s+nicht|was\s+tun|schlag\s+vor|vorschlag|empfiehl|empfehlen|rate|raten|organisiere|organisieren|struktur|waehle|waehlen|entscheidung|optionen?|aiuto|aiutare|bisogno|non\s+so|cosa\s+fare|proponi|proporre|suggerisci|suggerire|consiglia|consigliare|organizza|organizzare|scegli|scegliere|decisione|opzioni?)\b",
            RegexOptions.CultureInvariant);
    }

}
