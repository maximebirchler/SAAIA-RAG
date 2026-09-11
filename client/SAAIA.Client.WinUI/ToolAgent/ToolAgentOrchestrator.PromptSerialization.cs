using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string SerializeToolResults(ToolResults tr)
    {
        var output = tr.Items.Select(x => new
        {
            tool = x.ToolName,
            error = x.Error,
            durationMs = x.DurationMs,
            result = x.Result
        }).ToList();

        return JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string SerializeTail(IReadOnlyList<(string role, string content)> hist, int maxTurns)
    {
        var tail = hist
            .TakeLast(maxTurns)
            .Select(m => new
            {
                role = m.role,
                content = TruncateForPrompt(m.content, SerializedTailContentMaxChars)
            })
            .ToList();
        return JsonSerializer.Serialize(tail);
    }

    private static string TruncateForPrompt(string? value, int maxChars)
    {
        var s = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (maxChars <= 0 || s.Length <= maxChars)
            return s;

        return s[..maxChars].TrimEnd() + "...";
    }
}
