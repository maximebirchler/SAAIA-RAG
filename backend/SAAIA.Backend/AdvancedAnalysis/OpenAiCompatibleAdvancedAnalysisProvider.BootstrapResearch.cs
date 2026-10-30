using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    // A stopped bootstrap batch is historical feedback, not a permanent ban on
    // research after the first visible projection activates protected rotation.
    private sealed record BootstrapResearchInterruption(
        AdvancedAnalysisResearchResourceLimit Limit, AdvancedAnalysisSearchRequest RefusedQuery,
        IReadOnlyList<AdvancedAnalysisSearchRequest> OmittedQueries, int RefusedPriorSearchIndex,
        int SuccessfulQueryCount);

    private static async Task<BootstrapResearchInterruption?> ExecuteBootstrapSearchBatchAsync(
        IAdvancedAnalysisToolGateway tools, IReadOnlyList<AdvancedAnalysisSearchRequest> plannedQueries,
        HashSet<string> previouslyExecuted, List<AdvancedAnalysisSearchRequest> priorSearches,
        List<IReadOnlyList<AdvancedAnalysisResolvedEvidence>> evidenceGroups,
        Dictionary<string, HashSet<string>> retrievalQueriesByEvidenceId, CancellationToken cancellationToken,
        Dictionary<int, string> executionStatuses)
    {
        var unique = plannedQueries.GroupBy(BuildSearchIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        var observations = new Dictionary<string, AdvancedAnalysisSearchObservation>(StringComparer.OrdinalIgnoreCase);
        var limit = await ExecuteSearchBatchAsync(tools, unique, previouslyExecuted, priorSearches,
            evidenceGroups, retrievalQueriesByEvidenceId, cancellationToken, observations,
            allowResourceLimitFeedback: true, allowReadWindowResourceLimitFeedback: false,
            stopBatchOnEvidenceCapacity: true, executionStatuses: executionStatuses).ConfigureAwait(false);
        if (limit is null) return null;
        var successful = observations.Values.Where(item => item.ResourceLimit is null
            && !item.NotExecutedAfterResourceLimit).ToArray();
        // Existing handoff evidence is insufficient: this same batch must have
        // completed a canonical admission before a capacity refusal is recoverable.
        if (limit.ReasonCode != "accumulated_evidence_limit_exceeded"
            || !successful.Any(item => item.Evidence.Count > 0) || tools.Evidence.Count == 0)
            throw new AdvancedAnalysisToolException(limit.ReasonCode, limit);
        var refused = unique.Single(query => observations.TryGetValue(BuildSearchIdentity(query), out var item)
            && item.ResourceLimit is not null && !item.NotExecutedAfterResourceLimit);
        var identity = BuildSearchIdentity(refused);
        var priorIndex = priorSearches.FindLastIndex(query => BuildSearchIdentity(query) == identity);
        if (priorIndex < 0)
            throw new AdvancedAnalysisProviderException("advanced_bootstrap_prior_identity_changed");
        previouslyExecuted.Remove(identity);
        var omitted = unique.Where(query => observations.TryGetValue(BuildSearchIdentity(query), out var item)
            && item.NotExecutedAfterResourceLimit && !previouslyExecuted.Contains(BuildSearchIdentity(query))).ToArray();
        return new(limit, refused, omitted, priorIndex, successful.Length);
    }

    private static object BootstrapQueryForPrompt(AdvancedAnalysisSearchRequest query)
    {
        static string Preview(string value, int maximum)
        {
            var count = Math.Min(value.Length, maximum);
            if (count > 0 && char.IsHighSurrogate(value[count - 1])) count--;
            return value[..count];
        }
        var text = Preview(query.Query, 256);
        var category = Preview(query.Category ?? string.Empty, 80);
        var hint = Preview(query.DocumentHint ?? string.Empty, 160);
        return new
        {
            query.Operation, query.TopK, query = text, queryCharacters = query.Query.Length,
            queryTruncated = text.Length != query.Query.Length, category,
            categoryTruncated = category.Length != (query.Category?.Length ?? 0), documentHint = hint,
            documentHintTruncated = hint.Length != (query.DocumentHint?.Length ?? 0), query.PageStart, query.PageEnd
        };
    }

    private static void AddPriorSearchExecutionFeedback(JsonArray? history,
        IReadOnlyList<AdvancedAnalysisSearchRequest> searches, IReadOnlyDictionary<int, string> statuses,
        IReadOnlySet<string> previouslyExecuted, bool retryAllowed)
    {
        if (history is null) return;
        // PriorSearches only appends: separate attempts keep separate statuses,
        // including a failed attempt followed by a successful identical retry.
        for (var index = 0; index < history.Count && index < searches.Count; index++)
            if (history[index] is JsonObject prior)
            {
                var status = statuses.GetValueOrDefault(index, "unknown");
                prior["executionStatus"] = status;
                if (status == "failed")
                    prior["retryAllowed"] = retryAllowed
                        && !previouslyExecuted.Contains(BuildSearchIdentity(searches[index]));
            }
    }

    private void AddBootstrapResearchFeedback(JsonObject payload, SynthesisResearchContext context, Guid jobId)
    {
        var research = payload["researchTools"] as JsonObject;
        var continuationAllowed = research?["researchAllowed"]?.GetValue<bool>() is true;
        AddPriorSearchExecutionFeedback(research?["priorSearches"] as JsonArray,
            context.PriorSearches, context.PriorSearchExecutionStatuses,
            context.PreviouslyExecuted, continuationAllowed);
        if (context.BootstrapInterruption is not { } interrupted) return;
        if (research?["priorSearches"] is JsonArray searches
            && interrupted.RefusedPriorSearchIndex >= 0 && interrupted.RefusedPriorSearchIndex < searches.Count
            && searches[interrupted.RefusedPriorSearchIndex] is JsonObject failed)
        {
            failed["executionStatus"] = "resource_limit_before_evidence_admission";
            failed["admittedEvidenceCount"] = 0;
            failed["retryAllowed"] = continuationAllowed
                && !context.PreviouslyExecuted.Contains(BuildSearchIdentity(interrupted.RefusedQuery));
        }
        payload["bootstrapResearch"] = JsonSerializer.SerializeToNode(new
        {
            status = "initial_batch_interrupted", interrupted.Limit.ReasonCode,
            interrupted.Limit.MaximumEvidenceItems, interrupted.Limit.ConsumedEvidenceItems,
            interrupted.Limit.RequestedNewEvidenceItems, interrupted.SuccessfulQueryCount,
            refusedOperationCount = 1, refusedQuery = BootstrapQueryForPrompt(interrupted.RefusedQuery),
            omittedOperationCount = interrupted.OmittedQueries.Count,
            omittedQueries = interrupted.OmittedQueries.Take(8).Select(BootstrapQueryForPrompt).ToArray(),
            omittedQueryDetailsOmittedCount = Math.Max(0, interrupted.OmittedQueries.Count - 8),
            omittedOperationStatus = "not_executed_after_resource_limit", researchCanContinue = continuationAllowed,
            instruction = "The initial planner batch stopped at an operational evidence-admission limit. Earlier admitted canonical evidence remains valid; the refused result admitted nothing and the omitted operations never ran. This is not proof of corpus absence or insufficient content. Query previews may be explicitly shortened. Use current source handles and declared tools to continue under the current protected working-set, model-call and tool budgets; a refused query may be retried when it is still needed. Do not treat omitted queries as completed or publish unsupported claims."
        }, JsonOptions);
        if (context.BootstrapInterruptionReported) return;
        using var probe = BackendDiagnostics.Start("advanced.bootstrap_research", _logger);
        probe.Set("job_id", jobId);
        probe.Set("maximum_evidence_items", interrupted.Limit.MaximumEvidenceItems);
        probe.Set("admitted_evidence_items", interrupted.Limit.ConsumedEvidenceItems);
        probe.Set("requested_new_evidence_items", interrupted.Limit.RequestedNewEvidenceItems);
        probe.Set("successful_operation_count", interrupted.SuccessfulQueryCount);
        probe.Set("executed_operation_count", interrupted.SuccessfulQueryCount + 1);
        probe.Set("refused_operation_count", 1);
        probe.Set("omitted_operation_count", interrupted.OmittedQueries.Count);
        probe.Set("research_can_continue", continuationAllowed);
        probe.Set("working_set_rotation_enabled", context.Tools.Budget?.EvidenceRotationEnabled);
        probe.Complete("continued", "initial_batch_capacity_refusal");
        context.BootstrapInterruptionReported = true;
    }
}
