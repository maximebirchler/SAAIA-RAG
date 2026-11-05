using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    // Local to this synthesis round. Counts describe executed operations, never
    // supported items, corpus coverage, or an obligation to invent an answer.
    private sealed class DiscoveryChallengeState
    {
        public bool EmptyReviewStarted { get; set; }
        public bool Used { get; set; }
        public int AttemptedChecks { get; set; }
        public int SuccessfulChecks { get; set; }
        public object? Prompt { get; set; }
    }

    private static bool IsEmptyDiscoveryInsufficiency(string raw,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence, AdvancedAnalysisProviderRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
                    != root.EnumerateObject().Count()
                || !root.TryGetProperty("claims", out var claims)
                || claims.ValueKind != JsonValueKind.Array || claims.GetArrayLength() != 0)
                return false;
            var parsed = ParseResult(raw, evidence, request);
            return parsed.Outcome == "insufficient_documentation" && parsed.Claims.Count == 0;
        }
        catch (Exception error) when (error is JsonException or AdvancedAnalysisProviderException)
        {
            // The existing protocol/binding path still owns invalid proposals.
            return false;
        }
    }

    private bool TryScheduleDiscoveryChallenge(DiscoveryChallengeState state, bool reviewed,
        string raw, AdvancedAnalysisProviderRequest request, SynthesisResearchContext context,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence, IReadOnlyList<PromptEvidenceItem> observations,
        int remainingCalls, int reservedFinalCalls, IReadOnlyList<PendingTitleLocator> pendingLocators)
    {
        if (!reviewed || !state.EmptyReviewStarted
            || ResolveSelectionMode(request.Handoff.Load) != "distinct_named_items"
            || !IsEmptyDiscoveryInsufficiency(raw, evidence, request))
            return false;
        var budget = context.Tools.Budget;
        var reason = context.CandidateExplorerDossier is not null ? "candidate_explorer_dossier_present"
            : state.Used ? "discovery_challenge_already_used"
            : state.SuccessfulChecks > 0 ? "new_model_documentary_check_succeeded"
            : remainingCalls < 2 ? "model_budget_unavailable"
            : context.ResourceLimit is not null || !HasDocumentaryResearchBudget(budget)
                ? "documentary_budget_unavailable"
            : !HasUnexaminedDiscoveryLocator(context, pendingLocators) && !HasOpenResearchLocator(observations)
                ? "no_current_research_locator" : null;
        var scheduled = reason is null;
        if (scheduled)
        {
            state.Used = true;
            state.Prompt = new
            {
                reasonCode = "empty_insufficiency_without_model_directed_discovery_after_review",
                priorReason = BoundedInsufficiencyReason(raw),
                selectionContract = BuildResolvedSelectionContract(request.Handoff.Load),
                documentaryEvidence = false,
                instruction = "Since the first empty insufficiency, no new model-directed documentary operation has succeeded. Automatic review reads and cache windows do not establish that a model-chosen discovery hypothesis was tested. Choose a different, justified search, title-navigation continuation, literal find or physical-page read of the unresolved deficit using the currently offered tools and revalidated sources. Use observed names, locators and prior-search statuses; do not repeat a succeeded operation. Reading another cache window alone is not a new documentary check. The code has not identified enough items or proved they exist. If no relevant different check can be justified within the remaining budgets, return a precise insufficient_documentation result scoped to the examined evidence; do not invent a complete answer. This is one bounded discovery opportunity, not an added budget or a requirement to succeed."
            };
        }
        using var probe = BackendDiagnostics.Start("llm.discovery_challenge", _logger);
        probe.Set("job_id", request.JobId);
        probe.Set("scheduled", scheduled);
        probe.Set("used", state.Used);
        probe.Set("remaining_model_calls", remainingCalls);
        probe.Set("reserved_final_calls", reservedFinalCalls);
        probe.Set("remaining_tool_calls", budget?.RemainingCalls);
        probe.Set("remaining_tool_ms", budget?.RemainingElapsedMilliseconds);
        probe.Set("current_source_count", observations is PromptEvidenceProjection { SourceCatalogueEnabled: true } projection
            ? projection.SourceCatalogue.Count : observations.Select(item => item.SourceKey).Distinct(StringComparer.Ordinal).Count());
        probe.Set("novel_model_checks_since_review", state.SuccessfulChecks);
        probe.Complete(scheduled ? "scheduled" : "skipped", reason ?? "empty_reviewed_insufficiency_without_model_discovery");
        return scheduled;
    }

    private static bool HasUnexaminedDiscoveryLocator(SynthesisResearchContext context,
        IReadOnlyList<PendingTitleLocator> locators)
        => locators.Any(locator => !context.PriorSearches.Where((_, index) =>
                context.PriorSearchExecutionStatuses.GetValueOrDefault(index) == "succeeded")
            .Any(search => search.Operation == "read_source"
                && search.TableOrdinal is null && search.TableRowOffset is null
                && search.TableRowLimit is null && search.TableObservationId is null
                && search.TableObservationView is null && search.NativeObservationView is null
                && search.DocId == locator.Scope.DocId && search.RevisionId == locator.Scope.RevisionId
                && search.DocPath == locator.Scope.DocPath
                && search.PageStart <= locator.Page && search.PageEnd >= locator.Page));

    private void ObserveDiscoveryChallengeOperations(DiscoveryChallengeState state,
        AdvancedAnalysisProviderRequest request, bool nativeCalls,
        IReadOnlyList<AdvancedAnalysisSearchRequest> queries,
        IReadOnlyDictionary<string, AdvancedAnalysisSearchObservation> results)
    {
        if (!state.EmptyReviewStarted || !nativeCalls) return;
        var attempted = 0;
        var succeeded = 0;
        foreach (var query in queries.DistinctBy(BuildSearchIdentity, StringComparer.OrdinalIgnoreCase))
        {
            if (query.Operation is not ("search_corpus" or "list_source_titles" or "find_source_text" or "read_source")
                || !results.TryGetValue(BuildSearchIdentity(query), out var observation)
                || observation.NotExecutedAfterResourceLimit)
                continue;
            attempted++;
            if (observation.ResourceLimit is null) succeeded++;
        }
        state.AttemptedChecks += attempted;
        state.SuccessfulChecks += succeeded;
        if (attempted == 0) return;
        using var probe = BackendDiagnostics.Start("llm.discovery_challenge", _logger);
        probe.Set("job_id", request.JobId);
        probe.Set("used", state.Used);
        probe.Set("attempted_model_checks", attempted);
        probe.Set("succeeded_model_checks", succeeded);
        probe.Set("total_attempted_model_checks_since_review", state.AttemptedChecks);
        probe.Set("novel_model_checks_since_review", state.SuccessfulChecks);
        probe.Complete(succeeded > 0 ? "succeeded" : "attempted", "model_documentary_operations_observed");
    }
}
