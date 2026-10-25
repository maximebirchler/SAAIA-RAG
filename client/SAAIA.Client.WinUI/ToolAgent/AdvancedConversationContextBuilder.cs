using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

internal static class AdvancedConversationContextBuilder
{
    internal static AdvancedAnalysisConversationContext? Build(
        IReadOnlyList<(string role, string content)> history, string currentRequest)
    {
        var eligible = history.Where(turn => (turn.role is "user" or "assistant")
                && !string.IsNullOrWhiteSpace(turn.content)
                && !(turn.role == "user" && string.Equals(turn.content, currentRequest, StringComparison.Ordinal)))
            .ToArray();
        var retained = new List<AdvancedAnalysisConversationTurn>();
        var characters = 0;
        foreach (var turn in eligible.Reverse())
        {
            if (retained.Count >= AdvancedAnalysisConversationContextPolicy.MaximumTurns
                || characters + turn.content.Length > AdvancedAnalysisConversationContextPolicy.MaximumCharacters)
                break; // Keep complete statements and preserve chronological revocation semantics.
            retained.Add(new() { Role = turn.role, Text = turn.content });
            characters += turn.content.Length;
        }
        retained.Reverse();
        return eligible.Length == 0 ? null : new()
        {
            IsDocumentaryEvidence = false,
            Truncated = retained.Count != eligible.Length,
            Turns = retained
        };
    }
}
