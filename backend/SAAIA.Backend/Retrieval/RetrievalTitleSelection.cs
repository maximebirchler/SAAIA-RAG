using System.Globalization;
using System.Text.RegularExpressions;
using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalText;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalTitleSelection
{

    internal static void PrioritizeFinalSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var documentHintTokens = ExpandDocumentHintTokensFromCandidateDocs(
            query,
            selected,
            ExtractDocumentHintTokens(query));
        var lexicalTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var titleScoringQuery = BuildFocusedLexicalBackfillQuery(query);
        var titleTokens = ExtractTitleScoringTokens(
            string.IsNullOrWhiteSpace(titleScoringQuery) ? query : titleScoringQuery);
        var titlePlacementTokens = BuildTitlePlacementTokens(titleTokens, documentHintTokens);
        var unrequestedQualifiedTitleSubjectTokens =
            BuildUnrequestedQualifiedTitleSubjectTokens(query, titleTokens);
        var applyUnrequestedQualifiedTitlePenalty =
            ShouldPreferComparativeDocumentDiversity(query)
            && unrequestedQualifiedTitleSubjectTokens.Count is >= 1 and <= 3;
        var quotedPhrases = ExtractTitleLookupPhrases(query);
        var hasExplicitFileDocumentHint = HasExplicitDocumentNameHint(query);
        var focusedHeadAnchorTokens = ExtractFocusedLookupPrimaryHeadTokens(query);
        var useSpecificCoverageTitlePriority = ShouldApplySpecificCoverageTitlePriority(
            query,
            lexicalTokens,
            ExtractComparativeSubjectAnchorTokens(query));
        var profileConstraintTokens = BuildDocumentProfileSpecificityTokens(query);
        var prioritizeComparativeProfileCoverage =
            ShouldPreferComparativeDocumentDiversity(query)
            && ShouldAllowDocumentProfileAssistForComparativeLookup(query);
        var prioritizeComparativeOperandTitleSignal = ShouldUseBareVsComparativeOperandRoute(query);
        var prioritizeReferenceVersionOperandTitleSignal = ShouldUseReferenceVersionOperandRoute(query);
        var normalizedQueryForOperationalSettings = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        var prioritizeOperationalSettings = ShouldPrioritizeOperationalSettings(normalizedQueryForOperationalSettings);
        var prioritizeFastDuration = ContainsFastDurationPreference(query);
        var structuredTitleLookup = IsStructuredTitleLookupQuery(query);
        var hasStructuredDetailIntent = HasStructuredContinuationBackfillIntent(query);
        var allowWeakContentCardTitleSignalWaiver = !ShouldPreferComparativeDocumentDiversity(query);
        var prioritizeSituationalConstraintCoverage =
            ShouldPrioritizeSituationalSelectionConstraintCoverage(query);
        var prioritizeFocusedSpecificContentCoverage =
            ShouldPrioritizeFocusedSpecificContentCoverage(query, lexicalTokens);
        var requiredFocusedSpecificContentCoverage = ResolveRequiredFocusedSpecificContentCoverage(lexicalTokens);
        var isStructuredQuotedTitleLookup = quotedPhrases.Count > 0
            && ShouldKeepQuotedTitleSearchOpenForStructuredDetails(query, normalizedQueryForOperationalSettings);
        var hasStructuredQuotedTitleDetailIntent = quotedPhrases.Count > 0
            && (isStructuredQuotedTitleLookup || HasStructuredContinuationBackfillIntent(query));
        var focusedSpecificDocumentCoverage = prioritizeFocusedSpecificContentCoverage
            ? selected
                .Where(static match => !string.IsNullOrWhiteSpace(match.DocPath))
                .GroupBy(static match => NormalizeRagDocPath(match.DocPath) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    group => CountSpecificLexicalAnchors(
                        lexicalTokens,
                        string.Join("\n", group
                            .Take(4)
                            .Select(GetDirectChunkSignalText))),
                    StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var ranked = selected
            .Select((match, index) =>
            {
                var exactTitleScore = ComputeExactTitleCandidateScore(query, match);
                var quotedTitleAnchorSignal = ComputeQuotedTitleAnchorSignal(query, match);
                var hasStructuredProcedureCue = ContainsStructuredProcedureCue(match);
                var hasQuotedTitlePlacementEvidence = quotedPhrases.Count > 0
                    && HasQuotedTitlePlacementEvidence(quotedPhrases, match);
                var directQuotedTitleTokenCoverage = ComputeDirectQuotedTitleTokenCoverage(quotedPhrases, match);
                var contentEvidencePriority = ComputeContentEvidencePriority(match);
                var rawWeakDirectTitleTokenRoute = IsDirectTitleTokenRouteMatch(match)
                    && !DirectTitleTokenRouteHasStrongLeadEvidence(match);
                var completeStructuredDirectTokenRoute =
                    IsDirectTitleTokenRouteMatch(match)
                    && directQuotedTitleTokenCoverage >= 0.99
                    && (LooksLikeStructuredAnswerChunk(match)
                        || (ContainsStructuredInventoryCue(match) && hasStructuredProcedureCue))
                    && EstimateNormalizedTokenCount(match.Text) >= 60
                    && (match.ContentDensityScore is null or >= 0.50);
                var weakDirectTitleTokenRoute = rawWeakDirectTitleTokenRoute
                    && !completeStructuredDirectTokenRoute;
                var directChunkTitleSignal = weakDirectTitleTokenRoute
                    ? 0
                    : ComputeDirectChunkTitleSignal(query, match);
                var matchedCardTitleSignal = ComputeMatchedContentCardTitleSignal(
                    match,
                    lexicalTokens,
                    NormalizeForLexicalSignal(query));
                var quotedMatchedCardTitleSignal = ComputeQuotedMatchedContentCardTitleSignal(quotedPhrases, match);
                var titlePlacementSignal = ComputeTitlePlacementSignal(query, titlePlacementTokens, match);
                var matchesDocumentHint = DocumentMatchesHint(documentHintTokens, match);
                var documentHintSubjectCoverage = matchesDocumentHint && titlePlacementTokens.Count > 0
                    ? CountSpecificLexicalAnchors(titlePlacementTokens, GetDirectChunkSignalText(match))
                    : 0;
                var usableDocumentHint = matchesDocumentHint
                    && (titlePlacementTokens.Count == 0
                        || titlePlacementSignal > 0
                        || documentHintSubjectCoverage > 0);
                var fullTitleCoverage = HasFullTitleAnchorCoverage(titleTokens, match);
                var directStructuredTitleRoutePriority =
                    IsDirectTitleTokenRouteMatch(match)
                    && (directQuotedTitleTokenCoverage >= 0.75
                        || fullTitleCoverage
                        || exactTitleScore > 0.0
                        || directChunkTitleSignal > 0)
                    && (LooksLikeStructuredAnswerChunk(match)
                        || ContainsStructuredInventoryCue(match)
                        || hasStructuredProcedureCue)
                        ? 1
                        : 0;
                var weakMatchedContentCardTitlePenalty = ComputeWeakMatchedContentCardTitlePenalty(
                    match,
                    fullTitleCoverage,
                    exactTitleScore,
                    directChunkTitleSignal,
                    quotedTitleAnchorSignal,
                    allowWeakContentCardTitleSignalWaiver);
                var singleSpecificTitleAnchorCoverage =
                    titleTokens.Count == 1
                    && IsPrimarySpecificLexicalAnchorToken(titleTokens[0])
                    && ContainsPrimarySpecificLexicalAnchor(titleTokens, GetTitleSignalText(match));
                var hasStrongTitleForOperationalSettings =
                    directChunkTitleSignal >= 3
                    || exactTitleScore > 0.0
                    || quotedTitleAnchorSignal > 0.0
                    || matchedCardTitleSignal > 0
                    || fullTitleCoverage;
                var structuredTitleAnswerPriority = ComputeStructuredTitleAnswerPriority(
                    structuredTitleLookup,
                    match,
                    exactTitleScore,
                    matchedCardTitleSignal,
                    quotedTitleAnchorSignal,
                    hasQuotedTitlePlacementEvidence,
                    directChunkTitleSignal);
                var hasReliableStructuredQuotedTitleEvidence =
                    quotedMatchedCardTitleSignal > 0
                    || titlePlacementSignal > 0
                    || directChunkTitleSignal > 0
                    || hasQuotedTitlePlacementEvidence
                    || (IsResolvedTitleOrNavigationRoute(match) && !IsWeakResolvedRouteTarget(match));
                var weakStructuredQuotedNavigationMentionPenalty =
                    hasStructuredQuotedTitleDetailIntent
                    && directStructuredTitleRoutePriority <= 0
                    && IsWeakStructuredQuotedNavigationMention(
                        match,
                        hasReliableStructuredQuotedTitleEvidence,
                        completeStructuredDirectTokenRoute);
                return new
                {
                    Match = match,
                    Index = index,
                    DirectChunkTitleSignal = directChunkTitleSignal,
                    ExactQuotedTitlePriority = ComputeExactQuotedTitlePlacementPriority(quotedPhrases, match),
                    PreciseQuotedTitleSpecificityPriority = ComputePreciseQuotedTitleSpecificityPriority(quotedPhrases, match),
                    ResolvedQuotedTitleRoutePriority = IsResolvedTitleOrNavigationRoute(match)
                        && hasQuotedTitlePlacementEvidence
                            ? 1
                            : 0,
                    DirectQuotedTitleTokenCoverage = directQuotedTitleTokenCoverage,
                    DirectStructuredTitleRoutePriority = directStructuredTitleRoutePriority,
                    CompleteStructuredDirectTokenRoute = completeStructuredDirectTokenRoute,
                    TrustedExactTitleSignal = exactTitleScore > 0.0 && !IsWeakResolvedRouteTarget(match),
                    StructuredTitleAnswerPriority = structuredTitleAnswerPriority,
                    WeakExactStructuredTitleMention = directStructuredTitleRoutePriority <= 0
                        && !completeStructuredDirectTokenRoute
                        && IsWeakExactStructuredTitleMention(
                            structuredTitleLookup,
                            match,
                            ResolveRetriever(match),
                            hasQuotedTitlePlacementEvidence,
                            structuredTitleAnswerPriority,
                            strongExactReferenceSignal: false),
                    MatchedCardTitleSignal = matchedCardTitleSignal,
                    WeakDirectTitleTokenRoute = weakDirectTitleTokenRoute
                        && quotedMatchedCardTitleSignal <= 0
                        && !(usableDocumentHint && (titlePlacementSignal > 0 || documentHintSubjectCoverage > 0)),
                    WeakMatchedContentCardTitlePenalty = weakMatchedContentCardTitlePenalty,
                    QuotedMatchedCardTitleSignal = quotedMatchedCardTitleSignal,
                    WeakStructuredQuotedNavigationMentionPenalty = weakStructuredQuotedNavigationMentionPenalty,
                    UnrequestedQualifiedTitlePenalty = applyUnrequestedQualifiedTitlePenalty
                        ? ComputeUnrequestedQualifiedTitlePenalty(unrequestedQualifiedTitleSubjectTokens, lexicalTokens, match)
                        : 0,
                    SituationalSelectionConstraintCoverage = prioritizeSituationalConstraintCoverage
                        ? ComputeSituationalSelectionConstraintCoverage(query, match)
                        : 0.0,
                    FocusedHeadAnchorSignal = ComputeFocusedHeadAnchorSignal(focusedHeadAnchorTokens, match),
                    OperationalSettingSignal = prioritizeOperationalSettings
                        && hasStrongTitleForOperationalSettings
                        ? ComputeOperationalSettingCandidateSignal(normalizedQueryForOperationalSettings, match)
                        : 0,
                    FastDurationPriority = prioritizeFastDuration
                        && HasFocusedTitlePhraseEvidenceForPreference(query, match)
                            ? ComputeFastDurationPreferencePriority(match)
                            : 0,
                    FullTitleCoverage = fullTitleCoverage,
                    BorrowedLinkedTitleCompanionPenalty = ShouldPenalizeBorrowedLinkedTitleAgainstResolvedRoute(
                        query,
                        match,
                        selected,
                        quotedPhrases,
                        hasQuotedTitlePlacementEvidence,
                        fullTitleCoverage),
                    LeadingLinkedContinuationPenalty = IsLinkedContextLeadingProcedureContinuation(query, match),
                    MatchesDocumentHint = DocumentMatchesHint(documentHintTokens, match),
                    SelectionPriorityBucket = ComputeSelectionPriorityBucket(match),
                    ContentEvidencePriority = contentEvidencePriority,
                    QualityPenalty = ComputeChunkExtractionQualityPenalty(match),
                    QuotedTitleAnchorSignal = quotedTitleAnchorSignal,
                    ExactTitleScore = exactTitleScore,
                    SpecificAnchorCount = lexicalTokens.Length > 0
                        ? CountSpecificLexicalAnchors(lexicalTokens, GetTitleSignalText(match))
                        : 0,
                    TitlePlacementSignal = titlePlacementSignal,
                    SpecificTitlePlacementSignal = titlePlacementTokens.Count >= 2 ? titlePlacementSignal : 0,
                    DocumentHintSubjectCoverage = documentHintSubjectCoverage,
                    FocusedSpecificContentCoverage = prioritizeFocusedSpecificContentCoverage
                        ? CountSpecificLexicalAnchors(lexicalTokens, GetDirectChunkSignalText(match))
                        : 0,
                    FocusedSpecificDocumentCoverage = prioritizeFocusedSpecificContentCoverage
                        && !string.IsNullOrWhiteSpace(match.DocPath)
                        && focusedSpecificDocumentCoverage.TryGetValue(NormalizeRagDocPath(match.DocPath) ?? string.Empty, out var docCoverage)
                            ? docCoverage
                            : 0,
                    StructuredCardCompletenessPriority =
                        structuredTitleLookup
                        && singleSpecificTitleAnchorCoverage
                        && ContainsStructuredInventoryCue(match)
                        && hasStructuredProcedureCue
                            ? 1
                            : 0,
                    StructuredProcedureDetailPriority =
                        hasStructuredDetailIntent
                        && hasStructuredProcedureCue
                        && CountSpecificLexicalAnchors(lexicalTokens, GetDirectChunkSignalText(match)) >= requiredFocusedSpecificContentCoverage
                            ? 1
                            : 0,
                    ComparativeProfileConstraintCoverage =
                        prioritizeComparativeProfileCoverage
                        && IsDocumentProfileMatch(match)
                            ? ComputeDocumentProfileSelectionConstraintCoverage(profileConstraintTokens, match)
                            : 0.0,
                    ComparativeOperandTitleSignal = prioritizeComparativeOperandTitleSignal
                        ? ComputeComparativeOperandTitleSignal(query, GetTitleSignalText(match))
                        : 0,
                    ReferenceVersionOperandTitleSignal = prioritizeReferenceVersionOperandTitleSignal
                        ? ComputeReferenceVersionOperandTitleSignal(query, GetTitleSignalText(match))
                        : 0,
                    UsableDocumentHint = usableDocumentHint,
                    StructuredAnswerPriority = GetStructuredAnswerPriority(match)
                };
            })
            .ToList();

        var hasExplicitFileDocumentHintMatch = hasExplicitFileDocumentHint
                                               && documentHintTokens.Count > 0
                                               && ranked.Any(static item => item.MatchesDocumentHint);

        var hasStrongQuotedTitleOrderingSignal = !isStructuredQuotedTitleLookup
            || ranked.Any(static item =>
                item.ExactQuotedTitlePriority > 0
                || item.PreciseQuotedTitleSpecificityPriority > 0
                || item.ResolvedQuotedTitleRoutePriority > 0
                || item.DirectChunkTitleSignal > 0
                || item.DirectQuotedTitleTokenCoverage >= 0.75
                || (item.QuotedTitleAnchorSignal > 0.0 && item.DirectQuotedTitleTokenCoverage >= 0.75));

        if (isStructuredQuotedTitleLookup && !hasStrongQuotedTitleOrderingSignal)
        {
            selected.Clear();
            selected.AddRange(ranked
                .OrderBy(static item => item.WeakStructuredQuotedNavigationMentionPenalty ? 1 : 0)
                .ThenByDescending(static item => item.SelectionPriorityBucket)
                .ThenBy(static item => item.LeadingLinkedContinuationPenalty ? 1 : 0)
                .ThenBy(static item => item.BorrowedLinkedTitleCompanionPenalty)
                .ThenBy(static item => item.QualityPenalty)
                .ThenByDescending(static item => item.Match.Score)
                .ThenByDescending(static item => item.ContentEvidencePriority)
                .ThenBy(static item => item.Index)
                .Select(static item => item.Match));
            return;
        }

        var hasSpecializedOrderingSignal = ranked.Any(item =>
            (prioritizeComparativeOperandTitleSignal && item.ComparativeOperandTitleSignal > 0)
            || (prioritizeReferenceVersionOperandTitleSignal && item.ReferenceVersionOperandTitleSignal > 0)
            || (prioritizeComparativeProfileCoverage && item.ComparativeProfileConstraintCoverage > 0.0)
            || item.ExactQuotedTitlePriority > 0
            || item.PreciseQuotedTitleSpecificityPriority > 0
            || item.ResolvedQuotedTitleRoutePriority > 0
            || item.DirectChunkTitleSignal > 0
            || item.TrustedExactTitleSignal
            || item.StructuredTitleAnswerPriority > 0
            || item.MatchedCardTitleSignal > 0
            || item.QuotedMatchedCardTitleSignal > 0
            || item.FocusedHeadAnchorSignal > 0
            || item.OperationalSettingSignal > 0
            || item.FastDurationPriority > 0
            || item.SituationalSelectionConstraintCoverage > 0.0
            || item.WeakMatchedContentCardTitlePenalty > 0
            || item.WeakStructuredQuotedNavigationMentionPenalty
            || item.UnrequestedQualifiedTitlePenalty > 0
            || item.UsableDocumentHint
            || item.TitlePlacementSignal > 0
            || item.CompleteStructuredDirectTokenRoute
            || item.LeadingLinkedContinuationPenalty
            || item.StructuredCardCompletenessPriority > 0
            || item.StructuredProcedureDetailPriority > 0
            || item.QuotedTitleAnchorSignal > 0.0
            || item.ExactQuotedTitlePriority > 0
            || item.FullTitleCoverage
            || item.ExactTitleScore > 0.0
            || (prioritizeFocusedSpecificContentCoverage
                && item.FocusedSpecificContentCoverage >= requiredFocusedSpecificContentCoverage
                && item.ContentEvidencePriority >= 0.70)
            || (prioritizeFocusedSpecificContentCoverage
                && item.FocusedSpecificDocumentCoverage >= requiredFocusedSpecificContentCoverage
                && item.ContentEvidencePriority >= 0.70)
            || (useSpecificCoverageTitlePriority
                && item.SpecificAnchorCount >= 2
                && item.StructuredAnswerPriority > 0));
        if (!hasSpecializedOrderingSignal)
        {
            if (!isStructuredQuotedTitleLookup)
            {
                return;
            }

            selected.Clear();
            selected.AddRange(ranked
                .OrderBy(static item => item.WeakStructuredQuotedNavigationMentionPenalty ? 1 : 0)
                .ThenByDescending(static item => item.SelectionPriorityBucket)
                .ThenBy(static item => item.LeadingLinkedContinuationPenalty ? 1 : 0)
                .ThenBy(static item => item.BorrowedLinkedTitleCompanionPenalty)
                .ThenBy(static item => item.QualityPenalty)
                .ThenByDescending(static item => item.Match.Score)
                .ThenByDescending(static item => item.ContentEvidencePriority)
                .ThenBy(static item => item.Index)
                .Select(static item => item.Match));
            return;
        }

        var hasDocumentHintMatch = documentHintTokens.Count > 0
                                   && ranked.Any(static item => item.UsableDocumentHint);
        var hasDocumentHintSubjectMatch = documentHintTokens.Count > 0
                                          && ranked.Any(static item => item.DocumentHintSubjectCoverage > 0);
        var hasComparativeDocumentProfileHintMatch =
            prioritizeComparativeProfileCoverage
            && documentHintTokens.Count > 0
            && ranked.Any(static item => item.MatchesDocumentHint && IsDocumentProfileMatch(item.Match));
        selected.Clear();
        selected.AddRange(ranked
            .OrderByDescending(item => hasExplicitFileDocumentHintMatch && item.MatchesDocumentHint ? 1 : 0)
            .ThenByDescending(item => hasComparativeDocumentProfileHintMatch && item.MatchesDocumentHint ? 1 : 0)
            .ThenByDescending(item => prioritizeReferenceVersionOperandTitleSignal ? item.ReferenceVersionOperandTitleSignal : 0)
            .ThenByDescending(item => prioritizeComparativeOperandTitleSignal ? item.ComparativeOperandTitleSignal : 0)
            .ThenByDescending(item => prioritizeComparativeProfileCoverage ? item.ComparativeProfileConstraintCoverage : 0.0)
            .ThenByDescending(item => prioritizeComparativeProfileCoverage && IsDocumentProfileMatch(item.Match) ? item.Match.Score : 0.0)
            .ThenBy(static item => item.WeakStructuredQuotedNavigationMentionPenalty ? 1 : 0)
            .ThenBy(static item => item.WeakExactStructuredTitleMention ? 1 : 0)
            .ThenBy(static item => item.LeadingLinkedContinuationPenalty ? 1 : 0)
            .ThenBy(static item => item.BorrowedLinkedTitleCompanionPenalty)
            .ThenBy(static item => item.WeakDirectTitleTokenRoute ? 1 : 0)
            .ThenBy(static item => item.WeakMatchedContentCardTitlePenalty)
            .ThenBy(static item => item.UnrequestedQualifiedTitlePenalty)
            .ThenByDescending(item => hasDocumentHintSubjectMatch ? item.DocumentHintSubjectCoverage : 0)
            .ThenByDescending(static item => item.DirectStructuredTitleRoutePriority)
            .ThenByDescending(static item => item.StructuredCardCompletenessPriority)
            .ThenByDescending(static item => item.PreciseQuotedTitleSpecificityPriority)
            .ThenByDescending(static item => item.ExactQuotedTitlePriority)
            .ThenByDescending(static item => item.ResolvedQuotedTitleRoutePriority)
            .ThenByDescending(static item => item.QuotedMatchedCardTitleSignal)
            .ThenByDescending(static item => item.SpecificTitlePlacementSignal)
            .ThenByDescending(static item => item.CompleteStructuredDirectTokenRoute ? item.DirectQuotedTitleTokenCoverage : 0.0)
            .ThenByDescending(static item => item.SituationalSelectionConstraintCoverage)
            .ThenByDescending(static item => item.FastDurationPriority)
            .ThenByDescending(static item => item.FocusedHeadAnchorSignal)
            .ThenByDescending(static item => item.TitlePlacementSignal)
            .ThenByDescending(static item => item.DirectChunkTitleSignal)
            .ThenByDescending(static item => item.OperationalSettingSignal)
            .ThenByDescending(static item => item.TrustedExactTitleSignal ? 1 : 0)
            .ThenByDescending(static item => item.QuotedTitleAnchorSignal)
            .ThenByDescending(static item => item.FullTitleCoverage ? 1 : 0)
            .ThenByDescending(static item => item.ExactTitleScore)
            .ThenByDescending(static item => item.StructuredProcedureDetailPriority)
            .ThenByDescending(item => prioritizeFocusedSpecificContentCoverage
                && item.FocusedSpecificDocumentCoverage >= requiredFocusedSpecificContentCoverage
                && item.ContentEvidencePriority >= 0.70
                    ? 1
                    : 0)
            .ThenByDescending(item => prioritizeFocusedSpecificContentCoverage
                ? item.FocusedSpecificDocumentCoverage
                : 0)
            .ThenByDescending(item => prioritizeFocusedSpecificContentCoverage
                && item.FocusedSpecificContentCoverage >= requiredFocusedSpecificContentCoverage
                && item.ContentEvidencePriority >= 0.70
                    ? 1
                    : 0)
            .ThenByDescending(item => prioritizeFocusedSpecificContentCoverage
                ? item.FocusedSpecificContentCoverage
                : 0)
            .ThenByDescending(item => hasDocumentHintMatch && item.UsableDocumentHint ? 1 : 0)
            .ThenByDescending(item => hasDocumentHintMatch && item.UsableDocumentHint ? item.SelectionPriorityBucket : 0)
            .ThenByDescending(static item => item.StructuredTitleAnswerPriority)
            .ThenByDescending(static item => item.MatchedCardTitleSignal)
            .ThenByDescending(static item => item.ExactQuotedTitlePriority)
            .ThenByDescending(static item => item.SelectionPriorityBucket)
            .ThenByDescending(item => useSpecificCoverageTitlePriority && item.SpecificAnchorCount >= 2 ? 1 : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.StructuredAnswerPriority : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.SpecificAnchorCount : 0)
            .ThenByDescending(static item => item.ContentEvidencePriority)
            .ThenBy(static item => item.QualityPenalty)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Match));
    }


    private static bool ShouldPenalizeBorrowedLinkedTitleAgainstResolvedRoute(
        string query,
        RagMatch match,
        IReadOnlyList<RagMatch> selected,
        IReadOnlyList<string> quotedPhrases,
        bool hasQuotedTitlePlacementEvidence,
        bool fullTitleCoverage)
    {
        if (!string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal))
            return false;
        var titlePhrases = quotedPhrases.Count > 0
            ? quotedPhrases
            : ExtractFocusedLookupPhrases(query);
        if (titlePhrases.Count == 0)
            return false;
        if (HasStandaloneQuotedTitlePlacementEvidence(titlePhrases, match.Text)
            || HasLocalTitleTokenLeadEvidence(match.Text, sectionTitle: null, headingPath: null, titlePhrases))
        {
            return false;
        }

        foreach (var candidate in selected)
        {
            if (ReferenceEquals(candidate, match)
                || string.Equals(candidate.ChunkId, match.ChunkId, StringComparison.Ordinal)
                || string.Equals(ResolveRetriever(candidate), "linked_context", StringComparison.Ordinal)
                || !string.Equals(candidate.DocPath, match.DocPath, StringComparison.OrdinalIgnoreCase)
                || GetStructuredAnswerPriority(candidate) < 2
                || ComputeContentEvidencePriority(candidate) < 0.80)
            {
                continue;
            }

            var candidateRouteTitle = ExtractMatchedRouteOrProfileTitle(candidate.EmbedText);
            if (string.IsNullOrWhiteSpace(candidateRouteTitle)
                || ComputeQuotedLookupCandidateScore(titlePhrases, candidateRouteTitle) <= 0.0)
            {
                continue;
            }
            if (!HasStandaloneQuotedTitlePlacementEvidence(titlePhrases, candidate.Text)
                && !HasLocalTitleTokenLeadEvidence(candidate.Text, sectionTitle: null, headingPath: null, titlePhrases))
            {
                continue;
            }

            if (match.PageStart.HasValue
                && candidate.PageStart.HasValue
                && Math.Abs(match.PageStart.Value - candidate.PageStart.Value) > 2)
            {
                continue;
            }

            if (ComputeQuotedTitleAnchorSignal(query, candidate) > 0.0
                || HasQuotedTitlePlacementEvidence(titlePhrases, candidate)
                || ComputeDirectQuotedTitleTokenCoverage(titlePhrases, candidate) >= 0.75)
            {
                return true;
            }
        }

        return false;
    }


    internal static void PrioritizeOperationalSettingSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var normalizedQuery = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        if (!ShouldPrioritizeOperationalSettings(normalizedQuery))
            return;

        var ranked = selected
            .Select((match, index) => new
            {
                Match = match,
                Index = index,
                SettingSignal = ComputeOperationalSettingCandidateSignal(normalizedQuery, match)
            })
            .ToList();

        if (!ranked.Any(static item => item.SettingSignal > 0))
            return;

        selected.Clear();
        selected.AddRange(ranked
            .OrderByDescending(static item => item.SettingSignal)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Match));
    }


    internal static bool ContainsOperationalSettingLookupIntent(string normalized)
        => ContainsAny(
            normalized,
            " robot ",
            " machine ",
            " appareil ",
            " appliance ",
            " device ",
            " parametre ",
            " parametres ",
            " parameter ",
            " parameters ",
            " reglage ",
            " reglages ",
            " setting ",
            " settings ",
            " vitesse ",
            " vitesses ",
            " speed ",
            " speeds ",
            " temperature ",
            " temperatures ",
            " programme ",
            " programmes ",
            " program ",
            " programs ",
            " mode ",
            " modes ",
            " accessoire ",
            " accessoires ",
            " accessory ",
            " accessories ");


    private static bool ShouldPrioritizeOperationalSettings(string normalized)
    {
        if (!ContainsOperationalSettingLookupIntent(normalized))
            return false;

        if (!ContainsFastDurationPreferenceNormalized(normalized))
            return true;

        return ContainsAny(
            normalized,
            " robot ",
            " machine ",
            " appareil ",
            " appliance ",
            " device ",
            " parametre ",
            " parametres ",
            " parameter ",
            " parameters ",
            " reglage ",
            " reglages ",
            " setting ",
            " settings ",
            " vitesse ",
            " vitesses ",
            " speed ",
            " speeds ",
            " temperature ",
            " temperatures ",
            " programme ",
            " programmes ",
            " program ",
            " programs ",
            " accessoire ",
            " accessoires ",
            " accessory ",
            " accessories ");
    }


    internal static bool ContainsFastDurationPreference(string query)
        => ContainsFastDurationPreferenceNormalized($" {NormalizeForLexicalSignal(query)} ");


    private static bool ContainsFastDurationPreferenceNormalized(string normalized)
        => ContainsAny(
            normalized,
            " rapide ",
            " rapidement ",
            " vite ",
            " express ",
            " quick ",
            " quickly ",
            " fast ",
            " speedy ",
            " rapido ");


    internal static bool HasFocusedTitlePhraseEvidenceForPreference(string query, RagMatch match)
    {
        var phraseCandidates = ExtractFocusedLookupPhrases(query)
            .Concat(ExtractTitleLookupPhrases(query))
            .Append(BuildFocusedLexicalBackfillQuery(query))
            .Select(NormalizeForLexicalSignal)
            .SelectMany(static phrase => new[] { phrase, StripFastPreferenceFacetFromTitlePhrase(phrase) })
            .Where(static phrase => phrase.Length >= 5)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (phraseCandidates.Length == 0)
            return false;

        var signalText = NormalizeForLexicalSignal(string.Join(' ', new[]
        {
            GetTitleSignalText(match),
            match.Text
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));
        if (signalText.Length == 0)
            return false;

        return phraseCandidates.Any(phrase =>
            ContainsOrderedPhraseWindowInNormalizedText(signalText, phrase, maxGapChars: 16));
    }


    private static string StripFastPreferenceFacetFromTitlePhrase(string normalizedPhrase)
    {
        var withoutMode = SafeRegexReplace(
            normalizedPhrase,
            @"\b(?:en\s+)?mode\s+(?:rapide|express|quick|fast|rapido)\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        return SafeRegexReplace(
                withoutMode,
                @"\b(?:rapide|rapidement|vite|express|quick|quickly|fast|speedy|rapido)\b.*$",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100))
            .Trim();
    }


    internal static int ComputeFastDurationPreferencePriority(RagMatch match)
    {
        var minutes = ExtractBestDurationMinutes(GetDirectChunkSignalText(match));
        if (minutes is null)
            return 0;

        return minutes.Value switch
        {
            <= 10 => 5,
            <= 20 => 4,
            <= 35 => 3,
            <= 60 => 2,
            <= 90 => 1,
            _ => 0
        };
    }


    private static int? ExtractBestDurationMinutes(string? text)
    {
        var normalized = NormalizeForLexicalSignal(text);
        if (normalized.Length == 0)
            return null;

        var totalDurations = ExtractDurationMinutes(
            normalized,
            @"\b(?:temps\s+total|total\s+time|duree\s+totale)\s*:?\s*(?:(?<h>\d{1,2})\s*h(?:eures?)?\s*)?(?<m>\d{1,3})?\s*(?:min|minute|minutes)?\b");
        if (totalDurations.Count > 0)
            return totalDurations.Min();

        var durations = ExtractDurationMinutes(
            normalized,
            @"\b(?:(?<h>\d{1,2})\s*h(?:eures?)?(?:\s*(?<m>\d{1,3})\s*(?:min|minute|minutes))?|(?<m>\d{1,3})\s*(?:min|minute|minutes))\b");
        return durations.Count == 0 ? null : durations.Min();
    }


    private static List<int> ExtractDurationMinutes(string normalized, string pattern)
    {
        var values = new List<int>();
        foreach (var match in SafeRegexMatches(
            normalized,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100)))
        {
            var hours = 0;
            var minutes = 0;
            if (match.Groups["h"].Success
                && int.TryParse(match.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHours))
            {
                hours = parsedHours;
            }
            if (match.Groups["m"].Success
                && int.TryParse(match.Groups["m"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMinutes))
            {
                minutes = parsedMinutes;
            }

            var total = (hours * 60) + minutes;
            if (total > 0)
                values.Add(total);
        }

        return values;
    }


    private static int ComputeOperationalSettingCandidateSignal(string normalizedQuery, RagMatch match)
    {
        var normalizedText = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(string.Join(' ', new[]
        {
            match.Text,
            match.EmbedText,
            match.SectionTitle,
            match.HeadingPath,
            match.DocName,
            match.DocPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value))))).ToLowerInvariant()} ";

        if (string.IsNullOrWhiteSpace(normalizedText))
            return 0;

        var signal = 0;
        if (ContainsAny(normalizedQuery, " robot ", " machine ", " appareil ", " appliance ", " device ")
            && ContainsAny(normalizedText, " robot ", " machine ", " appareil ", " appliance ", " device "))
        {
            signal += 5;
        }

        if (ContainsAny(normalizedText, " vitesse ", " vitesses ", " speed ", " speeds "))
            signal += 4;
        if (ContainsAny(normalizedText, " temperature ", " temperatures ", " degre ", " degres ")
            || ContainsCelsiusLikeSetting(normalizedText))
        {
            signal += 3;
        }
        if (ContainsAny(normalizedText, " programme ", " programmes ", " program ", " programs ", " mode ", " modes "))
            signal += 3;
        if (ContainsAny(normalizedText, " accessoire ", " accessoires ", " accessory ", " accessories ", " attachment ", " attachments "))
            signal += 2;
        if (ContainsAny(normalizedText, " temps total ", " total time ", " duree totale "))
            signal += 2;

        return signal + Math.Min(GetStructuredAnswerPriority(match), 2);
    }


    private static bool ContainsCelsiusLikeSetting(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;

        for (var i = 0; i < normalizedText.Length; i++)
        {
            if (!char.IsDigit(normalizedText[i]))
                continue;

            var start = i;
            var value = 0;
            var digitCount = 0;
            while (i < normalizedText.Length && char.IsDigit(normalizedText[i]) && digitCount < 4)
            {
                value = (value * 10) + (normalizedText[i] - '0');
                digitCount++;
                i++;
            }

            if (digitCount is < 2 or > 3 || value < 10 || value > 999)
                continue;

            while (i < normalizedText.Length && normalizedText[i] == ' ')
                i++;

            if (i >= normalizedText.Length || normalizedText[i] != 'c')
                continue;

            var before = start == 0 ? ' ' : normalizedText[start - 1];
            var after = i + 1 >= normalizedText.Length ? ' ' : normalizedText[i + 1];
            if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after))
                return true;
        }

        return false;
    }


    internal static void PrioritizeExactTitleSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var lexicalTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var useSpecificCoverageTitlePriority = ShouldApplySpecificCoverageTitlePriority(
            query,
            lexicalTokens,
            ExtractComparativeSubjectAnchorTokens(query));
        var ranked = selected
            .Select(match =>
            {
                var exactTitleScore = ComputeExactTitleCandidateScore(query, match);
                var weakDirectTitleTokenRoute = IsDirectTitleTokenRouteMatch(match)
                    && !DirectTitleTokenRouteHasStrongLeadEvidence(match);
                return new
                {
                    Match = match,
                    ExactTitleScore = exactTitleScore,
                    DirectChunkTitleSignal = weakDirectTitleTokenRoute
                        ? 0
                        : ComputeDirectChunkTitleSignal(query, match),
                    MatchedCardTitleSignal = ComputeMatchedContentCardTitleSignal(
                        match,
                        lexicalTokens,
                        NormalizeForLexicalSignal(query)),
                    TrustedExactTitleSignal = exactTitleScore > 0.0 && !IsWeakResolvedRouteTarget(match),
                    SpecificAnchorCount = lexicalTokens.Length > 0
                        ? CountSpecificLexicalAnchors(lexicalTokens, GetTitleSignalText(match))
                        : 0,
                    StructuredAnswerPriority = GetStructuredAnswerPriority(match),
                    ContentEvidencePriority = ComputeContentEvidencePriority(match)
                };
            })
            .ToList();

        var hasExactTitleSignal = ranked.Any(static item => item.ExactTitleScore > 0.0 || item.MatchedCardTitleSignal > 0);
        var hasFullSpecificCoverage = useSpecificCoverageTitlePriority
            && ranked.Any(static item => item.SpecificAnchorCount >= 2);
        if (!hasExactTitleSignal && !hasFullSpecificCoverage)
            return;

        selected.Clear();
        selected.AddRange(ranked
            .OrderByDescending(static item => item.DirectChunkTitleSignal)
            .ThenByDescending(static item => item.TrustedExactTitleSignal ? 1 : 0)
            .ThenByDescending(static item => item.MatchedCardTitleSignal)
            .ThenByDescending(static item => item.ExactTitleScore > 0.0 ? 1 : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority && item.SpecificAnchorCount >= 2 ? 1 : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.StructuredAnswerPriority : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.SpecificAnchorCount : 0)
            .ThenByDescending(static item => item.ExactTitleScore)
            .ThenByDescending(static item => item.ContentEvidencePriority)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .Select(static item => item.Match));
    }


    internal static void PruneWeakTitleExpansionSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        if (lexicalTokens.Length < 2)
            return;

        var useTitlePriority = ExtractQuotedLookupPhrases(query).Count > 0
            && !ShouldTreatQuotedLookupAsTopic(query)
            || ShouldApplySpecificCoverageTitlePriority(
                query,
                lexicalTokens,
                ExtractComparativeSubjectAnchorTokens(query));
        if (!useTitlePriority)
            return;

        var ranked = selected
            .Select(match => new TitleSelectionSignal(
                match,
                ComputeExactTitleCandidateScore(query, match),
                CountSpecificLexicalAnchors(lexicalTokens, GetTitleSignalText(match)),
                HasFullTitleAnchorCoverage(lexicalTokens, match),
                GetStructuredAnswerPriority(match)))
            .ToList();

        var requiredAnchorCount = Math.Min(2, lexicalTokens.Length);
        var strongAnchors = ranked
            .Where(item => item.SpecificAnchorCount >= requiredAnchorCount
                && item.FullTitleCoverage
                && item.StructuredAnswerPriority >= 2)
            .ToArray();
        if (strongAnchors.Length == 0)
        {
            var first = ranked.FirstOrDefault();
            if (first is null
                || first.StructuredAnswerPriority < 2
                || first.SpecificAnchorCount <= 0
                || first.Match.Score < 0.90)
            {
                return;
            }

            strongAnchors = [first];
        }

        var pruned = ranked
            .Where(item => !ShouldPruneWeakTitleExpansion(item, strongAnchors, lexicalTokens))
            .Select(static item => item.Match)
            .ToList();

        if (pruned.Count == selected.Count || pruned.Count == 0)
            return;

        selected.Clear();
        selected.AddRange(pruned);
    }


    private static bool ShouldPruneWeakTitleExpansion(
        TitleSelectionSignal item,
        IReadOnlyList<TitleSelectionSignal> strongAnchors,
        IReadOnlyList<string> lexicalTokens)
    {
        if (item.FullTitleCoverage)
        {
            if (string.Equals(ResolveRetriever(item.Match), "linked_context", StringComparison.Ordinal))
            {
                return !strongAnchors.Any(anchor =>
                    IsUsefulLinkedContextCompanion(lexicalTokens, anchor.Match, item.Match));
            }

            return false;
        }

        var retriever = ResolveRetriever(item.Match);
        if (string.Equals(retriever, "linked_context", StringComparison.Ordinal))
        {
            return !strongAnchors.Any(anchor =>
                IsUsefulLinkedContextCompanion(lexicalTokens, anchor.Match, item.Match));
        }

        foreach (var anchor in strongAnchors)
        {
            if (!string.Equals(anchor.Match.DocPath, item.Match.DocPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (SharesSpecificSection(anchor.Match, item.Match))
                return false;
            if (IsNearbyPage(anchor.Match, item.Match, maxDistance: 2))
                return true;
        }

        return false;
    }


    internal static bool HasFullTitleAnchorCoverage(IReadOnlyList<string> lexicalTokens, RagMatch match)
    {
        if (lexicalTokens.Count < 2)
            return false;

        var titleSignalText = GetTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(titleSignalText))
            return false;

        return ContainsOrderedTitleTokenSubstrings(titleSignalText, lexicalTokens, maxGapChars: 100)
            || ContainsTitleLikeLexicalSequence(
                FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(titleSignalText)),
                lexicalTokens);
    }


    internal static bool IsNearbyPage(RagMatch anchor, RagMatch candidate, int maxDistance)
    {
        if (!anchor.PageStart.HasValue || !candidate.PageStart.HasValue)
            return false;

        return Math.Abs(anchor.PageStart.Value - candidate.PageStart.Value) <= maxDistance;
    }


    internal static bool IsUsefulLinkedContextCompanion(
        IReadOnlyList<string> lexicalTokens,
        RagMatch anchor,
        RagMatch candidate)
    {
        if (!string.Equals(ResolveRetriever(candidate), "linked_context", StringComparison.Ordinal))
            return false;
        if (!string.Equals(anchor.DocPath, candidate.DocPath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IsNearbyPage(anchor, candidate, maxDistance: 2))
            return false;
        if (LooksLikeNavigationalChunk(candidate) || LooksLikeGlossaryChunk(candidate))
            return false;
        if (IsNearDuplicatePageOverlap(anchor, candidate))
            return false;

        if (SharesSpecificSection(anchor, candidate))
            return true;
        if (lexicalTokens.Count == 0)
            return false;

        var directSignalText = GetDirectChunkSignalText(candidate);
        if (string.IsNullOrWhiteSpace(directSignalText))
            return false;

        if (ComputeLexicalCoverage(lexicalTokens, directSignalText) >= 0.50)
            return true;

        var specificAnchors = CountSpecificLexicalAnchors(lexicalTokens, directSignalText);
        if (specificAnchors >= Math.Min(2, lexicalTokens.Count))
            return true;

        var titleTokens = lexicalTokens
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Take(6)
            .ToArray();
        return titleTokens.Length >= 2
            && ContainsOrderedTitleTokenSubstrings(directSignalText, titleTokens, maxGapChars: 80);
    }


    internal static bool SharesSpecificSection(RagMatch left, RagMatch right)
    {
        if (!string.IsNullOrWhiteSpace(left.SectionTitle)
            && !string.IsNullOrWhiteSpace(right.SectionTitle)
            && !IsGenericSectionTitle(left.SectionTitle)
            && string.Equals(left.SectionTitle.Trim(), right.SectionTitle.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(left.HeadingPath)
            && !string.IsNullOrWhiteSpace(right.HeadingPath)
            && !IsGenericSectionTitle(left.HeadingPath)
            && string.Equals(left.HeadingPath.Trim(), right.HeadingPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }


    internal static bool IsGenericSectionTitle(string? value)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(value ?? string.Empty));
        return string.IsNullOrWhiteSpace(normalized)
            || normalized is "document" or "documents" or "root" or "page" or "pages" or "untitled" or "sans titre";
    }


    internal static void PruneWeakAdjacentSiblingSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        if (lexicalTokens.Length < 2)
            return;

        var anchor = selected.FirstOrDefault(match =>
            match.Score >= 0.90
            && !string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal)
            && LooksLikeStructuredAnswerChunk(match)
            && CountSpecificLexicalAnchors(lexicalTokens, GetTitleSignalText(match)) > 0);
        if (anchor is null)
            return;

        selected.RemoveAll(match =>
        {
            if (string.Equals(match.ChunkId, anchor.ChunkId, StringComparison.Ordinal)
                && string.Equals(match.DocPath, anchor.DocPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(match.DocPath, anchor.DocPath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (SharesSpecificSection(anchor, match))
                return false;
            if (!IsNearbyPage(anchor, match, maxDistance: 2))
                return false;
            if (string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal)
                && IsUsefulLinkedContextCompanion(lexicalTokens, anchor, match))
            {
                return false;
            }

            var coverage = ComputeLexicalCoverage(lexicalTokens, GetTitleSignalText(match));
            return coverage < 0.80;
        });
    }


    internal static void PrunePreciseTitleTailSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1 || !ShouldConstrainPreciseTitleLookup(query))
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        var requiredAnchorCount = Math.Min(2, lexicalTokens.Length);
        var ranked = selected
            .Select(match => new
            {
                Match = match,
                StrictCoverage = HasStrictTitleAnchorCoverage(lexicalTokens, match),
                TitleAnchorCount = CountTitleAnchorTokens(lexicalTokens, GetPreciseTitleSignalText(match)),
                ExactTitleScore = ComputeExactTitleCandidateScore(query, match),
                DirectChunkTitleSignal = ComputeDirectChunkTitleSignal(query, match),
                StructuredAnswerPriority = GetStructuredAnswerPriority(match)
            })
            .ToList();
        var anchor = ranked
            .Where(item => item.Match.Score >= 0.90
                && item.TitleAnchorCount >= requiredAnchorCount
                && (item.StrictCoverage
                    || item.ExactTitleScore > 0.0
                    || item.StructuredAnswerPriority >= 2
                    || HasProfileTitleHint(item.Match)))
            .OrderByDescending(static item => item.StrictCoverage)
            .ThenByDescending(static item => item.DirectChunkTitleSignal)
            .ThenByDescending(static item => item.ExactTitleScore)
            .ThenByDescending(static item => item.StructuredAnswerPriority)
            .ThenByDescending(static item => item.Match.Score)
            .Select(static item => item.Match)
            .FirstOrDefault();
        if (anchor is null)
            return;

        var anchorIsContentChunk = GetStructuredAnswerPriority(anchor) >= 2;
        selected.RemoveAll(match =>
        {
            if (IsSameChunk(match, anchor))
                return false;

            if (string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal)
                && IsUsefulLinkedContextCompanion(lexicalTokens, anchor, match))
            {
                return false;
            }

            return !HasStrictTitleAnchorCoverage(lexicalTokens, match)
                || (anchorIsContentChunk && string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal));
        });
    }


    internal static void PrunePreciseTitleLocalNeighborhoodSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1 || ShouldPreferComparativeDocumentDiversity(query) || ContainsDocumentOverviewIntent(query))
            return;

        var titlePhrases = ExtractTitleLookupPhrases(query);
        if (titlePhrases.Count == 0)
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        if (lexicalTokens.Length < 2)
            return;

        var anchors = selected
            .Select((match, index) => new
            {
                Match = match,
                Index = index,
                DirectChunkTitleSignal = ComputeDirectChunkTitleSignal(query, match),
                ExactTitleScore = ComputeExactTitleCandidateScore(query, match),
                HasPlacementEvidence = HasQuotedTitlePlacementEvidence(titlePhrases, match),
                StrictCoverage = HasStrictTitleAnchorCoverage(lexicalTokens, match),
                ContentEvidencePriority = ComputeContentEvidencePriority(match)
            })
            .Where(item => IsStrongPreciseTitleLocalNeighborhoodAnchor(
                item.Match,
                item.HasPlacementEvidence,
                item.StrictCoverage,
                item.DirectChunkTitleSignal,
                item.ExactTitleScore,
                item.ContentEvidencePriority))
            .OrderBy(static item => item.Index)
            .Select(static item => item.Match)
            .ToArray();
        if (anchors.Length == 0)
            return;

        selected.RemoveAll(match =>
        {
            var anchor = anchors.FirstOrDefault(candidate =>
                !IsSameChunk(candidate, match)
                && SharesSameDocumentPage(candidate, match));
            if (anchor is null)
                return false;

            return !IsUsefulPreciseTitleLocalNeighborhoodCompanion(query, anchor, match);
        });
    }


    internal static void PruneQualifiedQuotedTitleCompetitorDocuments(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1 || ShouldPreferComparativeDocumentDiversity(query) || ContainsDocumentOverviewIntent(query))
            return;

        var quotedPhrases = ExtractTitleLookupPhrases(query);
        if (quotedPhrases.Count == 0)
            return;

        var normalizedQuery = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        if (!ShouldKeepQuotedTitleSearchOpenForStructuredDetails(query, normalizedQuery)
            && !ShouldConstrainPreciseTitleLookup(query))
        {
            return;
        }

        var documentSignals = selected
            .Select(match => new
            {
                Match = match,
                DocumentKey = BuildSameDocumentPromotionKey(match),
                DirectTextSpecificityPriority = ComputePreciseQuotedLeadTitleSpecificityPriority(quotedPhrases, match.Text),
                HasStructuredEvidence = IsContentSelectionCandidate(match)
                    && !LooksLikeNavigationalChunk(match)
                    && !LooksLikeGlossaryChunk(match)
                    && (ContainsStructuredInventoryLikeCue(match)
                        || ContainsStructuredProcedureCue(match)
                        || ContainsStructuredTimeCue(match)
                        || ComputeContentEvidencePriority(match) >= 0.65)
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.DocumentKey))
            .GroupBy(static item => item.DocumentKey!, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new
            {
                DocumentKey = group.Key,
                BestDirectTextSpecificityPriority = group.Max(static item => item.DirectTextSpecificityPriority),
                HasStructuredEvidence = group.Any(static item => item.HasStructuredEvidence)
            })
            .ToArray();
        if (documentSignals.Length <= 1)
            return;

        var protectedDocumentKeys = documentSignals
            .Where(static item => item.BestDirectTextSpecificityPriority >= 3 && item.HasStructuredEvidence)
            .Select(static item => item.DocumentKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (protectedDocumentKeys.Count == 0)
            return;

        var qualifiedCompetitorDocumentKeys = documentSignals
            .Where(item => item.BestDirectTextSpecificityPriority == 1
                && !protectedDocumentKeys.Contains(item.DocumentKey))
            .Select(static item => item.DocumentKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (qualifiedCompetitorDocumentKeys.Count == 0)
            return;

        selected.RemoveAll(match =>
        {
            var documentKey = BuildSameDocumentPromotionKey(match);
            return !string.IsNullOrWhiteSpace(documentKey)
                && qualifiedCompetitorDocumentKeys.Contains(documentKey);
        });
    }


    private static bool IsStrongPreciseTitleLocalNeighborhoodAnchor(
        RagMatch match,
        bool hasPlacementEvidence,
        bool strictCoverage,
        int directChunkTitleSignal,
        double exactTitleScore,
        double contentEvidencePriority)
    {
        if (!string.Equals(ResolveRetriever(match), "local_title_token_route", StringComparison.Ordinal))
            return false;
        if (match.Score < 0.86)
            return false;
        if (!hasPlacementEvidence && !strictCoverage)
            return false;

        return directChunkTitleSignal > 0
            || exactTitleScore > 0.0
            || GetStructuredAnswerPriority(match) >= 2
            || contentEvidencePriority >= 0.65;
    }


    private static bool SharesSameDocumentPage(RagMatch left, RagMatch right)
    {
        if (string.IsNullOrWhiteSpace(left.DocPath) || string.IsNullOrWhiteSpace(right.DocPath))
            return false;
        if (!string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase))
            return false;

        return PageRangesOverlap(left.PageStart, left.PageEnd, right.PageStart, right.PageEnd);
    }


    private static bool IsUsefulPreciseTitleLocalNeighborhoodCompanion(string query, RagMatch anchor, RagMatch candidate)
    {
        if (!IsContentSelectionCandidate(candidate))
            return false;
        if (LooksLikeNavigationalChunk(candidate) || LooksLikeGlossaryChunk(candidate))
            return false;
        if (IsNearDuplicatePageOverlap(anchor, candidate))
            return false;

        var normalizedQuery = $" {NormalizeForLexicalSignal(query)} ";
        var asksInventory = ContainsStructuredInventoryRequest(normalizedQuery);
        var asksProcedure = ContainsStructuredProcedureRequest(normalizedQuery);
        var asksTime = ContainsStructuredTimeRequest(normalizedQuery);

        var anchorHasInventory = ContainsStructuredInventoryLikeCue(anchor);
        var anchorHasProcedure = ContainsStructuredProcedureCue(anchor);
        var anchorHasTime = ContainsStructuredTimeCue(anchor);
        var candidateHasInventory = ContainsStructuredInventoryLikeCue(candidate);
        var candidateHasProcedure = ContainsStructuredProcedureCue(candidate);
        var candidateHasTime = ContainsStructuredTimeCue(candidate);

        return (asksInventory && !anchorHasInventory && candidateHasInventory)
            || (asksProcedure && !anchorHasProcedure && candidateHasProcedure)
            || (asksTime && !anchorHasTime && candidateHasTime);
    }


    private static bool ContainsStructuredInventoryLikeCue(RagMatch match)
        => ContainsStructuredInventoryCue(match)
           || LooksLikeStructuredQuantityList(match.Text)
           || ContainsMeasuredQuantityListCue(match);


    private static bool ContainsMeasuredQuantityListCue(RagMatch match)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(GetDirectChunkSignalText(match)))
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var count = Regex.Matches(
            normalized,
            @"\b\d{1,4}(?:[\.,]\d{1,2})?\s*(?:g|gr|kg|mg|ml|cl|l|oz|lb|c\s*a\s*c|c\s*a\s*s|units?|items?|pieces?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        return count >= 2;
    }


    internal static void PruneUnmatchedPreciseTitleSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count == 0 || !ShouldConstrainPreciseTitleLookup(query))
            return;
        if (ContainsSimpleDocumentSelectionQuestion(NormalizeQueryForGuidance(query)))
            return;
        if (ShouldUseCatalogDocumentOverviewFallback(query))
            return;

        var primaryTokens = ExtractTitlePruneTokens(query)
            .Where(IsPrimarySpecificLexicalAnchorToken)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (primaryTokens.Length == 0)
            return;

        var requiredPrimaryAnchorCount = Math.Min(2, primaryTokens.Length);
        var primaryAnchors = selected
            .Where(match => CountPrimarySpecificSelectionAnchors(primaryTokens, match) >= requiredPrimaryAnchorCount)
            .ToArray();
        if (primaryAnchors.Length == 0)
        {
            selected.Clear();
            return;
        }

        selected.RemoveAll(match =>
        {
            if (CountPrimarySpecificSelectionAnchors(primaryTokens, match) >= requiredPrimaryAnchorCount)
                return false;

            return !IsUsefulLinkedSelectionNearPrimaryAnchor(primaryTokens, match, primaryAnchors);
        });
    }


    internal static void PruneUnanchoredFocusedLookupSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count == 0)
            return;
        if (ContainsDocumentOverviewIntent(query))
            return;
        if (ShouldUseCatalogDocumentOverviewFallback(query))
            return;
        if (ShouldPreferComparativeDocumentDiversity(query)
            && ShouldAllowDocumentProfileAssistForComparativeLookup(query))
        {
            return;
        }
        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsEvidenceQualityScopedSynthesisIntent(normalized))
            return;
        if (ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || (ShouldPreferComparativeDocumentDiversity(query)
                && ShouldTreatAsBroadDiversityQuery(query, normalized)))
        {
            return;
        }

        var standardReferencePhrases = ExtractStandardReferenceTitleLookupPhrases(query);
        if (standardReferencePhrases.Count > 0)
        {
            var standardAnchored = selected
                .Where(match => ContainsStandardReferenceSelectionAnchor(standardReferencePhrases, match))
                .ToArray();
            if (standardAnchored.Length > 0)
            {
                selected.RemoveAll(match =>
                    !ContainsStandardReferenceSelectionAnchor(standardReferencePhrases, match)
                    && !IsUsefulLinkedStandardReferenceCompanion(match, standardAnchored));
                return;
            }
        }

        var anchorTokens = ExtractFocusedLookupSelectionAnchorTokens(query);
        if (anchorTokens.Count == 0)
            return;

        var anchored = selected
            .Where(match => ContainsFocusedLookupSelectionAnchor(anchorTokens, match))
            .ToArray();
        if (anchored.Length == 0)
        {
            selected.Clear();
            return;
        }

        selected.RemoveAll(match =>
            !ContainsFocusedLookupSelectionAnchor(anchorTokens, match)
            && !IsUsefulLinkedFocusedLookupCompanion(anchorTokens, match, anchored));
    }


    private static bool ContainsStandardReferenceSelectionAnchor(
        IReadOnlyList<string> standardReferencePhrases,
        RagMatch match)
    {
        if (standardReferencePhrases.Count == 0)
            return true;
        if (IsDocumentProfileMatch(match)
            && !string.Equals(ResolveRetriever(match), "standard_reference_document_name", StringComparison.Ordinal))
            return false;

        var signalText = GetLexicalSignalText(match);
        if (string.IsNullOrWhiteSpace(signalText))
            return false;

        foreach (var phrase in standardReferencePhrases.Take(12))
        {
            var normalizedPhrase = NormalizeForLexicalSignal(phrase);
            if (!string.IsNullOrWhiteSpace(normalizedPhrase)
                && (StandardReferencePhraseMatchesText(normalizedPhrase, match.DocName)
                    || StandardReferencePhraseMatchesText(normalizedPhrase, match.DocPath)
                    || StandardReferencePhraseMatchesText(normalizedPhrase, match.EmbedText)))
            {
                return true;
            }

            var phraseTokens = ExtractTitlePruneTokens(phrase);
            if (phraseTokens.Length < 2)
                continue;

            if (ComputeLexicalCoverage(phraseTokens, signalText) >= 0.80)
                return true;

            var normalizedSignal = NormalizeForLexicalSignal(signalText);
            if (normalizedPhrase.Length >= 4
                && ContainsOrderedPhraseWindowInNormalizedText(normalizedSignal, normalizedPhrase, maxGapChars: 24))
            {
                return true;
            }
        }

        return false;
    }


    private static bool IsUsefulLinkedStandardReferenceCompanion(RagMatch match, IReadOnlyList<RagMatch> anchored)
    {
        if (!string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal))
            return false;
        if (LooksLikeNavigationalChunk(match) || LooksLikeGlossaryChunk(match))
            return false;

        return anchored.Any(anchor =>
            string.Equals(anchor.DocPath, match.DocPath, StringComparison.OrdinalIgnoreCase)
            && IsNearbyPage(anchor, match, maxDistance: 3));
    }


    internal static IReadOnlyList<string> ExtractFocusedLookupSelectionAnchorTokens(string query)
    {
        var focusedPhrases = ExtractFocusedLookupPhrases(query);
        if (focusedPhrases.Count == 0)
            return Array.Empty<string>();

        return focusedPhrases
            .SelectMany(ExtractTitlePruneTokens)
            .Where(IsFocusedLookupSelectionAnchorToken)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static token => token.Length)
            .ThenBy(static token => token, StringComparer.Ordinal)
            .Take(4)
            .ToArray();
    }


    internal static IReadOnlyList<string> ExtractFocusedLookupPrimaryHeadTokens(string query)
    {
        var phraseCandidates = ExtractFocusedLookupPhrases(query)
            .Append(BuildFocusedLexicalBackfillQuery(query))
            .Where(static phrase => !string.IsNullOrWhiteSpace(phrase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var tokens = new List<string>();
        foreach (var phrase in phraseCandidates)
        {
            var titleTokens = ExtractTitlePruneTokens(phrase)
                .Where(static token => !TitleConnectorTokens.Contains(token))
                .Where(static token => !FocusedLookupRequestLeadTokens.Contains(token))
                .Where(static token => !LexicalStopwords.Contains(token))
                .Where(static token => !SpecificAnchorStopwords.Contains(token))
                .Where(static token => !PrimaryAnchorStopwords.Contains(token))
                .Where(static token => !FocusedLookupSecondaryQualifierTokens.Contains(token))
                .ToArray();
            if (titleTokens.Length < 2)
                continue;

            var head = titleTokens[0];
            if (head.Length < 5 && !head.Any(char.IsDigit))
                continue;
            if (!tokens.Contains(head, StringComparer.Ordinal))
                tokens.Add(head);
            if (tokens.Count >= 3)
                break;
        }

        return tokens;
    }


    internal static int ComputeFocusedHeadAnchorSignal(IReadOnlyList<string> headTokens, RagMatch match)
    {
        if (headTokens.Count == 0)
            return 0;

        var normalized = NormalizeForLexicalSignal(string.Join(' ', new[]
        {
            GetDirectChunkSignalText(match),
            ExtractMatchedRouteOrProfileTitle(match.EmbedText)
        }.Where(static value => !string.IsNullOrWhiteSpace(value))));
        if (normalized.Length == 0)
            return 0;

        var signal = 0;
        foreach (var token in headTokens)
        {
            if (BuildLexicalTokenVariants(token)
                .DefaultIfEmpty(token)
                .Select(FoldDiacritics)
                .Distinct(StringComparer.Ordinal)
                .Any(variant => variant.Length >= 3 && ContainsExactNormalizedToken(normalized, variant)))
            {
                signal++;
            }
        }

        return signal;
    }


    internal static bool IsFocusedLookupSelectionAnchorToken(string token)
        => token.Length >= 3
           && token.Any(char.IsLetterOrDigit)
           && !TitleConnectorTokens.Contains(token)
           && !LexicalStopwords.Contains(token)
           && !SpecificAnchorStopwords.Contains(token)
           && !PrimaryAnchorStopwords.Contains(token)
           && !FocusedLookupSecondaryQualifierTokens.Contains(token);


    private static bool ContainsFocusedLookupSelectionAnchor(IReadOnlyList<string> anchorTokens, RagMatch match)
    {
        if (anchorTokens.Count == 0)
            return true;

        var directSignalText = string.Join("\n", new[]
        {
            GetDirectChunkSignalText(match),
            ExtractMatchedRouteOrProfileTitle(match.EmbedText)
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(directSignalText))
            return false;

        return ContainsFocusedLookupSelectionAnchorInNormalizedText(
            anchorTokens,
            NormalizeForLexicalSignal(directSignalText));
    }


    private static bool ContainsFocusedLookupSelectionAnchorInNormalizedText(
        IReadOnlyList<string> anchorTokens,
        string normalizedCandidate)
    {
        if (anchorTokens.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;

        foreach (var token in anchorTokens)
        {
            if (token.Any(char.IsDigit)
                && ContainsExactNormalizedToken(normalizedCandidate, token))
            {
                return true;
            }

            foreach (var variant in BuildLexicalTokenVariants(token).DefaultIfEmpty(token))
            {
                var foldedVariant = FoldDiacritics(variant);
                if (foldedVariant.Length >= 3 && ContainsExactNormalizedToken(normalizedCandidate, foldedVariant))
                    return true;
                if (foldedVariant.Length >= 6 && normalizedCandidate.Contains(foldedVariant, StringComparison.Ordinal))
                    return true;
                if (foldedVariant.Length >= 6 && ContainsNearNormalizedToken(normalizedCandidate, foldedVariant))
                    return true;
            }
        }

        return false;
    }


    private static bool IsUsefulLinkedFocusedLookupCompanion(
        IReadOnlyList<string> anchorTokens,
        RagMatch match,
        IReadOnlyList<RagMatch> anchored)
    {
        if (!string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal))
            return false;
        if (LooksLikeNavigationalChunk(match) || LooksLikeGlossaryChunk(match))
            return false;

        return anchored.Any(anchor =>
            string.Equals(anchor.DocPath, match.DocPath, StringComparison.OrdinalIgnoreCase)
            && IsNearbyPage(anchor, match, maxDistance: 2)
            && (SharesSpecificSection(anchor, match)
                || ContainsFocusedLookupSelectionAnchor(anchorTokens, match)));
    }


    internal static bool ShouldRunStructuredContinuationBackfill(string query, IReadOnlyList<RagMatch> selected)
        => selected.Count > 0
           && HasStructuredContinuationBackfillIntent(query)
           && SelectStructuredContinuationAnchors(query, selected, limit: 1).Count > 0;


    internal static IReadOnlyList<RagMatch> SelectStructuredContinuationAnchors(
        string query,
        IReadOnlyList<RagMatch> selected,
        int limit)
    {
        if (limit <= 0 || selected.Count == 0 || !HasStructuredContinuationBackfillIntent(query))
            return Array.Empty<RagMatch>();

        var titleScoringQuery = BuildFocusedLexicalBackfillQuery(query);
        var titleTokens = ExtractTitleScoringTokens(
            string.IsNullOrWhiteSpace(titleScoringQuery) ? query : titleScoringQuery);

        return selected
            .Where(match =>
                Guid.TryParse(match.ChunkId, out _)
                && IsStructuredContinuationAnchor(query, titleTokens, match))
            .OrderByDescending(match => LinkedContextLeadingContinuationNeedsPreviousCompanion(query, match) ? 1 : 0)
            .ThenByDescending(match => ComputeStructuredContinuationAnchorTitleSignal(query, titleTokens, match))
            .ThenByDescending(static match => GetStructuredAnswerPriority(match))
            .ThenByDescending(static match => ComputeContentEvidencePriority(match))
            .ThenByDescending(static match => match.Score)
            .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static match => match.ChunkIndex)
            .Take(limit)
            .ToArray();
    }


    internal static IReadOnlyList<RagMatch> FilterStructuredContinuationMatches(
        string query,
        IReadOnlyList<RagMatch> anchors,
        IReadOnlyList<RagMatch> candidates,
        int limit)
    {
        if (limit <= 0 || anchors.Count == 0 || candidates.Count == 0 || !HasStructuredContinuationBackfillIntent(query))
            return Array.Empty<RagMatch>();

        var ranked = new List<(RagMatch Match, int AnchorIndex, int RelationPriority, int CuePriority)>();
        for (var anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
        {
            var anchor = anchors[anchorIndex];
            foreach (var candidate in candidates)
            {
                if (IsSameChunk(anchor, candidate))
                    continue;

                var relation = ResolveStructuredContinuationRelation(anchor, candidate);
                if (relation is null)
                    continue;
                if (!IsUsefulStructuredContinuationCandidate(query, anchor, candidate, relation))
                    continue;

                ranked.Add((
                    candidate,
                    anchorIndex,
                    StructuredContinuationRelationPriority(relation),
                    ComputeStructuredContinuationCuePriority(query, candidate)));
            }
        }

        return ranked
            .GroupBy(item => BuildMatchDedupKey(item.Match), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderBy(static item => item.AnchorIndex)
                .ThenByDescending(static item => item.RelationPriority)
                .ThenByDescending(static item => item.CuePriority)
                .ThenByDescending(static item => item.Match.Score)
                .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Match.ChunkIndex)
                .First())
            .OrderBy(static item => item.AnchorIndex)
            .ThenByDescending(static item => item.RelationPriority)
            .ThenByDescending(static item => item.CuePriority)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .Select(static item => item.Match)
            .Take(limit)
            .ToArray();
    }


    internal static void MergeStructuredContinuationSelections(
        string query,
        List<RagMatch> selected,
        IReadOnlyList<RagMatch> continuations,
        int topK)
    {
        if (topK <= 0 || selected.Count == 0 || continuations.Count == 0)
            return;

        var anchors = SelectStructuredContinuationAnchors(query, selected, Math.Max(1, Math.Min(topK, selected.Count)));
        if (anchors.Count == 0)
            return;

        var continuationsByAnchorKey = new Dictionary<string, List<RagMatch>>(StringComparer.OrdinalIgnoreCase);
        foreach (var continuation in continuations)
        {
            var anchor = anchors.FirstOrDefault(candidate =>
            {
                var relation = ResolveStructuredContinuationRelation(candidate, continuation);
                return relation is not null
                    && IsUsefulStructuredContinuationCandidate(query, candidate, continuation, relation);
            });
            if (anchor is null)
                continue;

            var anchorKey = BuildMatchDedupKey(anchor);
            if (!continuationsByAnchorKey.TryGetValue(anchorKey, out var bucket))
            {
                bucket = [];
                continuationsByAnchorKey[anchorKey] = bucket;
            }

            bucket.Add(continuation);
        }

        if (continuationsByAnchorKey.Count == 0)
            return;

        var protectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in continuationsByAnchorKey)
        {
            protectedKeys.Add(pair.Key);
            foreach (var continuation in pair.Value)
                protectedKeys.Add(BuildMatchDedupKey(continuation));
        }

        var merged = new List<RagMatch>(selected.Count + continuations.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(RagMatch match)
        {
            if (seen.Add(BuildMatchDedupKey(match)))
                merged.Add(match);
        }

        foreach (var match in selected)
        {
            AddIfNew(match);
            var matchKey = BuildMatchDedupKey(match);
            if (!continuationsByAnchorKey.TryGetValue(matchKey, out var companions))
                continue;

            foreach (var continuation in companions
                         .OrderByDescending(candidate =>
                             StructuredContinuationRelationPriority(
                                 ResolveStructuredContinuationRelation(match, candidate) ?? string.Empty))
                         .ThenByDescending(candidate => ComputeStructuredContinuationCuePriority(query, candidate))
                         .ThenByDescending(static candidate => candidate.Score)
                         .ThenBy(static candidate => candidate.ChunkIndex))
            {
                AddIfNew(continuation);
            }
        }

        foreach (var continuation in continuations)
            AddIfNew(continuation);

        while (merged.Count > topK)
        {
            var removeIndex = -1;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                if (!protectedKeys.Contains(BuildMatchDedupKey(merged[i])))
                {
                    removeIndex = i;
                    break;
                }
            }

            if (removeIndex < 0)
                removeIndex = merged.Count - 1;
            merged.RemoveAt(removeIndex);
        }

        selected.Clear();
        selected.AddRange(merged);
    }


    internal static void ReattachStructuredContinuationCompanions(
        string query,
        List<RagMatch> selected,
        int topK)
    {
        if (selected.Count <= 1 || !HasStructuredContinuationBackfillIntent(query))
            return;

        var continuations = selected
            .Where(static match => string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal))
            .ToArray();
        if (continuations.Length == 0)
            return;

        MergeStructuredContinuationSelections(query, selected, continuations, topK);
    }


    internal static bool HasStructuredContinuationBackfillIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {NormalizeForLexicalSignal(query)} ";
        var asksInventory = ContainsStructuredInventoryRequest(normalized);
        var asksProcedure = ContainsStructuredProcedureRequest(normalized);
        var asksTime = ContainsStructuredTimeRequest(normalized);
        var asksSheet = ContainsStructuredSheetRequest(normalized);

        return (asksInventory && asksProcedure)
            || (asksSheet && (asksInventory || asksProcedure || asksTime))
            || (asksProcedure && asksTime)
            || HasPreciseStructuredDetailContinuationIntent(query, normalized);
    }


    internal static bool ShouldRelaxDefaultScoreFloorForFocusedStructuredLookup(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var hasFocusedTarget = ExtractFocusedLookupPhrases(query).Count > 0
            || ExtractQuotedLookupPhrases(query).Count > 0;
        if (!hasFocusedTarget)
            return false;

        var normalized = $" {NormalizeForLexicalSignal(query)} ";
        return ContainsStructuredSheetRequest(normalized)
            || ContainsStructuredInventoryRequest(normalized)
            || ContainsStructuredProcedureRequest(normalized)
            || ContainsStructuredTimeRequest(normalized)
            || HasSourceAttributionTitleLookupShape(query, normalized);
    }


    private static bool HasPreciseStructuredDetailContinuationIntent(string query, string normalizedQuery)
    {
        var focusedTokens = ExtractTitleScoringTokens(BuildFocusedLexicalBackfillQuery(query));
        if (focusedTokens.Count is < 2 or > 8)
            return false;

        if (ContainsAny(
                normalizedQuery,
                " examples with ",
                " examples that ",
                " exemples avec ",
                " exemples qui ",
                " opciones con ",
                " opcoes com ",
                " esempi con "))
        {
            return false;
        }

        var hasFocusedTarget = ExtractFocusedLookupPhrases(query).Count > 0
            || ExtractQuotedLookupPhrases(query).Count > 0;
        if (!hasFocusedTarget)
            return false;

        return ContainsStructuredSheetRequest(normalizedQuery)
            || ContainsStructuredInventoryRequest(normalizedQuery)
            || ContainsStructuredProcedureRequest(normalizedQuery)
            || ContainsStructuredTimeRequest(normalizedQuery)
            || ContainsAny(
                normalizedQuery,
                " donne moi ",
                " donnez moi ",
                " donne ",
                " donnez ",
                " montre moi ",
                " montre ",
                " show me ",
                " give me ",
                " find me ",
                " get me ",
                " detaille ",
                " details ",
                " explique ",
                " explain ");
    }


    private static bool IsStructuredContinuationAnchor(
        string query,
        IReadOnlyList<string> titleTokens,
        RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(match.DocPath) || string.IsNullOrWhiteSpace(match.ChunkId))
            return false;
        if (IsDocumentProfileMatch(match) || LooksLikeNavigationalChunk(match) || LooksLikeGlossaryChunk(match))
            return false;

        var titleSignal = ComputeStructuredContinuationAnchorTitleSignal(query, titleTokens, match);
        var linkedContinuationNeedsPrevious = LinkedContextLeadingContinuationNeedsPreviousCompanion(query, match);
        if (titleSignal <= 0 && !linkedContinuationNeedsPrevious)
            return false;

        if (!IsContentSelectionCandidate(match)
            && !(IsResolvedTitleOrNavigationRoute(match) && titleSignal > 0)
            && !linkedContinuationNeedsPrevious)
        {
            return false;
        }

        var structuredAnswerPriority = GetStructuredAnswerPriority(match);
        var contentEvidencePriority = ComputeContentEvidencePriority(match);
        var titleOnlyStructuredAnchorNeedsCompanion =
            (IsResolvedTitleOrNavigationRoute(match) || titleSignal >= 4)
            && structuredAnswerPriority < 2
            && !ContainsStructuredInventoryCue(match)
            && !ContainsStructuredProcedureCue(match)
            && (match.Text?.Length ?? 0) <= 700;

        if (structuredAnswerPriority < 2
            && contentEvidencePriority < 0.80
            && !titleOnlyStructuredAnchorNeedsCompanion
            && !linkedContinuationNeedsPrevious)
        {
            return false;
        }

        if (!StructuredContinuationAnchorNeedsCompanion(query, match)
            && !titleOnlyStructuredAnchorNeedsCompanion)
        {
            return false;
        }

        return true;
    }


    private static bool StructuredContinuationAnchorNeedsCompanion(string query, RagMatch match)
    {
        var normalizedQuery = $" {NormalizeForLexicalSignal(query)} ";
        var asksInventory = ContainsStructuredInventoryRequest(normalizedQuery);
        var asksProcedure = ContainsStructuredProcedureRequest(normalizedQuery);
        var asksTime = ContainsStructuredTimeRequest(normalizedQuery);
        var asksSheet = ContainsStructuredSheetRequest(normalizedQuery);

        var hasInventory = ContainsStructuredInventoryCue(match);
        var hasProcedure = ContainsStructuredProcedureCue(match);
        var hasTime = ContainsStructuredTimeCue(match);

        if (DirectTitleRouteBridgeNeedsPreviousCompanion(query, match))
            return true;
        if (TitleRouteLeadingContinuationNeedsPreviousCompanion(query, match))
            return true;
        if (LinkedContextLeadingContinuationNeedsPreviousCompanion(query, match))
            return true;

        return (asksProcedure && !hasProcedure && hasInventory)
            || (asksInventory && !hasInventory && hasProcedure)
            || (asksTime && !hasTime && (hasInventory || hasProcedure))
            || (asksSheet && (hasInventory || hasProcedure) && !(hasInventory && hasProcedure));
    }


    internal static bool DirectTitleRouteBridgeNeedsPreviousCompanion(string query, RagMatch match)
    {
        if (!IsDirectTitleTokenRouteMatch(match))
            return false;
        if (string.IsNullOrWhiteSpace(match.PrevChunkId))
            return false;

        var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        var titleSource = string.Join(
            ' ',
            new[] { routeTitle }
                .Concat(ExtractFocusedLookupPhrases(query))
                .Concat(ExtractQuotedLookupPhrases(query))
                .Append(BuildFocusedLexicalBackfillQuery(query))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        var titleTokens = NormalizeForLexicalSignal(titleSource)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 3 || token.Any(char.IsDigit))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (titleTokens.Length < 3)
            return false;

        var directSignal = GetDirectChunkSignalText(match);
        var directTitleHits = CountTitleAnchorTokens(titleTokens, directSignal);
        if (directTitleHits >= titleTokens.Length || directTitleHits < Math.Min(2, titleTokens.Length))
            return false;

        return ContainsStructuredInventoryCue(match)
            || ContainsStructuredProcedureCue(match)
            || ContainsStructuredTimeCue(match);
    }

}
