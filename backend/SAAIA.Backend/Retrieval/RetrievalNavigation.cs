using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalText;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalNavigation
{

    internal static double ComputeLexicalCoverage(IReadOnlyList<string> queryTokens, string? candidateText)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        return ComputeLexicalCoverageInNormalizedText(queryTokens, NormalizeForLexicalSignal(candidateText));
    }


    internal static double ComputeLexicalCoverageInNormalizedText(IReadOnlyList<string> queryTokens, string normalizedCandidate)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var matched = 0;
        foreach (var token in queryTokens)
        {
            var variants = BuildLexicalTokenVariants(token);
            if (variants.Any(variant => normalizedCandidate.Contains(variant, StringComparison.Ordinal)))
                matched++;
        }

        return matched == 0 ? 0.0 : (double)matched / queryTokens.Count;
    }


    internal static bool ComputeHypQuestionsMatched(string query, IReadOnlyList<string>? hypotheticalQuestions)
    {
        if (hypotheticalQuestions is null || hypotheticalQuestions.Count == 0)
            return false;

        var queryTokens = ExtractLexicalQueryTokens(query);
        if (queryTokens.Count == 0)
            return false;

        return hypotheticalQuestions.Any(question => ComputeLexicalCoverage(queryTokens, question) >= 0.5);
    }


    internal static bool? ResolveHypQuestionsMatched(string? docPath, IReadOnlyDictionary<string, bool?>? byDocPath)
    {
        if (string.IsNullOrWhiteSpace(docPath) || byDocPath is null)
            return null;

        var normalizedDocPath = NormalizeRagDocPath(docPath);
        if (!string.IsNullOrWhiteSpace(normalizedDocPath)
            && byDocPath.TryGetValue(normalizedDocPath, out var normalizedMatched))
            return normalizedMatched;

        return byDocPath.TryGetValue(docPath, out var matched) ? matched : null;
    }


    internal static double ComputeLinkedMatchScore(double anchorScore, string linkType, string? anchorRetriever = null)
    {
        var penalty = linkType switch
        {
            "same_section" => 0.02,
            "next" => 0.035,
            "prev" => 0.04,
            "same_page_route" => 0.045,
            "same_page_exact" => 0.045,
            _ => 0.05
        };

        if (string.Equals(anchorRetriever, "linked_context", StringComparison.Ordinal))
            penalty += 0.015;
        else if (string.Equals(anchorRetriever, "exact_match", StringComparison.Ordinal))
            penalty += 0.01;
        else if (string.Equals(anchorRetriever, "sparse_bm25", StringComparison.Ordinal))
            penalty += 0.005;

        return Math.Max(0.0, anchorScore - penalty);
    }


    internal sealed record LinkedAnchorCandidate(
        Guid SourceId,
        string SourceRetriever,
        double AnchorScore,
        string? AnchorTitle);


    internal sealed record TitleSelectionSignal(
        RagMatch Match,
        double ExactTitleScore,
        int SpecificAnchorCount,
        bool FullTitleCoverage,
        int StructuredAnswerPriority);


    internal static double ComputeExactMatchScore(string? matchKind, string? matchedTerm, string? text)
    {
        var score = matchKind switch
        {
            "standard_ref" => 1.0,
            "code_ref" => 0.985,
            _ => 0.96
        };

        var normalizedMatchedTerm = ExactMatchEntryExtractor.NormalizeForLookup(matchedTerm ?? string.Empty);
        var normalizedText = ExactMatchEntryExtractor.NormalizeForLookup(text ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalizedMatchedTerm))
        {
            score += Math.Min(0.01, normalizedMatchedTerm.Length / 500.0);
            if (string.Equals(normalizedMatchedTerm, normalizedText, StringComparison.Ordinal))
            {
                score += 0.005;
            }
            else if (normalizedMatchedTerm.Length >= 12
                && normalizedMatchedTerm.Contains(' ')
                && normalizedText.StartsWith(normalizedMatchedTerm, StringComparison.Ordinal))
            {
                score -= 0.006;
            }
        }

        return Math.Min(1.02, score);
    }


    internal static bool ShouldShortCircuitAfterExact(IReadOnlyList<RagMatch> exactMatches)
        => ShouldShortCircuitAfterExact(string.Empty, exactMatches);


    internal static bool ShouldShortCircuitAfterExact(string query, IReadOnlyList<RagMatch> exactMatches)
    {
        if (exactMatches.Count == 0)
            return false;
        if (ContainsDocumentCompanionRelationIntent(query))
            return false;

        var top = exactMatches[0];
        if (!string.Equals(top.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal))
            return false;

        var isStrongReferenceHit =
            string.Equals(top.ChunkType, "exact_match_entry", StringComparison.Ordinal)
            || string.Equals(top.ChunkType, "document_metadata_ref", StringComparison.Ordinal)
            || (top.ChunkIndex is >= 0
                && Guid.TryParse(top.ChunkId, out _)
                && !string.IsNullOrWhiteSpace(top.Text)
                && !IsDocumentProfileMatch(top));

        if (!isStrongReferenceHit || top.Score < 0.97)
            return false;
        if (LooksLikeNavigationalChunk(top))
            return false;

        if (exactMatches.Count == 1)
            return true;

        var second = exactMatches[1];
        return top.Score - second.Score >= 0.04;
    }


    internal static bool ContainsDocumentCompanionRelationIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        return ContainsAny(
            normalized,
            " norme principale ",
            " norme complete ",
            " norme complète ",
            " standard principal ",
            " main standard ",
            " full standard ",
            " complete standard ",
            " document principal ",
            " fichier principal ",
            " main document ",
            " main file ",
            " par rapport au document ",
            " par rapport a document ",
            " par rapport au fichier ",
            " par rapport a fichier ",
            " role du fichier ",
            " role de fichier ",
            " role du document ",
            " role de document ",
            " corrigendum ",
            " corrigenda ",
            " amendement ",
            " amendment ",
            " addendum ",
            " supplement ",
            " supplemental document ",
            " supplemental file ",
            " document supplementaire ",
            " fichier supplementaire ",
            " annexe ",
            " annex ",
            " appendix ",
            " correction document ",
            " correction file ",
            " notice de correction ",
            " document connexe ",
            " fichier connexe ",
            " related document ",
            " related file ",
            " companion document ",
            " companion file ");
    }


    internal static bool ShouldProbeUnquotedTitleAnchorRoute(
        string query,
        bool skipChunkRetrieversForDocumentOverview,
        bool useScopedProfileFallback)
    {
        if (skipChunkRetrieversForDocumentOverview || useScopedProfileFallback)
            return false;
        if (ExtractQuotedLookupPhrases(query).Count > 0)
            return false;
        if (LooksLikeShortAudienceConstrainedGenericFragment(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        var focusedPhrases = ExtractFocusedLookupPhrases(query)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var focusedBackfillQuery = BuildFocusedLexicalBackfillQuery(query);
        var focusedBackfillTokens = ExtractLexicalQueryTokens(focusedBackfillQuery);
        var hasPreciseLookupVerbTarget = ContainsPreciseLookupVerb(normalized)
            && focusedBackfillTokens.Count is >= 2 and <= 6;
        var hasSourceAttributionTitleShape = HasSourceAttributionTitleLookupShape(query, normalized);

        if (ShouldPreferComparativeDocumentDiversity(query)
            || ContainsDocumentOverviewIntent(query)
            || ContainsSimpleDocumentSelectionQuestion(normalized)
            || ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || (ContainsSituationalBroadSelectionIntent(normalized) && focusedPhrases.Length == 0)
            || ContainsGuidanceOrAdviceSelectionIntent(normalized)
            || IsRecommendationSelectionQuery(normalized))
        {
            return false;
        }
        if (ContainsBroadScopedSynthesisIntent(normalized) && !hasPreciseLookupVerbTarget)
            return false;

        if (focusedPhrases.Length == 1
            || hasSourceAttributionTitleShape
            || ShouldConstrainPreciseTitleLookup(query))
        {
            return true;
        }

        return hasPreciseLookupVerbTarget;
    }


    internal static bool ShouldSkipLocalTitleTokenRouteForIndexedTitleAnchors(
        string query,
        bool hasCategoryScope,
        bool hasDocScope,
        bool skipChunkRetrieversForDocumentOverview,
        bool useScopedProfileFallback)
    {
        if (!hasCategoryScope || hasDocScope || skipChunkRetrieversForDocumentOverview || useScopedProfileFallback)
            return false;
        if (ExtractTitleLookupPhrases(query).Count == 0)
            return false;
        if (ExtractQuotedLookupPhrases(query).Count > 0)
            return true;

        return ShouldProbeUnquotedTitleAnchorRoute(
            query,
            skipChunkRetrieversForDocumentOverview,
            useScopedProfileFallback);
    }


    internal static bool ContainsPreciseLookupVerb(string normalized)
        => ContainsAny(
            normalized,
            " cherche ",
            " recherche ",
            " chercher ",
            " rechercher ",
            " donne ",
            " donne moi ",
            " donnez ",
            " montre ",
            " montre moi ",
            " explique ",
            " expliques ",
            " expliquer ",
            " detaille ",
            " trouve ",
            " trouver ",
            " find ",
            " give ",
            " show ",
            " explain ",
            " details ",
            " get ",
            " search ",
            " looking ",
            " busca ",
            " buscar ",
            " busco ",
            " procura ",
            " procurar ",
            " procuro ",
            " cerca ",
            " cercare ",
            " cerco ",
            " suche ",
            " such ",
            " finde ",
            " finden ");


    internal static bool ShouldShortCircuitAfterTitleAnchorRoute(string query, IReadOnlyList<RagMatch> titleAnchorRouteMatches)
    {
        if (titleAnchorRouteMatches.Count == 0)
            return false;
        if (!ShouldProbeUnquotedTitleAnchorRoute(
                query,
                skipChunkRetrieversForDocumentOverview: false,
                useScopedProfileFallback: false))
        {
            return false;
        }
        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        if (HasSourceAttributionTitleLookupShape(query, normalized))
            return false;

        var candidates = RankTitleAnchorRouteShortCircuitMatches(titleAnchorRouteMatches);
        if (candidates.Count == 0)
            return false;

        var top = candidates[0];
        var minimumScore = string.Equals(ResolveRetriever(top), "direct_title_token_route", StringComparison.Ordinal)
            ? 0.78
            : 0.90;
        if (top.Score < minimumScore)
            return false;

        if (QueryRequestsSafetyDataSheetDocumentType(query)
            && !TitleSignalMatchesSafetyDataSheetDocumentType(GetDocumentTypeTitleSignalText(top)))
        {
            return false;
        }

        var second = candidates.FirstOrDefault(match => !IsSameDocument(top, match));
        if (second is null)
            return true;

        return top.Score - second.Score >= 0.04;
    }


    internal static IReadOnlyList<RagMatch> RankTitleAnchorRouteShortCircuitMatches(IReadOnlyList<RagMatch> titleAnchorRouteMatches)
        => titleAnchorRouteMatches
            .Where(IsTitleAnchorRouteShortCircuitCandidate)
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static match => match.ChunkIndex)
            .ToArray();


    private static bool IsTitleAnchorRouteShortCircuitCandidate(RagMatch match)
        => IsResolvedTitleOrNavigationRoute(match)
            && (!IsDirectTitleTokenRouteMatch(match) || DirectTitleTokenRouteHasStrongLeadEvidence(match))
            && !LooksLikeNavigationalChunk(match)
            && !HasBorderlineNavigationShortcutRisk(match)
            && HasUsefulResolvedTitleBody(match);


    private static bool HasBorderlineNavigationShortcutRisk(RagMatch match)
        => match.NavigationScore is >= 0.72
            && match.ContentDensityScore is null or <= 0.55;


    internal static bool HasUsefulResolvedTitleBody(RagMatch match)
    {
        if (GetStructuredAnswerPriority(match) >= 2)
            return true;
        if (match.MatchedContentCards is { Count: > 0 })
            return true;

        return (match.Text?.Trim().Length ?? 0) >= 160;
    }


    internal static bool ShouldShortCircuitAfterQuotedTitle(string query, IReadOnlyList<RagMatch> quotedTitleMatches)
    {
        if (quotedTitleMatches.Count == 0)
            return false;
        var quotedPhrases = ExtractQuotedLookupPhrases(query);
        if (quotedPhrases.Count != 1)
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        if (ShouldPreferComparativeDocumentDiversity(query)
            || ContainsDocumentOverviewIntent(query)
            || ContainsEnumerativeLookupIntent(normalized)
            || ContainsGuidanceOrAdviceSelectionIntent(normalized)
            || ShouldKeepQuotedTitleSearchOpenForStructuredDetails(query, normalized))
        {
            return false;
        }

        var top = quotedTitleMatches[0];
        if (top.Score < 0.90 || LooksLikeNavigationalChunk(top))
            return false;
        if (!HasQuotedTitlePlacementEvidence(quotedPhrases, top))
            return false;

        if (quotedTitleMatches.Count == 1)
            return true;

        var second = quotedTitleMatches[1];
        if (IsSameDocument(top, second))
            return true;

        return top.Score - second.Score >= 0.04;
    }


    internal static bool ShouldKeepQuotedTitleSearchOpenForStructuredDetails(string query, string normalized)
        => ContainsParameterDetailLookupIntent(normalized)
           || HasSourceAttributionTitleLookupShape(query, normalized)
           || ContainsAny(
               normalized,
               " fiche ",
               " card ",
               " scheda ",
               " components ",
               " component ",
               " materials ",
               " material ",
               " etape ",
               " etapes ",
               " step ",
               " steps ",
               " source ",
               " sources ",
               " citation ",
               " citations ");


    internal static bool ShouldSkipQuotedTitleFullScan(string query, bool hasDocScope)
        => !hasDocScope
           && ExtractQuotedLookupPhrases(query).Count == 0
           && ExtractTitleLookupPhrases(query).Count > 0;


    internal static bool IsSameDocument(RagMatch left, RagMatch right)
    {
        if (!string.IsNullOrWhiteSpace(left.DocId)
            && !string.IsNullOrWhiteSpace(right.DocId)
            && string.Equals(left.DocId, right.DocId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(left.DocPath)
            && !string.IsNullOrWhiteSpace(right.DocPath)
            && string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase);
    }


    internal static string ResolveRetriever(RagMatch match)
        => match.EmbeddingBasis switch
        {
            "exact_match_v1" => "exact_match",
            "sparse_bm25_v1" => "sparse_bm25",
            "source_backed_sparse_fts_v1" => "sparse_bm25",
            "short_technical_phrase_v1" => "sparse_bm25",
            "document_profile_v1" => "document_profile",
            "linked_context_v1" => "linked_context",
            "explicit_document_title_route_v1" => "explicit_document_title_route",
            "title_anchor_route_v1" => "title_anchor_route",
            "navigation_route_v1" => "navigation_route",
            "navigation_catalog_route_v1" => "navigation_route",
            "direct_title_token_route_v1" => "direct_title_token_route",
            "local_title_token_route_v1" => "local_title_token_route",
            "fuzzy_title_lead_v1" => "fuzzy_title_lead",
            "standard_reference_document_name_v1" => "standard_reference_document_name",
            "reference_version_operand_document_name_v1" => "reference_version_operand_document_name",
            "date_document_name_v1" => "date_document_name",
            "document_status_operand_v1" => "document_status_operand",
            _ => "dense_qdrant"
        };


    internal static void AccumulateRrf(
        Dictionary<string, RrfAccumulator> accumulators,
        IReadOnlyList<RagMatch> matches,
        int rrfK)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var key = BuildMatchDedupKey(match);
            var contribution = 1.0 / (rrfK + i + 1);
            if (accumulators.TryGetValue(key, out var existing))
            {
                accumulators[key] = existing with
                {
                    Score = existing.Score + contribution,
                    Representative = SelectRepresentative(existing.Representative, match)
                };
            }
            else
            {
                accumulators[key] = new RrfAccumulator(contribution, match);
            }
        }
    }


    private static RagMatch SelectRepresentative(RagMatch left, RagMatch right)
    {
        var leftPriority = GetRetrieverPriority(left);
        var rightPriority = GetRetrieverPriority(right);
        if (rightPriority != leftPriority)
            return rightPriority > leftPriority ? right : left;

        return right.Score > left.Score ? right : left;
    }


    private static int GetRetrieverPriority(RagMatch match)
    {
        if (IsWeakResolvedRouteTarget(match))
            return 1;

        return ResolveRetriever(match) switch
        {
            "exact_match" => 5,
            "explicit_document_title_route" => 5,
            "title_anchor_route" => 4,
            "navigation_route" => 4,
            "direct_title_token_route" => 4,
            "local_title_token_route" => 4,
            "fuzzy_title_lead" => 4,
            "standard_reference_document_name" => 4,
            "reference_version_operand_document_name" => 4,
            "date_document_name" => 4,
            "sparse_bm25" => 3,
            "dense_qdrant" => 2,
            _ => 1
        };
    }

}
