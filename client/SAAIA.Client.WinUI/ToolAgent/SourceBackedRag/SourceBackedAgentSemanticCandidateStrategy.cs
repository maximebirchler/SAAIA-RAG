using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SemanticCandidateStrategyToolName =
        "submit_candidate_evidence_strategy";
    private const string SharedCandidatePoolRelation = "shared_pool";
    private const string PartitionedCandidatePoolRelation = "partitioned_pool";

    private sealed record SemanticCandidateStrategyOutcome(
        string CandidateObjectType,
        string RouterCandidateObjectType,
        string CandidateEligibilityRule,
        IReadOnlyList<string> CandidateScopePaths,
        string CandidatePoolRelation,
        bool Attempted,
        bool ProtocolValid,
        int Attempts,
        int PromptTokens,
        int CompletionTokens,
        string FailureReason,
        string LastFinishReason,
        string LastToolArguments,
        string SourceDiscoveryQuery = "",
        string NavigationKind = "",
        SourceBackedInitialToolCall? InitialAction = null);

    private async Task<SemanticCandidateStrategyOutcome>
        ReviewSemanticCandidateStrategyAsync(
            SourceBackedIntake intake,
            string semanticPlan,
            string rowHeader,
            IReadOnlyList<string> rowLabels,
            IReadOnlyList<string> columnLabels,
            IReadOnlyList<string> categoryPaths,
            int requiredAtomicEvidenceCount,
            CancellationToken ct)
    {
        SourceBackedAgentCompletion? previousCompletion = null;
        var failureReason = string.Empty;
        var promptTokens = 0;
        var completionTokens = 0;
        var routerCandidateObjectType = ReadSemanticCandidateObjectType(
            semanticPlan);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var completion = await _llm.CompleteAsync(
                    BuildSemanticCandidateStrategyMessages(
                        intake,
                        semanticPlan,
                        rowHeader,
                        rowLabels,
                        columnLabels,
                        categoryPaths,
                        requiredAtomicEvidenceCount,
                        attempt,
                        failureReason,
                        previousCompletion),
                    new[]
                    {
                        BuildSemanticCandidateStrategyTool(
                            categoryPaths.Count,
                            requiredAtomicEvidenceCount,
                            _options.MaximumWorkingEvidenceItems)
                    },
                    Math.Clamp(
                        _options.MaximumSemanticColumnRoleTokens * 2,
                        192,
                        384),
                    ct,
                    temperatureOverride: 0,
                    requireToolCall: true)
                .ConfigureAwait(false);
            promptTokens += completion.PromptTokens.GetValueOrDefault();
            completionTokens += completion.CompletionTokens.GetValueOrDefault();

            if (TryReadSemanticCandidateStrategy(
                    completion,
                    categoryPaths,
                    new[] { rowHeader }
                        .Concat(rowLabels)
                        .Concat(columnLabels)
                        .ToArray(),
                    requiredAtomicEvidenceCount,
                    _options.MaximumWorkingEvidenceItems,
                    out var candidateObjectType,
                    out var candidateEligibilityRule,
                    out var sourceDiscoveryQuery,
                    out var candidateScopePaths,
                    out var candidatePoolRelation,
                    out var initialCapability,
                    out var navigationKind,
                    out var initialLimit,
                    out failureReason))
            {
                var initialAction = string.Equals(
                        candidatePoolRelation,
                        SharedCandidatePoolRelation,
                        StringComparison.Ordinal)
                    ? BuildCandidateDiscoveryInitialAction(
                        initialCapability,
                        sourceDiscoveryQuery,
                        candidateScopePaths,
                        initialLimit,
                        navigationKind)
                    : null;
                return new SemanticCandidateStrategyOutcome(
                    candidateObjectType,
                    routerCandidateObjectType,
                    candidateEligibilityRule,
                    candidateScopePaths,
                    candidatePoolRelation,
                    Attempted: true,
                    ProtocolValid: true,
                    Attempts: attempt,
                    PromptTokens: promptTokens,
                    CompletionTokens: completionTokens,
                    FailureReason: string.Empty,
                    LastFinishReason: completion.FinishReason,
                    LastToolArguments: TrimPromptValue(
                        completion.ToolCalls[0].Arguments.GetRawText(),
                        800),
                    SourceDiscoveryQuery: sourceDiscoveryQuery,
                    NavigationKind: navigationKind,
                    InitialAction: initialAction);
            }

            previousCompletion = completion;
        }

        return new SemanticCandidateStrategyOutcome(
            routerCandidateObjectType,
            routerCandidateObjectType,
            string.Empty,
            Array.Empty<string>(),
            string.Empty,
            Attempted: true,
            ProtocolValid: false,
            Attempts: 2,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            FailureReason: failureReason,
            LastFinishReason: previousCompletion?.FinishReason ?? string.Empty,
            LastToolArguments: previousCompletion?.ToolCalls.Count == 1
                ? TrimPromptValue(
                    previousCompletion.ToolCalls[0].Arguments.GetRawText(),
                    800)
                : TrimPromptValue(previousCompletion?.Content, 1200));
    }

    private static SemanticCandidateStrategyOutcome
        CreateUnusedSemanticCandidateStrategyOutcome()
        => new(
            string.Empty,
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            string.Empty,
            Attempted: false,
            ProtocolValid: true,
            Attempts: 0,
            PromptTokens: 0,
            CompletionTokens: 0,
            FailureReason: string.Empty,
            LastFinishReason: string.Empty,
            LastToolArguments: string.Empty);

    private void AddSemanticCandidateStrategyTrace(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        SemanticCandidateStrategyOutcome outcome)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Planner,
            "source_backed_agent_v2.semantic_candidate_strategy.completed",
            ("attempted", outcome.Attempted),
            ("protocol_valid", outcome.ProtocolValid),
            ("candidate_pool_relation", outcome.CandidatePoolRelation),
            ("router_candidate_object_type", outcome.RouterCandidateObjectType),
            ("candidate_object_type", outcome.CandidateObjectType),
            ("candidate_eligibility_rule", outcome.CandidateEligibilityRule),
            ("source_discovery_query", outcome.SourceDiscoveryQuery),
            ("navigation_kind", outcome.NavigationKind),
            ("initial_action_tool", outcome.InitialAction?.ToolName),
            ("initial_action_arguments", outcome.InitialAction is null
                ? null
                : TrimPromptValue(
                    outcome.InitialAction.Arguments.GetRawText(),
                    800)),
            ("candidate_scope_paths", outcome.CandidateScopePaths),
            ("attempts", outcome.Attempts),
            ("prompt_tokens", outcome.PromptTokens),
            ("completion_tokens", outcome.CompletionTokens),
            ("failure_reason", outcome.FailureReason),
            ("last_finish_reason", outcome.LastFinishReason),
            ("last_tool_arguments", outcome.LastToolArguments)));

    private static bool TryReadSemanticCandidateStrategy(
        SourceBackedAgentCompletion completion,
        IReadOnlyList<string> categoryPaths,
        IReadOnlyList<string> layoutCoordinateLabels,
        int requiredAtomicEvidenceCount,
        int maximumWorkingEvidenceItems,
        out string candidateObjectType,
        out string candidateEligibilityRule,
        out string sourceDiscoveryQuery,
        out IReadOnlyList<string> candidateScopePaths,
        out string candidatePoolRelation,
        out string initialCapability,
        out string navigationKind,
        out int initialLimit,
        out string contractError)
    {
        candidateObjectType = string.Empty;
        candidateEligibilityRule = string.Empty;
        sourceDiscoveryQuery = string.Empty;
        candidateScopePaths = Array.Empty<string>();
        candidatePoolRelation = string.Empty;
        initialCapability = string.Empty;
        navigationKind = string.Empty;
        initialLimit = 0;
        contractError = string.Empty;
        if (completion.ToolCalls.Count != 1
            || !string.Equals(
                completion.ToolCalls[0].Name,
                SemanticCandidateStrategyToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            contractError = "candidate_strategy_single_tool_call_required";
            return false;
        }

        var arguments = completion.ToolCalls[0].Arguments;
        candidateObjectType = NormalizeSemanticPlanLine(
            GetString(arguments, "candidateObjectType"));
        if (candidateObjectType.Length is < 2 or > 80)
        {
            contractError = "candidate_strategy_object_type_invalid";
            return false;
        }
        candidateEligibilityRule = NormalizeSemanticPlanLine(
            GetString(arguments, "candidateEligibilityRule"));
        if (candidateEligibilityRule.Length is < 8 or > 240)
        {
            contractError = "candidate_strategy_eligibility_rule_invalid";
            return false;
        }
        sourceDiscoveryQuery = NormalizeSemanticPlanLine(
            GetString(arguments, "sourceDiscoveryQuery"));
        if (sourceDiscoveryQuery.Length > 120)
        {
            contractError = "candidate_strategy_source_discovery_query_invalid";
            return false;
        }
        candidatePoolRelation = NormalizeSemanticPlanLine(
            GetString(arguments, "candidatePoolRelation"));
        if (!IsValidCandidatePoolRelation(candidatePoolRelation))
        {
            contractError = "candidate_strategy_pool_relation_invalid";
            return false;
        }
        var submittedInitialCapability = NormalizeSemanticPlanLine(
            GetString(arguments, "initialCapability"));
        if (submittedInitialCapability is not (
                "documents_content_cards"
                or "documents_navigation_entries"
                or "documents_navigation_titles"
                or "documents_navigation_all"
                or "rag_search"))
        {
            contractError = "candidate_strategy_initial_capability_invalid";
            return false;
        }
        (initialCapability, navigationKind) = submittedInitialCapability switch
        {
            "documents_navigation_entries" =>
                ("documents_navigation", "navigation_entry"),
            "documents_navigation_titles" =>
                ("documents_navigation", "title_anchor"),
            "documents_navigation_all" =>
                ("documents_navigation", string.Empty),
            _ => (submittedInitialCapability, string.Empty)
        };
        if (string.Equals(
                initialCapability,
                "rag_search",
                StringComparison.Ordinal)
            && sourceDiscoveryQuery.Length < 2)
        {
            contractError = "candidate_strategy_source_discovery_query_required";
            return false;
        }
        if (!TryGetInteger(arguments, "initialLimit", out initialLimit)
            || initialLimit < Math.Max(1, requiredAtomicEvidenceCount)
            || initialLimit > maximumWorkingEvidenceItems)
        {
            contractError = "candidate_strategy_initial_limit_invalid";
            return false;
        }

        if (!TryGetPropertyIgnoreCase(
                arguments,
                "candidateScopeIds",
                out var scopeIdsElement)
            || scopeIdsElement.ValueKind != JsonValueKind.Array)
        {
            contractError = "candidate_strategy_scopes_invalid";
            return false;
        }

        var scopeIds = new List<int>();
        foreach (var scopeIdElement in scopeIdsElement.EnumerateArray())
        {
            if (scopeIdElement.ValueKind != JsonValueKind.Number
                || !scopeIdElement.TryGetInt32(out var scopeId)
                || scopeId < 0
                || scopeId > categoryPaths.Count
                || scopeIds.Contains(scopeId))
            {
                contractError = "candidate_strategy_scopes_invalid";
                return false;
            }
            scopeIds.Add(scopeId);
        }

        var maximumScopeCount = string.Equals(
            candidatePoolRelation,
            SharedCandidatePoolRelation,
            StringComparison.Ordinal)
            ? 1
            : Math.Max(1, Math.Min(4, categoryPaths.Count + 1));
        if (scopeIds.Count is < 1 || scopeIds.Count > maximumScopeCount)
        {
            contractError = "candidate_strategy_scopes_invalid";
            return false;
        }

        candidateScopePaths = scopeIds
            .Select(scopeId => scopeId == 0
                ? string.Empty
                : categoryPaths[scopeId - 1])
            .ToArray();
        return true;
    }

    private static string ReadSemanticCandidateObjectType(string semanticPlan)
    {
        var line = semanticPlan.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static candidate => candidate.StartsWith(
                "PREUVES_ATOMIQUES:",
                StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(line))
            return "instance source nommee";

        var value = line[(line.IndexOf(':') + 1)..].Trim();
        var offset = 0;
        while (offset < value.Length && char.IsDigit(value[offset]))
            offset++;
        while (offset < value.Length
               && (char.IsWhiteSpace(value[offset])
                   || value[offset] is ':' or '-' or 'x' or 'X'))
        {
            offset++;
        }

        var objectType = NormalizeSemanticPlanLine(value[offset..]);
        return objectType.Length is >= 2 and <= 180
            ? objectType
            : "instance source nommee";
    }

    private static SourceBackedInitialToolCall BuildCandidateDiscoveryInitialAction(
        string initialCapability,
        string sourceDiscoveryQuery,
        IReadOnlyList<string> candidateScopePaths,
        int initialLimit,
        string navigationKind)
    {
        var categoryPath = candidateScopePaths.Count == 1
            ? candidateScopePaths[0]
            : string.Empty;
        var arguments = initialCapability switch
        {
            "rag_search" => JsonSerializer.SerializeToElement(new
            {
                query = sourceDiscoveryQuery,
                categoryPath = categoryPath.Length > 0 ? categoryPath : null,
                topK = initialLimit
            }, ClientJson.CamelCase),
            "documents_navigation" => JsonSerializer.SerializeToElement(new
            {
                categoryPath = categoryPath.Length > 0 ? categoryPath : null,
                q = sourceDiscoveryQuery.Length > 0
                    ? sourceDiscoveryQuery
                    : null,
                kind = navigationKind.Length > 0
                    ? navigationKind
                    : null,
                limit = initialLimit,
                offset = 0
            }, ClientJson.CamelCase),
            _ => JsonSerializer.SerializeToElement(new
            {
                categoryPath = categoryPath.Length > 0 ? categoryPath : null,
                q = sourceDiscoveryQuery.Length > 0
                    ? sourceDiscoveryQuery
                    : null,
                limit = initialLimit,
                offset = 0
            }, ClientJson.CamelCase)
        };
        return new SourceBackedInitialToolCall(
            "candidate-discovery-1",
            initialCapability,
            arguments,
            "llm_candidate_discovery");
    }

    private static bool IsValidCandidatePoolRelation(string value)
        => value is SharedCandidatePoolRelation or PartitionedCandidatePoolRelation;

}
