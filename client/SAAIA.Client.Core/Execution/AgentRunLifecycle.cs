namespace SAAIA.Client.Core.Execution;

public enum AgentRunPhase { Created, Initializing, Running, ExecutingTools, Finalizing, Completed, Failed, Cancelled }
public sealed record AgentRunTransition(AgentRunPhase From, AgentRunPhase To, int Iteration, string? Reason);

/// <summary>Owns one run's lifecycle. Terminal states are exclusive; reusing a completed run is rejected.</summary>
public sealed class AgentRunLifecycle(Action<AgentRunTransition>? observe = null)
{
    public AgentRunPhase Phase { get; private set; } = AgentRunPhase.Created;
    public int Iteration { get; private set; }
    public int ObserverFailures { get; private set; }
    public AgentIterationIntent? Intent { get; private set; }
    public bool IsTerminal => Phase is AgentRunPhase.Completed or AgentRunPhase.Failed or AgentRunPhase.Cancelled;
    public void Initialize() => Transition(AgentRunPhase.Initializing);
    public void BeginIteration(int iteration)
    {
        if (iteration < 1 || iteration < Iteration) throw new ArgumentOutOfRangeException(nameof(iteration));
        if (IsTerminal) throw new InvalidOperationException("agent_run_already_terminal");
        if (Phase is not (AgentRunPhase.Initializing or AgentRunPhase.Running or AgentRunPhase.ExecutingTools))
            throw new InvalidOperationException($"agent_run_invalid_transition:{Phase}:{AgentRunPhase.Running}");
        Iteration = iteration;
        Transition(AgentRunPhase.Running);
    }
    public void BeginTools() => Transition(AgentRunPhase.ExecutingTools);
    public void PlanIntent(AgentIterationIntent intent)
    {
        if (Phase != AgentRunPhase.Running) throw new InvalidOperationException("agent_run_not_running");
        Intent = intent;
        Transition(AgentRunPhase.Running, intent.ToString());
    }
    public void EndTools() => Transition(AgentRunPhase.Running);
    public void Complete(string? reason = null)
    {
        Transition(AgentRunPhase.Finalizing);
        Transition(AgentRunPhase.Completed, reason);
    }
    public void Fail(string reason) => Transition(AgentRunPhase.Failed, reason);
    public void Cancel() => Transition(AgentRunPhase.Cancelled, "cancellation_requested");

    private void Transition(AgentRunPhase next, string? reason = null)
    {
        if (IsTerminal) throw new InvalidOperationException("agent_run_already_terminal");
        var allowed = next switch
        {
            AgentRunPhase.Initializing => Phase == AgentRunPhase.Created,
            AgentRunPhase.Running => Phase is AgentRunPhase.Initializing or AgentRunPhase.Running or AgentRunPhase.ExecutingTools,
            AgentRunPhase.ExecutingTools => Phase == AgentRunPhase.Running,
            AgentRunPhase.Finalizing => Phase is AgentRunPhase.Initializing or AgentRunPhase.Running,
            AgentRunPhase.Completed => Phase == AgentRunPhase.Finalizing,
            AgentRunPhase.Failed or AgentRunPhase.Cancelled => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"agent_run_invalid_transition:{Phase}:{next}");
        var previous = Phase;
        Phase = next;
        // Observers are diagnostics and cannot change the run's outcome.
        try { observe?.Invoke(new(previous, next, Iteration, reason)); } catch { ObserverFailures++; }
    }
}
