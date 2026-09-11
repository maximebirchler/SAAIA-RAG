using System.Collections.Generic;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private void ResetLastTurnDiagnostics()
    {
        _lastRouterMs = 0;
        _lastToolsMs = 0;
        _lastWriterMs = 0;
        _lastTotalMs = 0;
        _lastCriticMs = 0;
        _lastCriticStatus = null;
        _lastCriticWarning = null;
        _lastCriticRevisedAnswer = false;
        _lastCriticEligible = false;
        _lastCriticSkipReason = null;
        _lastUsedGeneralChatPrompt = false;
        _lastUsedInventoryRendered = false;
        _lastUsedSummaryFlow = false;
        _lastResponseFormat = "auto";
        _lastEffectiveMode = "auto";
        _lastWriterToolNames = new List<string>();
        _lastToolDurations = new List<(string tool, long durationMs, bool ok)>();
        _lastAnswerSource = "unknown";
        _lastAdvancedAnalysisHandoff = null;
        ResetRagTraceTurn();
        _mem.Execution.LastRagEvidenceExploration = new();
        _mem.Execution.LastRagInferredCategoryScope = null;
        _mem.Execution.LastRagInferredCategoryReason = null;
    }

}
