namespace SAAIA.Client.WinUI.Services.ToolAgent;

public interface ILlmClient
{
    // Primary signature: implementers should implement this one.
    Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct);

    // Convenience overload: allows callers to pass IEnumerable without forcing every implementation to support it.
    Task<string> CompleteAsync(IEnumerable<(string role, string content)> messages, bool forceJson, CancellationToken ct)
        => CompleteAsync(messages as IReadOnlyList<(string role, string content)> ?? messages.ToList(), forceJson, ct);
}
