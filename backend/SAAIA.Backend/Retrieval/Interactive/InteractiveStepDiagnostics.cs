

namespace SAAIA.Backend.Retrieval;

/// <summary>Stage evaluation is distinct from a retriever invocation; an unchanged selection is reported honestly.</summary>
internal static class InteractiveStepDiagnostics
{
    internal static async Task RunAsync(InteractiveRetrievalFlow flow, string stage, Func<Task> action)
    {
        using var operation = BackendDiagnostics.Start("retrieval.interactive_stage", flow.Session.Resources.Context.Logger);
        var before = flow.Selection.Matches.Count;
        var previousSelection = flow.Selection.Matches.ToArray();
        flow.Session.Diagnostics.CapturePhase(stage + ".before", "stage_evaluation", null, flow.Selection.Matches);
        operation.Set("stage", stage);
        operation.Set("candidates_before", before);
        try
        {
            flow.Session.Resources.Context.CancellationToken.ThrowIfCancellationRequested();
            await action().ConfigureAwait(false);
            operation.Set("candidates_after", flow.Selection.Matches.Count);
            var changed = !previousSelection.SequenceEqual(flow.Selection.Matches);
            operation.Set("selection_changed", changed);
            operation.Complete("success", changed ? "selection_changed" : "evaluated_no_change");
        }
        catch (Exception exception) { operation.Fail(exception); throw; }
        finally
        {
            operation.Set("candidates_after", flow.Selection.Matches.Count);
            flow.Session.Diagnostics.CapturePhase(stage + ".after", "stage_evaluation", (long)operation.ElapsedMilliseconds, flow.Selection.Matches);
        }
    }

    internal static void Apply(InteractiveRetrievalFlow flow, string stage, Action action)
    {
        using var operation = BackendDiagnostics.Start("retrieval.interactive_stage", flow.Session.Resources.Context.Logger);
        var before = flow.Selection.Matches.Count;
        var previousSelection = flow.Selection.Matches.ToArray();
        flow.Session.Diagnostics.CapturePhase(stage + ".before", "stage_evaluation", null, flow.Selection.Matches);
        operation.Set("stage", stage);
        operation.Set("candidates_before", before);
        try
        {
            flow.Session.Resources.Context.CancellationToken.ThrowIfCancellationRequested();
            action();
            operation.Set("candidates_after", flow.Selection.Matches.Count);
            var changed = !previousSelection.SequenceEqual(flow.Selection.Matches);
            operation.Set("selection_changed", changed);
            operation.Complete("success", changed ? "selection_changed" : "evaluated_no_change");
        }
        catch (Exception exception) { operation.Fail(exception); throw; }
        finally
        {
            operation.Set("candidates_after", flow.Selection.Matches.Count);
            flow.Session.Diagnostics.CapturePhase(stage + ".after", "stage_evaluation", (long)operation.ElapsedMilliseconds, flow.Selection.Matches);
        }
    }
}
