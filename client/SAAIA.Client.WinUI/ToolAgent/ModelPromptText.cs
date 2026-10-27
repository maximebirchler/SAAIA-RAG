namespace SAAIA.Client.WinUI.Services.ToolAgent;

// Prompt budgets use UTF-16 code units. Shorten only at whole-character
// boundaries so JSON serialization cannot replace half a surrogate pair.
internal static class ModelPromptText
{
    internal static string Prefix(string value, int maximumCharacters)
    {
        var end = Math.Clamp(maximumCharacters, 0, value.Length);
        if (SplitsCharacter(value, end)) end--;
        return value[..end];
    }

    internal static string Suffix(string value, int maximumCharacters)
    {
        var start = value.Length - Math.Clamp(maximumCharacters, 0, value.Length);
        if (SplitsCharacter(value, start)) start++;
        return value[start..];
    }

    internal static string Window(string value, int start, int maximumCharacters)
    {
        start = Math.Clamp(start, 0, value.Length);
        var end = start + Math.Clamp(maximumCharacters, 0, value.Length - start);
        // Move both edges inward. A partial character supplies no replacement
        // character and never expands the caller's original UTF-16 window.
        if (SplitsCharacter(value, start)) start++;
        if (SplitsCharacter(value, end)) end--;
        return end <= start ? string.Empty : value[start..end];
    }

    private static bool SplitsCharacter(string value, int offset)
        => offset > 0 && offset < value.Length
           && char.IsHighSurrogate(value[offset - 1]) && char.IsLowSurrogate(value[offset]);
}
