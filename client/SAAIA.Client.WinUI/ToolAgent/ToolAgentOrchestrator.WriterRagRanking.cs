using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using SAAIA.Client.WinUI.Models;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool IsLlmContextOverflowException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            var message = current.Message ?? string.Empty;
            if (message.Contains("exceed_context_size", StringComparison.OrdinalIgnoreCase)
                || message.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase)
                || message.Contains("context size", StringComparison.OrdinalIgnoreCase)
                || message.Contains("n_ctx", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ResolveBroadWriterContentCardLimit(string? userMessage, bool keepBroadCardEvidence)
    {
        if (!keepBroadCardEvidence)
            return 2;

        if (LooksLikeAnyDocumentaryPlanningRequest(userMessage))
            return 4;

        if (LooksLikeBroadSourceBackedCompositionRequest(userMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(userMessage)
            || LooksLikeSoftChoiceRecommendationRequest(userMessage)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(userMessage))
        {
            return 3;
        }

        return 2;
    }

    private static bool ShouldKeepBroadWriterCardEvidence(RagHitSummary hit, int selectedIndex, string? userMessage)
    {
        if (hit.MatchedContentCards is not { Count: > 0 })
            return false;

        var indexLimit = LooksLikeAnyDocumentaryPlanningRequest(userMessage)
            ? 8
            : LooksLikeBroadSourceBackedCompositionRequest(userMessage)
              || LooksLikeMultipleCandidateSynthesisRequest(userMessage)
              || LooksLikeSoftChoiceRecommendationRequest(userMessage)
              || LooksLikeUserNeedsSynthesizedDecisionOrPlan(userMessage)
                ? 5
                : 2;
        if (selectedIndex >= indexLimit)
            return false;

        var role = NormalizeRagEvidenceRole(hit.SelectionHintRole);
        if (role is not ("actionable_item" or "supporting_context" or "advisory")
            && !HasRichSourceBackedEvidence(hit))
        {
            return false;
        }

        return hit.MatchedContentCards.Any(static card =>
            card.RawEvidence.HasValue
            || card.Evidence is { QuantityFacts.Count: > 0 }
            || card.Evidence?.Facts is { Count: > 0 });
    }

    private static IReadOnlyList<JsonElement> PreserveComparativeEntityCoverageForWriter(
        IReadOnlyList<JsonElement> rankedHits,
        IReadOnlyList<JsonElement> sourceHits,
        string userMessage,
        int maxHits)
    {
        var entityAnchors = ExtractComparativeEntityAnchorTerms(userMessage);
        if (maxHits <= 0 || entityAnchors.Length < 2 || sourceHits.Count == 0)
            return rankedHits;

        var requiredCoverage = Math.Min(entityAnchors.Length, maxHits);
        var currentCoverage = CountComparativeEntityCoverage(
            rankedHits.Take(maxHits).Select(BuildRagHitSummary),
            entityAnchors);
        if (currentCoverage >= requiredCoverage)
            return rankedHits;

        var evidenceQuery = BuildRagEvidenceSelectionQuery(userMessage);
        var focusTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(evidenceQuery))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .ToArray();
        var candidates = sourceHits
            .Select((hit, index) => new
            {
                Hit = hit,
                Index = index,
                Summary = BuildRagHitSummary(hit)
            })
            .Select(item => new
            {
                item.Hit,
                item.Index,
                item.Summary,
                EntityMatches = GetMatchedComparativeEntityAnchorIndexes(item.Summary, entityAnchors),
                Score = ComputeComparativeDocumentaryEvidenceScore(item.Summary, evidenceQuery, focusTerms)
            })
            .Where(item => item.EntityMatches.Length > 0)
            .Select(item => new
            {
                item.Hit,
                Scored = new ComparativeScoredHit(
                    item.Summary,
                    item.Index,
                    item.EntityMatches,
                    item.Score + (item.EntityMatches.Length * 10.0))
            })
            .ToList();
        if (candidates.Count == 0)
            return rankedHits;

        var coverageHits = SelectComparativeEntityCoverageHits(
            candidates.Select(static item => item.Scored).ToList(),
            entityAnchors.Length,
            requiredCoverage);
        if (CountComparativeEntityCoverage(coverageHits, entityAnchors) <= currentCoverage)
            return rankedHits;

        var candidateByKey = candidates
            .GroupBy(static item => BuildRagHitIdentityKey(item.Scored.Hit), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Hit, StringComparer.OrdinalIgnoreCase);
        var coverageKeys = coverageHits
            .Select(BuildRagHitIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return coverageHits
            .Select(hit => candidateByKey.TryGetValue(BuildRagHitIdentityKey(hit), out var sourceHit) ? sourceHit : default)
            .Where(static hit => hit.ValueKind != JsonValueKind.Undefined)
            .Concat(rankedHits.Where(hit => !coverageKeys.Contains(BuildRagHitIdentityKey(BuildRagHitSummary(hit)))))
            .ToList();
    }

    private static IReadOnlyList<JsonElement> PreservePrimaryQueryTopHitsForWriter(
        IReadOnlyList<JsonElement> rankedHits,
        IReadOnlyList<JsonElement> sourceHits,
        string userMessage,
        int maxHits)
    {
        if (maxHits <= 1 || sourceHits.Count == 0)
            return rankedHits;

        if (LooksLikeShortTechnicalEvidenceTopic(userMessage)
            && rankedHits.Any(hit => HasShortTechnicalPhraseEvidence(userMessage, BuildRagHitSummary(hit))))
        {
            return rankedHits;
        }

        var primaryTopHits = sourceHits
            .Select((hit, index) => new
            {
                Hit = hit,
                Index = index,
                Summary = BuildRagHitSummary(hit),
                QueryIndex = TryGetInt(hit, "retrievalQueryIndex") ?? TryGetInt(hit, "retrieval_query_index") ?? TryGetInt(hit, "RetrievalQueryIndex"),
                HitRank = TryGetInt(hit, "retrievalHitRank") ?? TryGetInt(hit, "retrieval_hit_rank") ?? TryGetInt(hit, "RetrievalHitRank")
            })
            .Where(static item => (item.Summary.RetrievalQueryIndex ?? item.QueryIndex) == 0)
            .Where(static item => (item.Summary.RetrievalHitRank ?? item.HitRank) is >= 0 and <= 1)
            .Where(static item => IsUsablePrimaryQueryTopWriterHit(item.Summary))
            .OrderBy(static item => item.Summary.RetrievalHitRank ?? item.HitRank ?? int.MaxValue)
            .ThenByDescending(static item => ComputeBackendSelectionPriority(item.Summary))
            .ThenByDescending(static item => item.Summary.Score)
            .ThenBy(static item => item.Index)
            .Take(Math.Min(2, maxHits))
            .ToList();
        if (primaryTopHits.Count == 0)
            return rankedHits;

        var primaryKeys = primaryTopHits
            .Select(static item => GetWriterRagHitIdentityKey(item.Summary))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return primaryTopHits
            .Select(static item => item.Hit)
            .Concat(rankedHits.Where(hit => !primaryKeys.Contains(GetWriterRagHitIdentityKey(BuildRagHitSummary(hit)))))
            .ToList();
    }

    private sealed record WriterRagHitCandidate(JsonElement Hit, int Index, RagHitSummary Summary);

    private static string GetWriterRagHitDocumentKey(RagHitSummary hit)
        => string.IsNullOrWhiteSpace(hit.DocPath) ? hit.DocName : hit.DocPath;

    private static string GetWriterRagHitIdentityKey(RagHitSummary hit)
    {
        var docKey = GetWriterRagHitDocumentKey(hit);
        if (!string.IsNullOrWhiteSpace(hit.ChunkId))
            return $"chunk|{docKey}|{hit.ChunkId}";

        var contentCardKey = hit.MatchedContentCards?
            .Select(static card => NormalizeLexicalLookup(
                card.ContentCardId
                ?? (string.IsNullOrWhiteSpace(card.Kind) ? card.Title : $"{card.Kind}:{card.Title}")
                ?? string.Empty))
            .FirstOrDefault(static key => !string.IsNullOrWhiteSpace(key));
        if (!string.IsNullOrWhiteSpace(contentCardKey))
            return $"card|{docKey}|{hit.PageStart}|{hit.PageEnd}|{contentCardKey}";

        return $"range|{docKey}|{hit.PageStart}|{hit.PageEnd}|{hit.OffsetStart}|{hit.OffsetEnd}";
    }

    private static bool IsUsableBackendTopDocumentWriterHit(RagHitSummary hit)
    {
        if (LooksLikeNavigationOnlyHit(hit) || LooksLikeLowSignalContentCandidateHit(hit))
            return false;

        var hasEnoughText = CollapseWhitespace($"{GetRagHitPrimaryEvidenceText(hit)} {GetRagHitLookupText(hit)}").Length >= 80;
        var hasConcreteContentCard = hit.MatchedContentCards?.Any(HasConcreteContentCardEvidence) == true;
        return hasEnoughText
               || hasConcreteContentCard
               || (hit.ProfileSignals is not null && HasConcretePageGroundedEvidence(hit));
    }

    private static bool IsUsablePrimaryQueryTopWriterHit(RagHitSummary hit)
    {
        var hasConcreteContentCard = hit.MatchedContentCards?.Any(IsConcretePrimaryQueryTopWriterCard) == true;
        var hasEnoughText = CollapseWhitespace(GetRagHitPrimaryEvidenceText(hit)).Length >= 80;

        if (LooksLikeNavigationOnlyHit(hit)
            && !hasConcreteContentCard)
        {
            return false;
        }

        if (LooksLikeLowSignalContentCandidateHit(hit)
            && !hasConcreteContentCard)
        {
            return false;
        }

        return hasEnoughText
               || hasConcreteContentCard
               || (hit.ProfileSignals is not null && HasConcretePageGroundedEvidence(hit));
    }

    private static bool IsConcretePrimaryQueryTopWriterCard(RagHitContentCardSummary card)
    {
        var title = CleanSourceBackedOptionTitle(card.Title);
        if (string.IsNullOrWhiteSpace(title)
            || !IsUsefulSourceBackedDisplayTitle(title)
            || LooksLikePlanItemNoise(title)
            || LooksLikeProcedureSentenceTitle(NormalizeLexicalLookup(title)))
        {
            return false;
        }

        var normalizedKind = NormalizeLexicalLookup(card.Kind);
        if (Regex.IsMatch(
                normalizedKind,
                @"\b(?:navigation|toc|sommaire|contents|index|catalog|catalogue|section|category|categorie|chapter|heading)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var normalizedTitle = NormalizeLexicalLookup(title);
        if (Regex.IsMatch(
                normalizedTitle,
                @"^(?:index|sommaire|contents|table\s+of\s+contents|table\s+des\s+matieres|catalog|catalogue|category|categories|section|sections|chapter|chapters|liste|list)$",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (card.RawEvidence.HasValue
            || card.Evidence?.QuantityFacts is { Count: > 0 }
            || card.Evidence?.Facts is { Count: > 0 })
        {
            return true;
        }

        if (normalizedKind.Contains("unit", StringComparison.Ordinal)
            || normalizedKind.Contains("exact", StringComparison.Ordinal)
            || normalizedKind.Contains("lead", StringComparison.Ordinal))
        {
            return card.PageStart.HasValue
                   || !string.IsNullOrWhiteSpace(card.ContentCardId)
                   || ExtractQuerySignalTerms(normalizedTitle).Any();
        }

        var titleTermCount = ExtractQuerySignalTerms(normalizedTitle).Count();
        return titleTermCount >= 2
               && (card.PageStart.HasValue
                   || !string.IsNullOrWhiteSpace(card.ContentCardId)
                   || card.Signals is { Count: >= 2 });
    }

    private static IReadOnlyList<JsonElement> PinBackendTopDocumentFirstForWriter(
        IReadOnlyList<JsonElement> rankedHits,
        IReadOnlyList<JsonElement> originalHits,
        string userMessage)
    {
        if (rankedHits.Count <= 1 || originalHits.Count == 0 || !LooksLikeComparativeDocumentaryRequest(userMessage))
            return rankedHits;

        var originalCandidates = originalHits
            .Select((hit, index) => new WriterRagHitCandidate(hit, index, BuildRagHitSummary(hit)))
            .ToList();
        if (originalCandidates.Count == 0)
            return rankedHits;

        var backendTopDocumentKey = GetWriterRagHitDocumentKey(originalCandidates[0].Summary);
        if (string.IsNullOrWhiteSpace(backendTopDocumentKey))
            return rankedHits;

        var pinnedFromRanked = rankedHits
            .Select((hit, index) => new WriterRagHitCandidate(hit, index, BuildRagHitSummary(hit)))
            .FirstOrDefault(item =>
                string.Equals(GetWriterRagHitDocumentKey(item.Summary), backendTopDocumentKey, StringComparison.OrdinalIgnoreCase)
                && IsUsableBackendTopDocumentWriterHit(item.Summary));

        var evidenceQuery = BuildRagEvidenceSelectionQuery(userMessage);
        var focusTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(evidenceQuery))
            .Where(term => !IsComparativeRetrievalNoiseTerm(term))
            .ToArray();
        var pinned = pinnedFromRanked
                     ?? originalCandidates
                         .Where(item =>
                             string.Equals(GetWriterRagHitDocumentKey(item.Summary), backendTopDocumentKey, StringComparison.OrdinalIgnoreCase)
                             && IsUsableBackendTopDocumentWriterHit(item.Summary))
                         .OrderByDescending(item => ComputeBackendSelectionPriority(item.Summary))
                         .ThenByDescending(item => ComputeComparativeDocumentaryEvidenceScore(item.Summary, evidenceQuery, focusTerms))
                         .ThenByDescending(item => ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitPrimaryEvidenceText(item.Summary)))
                         .ThenByDescending(item => ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitLookupText(item.Summary)))
                         .ThenByDescending(item => item.Summary.Score)
                         .ThenBy(item => item.Index)
                         .FirstOrDefault();
        if (pinned is null)
            return rankedHits;

        var pinnedKey = GetWriterRagHitIdentityKey(pinned.Summary);
        return new[] { pinned.Hit }
            .Concat(rankedHits.Where(hit => !string.Equals(GetWriterRagHitIdentityKey(BuildRagHitSummary(hit)), pinnedKey, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static IReadOnlyList<JsonElement> RankRagHitsForWriter(IReadOnlyList<JsonElement> hits, string userMessage)
    {
        if (hits.Count <= 1)
            return hits;

        hits = hits
            .Where(static hit => !LooksLikeNavigationOnlyHit(BuildRagHitSummary(hit)))
            .Where(static hit => !LooksLikeLowSignalContentCandidateHit(BuildRagHitSummary(hit)))
            .ToList();
        if (hits.Count <= 1)
            return hits;

        if (LooksLikeComparativeDocumentaryRequest(userMessage))
        {
            var summaries = hits
                .Select((hit, index) => new
                {
                    Hit = hit,
                    Index = index,
                    Summary = BuildRagHitSummary(hit)
                })
                .ToList();
            var selected = SelectComparativeDocumentaryHits(
                    summaries.Select(static item => item.Summary),
                    userMessage,
                    RagWriterMaxHits)
                .ToList();
            if (selected.Count > 0)
            {
                var selectedRanks = selected
                    .Select((hit, rank) => new { Key = GetWriterRagHitIdentityKey(hit), Rank = rank })
                    .GroupBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(static group => group.Key, static group => group.First().Rank, StringComparer.OrdinalIgnoreCase);
                var ranked = summaries
                    .Where(item => selectedRanks.ContainsKey(GetWriterRagHitIdentityKey(item.Summary)))
                    .OrderBy(item => selectedRanks[GetWriterRagHitIdentityKey(item.Summary)])
                    .Concat(summaries.Where(item => !selectedRanks.ContainsKey(GetWriterRagHitIdentityKey(item.Summary))))
                    .Take(RagWriterMaxHits)
                    .Select(static item => item.Hit)
                    .ToList();
                return PinBackendTopDocumentFirstForWriter(ranked, hits, userMessage)
                    .Take(RagWriterMaxHits)
                    .ToList();
            }
        }

        var evidenceQuery = BuildRagEvidenceSelectionQuery(userMessage);
        var isShortTechnicalEvidenceTopic = LooksLikeShortTechnicalEvidenceTopic(evidenceQuery);
        var requestedTitle = isShortTechnicalEvidenceTopic
            ? null
            : TryExtractRequestedItemTitle(userMessage);
        evidenceQuery = !string.IsNullOrWhiteSpace(requestedTitle)
            ? requestedTitle!
            : evidenceQuery;

        if (string.IsNullOrWhiteSpace(evidenceQuery))
            return hits;

        var broadAnchorTerms = ExtractBroadCompositionAnchorTerms(userMessage);
        var evidenceCandidates = hits
            .Select((hit, index) => new
            {
                Hit = hit,
                Index = index,
                Summary = BuildRagHitSummary(hit)
            })
            .Select(item => new
            {
                item.Hit,
                item.Index,
                item.Summary,
                DocumentTypeScore = ComputeRequestedDocumentTypeAnchorScore(userMessage, item.Summary),
                AnchorMatchCount = broadAnchorTerms.Length == 0
                    ? 0
                    : broadAnchorTerms.Count(term => NormalizeLexicalLookup(GetRagHitLookupText(item.Summary)).Contains(term, StringComparison.Ordinal))
            })
            .ToList();
        var documentTypeCandidates = evidenceCandidates
            .Where(static item => item.DocumentTypeScore > 0)
            .ToList();
        if (documentTypeCandidates.Count > 0)
            evidenceCandidates = documentTypeCandidates;

        var rankedByEvidence = evidenceCandidates
            .OrderBy(item => LooksLikeNavigationOnlyHit(item.Summary) ? 1 : 0)
            .ThenByDescending(item => !string.IsNullOrWhiteSpace(requestedTitle) ? ComputeExactItemAnchorStrengthScore(requestedTitle!, item.Summary) : 0)
            .ThenByDescending(item => !string.IsNullOrWhiteSpace(requestedTitle) && RagHitContainsRequestedTitle(item.Summary, requestedTitle!) ? 1 : 0)
            .ThenByDescending(item => item.DocumentTypeScore)
            .ThenByDescending(item => isShortTechnicalEvidenceTopic ? ComputeDirectTechnicalEvidenceScore(evidenceQuery, item.Summary) : 0)
            .ThenByDescending(item => ComputeBackendSelectionPriority(item.Summary))
            .ThenByDescending(item => item.AnchorMatchCount)
            .ThenByDescending(item => ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitPrimaryEvidenceText(item.Summary)))
            .ThenByDescending(item => ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitLookupText(item.Summary)))
            .ThenByDescending(item => item.Summary.Score)
            .ThenBy(item => item.Index)
            .Select(item => item.Hit)
            .ToList();
        return PinBackendTopDocumentFirstForWriter(rankedByEvidence, hits, userMessage);
    }
}
