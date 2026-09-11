namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private SourceBackedIntake ApplyNamedDocumentResolution(
        SourceBackedIntake intake,
        SourceBackedDocumentResolutionObservation? resolution,
        List<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence)
    {
        if (resolution is null)
            return intake;

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Planner,
            "source_backed_named_document_resolution.completed",
            ("requested_reference", resolution.RequestedReference),
            ("requested_reference_length", resolution.RequestedReference.Length),
            ("status", resolution.Status),
            ("complete", resolution.CatalogObservationComplete),
            ("candidate_count", resolution.Candidates.Count),
            ("exact_match_count", resolution.ExactMatchCount),
            ("reason", resolution.ReasonCode),
            ("pages", resolution.PagesObserved),
            ("items", resolution.ItemsObserved),
            ("elapsed_ms", resolution.ElapsedMilliseconds)));
        return intake with { RequestedDocumentResolution = resolution };
    }

    private async Task<SourceBackedDocumentResolutionObservation?>
        ResolveRequestedDocumentAsync(
            SourceBackedIntake intake,
            CancellationToken ct)
    {
        var requested = intake.RequestedDocumentName?.Trim();
        if (_namedDocumentResolver is null || string.IsNullOrWhiteSpace(requested))
            return null;

        try
        {
            var observation = await _namedDocumentResolver.ResolveAsync(requested, ct)
                .ConfigureAwait(false);
            return ValidateNamedDocumentResolutionContract(requested, observation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SourceBackedDocumentResolutionObservation(
                requested,
                SourceBackedDocumentResolutionStatus.Inconclusive,
                CatalogObservationComplete: false,
                Array.Empty<SourceBackedDocumentResolutionCandidate>(),
                "resolver_port_failed",
                PagesObserved: 0,
                ItemsObserved: 0,
                ReachedSafetyLimit: false,
                ElapsedMilliseconds: 0,
                TechnicalError: ex.GetType().Name);
        }
    }

    private static SourceBackedDocumentResolutionObservation
        ValidateNamedDocumentResolutionContract(
            string requested,
            SourceBackedDocumentResolutionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var valid = observation.Status switch
        {
            SourceBackedDocumentResolutionStatus.Resolved
                => observation.CatalogObservationComplete
                   && observation.Candidates.Count == 1,
            SourceBackedDocumentResolutionStatus.NotFound
                => observation.CatalogObservationComplete
                   && observation.Candidates.Count == 0,
            SourceBackedDocumentResolutionStatus.Ambiguous
                => observation.CatalogObservationComplete
                   && observation.Candidates.Count > 1,
            SourceBackedDocumentResolutionStatus.Inconclusive
                => !observation.CatalogObservationComplete,
            _ => false
        };
        if (valid)
            return observation with { RequestedReference = requested };

        return observation with
        {
            RequestedReference = requested,
            Status = SourceBackedDocumentResolutionStatus.Inconclusive,
            CatalogObservationComplete = false,
            ReasonCode = "invalid_resolver_contract"
        };
    }
}
