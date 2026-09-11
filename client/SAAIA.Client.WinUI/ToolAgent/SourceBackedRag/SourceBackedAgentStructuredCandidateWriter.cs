using System.Text;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    internal sealed record StructuredCandidateWriterExecution(
        SourceBackedAgentCompletion Completion,
        bool ProtocolValid,
        string? FailureReason,
        string RawOutput,
        int Attempts,
        int LlmCallCount,
        bool RequiresMoreEvidence = false,
        string? ResearchNeed = null,
        int RequestedAdditionalCandidateCount = 0);

    internal async Task<StructuredCandidateWriterExecution>
        CompleteStructuredCandidateWriterAsync(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            IReadOnlyList<string> candidateEvidenceIds,
            string rowHeader,
            IReadOnlyList<string> rowLabels,
            IReadOnlyList<string> columnLabels,
            IReadOnlyDictionary<string, string> columnRoles,
            string? repairInstruction,
            CancellationToken ct)
    {
        var completions = new List<SourceBackedAgentCompletion>(3);
        var cellCount = rowLabels.Count * columnLabels.Count;
        var allCellIndexes = Enumerable.Range(0, cellCount).ToArray();
        var maximumOutputTokens = Math.Min(
            _options.MaximumOutputTokens,
            Math.Clamp(160 + cellCount * 24, 384, 768));
        var completion = await _llm.CompleteAsync(
                BuildStructuredCandidateWriterMessages(
                    intake,
                    bundle,
                    candidateEvidenceIds,
                    rowHeader,
                    rowLabels,
                    columnLabels,
                    columnRoles,
                    repairInstruction),
                Array.Empty<SourceBackedAgentToolDefinition>(),
                maximumOutputTokens,
                ct,
                temperatureOverride: 0,
                requireToolCall: false)
            .ConfigureAwait(false);
        completions.Add(completion);

        string? malformedEvidenceRequestFailure = null;
        if (LooksLikeStructuredCandidateEvidenceRequest(completion.Content))
        {
            if (TryReadStructuredCandidateEvidenceRequest(
                    completion.Content,
                    allCellIndexes,
                    out var researchNeed,
                    out var requestedAdditionalCandidateCount,
                    out var requestFailureReason))
            {
                return StructuredCandidateWriterEvidenceRequest(
                    completions,
                    completion.Content,
                    researchNeed,
                    requestedAdditionalCandidateCount);
            }

            // A small local model can append the protocol example after an otherwise
            // usable table. Keep the malformed marker as diagnostic evidence, but let
            // the mechanical table normalizer extract or repair the actual grid.
            malformedEvidenceRequestFailure = requestFailureReason;
        }

        var failureReason = malformedEvidenceRequestFailure;
        if (malformedEvidenceRequestFailure is null
            && TryValidateStructuredCandidateWriterCompletion(
                completion,
                bundle,
                candidateEvidenceIds,
                cellCount,
                out _,
                out failureReason))
        {
            return SuccessfulStructuredCandidateWriterExecution(
                completions,
                completion.Content,
                completion.Content,
                "structured_candidate_table");
        }

        if (!TryPrepareStructuredCandidateTableRepair(
                completion.Content,
                bundle,
                candidateEvidenceIds,
                rowLabels,
                columnLabels,
                out var repair))
        {
            return FailedStructuredCandidateWriterExecution(
                completions,
                completion.Content,
                malformedEvidenceRequestFailure ?? failureReason);
        }

        if (repair.ConflictCellIndexes.Count == 0)
        {
            var normalizedAssignments =
                RenderStructuredCandidateAssignments(repair.Assignments);
            if (TryBuildStructuredCandidateAssignmentTable(
                    normalizedAssignments,
                    bundle,
                    candidateEvidenceIds,
                    rowHeader,
                    rowLabels,
                    columnLabels,
                    out var normalizedTable,
                    out _,
                    out failureReason))
            {
                return SuccessfulStructuredCandidateWriterExecution(
                    completions,
                    normalizedTable,
                    completion.Content,
                    "structured_candidate_table_mechanically_normalized");
            }

            return FailedStructuredCandidateWriterExecution(
                completions,
                completion.Content,
                failureReason);
        }

        string? previousInvalidOutput = null;
        string? previousFailureReason = null;
        for (var repairAttempt = 1; repairAttempt <= 2; repairAttempt++)
        {
            var repairCompletion = await _llm.CompleteAsync(
                    BuildStructuredCandidateConflictRepairMessages(
                        intake,
                        bundle,
                        repair,
                        rowLabels,
                        columnLabels,
                        columnRoles,
                        previousInvalidOutput,
                        previousFailureReason),
                    Array.Empty<SourceBackedAgentToolDefinition>(),
                    Math.Min(
                        _options.MaximumOutputTokens,
                        Math.Clamp(
                            repair.ConflictCellIndexes.Count * 8 + 32,
                            48,
                            224)),
                    ct,
                    temperatureOverride: 0,
                    requireToolCall: false)
                .ConfigureAwait(false);
            completions.Add(repairCompletion);
            if (LooksLikeStructuredCandidateEvidenceRequest(
                    repairCompletion.Content))
            {
                if (TryReadStructuredCandidateEvidenceRequest(
                        repairCompletion.Content,
                        repair.ConflictCellIndexes,
                        out var researchNeed,
                        out var requestedAdditionalCandidateCount,
                        out failureReason))
                {
                    return StructuredCandidateWriterEvidenceRequest(
                        completions,
                        repairCompletion.Content,
                        researchNeed,
                        requestedAdditionalCandidateCount);
                }

                previousInvalidOutput = repairCompletion.Content;
                previousFailureReason = failureReason;
                continue;
            }
            if (TryApplyStructuredCandidateConflictRepair(
                    repair,
                    repairCompletion.Content,
                    out var repairedAssignments,
                    out failureReason)
                && TryBuildStructuredCandidateAssignmentTable(
                    repairedAssignments,
                    bundle,
                    candidateEvidenceIds,
                    rowHeader,
                    rowLabels,
                    columnLabels,
                    out var repairedTable,
                    out _,
                    out failureReason))
            {
                return SuccessfulStructuredCandidateWriterExecution(
                    completions,
                    repairedTable,
                    repairCompletion.Content,
                    "structured_candidate_cells_repaired");
            }

            previousInvalidOutput = repairCompletion.Content;
            previousFailureReason = failureReason;
        }

        return FailedStructuredCandidateWriterExecution(
            completions,
            previousInvalidOutput ?? completion.Content,
            previousFailureReason ?? failureReason);
    }

    private static StructuredCandidateWriterExecution
        SuccessfulStructuredCandidateWriterExecution(
            IReadOnlyList<SourceBackedAgentCompletion> completions,
            string content,
            string rawOutput,
            string finishReason)
        => new(
            AggregateStructuredCandidateWriterCompletions(
                completions,
                content,
                finishReason),
            true,
            null,
            rawOutput,
            completions.Count,
            completions.Count);

    private static StructuredCandidateWriterExecution
        FailedStructuredCandidateWriterExecution(
            IReadOnlyList<SourceBackedAgentCompletion> completions,
            string rawOutput,
            string? failureReason)
        => new(
            AggregateStructuredCandidateWriterCompletions(
                completions,
                rawOutput,
                "protocol_error"),
            false,
            failureReason
            ?? "structured_candidate_writer_table_protocol_invalid",
            rawOutput,
            completions.Count,
            completions.Count);

    private static StructuredCandidateWriterExecution
        StructuredCandidateWriterEvidenceRequest(
            IReadOnlyList<SourceBackedAgentCompletion> completions,
            string rawOutput,
            string researchNeed,
            int requestedAdditionalCandidateCount)
        => new(
            AggregateStructuredCandidateWriterCompletions(
                completions,
                rawOutput,
                "structured_candidate_more_evidence_requested"),
            false,
            null,
            rawOutput,
            completions.Count,
            completions.Count,
            true,
            researchNeed,
            requestedAdditionalCandidateCount);

    private static SourceBackedAgentCompletion
        AggregateStructuredCandidateWriterCompletions(
            IReadOnlyList<SourceBackedAgentCompletion> completions,
            string content,
            string finishReason)
        => new(
            content,
            Array.Empty<SourceBackedAgentToolCall>(),
            finishReason,
            SumNullable(completions.Select(static item => item.PromptTokens)),
            SumNullable(completions.Select(static item => item.CompletionTokens)),
            SumNullable(completions.Select(static item => item.ServerCacheTokens)),
            SumNullable(completions.Select(
                static item => item.ServerPromptTokensEvaluated)),
            SumNullable(completions.Select(
                static item => item.ServerPromptMilliseconds)),
            SumNullable(completions.Select(static item => item.ServerPredictedTokens)),
            SumNullable(completions.Select(
                static item => item.ServerPredictedMilliseconds)));

    private void TraceStructuredCandidateWriterCompletion(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        bool protocolValid,
        string failureReason,
        int rowCount,
        int columnCount,
        IReadOnlyList<string> selectedEvidenceIds,
        SourceBackedAgentCompletion completion,
        StructuredCandidateWriterExecution? execution)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.Writer,
            "source_backed_agent_v2.structured_candidate_writer.completed",
            ("turn", turn),
            ("protocol_valid", protocolValid),
            ("failure_reason", failureReason),
            ("rows", rowCount),
            ("columns", columnCount),
            ("selected_evidence_ids", selectedEvidenceIds),
            ("content_characters", completion.Content.Length),
            ("assignment_attempts", execution?.Attempts),
            ("assignment_llm_calls", execution?.LlmCallCount),
            ("assignment_raw_output", execution?.RawOutput),
            ("prompt_tokens", completion.PromptTokens),
            ("completion_tokens", completion.CompletionTokens),
            ("server_predicted_ms", completion.ServerPredictedMilliseconds),
            ("decision_source", "llm_orchestrator_writer")));

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildStructuredCandidateWriterMessages(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            IReadOnlyList<string> candidateEvidenceIds,
            string rowHeader,
            IReadOnlyList<string> rowLabels,
            IReadOnlyList<string> columnLabels,
            IReadOnlyDictionary<string, string> columnRoles,
            string? repairInstruction)
    {
        var context = new StringBuilder()
            .AppendLine("DEMANDE UTILISATEUR:")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 800))
            .AppendLine("GRILLE A PRODUIRE:")
            .Append("- en-tete des lignes: ")
            .AppendLine(TrimPromptValue(rowHeader, 60))
            .Append("- lignes dans l'ordre: ")
            .AppendLine(string.Join(" | ", rowLabels))
            .AppendLine("- colonnes et frontieres semantiques:");
        foreach (var columnLabel in columnLabels)
        {
            context.Append("  - ")
                .Append(TrimPromptValue(columnLabel, 60))
                .Append(": ")
                .AppendLine(TrimPromptValue(
                    columnRoles.TryGetValue(columnLabel, out var role)
                        ? role
                        : columnLabel,
                    280));
        }
        var candidateItems = candidateEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(bundle.ById.ContainsKey)
            .Select(evidenceId => bundle.ById[evidenceId])
            .ToArray();
        var duplicateLabelGroups = candidateItems
            .GroupBy(GetEvidenceDisplayValue, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .ToArray();
        if (duplicateLabelGroups.Length > 0)
        {
            context.AppendLine(
                "GROUPES DE LIBELLES TEXTUELLEMENT IDENTIQUES: utilise au plus un "
                + "EvidenceId de chaque groupe:");
            foreach (var group in duplicateLabelGroups)
            {
                context.Append("- ")
                    .Append(TrimPromptValue(group.Key, 100))
                    .Append(" => ")
                    .AppendLine(string.Join(", ", group.Select(static item => item.EvidenceId)));
            }
        }
        context.AppendLine("CANDIDATS SOURCES, SANS ORDRE DE PREFERENCE:");
        foreach (var item in candidateItems)
        {
            context.Append("- [")
                .Append(item.EvidenceId)
                .Append("] ")
                .Append(TrimPromptValue(GetEvidenceDisplayValue(item), 120))
                .Append(" | ")
                .Append(TrimPromptValue(item.Excerpt, 110));
            if (TryReadCompatibleColumnLabels(
                    item,
                    out var compatibleColumnLabels))
            {
                context.Append(" | colonnes compatibles decidees par le LLM: ")
                    .Append(compatibleColumnLabels.Count == 0
                        ? "AUCUNE"
                        : string.Join(", ", compatibleColumnLabels));
            }
            context.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(repairInstruction))
        {
            context.AppendLine("REVUE DU TABLEAU PRECEDENT A APPLIQUER:");
            context.AppendLine(TrimPromptBlock(repairInstruction, 3600));
        }
        context.AppendLine(
            "PRODUIS DIRECTEMENT LE TABLEAU MARKDOWN FINAL. Chaque cellule contient "
            + "exactement un nom candidat suivi immediatement de son unique citation [E#]. "
            + "Utilise exactement un candidat distinct par cellule et ne cite aucun candidat "
            + "non utilise. N'ajoute aucun paragraphe avant ou apres le tableau.")
            .AppendLine(
                "SI ET SEULEMENT SI les candidats affiches ne permettent pas une valeur "
                + "distincte et semantiquement valide pour certaines cellules, ne force "
                + "pas le tableau. Reponds exactement sur deux lignes:")
            .AppendLine("BESOIN_PREUVES=CXX,CYY")
            .AppendLine("RAISON=besoin documentaire concis et actionnable");

        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es l'orchestrateur semantique et le redacteur final. Compose librement "
                + "la meilleure grille professionnelle a partir des seules preuves "
                + "affichees. Evalue chaque nom selon la frontiere de sa colonne; une preuve "
                + "valide n'est pas automatiquement adaptee a toutes les colonnes. Respecte "
                + "les compatibilites deja decidees par le LLM. Ne suis pas l'ordre des "
                + "candidats et ne remplis jamais une case avec une valeur "
                + "incompatible pour atteindre le quota. N'invente, ne renomme et ne "
                + "duplique aucune valeur. Les citations servent de contrat mecanique, pas "
                + "de puzzle de selection separe. Si le corpus visible est insuffisant pour "
                + "une affectation honnete, utilise le protocole BESOIN_PREUVES au lieu de "
                + "forcer des doublons ou des valeurs incompatibles."),
            SourceBackedAgentMessage.User(context.ToString().Trim())
        };
    }

    private bool ShouldUseStructuredCandidateWriter(
        bool semanticSelectionDecisionTurn,
        SemanticLayoutDimensions? dimensions,
        IReadOnlyList<string> rowLabels,
        IReadOnlyList<string> columnLabels,
        int requiredEvidenceCount)
        => semanticSelectionDecisionTurn
           && dimensions is not null
           && rowLabels.Count > 0
           && columnLabels.Count > 0
           && requiredEvidenceCount
           > _options.MaximumFlatStructuredSelectionItems;
}
