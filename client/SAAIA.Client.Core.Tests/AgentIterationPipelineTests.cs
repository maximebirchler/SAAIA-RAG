using SAAIA.Client.Core.Execution;
using Xunit;

namespace SAAIA.Client.Core.Tests;

public sealed class AgentIterationPipelineTests
{
    [Theory]
    [InlineData(AgentIterationDisposition.Advance, 4)]
    [InlineData(AgentIterationDisposition.Continue, 2)]
    [InlineData(AgentIterationDisposition.Stop, 2)]
    [InlineData(AgentIterationDisposition.Complete, 2)]
    public async Task Ordered_phases_share_only_their_run_and_iteration_and_short_circuit(
        AgentIterationDisposition disposition, int expectedSteps)
    {
        var run = new List<int>();
        var iteration = new List<string>();
        var observations = new List<AgentIterationObservation>();
        var final = new object();
        var steps = Enumerable.Range(1, 4).Select(index =>
            new AgentIterationStep<List<int>, List<string>, object>("phase_" + index, (state, turn, _) =>
            {
                Assert.Same(run, state);
                Assert.Same(iteration, turn);
                state.Add(index);
                turn.Add("iteration_" + index);
                return Task.FromResult(index == 2 ? disposition switch
                {
                    AgentIterationDisposition.Continue => AgentIterationOutcome<object>.Continue,
                    AgentIterationDisposition.Stop => AgentIterationOutcome<object>.Stop,
                    AgentIterationDisposition.Complete => AgentIterationOutcome<object>.Complete(final),
                    _ => AgentIterationOutcome<object>.Advance
                } : AgentIterationOutcome<object>.Advance);
            })).ToArray();
        var health = new AgentIterationObserverHealth();
        var result = await AgentIterationPipeline.RunAsync(run, iteration, steps, CancellationToken.None, health, observations.Add);
        Assert.Equal(disposition, result.Disposition);
        Assert.Equal(Enumerable.Range(1, expectedSteps), run);
        Assert.Equal(expectedSteps, iteration.Count);
        Assert.Equal(expectedSteps, observations.Count);
        Assert.Equal(0, health.ObserverFailures);
        Assert.All(observations, observation => { Assert.Equal("success", observation.Outcome); Assert.True(observation.ElapsedMilliseconds >= 0); });
        if (disposition == AgentIterationDisposition.Complete) Assert.Same(final, result.Result);
        else Assert.Null(result.Result);
        Assert.Throws<ArgumentNullException>(() => AgentIterationOutcome<object>.Complete(null!));
    }

    [Theory]
    [InlineData(false, "failed", "IOException")]
    [InlineData(true, "cancelled", "cancellation_requested")]
    public async Task Failed_or_cancelled_phase_is_observed_and_never_reaches_later_phases(
        bool cancel, string expectedOutcome, string expectedReason)
    {
        using var cancellation = new CancellationTokenSource();
        var observations = new List<AgentIterationObservation>();
        var laterCalled = false;
        var steps = new[]
        {
            new AgentIterationStep<object, object, object>("acquire", (_, _, _) =>
            {
                if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                throw new IOException("private error must not appear in observations");
            }),
            new AgentIterationStep<object, object, object>("tools", (_, _, _) =>
            {
                laterCalled = true;
                return Task.FromResult(AgentIterationOutcome<object>.Advance);
            })
        };
        var health = new AgentIterationObserverHealth();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => AgentIterationPipeline.RunAsync(new object(), new object(), steps,
            cancellation.Token, health, observation => { observations.Add(observation); throw new IOException("observer"); }));
        Assert.Equal(1, health.ObserverFailures);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("private error must not appear in observations", Assert.IsType<IOException>(error).Message);
        Assert.False(laterCalled);
        var observed = Assert.Single(observations);
        Assert.Equal(expectedOutcome, observed.Outcome);
        Assert.Equal(expectedReason, observed.Reason);
        Assert.DoesNotContain("private", observed.ToString());
    }

    [Fact]
    public async Task Observer_failure_cannot_replace_the_completed_result()
    {
        var result = new object();
        var health = new AgentIterationObserverHealth();
        var steps = new[] { new AgentIterationStep<object, object, object>("complete",
            (_, _, _) => Task.FromResult(AgentIterationOutcome<object>.Complete(result))) };
        var observed = await AgentIterationPipeline.RunAsync(new object(), new object(), steps, CancellationToken.None,
            health, _ => throw new IOException("observer"));
        Assert.Same(result, observed.Result);
        Assert.Equal(1, health.ObserverFailures);
    }
}
