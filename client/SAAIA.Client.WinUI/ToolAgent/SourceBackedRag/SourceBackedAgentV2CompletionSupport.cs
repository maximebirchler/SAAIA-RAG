namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static int? ReadRequiredAtomicEvidenceCount(string semanticPlan)
    {
        foreach (var line in semanticPlan.Split(
                     new[] { '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(
                    "PREUVES_ATOMIQUES:",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var number = new string(
                line
                    .SkipWhile(static character => !char.IsDigit(character))
                    .TakeWhile(char.IsDigit)
                    .ToArray());
            if (int.TryParse(
                    number,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed)
                && parsed is > 0 and <= 80)
            {
                return parsed;
            }
        }

        return null;
    }

    private static string ReadAtomicEvidenceMode(string semanticPlan)
    {
        foreach (var line in semanticPlan.Split(
                     new[] { '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            const string prefix = "MODE_PREUVES_ATOMIQUES:";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var mode = line[prefix.Length..].Trim().ToLowerInvariant();
            if (mode is "named_item" or "content_claim")
                return mode;
        }

        return "named_item";
    }

    private static int CountObservedCitableSources(
        EvidenceBundle bundle,
        IEnumerable<string> observedEvidenceIds)
        => observedEvidenceIds
            .Select(id => bundle.ById.TryGetValue(id, out var item) ? item : null)
            .Where(static item => item is not null
                                  && !item.RiskFlags.Contains(
                                      "orientation_only",
                                      StringComparer.OrdinalIgnoreCase))
            .Cast<EvidenceItem>()
            .Select(static item => item.VisibleSourceKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static int CountObservedSelectableEvidenceUnits(
        EvidenceBundle bundle,
        IEnumerable<string> observedEvidenceIds,
        string atomicEvidenceMode)
    {
        var items = observedEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => bundle.ById.TryGetValue(id, out var item) ? item : null)
            .Where(static item => item is not null
                                  && IsMechanicallyCitableCandidate(item)
                                  && HasRenderableEvidenceValue(item))
            .Cast<EvidenceItem>();
        return string.Equals(
                atomicEvidenceMode,
                "content_claim",
                StringComparison.OrdinalIgnoreCase)
            ? items.Select(static item => item.EvidenceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()
            : items.Select(static item => item.VisibleSourceKey)
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
    }

    private async Task<SourceBackedAgentCompletion> CompleteDedicatedWriterAsync(
        IReadOnlyCollection<SourceBackedAgentMessage> messages,
        string? actionDecision,
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        IReadOnlyList<string>? selectedEvidenceIds,
        string atomicEvidenceMode,
        CancellationToken ct,
        string? semanticJudgeAssessment = null,
        IReadOnlyList<string>? semanticJudgeLeadEvidenceIds = null)
    {
        using var terminalWriterBudget = SourceBackedLlmCumulativeBudgetContext.PushTerminalCall();
        if (_options.StructuredFlatWriterEnabled
            && selectedEvidenceIds is { Count: > 0 }
            && _llm is ISourceBackedAgentStructuredLlmClient)
        {
            var execution = await CompleteStructuredFlatWriterAsync(
                    intake,
                    bundle,
                    selectedEvidenceIds,
                    atomicEvidenceMode,
                    IsEvidenceSelectionDecision(actionDecision)
                        ? null
                        : actionDecision,
                    ct,
                    semanticJudgeAssessment,
                    semanticJudgeLeadEvidenceIds)
                .ConfigureAwait(false);
            return execution.Completion;
        }

        var writerMessages = selectedEvidenceIds is { Count: > 0 }
            ? BuildSelectedEvidenceWriterMessages(
                intake,
                bundle,
                selectedEvidenceIds,
                atomicEvidenceMode,
                IsEvidenceSelectionDecision(actionDecision)
                    ? null
                    : actionDecision,
                semanticJudgeAssessment,
                semanticJudgeLeadEvidenceIds)
            : messages
                .Select(static (message, index) =>
                    index == 0
                    && string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase)
                        ? SourceBackedAgentMessage.System(BuildDedicatedWriterSystemPrompt())
                        : message)
                .Concat(new[]
                {
                    SourceBackedAgentMessage.User(BuildDedicatedWriterMessage(actionDecision))
                })
                .ToArray();
        return await _llm.CompleteAsync(
                writerMessages,
                Array.Empty<SourceBackedAgentToolDefinition>(),
                _options.MaximumOutputTokens,
                ct)
            .ConfigureAwait(false);
    }

    private static bool IsEvidenceSelectionDecision(string? actionDecision)
        => actionDecision?.StartsWith(
            "SELECTION_LLM_AUTORISEE:",
            StringComparison.OrdinalIgnoreCase) == true;

    private void AddWriterTrace(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        SourceBackedAgentCompletion completion,
        bool directRevision)
    {
        var citedEvidenceIds = SourceContractVerifier.ExtractEvidenceIds(
            completion.Content ?? string.Empty);
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Writer,
            "source_backed_agent_v2.writer.completed",
            ("turn", turn),
            ("direct_revision", directRevision),
            ("finish_reason", completion.FinishReason),
            ("content_characters", completion.Content?.Length ?? 0),
            ("answer_preview", TrimPromptValue(
                completion.Content,
                600)),
            ("content_evidence_ids", citedEvidenceIds.Count),
            ("cited_evidence_ids", citedEvidenceIds),
            ("prompt_tokens", completion.PromptTokens),
            ("completion_tokens", completion.CompletionTokens),
            ("server_cache_tokens", completion.ServerCacheTokens),
            ("server_prompt_evaluated_tokens",
                completion.ServerPromptTokensEvaluated),
            ("server_prompt_ms", completion.ServerPromptMilliseconds),
            ("server_predicted_tokens", completion.ServerPredictedTokens),
            ("server_predicted_ms", completion.ServerPredictedMilliseconds),
            ("protocol_error", completion.ProtocolError),
            ("protocol_raw_output", TrimPromptValue(
                completion.ProtocolRawOutput,
                900))));
    }
}
