using System.Net;
using System.Net.Http;
using System.Text.Json;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private AdvancedAnalysisClientService CreateAdvancedAnalysisService()
        => new(_api, (sources, answer) =>
        {
            _mem.LastSourcesUsed = sources;
            _mem.LastRouterIntent = "advanced_analysis.answer";
            _mem.LastAnswerSource = "advanced_analysis:server";
            _mem.LastAssistantAnswer = answer;
        });

    internal Task<AdvancedAnalysisClientExecutionResult>
        ExecuteAdvancedAnalysisHandoffAsync(
            Guid sessionId,
            AdvancedAnalysisHandoffEnvelope handoff,
            CancellationToken cancellationToken,
            Action<string>? onProgress = null,
            Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot = null)
        => CreateAdvancedAnalysisService().ExecuteAdvancedAnalysisHandoffCoreAsync(
            sessionId,
            handoff,
            new AdvancedAnalysisClientPollingOptions(),
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken,
            onProgress,
            onSnapshot);

    internal Task<AdvancedAnalysisClientExecutionResult>
        ExecuteAdvancedAnalysisHandoffForTestsAsync(
            Guid sessionId,
            AdvancedAnalysisHandoffEnvelope handoff,
            AdvancedAnalysisClientPollingOptions options,
            Func<TimeSpan, CancellationToken, Task> delayAsync,
            CancellationToken cancellationToken,
            Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot = null)
        => CreateAdvancedAnalysisService().ExecuteAdvancedAnalysisHandoffCoreAsync(
            sessionId,
            handoff,
            options,
            delayAsync,
            cancellationToken,
            onProgress: null,
            onSnapshot);

    internal Task<AdvancedAnalysisClientExecutionResult>
        ResumeAdvancedAnalysisJobAsync(
            Guid sessionId,
            Guid handoffId,
            Guid jobId,
            int minimumRevision,
            string language,
            CancellationToken cancellationToken,
            Action<string>? onProgress = null,
            Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot = null)
        => CreateAdvancedAnalysisService().ResumeAdvancedAnalysisJobCoreAsync(
            sessionId,
            handoffId,
            jobId,
            minimumRevision,
            language,
            new AdvancedAnalysisClientPollingOptions(),
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken,
            onProgress,
            onSnapshot);

    internal Task<AdvancedAnalysisClientExecutionResult>
        ResumeAdvancedAnalysisJobForTestsAsync(
            Guid sessionId,
            Guid handoffId,
            Guid jobId,
            int minimumRevision,
            string language,
            AdvancedAnalysisClientPollingOptions options,
            Func<TimeSpan, CancellationToken, Task> delayAsync,
            CancellationToken cancellationToken,
            Func<AdvancedAnalysisClientExecutionResult, CancellationToken, Task>? onSnapshot = null)
        => CreateAdvancedAnalysisService().ResumeAdvancedAnalysisJobCoreAsync(
            sessionId,
            handoffId,
            jobId,
            minimumRevision,
            language,
            options,
            delayAsync,
            cancellationToken,
            onProgress: null,
            onSnapshot);

}
