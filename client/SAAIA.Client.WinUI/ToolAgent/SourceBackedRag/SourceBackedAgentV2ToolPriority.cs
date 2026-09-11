namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string NormalizeDraftForProgressComparison(string? answer)
        => string.Join(
            " ",
            (answer ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static IReadOnlyList<SourceBackedAgentToolDefinition> PrioritizeToolsFromSemanticPlan(
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        string semanticPlan)
    {
        var approach = semanticPlan
            .Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(
                "APPROCHE_OUTILS:",
                StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(approach))
            return tools;

        var authoredOrder = tools
            .Select(tool => new
            {
                Tool = tool,
                Position = approach.IndexOf(
                    tool.Name,
                    StringComparison.OrdinalIgnoreCase)
            })
            .Where(static item => item.Position >= 0)
            .OrderBy(static item => item.Position)
            .Select(static item => item.Tool.Name)
            .ToArray();
        if (authoredOrder.Length == 0)
            return tools;

        var authoredRank = authoredOrder
            .Select(static (name, index) => (name, index))
            .ToDictionary(
                static pair => pair.name,
                static pair => pair.index,
                StringComparer.OrdinalIgnoreCase);
        return tools
            .Select(static (tool, index) => (tool, index))
            .OrderBy(pair => authoredRank.ContainsKey(pair.tool.Name) ? 0 : 1)
            .ThenBy(pair => authoredRank.GetValueOrDefault(pair.tool.Name, int.MaxValue))
            .ThenBy(static pair => pair.index)
            .Select(static pair => pair.tool)
            .ToArray();
    }

    private static SourceBackedAgentToolCall? ParsePlannedFirstAction(
        string semanticPlan,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
    {
        var line = semanticPlan
            .Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static value => value.StartsWith(
                "PREMIERE_ACTION:",
                StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var action = line[(line.IndexOf(':') + 1)..].Trim();
        if (string.Equals(action, "aucune", StringComparison.OrdinalIgnoreCase))
            return null;

        var jsonStart = action.IndexOf('{');
        var jsonEnd = action.LastIndexOf('}');
        if (jsonStart <= 0 || jsonEnd <= jsonStart)
            return null;

        var toolName = action[..jsonStart].Trim();
        if (!tools.Any(tool => string.Equals(
                tool.Name,
                toolName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                action[jsonStart..(jsonEnd + 1)]);
            if (document.RootElement.ValueKind
                != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            return new SourceBackedAgentToolCall(
                "plan-first-action",
                toolName,
                document.RootElement.Clone());
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
