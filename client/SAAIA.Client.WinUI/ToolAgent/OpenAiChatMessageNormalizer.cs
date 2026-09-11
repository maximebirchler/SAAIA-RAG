namespace SAAIA.Client.WinUI.Services.ToolAgent;

internal static class OpenAiChatMessageNormalizer
{
    public static IReadOnlyList<(string role, string content)> MergeSystemMessages(
        IReadOnlyList<(string role, string content)> messages)
    {
        var systemContents = messages
            .Where(static message => string.Equals(
                message.role,
                "system",
                StringComparison.OrdinalIgnoreCase))
            .Select(static message => message.content?.Trim() ?? string.Empty)
            .Where(static content => content.Length > 0)
            .ToArray();
        if (systemContents.Length <= 1
            && (systemContents.Length == 0
                || string.Equals(messages[0].role, "system", StringComparison.OrdinalIgnoreCase)))
        {
            return messages;
        }

        var normalized = new List<(string role, string content)>(messages.Count);
        if (systemContents.Length > 0)
            normalized.Add(("system", string.Join("\n\n", systemContents)));
        normalized.AddRange(messages.Where(static message => !string.Equals(
            message.role,
            "system",
            StringComparison.OrdinalIgnoreCase)));
        return normalized;
    }
}
