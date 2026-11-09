using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const int MaximumRetainedTitleLocators = 64;

    private static bool RetainedTitleLocatorIsCurrent(PendingTitleLocator locator,
        IReadOnlyList<PromptSourceHandle> sources)
    {
        if (locator.Page <= 0) return false;
        var current = sources.Where(source => source.SourceKey == locator.SourceKey).ToArray();
        return current.Length == 1 && MatchesBoundReadSource(current[0].SourceReference, locator.Scope);
    }

    private void PruneRetainedTitleLocators(AdvancedAnalysisProviderRequest request,
        SynthesisResearchContext context, IReadOnlyList<AdvancedAnalysisResolvedEvidence> active)
    {
        if (!_options.NativeSourceTitleNavigationEnabled)
        {
            context.RetainedTitleLocators = [];
            return;
        }
        if (context.RetainedTitleLocators.Count == 0) return;
        var sources = BuildPromptSourceCatalogue(active, context.PromptSourceKeys);
        var retained = context.RetainedTitleLocators.Where(locator => RetainedTitleLocatorIsCurrent(locator, sources)).ToArray();
        var expired = context.RetainedTitleLocators.Count - retained.Length;
        if (expired == 0) return;
        context.RetainedTitleLocators = retained;
        using var probe = BackendDiagnostics.Start("llm.retained_title_locators", _logger);
        probe.Set("job_id", request.JobId);
        probe.Set("expired_locator_count", expired);
        probe.Set("retained_locator_count", retained.Length);
        probe.Set("retained_source_count", retained.Select(locator => locator.SourceKey).Distinct(StringComparer.Ordinal).Count());
        probe.Complete("expired", "source_scope_no_longer_current");
    }

    private void RememberRetainedTitleLocators(AdvancedAnalysisProviderRequest request,
        SynthesisResearchContext context)
    {
        if (!_options.NativeSourceTitleNavigationEnabled || context.NativeTurn is not { Rejection: null } turn) return;
        var sources = BuildPromptSourceCatalogue(context.Tools.Evidence, context.PromptSourceKeys);
        var retained = context.RetainedTitleLocators.Where(locator => RetainedTitleLocatorIsCurrent(locator, sources)).ToList();
        var observed = 0;
        foreach (var (callId, query) in turn.CallRequests)
        {
            if (query.Operation != AdvancedAnalysisSourceTitlePage.Operation || turn.DeferredCallIds.Contains(callId)
                || !turn.CallSourceScopes.TryGetValue(callId, out var scope)
                || !turn.Results.TryGetValue(BuildSearchIdentity(query), out var result)
                || result.NotExecutedAfterResourceLimit || result.ResourceLimit is not null || result.Evidence.Count != 0
                || result.SourceTitlePage is not { } page || page.Offset != query.Offset
                || !AdvancedAnalysisSourceTitlePage.IsValidWindow(query.Offset, query.TopK)
                || page.NextOffset is { } next && (next <= page.Offset || next > AdvancedAnalysisSourceTitlePage.MaximumOffset
                    || next != page.Offset + page.Items.Count + page.OmittedOversizedLocators)
                || CurrentNativeSourceKey(sources, scope) is not { } sourceKey)
                continue;
            foreach (var item in page.Items)
            {
                if (item.PhysicalPageStart is not > 0) continue;
                var locator = new PendingTitleLocator(sourceKey, SnapshotCanonicalReference(scope), item.PhysicalPageStart.Value)
                { ObservedTitle = item };
                retained.RemoveAll(previous => previous.SourceKey == locator.SourceKey && previous.Page == locator.Page
                    && previous.ObservedTitle == locator.ObservedTitle && MatchesBoundReadSource(previous.Scope, locator.Scope));
                retained.Add(locator);
                observed++;
            }
        }
        if (observed == 0) return;
        // Bounded literal navigation metadata, including the observed title.
        // No body, evidence grant or historical provider item is retained.
        context.RetainedTitleLocators = retained.TakeLast(MaximumRetainedTitleLocators).ToArray();
        using var probe = BackendDiagnostics.Start("llm.retained_title_locators", _logger);
        probe.Set("job_id", request.JobId);
        probe.Set("observed_locator_count", observed);
        probe.Set("retained_locator_count", context.RetainedTitleLocators.Count);
        probe.Set("retained_source_count", context.RetainedTitleLocators.Select(locator => locator.SourceKey).Distinct(StringComparer.Ordinal).Count());
        probe.Set("maximum_retained_locators", MaximumRetainedTitleLocators);
        probe.Complete("stored", "successful_navigation_metadata_only");
    }

    private IReadOnlyList<PendingTitleLocator> FindPendingTitleLocatorsForReview(
        AdvancedAnalysisLoadDescriptor load, IReadOnlyList<PromptEvidenceItem> observations,
        SynthesisResearchContext context)
    {
        if (!_options.NativeSourceTitleNavigationEnabled) return [];
        var current = FindPendingTitleLocators(load, observations, context.NativeTurn);
        if (!load.StructuredLayout || load.AnswerUnitCount < 2
            || !string.Equals(load.AtomicEvidenceMode, "named_item", StringComparison.OrdinalIgnoreCase)
            || observations is not PromptEvidenceProjection { SourceCatalogueEnabled: true } projection)
            return current;
        return current.Concat(context.RetainedTitleLocators
                .Where(locator => RetainedTitleLocatorIsCurrent(locator, projection.SourceCatalogue)))
            .DistinctBy(locator => (locator.SourceKey, locator.Scope.DocId, locator.Scope.RevisionId,
                locator.Scope.SourceHash, locator.Scope.DocPath, locator.Page))
            .ToArray();
    }
}
