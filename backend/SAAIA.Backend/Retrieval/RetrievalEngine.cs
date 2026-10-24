using Npgsql;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalEngine
{
    /// <summary>Coordinates named stages. Rules live in immutable plans and dedicated candidate/backfill components.</summary>
    private static async Task<RagSearchResponse> SearchInteractiveAsync(RetrievalExecutionContext context,
        NpgsqlDataSource ds, RagOptions rag, IHttpClientFactory httpFactory, RagSearchRequestDto req)
    {
        var resources = new InteractiveRetrievalResources(context, ds, rag, httpFactory);
        var timings = new InteractiveRetrievalTimings();
        var plan = await InteractiveRetrievalPlanner.CreateAsync(resources, req, timings).ConfigureAwait(false);
        var diagnostics = new RagRetrievalDiagnosticsBuilder(plan.Request.Query, plan.Query.RetrievalQuery,
            plan.Query.SelectionQuery, plan.Routes.Mode, plan.Scope.RequestedCategory ?? plan.Scope.CategoryQueryHint,
            plan.Scope.CategoryPath, plan.Selection.RequestedTopK, plan.Selection.Candidates,
            plan.Selection.MaxPerDoc, plan.Selection.MaxPerPage,
            includeContent: req.IncludeDiagnostics == true && req.ResearchMode != "advanced_analysis");
        using var searchScope = RetrievalTelemetry.StartSearchScope(plan.Routes.Mode,
            plan.Scope.HasCategoryFilter, plan.Scope.HasDocScope, plan.Selection.TopK, plan.Selection.Candidates, plan.Request.Query);
        var searchActivity = searchScope.Activity;
        var session = new InteractiveRetrievalSession(resources, plan, timings, new RetrievalPhaseDiagnostics(context.Logger),
            new RetrievalDegradationTracker(), diagnostics);
        var seeds = await InteractiveSeedLookup.SearchAsync(session).ConfigureAwait(false);
        var flow = new InteractiveRetrievalFlow(session, seeds, new InteractiveCandidateSelection(plan.Selection.TopK));
        await InteractiveInitialSelection.ExecuteAsync(flow).ConfigureAwait(false);
        await InteractiveStepDiagnostics.RunAsync(flow, "document_hint_backfill", () => InteractiveDocumentHintBackfill.ExecuteAsync(flow));
        InteractiveStepDiagnostics.Apply(flow, "initial_selection_cleanup", () => InteractiveInitialSelectionCleanup.Apply(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "direct_technical_phrase", () => InteractiveDirectTechnicalPhrase.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "direct_technical_linked_evidence", () => InteractiveDirectTechnicalLinkedEvidence.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "fuzzy_title_lead", () => InteractiveFuzzyTitleLead.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "enumerative_lookup", () => InteractiveEnumerativeLookup.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "scoped_profile_fallback", () => InteractiveScopedProfileFallback.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "empty_catalog_and_lexical_fallback", () => InteractiveEmptyCatalogAndLexicalFallback.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "early_date_title_backfill", () => InteractiveEarlyDateTitleBackfill.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "early_reference_version_backfill", () => InteractiveEarlyReferenceVersionBackfill.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "final_precise_title_backfill", () => InteractiveFinalPreciseTitleBackfill.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "final_document_hint_evidence", () => InteractiveFinalDocumentHintEvidence.ExecuteAsync(flow));
        InteractiveStepDiagnostics.Apply(flow, "focused_ordering", () => InteractiveFocusedOrdering.Apply(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "comparative_diversity_backfill", () => InteractiveComparativeDiversityBackfill.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "diversity_coverage_and_continuation", () => InteractiveDiversityCoverageAndContinuation.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "focused_empty_recovery", () => InteractiveFocusedEmptyRecovery.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "early_compact_code_recovery", () => InteractiveEarlyCompactCodeRecovery.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "file_and_companion_recovery", () => InteractiveFileAndCompanionRecovery.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "document_status_operands", () => InteractiveDocumentStatusOperands.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "late_standard_reference_backfill", () => InteractiveLateStandardReferenceBackfill.ExecuteAsync(flow));
        InteractiveStepDiagnostics.Apply(flow, "late_ordering", () => InteractiveLateOrdering.Apply(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "late_date_title_recovery", () => InteractiveLateDateTitleRecovery.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "late_reference_version_recovery", () => InteractiveLateReferenceVersionRecovery.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "empty_title_and_profile_recovery", () => InteractiveEmptyTitleAndProfileRecovery.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "final_standard_reference_profiles", () => InteractiveFinalStandardReferenceProfiles.ExecuteAsync(flow));
        InteractiveStepDiagnostics.Apply(flow, "ambiguous_source_suppression", () => InteractiveAmbiguousSourceSuppression.Apply(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "final_compact_code_recovery", () => InteractiveFinalCompactCodeRecovery.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "final_compact_code_profiles", () => InteractiveFinalCompactCodeProfiles.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "final_lexical_profiles", () => InteractiveFinalLexicalProfiles.ExecuteAsync(flow));
        InteractiveStepDiagnostics.Apply(flow, "requested_page_window", () => InteractiveRequestedPageWindow.Apply(flow));
        return InteractiveResponseProjection.Build(flow, searchActivity);
    }
}
