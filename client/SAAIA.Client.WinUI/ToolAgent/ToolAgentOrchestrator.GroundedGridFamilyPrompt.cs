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
                + "other information about that row subject. Example: recipes already named as rows with "
                + "ingredients, duration, and source columns are property rows."),
            BuildGroundedGridFamilyTool(
                ClassifyGridSlotsToolName,
                "Choose when the rows and columns are planning or schedule coordinates and every cell will "
                + "name a new recipe, activity, product, document, or other object selected to fill that slot. "
                + "Example: days as rows and meal moments as columns, with a recipe name put into each cell, "
                + "are slots.")
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
