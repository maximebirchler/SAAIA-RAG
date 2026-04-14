using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    internal static string NormalizeRouterIntentForTests(string? intent)
        => NormalizeRouterIntent(intent);

    internal static string InferIntentFromToolCallsForTests(params RouterPlan.ToolCall[] toolCalls)
        => InferIntentFromToolCalls(toolCalls) ?? string.Empty;

    internal static RouterPlan.ToolCall[] SanitizeToolCallsForTests(params RouterPlan.ToolCall[] toolCalls)
        => SanitizeToolCalls(toolCalls).ToArray();

    internal static JsonElement NormalizeToolArgsForTests(string toolName, string jsonArgs)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(jsonArgs) ? "{}" : jsonArgs);
        return NormalizeToolArgs(toolName, doc.RootElement);
    }
}
