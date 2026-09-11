namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static SemanticCandidateStrategyOutcome
        PreserveRouterChosenCandidateScope(
            SemanticCandidateStrategyOutcome outcome,
            SourceBackedInitialSemanticMission? mission,
            IReadOnlyList<SourceBackedCatalogHint>? catalogHints)
    {
        if (outcome.CandidateScopePaths.Any(static path =>
                !string.IsNullOrWhiteSpace(path))
            || mission is null
            || !string.Equals(
                mission.DecisionSource,
                "llm_router",
                StringComparison.OrdinalIgnoreCase)
            || mission.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object
            || catalogHints is not { Count: > 0 }
            || !TryGetPropertyIgnoreCase(
                mission.Arguments,
                "candidateScopePaths",
                out var submittedScopes)
            || submittedScopes.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return outcome;
        }

        var availableScopes = catalogHints
            .Select(static hint => hint.CategoryPath?.Trim())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var scopes = submittedScopes
            .EnumerateArray()
            .Where(static item =>
                item.ValueKind == System.Text.Json.JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => availableScopes.FirstOrDefault(available =>
                string.Equals(
                    available,
                    path,
                    StringComparison.OrdinalIgnoreCase)))
            .Where(static path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return scopes.Length > 0
            ? outcome with { CandidateScopePaths = scopes }
            : outcome;
    }

    private static SemanticCandidateStrategyOutcome PreserveLlmChosenCandidateScope(
        SemanticCandidateStrategyOutcome outcome,
        IReadOnlyList<SourceBackedInitialToolCall>? initialActions)
    {
        if (outcome.CandidateScopePaths.Any(static path =>
                !string.IsNullOrWhiteSpace(path))
            || initialActions is not { Count: > 0 })
        {
            return outcome;
        }

        var documentaryActions = initialActions
            .Where(static action => action.ToolName is
                "rag_search"
                or "documents_navigation"
                or "documents_content_cards")
            .ToArray();
        if (documentaryActions.Length == 0)
            return outcome;

        var scopes = documentaryActions
            .Select(static action =>
                GetString(action.Arguments, "categoryPath", "path")?.Trim())
            .Where(static scope => !string.IsNullOrWhiteSpace(scope))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allActionsCarryThatScope = scopes.Length == 1
            && documentaryActions.All(action => string.Equals(
                GetString(action.Arguments, "categoryPath", "path")?.Trim(),
                scopes[0],
                StringComparison.OrdinalIgnoreCase));
        return allActionsCarryThatScope
            ? outcome with { CandidateScopePaths = scopes }
            : outcome;
    }
}
