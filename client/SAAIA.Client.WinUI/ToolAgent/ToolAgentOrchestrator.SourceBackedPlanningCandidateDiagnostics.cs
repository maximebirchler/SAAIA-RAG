using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed class SourceBackedPlanningCandidateRejectionMemo
    {
        internal object Gate { get; } = new();
        internal Dictionary<string, string> Results { get; } = new(StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<SourceBackedOptionCandidate, SourceBackedPlanningCandidateRejectionMemo>
        SourceBackedPlanningCandidateRejectionCache = new();

    private static string ExplainSourceBackedPlanningCandidateRejection(
        SourceBackedOptionCandidate candidate,
        string? query,
        bool requireDirectPageEvidence,
        bool requireStrictStructuredEvidence = true,
        string? dominantTopLevelScope = null)
    {
        var contextKey = NormalizeLexicalLookup(query)
            + "|" + requireDirectPageEvidence
            + "|" + requireStrictStructuredEvidence
            + "|" + NormalizeLexicalLookup(dominantTopLevelScope);
        var memo = SourceBackedPlanningCandidateRejectionCache.GetOrCreateValue(candidate);
        lock (memo.Gate)
        {
            if (memo.Results.TryGetValue(contextKey, out var cached))
                return cached;
        }

        var result = ExplainSourceBackedPlanningCandidateRejectionUncached(
            candidate,
            query,
            requireDirectPageEvidence,
            requireStrictStructuredEvidence,
            dominantTopLevelScope);
        lock (memo.Gate)
            memo.Results[contextKey] = result;
        return result;
    }

    private static string ExplainSourceBackedPlanningCandidateRejectionUncached(
        SourceBackedOptionCandidate candidate,
        string? query,
        bool requireDirectPageEvidence,
        bool requireStrictStructuredEvidence = true,
        string? dominantTopLevelScope = null)
    {
        if (LooksLikePageReferenceOnlyHit(candidate.Hit))
            return "page_reference_only";
        if (ShouldRejectSourceBackedPlanningOrientationSurfaceCandidate(candidate))
            return "orientation_surface";
        if (string.IsNullOrWhiteSpace(candidate.Title))
            return "missing_candidate_title";
        var normalizedTitle = NormalizeLexicalLookup(candidate.Title);
        var normalizedCleanTitle = NormalizeLexicalLookup(CleanSourceBackedOptionTitle(candidate.Title));
        if (requireDirectPageEvidence
            && LooksLikeStrictStructuredPlanningInventoryPageTitleNoise(normalizedCleanTitle))
        {
            return "strict_inventory_title_noise";
        }

        var hasStrictEvidence = HasStrictStructuredPlanningCandidateEvidence(candidate);
        var hasAnchoredContextProof = SourceBackedContextCandidateHasAnchoredFuzzyLocalStructuredProof(candidate, normalizedTitle);
        if (LooksLikePlanItemNoise(candidate.Title))
            return "plan_item_noise";
        if (LooksLikeWeakSourceBackedOptionTitle(candidate.Title) && !hasStrictEvidence)
            return "weak_candidate_title";
        if (LooksLikeNonConcreteSourceBackedPlanningCandidate(candidate, hasStrictEvidence))
            return "non_concrete_candidate_evidence";
        if (LooksLikeRequestedPlanningSlotAxisLabelCandidate(candidate, query))
            return "requested_axis_label";
        if (LooksLikeGenericInventorySurfaceDerivedPlanningCandidate(candidate))
            return "generic_inventory_surface";
        if (LooksLikeGenericPlanningContextCandidate(candidate))
            return "generic_planning_context";
        if (LooksLikePlanningFrameOrAdviceCandidate(candidate, hasStrictEvidence))
            return "planning_frame_or_advice";
        if (LooksLikeWeakSingleTermStructuredPlanningCandidate(candidate, hasStrictEvidence))
            return "weak_single_term_candidate";
        if (LooksLikeUnattachedShortSectionStructuredPlanningCandidate(candidate))
            return "unattached_section_heading";
        if (LooksLikeProcedureSentenceTitle(normalizedTitle))
            return "procedure_sentence_title";
        var fieldValueReason = ExplainStructuredPlanningFieldValueCandidateRejection(candidate, normalizedTitle);
        if (!string.IsNullOrWhiteSpace(fieldValueReason))
            return fieldValueReason;
        if (!SourceBackedPlanningCandidateMatchesDominantTopLevel(candidate, dominantTopLevelScope))
            return "outside_dominant_scope";
        if (!IsUsableSourceBackedPlanningCandidate(candidate))
            return "unusable_or_generic_candidate";
        if (requireDirectPageEvidence
            && !LooksLikeConcreteStructuredPlanningCandidateTitle(candidate.Title)
            && !hasAnchoredContextProof)
        {
            return "not_concrete_candidate_title";
        }
        if (requireDirectPageEvidence
            && (requireStrictStructuredEvidence
                ? !HasStrictStructuredPlanningCandidateEvidence(candidate)
                : !HasDirectSourceBackedPlanningCandidateEvidence(candidate)))
        {
            return requireStrictStructuredEvidence
                ? "missing_strict_candidate_evidence"
                : "missing_direct_candidate_evidence";
        }
        if (!string.IsNullOrWhiteSpace(query) && candidate.Score <= 0)
            return "not_relevant_to_query";

        return string.Empty;
    }

    private static string FormatPlanningTraceBool(bool value)
        => value ? "true" : "false";

    private static string FormatPlanningTraceValue(string? value)
    {
        var normalized = CollapseWhitespace(value ?? string.Empty);
        if (normalized.Length > 140)
            normalized = normalized[..140] + "...";

        return normalized
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string FormatStructuredPlanningSlotPoolCounts(
        IReadOnlyList<string> labels,
        IReadOnlyList<int> counts)
    {
        if (labels.Count == 0 || counts.Count == 0)
            return string.Empty;

        return string.Join(
            ",",
            labels.Take(counts.Count).Select((label, index) =>
                $"{NormalizeLexicalLookup(label)}:{counts[index].ToString(CultureInfo.InvariantCulture)}"));
    }

    private static string FormatSourceBackedOptionCandidateTraceSamples(
        IEnumerable<SourceBackedOptionCandidate> candidates,
        int limit = 6)
        => FormatPlanningTraceValue(string.Join("; ", candidates
            .Take(limit)
            .Select(static candidate =>
            {
                var doc = string.IsNullOrWhiteSpace(candidate.Hit.DocName)
                    ? Path.GetFileName(candidate.Hit.DocPath)
                    : candidate.Hit.DocName;
                return string.Join(" ", new[]
                {
                    candidate.Title,
                    string.IsNullOrWhiteSpace(doc) ? null : $"@{doc}",
                    candidate.Hit.PageStart > 0 ? $"p{candidate.Hit.PageStart}" : null,
                    string.IsNullOrWhiteSpace(candidate.Hit.RetrievalQuery) ? null : $"q={candidate.Hit.RetrievalQuery}"
                }.Where(static value => !string.IsNullOrWhiteSpace(value)));
            })));

    private static int ResolveMinimumSourceBackedPlanningCandidateCount(string? query, int targetSlots, bool hasStructuredAxes)
    {
        if (LooksLikeSourceBackedVerificationChecklistRequest(query))
            return Math.Min(3, Math.Max(1, targetSlots));

        if (ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return Math.Min(20, Math.Max(1, targetSlots));

        if (!hasStructuredAxes)
            return targetSlots > 3
                ? Math.Min(20, Math.Max(2, targetSlots))
                : Math.Min(3, Math.Max(2, targetSlots));

        return Math.Max(1, targetSlots);
    }

    private static bool RequiresFullyDistinctStructuredPlanningItems(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized.Contains("non dupli", StringComparison.Ordinal)
            || normalized.Contains("pas dupli", StringComparison.Ordinal)
            || normalized.Contains("not duplic", StringComparison.Ordinal))
        {
            return true;
        }

        return Regex.IsMatch(
            normalized,
            @"\b(?:tous|toutes|chaque|each|every|cada|ogni|jeder|jede)\b.{0,36}\b(?:different|differents|differentes|distinct|distincts|distinctes|unterschiedlich|verschieden|diverso|diversi|distinto|distintos|distintas)\b|\b(?:sans|aucune|no|without|sin|sem|ohne|senza)\b.{0,24}\b(?:repetition|repeter|repeat|repeats|duplicat|duplicado|wiederholung|ripetizione)\b|\b(?:non|pas|not)\b.{0,24}\b(?:dupliq|duplicat|repet|repeat)\b|\b(?:15|quinze|fifteen|quince|funfzehn|fuenfzehn|quindici)\b.{0,42}\b(?:different|differents|differentes|distinct|distincts|distinctes)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool ShouldAllowSourcedStructuredPlanningRotation(string? query)
        => !RequiresFullyDistinctStructuredPlanningItems(query);

    private static bool RequiresExplicitStructuredPlanningSlotEvidence(string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return false;

        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:seulement|uniquement|only|solo|solamente|apenas|nur|solo)\b.{0,56}\b(?:sources?|source|utile|utiles|useful|relevant|pertinent|pertinentes?|adaptees?|adapted|adequat|adequates?)\b|\b(?:vraiment|really|truly|bien|best)\b.{0,32}\b(?:utile|utiles|useful|relevant|pertinent|pertinentes?|adaptees?|adapted)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool HasSourceBackedPlanningAnchorCoverage(ToolResults toolResults, string? query)
    {
        var anchorTerms = ExtractPlanningCoverageAnchorTerms(query).ToArray();
        if (anchorTerms.Length == 0)
            return true;

        return EnumerateRagHitSummaries(toolResults)
            .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
            .Any(hit => QueryAnchorTermsMatchHit(anchorTerms, hit));
    }

    private static IEnumerable<string> ExtractPlanningCoverageAnchorTerms(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        foreach (var term in ExtractPlanningRetrievalTerms(normalized))
        {
            if (term.Length >= 4 && !IsGenericPlanningCoverageTerm(term))
                yield return term;
        }
    }

    private static bool IsGenericPlanningCoverageTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return normalized is
            "plan" or "plans" or "planning" or "programme" or "program" or "schedule" or "calendar" or
            "calendrier" or "organisation" or "organizacion" or "organizacao" or "organizzazione" or
            "besoin" or "besoins" or "need" or "needs" or "asse" or "asses" or "fasse" or "fasses" or "faire" or "proposer" or "mettre" or
            "mettant" or "mets" or "met" or "include" or "includes" or "put" or "puts" or
            "candidate" or "candidates" or "candidat" or "candidats" or
            "semaine" or "hebdo" or "hebdomadaire" or "week" or "weekly" or "semana" or "semanal" or
            "woche" or "wochenplan" or "settimana" or "settimanale" or
            "lundi" or "mardi" or "mercredi" or "jeudi" or "vendredi" or "vrendredi" or "samedi" or "dimanche" or
            "monday" or "tuesday" or "wednesday" or "thursday" or "friday" or "saturday" or "sunday" or
            "items" or "option" or "options" or "item" or "items" or
            "option" or "options" or "option" or "options" or
            "morning" or "afternoon" or "evening" or
            "rapide" or "rapides" or "simple" or "simples" or "facile" or "faciles" or
            "utile" or "utiles" or "useful" or "available" or "disponible" or "disponibles" or
            "options" or "option" or "suggestions" or "suggestion" or "idees" or "idee" or "ideas" or
            "detail" or "details" or "detailed" or "detaille" or "detailles" or
            "ideal" or "ideals" or "ideaux";
    }

}
