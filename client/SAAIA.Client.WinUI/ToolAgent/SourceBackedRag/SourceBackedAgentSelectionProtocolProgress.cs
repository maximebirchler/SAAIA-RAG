namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private void ExtendSelectionProtocolRepairBudgetWhenPossible(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        bool selectionOnlyNextTurn,
        string selectionContractError,
        int maximumRunTurnCeiling,
        ref int maximumRunTurns,
        ref int selectionProtocolRepairTurns)
    {
        if (turn < maximumRunTurns
            || !selectionOnlyNextTurn
            || selectionProtocolRepairTurns
            >= _options.MaximumSelectionProtocolRepairTurns
            || maximumRunTurns >= maximumRunTurnCeiling)
        {
            return;
        }

        selectionProtocolRepairTurns++;
        maximumRunTurns = Math.Min(
            maximumRunTurnCeiling,
            maximumRunTurns + 1);
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.selection_protocol_repair.extended",
            ("turn", turn),
            ("repair_turn", selectionProtocolRepairTurns),
            ("maximum_run_turns", maximumRunTurns),
            ("selection_contract_error", selectionContractError)));
    }

    private bool StopRepeatedSelectionProtocolWhenNeeded(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        IReadOnlyList<SourceBackedAgentToolCall> selectionCalls,
        string selectionContractError,
        ref string? lastInvalidSelectionProtocolKey,
        ref int consecutiveIdenticalInvalidSelections,
        ref int maximumRunTurns)
    {
        var arguments = selectionCalls.Count == 1
            ? CanonicalizeArguments(selectionCalls[0].Arguments)
            : "selection_call_count=" + selectionCalls.Count;
        var currentKey = selectionContractError + "|" + arguments;
        if (string.Equals(
                currentKey,
                lastInvalidSelectionProtocolKey,
                StringComparison.Ordinal))
        {
            consecutiveIdenticalInvalidSelections++;
        }
        else
        {
            lastInvalidSelectionProtocolKey = currentKey;
            consecutiveIdenticalInvalidSelections = 1;
        }

        if (consecutiveIdenticalInvalidSelections < 2)
            return false;

        maximumRunTurns = Math.Min(maximumRunTurns, turn);
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.selection_protocol_repeat.stopped",
            ("turn", turn),
            ("identical_invalid_selections", consecutiveIdenticalInvalidSelections),
            ("selection_contract_error", selectionContractError)));
        return true;
    }
}
