using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildSourceBackedLlmCategoryScopeSystemPrompt(string language)
        => $@"
You are SAAIA's retrieval scope adjudicator, not the final answer writer.
Target user language: {NormalizeLanguageCode(language)}.

Choose whether the next retrieval passes should use one exact categoryScope.
Use CATEGORY_HINTS as the only allowed scope values: copy an exact category/path/ref from a hint, but judge fit semantically.
Treat each category label/path/ref as semantic evidence; lexical scores are weak sorting hints only.
A broad category can fit when it naturally contains the user's requested content, even if the label is not a literal query word.
When the user asks to compose a plan, schedule, list or recommendation from source items, choose the category that contains those source items; do not require a category named after the final format.
Do not invent categories.
If no hint clearly matches the user's request, choose null. Do not answer the user.

Return strict JSON only:
{{
  ""categoryDecision"": {{
    ""categoryScope"": ""exact category from CATEGORY_HINTS or null"",
    ""decision"": ""use_scope or none"",
    ""confidence"": ""high, medium or low"",
    ""reason"": ""short reason""
  }}
}}";

    private string BuildSourceBackedLlmCategoryScopeUserPrompt(
        ToolResults toolResults,
        SourceBackedEvidenceSufficiency currentAnalysis,
        IReadOnlyList<SourceBackedEvidenceExplorationPass> plannedPasses,
        string effectiveUserMessage,
        string language,
        string categoryHints)
    {
        var sourceLeads = BuildSourceBackedLlmEvidenceSnapshotForPrompt(
            toolResults,
            effectiveUserMessage,
            language,
            maxHits: 2,
            cueMaxLength: 70,
            maxCardHints: 1,
            maxProfileHints: 1,
            retrievalQueryMaxLength: 70);

        return $@"
USER_REQUEST:
{effectiveUserMessage}

REQUEST_SHAPE:
{BuildSourceBackedRequestShapeForPrompt(effectiveUserMessage, language)}

SUFFICIENCY:
- kind: {currentAnalysis.Kind}
- reason: {currentAnalysis.Reason}
- score: {currentAnalysis.Score}
- usableHits: {currentAnalysis.UsableHitCount}
- candidates: {currentAnalysis.CandidateCount}/{currentAnalysis.MinimumCandidateCount}
- targetSlots: {currentAnalysis.TargetSlotCount}

CATEGORY_HINTS:
{categoryHints}

CURRENT_SOURCE_LEADS:
{sourceLeads}

PLANNED_PASSES_WITHOUT_SCOPE:
{FormatSourceBackedLlmEvidenceExplorationPassesForPrompt(plannedPasses)}

DECISION_RULES:
- Pick categoryScope only when one CATEGORY_HINTS line is a clear semantic container for USER_REQUEST.
- Treat the hint label/path/scope itself as semantic evidence; lexicalScore is not a rejection rule.
- Do not reject a category only because it is broader than the specific task, or because its label is not repeated verbatim in USER_REQUEST.
- For plans, schedules, lists or recommendations assembled from source items, category fit is about where the source items live, not whether the category label names the final format.
- If exactly one hinted category is semantically plausible and the other hints are clearly unrelated, prefer using that categoryScope with medium or high confidence.
- Keep null for weak lexical coincidences, ambiguous requests, or when several unrelated categories could fit.
- The decision must be generic and based only on USER_REQUEST, REQUEST_SHAPE, current evidence and CATEGORY_HINTS.
- Return JSON only. Do not write search queries, final answers, citations or UI prose.";
    }

    private static string FormatSourceBackedLlmEvidenceExplorationPassesForPrompt(
        IReadOnlyList<SourceBackedEvidenceExplorationPass> plannedPasses)
    {
        if (plannedPasses.Count == 0)
            return "none";

        return string.Join(
            Environment.NewLine,
            plannedPasses
                .Take(MaxSourceBackedLlmEvidenceExplorationPasses)
                .Select(pass =>
                {
                    var scope = string.IsNullOrWhiteSpace(pass.CategoryScope) ? "null" : pass.CategoryScope;
                    var docScope = string.Join(
                        " ",
                        new[]
                        {
                            string.IsNullOrWhiteSpace(pass.DocId) ? null : $"docId:{pass.DocId}",
                            string.IsNullOrWhiteSpace(pass.DocPath) ? null : $"docPath:{pass.DocPath}",
                            pass.PageStart is null ? null : $"pageStart:{pass.PageStart}",
                            pass.PageEnd is null ? null : $"pageEnd:{pass.PageEnd}"
                        }.Where(static part => !string.IsNullOrWhiteSpace(part)));
                    var queries = string.Join("; ", pass.Queries.Take(4).Select(static query => TruncateForPrompt(query, 80)));
                    return TruncateForPrompt(
                        $"- {pass.Label} | categoryScope: {scope} | docScope: {(string.IsNullOrWhiteSpace(docScope) ? "none" : docScope)} | queries: {queries}",
                        260);
                }));
    }

    private static string BuildSourceBackedAvailableResearchSurfacesForPrompt()
        => """
- CATEGORY_HINTS: exact categoryScope only when semantically fitting; otherwise null.
- STRUCTURE_HINTS: maps from trees, summaries, profiles, headings and cards; use them only to reach concrete pages.
- CURRENT_SOURCE_LEADS: separate concrete page/card evidence from navigation/profile/index leads.
- WORKING_NOTES: bounded notes from earlier research passes; use for strategy only, never as source evidence.
- matchedContentCards/profileSignals/selectionHints: use as pivots and quality hints, not standalone facts.
- retrievalQuery/queryRuns: avoid repeats; search complementary facets, discovered labels and source-language variants.
- rag.multi_search: output JSON passes only; no final answer text.
""";

    private static string BuildSourceBackedRequestShapeForPrompt(string? effectiveUserMessage, string language)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(effectiveUserMessage ?? string.Empty);
        var normalizedLanguage = NormalizeLanguageCode(language);
        var flags = new List<string>();
        if (LooksLikeAnyDocumentaryPlanningRequest(intentQuery))
            flags.Add("planning");
        if (LooksLikeGenericCollectionOrListRequest(intentQuery))
            flags.Add("collection_or_list");
        if (LooksLikeMultipleCandidateSynthesisRequest(intentQuery))
            flags.Add("multiple_candidates");
        if (LooksLikeSoftChoiceRecommendationRequest(intentQuery))
            flags.Add("recommendation");
        if (LooksLikeSourceBackedPairingRecommendationRequest(intentQuery))
            flags.Add("pairing");
        if (LooksLikeBroadSourceBackedCompositionRequest(intentQuery))
            flags.Add("composition");
        if (LooksLikeUserNeedsSynthesizedDecisionOrPlan(intentQuery))
            flags.Add("synthesis_or_plan");
        if (flags.Count == 0)
            flags.Add("targeted");

        var dayLabels = DetectRequestedDayAxisLabels(intentQuery, normalizedLanguage);
        var explicitSlotLabels = DetectRequestedPlanningSlotAxisLabels(intentQuery, normalizedLanguage);
        var hasStructuredAxes = dayLabels.Count > 0 && explicitSlotLabels.Count > 0;
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(intentQuery);
        var minimumCandidates = ResolveMinimumSourceBackedPlanningCandidateCount(intentQuery, targetSlots, hasStructuredAxes);

        var lines = new List<string>
        {
            $"- flags: {string.Join(", ", flags.Distinct(StringComparer.OrdinalIgnoreCase))}",
            $"- detectedLanguage: {normalizedLanguage}",
            $"- targetSlots: {targetSlots}",
            $"- minimumCandidates: {minimumCandidates}",
            $"- structuredAxes: {(hasStructuredAxes ? "yes" : "no")}"
        };

        if (dayLabels.Count > 0)
            lines.Add($"- requestedDayAxis: {string.Join(", ", dayLabels.Take(10))}");
        if (explicitSlotLabels.Count > 0)
            lines.Add($"- requestedSlotAxis: {string.Join(", ", explicitSlotLabels.Take(10))}");
        if (hasStructuredAxes)
        {
            lines.Add("- structuredSearchGuidance: search complementary slot/type/facet terms, constraints, candidate inventory and concrete candidate labels; day-axis labels are placement targets, not enough as broad retrieval queries unless source leads are explicitly organized by those labels. Do not fan out the same query once per day/row/column.");
            lines.Add("- balancedSlotCoverage: cover multiple requested slot/type/facet labels before repeating one label with near-duplicate wording.");
            lines.Add("- plannerCoverageContract: your query set is invalid if it drops a requested slot/type/facet already covered by the current router queries.");
        }

        lines.Add("- searchObjective: gather enough distinct page-grounded candidates and context to let the writer synthesize a useful answer; if evidence is weak, keep exploring before falling back.");
        lines.Add("- categoryObjective: pick a semantic categoryScope from CATEGORY_HINTS when one hinted category clearly contains the requested content; category labels can be broader than the task, but keep null for weak single-term or ambiguous hints.");
        lines.Add("- evidenceObjective: prefer concrete content pages; use navigation, profiles, summaries and tables of contents as maps for follow-up retrieval.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildSourceBackedLlmEvidenceSnapshotForPrompt(
        ToolResults toolResults,
        string query,
        string language,
        int maxHits = 10,
        int cueMaxLength = 120,
        int maxCardHints = 3,
        int maxProfileHints = 4,
        int retrievalQueryMaxLength = 120)
    {
        var lines = EnumerateRagHitSummaries(toolResults)
            .Where(ShouldExposeHitForSourceBackedEvidenceDiscovery)
            .OrderByDescending(static hit => LooksLikeNavigationOnlyHit(hit) ? 0 : ComputeSourceBackedEvidenceRichnessScore(hit))
            .ThenByDescending(static hit => hit.Score)
            .Take(Math.Max(1, maxHits))
            .Select(hit =>
            {
                var source = string.IsNullOrWhiteSpace(hit.DocName) ? Path.GetFileName(hit.DocPath) : hit.DocName;
                var cue = BuildWriterEvidenceCueForPrompt(hit, query, maxLength: cueMaxLength);
                if (string.IsNullOrWhiteSpace(cue))
                    cue = ExtractRouteDiscoveryTitleCue(hit);
                if (string.IsNullOrWhiteSpace(cue))
                    cue = BuildSourceBackedCandidateSupportCue(hit);
                if (string.IsNullOrWhiteSpace(cue))
                    cue = "source-backed hit";
                var role = LooksLikeNavigationOnlyHit(hit) && IsRouteDiscoveryAnchorHit(hit)
                    ? "navigation/title anchor"
                    : "source lead";
                var signalParts = new List<string>();
                var signalRole = CollapseWhitespace(hit.SelectionHintRole ?? hit.ContentRole ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(signalRole))
                    signalParts.Add($"signalRole: {signalRole}");
                var richness = ComputeSourceBackedEvidenceRichnessScore(hit);
                if (richness > 0)
                    signalParts.Add($"richness: {richness}");
                if (hit.MatchedContentCards is { Count: > 0 })
                {
                    var cardHints = hit.MatchedContentCards
                        .Select(static card => CollapseWhitespace(card.Title))
                        .Where(static title => !string.IsNullOrWhiteSpace(title))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(Math.Max(1, maxCardHints))
                        .ToArray();
                    if (cardHints.Length > 0)
                        signalParts.Add($"contentCards: {string.Join("; ", cardHints)}");
                }

                var profileHints = hit.ProfileSignals is null
                    ? Array.Empty<string>()
                    : hit.ProfileSignals.Topics
                        .Concat(hit.ProfileSignals.Keywords)
                        .Concat(hit.ProfileSignals.Entities)
                        .Concat(hit.ProfileSignals.MatchedTerms)
                        .Select(CollapseWhitespace)
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(Math.Max(1, maxProfileHints))
                        .ToArray();
                if (profileHints.Length > 0)
                    signalParts.Add($"profileHints: {string.Join("; ", profileHints)}");

                var retrievalQuery = string.IsNullOrWhiteSpace(hit.RetrievalQuery)
                    ? string.Empty
                    : $" | retrievalQuery: {TruncateForPrompt(CollapseWhitespace(hit.RetrievalQuery), retrievalQueryMaxLength)}";
                var signalSuffix = signalParts.Count == 0 ? string.Empty : $" | {string.Join(" | ", signalParts)}";
                return $"- {role}: {source} {SourceBackedPagePrefix(language)}{Math.Max(1, hit.PageStart)} | {CollapseWhitespace(cue)}{signalSuffix}{retrievalQuery}";
            })
            .ToArray();

        return lines.Length == 0 ? "none" : string.Join(Environment.NewLine, lines);
    }
}
