using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildSourceBackedRankingAnswer(string language, string query, IReadOnlyList<RagHitSummary> hits)
    {
        language = NormalizeLanguageCode(language);
        var entityAnchors = ExtractComparativeEntityAnchorTerms(query);
        var ranked = RankSourceBackedRankingHits(hits, query)
            .Take(4)
            .ToList();
        if (LooksLikeTechnicalRankingQuery(query))
        {
            var structuredEnoughRanked = ranked
                .Where(static hit => !LooksLikeLowStructureShortProcedureHit(hit))
                .ToList();
            if (structuredEnoughRanked.Count > 0)
                ranked = PreserveComparativeEntityCoverage(ranked, structuredEnoughRanked, entityAnchors);

            var nonVeryShortRanked = ranked
                .Where(static hit => ExtractBestVisibleDurationMinutes(hit) is not int minutes || minutes > 3)
                .ToList();
            if (nonVeryShortRanked.Count > 0)
                ranked = PreserveComparativeEntityCoverage(ranked, nonVeryShortRanked, entityAnchors);
        }
        if (ranked.Count == 0)
            return string.Empty;

        var labels = BuildSourceBackedRankingLabels(language);
        var sb = new StringBuilder();
        sb.AppendLine(labels.Header);

        var winner = ranked[0];
        sb.Append("- ");
        sb.Append(labels.MainCandidate);
        sb.Append(" : ");
        sb.Append(FormatRankingHitReference(language, winner));
        sb.Append(" - ");
        sb.AppendLine(BuildSourceBackedRankingReason(language, winner, query));

        if (ranked.Count > 1)
        {
            sb.AppendLine(labels.OtherCandidates);
            foreach (var hit in ranked.Skip(1).Take(3))
            {
                sb.Append("- ");
                sb.Append(FormatRankingHitReference(language, hit));
                sb.Append(" - ");
                sb.AppendLine(BuildSourceBackedRankingReason(language, hit, query));
            }
        }

        sb.Append(labels.Caution);
        return sb.ToString().TrimEnd();
    }

    private static (string Header, string MainCandidate, string OtherCandidates, string Caution) BuildSourceBackedRankingLabels(string language)
    {
        return NormalizeLanguageCode(language) switch
        {
            "en" => (
                "Based only on the available excerpts, here is the most defensible ranking:",
                "Main candidate",
                "Other source-backed candidates:",
                "This is not an absolute ranking of the whole knowledge base; it only reflects the retrieved excerpts."),
            "es" => (
                "Basandome solo en los extractos disponibles, este es el ranking mas defendible:",
                "Candidato principal",
                "Otros candidatos con fuente:",
                "No es un ranking absoluto de toda la base de conocimiento; refleja solo los extractos recuperados."),
            "pt" => (
                "Com base apenas nos excertos disponiveis, este e o ranking mais defensavel:",
                "Candidato principal",
                "Outros candidatos com fonte:",
                "Nao e um ranking absoluto de toda a base de conhecimento; reflete apenas os excertos recuperados."),
            "de" => (
                "Nur auf Basis der verfuegbaren Auszuege ist dies die belastbarste Rangfolge:",
                "Hauptkandidat",
                "Weitere belegte Kandidaten:",
                "Das ist keine absolute Rangfolge der gesamten Wissensbasis; sie spiegelt nur die gefundenen Auszuege wider."),
            "it" => (
                "Basandomi solo sugli estratti disponibili, questa e la classifica piu difendibile:",
                "Candidato principale",
                "Altri candidati con fonte:",
                "Non e una classifica assoluta di tutta la base di conoscenza; riflette solo gli estratti recuperati."),
            _ => (
                "D'aprÃ¨s les passages disponibles, voici le classement le plus dÃ©fendable :",
                "Candidat principal",
                "Autres candidats sourcÃ©s :",
                "Ce n'est pas un classement absolu de toute la base de connaissance ; il reflÃ¨te seulement les passages retrouvÃ©s.")
        };
    }

    private static IReadOnlyList<RagHitSummary> RankSourceBackedRankingHits(IEnumerable<RagHitSummary> hits, string query)
    {
        var candidateHits = hits.ToList();
        if (candidateHits.Count == 0)
            return Array.Empty<RagHitSummary>();

        var technicalRanking = LooksLikeTechnicalRankingQuery(query);
        if (technicalRanking)
        {
            var nonGenericCandidates = candidateHits
                .Where(static hit => !LooksLikeGenericTechniqueDefinitionHit(hit))
                .ToList();
            if (nonGenericCandidates.Count > 0)
                candidateHits = nonGenericCandidates;
        }

        var entityAnchors = ExtractComparativeEntityAnchorTerms(query);
        if (entityAnchors.Length >= 2)
        {
            var entityMatchedHits = candidateHits
                .Where(hit => GetMatchedComparativeEntityAnchorIndexes(hit, entityAnchors).Length > 0)
                .ToList();
            if (entityMatchedHits.Count > 0)
                candidateHits = entityMatchedHits;
        }

        var allCandidateHits = candidateHits;
        var dominantHits = FilterHitsToDominantTopLevel(candidateHits, query).ToList();
        var dominantTargetHits = FilterRankingHitsToRequestedContentTarget(dominantHits, query).ToList();
        var allTargetHits = FilterRankingHitsToRequestedContentTarget(allCandidateHits, query).ToList();
        if (!TargetFilterHasEnoughStructuredRankingSupport(allCandidateHits, allTargetHits))
        {
            dominantTargetHits = dominantHits;
            allTargetHits = allCandidateHits;
        }

        candidateHits = dominantHits;
        var targetFilteredHits = dominantTargetHits;
        if (allTargetHits.Count < allCandidateHits.Count
            && dominantTargetHits.Count == dominantHits.Count)
        {
            candidateHits = allTargetHits;
            targetFilteredHits = allTargetHits;
        }
        if (technicalRanking)
        {
            var targetHasStructuredEvidence = targetFilteredHits.Any(static hit => !LooksLikeLowStructureShortProcedureHit(hit));
            var allHitsHaveStructuredEvidence = candidateHits.Any(static hit => !LooksLikeLowStructureShortProcedureHit(hit));
            if (targetHasStructuredEvidence || !allHitsHaveStructuredEvidence)
                candidateHits = targetFilteredHits;
        }
        else
        {
            candidateHits = targetFilteredHits;
        }
        if (technicalRanking)
        {
            var singlePageHits = candidateHits
                .Where(static hit => hit.PageEnd <= hit.PageStart)
                .ToList();
            if (singlePageHits.Count >= 2)
                candidateHits = PreserveComparativeEntityCoverage(candidateHits, singlePageHits, entityAnchors);

            var structuredEnoughHits = candidateHits
                .Where(static hit => !LooksLikeLowStructureShortProcedureHit(hit))
                .ToList();
            if (structuredEnoughHits.Count > 0)
                candidateHits = PreserveComparativeEntityCoverage(candidateHits, structuredEnoughHits, entityAnchors);

            var nonGenericTechniqueHits = candidateHits
                .Where(static hit => !LooksLikeGenericTechniqueDefinitionHit(hit))
                .ToList();
            if (nonGenericTechniqueHits.Count > 0)
                candidateHits = PreserveComparativeEntityCoverage(candidateHits, nonGenericTechniqueHits, entityAnchors);

            var completeProcedureHits = candidateHits
                .Where(static hit => ComputeProcedureCompletenessCueScore(hit) >= 7)
                .ToList();
            if (completeProcedureHits.Count > 0)
                candidateHits = PreserveComparativeEntityCoverage(candidateHits, completeProcedureHits, entityAnchors);

            var nonMidProcedureFragmentHits = candidateHits
                .Where(static hit => !LooksLikeMidProcedureFragment(hit))
                .ToList();
            if (nonMidProcedureFragmentHits.Count > 0)
                candidateHits = PreserveComparativeEntityCoverage(candidateHits, nonMidProcedureFragmentHits, entityAnchors);

            var technicallyConstrainedHits = candidateHits
                .Where(static hit => ComputeTechnicalComplexityCueScore(hit) >= 5)
                .ToList();
            if (technicallyConstrainedHits.Count > 0)
                candidateHits = PreserveComparativeEntityCoverage(candidateHits, technicallyConstrainedHits, entityAnchors);

            var nonSimpleHits = candidateHits
                .Where(static hit => !LooksLikeVerySimpleProcedureHit(hit))
                .ToList();
            if (nonSimpleHits.Count >= 2)
                candidateHits = PreserveComparativeEntityCoverage(candidateHits, nonSimpleHits, entityAnchors);

            structuredEnoughHits = candidateHits
                .Where(static hit => !LooksLikeLowStructureShortProcedureHit(hit))
                .ToList();
            if (structuredEnoughHits.Count > 0)
                candidateHits = PreserveComparativeEntityCoverage(candidateHits, structuredEnoughHits, entityAnchors);
        }

        var evidenceQuery = BuildRagEvidenceSelectionQuery(query);
        return candidateHits
            .Where(hit => !LooksLikeNavigationOnlyHit(hit))
            .Select((hit, index) => new
            {
                Hit = hit,
                Index = index,
                Score = ComputeSourceBackedRankingScore(hit, query, evidenceQuery)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .GroupBy(item => $"{item.Hit.DocPath}|{item.Hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(item => item.Hit)
            .ToList();
    }

    private static bool TargetFilterHasEnoughStructuredRankingSupport(
        IReadOnlyList<RagHitSummary> allHits,
        IReadOnlyList<RagHitSummary> targetHits)
    {
        if (targetHits.Count == 0 || targetHits.Count == allHits.Count)
            return true;

        if (targetHits.All(LooksLikeMidProcedureFragment)
            && allHits.Any(hit => !LooksLikeMidProcedureFragment(hit) && HasStrongStructuredRankingEvidence(hit)))
        {
            return false;
        }

        if (targetHits.Any(HasStrongStructuredRankingEvidence))
            return true;

        var targetKeys = targetHits
            .Select(BuildRagHitIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !allHits
            .Where(hit => !targetKeys.Contains(BuildRagHitIdentityKey(hit)))
            .Any(HasStrongStructuredRankingEvidence);
    }

    private static List<RagHitSummary> PreserveComparativeEntityCoverage(
        IReadOnlyList<RagHitSummary> current,
        List<RagHitSummary> candidate,
        IReadOnlyList<string[]> entityAnchors)
    {
        if (candidate.Count == 0 || entityAnchors.Count < 2)
            return candidate;

        var currentCoverage = CountComparativeEntityCoverage(current, entityAnchors);
        var candidateCoverage = CountComparativeEntityCoverage(candidate, entityAnchors);
        var requiredCoverage = Math.Min(currentCoverage, entityAnchors.Count);
        return candidateCoverage >= requiredCoverage ? candidate : current.ToList();
    }

    private static int CountComparativeEntityCoverage(
        IEnumerable<RagHitSummary> hits,
        IReadOnlyList<string[]> entityAnchors)
    {
        if (entityAnchors.Count == 0)
            return 0;

        return hits
            .SelectMany(hit => GetMatchedComparativeEntityAnchorIndexes(hit, entityAnchors))
            .Distinct()
            .Count();
    }

    private static bool HasStrongStructuredRankingEvidence(RagHitSummary hit)
        => ComputeProcedureCompletenessCueScore(hit) >= 7
            || ComputeStructuredProcedureEvidenceCueScore(hit) >= 7
            || ComputeStructuredProcedureVisibleEvidenceCueScore(hit) >= 7;

    private static string BuildRagHitIdentityKey(RagHitSummary hit)
        => $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}";

    private static IReadOnlyList<RagHitSummary> FilterRankingHitsToRequestedContentTarget(IReadOnlyList<RagHitSummary> hits, string query)
    {
        var entityAnchors = ExtractComparativeEntityAnchorTerms(query);
        if (entityAnchors.Length >= 2)
        {
            var entityMatchedHits = hits
                .Where(hit => GetMatchedComparativeEntityAnchorIndexes(hit, entityAnchors).Length > 0)
                .ToList();
            if (entityMatchedHits.Count > 0)
                return entityMatchedHits;
        }

        var normalizedQuery = NormalizeLexicalLookup(query);
        var targetTerms = ExtractQuerySignalTerms(normalizedQuery)
            .Where(static term => term.Length >= 4)
            .Where(static term => !Regex.IsMatch(
                term,
                @"^(?:quel|quelle|quels|quelles|which|what|plus|moins|meilleur|meilleure|best|worst|most|least|technique|technical|complexe|complex|difficile|difficult|classement|ranking|candidat|candidate)$",
                RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (targetTerms.Length == 0)
            return hits;

        var matchingHits = hits
            .Where(hit =>
            {
                var lookup = NormalizeLexicalLookup($"{hit.DocName} {hit.DocPath} {hit.SectionTitle} {hit.HeadingPath} {GetRagHitPrimaryContentText(hit)}");
                return targetTerms.Any(term => lookup.Contains(term, StringComparison.Ordinal));
            })
            .ToList();

        return matchingHits.Count > 0 ? matchingHits : hits;
    }

    private static double ComputeSourceBackedRankingScore(RagHitSummary hit, string query, string evidenceQuery)
    {
        var score = ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitPrimaryEvidenceText(hit)) * 3
            + ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitLookupText(hit))
            + Math.Min(3.0, Math.Max(0.0, hit.Score));

        if (LooksLikeTechnicalRankingQuery(query))
            score += ComputeTechnicalEvidenceCueScore(hit);

        var structuredProcedureScore = 0;
        if (LooksLikeProcedureRankingQuery(query))
        {
            structuredProcedureScore = ComputeStructuredProcedureEvidenceCueScore(hit);
            score += structuredProcedureScore;
        }

        return score;
    }

    private static bool LooksLikeTechnicalRankingQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:technique|technical|tecnico|tecnica|technisch|complexe|complex|complique|difficile|difficult|schwierig|avance|advanced)\b",
            RegexOptions.CultureInvariant);
    }

    private static int ComputeTechnicalEvidenceCueScore(RagHitSummary hit)
    {
        return Math.Clamp(
            ComputeEvidenceShapeScore(hit, GetRagHitLookupText(hit), includeBackendHints: true),
            -4,
            12);
    }

    private static int ComputeTechnicalComplexityCueScore(RagHitSummary hit)
    {
        var rawText = GetRagHitPrimaryEvidenceText(hit);
        var text = NormalizeLexicalLookup(rawText);
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var labelCount = CountLabelValueMarkers(rawText);
        var stepCount = CountProcedureStepMarkers(text);
        var measuredCount = CountMeasuredValueMarkers(text);

        var score = 0;
        if (labelCount > 0 || stepCount >= 3)
            score += Math.Min(5, measuredCount);
        score += Math.Min(4, labelCount * 2);
        score += Math.Min(4, stepCount);
        score += Math.Min(3, CountBulletListMarkers(rawText));

        if (HasMeasurableConstraintEvidence(rawText, text))
            score += 3;
        if (labelCount >= 2 || stepCount >= 4)
            score += 2;
        if (ScoreMatchedContentCardShape(hit.MatchedContentCards) >= 3)
            score += 1;

        return score;
    }

    private static int ComputeProcedureCompletenessCueScore(RagHitSummary hit)
    {
        return Math.Clamp(
            ComputeEvidenceShapeScore(hit, GetRagHitPrimaryContentText(hit), includeBackendHints: true),
            -4,
            14);
    }

    private static int CountProcedureStepMarkers(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        var numeric = Regex.Matches(
            normalizedText,
            @"(?:^|[\r\n.;:]\s*|\s)[1-9]\d{0,2}[\.)]\s+\S",
            RegexOptions.CultureInvariant).Count;
        var bullets = Regex.Matches(
            normalizedText,
            @"[â€¢\-]\s*(?:\p{L}{4,})(?:\s+\p{L}{2,}){2,}",
            RegexOptions.CultureInvariant).Count;

        return numeric + bullets;
    }

    private static int ComputeEvidenceShapeScore(
        RagHitSummary hit,
        string? evidenceText,
        bool includeBackendHints)
    {
        var rawText = evidenceText ?? string.Empty;
        var normalizedText = NormalizeStructuredScanText(rawText);
        if (string.IsNullOrWhiteSpace(normalizedText)
            && hit.MatchedContentCards is not { Count: > 0 }
            && string.IsNullOrWhiteSpace(hit.SelectionHintRole))
        {
            return 0;
        }

        var score = 0;
        if (includeBackendHints)
        {
            score += ScoreEvidenceRoleHint(hit);
            score += ScoreSelectionHintNumbers(hit);
        }

        if (hit.ExactMatchHit)
            score += 3;

        var retriever = NormalizeLexicalLookup(hit.Retriever);
        if (retriever.Contains("exact", StringComparison.Ordinal))
            score += 2;
        else if (retriever.Contains("sparse", StringComparison.Ordinal))
            score += 1;

        if (!string.IsNullOrWhiteSpace(hit.SectionTitle) || !string.IsNullOrWhiteSpace(hit.HeadingPath))
            score += 1;
        if (HasSourceBackedProfileTitle(hit))
            score += 2;

        score += ScoreMatchedContentCardShape(hit.MatchedContentCards);
        score += Math.Min(4, CountProcedureStepMarkers(rawText));
        score += Math.Min(3, CountBulletListMarkers(rawText));
        score += Math.Min(3, CountNumericFactMarkers(normalizedText));
        score += Math.Min(2, CountLabelValueMarkers(rawText));

        if (Regex.IsMatch(rawText, @"\|.+\|", RegexOptions.CultureInvariant))
            score += 1;

        if (hit.ManualReviewRecommended)
            score -= 1;
        if (hit.ExtractionConfidence.HasValue && hit.ExtractionConfidence.Value < 0.55)
            score -= 2;

        return Math.Clamp(score, -8, 18);
    }

    private static int ScoreEvidenceRoleHint(RagHitSummary hit)
    {
        return NormalizeRagEvidenceRole(hit.SelectionHintRole) switch
        {
            "actionable_item" => 4,
            "advisory" => 2,
            "supporting_context" => 1,
            "fragment" => -4,
            "navigation" => -5,
            "low_confidence" => -4,
            _ => 0
        };
    }

    private static int ScoreSelectionHintNumbers(RagHitSummary hit)
    {
        var score = 0;
        score += ScorePositiveHint(hit.SelectionHintActionabilityScore, high: 9, medium: 7, low: 5, highScore: 5, mediumScore: 3, lowScore: 1);
        score += ScorePositiveHint(hit.SelectionHintSupportScore, high: 8, medium: 5, low: 3, highScore: 2, mediumScore: 1, lowScore: 0);
        score -= ScorePositiveHint(hit.SelectionHintFragmentScore, high: 8, medium: 5, low: 3, highScore: 4, mediumScore: 2, lowScore: 1);
        score -= ScorePositiveHint(hit.SelectionHintNavigationScore, high: 8, medium: 5, low: 3, highScore: 4, mediumScore: 2, lowScore: 1);
        score -= ScorePositiveHint(hit.SelectionHintQualityPenalty, high: 8, medium: 5, low: 3, highScore: 3, mediumScore: 2, lowScore: 1);
        return score;
    }

    private static int ScorePositiveHint(
        int? value,
        int high,
        int medium,
        int low,
        int highScore,
        int mediumScore,
        int lowScore)
    {
        if (!value.HasValue)
            return 0;
        if (value.Value >= high)
            return highScore;
        if (value.Value >= medium)
            return mediumScore;
        return value.Value >= low ? lowScore : 0;
    }

    private static int ScoreMatchedContentCardShape(IReadOnlyList<RagHitContentCardSummary>? cards)
    {
        if (cards is not { Count: > 0 })
            return 0;

        var score = 0;
        foreach (var card in cards.Take(5))
        {
            if (!string.IsNullOrWhiteSpace(card.Title) && IsUsefulSourceBackedDisplayTitle(card.Title))
                score += 1;

            var kind = NormalizeLexicalLookup(card.Kind);
            if (kind.Contains("unit", StringComparison.Ordinal)
                || kind.Contains("exact", StringComparison.Ordinal)
                || kind.Contains("lead", StringComparison.Ordinal))
            {
                score += 2;
            }

            if (card.Signals is { Count: > 0 })
                score += 1;
        }

        return Math.Min(7, score);
    }

    private static int CountBulletListMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Regex.Matches(
            text,
            @"(?:^|[\r\n]\s*)[-*+\u2022\u00b7]\s*\S+(?:\s+\S+){2,}",
            RegexOptions.CultureInvariant).Count;
    }

    private static int CountNumericFactMarkers(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        return Regex.Matches(
            normalizedText,
            @"\b\d+(?:[,.]\d+)?(?:\s*(?:%|\p{L}{1,12}\.?|\p{Sc}))?\b",
            RegexOptions.CultureInvariant).Count;
    }

    private static int CountMeasuredValueMarkers(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        return Regex.Matches(
            normalizedText,
            @"\b\d+(?:[,.]\d+)?\s*(?:%|\p{L}{1,12}\.?|\p{Sc})\b|(?:<=|>=|<|>|=)\s*\d",
            RegexOptions.CultureInvariant).Count;
    }

    private static bool HasMeasurableConstraintEvidence(string rawText, string normalizedText)
        => CountLabelValueMarkers(rawText) >= 1
           || (CountProcedureStepMarkers(normalizedText) >= 3 && CountMeasuredValueMarkers(normalizedText) >= 2)
           || Regex.IsMatch(normalizedText, @"(?:^|\s)(?:<=|>=|<|>|=)\s*\d", RegexOptions.CultureInvariant);

    private static bool HasExplicitConstraintEvidence(string rawText, string normalizedText)
        => CountLabelValueMarkers(rawText) >= 1
           || Regex.IsMatch(normalizedText, @"(?:^|\s)(?:<=|>=|<|>|=)\s*\d", RegexOptions.CultureInvariant);

    private static bool LooksLikeVeryShortTimedOperation(string normalizedText)
        => Regex.IsMatch(normalizedText, @"\b(?:1|2|3)\s*(?:s|sec|secs|min|h|hr|hrs)\b", RegexOptions.CultureInvariant);

    private static int CountLabelValueMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Regex.Matches(
            text,
            @"(?:^|[\r\n.;]\s*)[\p{L}\p{N}][^:\r\n]{2,48}:\s+\S",
            RegexOptions.CultureInvariant).Count;
    }

    private static bool LooksLikeMidProcedureFragment(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasItemizedData = Regex.IsMatch(text, @"\b(?:items?|elements?|requirements?|quantities?|quantites?|values?|valeurs?)\b", RegexOptions.CultureInvariant);
        var hasEarlySteps = Regex.IsMatch(text, @"(?:^|\s|preparation|procedure|process|operation|technique)[\s:]*[1-3][\.)]\s*", RegexOptions.CultureInvariant);
        var hasLateSteps = Regex.IsMatch(text, @"(?:^|\s)[4-9][\.)]\s*", RegexOptions.CultureInvariant);
        var startsMidSentence = Regex.IsMatch(
            text,
            @"^(?:arreter|ajouter|appliquer|verifier|controler|mesurer|regler|ajuster|retirer|transvider|lorsque|quand|stop|add|apply|verify|check|measure|set|adjust|when|remove)\b",
            RegexOptions.CultureInvariant);

        return (startsMidSentence && !hasItemizedData)
            || (hasLateSteps && !hasEarlySteps && !hasItemizedData);
    }

    private static bool LooksLikeIntroLeadInHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lead = text.Length <= 360 ? text : text[..360];
        var hasIntroLead = Regex.IsMatch(
            lead,
                @"\b(?:bienvenue|introduction|avant propos|preface|sommaire|table des matieres|reperes de contenu|premiers extraits)\b",
            RegexOptions.CultureInvariant);
        if (!hasIntroLead)
            return false;

        var firstStructure = Regex.Match(
            text,
            @"\b(?:items?|elements?|requirements?|quantities?|preparation|preparacion|preparacao|procedure|process|operation|workflow|instructions?|(?:pour|for|para|per|fur|fuer|zu|a|da)\s+\d{1,3}\s+\p{L}|\d+\s+(?:units?|items?))\b",
            RegexOptions.CultureInvariant);
        return !firstStructure.Success || firstStructure.Index > 280;
    }

    private static bool LooksLikeVerySimpleProcedureHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lookupText = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        var hasSimpleWording = Regex.IsMatch(
            text,
            @"\b(?:facile|faciles|simple|simples|rapide|rapides|plus simple|very simple|easy|quick)\b",
            RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                lookupText,
                @"\b(?:facile|faciles|simple|simples|rapide|rapides|plus simple|very simple|easy|quick)\b",
            RegexOptions.CultureInvariant);
        var hasVeryShortDuration = LooksLikeVeryShortTimedOperation(text);
        var hasControlledParameter = HasExplicitConstraintEvidence(GetRagHitPrimaryContentText(hit), text)
            || CountProcedureStepMarkers(text) >= 4;
        var lacksControlledParameter = !hasControlledParameter;

        if (hasSimpleWording && hasVeryShortDuration && !hasControlledParameter)
            return true;
        if (hasVeryShortDuration && !hasControlledParameter && CountProcedureStepMarkers(text) <= 3)
            return true;

        return (hasSimpleWording || hasVeryShortDuration)
            && lacksControlledParameter
            && ComputeTechnicalComplexityCueScore(hit) < 9;
    }

    private static bool LooksLikeLowStructureShortProcedureHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup($"{GetRagHitPrimaryContentText(hit)} {GetRagHitLookupText(hit)}");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasControlledParameter = HasExplicitConstraintEvidence(GetRagHitPrimaryContentText(hit), text)
            || CountProcedureStepMarkers(text) >= 4;
        var hasVeryShortOperation = LooksLikeVeryShortTimedOperation(text);
        var hasLowStructure = CountProcedureStepMarkers(text) <= 3
            && Regex.Matches(text, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|min|h)\b", RegexOptions.CultureInvariant).Count < 5;

        if (!hasControlledParameter && hasVeryShortOperation && hasLowStructure)
            return true;
        if (!hasControlledParameter && hasVeryShortOperation && ComputeTechnicalComplexityCueScore(hit) < 9)
            return true;
        if (!hasControlledParameter
            && hasVeryShortOperation
            && Regex.IsMatch(text, @"\b(?:immediat|immediate|instant|quick|rapide)\b", RegexOptions.CultureInvariant))
            return true;

        return !hasControlledParameter
            && hasLowStructure
            && ComputeTechnicalComplexityCueScore(hit) < 12;
    }

    private static bool LooksLikeGenericTechniqueDefinitionHit(RagHitSummary hit)
    {
        var text = NormalizeLexicalLookup(GetRagHitPrimaryContentText(hit));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasDefinitionWording = Regex.IsMatch(
            text,
            @"\b(?:cette\s+technique\s+consiste|technique\s+consiste|mode\s+de\s+preparation|modes\s+de\s+preparation|mode\s+operatoire|modes\s+operatoires|preparation\s+method|operation\s+method|workflow\s+method|process\s+method|procedure\s+method)\b",
            RegexOptions.CultureInvariant);
        var hasGenericTechniqueHeading = Regex.IsMatch(
            text,
            @"\b(?:technique|method|methode|procedure)\s*[:\-]",
            RegexOptions.CultureInvariant);
        var hasGenericTechniqueOverview = Regex.IsMatch(
            text,
            @"\b(?:technique|method|methode|procedure)\s+(?:generale|general|overview|vue\s+d\s+ensemble)\b",
            RegexOptions.CultureInvariant);
        if (!hasDefinitionWording && !hasGenericTechniqueHeading && !hasGenericTechniqueOverview)
            return false;

        var hasStructuredItemEvidence = Regex.IsMatch(
                text,
                @"\b(?:items?|elements?|requirements?|components?|composants?|materials?|materiel|equipment|preparation\s*[:â€¢]|operation\s*[:â€¢]|workflow\s*[:â€¢]|procedure\s*[:â€¢]|method\s*[:â€¢])\b",
                RegexOptions.CultureInvariant)
            || CountProcedureStepMarkers(text) >= 2
            || Regex.Matches(text, @"\b\d+(?:[,.]\d+)?\s*(?:g|kg|mg|ml|cl|l|mm|cm|m|bar|pa|kpa|mpa|v|a|w|hz|rpm|%|min|h)\b", RegexOptions.CultureInvariant).Count >= 2;

        return !hasStructuredItemEvidence
            && (!hasGenericTechniqueHeading || ComputeProcedureCompletenessCueScore(hit) < 7);
    }

    private static bool LooksLikeProcedureRankingQuery(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        return Regex.IsMatch(
            normalized,
            @"\b(?:procedura|procedure|procedures|procedimento|process|processus|operation|operations|workflow|workflows|method|methode|instruction|instructions|execution|preparation|preparacion|preparacao|etape|etapes|steps|pasos|passos)\b",
            RegexOptions.CultureInvariant);
    }

    private static int ComputeStructuredProcedureEvidenceCueScore(RagHitSummary hit)
    {
        return Math.Clamp(
            ComputeEvidenceShapeScore(hit, GetRagHitPrimaryEvidenceText(hit), includeBackendHints: true),
            -4,
            12);
    }

    private static int ComputeStructuredProcedureVisibleEvidenceCueScore(RagHitSummary hit)
    {
        return Math.Clamp(
            ComputeEvidenceShapeScore(hit, GetRagHitPrimaryContentText(hit), includeBackendHints: true),
            -4,
            12);
    }

    private static string FormatRankingHitReference(string language, RagHitSummary hit)
    {
        var docLabel = string.IsNullOrWhiteSpace(hit.DocName) ? hit.DocPath : hit.DocName;
        return $"{docLabel} {SourceBackedPagePrefix(language)}{hit.PageStart}";
    }

    private static string BuildSourceBackedRankingReason(string language, RagHitSummary hit, string query)
    {
        var reasons = BuildSourceBackedRankingReasonFragments(language, hit, query);
        var excerpt = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 260);
        if (reasons.Count == 0)
            reasons.Add(NormalizeLanguageCode(language) switch
            {
                "en" => "it is one of the strongest retrieved matches",
                "es" => "es una de las coincidencias recuperadas mas fuertes",
                "pt" => "e uma das correspondencias recuperadas mais fortes",
                "de" => "es ist einer der staerksten gefundenen Treffer",
                "it" => "e una delle corrispondenze recuperate piu forti",
                _ => "c'est une des correspondances retrouvÃ©es les plus fortes"
            });

        var because = NormalizeLanguageCode(language) switch
        {
            "en" => "because",
            "es" => "porque",
            "pt" => "porque",
            "de" => "weil",
            "it" => "perche",
            _ => "car"
        };

        var excerptLabel = SourceBackedLabel(language, "Extrait", "Excerpt", "Extracto", "Excerto", "Auszug", "Estratto");
        return $"{because} {string.Join(", ", reasons)}. {excerptLabel}: {excerpt}";
    }

    private static List<string> BuildSourceBackedRankingReasonFragments(string language, RagHitSummary hit, string query)
    {
        var normalizedLanguage = NormalizeLanguageCode(language);
        var text = NormalizeLexicalLookup(GetRagHitLookupText(hit));
        var fragments = new List<string>();

        if (LooksLikeTechnicalRankingQuery(query)
            && Regex.IsMatch(text, @"\b(?:technique|procedure|procedures|mode operatoire|operation|workflow|execution|instruction|instructions|etape|etapes|step|steps|preparation|method|methode)\b", RegexOptions.CultureInvariant))
        {
            fragments.Add(normalizedLanguage switch
            {
                "en" => "the excerpt contains explicit procedural or technical wording",
                "es" => "el extracto contiene vocabulario procedimental o tecnico explicito",
                "pt" => "o excerto contem vocabulario procedural ou tecnico explicito",
                "de" => "der Auszug enthaelt ausdrueckliche Verfahrens- oder Technikbegriffe",
                "it" => "l'estratto contiene lessico procedurale o tecnico esplicito",
                _ => "l'extrait contient du vocabulaire procedural ou technique explicite"
            });
        }

        if (HasMeasurableConstraintEvidence(GetRagHitLookupText(hit), text))
        {
            fragments.Add(normalizedLanguage switch
            {
                "en" => "it includes parameters, timing, or measurable constraints",
                "es" => "incluye parametros, tiempos o restricciones medibles",
                "pt" => "inclui parametros, tempos ou restricoes mensuraveis",
                "de" => "er enthaelt Parameter, Zeiten oder messbare Vorgaben",
                "it" => "include parametri, tempi o vincoli misurabili",
                _ => "il contient des parametres, temps ou contraintes mesurables"
            });
        }

        if (ScoreEvidenceRoleHint(hit) > 0
            || CountLabelValueMarkers(GetRagHitLookupText(hit)) > 0
            || CountProcedureStepMarkers(text) >= 3)
        {
            fragments.Add(normalizedLanguage switch
            {
                "en" => "it has structured or constraint-bearing evidence",
                "es" => "aporta evidencia estructurada o con restricciones",
                "pt" => "traz evidencia estruturada ou com restricoes",
                "de" => "er enthaelt strukturierte oder einschraenkende Nachweise",
                "it" => "contiene evidenza strutturata o vincolante",
                _ => "il contient des indices structures ou contraints"
            });
        }

        return fragments;
    }
}
