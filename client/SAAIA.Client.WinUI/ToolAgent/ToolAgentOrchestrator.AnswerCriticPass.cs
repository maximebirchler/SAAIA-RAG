using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private bool ShouldRunCriticPass(RouterPlan plan, ToolResults toolResults, bool useGeneralChatPrompt, string userMessage)
    {
        _lastCriticEligible = false;
        _lastCriticSkipReason = null;

        if (useGeneralChatPrompt)
        {
            _lastCriticStatus = "skipped";
            _lastCriticSkipReason = "general_chat";
            return false;
        }

        var strict = string.Equals(plan.Mode, "strict", StringComparison.OrdinalIgnoreCase)
            || string.Equals(AppSettings.NormalizeActiveMode(_settings?.ActiveMode), "strict", StringComparison.OrdinalIgnoreCase);
        var broadSourceBackedSynthesis = ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, userMessage)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, userMessage)
            || ShouldRouteSourceBackedAnswerThroughWriter(toolResults, userMessage, plan.Language)
            || ShouldRequireWriterForBroadDocumentaryFinal(toolResults, userMessage, plan.Language);
        if (!strict && !broadSourceBackedSynthesis)
        {
            _lastCriticStatus = "skipped";
            _lastCriticSkipReason = "mode_not_strict_or_broad";
            return false;
        }

        var eligible = toolResults.Items.Any(x => string.IsNullOrWhiteSpace(x.Error) && IsGroundedToolForCritic(x.ToolName));
        _lastCriticEligible = eligible;
        if (!eligible)
        {
            _lastCriticStatus = "skipped";
            _lastCriticSkipReason = "no_grounded_tools";
            return false;
        }

        return true;
    }

    private async Task<string> RunCriticPassAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        RouterPlan plan,
        ToolResults toolResults,
        string draftAnswer,
        CancellationToken ct)
    {
        var system = PromptCatalog.BuildCriticSystemPrompt(plan.Language);
        var user = $@"
CHAT_TAIL:
{SerializeTail(chatHistory, maxTurns: 8)}

USER_MESSAGE:
{userMessage}

DRAFT_ANSWER:
{draftAnswer}

TOOL_RESULTS (json):
{SerializeToolResults(toolResults)}
";

        _lastCriticMs = 0;
        _lastCriticStatus = "skipped";
        _lastCriticWarning = null;
        _lastCriticRevisedAnswer = false;

        var swCritic = Stopwatch.StartNew();
        try
        {
            EmitRagTrace(
                "writer.critic.start",
                ("intent", plan.Intent),
                ("prompt_chars", system.Length + user.Length),
                ("draft_chars", draftAnswer?.Length ?? 0),
                ("tool_items", toolResults.Items.Count));
            var raw = await _llm.CompleteAsync(new[]
            {
                ("system", system),
                ("user", user)
            }, forceJson: true, ct).ConfigureAwait(false);
            swCritic.Stop();
            _lastCriticMs = swCritic.ElapsedMilliseconds;

            if (TryParseCriticEnvelope(raw, out var status, out var revised, out var warning))
            {
                _lastCriticStatus = string.IsNullOrWhiteSpace(status) ? "ok" : status!.Trim().ToLowerInvariant();
                _lastCriticWarning = string.IsNullOrWhiteSpace(warning) ? null : warning!.Trim();

                if (string.Equals(status, "revise", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(revised))
                {
                    _lastCriticRevisedAnswer = true;
                    EmitRagTrace(
                        "writer.critic.end",
                        ("intent", plan.Intent),
                        ("status", _lastCriticStatus),
                        ("revised", true),
                        ("raw_chars", raw?.Length ?? 0),
                        ("ms", _lastCriticMs));
                    return revised.Trim();
                }

                EmitRagTrace(
                    "writer.critic.end",
                    ("intent", plan.Intent),
                    ("status", _lastCriticStatus),
                    ("revised", false),
                    ("raw_chars", raw?.Length ?? 0),
                    ("ms", _lastCriticMs));
                return draftAnswer ?? string.Empty;
            }

            _lastCriticStatus = "invalid";
            EmitRagTrace(
                "writer.critic.end",
                ("intent", plan.Intent),
                ("status", _lastCriticStatus),
                ("revised", false),
                ("raw_chars", raw?.Length ?? 0),
                ("ms", _lastCriticMs));
        }
        catch (Exception ex)
        {
            if (swCritic.IsRunning)
                swCritic.Stop();
            _lastCriticMs = swCritic.ElapsedMilliseconds;
            _lastCriticStatus = "error";
            EmitRagTrace(
                "writer.critic.failed",
                ("intent", plan.Intent),
                ("error", TruncateForPrompt(ex.Message, 220)),
                ("ms", _lastCriticMs));
        }

        return draftAnswer ?? string.Empty;
    }
}