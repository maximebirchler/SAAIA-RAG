using SAAIA.Client.Core.Execution;
using Xunit;

namespace SAAIA.Client.Core.Tests;

public sealed class AgentRunLifecycleTests
{
    [Fact]
    public void Normal_run_has_exclusive_terminal_state_and_tracks_iterations()
    {
        var transitions = new List<AgentRunTransition>();
        var run = new AgentRunLifecycle(transitions.Add);
        run.Initialize(); run.BeginIteration(1); run.BeginTools(); run.EndTools(); run.BeginIteration(2); run.Complete("verified");
        Assert.Equal(AgentRunPhase.Completed, run.Phase);
        Assert.Equal(2, run.Iteration);
        Assert.Single(transitions, x => x.To == AgentRunPhase.Completed);
        Assert.Throws<InvalidOperationException>(() => run.Fail("late_error"));
        Assert.Throws<InvalidOperationException>(() => run.BeginIteration(3));
        Assert.Equal(2, run.Iteration);
    }

    [Theory]
    [InlineData(true, AgentRunPhase.Cancelled)]
    [InlineData(false, AgentRunPhase.Failed)]
    public void Initialization_failure_or_cancellation_is_terminal(bool cancel, AgentRunPhase expected)
    {
        var run = new AgentRunLifecycle(_ => throw new IOException());
        run.Initialize();
        if (cancel) run.Cancel(); else run.Fail("transport_failed");
        Assert.Equal(expected, run.Phase);
        Assert.True(run.IsTerminal);
        Assert.Equal(2, run.ObserverFailures);
    }

    [Fact]
    public void Invalid_phase_order_and_backwards_iteration_are_rejected()
    {
        var run = new AgentRunLifecycle();
        Assert.Throws<InvalidOperationException>(() => run.BeginTools());
        run.Initialize(); run.BeginIteration(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => run.BeginIteration(1));
        Assert.Throws<InvalidOperationException>(() => run.Initialize());
    }
}
