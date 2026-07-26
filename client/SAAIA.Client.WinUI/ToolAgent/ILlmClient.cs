namespace SAAIA.Client.WinUI.Services.ToolAgent;

public interface ILlmClient
{
    bool SupportsStructuredOutput => false;

    Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct);

    Task<string> CompleteStructuredAsync(
        IReadOnlyList<(string role, string content)> messages,
        LlmStructuredOutputContract contract,
        CancellationToken ct)
        => CompleteAsync(messages, forceJson: true, ct);

    Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct);

    Task<string> CompleteAsync(IEnumerable<(string role, string content)> messages, bool forceJson, CancellationToken ct)
        => CompleteAsync(messages as IReadOnlyList<(string role, string content)> ?? messages.ToList(), forceJson, ct);

    Task<string> CompleteStructuredAsync(
        IEnumerable<(string role, string content)> messages,
        LlmStructuredOutputContract contract,
        CancellationToken ct)
        => CompleteStructuredAsync(
            messages as IReadOnlyList<(string role, string content)> ?? messages.ToList(),
            contract,
            ct);

    Task StreamAsync(IEnumerable<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
        => StreamAsync(messages as IReadOnlyList<(string role, string content)> ?? messages.ToList(), forceJson, onDelta, ct);
}
