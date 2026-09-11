using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedLlmPromptSanitizer
{
    private const string ControlMetadataPrefix = "SAAIA_SOURCE_BACKED_";

    public static IReadOnlyList<(string role, string content)> RemoveControlMetadata(
        IReadOnlyList<(string role, string content)> messages)
    {
        if (messages.Count == 0)
            return messages;

        List<(string role, string content)>? sanitized = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var content = string.Equals(message.role, "system", StringComparison.OrdinalIgnoreCase)
                ? RemoveStandaloneControlMetadata(message.content)
                : message.content;

            if (sanitized is null && !string.Equals(content, message.content, StringComparison.Ordinal))
            {
                sanitized = new List<(string role, string content)>(messages.Count);
                for (var priorIndex = 0; priorIndex < index; priorIndex++)
                    sanitized.Add(messages[priorIndex]);
            }

            sanitized?.Add((message.role, content));
        }

        return sanitized ?? messages;
    }

    private static string RemoveStandaloneControlMetadata(string? content)
    {
        if (string.IsNullOrEmpty(content)
            || !content.Contains(ControlMetadataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return content ?? string.Empty;
        }

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var kept = new List<string>(lines.Length);
        var removedAny = false;
        foreach (var line in lines)
        {
            if (line.Trim().StartsWith(ControlMetadataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                removedAny = true;
                continue;
            }

            kept.Add(line);
        }

        if (!removedAny)
            return content;

        var result = new StringBuilder(content.Length);
        for (var index = 0; index < kept.Count; index++)
        {
            if (index > 0)
                result.Append('\n');
            result.Append(kept[index]);
        }

        return result.ToString();
    }
}
