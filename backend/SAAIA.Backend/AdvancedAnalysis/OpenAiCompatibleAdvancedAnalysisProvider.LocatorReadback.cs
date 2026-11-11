using SAAIA.Backend.Retrieval;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const int MaximumLocatorReadbackPages = 4;
    private const int LocatorReadbackTopK = 12;

    private sealed record PendingTitleLocator(string SourceKey, AdvancedAnalysisResultEvidence Scope, int Page)
    {
        public AdvancedAnalysisSourceTitle? ObservedTitle { get; init; }
    }

    private static IReadOnlyList<PendingTitleLocator> FindPendingTitleLocators(
        AdvancedAnalysisLoadDescriptor load,
        IReadOnlyList<PromptEvidenceItem> observations,
        NativeResearchTurn? turn)
    {
        if (!load.StructuredLayout || load.AnswerUnitCount < 2
            || !string.Equals(load.AtomicEvidenceMode, "named_item", StringComparison.OrdinalIgnoreCase)
            || observations is not PromptEvidenceProjection { SourceCatalogueEnabled: true, SourceCatalogue.Count: > 0 } projection
            || turn is null || turn.Rejection is not null)
            return [];

        static bool HasVisibleBody(PromptEvidenceItem item)
            => item.ContentRole != RetrievalContentClassifier.NavigationRole
                && GetVisibleEvidenceContents(item).Any(content => !string.IsNullOrWhiteSpace(content)
                    && RetrievalContentClassifier.AnalyzeEvidenceContent(content, item.CandidateTitle).ContentRole
                        != RetrievalContentClassifier.NavigationRole
                    && (item.CandidateTitle is not { Length: > 0 } title
                        || !RetrievalContentClassifier.IsIdentityOnlyCandidateEvidence(content, title, title)));

        var located = new List<PendingTitleLocator>();
        var seen = new HashSet<(string SourceKey, int Page, string Title)>();
        var visibleBodies = new List<AdvancedAnalysisResultEvidence>();
        foreach (var (callId, request) in turn.CallRequests)
        {
            if (!turn.Results.TryGetValue(BuildSearchIdentity(request), out var result)
                || result.ResourceLimit is not null || result.NotExecutedAfterResourceLimit)
                continue;
            if (request.Operation is "read_source" or "find_source_text"
                && request.NativeObservationView is null && request.TableObservationView is null)
                visibleBodies.AddRange(result.Evidence.Where(item => item.Reference.EvidenceId is { } id
                    && observations.Any(visible => visible.EvidenceId == id && HasVisibleBody(visible)))
                    .Select(item => item.Reference));
            if (request.Operation != AdvancedAnalysisSourceTitlePage.Operation || result.SourceTitlePage is not { } page
                || !turn.CallSourceScopes.TryGetValue(callId, out var scope)
                || CurrentNativeSourceKey(projection.SourceCatalogue, scope) is not { } sourceKey)
                continue;
            foreach (var item in page.Items)
                if (item.PhysicalPageStart is > 0
                    && seen.Add((sourceKey, item.PhysicalPageStart.Value, item.Title)))
                    located.Add(new(sourceKey, scope, item.PhysicalPageStart.Value));
        }
        return located.Where(locator => !visibleBodies.Any(body =>
            body.DocId == locator.Scope.DocId && body.RevisionId == locator.Scope.RevisionId
            && body.SourceHash == locator.Scope.SourceHash && body.DocPath == locator.Scope.DocPath
            && body.PageStart <= locator.Page && body.PageEnd >= locator.Page)).ToArray();
    }

    private async Task<object?> ReadLocatedBodiesForInsufficiencyReviewAsync(
        AdvancedAnalysisProviderRequest request,
        SynthesisResearchContext context,
        IReadOnlyList<PromptEvidenceItem> observations,
        IReadOnlyList<PendingTitleLocator> locators,
        CancellationToken cancellationToken)
    {
        if (locators.Count == 0)
            return null;

        var budget = context.Tools.Budget;
        if (!HasDocumentaryResearchBudget(budget))
            return null;

        var topK = Math.Clamp(_options.MaximumSearchTopK, 1, LocatorReadbackTopK);
        var priorReads = context.PriorSearches.Select((search, index) =>
                (search, status: context.PriorSearchExecutionStatuses.GetValueOrDefault(index)))
            .Where(prior => prior.search.Operation == "read_source"
                && prior.search.TableOrdinal is null && prior.search.TableRowOffset is null
                && prior.search.TableRowLimit is null && prior.search.TableObservationId is null
                && prior.search.TableObservationView is null && prior.search.NativeObservationView is null)
            .ToArray();
        // Rotate pages across sources before taking a second page from one.
        // Within each round, prefer a source with no successful primary read.
        // Source novelty must not consume the whole sample or imply relevance.
        var pending = locators
            .DistinctBy(locator => (locator.SourceKey, locator.Page))
            .Where(locator => !priorReads.Any(prior =>
                prior.search.DocId == locator.Scope.DocId && prior.search.RevisionId == locator.Scope.RevisionId
                && prior.search.DocPath == locator.Scope.DocPath
                && prior.search.PageStart <= locator.Page && prior.search.PageEnd >= locator.Page
                && (prior.status == "succeeded"
                    // A narrower page or larger result cap may repair a failed
                    // window; repeating the same failed window cannot.
                    || prior.status == "failed" && prior.search.PageStart == locator.Page
                    && prior.search.PageEnd == locator.Page && prior.search.TopK >= topK)))
            .ToArray();
        var reads = pending
            .GroupBy(locator => locator.SourceKey, StringComparer.Ordinal)
            .SelectMany(source => source.Select((locator, rank) => (locator, rank,
                alreadyReadSource: priorReads.Any(prior => prior.status == "succeeded"
                    && prior.search.DocId == locator.Scope.DocId && prior.search.RevisionId == locator.Scope.RevisionId
                    && prior.search.DocPath == locator.Scope.DocPath))))
            .OrderBy(entry => entry.rank)
            .ThenBy(entry => entry.alreadyReadSource)
            .Take(Math.Min(MaximumLocatorReadbackPages, budget?.RemainingCalls ?? MaximumLocatorReadbackPages))
            .Select(entry =>
            {
                var locator = entry.locator;
                return (locator.Scope, Read: new AdvancedAnalysisSearchRequest(
                    $"Canonical source physical pages {locator.Page}-{locator.Page}",
                    TopK: topK, DocId: locator.Scope.DocId, DocPath: locator.Scope.DocPath,
                    PageStart: locator.Page, PageEnd: locator.Page,
                    Operation: "read_source", RevisionId: locator.Scope.RevisionId)
                    { ObservedSourceHash = locator.Scope.SourceHash });
            }).ToArray();
        using var probe = BackendDiagnostics.Start("llm.locator_readback", _logger);
        probe.Set("job_id", request.JobId);
        probe.Set("observed_locator_count", locators.Count);
        probe.Set("unread_candidate_count", pending.Length);
        probe.Set("candidate_source_count", pending.Select(locator => locator.SourceKey).Distinct(StringComparer.Ordinal).Count());
        probe.Set("uninspected_source_count", pending.Where(locator => !priorReads.Any(prior => prior.status == "succeeded"
                && prior.search.DocId == locator.Scope.DocId && prior.search.RevisionId == locator.Scope.RevisionId
                && prior.search.DocPath == locator.Scope.DocPath))
            .Select(locator => locator.SourceKey).Distinct(StringComparer.Ordinal).Count());
        probe.Set("planned_read_count", reads.Length);
        probe.Set("planned_source_count", reads.Select(read => read.Scope.DocId).Distinct(StringComparer.Ordinal).Count());
        probe.Set("maximum_reads_per_source", reads.GroupBy(read => read.Scope.DocId, StringComparer.Ordinal)
            .Select(group => group.Count()).DefaultIfEmpty(0).Max());
        if (reads.Length == 0)
        {
            probe.Set("executed_read_count", 0);
            probe.Complete("skipped", "no_unread_candidate");
            return null;
        }
        var retainedForBatch = RetainedResearchEvidenceIds(context, observations);
        context.Tools.BeginEvidenceBatch(retainedForBatch);
        if (context.Tools.Budget?.EvidenceRotationEnabled is true)
            await SaveCandidateInventoryCheckpointAsync(context, cancellationToken,
                retainedForBatch.ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);

        var results = new Dictionary<string, AdvancedAnalysisSearchObservation>(StringComparer.OrdinalIgnoreCase);
        var firstReadIndex = context.PriorSearches.Count;
        var timeLimitReached = false;
        foreach (var (scope, read) in reads)
        {
            // A completed read can consume the remaining time or working set.
            if (context.ResourceLimit is not null || !HasDocumentaryResearchBudget(context.Tools.Budget))
                break;
            // A catalogue source can leave the working set during an earlier read.
            if (!context.Tools.Evidence.Any(item => item.Reference.DocId == scope.DocId
                    && item.Reference.RevisionId == scope.RevisionId && item.Reference.DocPath == scope.DocPath
                    && item.Reference.SourceHash == scope.SourceHash))
                continue;
            var readIndex = context.PriorSearches.Count;
            try
            {
                context.ResourceLimit = await ExecuteSearchBatchAsync(context.Tools, [read], context.PreviouslyExecuted,
                    context.PriorSearches, context.EvidenceGroups, context.RetrievalQueriesByEvidenceId,
                    cancellationToken, results, allowResourceLimitFeedback: true,
                    executionStatuses: context.PriorSearchExecutionStatuses).ConfigureAwait(false);
            }
            catch (AdvancedAnalysisToolException error) when (error.ErrorCode == "tool_time_limit_exceeded")
            {
                // This optional read can use the rest of the tool deadline.
                // Still allow the existing review to use its current evidence.
                if (context.PriorSearches.Count > readIndex)
                    context.PriorSearchExecutionStatuses[readIndex] = "failed";
                timeLimitReached = true;
                break;
            }
        }

        if (results.Values.Any(result => result.Evidence.Count > 0))
            context.DiscoveryBaselineEvidenceIds = null;
        PruneRetiredEvidenceCopies(context);
        RememberLatestExplicitRead(context, reads.Select(item => item.Read).ToArray(), results);

        // The native turn still correlates only calls made by the model.
        // Backend reads contribute through the usual revalidated user.evidence.
        var executedReads = context.PriorSearches.Count - firstReadIndex;
        if (executedReads > 0)
            context.LocatorReadbackTurn = context.NativeTurn;
        var readsWithEvidence = results.Values.Count(result => result.Evidence.Count > 0);
        probe.Set("executed_read_count", executedReads);
        probe.Set("reads_with_evidence", readsWithEvidence);
        probe.Complete("completed", timeLimitReached ? "tool_time_limit_exceeded"
            : readsWithEvidence > 0 ? "evidence_returned" : "no_evidence_returned");
        return new
        {
            executedReads,
            readsWithEvidence,
            timeLimitReached,
            instruction = "SAAIA checked a bounded sample of physical pages from the previous title listings. Use only the currently visible substantive passages in user.evidence. A title and a nearby passage do not establish identity or suitability. This sample cannot establish corpus absence."
        };
    }
}
