namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

/// <summary>The current user request is the reference contract, not a disposable excerpt of evidence.</summary>
internal static class SourceBackedUserRequestPrompt
{
    internal static string Render(string? question) => question?.Trim() ?? string.Empty;
}
