namespace SAAIA.Client.Core.Execution;

public enum AgentIterationDisposition { Advance, Continue, Stop, Complete }

/// <summary>A phase either advances, starts the next iteration, stops, or supplies one terminal result.</summary>
public readonly record struct AgentIterationOutcome<TResult> where TResult : class
{
    private AgentIterationOutcome(AgentIterationDisposition disposition, TResult? result = null)
    {
        Disposition = disposition;
        Result = result;
    }

    public AgentIterationDisposition Disposition { get; }
    public TResult? Result { get; }
    public static AgentIterationOutcome<TResult> Advance => new(AgentIterationDisposition.Advance);
    public static AgentIterationOutcome<TResult> Continue => new(AgentIterationDisposition.Continue);
    public static AgentIterationOutcome<TResult> Stop => new(AgentIterationDisposition.Stop);
    public static AgentIterationOutcome<TResult> Complete(TResult result) =>
        new(AgentIterationDisposition.Complete, result ?? throw new ArgumentNullException(nameof(result)));
}
