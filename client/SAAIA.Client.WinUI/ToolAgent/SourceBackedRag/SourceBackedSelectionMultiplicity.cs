namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

/// <summary>The current named-item protocol assigns distinct items. Reusable cell assignments need a separate versioned contract.</summary>
internal static class SourceBackedSelectionMultiplicity
{
    internal const bool SupportsRepeatedNamedAssignments = false;
    internal const string AgentContract = "For named-item selections this client protocol requires distinct source items and distinct displayed values. Do not silently reinterpret an explicit user request to reuse an item as a diversity request: explain the capability limit or request a compatible scope. Reusable assignments are not supported by this protocol.";
    internal static bool HasDuplicates(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return values.Any(value => !seen.Add(value));
    }
}
