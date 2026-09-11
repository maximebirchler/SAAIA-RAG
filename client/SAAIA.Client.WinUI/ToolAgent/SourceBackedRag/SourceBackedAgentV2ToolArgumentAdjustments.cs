using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private SourceBackedAgentToolCall
        ApplyMechanicalToolArgumentAdjustments(
            SourceBackedAgentToolCall call,
            string internalToolName,
            IReadOnlyList<string> candidateScopePaths,
            ICollection<SourceBackedTraceEvent> traces,
            string traceId,
            ref int traceSequence,
            int turn)
    {
        var normalizedCall = call;
        normalizedCall = ApplyMechanicalToolArgumentAdjustment(
            normalizedCall,
            internalToolName,
            NormalizeLlmChosenCandidateScopeArguments(
                internalToolName,
                normalizedCall.Arguments,
                candidateScopePaths,
                out var candidateScopeAdjustment),
            candidateScopeAdjustment,
            traces,
            traceId,
            ref traceSequence,
            turn);
        return normalizedCall;
    }

    private static JsonElement NormalizeLlmChosenCandidateScopeArguments(
        string internalToolName,
        JsonElement arguments,
        IReadOnlyList<string> candidateScopePaths,
        out string? adjustment)
    {
        adjustment = null;
        var exactScopes = candidateScopePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (exactScopes.Length != 1
            || internalToolName is not (
                "rag.search"
                or "rag.multi_search"
                or "documents.navigation"
                or "documents.content_cards")
            || arguments.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        var chosenScope = exactScopes[0];
        var currentScope = GetString(arguments, "categoryPath", "category");
        if (string.Equals(
                currentScope?.Trim(),
                chosenScope,
                StringComparison.OrdinalIgnoreCase))
        {
            return arguments;
        }

        var normalized = arguments.EnumerateObject()
            .Where(static property => !string.Equals(
                property.Name,
                "categoryPath",
                StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    property.Name,
                    "category",
                    StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.Ordinal);
        normalized["categoryPath"] = JsonSerializer.SerializeToElement(
            chosenScope);
        adjustment = string.IsNullOrWhiteSpace(currentScope)
            ? "llm_candidate_scope_applied"
            : "llm_candidate_scope_reapplied";
        return JsonSerializer.SerializeToElement(
            normalized,
            ClientJson.CamelCase);
    }

    private SourceBackedAgentToolCall
        ApplyMechanicalToolArgumentAdjustment(
            SourceBackedAgentToolCall call,
            string internalToolName,
            System.Text.Json.JsonElement adjustedArguments,
            string? adjustment,
            ICollection<SourceBackedTraceEvent> traces,
            string traceId,
            ref int traceSequence,
            int turn)
    {
        if (string.IsNullOrWhiteSpace(adjustment))
            return call;

        var originalArguments = call.Arguments;
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.RetrievalTools,
            "source_backed_agent_v2.tool.mechanical_adjustment",
            ("turn", turn),
            ("tool", internalToolName),
            ("adjustment", adjustment),
            ("original_arguments",
                TrimPromptValue(originalArguments.GetRawText(), 800)),
            ("normalized_arguments",
                TrimPromptValue(adjustedArguments.GetRawText(), 800))));
        return call with { Arguments = adjustedArguments };
    }
}
