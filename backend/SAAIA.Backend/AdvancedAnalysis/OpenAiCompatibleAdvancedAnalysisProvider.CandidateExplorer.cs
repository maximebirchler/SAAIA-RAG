using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record CandidateExplorerRoleCoverage(
        string TargetRole,
        int RequiredCount,
        int ReserveTargetCount,
        int BodyVerifiedCount);

    private sealed record CandidateExplorerCoverage(
        bool Ready,
        int RequiredDistinctCount,
        int BodyVerifiedDistinctCount,
        IReadOnlyList<CandidateExplorerRoleCoverage> Roles,
        IReadOnlyList<string> BodyEvidenceIds,
        IReadOnlyList<string> EligibleCandidateKeys);

    private sealed record CandidateExplorerDossier(
        string Outcome,
        string Reason,
        int RequiredDistinctCount,
        int BodyVerifiedDistinctCount,
        IReadOnlyList<CandidateExplorerRoleCoverage> Roles,
        IReadOnlyList<string> BodyEvidenceIds,
        IReadOnlyList<string> EligibleCandidateKeys,
        string Instruction);

    private bool CandidateExplorerEnabled(AdvancedAnalysisProviderRequest request)
        => _options.NativeCandidateExplorerEnabled
           && _options.AdaptiveResearchEnabled
           && _options.NativeResearchToolsEnabled
           && _options.NativeResearchWorkspaceEnabled
           && _options.NativeResearchTopology == "agent"
           && CandidateInventoryEnabled(request);

    private CandidateExplorerCoverage BuildCandidateExplorerCoverage(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<CandidateInventoryItem> inventory)
    {
        var verified = inventory.Where(item =>
                (item.Status is "body_verified" or "selected")
                && item.BodyEvidenceIds.Count > 0)
            .ToArray();
        var requestedRoles = request.Handoff.Load.Columns
            .ToHashSet(StringComparer.Ordinal);
        var eligible = requestedRoles.Count == 0
            ? verified
            : verified.Where(item => item.TargetRoles.Any(requestedRoles.Contains))
                .ToArray();
        var requiredDistinct = Math.Max(1, request.Handoff.Load.AnswerUnitCount);
        var requiredPerRole = Math.Max(1, request.Handoff.Load.RowCount);
        var reserve = Math.Clamp(_options.CandidateExplorerReservePerRole, 0, 8);
        var roles = request.Handoff.Load.Columns.Select(role =>
        {
            var count = eligible.Count(item =>
                item.TargetRoles.Contains(role, StringComparer.Ordinal));
            return new CandidateExplorerRoleCoverage(
                role,
                requiredPerRole,
                Math.Min(64, requiredPerRole + reserve),
                count);
        }).ToArray();
        return new CandidateExplorerCoverage(
            eligible.Length >= requiredDistinct
            && roles.All(role => role.BodyVerifiedCount >= role.RequiredCount),
            requiredDistinct,
            eligible.Length,
            roles,
            eligible.SelectMany(item => item.BodyEvidenceIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            eligible.Select(item => item.Key)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    private async Task RunCandidateExplorerAsync(
        AdvancedAnalysisProviderRequest request,
        bool runSemanticCritic,
        SynthesisResearchContext context,
        List<CompletionResult> completions,
        CancellationToken cancellationToken)
    {
        var reservedFinalCalls = 1 + (runSemanticCritic ? 1 : 0);
        object? explorerFeedback = null;
        var correction = 0;
        while (true)
        {
            var round = await CompleteWithCorpusResearchAsync(
                    request,
                    correction == 0 ? "candidate-explorer" : $"candidate-explorer-correction-{correction}",
                    BuildCandidateExplorerSystemPrompt(),
                    observations => BuildCandidateExplorerUserPrompt(
                        request,
                        observations,
                        explorerFeedback),
                    Math.Clamp(_options.CandidateExplorerMaxTokens, 512, 16_384),
                    reservedFinalCalls,
                    context,
                    completions,
                    cancellationToken,
                    candidateExplorer: true)
                .ConfigureAwait(false);
            completions.Add(round.Completion);

            var terminal = ParseCandidateExplorerTerminal(round.Completion.Content);
            var coverage = BuildCandidateExplorerCoverage(
                request,
                context.CandidateInventory);
            if (terminal.Outcome == "candidate_dossier_ready" && coverage.Ready)
            {
                context.CandidateExplorerDossier = new CandidateExplorerDossier(
                    "ready",
                    terminal.Reason,
                    coverage.RequiredDistinctCount,
                    coverage.BodyVerifiedDistinctCount,
                    coverage.Roles,
                    coverage.BodyEvidenceIds,
                    coverage.EligibleCandidateKeys,
                    "For each claim coordinate, choose one distinct body-verified candidate whose targetRoles contains that coordinate's columnLabel. Do not select an unassigned inventory item. If further research reveals a better item, first save it with substantive body evidence and the intended target role. Never reuse one candidate for two distinct coordinates.");
                context.FocusSearches.Clear();
                return;
            }

            var maximumCalls = Math.Clamp(
                _options.ExternalMaximumCallsPerJob,
                1,
                1_024);
            var budget = context.Tools.Budget;
            var canContinue = maximumCalls - completions.Count - reservedFinalCalls > 0
                              && context.ResourceLimit is null
                              && (budget is null || budget.RemainingCalls > 0
                                  && budget.RemainingElapsedMilliseconds > 0
                                  && budget.RemainingEvidenceItems is not 0);
            if (!canContinue)
            {
                context.CandidateExplorerDossier = new CandidateExplorerDossier(
                    "bounded_gap",
                    terminal.Reason,
                    coverage.RequiredDistinctCount,
                    coverage.BodyVerifiedDistinctCount,
                    coverage.Roles,
                    coverage.BodyEvidenceIds,
                    coverage.EligibleCandidateKeys,
                    "The Explorer stopped with a bounded gap. Use only role-assigned body-verified candidates for supported cells and describe the smallest exact remaining gap; do not treat unassigned headings as replacements.");
                return;
            }

            explorerFeedback = new
            {
                reasonCode = terminal.Outcome == "candidate_dossier_ready"
                    ? "candidate_dossier_coverage_incomplete"
                    : "candidate_dossier_stopped_while_research_available",
                submittedOutcome = terminal.Outcome,
                coverage,
                instruction = "The Explorer phase is not terminal yet. Continue documentary research from the measured gaps, save newly verified candidates, then reassess. Do not draft the user's deliverable and do not describe an operational bound while researchTools.researchAllowed remains true."
            };
            correction++;
        }
    }

    private static (string Outcome, string Reason) ParseCandidateExplorerTerminal(
        string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            var root = document.RootElement;
            var properties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Select(property => property.Name).ToArray()
                : [];
            if (properties.Length != 2
                || !properties.Contains("outcome", StringComparer.Ordinal)
                || !properties.Contains("reason", StringComparer.Ordinal)
                || root.GetProperty("outcome").ValueKind != JsonValueKind.String
                || root.GetProperty("reason").ValueKind != JsonValueKind.String)
                throw new JsonException();
            var outcome = root.GetProperty("outcome").GetString()!;
            var reason = root.GetProperty("reason").GetString()!;
            if (outcome is not ("candidate_dossier_ready" or "candidate_dossier_bounded")
                || string.IsNullOrWhiteSpace(reason)
                || reason.Length > 500)
                throw new JsonException();
            return (outcome, reason);
        }
        catch (JsonException)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_candidate_explorer_protocol_invalid");
        }
    }

    private string BuildCandidateExplorerSystemPrompt()
        => """
           You are the SAAIA documentary Candidate Explorer. Build a bounded,
           reusable dossier before the Writer drafts the user's deliverable.
           Never answer the user, create the requested table, or choose final
           placements. Discover exact named candidates, follow navigation
           entries to substantive canonical bodies, and use
           save_candidate_inventory after every useful discovery. Keep proposed
           target roles as your semantic assessment; the application only checks
           counts and documentary identities.

           A source-exact candidateTitle can still be a subordinate section,
           component or stage rather than the complete candidate identity. Inspect
           its content before assigning a role. When that same body contains the
           complete item name as an exact displayed line, save that complete phrase
           as exactTitle. Otherwise reject section labels, component headings,
           ingredient groups, schedules, example-menu categories and other open
           templates; they cannot fill a distinct named-item coordinate.

           candidateInventory.coverage gives the minimum body-verified count per
           role. candidateInventory also exposes the total inventory. Reach at
           least load.answerUnitCount distinct body-verified candidates and the
           required count for every target role. When budget permits, aim for the
           reserve target shown in coverage rather than stopping at the first
           barely complete set. For a flat collection with no target roles,
           targetRoles stays empty and body_verified means that you judge the
           candidate suitable for the request as a whole; reject observed named
           items that do not meet that request. A contents entry is a locator, not a body. Search
           an observed exact title in its opaque source and read canonical
           physical pages when needed. Preserve candidates across focus changes;
           do not repeat completed searches.

           When coverage is incomplete and current evidence contains a contents
           or index with concrete named entries, prefer following promising entry
           names in that same opaque source with find_source_text or read_source.
           Use another broad semantic search when the visible navigation does not
           offer a useful candidate for the remaining gap. You choose which names
           and operations are relevant; do not merely count the locator itself.

           While researchTools.researchAllowed is true, continue from measured
           gaps instead of claiming an operational bound. When the dossier is
           mechanically covered and you judge it ready for synthesis, return only
           {"outcome":"candidate_dossier_ready","reason":"short assessment"}.
           When research is no longer allowed and gaps remain, return only
           {"outcome":"candidate_dossier_bounded","reason":"short exact operational gap"}.
           These objects are internal handoffs, never user answers.
           """ + "\n" + BuildSynthesisResearchContract();

    private string BuildCandidateExplorerUserPrompt(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<PromptEvidenceItem> evidence,
        object? explorerFeedback)
        => JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            load = BuildPromptLoad(request.Handoff.Load),
            claimCoordinates = BuildStructuredClaimCoordinates(
                request.Handoff.Load),
            explorerFeedback,
            evidence
        }, JsonOptions);
}
