using SAAIA.Client.Core.Execution;
using Xunit;

namespace SAAIA.Client.Core.Tests;

public sealed class AgentIterationPlanningTests
{
    [Fact]
    public void Structured_run_keeps_research_open_for_a_model_reported_gap_until_its_actual_extended_ceiling()
    {
        var input = Input() with { Turn = 7, MaximumTurns = 6, MaximumRunTurns = 10, SemanticNeedsMoreEvidence = true };
        Assert.False(AgentIterationPlanner.Plan(input).FinalizationTurn);
        Assert.True(AgentIterationPlanner.Plan(input with { Turn = 10 }).FinalizationTurn);
    }

    [Fact]
    public void Rendering_reservation_uses_the_proven_evidence_count_and_never_selects_during_collection()
    {
        var ready = Input() with { Turn = 4, MaximumTurns = 6, ObservedCitableSources = 4, CandidateCollectionOpen = false };
        var plan = AgentIterationPlanner.Plan(ready);
        Assert.True(plan.RenderingReservationTurn);
        Assert.True(plan.SemanticSelectionDecisionTurn);
        Assert.Equal(AgentIterationIntent.SelectEvidence, plan.Intent);
        Assert.False(AgentIterationPlanner.Plan(ready with { CandidateCollectionOpen = true }).SemanticSelectionDecisionTurn);
        Assert.False(AgentIterationPlanner.Plan(ready with { ObservedCitableSources = 3 }).RenderingReservationTurn);
    }

    [Fact]
    public void Iteration_intent_distinguishes_real_writer_and_candidate_audit_paths()
    {
        Assert.Equal(AgentIterationIntent.WriteAnswer, AgentIterationPlanner.Plan(Input() with { DirectWriterRequested = true }).Intent);
        Assert.Equal(AgentIterationIntent.ReviewCandidates, AgentIterationPlanner.Plan(Input() with { DedicatedCandidateAudit = true }).Intent);
        var lifecycle = new AgentRunLifecycle();
        lifecycle.Initialize(); lifecycle.BeginIteration(1);
        lifecycle.PlanIntent(AgentIterationIntent.ReviewCandidates);
        Assert.Equal(AgentIterationIntent.ReviewCandidates, lifecycle.Intent);
    }

    [Fact]
    public void Adaptive_flat_checkpoint_requires_observed_sources_and_no_imposed_table_layout()
    {
        var input = Input() with { CandidateCollectionOpen = true, CandidateAuditEnabled = true, ObservedCitableSources = 2 };
        Assert.True(AgentIterationPlanner.Plan(input).AllowAdaptiveFlatSelection);
        Assert.False(AgentIterationPlanner.Plan(input with { HasSemanticLayout = true }).AllowAdaptiveFlatSelection);
        Assert.False(AgentIterationPlanner.Plan(input with { ObservedCitableSources = 0 }).AllowAdaptiveFlatSelection);
    }

    [Fact]
    public void Interactive_tool_menu_hides_selection_without_coverage_and_workspace_without_a_citable_source()
    {
        var tools = new[] { "search", "read", "selection", "workspace" };
        var input = MenuInput() with { HasCitableEvidence = false, RequiredEvidenceCount = 4, ObservedCitableSources = 2 };
        var result = AgentToolMenuPlanner.Select(input, tools, static name => name,
            _ => throw new InvalidOperationException("interactive menu must not invoke a specialized factory"),
            () => "flat", () => "clarify");
        Assert.Equal(new[] { "search", "read" }, result.Tools);
    }

    [Fact]
    public void Finalization_exposes_no_retrieval_tools_and_explicit_workspace_selection_uses_its_own_menu()
    {
        var input = MenuInput() with { Finalization = true };
        var terminal = AgentToolMenuPlanner.Select(input, new[] { "search" }, static name => name,
            _ => throw new InvalidOperationException(), () => "flat", () => "clarify");
        Assert.Empty(terminal.Tools);
        var selection = AgentToolMenuPlanner.Select(input with { SemanticSelectionDecision = true, WorkspaceWriterDecision = true },
            new[] { "search" }, static name => name, mode => new[] { mode.ToString() }, () => "flat", () => "clarify");
        Assert.Equal(AgentToolMenu.WorkspaceSelection, selection.Menu);
        Assert.Equal(new[] { "WorkspaceSelection" }, selection.Tools);
    }

    [Fact]
    public void Adaptive_collection_menu_preserves_provisional_selection_and_clarification_capability()
    {
        var input = MenuInput() with { CandidateCollectionOpen = true, AllowAdaptiveFlatSelection = true };
        var result = AgentToolMenuPlanner.Select(input, new[] { "ignored" }, static name => name,
            mode => new[] { mode.ToString() }, () => "provisional_selection", () => "clarification");
        Assert.Equal(new[] { "CandidateCollection", "provisional_selection", "clarification" }, result.Tools);
    }

    [Fact]
    public async Task Invalid_checkpoint_cannot_replace_the_last_valid_gap_or_selection_and_identical_sets_are_not_rejudged()
    {
        var checkpoint = new EvidenceAdequacyCheckpoint<string>();
        var first = await checkpoint.EvaluateAsync(new[] { "E1" }, (_, _) => Task.FromResult(
            new EvidenceAdequacyReview<string>(true, "continue", "missing_field", null, new[] { "E1" })), CancellationToken.None);
        Assert.True(first.RequiresFeedback);
        Assert.True(checkpoint.GapActive);
        var invalid = await checkpoint.EvaluateAsync(new[] { "E1", "E2" }, (_, _) => Task.FromResult(
            new EvidenceAdequacyReview<string>(false, "select", "truncated", null, new[] { "E2" })), CancellationToken.None);
        Assert.False(invalid.RequiresFeedback);
        Assert.True(checkpoint.GapActive);
        Assert.Equal(new[] { "E1" }, checkpoint.IncumbentIds);
        var same = await checkpoint.EvaluateAsync(new[] { "E2", "E1" }, (_, _) => throw new InvalidOperationException("same candidate set"), CancellationToken.None);
        Assert.False(same.Evaluated);
        Assert.Empty(new EvidenceAdequacyCheckpoint<string>().IncumbentIds);
    }

    private static AgentIterationInput Input() => new(1, 6, 10, true, 4, true, 2,
        true, false, false, false, true, true, false, false, true, false, false);
    private static AgentToolMenuInput MenuInput() => new(false, false, false, false, false, false,
        false, false, false, true, 4, 4, "selection", "workspace");
}
