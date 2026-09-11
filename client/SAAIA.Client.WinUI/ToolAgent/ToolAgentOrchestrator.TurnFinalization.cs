using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private (string finalAnswer, object? sourcesPayload) FinalizeAndReturn(
        Stopwatch swTotalPipeline,
        string rememberedUserMessage,
        string finalAnswer,
        object? sourcesPayload,
        string? routerIntent,
        IEnumerable<string>? toolNames,
        IEnumerable<string>? reasoningTracePublic,
        bool clearPendingClarification = true)
    {
        rememberedUserMessage ??= string.Empty;
        finalAnswer ??= string.Empty;
        var toolNameList = toolNames?.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray()
            ?? Array.Empty<string>();

        if (clearPendingClarification)
            ClearPendingClarification();

        if (ContainsBroadenedSourceSearchOffer(finalAnswer)
            && ShouldOfferBroadenedSourceSearch(rememberedUserMessage))
        {
            RememberPendingClarification(
                "source_backed_broaden_search",
                rememberedUserMessage,
                "broader_source_search_offer",
                DetectMessageLanguage(finalAnswer));
        }

        if (IsInventoryIntent(routerIntent)
            && _mem.LastDeterministicRender is not null
            && string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.SourceUserMessage))
        {
            _mem.LastDeterministicRender.SourceUserMessage = rememberedUserMessage;
        }

        if (sourcesPayload is not null
            && ShouldSuppressVisibleSourcesForInsufficientStructuredPlanningAnswer(finalAnswer, new ToolResults(), rememberedUserMessage, DetectMessageLanguage(finalAnswer)))
        {
            finalAnswer = RemoveTrailingModelEmittedSourceList(finalAnswer).Trim();
            sourcesPayload = null;
            _mem.LastSourcesUsed = new List<ToolMemory.SourceRef>();
        }

        RememberTurnState(rememberedUserMessage, finalAnswer, routerIntent, toolNameList, reasoningTracePublic);
        swTotalPipeline.Stop();
        _lastTotalMs = swTotalPipeline.ElapsedMilliseconds;
        EmitRagTrace(
            "turn.final",
            ("planning", LooksLikeAnyDocumentaryPlanningRequest(rememberedUserMessage)),
            ("intent", routerIntent),
            ("answer_source", _lastAnswerSource),
            ("answer_chars", finalAnswer?.Length ?? 0),
            ("sources_payload", sourcesPayload is null ? "none" : sourcesPayload.GetType().Name),
            ("tool_count", toolNameList.Length),
            ("tools", toolNameList.Take(12).ToArray()),
            ("total_ms", _lastTotalMs));
        ClientLog.Info(
            "ToolAgent turn final: " +
            $"planning={FormatPlanningTraceBool(LooksLikeAnyDocumentaryPlanningRequest(rememberedUserMessage))}|" +
            $"intent={TruncateForPrompt(routerIntent, 80)}|" +
            $"answerSource={TruncateForPrompt(_lastAnswerSource, 120)}|" +
            $"answerChars={finalAnswer?.Length ?? 0}|" +
            $"sourcesPayload={(sourcesPayload is null ? "none" : sourcesPayload.GetType().Name)}|" +
            $"toolCount={toolNameList.Length}|" +
            $"tools={TruncateForPrompt(string.Join(',', toolNameList.Take(12)), 180)}|" +
            $"totalMs={_lastTotalMs}|" +
            $"answerPreview={TruncateForPrompt(finalAnswer, 220)}");
        return (finalAnswer ?? string.Empty, sourcesPayload);
    }
}
