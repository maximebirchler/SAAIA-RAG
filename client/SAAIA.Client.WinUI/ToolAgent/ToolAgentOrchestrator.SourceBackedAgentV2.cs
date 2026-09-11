using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private bool IsSourceBackedAgentV2Available()
        => _llm is ISourceBackedAgentLlmClient;

    private static bool ShouldPreserveCanonicalSourceBackedRouterDecision(
        RouterPlan plan,
        bool sourceBackedAgentAvailable)
        => sourceBackedAgentAvailable
           && plan.Origin == RouterPlanOrigin.Llm
           && plan.SourceBackedMission is not null
           && (plan.ToolCalls.Count == 0
               || HasCanonicalSourceBackedToolCall(plan));

    private static bool HasCanonicalSourceBackedToolCall(RouterPlan plan)
        => plan.ToolCalls.Count > 0
           && plan.ToolCalls.All(static call =>
               !ToolManifest.IsAdminTool(NormalizeToolName(call.Name)))
           && plan.ToolCalls.Any(static call =>
               SourceBackedAgentToolCatalog.TryResolveExternalName(
                   NormalizeToolName(call.Name),
                   out _));

    internal static bool ShouldPreserveCanonicalSourceBackedRouterDecisionForTests(
        RouterPlan plan,
        bool sourceBackedAgentAvailable)
        => ShouldPreserveCanonicalSourceBackedRouterDecision(
            plan,
            sourceBackedAgentAvailable);

    private async Task<SourceBackedPipelineResult> RunSourceBackedAgentV2Async(
        SourceBackedIntake intake,
        string pipelineIntent,
        string entryPoint,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onProgress)
        => await (await CreateSourceBackedAgentV2RunnerAsync(
                pipelineIntent, entryPoint, ct, onPhase, onProgress)
            .ConfigureAwait(false)).RunAsync(intake, ct).ConfigureAwait(false);

    private async Task<SourceBackedAgentV2Runner> CreateSourceBackedAgentV2RunnerAsync(
        string pipelineIntent, string entryPoint, CancellationToken ct,
        Action<string>? onPhase, Action<string>? onProgress)
    {
        var options = SourceBackedAgentV2Options.ResolveFromEnvironment();
        if (_llm is not ISourceBackedAgentLlmClient nativeToolLlm)
        {
            EmitRagTrace(
                "source_backed_agent_v2.unavailable",
                ("reason", "llm_client_has_no_native_tool_support"));
            throw new InvalidOperationException(
                "The canonical source-backed agent requires native LLM tool support.");
        }

        var configuredContextTokens = ResolveActiveLlmContextTokens(_settings);
        var runtimeContextTokens = nativeToolLlm
            is ISourceBackedAgentRuntimeContextProvider runtimeContextProvider
            ? await runtimeContextProvider.GetRuntimeContextTokensAsync(ct)
                .ConfigureAwait(false)
            : null;
        options = options with
        {
            MaximumContextTokens =
                runtimeContextTokens ?? configuredContextTokens
        };
        EmitRagTrace(
            "source_backed_agent_v2.active",
            ("intent", pipelineIntent),
            ("entry", entryPoint),
            ("max_turns", options.MaximumTurns),
            ("max_tool_calls", options.MaximumToolCalls),
            ("candidate_definition_enabled",
                options.SemanticCandidateDefinitionEnabled),
            ("candidate_strategy_enabled",
                options.SemanticCandidateStrategyEnabled),
            ("candidate_audit_batch_size",
                options.MaximumSemanticCandidatesPerAuditBatch),
            ("maximum_cumulative_llm_tokens",
                options.MaximumCumulativeLlmTokens),
            ("maximum_cumulative_llm_elapsed_ms",
                options.MaximumCumulativeLlmElapsedMilliseconds),
            ("cumulative_llm_terminal_reserve_tokens",
                options.CumulativeLlmTerminalReserveTokens),
            ("cumulative_llm_terminal_reserve_ms",
                options.CumulativeLlmTerminalReserveMilliseconds),
            ("context_tokens", options.MaximumContextTokens),
            ("configured_context_tokens", configuredContextTokens),
            ("runtime_context_tokens", runtimeContextTokens),
            ("context_source", runtimeContextTokens is > 0
                ? "runtime_models"
                : "application_settings"));
        return new SourceBackedAgentV2Runner(
                nativeToolLlm,
                new OrchestratorSourceBackedRagToolExecutor(
                    this,
                    onPhase,
                    onProgress,
                    options.MaximumWorkingEvidenceItems),
                options,
                traceSink: trace => EmitRagTrace(
                    "source_backed_agent_v2.step",
                    ("source_trace_id", trace.TraceId),
                    ("source_seq", trace.Sequence),
                    ("source_step", trace.Step.ToString()),
                    ("source_event", trace.EventName),
                    ("source_fields", FormatSourceBackedPipelineTraceFields(trace.Fields))),
                namedDocumentResolver: new SourceBackedNamedDocumentResolver(
                    new ApiClientSourceBackedNamedDocumentCatalogClient(_api),
                    new ApiClientSourceBackedNamedDocumentIdentityHydrator(_api)));
    }
}
