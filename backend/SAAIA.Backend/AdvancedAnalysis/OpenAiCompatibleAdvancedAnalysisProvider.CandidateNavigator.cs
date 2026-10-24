using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record CandidateNavigationWindow(string? EvidenceId, string SourceKey,
        int ContentOffset, string Content, int PhysicalPageStart, int PhysicalPageEnd);

    private sealed record CandidateNavigationMemory(
        IReadOnlyList<CandidateNavigationWindow> Windows, int OmittedWindowCount);

    private static CandidateNavigationMemory RememberCandidateNavigationWindows(
        CandidateNavigationMemory remembered,
        IReadOnlyList<PromptEvidenceItem> observations)
    {
        var windows = remembered.Windows.Concat(observations.Where(item => item.ContentRole is
                RetrievalContentClassifier.NavigationRole or RetrievalContentClassifier.MixedNavigationContentRole)
            .Select(item => new CandidateNavigationWindow(item.EvidenceId, item.SourceKey,
                item.ContentOffset, item.Content, item.PhysicalPageStart, item.PhysicalPageEnd)))
            .GroupBy(item => (item.EvidenceId, item.ContentOffset))
            .Select(group => group.OrderByDescending(item => item.Content.Length).First())
            .ToList();
        var omitted = remembered.OmittedWindowCount;
        var characters = JsonSerializer.Serialize(windows, JsonOptions).Length;
        while (windows.Count > 32 || characters > 64_000)
        {
            windows.RemoveAt(0);
            omitted++;
            characters = JsonSerializer.Serialize(windows, JsonOptions).Length;
        }
        return new CandidateNavigationMemory(windows, omitted);
    }

    private static IReadOnlyList<PromptEvidenceItem> RememberCandidateNavigatorEvidence(
        IReadOnlyList<PromptEvidenceItem> remembered,
        IReadOnlyList<PromptEvidenceItem> observations)
    {
        // Prompt projection is a view, not a new search result. Preserve the canonical
        // navigation excerpts already seen when later body-focused projections omit them.
        return remembered.Concat(observations)
            .GroupBy(item => item.EvidenceId ?? item.SourceKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var currentWindow = group.Last().ContentOffset;
                return group.Where(item => item.ContentOffset == currentWindow)
                    .OrderByDescending(item => item.Content.Length).First();
            })
            .ToArray();
    }

    private static IReadOnlyList<PromptEvidenceItem> BuildCandidateNavigatorEvidence(
        IReadOnlyList<PromptEvidenceItem> observations,
        IReadOnlyList<CandidateInventoryItem> inventory)
    {
        var unresolvedIds = inventory.Where(item => item.Status is
                "navigation_only" or "body_requested" or "needs_body")
            .SelectMany(item => item.LocatorEvidenceIds.Concat(item.BodyEvidenceIds))
            .ToHashSet(StringComparer.Ordinal);
        var unresolved = observations.Where(item => item.EvidenceId is not null
                                                   && unresolvedIds.Contains(item.EvidenceId));
        var navigation = observations.Where(item => item.ContentRole is
            RetrievalContentClassifier.NavigationRole or RetrievalContentClassifier.MixedNavigationContentRole);
        var sourceAnchors = observations.Where(item => !string.IsNullOrWhiteSpace(item.SourceKey))
            .GroupBy(item => item.SourceKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var selected = navigation.Concat(unresolved).Concat(sourceAnchors)
            .GroupBy(item => item.EvidenceId ?? item.SourceKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(20)
            .ToList();
        var selectedSources = selected.Select(item => item.SourceKey).ToHashSet(StringComparer.Ordinal);
        // Keep every remembered source callable without presenting its omitted body as proof.
        selected.AddRange(sourceAnchors.Where(item => !selectedSources.Contains(item.SourceKey))
            .Select(item => item with { Content = string.Empty, ContentTruncated = true }));
        return selected;
    }

    private IReadOnlyList<object> BuildCandidateNavigatorHistory(
        List<NativeResearchTurn> turns,
        IReadOnlyList<PromptEvidenceItem> evidence,
        out int omittedTurnCount)
    {
        var messages = new List<object>();
        var retained = new List<NativeResearchTurn>();
        for (var index = turns.Count - 1; index >= 0; index--)
        {
            IReadOnlyList<object> turnMessages;
            try
            {
                turnMessages = BuildNativeToolTurnMessages(turns[index], evidence, UsesNativeResponses);
            }
            catch (AdvancedAnalysisProviderException error) when (
                error.ErrorCode == "advanced_native_tool_history_limit_exceeded")
            {
                // An opaque reasoning item cannot be safely shortened. Omit its
                // whole native turn, including every linked call and result.
                continue;
            }
            var combined = turnMessages.Concat(messages).ToArray();
            if (JsonSerializer.Serialize(combined, JsonOptions).Length
                > _options.NativeResearchMaximumHistoryCharacters)
                break;
            messages.InsertRange(0, turnMessages);
            retained.Insert(0, turns[index]);
        }
        // Evict whole call/result turns together; never truncate a linked result or
        // encrypted Responses reasoning item into a malformed historical exchange.
        omittedTurnCount = turns.Count - retained.Count;
        turns.Clear();
        turns.AddRange(retained);
        return messages;
    }

    private string BuildCandidateNavigatorUserPrompt(
        AdvancedAnalysisProviderRequest request,
        SynthesisResearchContext context,
        CandidateExplorerCoverage coverage,
        IReadOnlyList<PromptEvidenceItem> evidence,
        object? navigatorFeedback,
        int remainingModelCalls,
        IReadOnlyList<PromptEvidenceItem> locatorObservations,
        bool documentaryResearchAllowed,
        CandidateNavigationMemory navigationMemory,
        int nativeHistoryOmittedTurnCount,
        bool capacityRepairOnly)
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
                reserveGapByRole = coverage.Roles.Where(role =>
                        role.BodyVerifiedCount < role.ReserveTargetCount)
                    .ToDictionary(role => role.TargetRole,
                        role => role.ReserveTargetCount - role.BodyVerifiedCount,
                        StringComparer.Ordinal),
                focusRoles = coverage.Ready
                    ? coverage.Roles.Where(role => role.BodyVerifiedCount < role.ReserveTargetCount)
                        .Select(role => role.TargetRole).ToArray()
                    : coverage.MissingByRole.Keys.ToArray(),
                researchPurpose = coverage.Ready ? "optional_reserve" : "required_assignment"
            },
            candidateLedger = context.CandidateInventory.Select(item => new
            {
                item.Key,
                item.ExactTitle,
                item.SourceKey,
                item.TargetRoles,
                item.Status,
                item.Note,
                item.LocatorEvidenceIds,
                item.BodyEvidenceIds,
                documentaryLocators = locatorObservations.Where(observation =>
                        observation.EvidenceId is { } id
                        && (item.LocatorEvidenceIds.Contains(id, StringComparer.Ordinal)
                            || item.BodyEvidenceIds.Contains(id, StringComparer.Ordinal)))
                    .Select(observation => new
                    {
                        observation.EvidenceId,
                        observation.PhysicalPageStart,
                        observation.PhysicalPageEnd,
                        instruction = "Canonical location only; omitted excerpt content is not body proof."
                    }).ToArray()
            }).ToArray(),
            navigatorFeedback,
            evidence,
            navigationMemory = new
            {
                retainedWindowCount = navigationMemory.Windows.Count,
                windows = navigationMemory.Windows.Where(window => !evidence.Any(item =>
                    item.EvidenceId == window.EvidenceId && item.ContentOffset == window.ContentOffset
                    && item.Content == window.Content)).ToArray(),
                navigationMemory.OmittedWindowCount,
                instruction = "Previously visible canonical navigation windows, retained only as locators. Their entries establish neither substantive bodies nor suitability. Current evidence IDs remain unique; use source handles or read_evidence to inspect the required canonical body. Omitted windows reflect a bounded operational memory, not corpus absence."
            },
            projection = new
            {
                context.CandidateInventoryAdmissionBlockedCount,
                nativeHistoryOmittedTurnCount,
                capacityRepairOnly,
                sourceOnlyAnchorCount = evidence.Count(item => item.Content.Length == 0),
                instruction = "The excerpt view is bounded. Empty source anchors and ledger evidence IDs locate canonical sources but do not establish omitted body facts. A bound never establishes corpus absence."
            },
            researchTools = new
            {
                tools = BuildResearchToolsForPrompt(),
                researchAllowed = documentaryResearchAllowed,
                evidenceReadingAllowed = true,
                instruction = capacityRepairOnly
                    ? "The active inventory is at capacity. This is one bounded local repair opportunity. Only read_evidence for the visible incomplete candidate bodies is allowed; do not request new corpus discoveries."
                    : "Use the declared operations and current resource budgets.",
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

           Ledger notes explain incomplete bodies and rejected interpretations.
           For needs_body candidates, locate complementary canonical pages or
           exact titles before abandoning the item. Remembered navigation excerpts
           remain visible documentary locators. Source-only anchors with empty
           content permit navigation but establish no body facts. Tool history is
           operational memory; only current visible excerpts are documentary proof.

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
