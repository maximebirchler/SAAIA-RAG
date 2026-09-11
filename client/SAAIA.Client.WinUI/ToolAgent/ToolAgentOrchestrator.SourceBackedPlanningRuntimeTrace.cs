using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string[] BuildSourceBackedPlanningRuntimeTraceLines(
        ToolResults toolResults,
        string? query,
        string language)
    {
        language = NormalizeLanguageCode(language);
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(query);
        var hasStructuredAxes = DetectRequestedDayAxisLabels(query, language).Count > 0
            && DetectRequestedPlanningSlotAxisLabels(query, language).Count > 0;
        var minimumSources = ResolveMinimumSourceBackedPlanningCandidateCount(query, targetSlots, hasStructuredAxes);
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var trace = new List<string>
        {
            $"trace_path=rag.planning.runtime|trace_step=scope|stage=scope|target_slots={targetSlots}|minimum_sources={minimumSources}|structured_axes={FormatPlanningTraceBool(hasStructuredAxes)}|strict={FormatPlanningTraceBool(strictStructuredPlanning)}"
        };

        AppendPlanningTraceQueries(trace, "primary", BuildPlanningRetrievalQueries(query ?? string.Empty));
        AppendPlanningTraceQueries(trace, "exploration", BuildPlanningExplorationRetrievalQueries(query ?? string.Empty));

        var normalizedQuery = query ?? string.Empty;
        var sourcePageLimit = Math.Clamp(Math.Max(targetSlots, minimumSources), 12, 80);
        var sourcePages = EnumerateRagHitSummaries(toolResults)
            .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
            .Where(static hit => !LooksLikePageReferenceOnlyHit(hit))
            .Where(hit => !LooksLikeLowSignalAppFeatureHit(hit, normalizedQuery))
            .Where(hit => !strictStructuredPlanning
                || ShouldExposeHitForStrictSourceBackedPlanningInventory(hit, normalizedQuery))
            .OrderByDescending(static hit => BackendSelectionHintsPreferUsableEvidence(hit) ? 1 : 0)
            .ThenByDescending(static hit => hit.MatchedContentCards?.Count ?? 0)
            .ThenByDescending(static hit => GetBestRagEvidenceText(hit).Length)
            .ThenByDescending(static hit => hit.Score)
            .GroupBy(BuildRagHitVisiblePageMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(sourcePageLimit)
            .ToList();

        var distinctSourcePages = sourcePages
            .Select(BuildRagHitVisiblePageMergeKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        trace.Add(
            "trace_path=rag.planning.runtime|trace_step=source_inventory|stage=source_inventory"
            + $"|source_pages={sourcePages.Count}"
            + $"|distinct_source_pages={distinctSourcePages}"
            + $"|top_titles={FormatPlanningTraceValue(string.Join("; ", sourcePages.Take(12).Select(hit => BuildSourceBackedPlanningRuntimeTraceTitle(hit, query))))}");

        foreach (var hit in sourcePages.Take(80))
        {
            var pageKey = BuildRagHitVisiblePageMergeKey(hit);
            trace.Add(
                "trace_path=rag.planning.runtime|trace_step=source_page|stage=source_page"
                + "|decision=exposed_to_llm"
                + $"|page_key={FormatPlanningTraceValue(pageKey)}"
                + $"|retrieval_query={FormatPlanningTraceValue(hit.RetrievalQuery)}"
                + $"|title={FormatPlanningTraceValue(BuildSourceBackedPlanningRuntimeTraceTitle(hit, query))}"
                + $"|doc={FormatPlanningTraceValue(hit.DocPath)}"
                + $"|page={hit.PageStart}"
                + $"|score={hit.Score}"
                + $"|evidence_chars={GetBestRagEvidenceText(hit).Length}"
                + $"|cards={hit.MatchedContentCards?.Count ?? 0}"
                + $"|backend_usable={FormatPlanningTraceBool(BackendSelectionHintsPreferUsableEvidence(hit))}");
        }

        var requiredSourcePageCount = strictStructuredPlanning
            ? Math.Min(targetSlots, Math.Max(1, minimumSources))
            : 1;
        trace.Add(
            "trace_path=rag.planning.runtime|trace_step=summary|stage=summary"
            + $"|exposed_source_pages={sourcePages.Count}"
            + $"|distinct_source_pages={distinctSourcePages}"
            + $"|required_source_pages={requiredSourcePageCount}"
            + $"|adequate_inventory={FormatPlanningTraceBool(sourcePages.Count >= minimumSources && distinctSourcePages >= requiredSourcePageCount)}");

        return trace.ToArray();
    }

    private static string BuildSourceBackedPlanningRuntimeTraceTitle(RagHitSummary hit)
    {
        var title = CollapseWhitespace(hit.SectionTitle ?? hit.HeadingPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        title = hit.MatchedContentCards?
            .Select(static card => CollapseWhitespace(card.Title))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        title = FormatReadableEvidenceExcerpt(GetBestRagEvidenceText(hit), maxLength: 100);
        return string.IsNullOrWhiteSpace(title)
            ? "source-backed context"
            : title;
    }

    private static string BuildSourceBackedPlanningRuntimeTraceTitle(RagHitSummary hit, string? query)
    {
        var title = BuildSourceBackedPlanningRuntimeTraceTitle(hit);
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            return title;

        var normalizedTitle = NormalizeLexicalLookup(title);
        var candidate = new SourceBackedOptionCandidate(
            hit,
            title,
            Score: 0,
            VisibleMinutes: ExtractBestVisibleDurationMinutes(hit));
        var titleNeedsReplacement = string.IsNullOrWhiteSpace(normalizedTitle)
            || LooksLikeRequestedPlanningSlotAxisLabelCandidate(candidate, query)
            || LooksLikeNoisyStructuredPlanningCandidateTitle(title)
            || LooksLikeStrictStructuredPlanningInventoryPageTitleNoise(normalizedTitle);
        if (!titleNeedsReplacement
            || LooksLikeDocumentaryPlanningContextSourcePage(hit, title, query))
        {
            return title;
        }

        return ExtractStrictSourceBackedOptionTitles(hit, query)
            .FirstOrDefault(IsStrictSourceBackedPlanningInventoryCandidateTitle)
            ?? title;
    }

    private static string[] BuildSourceBackedPlanningTraceLines(
        ToolResults toolResults,
        string? query,
        string language)
    {
        language = NormalizeLanguageCode(language);
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(query);
        var hasStructuredAxes = DetectRequestedDayAxisLabels(query, language).Count > 0
            && DetectRequestedPlanningSlotAxisLabels(query, language).Count > 0;
        var minimumCandidates = ResolveMinimumSourceBackedPlanningCandidateCount(query, targetSlots, hasStructuredAxes);
        var strictStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var trace = new List<string>
        {
            $"trace_path=rag.planning.candidate_trace|trace_step=scope|stage=scope|target_slots={targetSlots}|minimum_candidates={minimumCandidates}|structured_axes={FormatPlanningTraceBool(hasStructuredAxes)}|strict={FormatPlanningTraceBool(strictStructuredPlanning)}"
        };

        AppendPlanningTraceQueries(trace, "primary", BuildPlanningRetrievalQueries(query ?? string.Empty));
        AppendPlanningTraceQueries(trace, "exploration", BuildPlanningExplorationRetrievalQueries(query ?? string.Empty));

        var acceptedPool = SelectSourceBackedPlanningCandidates(
                toolResults,
                query,
                Math.Max(64, targetSlots),
                language)
            .ToList();
        trace.Add(
            "trace_path=rag.planning.candidate_trace|trace_step=candidate_pool|stage=candidate_pool"
            + $"|raw_candidates={acceptedPool.Count}"
            + $"|top_titles={FormatPlanningTraceValue(string.Join("; ", acceptedPool.Take(12).Select(static candidate => candidate.Title)))}");
        var optionPool = SelectSourceBackedOptionCandidates(
                toolResults,
                query,
                keepOverRequestedDuration: true,
                language: language,
                allowPartialStructuredPlanningCandidates: !strictStructuredPlanning)
            .ToList();
        var acceptedPoolKeys = acceptedPool
            .Select(BuildSourceBackedPlanningCandidateKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        trace.Add(
            "trace_path=rag.planning.candidate_trace|trace_step=option_candidate_pool|stage=option_candidate_pool"
            + $"|candidates={optionPool.Count}"
            + $"|not_in_planning_pool={optionPool.Count(candidate => !acceptedPoolKeys.Contains(BuildSourceBackedPlanningCandidateKey(candidate)))}"
            + $"|top_titles={FormatPlanningTraceValue(string.Join("; ", optionPool.Take(12).Select(static candidate => candidate.Title)))}");
        var optionDominantTopLevelScope = strictStructuredPlanning
            ? TryInferDominantTopLevelCategoryScope(toolResults, query)
            : null;
        foreach (var optionCandidate in optionPool
                     .Where(candidate => !acceptedPoolKeys.Contains(BuildSourceBackedPlanningCandidateKey(candidate)))
                     .Take(24))
        {
            var reason = ExplainSourceBackedPlanningCandidateRejection(
                optionCandidate,
                query,
                strictStructuredPlanning,
                requireStrictStructuredEvidence: true,
                dominantTopLevelScope: optionDominantTopLevelScope);
            trace.Add(
                "trace_path=rag.planning.candidate_trace|trace_step=option_candidate.not_in_planning_pool|stage=option_candidate"
                + "|decision=not_in_planning_pool"
                + $"|reason={FormatPlanningTraceValue(string.IsNullOrWhiteSpace(reason) ? "ranked_or_deduplicated_out" : reason)}"
                + $"|candidate_key={FormatPlanningTraceValue(BuildSourceBackedPlanningCandidateKey(optionCandidate))}"
                + $"|strict_evidence={FormatPlanningTraceBool(HasStrictStructuredPlanningCandidateEvidence(optionCandidate))}"
                + $"|direct_evidence={FormatPlanningTraceBool(HasDirectSourceBackedPlanningCandidateEvidence(optionCandidate))}"
                + $"|retrieval_query={FormatPlanningTraceValue(optionCandidate.Hit.RetrievalQuery)}"
                + BuildSourceBackedPlanningCandidateEvidenceDiagnostics(optionCandidate)
                + $"|title={FormatPlanningTraceValue(optionCandidate.Title)}"
                + $"|doc={FormatPlanningTraceValue(optionCandidate.Hit.DocPath)}"
                + $"|page={optionCandidate.Hit.PageStart}"
                + $"|score={optionCandidate.Score}");
        }
        var accepted = strictStructuredPlanning
            ? SelectPageDiverseSourceBackedPlanningCandidates(
                    acceptedPool,
                    Math.Max(targetSlots, minimumCandidates),
                    query)
                .ToList()
            : acceptedPool;
        if (hasStructuredAxes)
        {
            var periodLabels = DetectRequestedPlanningSlotAxisLabels(query, language);
            var routeAwareGrid = BuildStructuredSourceBackedSlotAwareGrid(
                accepted,
                periodLabels,
                targetSlots,
                query,
                allowSourcedRotation: false,
                requireDistinctItems: true,
                out var routeAwareFit);
            trace.Add(
                "trace_path=rag.planning.candidate_trace|trace_step=slot_fit|stage=slot_fit"
                + $"|assigned_slots={routeAwareFit.AssignedSlots}"
                + $"|required_slots={targetSlots}"
                + $"|route_evidence={FormatPlanningTraceBool(routeAwareFit.HasRouteEvidence)}"
                + $"|routed_pool={routeAwareFit.RoutedPool}"
                + $"|neutral_pool={routeAwareFit.NeutralPool}"
                + $"|primary_pools={FormatPlanningTraceValue(FormatStructuredPlanningSlotPoolCounts(periodLabels, routeAwareFit.PrimaryPools))}"
                + $"|alternative_pools={FormatPlanningTraceValue(FormatStructuredPlanningSlotPoolCounts(periodLabels, routeAwareFit.AlternativePools))}");
        }
        var acceptedKeys = accepted
            .Select(BuildSourceBackedPlanningCandidateKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in accepted.Take(80))
        {
            var pageKey = BuildRagHitVisiblePageMergeKey(candidate.Hit);
            trace.Add(
                "trace_path=rag.planning.candidate_trace|trace_step=candidate.accepted|stage=candidate"
                + "|decision=accepted"
                + "|reason=selected"
                + $"|candidate_key={FormatPlanningTraceValue(BuildSourceBackedPlanningCandidateKey(candidate))}"
                + $"|page_key={FormatPlanningTraceValue(pageKey)}"
                + $"|strict_evidence={FormatPlanningTraceBool(HasStrictStructuredPlanningCandidateEvidence(candidate))}"
                + $"|direct_evidence={FormatPlanningTraceBool(HasDirectSourceBackedPlanningCandidateEvidence(candidate))}"
                + $"|retrieval_query={FormatPlanningTraceValue(candidate.Hit.RetrievalQuery)}"
                + BuildSourceBackedPlanningCandidateEvidenceDiagnostics(candidate)
                + $"|title={FormatPlanningTraceValue(candidate.Title)}"
                + $"|doc={FormatPlanningTraceValue(candidate.Hit.DocPath)}"
                + $"|page={candidate.Hit.PageStart}"
                + $"|score={candidate.Score}");
        }

        var inspected = 0;
        var rejectionReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in EnumerateRagHitSummaries(toolResults).Take(120))
        {
            inspected++;
            var title = ExtractSourceBackedOptionTitle(hit, query);
            var candidate = new SourceBackedOptionCandidate(
                hit,
                title,
                ComputeSourceBackedOptionHitScore(
                    hit,
                    title,
                    query,
                    requestedMaxMinutes: null,
                    ExtractBestVisibleDurationMinutes(hit)),
                ExtractBestVisibleDurationMinutes(hit));
            var key = BuildSourceBackedPlanningCandidateKey(candidate);
            if (acceptedKeys.Contains(key))
                continue;

            var pageKey = BuildRagHitVisiblePageMergeKey(hit);
            var rejectionReason = ExplainSourceBackedPlanningCandidateRejection(candidate, query, strictStructuredPlanning);
            rejectionReasons.TryGetValue(rejectionReason, out var rejectionCount);
            rejectionReasons[rejectionReason] = rejectionCount + 1;
            trace.Add(
                "trace_path=rag.planning.candidate_trace|trace_step=candidate.rejected|stage=candidate"
                + "|decision=rejected"
                + $"|reason={rejectionReason}"
                + $"|candidate_key={FormatPlanningTraceValue(key)}"
                + $"|page_key={FormatPlanningTraceValue(pageKey)}"
                + $"|strict_evidence={FormatPlanningTraceBool(HasStrictStructuredPlanningCandidateEvidence(candidate))}"
                + $"|direct_evidence={FormatPlanningTraceBool(HasDirectSourceBackedPlanningCandidateEvidence(candidate))}"
                + $"|retrieval_query={FormatPlanningTraceValue(hit.RetrievalQuery)}"
                + BuildSourceBackedPlanningCandidateEvidenceDiagnostics(candidate)
                + $"|title={FormatPlanningTraceValue(title)}"
                + $"|doc={FormatPlanningTraceValue(hit.DocPath)}"
                + $"|page={hit.PageStart}"
                + $"|score={candidate.Score}");
        }

        if (rejectionReasons.Count > 0)
        {
            trace.Add(
                "trace_path=rag.planning.candidate_trace|trace_step=rejection_summary|stage=rejection_summary"
                + "|"
                + string.Join(
                    '|',
                    rejectionReasons
                        .OrderByDescending(static pair => pair.Value)
                        .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(static pair => $"{pair.Key}={pair.Value}")));
        }

        var coverage = EvaluateSourceBackedPlanningCoverage(toolResults, query, language);
        trace.Add(
            "trace_path=rag.planning.candidate_trace|trace_step=summary|stage=summary"
            + $"|inspected_hits={inspected}"
            + $"|accepted_candidates={accepted.Count}"
            + $"|distinct_source_pages={coverage.DistinctSourcePages}"
            + $"|adequate={FormatPlanningTraceBool(coverage.IsAdequate)}");

        return trace.ToArray();
    }

    private static string BuildSourceBackedPlanningCandidateEvidenceDiagnostics(SourceBackedOptionCandidate candidate)
    {
        var title = NormalizeLexicalLookup(candidate.Title);
        var evidence = NormalizeLexicalLookup(BuildPageLocalSourceBackedPlanningProofText(candidate.Hit));
        var terms = ExtractPlanningAnswerSupportTerms(title)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var matchedTerms = terms.Count(term => evidence.Contains(term, StringComparison.Ordinal));
        var relaxedProof = !string.IsNullOrWhiteSpace(title)
            && PrimaryEvidenceContainsRelaxedLocalStructuredPlanningProof(candidate.Hit, title, evidence);
        var concreteTitle = !string.IsNullOrWhiteSpace(title)
            && LooksLikeConcreteStructuredPlanningCandidateNormalizedTitle(title);
        var anchoredContextProof = !string.IsNullOrWhiteSpace(title)
            && SourceBackedContextCandidateHasAnchoredFuzzyLocalStructuredProof(candidate, title);
        var anchoredCardProof = !string.IsNullOrWhiteSpace(title)
            && HasPageAnchoredContentCardStructuredPlanningProof(candidate.Hit, title);
        var fieldValueReason = ExplainStructuredPlanningFieldValueCandidateRejection(candidate, title);
        var primarySectionOwner = false;
        var primaryLeadingTitle = false;
        var cardAnchorKindCount = 0;
        var cardEvidenceTitleCount = 0;
        var cardAnchorCandidateCount = 0;
        var cardSignalBridgeCount = 0;
        var cardSignalBridgeTermCount = 0;
        var cardSignalBridgePageHitCount = 0;
        if (!string.IsNullOrWhiteSpace(title))
        {
            var rawPageEvidence = BuildPageLocalStructuredPlanningEvidenceText(candidate.Hit);
            var normalizedPageEvidence = NormalizeLexicalLookup(rawPageEvidence);
            primaryLeadingTitle = CandidateTitleHasLeadingOccurrenceBeforeStructuredFields(title, normalizedPageEvidence);
            primarySectionOwner = CandidateTitleOwnsLocalStructuredPlanningSection(title, normalizedPageEvidence);
            foreach (var card in candidate.Hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
            {
                if (ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card))
                    cardAnchorKindCount++;
                if (ContentCardCarriesStructuredPlanningEvidence(card, title))
                    cardEvidenceTitleCount++;
                if (ContentCardCanAnchorStructuredPlanningTitleWithoutVisiblePageTitle(
                        candidate.Hit,
                        card,
                        title,
                        rawPageEvidence,
                        normalizedPageEvidence)
                    || ContentCardCanBridgeClippedStructuredPlanningTitle(
                        candidate.Hit,
                        card,
                        title,
                        rawPageEvidence,
                        normalizedPageEvidence)
                    || ContentCardSignalsAnchorTitleToStructuredPageBody(
                        card,
                        title,
                        rawPageEvidence,
                        normalizedPageEvidence))
                {
                    cardAnchorCandidateCount++;
                }

                var signalTerms = ExtractContentCardStructuredPlanningSignalBridgeTerms(card, title);
                if (signalTerms.Length == 0)
                    continue;

                var signalPageHits = CountContentCardSignalBridgePageHits(signalTerms, normalizedPageEvidence);
                cardSignalBridgeTermCount = Math.Max(cardSignalBridgeTermCount, signalTerms.Length);
                cardSignalBridgePageHitCount = Math.Max(cardSignalBridgePageHitCount, signalPageHits);
                if (ContentCardSignalsAnchorTitleToStructuredPageBody(
                        card,
                        title,
                        rawPageEvidence,
                        normalizedPageEvidence))
                {
                    cardSignalBridgeCount++;
                }
            }
        }
        return "|evidence_trace_path=rag.planning.candidate_evidence"
            + "|evidence_trace_step=diagnose"
            + "|proof_chars=" + evidence.Length.ToString(CultureInfo.InvariantCulture)
            + "|title_terms=" + terms.Length.ToString(CultureInfo.InvariantCulture)
            + "|matched_title_terms=" + matchedTerms.ToString(CultureInfo.InvariantCulture)
            + "|exact_title_in_proof=" + FormatPlanningTraceBool(!string.IsNullOrWhiteSpace(title) && evidence.Contains(title, StringComparison.Ordinal))
            + "|concrete_title=" + FormatPlanningTraceBool(concreteTitle)
            + "|exact_structured_proof=" + FormatPlanningTraceBool(PrimaryPageEvidenceContainsExactPlanningCandidateTitle(candidate))
            + "|relaxed_structured_proof=" + FormatPlanningTraceBool(relaxedProof)
            + "|anchored_context_proof=" + FormatPlanningTraceBool(anchoredContextProof)
            + "|anchored_card_proof=" + FormatPlanningTraceBool(anchoredCardProof)
            + "|card_anchor_kind_count=" + cardAnchorKindCount.ToString(CultureInfo.InvariantCulture)
            + "|card_evidence_title_count=" + cardEvidenceTitleCount.ToString(CultureInfo.InvariantCulture)
            + "|card_anchor_candidate_count=" + cardAnchorCandidateCount.ToString(CultureInfo.InvariantCulture)
            + "|card_signal_bridge_count=" + cardSignalBridgeCount.ToString(CultureInfo.InvariantCulture)
            + "|card_signal_bridge_terms=" + cardSignalBridgeTermCount.ToString(CultureInfo.InvariantCulture)
            + "|card_signal_page_hits=" + cardSignalBridgePageHitCount.ToString(CultureInfo.InvariantCulture)
            + "|strong_proof_text=" + FormatPlanningTraceBool(HasStrongLocalStructuredPlanningProofText(evidence))
            + "|usable_candidate=" + FormatPlanningTraceBool(IsUsableSourceBackedPlanningCandidate(candidate))
            + "|page_context_label=" + FormatPlanningTraceBool(LooksLikePageContextLabelPlanningCandidate(candidate))
            + "|field_label=" + FormatPlanningTraceBool(LooksLikeGenericStructuredFieldLabelCandidate(candidate))
            + "|field_value_reason=" + FormatPlanningTraceValue(fieldValueReason ?? string.Empty)
            + "|primary_leading_title=" + FormatPlanningTraceBool(primaryLeadingTitle)
            + "|primary_section_owner=" + FormatPlanningTraceBool(primarySectionOwner)
            + "|supporting_field=" + FormatPlanningTraceBool(LooksLikeSupportingStructuredPlanningFieldValueCandidate(candidate))
            + "|embedded_field=" + FormatPlanningTraceBool(LooksLikeEmbeddedStructuredPlanningFieldValueCandidate(candidate))
            + "|noisy_title=" + FormatPlanningTraceBool(LooksLikeNoisyStructuredPlanningCandidateTitle(candidate.Title))
            + "|procedure_title=" + FormatPlanningTraceBool(LooksLikeProcedureSentenceTitle(title));
    }

    private void LogSourceBackedPlanningTrace(
        string context,
        ToolResults toolResults,
        string? query,
        string language)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        if (!LooksLikeAnyDocumentaryPlanningRequest(query)
            && !ShouldGateStructuredSourceBackedPlanningCoverage(query))
        {
            return;
        }

        var lines = BuildSourceBackedPlanningRuntimeTraceLines(toolResults, query, language);
        var safeContext = FormatPlanningTraceValue(context);
        ClientLog.Info($"ToolAgent planning trace begin: context={safeContext}|lines={lines.Length}");
        EmitRagTrace(
            "planning.trace.begin",
            ("trace_path", "rag.planning.runtime"),
            ("trace_step", "begin"),
            ("context", safeContext),
            ("lines", lines.Length),
            ("query", query));

        foreach (var line in lines.Take(160))
        {
            ClientLog.Info($"ToolAgent planning trace: context={safeContext}|{line}");
            EmitRagTrace(
                "planning.trace.line",
                ("trace_path", "rag.planning.runtime"),
                ("trace_step", "line"),
                ("context", safeContext),
                ("line", line));
        }

        if (lines.Length > 160)
        {
            ClientLog.Info($"ToolAgent planning trace truncated: context={safeContext}|remaining={lines.Length - 160}");
            EmitRagTrace(
                "planning.trace.truncated",
                ("trace_path", "rag.planning.runtime"),
                ("trace_step", "truncated"),
                ("context", safeContext),
                ("remaining", lines.Length - 160));
        }
    }

    private static void AppendPlanningTraceQueries(List<string> trace, string pass, IEnumerable<string> queries)
    {
        var index = 0;
        foreach (var query in queries)
        {
            index++;
            trace.Add($"trace_path=rag.planning.retrieval_query|trace_step={pass}.query|stage=query|pass={pass}|index={index}|value={FormatPlanningTraceValue(query)}");
        }
    }
}
