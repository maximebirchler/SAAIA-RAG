namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildAvailableEvidenceTools(
            SourceBackedIntake intake,
            string semanticPlan,
            bool compactSingleSelection,
            bool requireEvidenceSelection,
            bool enableWorkspaceWriterHandoff,
            int? requiredEvidenceCount,
            SemanticLayoutDimensions? dimensions,
            IReadOnlyList<string> columnLabels,
            string rowHeader,
            IReadOnlyList<string> rowLabels)
    {
        // The system prompt already describes capability semantics. Lean
        // schemas leave the small model's context for evidence observations.
        var availableTools = SourceBackedAgentToolCatalog.Build(
                useConstrainedContextDescriptions: true,
                allowedCategoryPaths:
                SourceBackedRetrievalScope.GetAvailableCategoryPaths(intake),
                includeCategoryPathEnums: false)
            .ToList();
        if (!compactSingleSelection)
        {
            availableTools.Add(BuildEvidenceWorkspaceTool(
                enableWriterHandoff: false,
                requiredEvidenceCount,
                orderedByCanonicalLayout: dimensions is not null,
                allowedEvidenceIds: null));
        }

        IReadOnlyList<SourceBackedAgentToolDefinition> tools =
            PrioritizeToolsFromSemanticPlan(
                availableTools,
                semanticPlan);
        if (_options.SeparateActionAndWriter
            && requiredEvidenceCount is > 0
            && (requiredEvidenceCount > 1 || requireEvidenceSelection)
            && !enableWorkspaceWriterHandoff)
        {
            tools = tools
                .Append(BuildSemanticSelectionTool(
                    requiredEvidenceCount.Value,
                    dimensions,
                    allowedEvidenceIds: null,
                    canonicalColumnLabels: columnLabels,
                    canonicalRowHeader: rowHeader,
                    canonicalRowLabels: rowLabels))
                .ToArray();
        }

        return tools;
    }

    private IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildTurnEvidenceTools(
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            EvidenceBundle bundle,
            IReadOnlyList<string> observedEvidenceIds,
            IReadOnlySet<string> semanticallyRejectedEvidenceIds,
            bool enableWorkspaceWriterHandoff,
            int? requiredEvidenceCount,
            bool orderedByCanonicalLayout)
    {
        var visibleEvidenceIds = SelectEligibleWorkspaceEvidenceIds(
            bundle,
            observedEvidenceIds,
            semanticallyRejectedEvidenceIds,
            maximumCount: 80);
        var readyMechanicallyAvailable =
            enableWorkspaceWriterHandoff
            && requiredEvidenceCount is > 0
            && visibleEvidenceIds.Count >= requiredEvidenceCount.Value;
        return tools
            .Where(tool =>
                !string.Equals(
                    tool.Name,
                    EvidenceWorkspaceToolName,
                    StringComparison.OrdinalIgnoreCase)
                || !enableWorkspaceWriterHandoff
                || readyMechanicallyAvailable)
            .Select(tool => string.Equals(
                tool.Name,
                EvidenceWorkspaceToolName,
                StringComparison.OrdinalIgnoreCase)
                ? BuildEvidenceWorkspaceTool(
                    readyMechanicallyAvailable,
                    requiredEvidenceCount,
                    orderedByCanonicalLayout,
                    allowedEvidenceIds: readyMechanicallyAvailable
                        ? visibleEvidenceIds
                        : null)
                : tool)
            .ToArray();
    }

    private static IReadOnlyList<string>
        SelectEligibleWorkspaceEvidenceIds(
            EvidenceBundle bundle,
            IEnumerable<string> observedEvidenceIds,
            IReadOnlySet<string> semanticallyRejectedEvidenceIds,
            int maximumCount)
        => observedEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? item
                : null)
            .Where(item =>
                item is not null
                && !item.RiskFlags.Contains(
                    "orientation_only",
                    StringComparer.OrdinalIgnoreCase)
                && !semanticallyRejectedEvidenceIds.Contains(item.EvidenceId))
            .Cast<EvidenceItem>()
            .Where(HasRenderableEvidenceValue)
            .DistinctBy(
                static item => item.VisibleSourceKey,
                StringComparer.OrdinalIgnoreCase)
            .DistinctBy(
                static item => GetEvidenceDisplayValue(item),
                StringComparer.OrdinalIgnoreCase)
            .Take(maximumCount)
            .Select(static item => item.EvidenceId)
            .ToArray();
}
