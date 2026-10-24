using SAAIA.Client.Core.Conversation;
using Xunit;

namespace SAAIA.Client.Core.Tests;

public sealed class ConversationActivityGateTests
{
    [Fact]
    public async Task A_session_load_blocks_sends_and_another_load_until_its_owner_finishes()
    {
        var gate = new ConversationActivityGate();
        using var load = gate.TryBeginSessionChange("session-b");
        Assert.NotNull(load);
        var pendingRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = pendingRead.Task;
        Assert.False(read.IsCompleted);
        Assert.Null(gate.TryBeginTurn("session-b"));
        Assert.Null(gate.TryBeginSessionChange("session-c"));
        pendingRead.SetResult();
        await read;
        Assert.True(load.IsCurrent);
        load.Dispose();
        using var turn = gate.TryBeginTurn("session-b");
        Assert.NotNull(turn);
        Assert.Equal("session-b", turn.SessionId);
    }

    [Fact]
    public void Cancellation_of_a_turn_does_not_allow_resetting_memory_before_that_turn_finishes()
    {
        var gate = new ConversationActivityGate();
        using var turn = gate.TryBeginTurn("session-a");
        gate.CancelActiveTurn();
        Assert.True(turn!.CancellationToken.IsCancellationRequested);
        Assert.True(gate.IsRunningTurn);
        Assert.Null(gate.TryBeginSessionChange("session-b"));
        Assert.Null(gate.TryBeginTurn("session-a"));
        turn.Dispose();
        using var load = gate.TryBeginSessionChange("session-b");
        Assert.NotNull(load);
    }

    [Fact]
    public void Completion_of_an_old_owner_cannot_release_a_later_session_or_turn()
    {
        var gate = new ConversationActivityGate();
        var first = gate.TryBeginSessionChange("session-a")!;
        first.Dispose();
        using var second = gate.TryBeginSessionChange("session-b");
        first.Dispose();
        Assert.False(first.IsCurrent);
        Assert.True(second!.IsCurrent);
        Assert.True(gate.IsChangingSession);
        Assert.Null(gate.TryBeginTurn("session-b"));
    }

    [Fact]
    public void A_failed_session_read_releases_admission_without_rebinding_the_turn_session()
    {
        var gate = new ConversationActivityGate();
        Action failRead = () =>
        {
            using var read = gate.TryBeginSessionChange("session-b");
            throw new IOException("synthetic read failure");
        };
        Assert.Throws<IOException>(failRead);
        using var turn = gate.TryBeginTurn("session-a");
        Assert.NotNull(turn);
        Assert.Equal("session-a", turn.SessionId);
    }

    [Fact]
    public void Concurrent_admission_has_exactly_one_owner()
    {
        var gate = new ConversationActivityGate();
        var leases = new System.Collections.Concurrent.ConcurrentBag<ConversationActivityGate.ActivityLease>();
        Parallel.For(0, 32, index =>
        {
            var lease = index % 2 == 0 ? gate.TryBeginSessionChange("load") : gate.TryBeginTurn("turn");
            if (lease is not null) leases.Add(lease);
        });
        Assert.Single(leases).Dispose();
        using var next = gate.TryBeginTurn("next");
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Cancelling_during_durable_message_preparation_preserves_the_write_and_prevents_model_start()
    {
        var gate = new ConversationActivityGate();
        using var turn = gate.TryBeginTurn("session-a");
        var token = turn!.CancellationToken;
        var durableWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeFinished = false;
        var modelCalls = 0;
        async Task PrepareAndStartModelAsync()
        {
            await durableWrite.Task;
            writeFinished = true;
            token.ThrowIfCancellationRequested();
            modelCalls++;
        }
        var pendingTurn = PrepareAndStartModelAsync();
        gate.CancelActiveTurn();
        durableWrite.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingTurn);
        Assert.True(writeFinished);
        Assert.Equal(0, modelCalls);
        Assert.Null(gate.TryBeginSessionChange("session-b"));
        turn.Dispose();
        using var next = gate.TryBeginTurn("session-b");
        Assert.False(next!.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Cancelling_the_current_owner_never_cancels_a_completed_turn_token()
    {
        var gate = new ConversationActivityGate();
        var old = gate.TryBeginTurn("session-a")!;
        var oldToken = old.CancellationToken;
        old.Dispose();
        using var current = gate.TryBeginTurn("session-b");
        gate.CancelActiveTurn();
        Assert.False(oldToken.IsCancellationRequested);
        Assert.True(current!.CancellationToken.IsCancellationRequested);
        Assert.True(current.IsCurrent);
    }
}
