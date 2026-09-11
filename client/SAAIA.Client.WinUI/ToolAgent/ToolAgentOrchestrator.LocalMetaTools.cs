using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static List<ToolResults.Item> ExecuteLocalTools(RouterPlan plan, IReadOnlyList<(string role, string content)> chatHistory)
    {
        var items = new List<ToolResults.Item>();

        if (plan.ToolCalls.Any(c => string.Equals(c.Name, "meta.list_questions", StringComparison.OrdinalIgnoreCase)))
        {
            var questions = chatHistory
                .Where(m => string.Equals(m.role, "user", StringComparison.OrdinalIgnoreCase))
                .Select(m => (m.content ?? string.Empty).Trim())
                .Where(s => s.Length > 0)
                .TakeLast(50)
                .ToList();

            var payload = new { questions };
            items.Add(new ToolResults.Item
            {
                ToolName = "meta.list_questions",
                Result = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement,
                DurationMs = 0
            });
        }

        return items;
    }
}
