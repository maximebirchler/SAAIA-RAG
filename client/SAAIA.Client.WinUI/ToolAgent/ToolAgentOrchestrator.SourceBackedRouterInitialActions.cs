using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IReadOnlyList<SourceBackedInitialToolCall>
        BuildSourceBackedRouterInitialActions(RouterPlan plan)
    {
        if (plan.Origin != RouterPlanOrigin.Llm)
            return Array.Empty<SourceBackedInitialToolCall>();

        var actions = new List<SourceBackedInitialToolCall>();
        for (var index = 0; index < plan.ToolCalls.Count; index++)
        {
            var call = plan.ToolCalls[index];
            if (call.Args.ValueKind != System.Text.Json.JsonValueKind.Object
                || !SourceBackedAgentToolCatalog.TryResolveExternalName(
                    call.Name,
                    out var externalName))
            {
                continue;
            }

            var normalizedArguments =
                SourceBackedAgentToolCatalog.NormalizeRouterArguments(
                    call.Name,
                    call.Args);
            if (string.Equals(
                    externalName,
                    "documents_content_cards",
                    StringComparison.Ordinal)
                && plan.SourceBackedMission?.StructuredLayout == true
                && plan.GroundedGridDiscoveryQueries.Count >= 2)
            {
                for (var rowIndex = 0;
                     rowIndex < plan.GroundedGridDiscoveryQueries.Count;
                     rowIndex++)
                {
                    var rowQuery = plan.GroundedGridDiscoveryQueries[rowIndex];
                    if (string.IsNullOrWhiteSpace(rowQuery))
                        continue;
                    actions.Add(new SourceBackedInitialToolCall(
                        $"router-plan-{index + 1}-grounded-row-{rowIndex + 1}",
                        externalName,
                        WithSourceBackedContentCardQuery(
                            normalizedArguments,
                            rowQuery),
                        "llm_router_grounded_grid_row",
                        rowQuery));
                }
                continue;
            }

            actions.Add(new SourceBackedInitialToolCall(
                $"router-plan-{index + 1}",
                externalName,
                normalizedArguments,
                "llm_router",
                string.IsNullOrWhiteSpace(call.QueryHint)
                    ? ReadSourceBackedRouterQueryHint(call.Args)
                    : call.QueryHint.Trim()));
        }

        return actions;
    }

    private static JsonElement WithSourceBackedContentCardQuery(
        JsonElement arguments,
        string query)
    {
        var root = JsonNode.Parse(arguments.GetRawText())?.AsObject()
                   ?? new JsonObject();
        root["q"] = query.Trim();
        return JsonSerializer.SerializeToElement(root);
    }

    private static string? ReadSourceBackedRouterQueryHint(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[] { "query", "q" })
        {
            if (args.TryGetProperty(propertyName, out var queryElement)
                && queryElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(queryElement.GetString()))
            {
                return queryElement.GetString()!.Trim();
            }
        }

        return null;
    }

    private static SourceBackedInitialSemanticMission?
        BuildSourceBackedRouterSemanticMission(RouterPlan plan)
    {
        if (plan.Origin != RouterPlanOrigin.Llm
            || plan.SourceBackedMission is null)
        {
            return null;
        }

        return new SourceBackedInitialSemanticMission(
            JsonSerializer.SerializeToElement(
                plan.SourceBackedMission,
                ClientJson.CamelCase),
            "llm_router");
    }
}
