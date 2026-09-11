namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public interface ISourceBackedRagToolExecutor
{
    Task<ToolResults> ExecuteAsync(SourceBackedIntake intake, RetrievalPlan plan, CancellationToken ct);
}
