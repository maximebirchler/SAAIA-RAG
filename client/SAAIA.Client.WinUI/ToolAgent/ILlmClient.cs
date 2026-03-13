namespace SAAIA.Client.WinUI.Services.ToolAgent;

public interface ILlmClient
{
    Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct);

    Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct);

    Task<string> CompleteAsync(IEnumerable<(string role, string content)> messages, bool forceJson, CancellationToken ct)
        => CompleteAsync(messages as IReadOnlyList<(string role, string content)> ?? messages.ToList(), forceJson, ct);

    Task StreamAsync(IEnumerable<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
        => StreamAsync(messages as IReadOnlyList<(string role, string content)> ?? messages.ToList(), forceJson, onDelta, ct);
}
