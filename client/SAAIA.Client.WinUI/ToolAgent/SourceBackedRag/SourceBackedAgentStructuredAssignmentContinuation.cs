using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record StructuredAssignmentContinuationDecision(
        bool ProtocolValid,
        bool CollectMoreEvidence,
        int MissingCellCount,
        string Reason,
        int Attempts,
        int? PromptTokens,
        int? CompletionTokens,
        string RawOutput);

    private sealed record StructuredAssignmentContinuationResolution(
        StructuredAssignmentCollectionDecision Decision,
        int TraceSequence);

    private async Task<StructuredAssignmentContinuationResolution>
        ResolveRepeatedStructuredAssignmentContinuationAsync(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            WriterDraft draft,
            SemanticReview review,
            IReadOnlyList<string> candidateEvidenceIds,
            IReadOnlyDictionary<int, HashSet<string>> rejectedEvidenceIdsByCell,
            bool freshEvidenceLifecycleAvailable,
            int observedCandidateCount,
            StructuredAssignmentCollectionDecision currentDecision,
            ICollection<SourceBackedTraceEvent> traces,
            string traceId,
            int traceSequence,
            int turn,
            CancellationToken ct)
    {
        if (!freshEvidenceLifecycleAvailable
            || currentDecision.RequiresCollection
            || currentDecision.RepeatedRejectedCellCount == 0
            || currentDecision.RejectedCellCount == 0)
        {
            return new(currentDecision, traceSequence);
        }

        var continuation = await DecideRepeatedStructuredAssignmentContinuationAsync(
                intake,
                bundle,
                draft,
                review,
                candidateEvidenceIds,
                rejectedEvidenceIdsByCell,
                ct)
            .ConfigureAwait(false);
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.AnswerAdequacyJudge,
            "source_backed_agent_v2.structured_assignment_revision.continuation_decided",
            ("turn", turn),
            ("protocol_valid", continuation.ProtocolValid),
            ("action", continuation.CollectMoreEvidence
                ? "collect_more_evidence"
                : "revise_locally"),
            ("missing_cells", continuation.MissingCellCount),
            ("reason", continuation.Reason),
            ("attempts", continuation.Attempts),
            ("prompt_tokens", continuation.PromptTokens),
            ("completion_tokens", continuation.CompletionTokens),
            ("raw_output", TrimPromptBlock(continuation.RawOutput, 600)),
            ("decision_source", "llm_orchestrator")));
        if (!continuation.ProtocolValid)
            return new(currentDecision, traceSequence);

        if (!continuation.CollectMoreEvidence)
        {
            return new(
                currentDecision with
                {
                    DecisionSource =
                        "llm_repeated_rejections_have_local_alternatives",
                    ResearchNeed = continuation.Reason
                },
                traceSequence);
        }

        var target = Math.Min(
            _options.MaximumWorkingEvidenceItems,
            observedCandidateCount + continuation.MissingCellCount);
        if (target <= observedCandidateCount)
            return new(currentDecision, traceSequence);

        return new(
            currentDecision with
            {
                MissingAlternativeCount = continuation.MissingCellCount,
                CandidatePoolTargetCount = target,
                RequiresCollection = true,
                DecisionSource =
                    "llm_repeated_rejections_require_fresh_evidence",
                ResearchNeed = continuation.Reason
            },
            traceSequence);
    }

    private async Task<StructuredAssignmentContinuationDecision>
        DecideRepeatedStructuredAssignmentContinuationAsync(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            WriterDraft draft,
            SemanticReview review,
            IReadOnlyList<string> candidateEvidenceIds,
            IReadOnlyDictionary<int, HashSet<string>> rejectedEvidenceIdsByCell,
            CancellationToken ct)
    {
        var claims = SourceContractVerifier
            .ExtractRequestedStructuredCellClaims(draft.Answer, intake)
            .ToArray();
        var rejectedIndexes = GetStructuredAssignmentRejectedCellIndexes(review)?
            .Where(index => index >= 0 && index < claims.Length)
            .OrderBy(static index => index)
            .ToArray()
            ?? Array.Empty<int>();
        if (rejectedIndexes.Length == 0)
            return FailedStructuredAssignmentContinuation();

        var rejectedIndexSet = rejectedIndexes.ToHashSet();
        var lockedEvidenceIds = claims
            .Where((_, index) => !rejectedIndexSet.Contains(index))
            .SelectMany(static claim => claim.EvidenceIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableEvidenceIds = candidateEvidenceIds
            .Where(id => !lockedEvidenceIds.Contains(id) && bundle.ById.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (availableEvidenceIds.Length == 0)
            return FailedStructuredAssignmentContinuation();

        var messages = BuildStructuredAssignmentContinuationMessages(
            intake,
            bundle,
            claims,
            rejectedIndexes,
            availableEvidenceIds,
            rejectedEvidenceIdsByCell);
        var completion = _llm is ISourceBackedAgentStructuredLlmClient structuredLlm
            ? await structuredLlm.CompleteStructuredAsync(
                    messages,
                    BuildStructuredAssignmentContinuationContract(),
                    32,
                    ct,
                    temperatureOverride: 0)
                .ConfigureAwait(false)
            : await _llm.CompleteAsync(
                    messages,
                    Array.Empty<SourceBackedAgentToolDefinition>(),
                    32,
                    ct,
                    temperatureOverride: 0,
                    requireToolCall: false)
                .ConfigureAwait(false);
        if (TryReadStructuredAssignmentContinuation(
                completion,
                rejectedIndexes.Length,
                out var collectMoreEvidence,
                out var missingCellCount))
        {
            return new StructuredAssignmentContinuationDecision(
                true,
                collectMoreEvidence,
                missingCellCount,
                collectMoreEvidence
                    ? "llm_decided_fresh_evidence_is_required"
                    : "llm_decided_local_alternatives_are_sufficient",
                1,
                completion.PromptTokens,
                completion.CompletionTokens,
                completion.Content);
        }

        return FailedStructuredAssignmentContinuation(
            1,
            completion.PromptTokens,
            completion.CompletionTokens,
            completion.Content);
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildStructuredAssignmentContinuationMessages(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            IReadOnlyList<SourceBackedStructuredCellClaim> claims,
            IReadOnlyList<int> rejectedIndexes,
            IReadOnlyList<string> availableEvidenceIds,
            IReadOnlyDictionary<int, HashSet<string>> rejectedEvidenceIdsByCell)
    {
        var prompt = new StringBuilder()
            .AppendLine("DEMANDE UTILISATEUR:")
            .AppendLine(TrimPromptValue(intake.UserQuestion, 300))
            .AppendLine("FRONTIERES DES COLONNES CONCERNEES:");
        foreach (var columnHeader in rejectedIndexes
                     .Select(index => claims[index].ColumnHeader)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var role = intake.CanonicalColumnSemanticRoles is not null
                       && intake.CanonicalColumnSemanticRoles.TryGetValue(
                           columnHeader,
                           out var semanticRole)
                ? semanticRole
                : columnHeader;
            prompt.Append("- ").Append(TrimPromptValue(columnHeader, 60))
                .Append(": ")
                .AppendLine(TrimPromptValue(role, 220));
        }
        prompt.AppendLine("CELLULES ENCORE REFUSEES APRES REVISION:");
        foreach (var index in rejectedIndexes)
        {
            var claim = claims[index];
            prompt.Append("- ").Append(claim.ClaimRef)
                .Append(" | ligne: ").Append(TrimPromptValue(claim.RowLabel, 60))
                .Append(" | colonne: ").Append(TrimPromptValue(claim.ColumnHeader, 60))
                .Append(" | interdits deja essayes: ")
                .AppendLine(rejectedEvidenceIdsByCell.TryGetValue(index, out var rejected)
                    ? string.Join(", ", rejected.OrderBy(
                        static id => id,
                        StringComparer.OrdinalIgnoreCase))
                    : string.Join(", ", claim.EvidenceIds));
        }
        prompt.AppendLine("CANDIDATS ENCORE AFFECTABLES:");
        foreach (var evidenceId in availableEvidenceIds)
        {
            var evidence = bundle.ById[evidenceId];
            prompt.Append("- [").Append(evidenceId).Append("] ")
                .Append(TrimPromptValue(GetEvidenceDisplayValue(evidence), 120))
                .Append(" | ")
                .AppendLine(TrimPromptValue(evidence.Excerpt, 90));
        }
        prompt.AppendLine(
            "Choisis revise_local seulement si chaque cellule refusee possede encore "
            + "au moins un candidat clairement compatible qui n'est pas deja interdit "
            + "pour elle. Choisis collect_more si au moins une cellule n'a plus de vraie "
            + "alternative. Retourne seulement l'action demandee; l'etape suivante "
            + "conservera les cellules, frontieres et refus affiches.");
        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es l'orchestrateur semantique qui decide s'il faut continuer une "
                + "revision locale ou reprendre la recherche documentaire. Ne te base "
                + "pas sur le seul nombre de candidats: examine leur adequation reelle "
                + "aux cellules et les essais deja refuses. Ne choisis encore aucune "
                + "affectation et ne choisis aucun outil. Retourne uniquement l'objet "
                + "JSON conforme."),
            SourceBackedAgentMessage.User(prompt.ToString().Trim())
        };
    }

    private static LlmStructuredOutputContract
        BuildStructuredAssignmentContinuationContract()
        => new(
            "source_backed_structured_assignment_continuation_v3",
            JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["action"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["enum"] = new[] { "revise_local", "collect_more" }
                        }
                    },
                    ["required"] = new[] { "action" },
                    ["additionalProperties"] = false
                },
                ClientJson.CamelCase));

    private static bool TryReadStructuredAssignmentContinuation(
        SourceBackedAgentCompletion completion,
        int rejectedCellCount,
        out bool collectMoreEvidence,
        out int missingCellCount)
    {
        collectMoreEvidence = false;
        missingCellCount = 0;
        if (completion.ToolCalls.Count > 0 || string.IsNullOrWhiteSpace(completion.Content))
            return false;
        try
        {
            using var document = JsonDocument.Parse(completion.Content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 1
                || !TryGetPropertyIgnoreCase(root, "action", out var actionValue)
                || actionValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var action = actionValue.GetString();
            collectMoreEvidence = string.Equals(
                action,
                "collect_more",
                StringComparison.OrdinalIgnoreCase);
            if (!collectMoreEvidence
                && !string.Equals(
                    action,
                    "revise_local",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            missingCellCount = collectMoreEvidence ? rejectedCellCount : 0;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static StructuredAssignmentContinuationDecision
        FailedStructuredAssignmentContinuation(
            int attempts = 0,
            int? promptTokens = null,
            int? completionTokens = null,
            string rawOutput = "")
        => new(
            false,
            false,
            0,
            "continuation_protocol_invalid",
            attempts,
            promptTokens,
            completionTokens,
            rawOutput);
}
