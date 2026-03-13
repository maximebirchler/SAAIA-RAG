using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<(bool handled, string finalAnswer, object? sourcesPayload)> TryHandleDeterministicShortcutAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string displayUserMessage,
        string effectiveUserMessage,
        string interactionLanguage,
        DocumentRefResolver.AnalysisResult docResolution,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress,
        Stopwatch swTotalPipeline)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return (false, string.Empty, null);
    }
}
