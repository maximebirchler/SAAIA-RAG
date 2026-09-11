namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static (
        SourceBackedAgentToolCall[] RouterActions,
        SourceBackedAgentToolCall? PlannedAction) PrepareInitialActions(
        SourceBackedIntake intake,
        SourceBackedInitialToolCall? semanticPlannerAction,
        string semanticPlan,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        bool suppressPlannedFirstAction)
    {
        var routerActions = (intake.InitialToolCalls
                ?? Array.Empty<SourceBackedInitialToolCall>())
            .Where(call => tools.Any(tool => string.Equals(
                tool.Name,
                call.ToolName,
                StringComparison.OrdinalIgnoreCase)))
            .Select(call => new SourceBackedAgentToolCall(
                call.Id,
                call.ToolName,
                call.Arguments))
            .ToArray();
        return (
            routerActions,
            routerActions.Length == 0
                && !suppressPlannedFirstAction
                ? semanticPlannerAction is not null
                  && tools.Any(tool => string.Equals(
                      tool.Name,
                      semanticPlannerAction.ToolName,
                      StringComparison.OrdinalIgnoreCase))
                    ? new SourceBackedAgentToolCall(
                        semanticPlannerAction.Id,
                        semanticPlannerAction.ToolName,
                        semanticPlannerAction.Arguments)
                    : ParsePlannedFirstAction(semanticPlan, tools)
                : null);
    }

    private void AddRunStartupTraces(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        SemanticPlanPreparation semanticPlanPreparation,
        SourceBackedAgentCompletion planningCompletion,
        string semanticPlan,
        int? requiredAtomicEvidenceCount,
        SemanticLayoutDimensions? semanticLayoutDimensions,
        IReadOnlyDictionary<string, string> semanticColumnRoles,
        SemanticColumnRoleOutcome? semanticColumnRoleOutcome,
        SemanticCandidateDefinitionOutcome semanticCandidateDefinitionOutcome,
        SemanticCandidateStrategyOutcome semanticCandidateStrategyOutcome,
        CandidateProjectedEvidenceContractOutcome candidateProjectedEvidenceContract,
        InitialResearchActionOutcome initialResearchActionOutcome,
        int maximumRunTurns,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        IReadOnlyList<SourceBackedAgentToolCall> routerInitialActions,
        SourceBackedAgentToolCall? plannedFirstAction)
    {
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Planner,
            "source_backed_agent_v2.semantic_plan.completed",
            ("prompt_tokens", planningCompletion.PromptTokens),
            ("completion_tokens", planningCompletion.CompletionTokens),
            ("server_cache_tokens", planningCompletion.ServerCacheTokens),
            ("server_prompt_evaluated_tokens",
                planningCompletion.ServerPromptTokensEvaluated),
            ("server_prompt_ms",
                planningCompletion.ServerPromptMilliseconds),
            ("server_predicted_tokens",
                planningCompletion.ServerPredictedTokens),
            ("server_predicted_ms",
                planningCompletion.ServerPredictedMilliseconds),
            ("finish_reason", planningCompletion.FinishReason),
            ("plan_characters", semanticPlan.Length),
            ("required_atomic_evidence_count", requiredAtomicEvidenceCount),
            ("required_layout_rows", semanticLayoutDimensions?.Rows),
            ("required_layout_columns", semanticLayoutDimensions?.Columns),
            ("semantic_column_roles", semanticColumnRoles.Count),
            ("structured_protocol_enabled",
                semanticPlanPreparation.StructuredProtocolEnabled),
            ("protocol_valid", semanticPlanPreparation.ProtocolValid),
            ("planning_attempts", semanticPlanPreparation.Attempts),
            ("planning_failure_reason", semanticPlanPreparation.FailureReason),
            ("initial_mission_failure_reason",
                semanticPlanPreparation.InitialMissionFailureReason),
            ("initial_action_decision_source",
                semanticPlanPreparation.InitialToolCall?.DecisionSource),
            ("initial_action_tool",
                semanticPlanPreparation.InitialToolCall?.ToolName),
            ("initial_action_arguments",
                semanticPlanPreparation.InitialToolCall is null
                    ? null
                    : TrimPromptValue(
                        semanticPlanPreparation.InitialToolCall.Arguments
                            .GetRawText(),
                        800)),
            ("planning_decision_source",
                semanticPlanPreparation.DecisionSource),
            ("role_boundaries", semanticColumnRoles
                .Select(static pair => pair.Key + "=" + pair.Value)
                .ToArray()),
            ("plan", semanticPlan)));
        AddSemanticColumnRoleTrace(
            traces,
            traceId,
            ref traceSequence,
            semanticColumnRoleOutcome);
        AddSemanticCandidateDefinitionTrace(
            traces,
            traceId,
            ref traceSequence,
            semanticCandidateDefinitionOutcome);
        AddSemanticCandidateStrategyTrace(
            traces,
            traceId,
            ref traceSequence,
            semanticCandidateStrategyOutcome);
        AddCandidateProjectedEvidenceContractTrace(
            traces,
            traceId,
            ref traceSequence,
            candidateProjectedEvidenceContract);
        AddInitialResearchActionTrace(
            traces,
            traceId,
            ref traceSequence,
            initialResearchActionOutcome);
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Planner,
            "source_backed_agent_v2.started",
            ("maximum_turns", _options.MaximumTurns),
            ("maximum_semantic_correction_turns",
                maximumRunTurns - _options.MaximumTurns),
            ("maximum_run_turns", maximumRunTurns),
            ("maximum_tool_calls", _options.MaximumToolCalls),
            ("maximum_context_tokens", _options.MaximumContextTokens),
            ("planned_tool_preference",
                tools.Select(static tool => tool.Name).ToArray()),
            ("router_initial_actions",
                routerInitialActions.Select(static call => call.Name).ToArray()),
            ("pre_observation_semantic_reviews_skipped",
                semanticLayoutDimensions is not null
                && routerInitialActions.Count > 0),
            ("planned_first_action", plannedFirstAction?.Name),
            ("available_tools",
                tools.Select(static tool => tool.Name).ToArray())));
    }

    private bool TryApplyInitialAction(
        ref SourceBackedAgentToolCall[] routerInitialActions,
        ref SourceBackedAgentToolCall? plannedFirstAction,
        string? initialActionDecisionSource,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        out SourceBackedAgentCompletion completion)
    {
        string eventName;
        (string Key, object? Value) actionField;
        if (routerInitialActions.Length > 0)
        {
            completion = new SourceBackedAgentCompletion(
                string.Empty,
                routerInitialActions,
                "tool_calls",
                0,
                0);
            routerInitialActions = Array.Empty<SourceBackedAgentToolCall>();
            eventName = initialActionDecisionSource?.StartsWith(
                            "semantic_planner",
                            StringComparison.OrdinalIgnoreCase) == true
                ? "source_backed_agent_v2.semantic_plan.first_action_applied"
                : "source_backed_agent_v2.router_plan.initial_actions_applied";
            actionField = (
                "tools",
                completion.ToolCalls.Select(static call => call.Name).ToArray());
        }
        else if (plannedFirstAction is not null)
        {
            completion = new SourceBackedAgentCompletion(
                string.Empty,
                new[] { plannedFirstAction },
                "tool_calls",
                0,
                0);
            plannedFirstAction = null;
            eventName =
                "source_backed_agent_v2.semantic_plan.first_action_applied";
            actionField = ("tool", completion.ToolCalls[0].Name);
        }
        else
        {
            completion = default!;
            return false;
        }

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            eventName,
            ("turn", turn),
            ("decision_source", initialActionDecisionSource
                                ?? (eventName.Contains(
                                    "router_plan",
                                    StringComparison.Ordinal)
                                    ? "llm_router"
                                    : "semantic_plan")),
            ("arguments", completion.ToolCalls
                .Select(static call => TrimPromptValue(
                    call.Arguments.GetRawText(),
                    800))
                .ToArray()),
            actionField));
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.turn.completed",
            ("turn", turn),
            ("finish_reason", completion.FinishReason),
            ("tool_calls", completion.ToolCalls.Count),
            ("content_characters", 0),
            ("content_evidence_ids", 0),
            ("prompt_tokens", 0),
            ("completion_tokens", 0)));
        return true;
    }
}
