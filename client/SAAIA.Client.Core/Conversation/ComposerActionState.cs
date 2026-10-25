namespace SAAIA.Client.Core.Conversation;

public enum ComposerActionKind { Send, Cancel, Cancelling }

/// <summary>The composer advertises only an action the current conversation can admit.</summary>
public readonly record struct ComposerActionState(ComposerActionKind Kind, bool IsEnabled)
{
    public static ComposerActionState Resolve(bool isConnected, bool hasSession, bool isChangingSession,
        bool isGenerating, bool isCancelling, bool hasText)
    {
        if (isGenerating && isCancelling)
            return new(ComposerActionKind.Cancelling, false);

        var available = isConnected && hasSession && !isChangingSession;
        return isGenerating
            ? new(ComposerActionKind.Cancel, available)
            : new(ComposerActionKind.Send, available && hasText);
    }
}
