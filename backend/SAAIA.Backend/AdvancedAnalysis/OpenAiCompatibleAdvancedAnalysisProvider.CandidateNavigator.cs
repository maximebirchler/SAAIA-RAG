using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static IReadOnlyList<PromptEvidenceItem> BuildCandidateNavigatorEvidence(
        IReadOnlyList<PromptEvidenceItem> observations,
        IReadOnlyList<CandidateInventoryItem> inventory)
    {
        var locatorIds = inventory.Where(item => item.Status == "navigation_only")
            .SelectMany(item => item.LocatorEvidenceIds)
            .ToHashSet(StringComparer.Ordinal);
        var navigation = observations.Where(item =>
                item.EvidenceId is not null
                && (locatorIds.Contains(item.EvidenceId)
                    || item.ContentRole is RetrievalContentClassifier.NavigationRole
                        or RetrievalContentClassifier.MixedNavigationContentRole));
        var sourceAnchors = observations.Where(item =>
                !string.IsNullOrWhiteSpace(item.SourceKey))
            .GroupBy(item => item.SourceKey, StringComparer.Ordinal)
            .Select(group => group.First());
        return sourceAnchors.Concat(navigation)
            .GroupBy(item => item.EvidenceId ?? item.SourceKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(20)
            .Select(item => item.Content.Length <= 1_200
                ? item
                : item with
                {
                    Content = item.Content[..1_200],
                    ContentTruncated = true
                })
            .ToArray();
    }

    private string BuildCandidateNavigatorUserPrompt(
        AdvancedAnalysisProviderRequest request,
        SynthesisResearchContext context,
        CandidateExplorerCoverage coverage,
        IReadOnlyList<PromptEvidenceItem> evidence,
        object? navigatorFeedback,
        int remainingModelCalls)
        => JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            load = BuildPromptLoad(request.Handoff.Load),
            assignmentGap = new
            {
                coverage.RequiredDistinctCount,
                coverage.BodyVerifiedDistinctCount,
                coverage.MaximumAssignableCount,
                coverage.MissingByRole,
                focusRoles = coverage.MissingByRole.Keys.ToArray()
            },
            candidateLedger = context.CandidateInventory.Select(item => new
            {
                item.Key,
                item.ExactTitle,
                item.SourceKey,
                item.TargetRoles,
                item.Status
            }).ToArray(),
            navigatorFeedback,
            evidence,
            researchTools = new
            {
                tools = BuildResearchToolsForPrompt(),
                researchAllowed = true,
                remainingModelCalls,
                documentaryBudget = context.Tools.Budget,
                maximumQueries = ResolveMaximumPlanQueries(request),
                availableCategories = context.AvailableCategories,
                priorSearches = BuildPriorSearchesForPrompt(
                    context.PriorSearches,
                    evidence)
            }
        }, JsonOptions);

    private string BuildCandidateNavigatorSystemPrompt()
        => """
           You are the SAAIA Candidate Navigator. Choose the next documentary
           operations that can expose substantive canonical bodies for the exact
           assignmentGap focusRoles. You receive compact navigation excerpts,
           opaque source handles, the candidate ledger and prior operations.

           Prefer exact observed contents/index titles with find_source_text, or
           valid inclusive read_source windows of at most four pages when physical
           coordinates are visible. Paginate find_source_text with its offset when
           a result reports more matches. Use search_corpus only when visible
           navigation has no promising exact entry or exact attempts failed. Do
           not repeat an existing operation with cosmetic topK changes. Do not
           classify candidates, update the ledger, draft the answer or infer corpus
           absence from a bound.

           Call one or more declared documentary functions when a useful route
           remains. Only when no useful untried route is visible, return exactly
           {"outcome":"navigation_bounded","reason":"short operational reason"}.
           The backend executes and paginates the chosen operations mechanically;
           a separate Candidate Judge will assess returned bodies.
           """ + "\n" + NativeResearchContract;

    private static string ParseCandidateNavigatorTerminal(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 2
                || ReadString(root, "outcome") != "navigation_bounded")
            {
                throw new JsonException();
            }
            var reason = ReadString(root, "reason").Trim();
            if (reason.Length is < 1 or > 500)
                throw new JsonException();
            return reason;
        }
        catch (JsonException)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_candidate_navigator_protocol_invalid");
        }
    }
}
