using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildGroundedGridFamilyTools()
        =>
        [
            BuildGroundedGridFamilyTool(
                ClassifyGridPropertiesToolName,
                "Choose when every row already names the subject being described and each cell will contain "
                + "a fact, property, definition, requirement, measurement, instruction, duration, source, or "
                + "other information about that row subject. The row subject remains the same across its columns."),
            BuildGroundedGridFamilyTool(
                ClassifyGridSlotsToolName,
                "Choose when the rows and columns identify positions and every cell will name an item "
                + "selected to fill that position. The axes identify where the item goes, not a subject "
                + "already named by the row.")
        ];

    private static SourceBackedAgentToolDefinition BuildGroundedGridFamilyTool(
        string name,
        string description)
        => new(
            name,
            description,
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>(),
                additionalProperties = false
            }, ClientJson.CamelCase));
}
