using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int SourceBackedOptionCandidateSelectionCacheMaxEntries = 32;
    private static readonly object SourceBackedOptionCandidateSelectionCacheGate = new();
    private static readonly Dictionary<string, IReadOnlyList<SourceBackedOptionCandidate>> SourceBackedOptionCandidateSelectionCache =
        new(StringComparer.Ordinal);

    private static IReadOnlyList<SourceBackedOptionCandidate> SelectSourceBackedOptionCandidates(
        ToolResults toolResults,
        string? query,
        bool keepOverRequestedDuration = false,
        string language = "",
        bool allowPartialStructuredPlanningCandidates = false)
    {
        var selectionCacheKey = BuildSourceBackedPlanningCandidateSelectionCacheKey(
            toolResults,
            query,
            maxItems: keepOverRequestedDuration ? -2 : -1,
            language,
            requireStrictStructuredEvidence: !allowPartialStructuredPlanningCandidates);
        var cacheKey = string.IsNullOrWhiteSpace(selectionCacheKey)
            ? string.Empty
            : selectionCacheKey + "|option_candidates";
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            lock (SourceBackedOptionCandidateSelectionCacheGate)
            {
                if (SourceBackedOptionCandidateSelectionCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }
        }

        var selected = SelectSourceBackedOptionCandidatesUncached(
                toolResults,
                query,
                keepOverRequestedDuration,
                language,
                allowPartialStructuredPlanningCandidates)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            lock (SourceBackedOptionCandidateSelectionCacheGate)
            {
                if (SourceBackedOptionCandidateSelectionCache.Count >= SourceBackedOptionCandidateSelectionCacheMaxEntries)
                    SourceBackedOptionCandidateSelectionCache.Clear();
                SourceBackedOptionCandidateSelectionCache[cacheKey] = selected;
            }
        }

        return selected;
    }

    private static IReadOnlyList<SourceBackedOptionCandidate> SelectSourceBackedOptionCandidatesUncached(
        ToolResults toolResults,
        string? query,
        bool keepOverRequestedDuration = false,
        string language = "",
        bool allowPartialStructuredPlanningCandidates = false)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        var broadCollectionLikeRequest = LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeAnyDocumentaryPlanningRequest(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query);
        var requiresStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var traceOptionSelection = requiresStructuredPlanning || broadCollectionLikeRequest;
        var optionStopwatch = traceOptionSelection ? Stopwatch.StartNew() : null;
        if (traceOptionSelection)
        {
            ClientLog.Info(
                "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=start|stage=start"
                + $"|structuredPlanning={requiresStructuredPlanning}|broadCollection={broadCollectionLikeRequest}|allowPartialStructuredPlanning={allowPartialStructuredPlanningCandidates}");
        }

        var namedEntityTerms = ExtractNamedEntityLikeQueryTerms(query);
        var softChoiceOptionKindTerms = ExtractSoftChoiceRequestedOptionKindTerms(query);
        var requiresNamedEntityEvidence = namedEntityTerms.Count > 0
            && !broadCollectionLikeRequest
            && !LooksLikeSourceBackedPairingRecommendationRequest(query)
            && Regex.IsMatch(normalizedQuery, @"\b(?:uniquement|only|avec|with|using|utiliser|utilise|concernant|about|sur)\b", RegexOptions.CultureInvariant);
        var sourceHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit)
                || (requiresStructuredPlanning && RagHitHasSelfContainedStructuredPlanningCardProof(hit)))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, query ?? string.Empty))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit)
                || (requiresStructuredPlanning && RagHitHasSelfContainedStructuredPlanningCardProof(hit)))
            .ToList();

        if (!string.IsNullOrWhiteSpace(query))
            sourceHits = FilterHitsToDominantTopLevel(sourceHits, query).ToList();
        if (traceOptionSelection)
        {
            ClientLog.Info(
                "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=source_hits.end|stage=source_hits.end"
                + $"|hits={sourceHits.Count}|ms={optionStopwatch!.ElapsedMilliseconds}");
        }

        var maxMinutes = TryExtractRequestedMaxMinutes(query);
        var rawObjectAnchorTerms = ExtractSourceBackedOptionObjectAnchorTerms(query)
            .Where(static term => term.Length >= 5)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        var objectAnchorTerms = rawObjectAnchorTerms
            .Where(term => !ShouldSuppressGenericStructuredPlanningCandidateAnchorTerm(
                term,
                requiresStructuredPlanning,
                broadCollectionLikeRequest))
            .ToArray();
        var rawQueryAnchorTerms = ExtractQuerySignalTerms(normalizedQuery)
            .Concat(rawObjectAnchorTerms)
            .Where(static term => term.Length >= 5)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var queryAnchorTerms = rawQueryAnchorTerms
            .Where(term => !ShouldSuppressGenericStructuredPlanningCandidateAnchorTerm(
                term,
                requiresStructuredPlanning,
                broadCollectionLikeRequest))
            .ToArray();
        var suppressedAnchorTerms = rawQueryAnchorTerms
            .Except(queryAnchorTerms, StringComparer.Ordinal)
            .ToArray();
        var hasConcreteObjectAnchor = objectAnchorTerms.Length > 0;
        var hasSourceBackedAnchorEvidence = queryAnchorTerms.Length > 0
            && (!broadCollectionLikeRequest || hasConcreteObjectAnchor)
            && !LooksLikeCompositeSourceBackedOptionAnchorRequest(normalizedQuery, maxMinutes.HasValue)
            && !LooksLikeSourceBackedPairingRecommendationRequest(query)
            && sourceHits.Any(hit => QueryAnchorTermsMatchHit(queryAnchorTerms, hit));

        if (traceOptionSelection)
        {
            ClientLog.Info(
                "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=anchor_scope|stage=anchor_scope"
                + $"|rawAnchors={FormatPlanningTraceValue(string.Join(",", rawQueryAnchorTerms))}"
                + $"|activeAnchors={FormatPlanningTraceValue(string.Join(",", queryAnchorTerms))}"
                + $"|suppressedGenericAnchors={FormatPlanningTraceValue(string.Join(",", suppressedAnchorTerms))}"
                + $"|objectAnchors={FormatPlanningTraceValue(string.Join(",", objectAnchorTerms))}"
                + $"|willFilter={hasSourceBackedAnchorEvidence}"
                + $"|ms={optionStopwatch!.ElapsedMilliseconds}");
        }

        if (traceOptionSelection)
        {
            ClientLog.Info(
                "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=raw_candidates.start|stage=raw_candidates.start"
                + $"|hits={sourceHits.Count}|ms={optionStopwatch!.ElapsedMilliseconds}");
        }

        var rawCandidates = new List<SourceBackedOptionCandidate>();
        var lastRawCandidateProgressMs = optionStopwatch?.ElapsedMilliseconds ?? 0;
        for (var sourceHitIndex = 0; sourceHitIndex < sourceHits.Count; sourceHitIndex++)
        {
            var hit = sourceHits[sourceHitIndex];
            foreach (var candidate in BuildSourceBackedOptionCandidatesFromHit(
                         hit,
                         query,
                         language,
                         keepOverRequestedDuration ? null : maxMinutes,
                         requiresNamedEntityEvidence,
                         namedEntityTerms,
                         allowPartialStructuredPlanningCandidates))
            {
                if (!string.IsNullOrWhiteSpace(candidate.Title))
                    rawCandidates.Add(candidate);
            }

            if (traceOptionSelection)
            {
                var processedHits = sourceHitIndex + 1;
                var elapsedMs = optionStopwatch!.ElapsedMilliseconds;
                if (processedHits == sourceHits.Count
                    || processedHits % 10 == 0
                    || elapsedMs - lastRawCandidateProgressMs >= 15000)
                {
                    ClientLog.Info(
                        "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=raw_candidates.progress|stage=raw_candidates.progress"
                        + $"|processedHits={processedHits}"
                        + $"|totalHits={sourceHits.Count}"
                        + $"|rawCandidates={rawCandidates.Count}"
                        + $"|ms={elapsedMs}"
                        + $"|lastDoc={FormatPlanningTraceValue(hit.DocName ?? hit.DocPath)}"
                        + $"|lastPage={hit.PageStart}");
                    lastRawCandidateProgressMs = elapsedMs;
                }
            }
        }

        if (traceOptionSelection)
        {
            ClientLog.Info(
                "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=raw_candidates.end|stage=raw_candidates.end"
                + $"|rawCandidates={rawCandidates.Count}|hits={sourceHits.Count}|ms={optionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", rawCandidates.Take(8).Select(static candidate => candidate.Title))}");
        }

        var candidates = rawCandidates
            .Where(candidate => !LooksLikeLowValueSourceBackedOptionCandidate(candidate, normalizedQuery))
            .Where(candidate => LooksLikeUsableSourceBackedOptionCandidate(candidate.Hit)
                || (requiresStructuredPlanning
                    && (allowPartialStructuredPlanningCandidates
                        ? HasDirectSourceBackedPlanningCandidateEvidence(candidate)
                        : HasStrictStructuredPlanningCandidateEvidence(candidate))))
            .Where(candidate => keepOverRequestedDuration || !maxMinutes.HasValue || !candidate.VisibleMinutes.HasValue || candidate.VisibleMinutes.Value <= maxMinutes.Value)
            .Select(candidate => ApplySoftChoiceOptionKindScore(candidate, softChoiceOptionKindTerms))
            .Where(candidate => candidate.Score > -20)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Hit.Score)
            .GroupBy(candidate => $"{NormalizeLexicalLookup(candidate.Title)}|{candidate.Hit.DocPath}|{candidate.Hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(group => SelectBestSourceBackedOptionDuplicate(group, query))
            .ToList();
        if (traceOptionSelection)
        {
            ClientLog.Info(
                "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=core_filter.end|stage=core_filter.end"
                + $"|candidates={candidates.Count}|rawCandidates={rawCandidates.Count}|ms={optionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", candidates.Take(8).Select(static candidate => candidate.Title))}");
        }

        var excludedTerms = ExtractSourceBackedExcludedTerms(query);
        if (excludedTerms.Count > 0)
        {
            candidates = candidates
                .Where(candidate => !RagHitContainsAnyExcludedTerm(candidate.Hit, excludedTerms))
                .ToList();
            if (traceOptionSelection)
            {
                ClientLog.Info(
                    "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=excluded_terms_filter.end|stage=excluded_terms_filter.end"
                    + $"|candidates={candidates.Count}|excludedTerms={excludedTerms.Count}|ms={optionStopwatch!.ElapsedMilliseconds}");
            }
        }

        var pairingOptionKindTerms = ExtractPairingRequestedOptionKindTerms(query);
        if (pairingOptionKindTerms.Count > 0 && candidates.Count > 1)
        {
            var kindMatchedCandidates = candidates
                .Where(candidate => PairingOptionKindMatchesCandidate(pairingOptionKindTerms, candidate))
                .ToList();
            if (kindMatchedCandidates.Count > 0)
                candidates = kindMatchedCandidates;
            if (traceOptionSelection)
            {
                ClientLog.Info(
                    "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=pairing_kind_filter.end|stage=pairing_kind_filter.end"
                    + $"|candidates={candidates.Count}|kindTerms={pairingOptionKindTerms.Count}|ms={optionStopwatch!.ElapsedMilliseconds}");
            }
        }

        if (softChoiceOptionKindTerms.Count > 0 && candidates.Count > 1)
        {
            var kindMatchedCandidates = candidates
                .Where(candidate => SoftChoiceOptionKindMatchesCandidate(softChoiceOptionKindTerms, candidate))
                .ToList();
            if (kindMatchedCandidates.Count > 0)
            {
                var kindMatchedKeys = kindMatchedCandidates
                    .Select(BuildSourceBackedOptionCandidateKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                candidates = candidates
                    .Where(candidate => kindMatchedKeys.Contains(BuildSourceBackedOptionCandidateKey(candidate))
                        || IsPrimaryQueryTopConcreteCardCandidate(candidate))
                    .ToList();
            }

            candidates = candidates
                .Where(candidate => !OptionKindContradictsCandidate(softChoiceOptionKindTerms, candidate))
                .ToList();
            if (traceOptionSelection)
            {
                ClientLog.Info(
                    "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=soft_kind_filter.end|stage=soft_kind_filter.end"
                    + $"|candidates={candidates.Count}|kindTerms={softChoiceOptionKindTerms.Count}|ms={optionStopwatch!.ElapsedMilliseconds}");
            }
        }

        if (hasSourceBackedAnchorEvidence)
        {
            var candidatesBeforeAnchorFilter = candidates;
            candidates = candidates
                .Where(candidate => QueryAnchorTermsMatchHit(queryAnchorTerms, candidate.Hit))
                .ToList();
            if (traceOptionSelection)
            {
                var removedByAnchorFilter = candidatesBeforeAnchorFilter
                    .Where(candidate => !candidates.Contains(candidate))
                    .ToList();
                ClientLog.Info(
                    "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=anchor_filter.end|stage=anchor_filter.end"
                    + $"|candidates={candidates.Count}"
                    + $"|removed={candidatesBeforeAnchorFilter.Count - candidates.Count}"
                    + $"|anchorTerms={queryAnchorTerms.Length}"
                    + $"|anchors={FormatPlanningTraceValue(string.Join(",", queryAnchorTerms))}"
                    + $"|kept={FormatSourceBackedOptionCandidateTraceSamples(candidates)}"
                    + $"|removedSamples={FormatSourceBackedOptionCandidateTraceSamples(removedByAnchorFilter)}"
                    + $"|ms={optionStopwatch!.ElapsedMilliseconds}");
            }
        }
        else if (traceOptionSelection && (rawQueryAnchorTerms.Length > 0 || suppressedAnchorTerms.Length > 0))
        {
            ClientLog.Info(
                "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=anchor_filter.skipped|stage=anchor_filter.skipped"
                + $"|reason={(queryAnchorTerms.Length == 0 ? "no_active_specific_anchor" : "no_concrete_anchor_evidence")}"
                + $"|activeAnchors={FormatPlanningTraceValue(string.Join(",", queryAnchorTerms))}"
                + $"|suppressedGenericAnchors={FormatPlanningTraceValue(string.Join(",", suppressedAnchorTerms))}"
                + $"|ms={optionStopwatch!.ElapsedMilliseconds}");
            }

        if (requiresNamedEntityEvidence)
        {
            candidates = candidates
                .Where(candidate => QueryAnchorTermsMatchHit(namedEntityTerms, candidate.Hit))
                .ToList();
            if (traceOptionSelection)
            {
                ClientLog.Info(
                    "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=named_entity_filter.end|stage=named_entity_filter.end"
                    + $"|candidates={candidates.Count}|terms={namedEntityTerms.Count}|ms={optionStopwatch!.ElapsedMilliseconds}");
            }
        }

        if (traceOptionSelection)
        {
            ClientLog.Info(
                "ToolAgent option candidate selection: trace_path=rag.source_backed.option_candidate_selection|trace_step=end|stage=end"
                + $"|candidates={candidates.Count}|ms={optionStopwatch!.ElapsedMilliseconds}|topTitles={string.Join("; ", candidates.Take(10).Select(static candidate => candidate.Title))}");
        }

        return candidates;
    }

    private static IEnumerable<string> ExtractSourceBackedOptionObjectAnchorTerms(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        foreach (Match match in Regex.Matches(
                     normalized,
                     @"\b(?:pour|for|para|per|sur|about|concernant|regarding|avec|with|con|com|mit)\s+(?:(?:le|la|les|l['\u2019]?|un|une|des|du|de\s+la|de\s+l['\u2019]?|the|a|an|some)\s+)?(?<target>[\p{L}\p{N}'\u2019-]{3,60}(?:\s+[\p{L}\p{N}'\u2019-]{2,40}){0,3})",
                     RegexOptions.CultureInvariant))
        {
            var target = NormalizeLexicalLookup(match.Groups["target"].Value);
            foreach (var term in ExtractQuerySignalTerms(target)
                         .Where(static term => term.Length >= 4)
                         .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
                         .Where(static term => !SourceBackedOptionConstraintTerms.Contains(term)))
            {
                yield return term;
            }
        }
    }

    private static bool ShouldSuppressGenericStructuredPlanningCandidateAnchorTerm(
        string? term,
        bool requiresStructuredPlanning,
        bool broadCollectionLikeRequest)
    {
        if (!requiresStructuredPlanning && !broadCollectionLikeRequest)
            return false;

        return IsGenericPlanningCoverageTerm(term ?? string.Empty);
    }

    private static string BuildPairingLeadCaveat(string query, string language)
    {
        var targetTerms = ExtractPairingTargetAnchorTerms(query);
        if (targetTerms.Count == 0)
            return string.Empty;

        var optionKindTerms = ExtractPairingRequestedOptionKindTerms(query);
        var targetList = string.Join(", ", targetTerms.Select(static term => $"\"{term}\""));
        var optionKindList = optionKindTerms.Count > 0
            ? string.Join(", ", optionKindTerms.Select(static term => $"\"{term}\""))
            : SourceBackedLabel(language, "la demande", "the request", "la solicitud", "o pedido", "die Anfrage", "la richiesta");

        var answer = SourceBackedLabel(
            language,
            $"Je n'ai pas trouvÃ© de passage qui relie explicitement {targetList} Ã  {optionKindList}. Je liste donc ces Ã©lÃ©ments documentÃ©s Ã  vÃ©rifier, pas comme compatibilitÃ© certifiÃ©e.",
            $"I did not find a passage that explicitly connects {targetList} to {optionKindList}. I therefore list these items as documented options to verify, not as certified compatibility.",
            $"No he encontrado un pasaje que conecte explicitamente {targetList} con {optionKindList}. Por eso enumero estos elementos como pistas con fuente, no como compatibilidad certificada.",
            $"Nao encontrei uma passagem que ligue explicitamente {targetList} a {optionKindList}. Por isso listo estes elementos como pistas com fonte, nao como compatibilidade certificada.",
            $"Ich habe keine Stelle gefunden, die {targetList} ausdruecklich mit {optionKindList} verbindet. Deshalb liste ich diese Punkte als belegte Hinweise, nicht als bestaetigte Kompatibilitaet.",
            $"Non ho trovato un passaggio che colleghi esplicitamente {targetList} a {optionKindList}. Li elenco quindi come indicazioni con fonte, non come compatibilita certificata.");
        return AppendBroadenedSearchOfferIfHelpful(answer, query, language);
    }
}
