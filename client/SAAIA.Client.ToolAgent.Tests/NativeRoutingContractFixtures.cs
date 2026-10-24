using System.Text.Json;
using SAAIA.Client.Core.NativeRouting;
using SAAIA.Client.WinUI.Services.ToolAgent.NativeRouting;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.ToolAgent.Tests;

/// <summary>Test input adapters; production owners expose their real typed contracts directly.</summary>
internal static class NativeRoutingContractFixtures
{
    internal static string SpecializedPrompt(string routeToolName)
        => NativeRouterPrompts.BuildNativeRouterSpecializedSystemPrompt(routeToolName, "fr", false);

    internal static string[] SecondStageToolNames(string selectedRouteToolName, bool classifierAccepted)
    {
        var all = NativeRouterToolCatalog.BuildNativeRouterTools();
        return (classifierAccepted ? NativeRouterToolCatalog.BuildNativeRouterSecondStageTools(all, selectedRouteToolName) : all)
            .Select(static tool => tool.Name).ToArray();
    }

    internal static IReadOnlyList<SourceBackedAgentToolDefinition> RepairTools(string routeToolName)
        => NativeRouterToolCatalog.BuildNativeRouterRepairTools(NativeRouterToolCatalog.BuildNativeRouterTools(), routeToolName);

    internal static bool TryReadLayout(string json, string compactKind, int atomicEvidenceCount,
        out string rowHeader, out IReadOnlyList<string> rowLabels, out IReadOnlyList<string> columns)
    {
        using var document = JsonDocument.Parse(json);
        var valid = NativeRouterResponseReader.TryReadNativeRouterLayout(document.RootElement, compactKind,
            atomicEvidenceCount, out rowHeader, out var parsedRows, out var parsedColumns);
        rowLabels = parsedRows;
        columns = parsedColumns;
        return valid;
    }
}
