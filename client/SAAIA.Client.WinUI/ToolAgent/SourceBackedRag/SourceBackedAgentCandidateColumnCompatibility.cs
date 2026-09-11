using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string CandidateColumnCompatibilityContractName =
        "source_backed_candidate_column_compatibility_v1";

    private sealed record CandidateColumnCompatibilityInput(
        string EvidenceId,
        string DisplayValue);

    private sealed record CandidateColumnCompatibilityResult(
        bool Applied,
        bool ProtocolValid,
        IReadOnlyDictionary<string, IReadOnlyList<string>>
            CompatibleColumnLabelsByEvidenceId,
        IReadOnlyList<SourceBackedAgentCompletion> Completions,
        int ExecutedBatchCount,
        int ProtocolRepairCount,
        int InputBudgetSplitCount,
        long ElapsedMilliseconds,
        string? FailureReason);

    private async Task<CandidateColumnCompatibilityResult>
        CompleteCandidateColumnCompatibilityAsync(
            SourceBackedIntake intake,
            IReadOnlyList<string> semanticRowLabels,
            IReadOnlyList<string> semanticColumnLabels,
            IReadOnlyList<EvidenceItem> candidates,
            IReadOnlyList<CandidateCollectionApproval> approvals,
            CancellationToken ct)
    {
        if (_llm is not ISourceBackedAgentStructuredLlmClient structuredLlm
            || semanticRowLabels.Count == 0
            || semanticColumnLabels.Count <= 1
            || intake.CanonicalColumnSemanticRoles is not { Count: > 0 } roles
            || semanticColumnLabels.Any(label =>
                !roles.TryGetValue(label, out var role)
                || string.IsNullOrWhiteSpace(role)))
        {
            return new CandidateColumnCompatibilityResult(
                Applied: false,
                ProtocolValid: true,
                new Dictionary<string, IReadOnlyList<string>>(
                    StringComparer.OrdinalIgnoreCase),
                Array.Empty<SourceBackedAgentCompletion>(),
                0,
                0,
                0,
                0,
                null);
        }

        var candidateById = candidates.ToDictionary(
            static candidate => candidate.EvidenceId,
            StringComparer.OrdinalIgnoreCase);
        var inputs = approvals
            .Where(approval => candidateById.ContainsKey(approval.EvidenceId))
            .Select(approval => new CandidateColumnCompatibilityInput(
                approval.EvidenceId,
                approval.DisplayValue))
            .DistinctBy(
                static candidate => candidate.EvidenceId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (inputs.Length == 0)
        {
            return new CandidateColumnCompatibilityResult(
                Applied: true,
                ProtocolValid: true,
                new Dictionary<string, IReadOnlyList<string>>(
                    StringComparer.OrdinalIgnoreCase),
                Array.Empty<SourceBackedAgentCompletion>(),
                0,
                0,
                0,
                0,
                null);
        }

        var stopwatch = Stopwatch.StartNew();
        var compatibleColumnsByEvidenceId = inputs.ToDictionary(
            static candidate => candidate.EvidenceId,
            static _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var completions = new List<SourceBackedAgentCompletion>();
        var executedBatchCount = 0;
        var protocolRepairCount = 0;
        var inputBudgetSplitCount = 0;

        foreach (var columnLabel in semanticColumnLabels)
        {
            var pendingBatches = new Queue<IReadOnlyList<
                CandidateColumnCompatibilityInput>>();
            var maximumBatchSize = Math.Max(
                1,
                _options.MaximumSemanticCandidatesPerAuditBatch);
            foreach (var batch in inputs.Chunk(maximumBatchSize))
                pendingBatches.Enqueue(batch);
            while (pendingBatches.Count > 0)
            {
                var batch = pendingBatches.Dequeue();
                var role = roles[columnLabel];
                var messages = BuildCandidateColumnCompatibilityMessages(
                    columnLabel,
                    role,
                    batch,
                    repairInstruction: null);
                var maximumOutputTokens = ResolveCandidateColumnCompatibilityTokens(
                    batch.Count);
                var measuredInputTokens = await CountInputTokensAsync(
                        messages,
                        Array.Empty<SourceBackedAgentToolDefinition>(),
                        requireToolCall: false,
                        ct)
                    .ConfigureAwait(false);
                if (ExceedsContextBudget(measuredInputTokens, maximumOutputTokens)
                    && batch.Count > 1)
                {
                    inputBudgetSplitCount++;
                    var split = batch.Count / 2;
                    pendingBatches.Enqueue(batch.Take(split).ToArray());
                    pendingBatches.Enqueue(batch.Skip(split).ToArray());
                    continue;
                }

                IReadOnlyList<string>? compatibleIds = null;
                string? failureReason = null;
                executedBatchCount++;
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    if (attempt > 1)
                    {
                        protocolRepairCount++;
                        messages = BuildCandidateColumnCompatibilityMessages(
                            columnLabel,
                            role,
                            batch,
                            "The previous JSON violated the contract: "
                            + failureReason
                            + ". Return the complete JSON object now.");
                    }
                    var completion = await structuredLlm.CompleteStructuredAsync(
                            messages,
                            BuildCandidateColumnCompatibilityContract(batch),
                            maximumOutputTokens,
                            ct,
                            temperatureOverride: 0)
                        .ConfigureAwait(false);
                    completions.Add(completion);
                    if (TryReadCandidateColumnCompatibility(
                            completion,
                            batch,
                            out compatibleIds,
                            out failureReason))
                    {
                        break;
                    }
                }

                if (compatibleIds is null)
                {
                    stopwatch.Stop();
                    return new CandidateColumnCompatibilityResult(
                        Applied: true,
                        ProtocolValid: false,
                        new Dictionary<string, IReadOnlyList<string>>(
                            StringComparer.OrdinalIgnoreCase),
                        completions,
                        executedBatchCount,
                        protocolRepairCount,
                        inputBudgetSplitCount,
                        stopwatch.ElapsedMilliseconds,
                        failureReason
                        ?? "candidate_column_compatibility_protocol_invalid");
                }
                foreach (var evidenceId in compatibleIds)
                    compatibleColumnsByEvidenceId[evidenceId].Add(columnLabel);
            }
        }

        stopwatch.Stop();
        var columnOrder = semanticColumnLabels
            .Select((label, index) => (label, index))
            .ToDictionary(
                static item => item.label,
                static item => item.index,
                StringComparer.OrdinalIgnoreCase);
        return new CandidateColumnCompatibilityResult(
            Applied: true,
            ProtocolValid: true,
            compatibleColumnsByEvidenceId.ToDictionary(
                static pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value
                    .OrderBy(label => columnOrder[label])
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase),
            completions,
            executedBatchCount,
            protocolRepairCount,
            inputBudgetSplitCount,
            stopwatch.ElapsedMilliseconds,
            null);
    }

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildCandidateColumnCompatibilityMessages(
            string columnLabel,
            string semanticRole,
            IReadOnlyList<CandidateColumnCompatibilityInput> candidates,
            string? repairInstruction)
    {
        var prompt = new StringBuilder()
            .AppendLine("SAAIA_SOURCE_BACKED_STEP=CandidateColumnCompatibility")
            .Append("TARGET ROLE LABEL: ")
            .AppendLine(TrimPromptValue(columnLabel, 80))
            .Append("TARGET ROLE DEFINITION AUTHORED BY THE LLM: ")
            .AppendLine(TrimPromptValue(semanticRole, 320))
            .AppendLine("CANDIDATES, WITH NO PREFERENCE ORDER:");
        foreach (var candidate in candidates)
        {
            prompt.Append(candidate.EvidenceId)
                .Append(" = ")
                .AppendLine(TrimPromptValue(candidate.DisplayValue, 120));
        }
        prompt.AppendLine(
            "Return every EvidenceId that actually fits this ONE target role. "
            + "Judge meaning independently; never select by position or quota. "
            + "Omit every incompatible candidate.");
        if (!string.IsNullOrWhiteSpace(repairInstruction))
            prompt.AppendLine(repairInstruction);

        return new[]
        {
            SourceBackedAgentMessage.System(
                "You are the semantic compatibility judge for ONE column of a "
                + "structured deliverable. The role definition and candidate evidence "
                + "are authoritative. Return only the required JSON object."),
            SourceBackedAgentMessage.User(prompt.ToString().Trim())
        };
    }

    private static LlmStructuredOutputContract
        BuildCandidateColumnCompatibilityContract(
            IReadOnlyList<CandidateColumnCompatibilityInput> candidates)
        => new(
            CandidateColumnCompatibilityContractName,
            JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["compatibleCandidateIds"] =
                            new Dictionary<string, object?>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["enum"] = candidates
                                        .Select(static candidate =>
                                            candidate.EvidenceId)
                                        .ToArray()
                                },
                                ["maxItems"] = candidates.Count
                            }
                    },
                    ["required"] = new[] { "compatibleCandidateIds" },
                    ["additionalProperties"] = false
                },
                ClientJson.CamelCase));

    private static int ResolveCandidateColumnCompatibilityTokens(int candidateCount)
        => Math.Clamp(32 + Math.Max(1, candidateCount) * 5, 64, 192);

    private static bool TryReadCandidateColumnCompatibility(
        SourceBackedAgentCompletion completion,
        IReadOnlyList<CandidateColumnCompatibilityInput> candidates,
        out IReadOnlyList<string>? compatibleEvidenceIds,
        out string? failureReason)
    {
        compatibleEvidenceIds = null;
        failureReason = null;
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(completion.Content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(
                    root,
                    "compatibleCandidateIds",
                    out var ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                failureReason =
                    "candidate_column_compatibility_ids_array_required";
                return false;
            }

            var allowedIds = candidates
                .Select(static candidate => candidate.EvidenceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var parsedIds = new List<string>();
            foreach (var element in ids.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(element.GetString())
                    || !allowedIds.Contains(element.GetString()!))
                {
                    failureReason =
                        "candidate_column_compatibility_unknown_evidence_id";
                    return false;
                }
                parsedIds.Add(element.GetString()!);
            }
            compatibleEvidenceIds = parsedIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            failureReason = "candidate_column_compatibility_json_invalid";
            return false;
        }
        finally
        {
            document?.Dispose();
        }
    }
}
