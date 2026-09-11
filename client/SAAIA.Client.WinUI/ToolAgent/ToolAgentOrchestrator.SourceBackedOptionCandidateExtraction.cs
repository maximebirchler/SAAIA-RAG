using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IEnumerable<SourceBackedOptionCandidate> BuildSourceBackedOptionCandidatesFromHit(
        RagHitSummary hit,
        string? query,
        string language,
        int? scoreMaxMinutes,
        bool requiresNamedEntityEvidence,
        IReadOnlyList<string> namedEntityTerms,
        bool allowPartialStructuredPlanningCandidates = false)
    {
        var requiresStructuredPlanning = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        var requiresStrictStructuredPlanning = requiresStructuredPlanning && !allowPartialStructuredPlanningCandidates;
        var emittedCardCandidate = false;
        foreach (var card in hit.MatchedContentCards ?? Array.Empty<RagHitContentCardSummary>())
        {
            LogSourceBackedCardCandidateInspect(hit, card, requiresStructuredPlanning);
            var titleVariants = ExtractSourceBackedCardTitleVariants(card.Title);
            if (titleVariants.Count == 0)
            {
                LogSourceBackedCardCandidateNoVariant(hit, card.Title, requiresStructuredPlanning);
            }

            foreach (var title in titleVariants)
            {
                var canBypassNoisyContextTitle = SourceBackedContextTitleCanBypassNoisyStructuredPlanningTitle(hit, title);
                if (!IsUsableSourceBackedOptionTitle(title)
                    && !canBypassNoisyContextTitle)
                {
                    LogSourceBackedCardCandidateSkip(hit, title, "unusable_title", requiresStructuredPlanning);
                    continue;
                }

                if (requiresStrictStructuredPlanning
                    && LooksLikeNoisyStructuredPlanningCandidateTitle(title)
                    && !canBypassNoisyContextTitle)
                {
                    LogSourceBackedCardCandidateSkip(hit, title, "noisy_title", requiresStructuredPlanning);
                    continue;
                }

                if (requiresStrictStructuredPlanning)
                {
                    var rawPageEvidence = BuildPageLocalStructuredPlanningEvidenceText(hit);
                    if (ContentCardTitleLooksLikeStructuredFieldValueInPrimaryPage(
                            hit,
                            card,
                            NormalizeLexicalLookup(title),
                            rawPageEvidence,
                            NormalizeLexicalLookup(rawPageEvidence)))
                    {
                        LogSourceBackedCardCandidateSkip(hit, title, "supporting_field_value", requiresStructuredPlanning);
                        continue;
                    }
                }

                if (requiresStrictStructuredPlanning
                    && !ContentCardHasPageLocalStructuredPlanningProof(hit, card, title))
                {
                    LogSourceBackedCardCandidateProofState(hit, card, title, requiresStructuredPlanning);
                    LogSourceBackedCardCandidateSkip(hit, title, "missing_page_local_card_proof", requiresStructuredPlanning);
                    continue;
                }

                var scopedHit = BuildSourceBackedCardScopedHit(
                    hit,
                    card,
                    title,
                    strictStructuredPlanning: requiresStrictStructuredPlanning);
                var visibleMinutes = ExtractBestVisibleDurationMinutes(scopedHit);
                var scopedCandidate = new SourceBackedOptionCandidate(
                    scopedHit,
                    title,
                    Score: ComputeSourceBackedOptionHitScore(scopedHit, title, query, scoreMaxMinutes, visibleMinutes) + 6,
                    visibleMinutes);
                if (requiresStrictStructuredPlanning)
                {
                    if (!HasStrictStructuredPlanningCandidateEvidence(scopedCandidate))
                    {
                        LogSourceBackedCardCandidateSkip(hit, title, "missing_strict_candidate_evidence", requiresStructuredPlanning);
                        continue;
                    }

                    scopedCandidate = BoostStrictStructuredPlanningCandidate(scopedCandidate);
                }

                emittedCardCandidate = true;
                yield return scopedCandidate;
            }
        }

        if (emittedCardCandidate && !requiresStrictStructuredPlanning)
            yield break;

        var fallbackTitles = requiresStrictStructuredPlanning
            ? ExtractStrictSourceBackedOptionTitles(hit, query)
            : new[] { ExtractSourceBackedOptionTitle(hit, query) };
        var fallbackTitleIsGeneratedFromNamedEntityEvidence = false;
        if (fallbackTitles.Count == 0
            && requiresNamedEntityEvidence
            && !requiresStructuredPlanning
            && QueryAnchorTermsMatchHit(namedEntityTerms, hit))
        {
            fallbackTitles = new[] { BuildSourceBackedFallbackOptionTitle(hit, language) };
            fallbackTitleIsGeneratedFromNamedEntityEvidence = true;
        }

        foreach (var fallbackTitle in fallbackTitles)
        {
            var canBypassNoisyContextTitle = SourceBackedContextTitleCanBypassNoisyStructuredPlanningTitle(hit, fallbackTitle);
            if (!string.IsNullOrWhiteSpace(fallbackTitle)
                && !IsUsableSourceBackedOptionTitle(fallbackTitle)
                && !canBypassNoisyContextTitle
                && !fallbackTitleIsGeneratedFromNamedEntityEvidence)
            {
                continue;
            }

            if (requiresStrictStructuredPlanning
                && LooksLikeNoisyStructuredPlanningCandidateTitle(fallbackTitle)
                && !canBypassNoisyContextTitle)
            {
                continue;
            }

            var fallbackVisibleMinutes = ExtractBestVisibleDurationMinutes(hit);
            var fallbackCandidate = new SourceBackedOptionCandidate(
                hit,
                fallbackTitle,
                ComputeSourceBackedOptionHitScore(hit, fallbackTitle, query, scoreMaxMinutes, fallbackVisibleMinutes),
                fallbackVisibleMinutes);
            if (requiresStrictStructuredPlanning)
            {
                if (!HasStrictStructuredPlanningCandidateEvidence(fallbackCandidate))
                    continue;

                fallbackCandidate = BoostStrictStructuredPlanningCandidate(fallbackCandidate);
            }

            yield return fallbackCandidate;
        }
    }

    private static void LogSourceBackedCardCandidateSkip(
        RagHitSummary hit,
        string? title,
        string reason,
        bool enabled)
    {
        if (!enabled)
            return;

        ClientLog.Info(
            "ToolAgent option candidate selection: trace_path=rag.source_backed.card_candidate|trace_step=skip|stage=card_candidate.skip"
            + $"|reason={FormatPlanningTraceValue(reason)}"
            + $"|title={FormatPlanningTraceValue(title)}"
            + $"|doc={FormatPlanningTraceValue(hit.DocPath)}"
            + $"|page={hit.PageStart}");
    }

    private static void LogSourceBackedCardCandidateInspect(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        bool enabled)
    {
        if (!enabled)
            return;

        ClientLog.Info(
            "ToolAgent option candidate selection: trace_path=rag.source_backed.card_candidate|trace_step=inspect|stage=card_candidate.inspect"
            + $"|title={FormatPlanningTraceValue(card.Title)}"
            + $"|kind={FormatPlanningTraceValue(card.Kind)}"
            + $"|doc={FormatPlanningTraceValue(hit.DocPath)}"
            + $"|page={hit.PageStart}");
    }

    private static void LogSourceBackedCardCandidateNoVariant(
        RagHitSummary hit,
        string? rawTitle,
        bool enabled)
    {
        if (!enabled)
            return;

        var cleaned = CleanSourceBackedOptionTitle(rawTitle);
        var normalized = NormalizeLexicalLookup(cleaned);
        ClientLog.Info(
            "ToolAgent option candidate selection: trace_path=rag.source_backed.card_candidate|trace_step=skip_title_variant|stage=card_candidate.skip"
            + "|reason=no_usable_title_variant"
            + $"|rawTitle={FormatPlanningTraceValue(rawTitle)}"
            + $"|cleaned={FormatPlanningTraceValue(cleaned)}"
            + $"|useful={FormatPlanningTraceBool(IsUsefulSourceBackedDisplayTitle(cleaned))}"
            + $"|planNoise={FormatPlanningTraceBool(LooksLikePlanItemNoise(cleaned))}"
            + $"|weak={FormatPlanningTraceBool(LooksLikeWeakSourceBackedOptionTitle(cleaned))}"
            + $"|procedure={FormatPlanningTraceBool(LooksLikeProcedureSentenceTitle(normalized))}"
            + $"|doc={FormatPlanningTraceValue(hit.DocPath)}"
            + $"|page={hit.PageStart}");
    }

    private static void LogSourceBackedCardCandidateProofState(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string? title,
        bool enabled)
    {
        if (!enabled)
            return;

        var normalizedTitle = NormalizeLexicalLookup(title);
        var rawPageEvidence = BuildPageLocalStructuredPlanningEvidenceText(hit);
        var normalizedPageEvidence = NormalizeLexicalLookup(rawPageEvidence);
        var signalBridgeTerms = string.IsNullOrWhiteSpace(normalizedTitle)
            ? Array.Empty<string>()
            : ExtractContentCardStructuredPlanningSignalBridgeTerms(card, normalizedTitle);
        var signalBridgePageHits = CountContentCardSignalBridgePageHits(signalBridgeTerms, normalizedPageEvidence);
        ClientLog.Info(
            "ToolAgent option candidate selection: trace_path=rag.planning.card_candidate|trace_step=proof_state|stage=card_candidate.proof_state"
            + $"|title={FormatPlanningTraceValue(title)}"
            + $"|kind={FormatPlanningTraceValue(card.Kind)}"
            + $"|explicitPage={FormatPlanningTraceBool(SourceBackedContentCardHasExplicitPageAnchor(card))}"
            + $"|anchorKind={FormatPlanningTraceBool(ContentCardKindLooksLikeStructuredPlanningTitleAnchor(card))}"
            + $"|pageChars={normalizedPageEvidence.Length}"
            + $"|titleInPage={FormatPlanningTraceBool(!string.IsNullOrWhiteSpace(normalizedTitle) && normalizedPageEvidence.Contains(normalizedTitle, StringComparison.Ordinal))}"
            + $"|tightTitleTerms={FormatPlanningTraceBool(!string.IsNullOrWhiteSpace(normalizedTitle) && PrimaryContentTermsTightlySupportPlanningTitle(normalizedTitle, normalizedPageEvidence))}"
            + $"|strongPageProof={FormatPlanningTraceBool(HasStrongLocalStructuredPlanningProofText(rawPageEvidence))}"
            + $"|pageBodyCue={FormatPlanningTraceBool(PageEvidenceHasStructuredBodyProofCue(rawPageEvidence))}"
            + $"|pageCardCue={FormatPlanningTraceBool(PageEvidenceHasStructuredCardProofCue(rawPageEvidence))}"
            + $"|cardEvidence={FormatPlanningTraceBool(!string.IsNullOrWhiteSpace(normalizedTitle) && ContentCardCarriesStructuredPlanningEvidence(card, normalizedTitle))}"
            + $"|cardSourceAnchor={FormatPlanningTraceBool(!string.IsNullOrWhiteSpace(normalizedTitle) && ContentCardSourceEvidenceCanAnchorStructuredPlanningTitle(card, normalizedTitle))}"
            + $"|cardBodyEvidence={FormatPlanningTraceBool(ContentCardCarriesStructuredPlanningBodyEvidence(card))}"
            + $"|cardBodyTermsSupported={FormatPlanningTraceBool(ContentCardStructuredEvidenceBodyTermsAreSupportedByPrimaryPageText(hit, card, rawPageEvidence, normalizedPageEvidence))}"
            + $"|cardBodyProof={FormatPlanningTraceBool(!string.IsNullOrWhiteSpace(normalizedTitle) && ContentCardHasPageAnchoredStructuredBodyProof(hit, card, normalizedTitle, rawPageEvidence, normalizedPageEvidence))}"
            + $"|pageFieldValue={FormatPlanningTraceBool(!string.IsNullOrWhiteSpace(normalizedTitle) && ContentCardTitleLooksLikeStructuredFieldValueInPrimaryPage(hit, card, normalizedTitle, rawPageEvidence, normalizedPageEvidence))}"
            + $"|clippedBridge={FormatPlanningTraceBool(!string.IsNullOrWhiteSpace(normalizedTitle) && ContentCardCanBridgeClippedStructuredPlanningTitle(hit, card, normalizedTitle, rawPageEvidence, normalizedPageEvidence))}"
            + $"|signalBridge={FormatPlanningTraceBool(!string.IsNullOrWhiteSpace(normalizedTitle) && ContentCardSignalsAnchorTitleToStructuredPageBody(card, normalizedTitle, rawPageEvidence, normalizedPageEvidence))}"
            + $"|signalTerms={signalBridgeTerms.Length}"
            + $"|signalPageHits={signalBridgePageHits}"
            + $"|navNoise={FormatPlanningTraceBool(LooksLikeStructuredPlanningNavigationOrIndexNoise(rawPageEvidence))}"
            + $"|doc={FormatPlanningTraceValue(hit.DocPath)}"
            + $"|page={hit.PageStart}");
    }

    private static SourceBackedOptionCandidate BoostStrictStructuredPlanningCandidate(SourceBackedOptionCandidate candidate)
    {
        var evidenceScore = ComputeSourceBackedEvidenceRichnessScore(candidate.Hit);
        var strictEvidenceFloor = 20 + Math.Min(12, Math.Max(0, evidenceScore));
        return candidate.Score >= strictEvidenceFloor
            ? candidate
            : candidate with { Score = strictEvidenceFloor };
    }

    private static string ExtractStrictSourceBackedOptionTitle(RagHitSummary hit, string? query)
        => ExtractStrictSourceBackedOptionTitles(hit, query).FirstOrDefault() ?? string.Empty;

    private static IReadOnlyList<string> ExtractStrictSourceBackedOptionTitles(RagHitSummary hit, string? query)
    {
        var normalizedEvidence = NormalizeLexicalLookup(CollapseWhitespace(string.Join(
            ' ',
            new[]
            {
                hit.SectionTitle,
                hit.HeadingPath,
                hit.Excerpt,
                hit.FullText,
                hit.ContextualSnippet
            }.Where(static part => !string.IsNullOrWhiteSpace(part)))));
        if (hit.MatchedContentCards is not { Count: > 0 }
            && LooksLikeExplicitlyNonConcreteSourceEvidence(normalizedEvidence))
        {
            return Array.Empty<string>();
        }

        var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
        var candidates = ExtractPageLocalStructuredPlanningTitleCandidates(hit)
            .Concat(EnumeratePlanExtractionTexts(hit)
            .SelectMany(ExtractPlanItemTitleCandidatesV2)
            )
            .Concat(ExtractSourceBackedTitleCandidates(hit))
            .Select(CleanSourceBackedOptionTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Where(title => IsUsableSourceBackedOptionTitle(title)
                || SourceBackedContextTitleCanBypassNoisyStructuredPlanningTitle(hit, title))
            .Where(title => !LooksLikeNoisyStructuredPlanningCandidateTitle(title)
                || SourceBackedContextTitleCanBypassNoisyStructuredPlanningTitle(hit, title))
            .Where(title => IsStrictSourceBackedPlanningInventoryCandidateTitle(title)
                || SourceBackedContextTitleCanBypassNoisyStructuredPlanningTitle(hit, title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((title, index) => new
            {
                Title = title,
                Index = index,
                Candidate = new SourceBackedOptionCandidate(
                    hit,
                    title,
                    Score: ComputeSourceBackedOptionHitScore(hit, title, query, requestedMaxMinutes: null, visibleMinutes: visibleMinutes),
                    VisibleMinutes: visibleMinutes)
            })
            .Where(item => HasStrictStructuredPlanningCandidateEvidence(item.Candidate))
            .Where(item => !LooksLikeGenericPlanningContextCandidate(item.Candidate))
            .OrderByDescending(item => item.Candidate.Score)
            .ThenByDescending(item => ComputeSourceBackedEvidenceRichnessScore(item.Candidate.Hit))
            .ThenBy(item => item.Index)
            .ToArray();

        return candidates
            .Select(static item => item.Title)
            .ToArray();
    }

    private static IEnumerable<string> ExtractPageLocalStructuredPlanningTitleCandidates(RagHitSummary hit)
    {
        const string structureLabelPattern =
            @"components?|composants?|requirements?|exigences?|quantit(?:y|ies)|quantit[eÃ©]s?|values?|valeurs?|materials?|mat[eÃ©]riel|items?|[eÃ©]l[eÃ©]ments?|procedure|proc[eÃ©]dure|instructions?|method|m[eÃ©]thode|preparation|pr[eÃ©]paration|technique|operation|workflow|temps\s+total|total\s+time";
        var pageLocalStructureLabelPattern = structureLabelPattern;
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in EnumeratePlanExtractionTexts(hit))
        {
            foreach (Match match in Regex.Matches(
                         text,
                         $@"(?i)(?:^|[.!?]\s+)(?<title>[\p{{Lu}}\p{{Lt}}0-9][\p{{Lu}}\p{{Lt}}0-9 '&/,\-\u00c0-\u017f]{{5,90}}?)\s+[\p{{Lu}}\p{{Lt}}][\p{{L}}'\u2019\-]{{3,32}}\s*(?:[:*]|\u2022)\s*.{{0,240}}\b(?:{pageLocalStructureLabelPattern})\b",
                         RegexOptions.CultureInvariant))
            {
                var title = HumanizePlanItemTitleV2(match.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title)
                    && emitted.Add(title)
                    && !LooksLikePlanPageHeading(text, title)
                    && IsUsableSourceBackedOptionTitle(title))
                {
                    yield return title;
                }
            }

            foreach (Match match in Regex.Matches(
                         text,
                         $@"(?i)(?:^|[.!?]\s+)(?<title>\p{{Lu}}[\p{{L}}\p{{N}}'\u2019 &/,\-\u00c0-\u017f]{{3,90}}?)\s*[.:]\s*(?:{pageLocalStructureLabelPattern})\b",
                         RegexOptions.CultureInvariant))
            {
                var title = HumanizePlanItemTitleV2(match.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title)
                    && emitted.Add(title)
                    && !LooksLikePlanPageHeading(text, title)
                    && IsUsableSourceBackedOptionTitle(title))
                {
                    yield return title;
                }
            }
        }
    }

    private static RagHitSummary BuildSourceBackedCardScopedHit(
        RagHitSummary hit,
        RagHitContentCardSummary card,
        string? candidateTitle = null,
        bool strictStructuredPlanning = false)
    {
        var pageStart = card.PageStart ?? hit.PageStart;
        var pageEnd = card.PageEnd ?? card.PageStart ?? hit.PageEnd;
        if (pageEnd < pageStart)
            pageEnd = pageStart;

        var cardEvidence = BuildSourceBackedCardEvidenceSnippet(card);
        var strictCardEvidence = BuildSourceBackedCardStrictEvidenceSnippet(card);
        var hasExplicitCardPage = SourceBackedContentCardHasExplicitPageAnchor(card);
        var normalizedCardTitle = NormalizeLexicalLookup(candidateTitle ?? card.Title);
        var rawPageEvidence = strictStructuredPlanning
            ? BuildPageLocalStructuredPlanningEvidenceText(hit)
            : string.Empty;
        var normalizedPageEvidence = strictStructuredPlanning
            ? NormalizeLexicalLookup(rawPageEvidence)
            : string.Empty;
        var hasVerifiedCardPageEvidence = hasExplicitCardPage
            && ContentCardEvidenceIsSupportedByPrimaryPageText(hit, card, normalizedCardTitle);
        var hasAnchoredStructuredBodyProof = strictStructuredPlanning
            && ContentCardHasPageAnchoredStructuredBodyProof(
                hit,
                card,
                normalizedCardTitle,
                rawPageEvidence,
                normalizedPageEvidence);
        var hasAnchoredStructuredCardProof = strictStructuredPlanning
            && HasPageAnchoredContentCardStructuredPlanningProof(hit, card, normalizedCardTitle);
        var hasConcreteStructuredCardEvidence = strictStructuredPlanning
            && ContentCardCarriesStructuredPlanningEvidence(card, normalizedCardTitle)
            && ContentCardSourceEvidenceCanAnchorStructuredPlanningTitle(card, normalizedCardTitle);
        var hasSelfContainedStructuredCardProof = strictStructuredPlanning
            && ContentCardHasSelfContainedStructuredPlanningProof(card, normalizedCardTitle);
        var localPageEvidence = strictStructuredPlanning
            ? BuildSourceBackedPageLocalStructuredPlanningEvidenceWindow(hit, normalizedCardTitle)
            : string.Empty;
        if (strictStructuredPlanning
            && string.IsNullOrWhiteSpace(localPageEvidence)
            && (hasAnchoredStructuredCardProof || hasAnchoredStructuredBodyProof))
        {
            localPageEvidence = rawPageEvidence;
        }
        var scopedEvidenceParts = strictStructuredPlanning
            ? new[]
            {
                hasAnchoredStructuredCardProof && (hasConcreteStructuredCardEvidence || hasSelfContainedStructuredCardProof || hasAnchoredStructuredBodyProof) ? candidateTitle ?? card.Title : null,
                localPageEvidence,
                (hasSelfContainedStructuredCardProof
                    || hasAnchoredStructuredBodyProof
                    || (hasVerifiedCardPageEvidence
                        && HasConcreteContentCardEvidenceForTitle(card, normalizedCardTitle, requireExactTitle: true)))
                        ? strictCardEvidence
                        : null
            }
            : new[]
            {
                hit.Excerpt,
                hit.FullText,
                hasVerifiedCardPageEvidence ? cardEvidence : null,
                hasVerifiedCardPageEvidence ? strictCardEvidence : null
            };
        var scopedEvidence = CollapseWhitespace(string.Join(' ', scopedEvidenceParts
            .Where(static value => !string.IsNullOrWhiteSpace(value))));

        var scopedExcerpt = !string.IsNullOrWhiteSpace(scopedEvidence)
            ? scopedEvidence
            : strictStructuredPlanning ? string.Empty : hit.Excerpt ?? string.Empty;
        var scopedFullText = !string.IsNullOrWhiteSpace(scopedEvidence)
            ? scopedEvidence
            : strictStructuredPlanning ? scopedExcerpt : hit.FullText ?? scopedExcerpt;
        return hit with
        {
            PageStart = pageStart,
            PageEnd = pageEnd,
            Excerpt = scopedExcerpt,
            FullText = scopedFullText,
            ContextualSnippet = BuildSourceBackedCardContextSnippet(hit, card, cardEvidence),
            MatchedContentCards = new[] { card }
        };
    }
}
