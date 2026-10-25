namespace SAAIA.Contracts;

/// <summary>User and assistant statements clarify a follow-up; they are never documentary evidence.</summary>
public sealed class AdvancedAnalysisConversationContext
{
    public bool IsDocumentaryEvidence { get; init; }
    public bool Truncated { get; init; }
    public List<AdvancedAnalysisConversationTurn> Turns { get; init; } = new();
}

public sealed class AdvancedAnalysisConversationTurn
{
    public string Role { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
}

public static class AdvancedAnalysisConversationContextPolicy
{
    public const int MaximumTurns = 12;
    public const int MaximumCharacters = 16_000;

    public static string? Validate(AdvancedAnalysisConversationContext? context)
    {
        if (context is null) return null; // Older clients provide no conversation context.
        if (context.IsDocumentaryEvidence) return "conversation_context_cannot_be_evidence";
        if (context.Turns is null || context.Turns.Count > MaximumTurns)
            return "conversation_context_turn_limit_exceeded";
        long characters = 0;
        foreach (var turn in context.Turns)
        {
            if (turn is null || turn.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(turn.Text))
                return "conversation_context_turn_invalid";
            characters += turn.Text.Length;
        }
        return characters > MaximumCharacters ? "conversation_context_character_limit_exceeded" : null;
    }
}
