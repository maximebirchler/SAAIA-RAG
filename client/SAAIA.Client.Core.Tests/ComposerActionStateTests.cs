using SAAIA.Client.Core.Conversation;
using Xunit;

namespace SAAIA.Client.Core.Tests;

public sealed class ComposerActionStateTests
{
    [Fact]
    public void Cancellation_remains_non_actionable_until_the_actual_turn_owner_releases_admission()
    {
        var gate = new ConversationActivityGate();
        using var turn = gate.TryBeginTurn("session-a");
        var generating = FromGate(gate);
        Assert.Equal(ComposerActionKind.Cancel, generating.Kind);
        Assert.True(generating.IsEnabled);

        gate.CancelActiveTurn();
        var cancelling = FromGate(gate);
        Assert.Equal(ComposerActionKind.Cancelling, cancelling.Kind);
        Assert.False(cancelling.IsEnabled);
        Assert.Null(gate.TryBeginTurn("session-a"));
        Assert.Null(gate.TryBeginSessionChange("session-b"));

        turn!.Dispose();
        var idle = FromGate(gate);
        Assert.Equal(ComposerActionKind.Send, idle.Kind);
        Assert.True(idle.IsEnabled);
        using var next = gate.TryBeginTurn("session-a");
        Assert.Equal(ComposerActionKind.Cancel, FromGate(gate).Kind);
    }

    [Fact]
    public void An_open_configuration_keeps_the_composer_disabled_until_its_owner_finishes()
    {
        var gate = new ConversationActivityGate();
        using var configuration = gate.TryBeginSessionChange();
        Assert.False(FromGate(gate).IsEnabled);
        configuration!.Dispose();
        Assert.True(FromGate(gate).IsEnabled);
    }

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    public void Missing_connection_session_free_admission_or_text_disables_send(bool connected, bool session, bool changingSession, bool text)
    {
        var action = ComposerActionState.Resolve(connected, session, changingSession,
            isGenerating: false, isCancelling: false, hasText: text);
        Assert.Equal(ComposerActionKind.Send, action.Kind);
        Assert.False(action.IsEnabled);
    }

    [Fact]
    public void A_stale_cancelling_flag_cannot_replace_an_idle_send_action()
    {
        var action = ComposerActionState.Resolve(true, true, false,
            isGenerating: false, isCancelling: true, hasText: true);
        Assert.Equal(ComposerActionKind.Send, action.Kind);
        Assert.True(action.IsEnabled);
    }

    private static ComposerActionState FromGate(ConversationActivityGate gate)
        => ComposerActionState.Resolve(true, true, gate.IsChangingSession,
            gate.IsRunningTurn, gate.IsTurnCancellationRequested, hasText: true);
}
