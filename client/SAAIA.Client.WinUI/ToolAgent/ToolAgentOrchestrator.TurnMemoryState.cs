using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private void RememberTurnState(string userMessage, string assistantAnswer, string? routerIntent, IEnumerable<string>? toolNames, IEnumerable<string>? reasoningTracePublic)
    {
        _mem.LastUserMessage = userMessage;
        _mem.LastAssistantAnswer = assistantAnswer;
        _mem.LastRouterIntent = routerIntent;
        _mem.LastAnswerSource = _lastAnswerSource;
        _mem.LastToolNames = toolNames?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        _mem.LastReasoningTracePublic = reasoningTracePublic?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();

        var hasExplicitLanguageSwitch = TryDetectExplicitLanguageSwitch(userMessage, out var requestedLanguage);
        var isOneShotTranslation = string.Equals(routerIntent, "meta.translate_last_answer", StringComparison.OrdinalIgnoreCase)
            || hasExplicitLanguageSwitch;

        var answerLanguage = isOneShotTranslation && !string.IsNullOrWhiteSpace(requestedLanguage)
            ? requestedLanguage
            : DetectMessageLanguage(assistantAnswer);

        if (!string.IsNullOrWhiteSpace(answerLanguage))
        {
            _mem.LastAnswerLanguage = answerLanguage;
            if (!isOneShotTranslation)
                _mem.LastLanguage = answerLanguage;
        }

        if (!isOneShotTranslation)
        {
            var userLanguage = DetectMessageLanguage(userMessage);
            if (string.IsNullOrWhiteSpace(userLanguage))
                userLanguage = "fr";

            _mem.LastUserDetectedLanguage = userLanguage;
        }
    }


}
