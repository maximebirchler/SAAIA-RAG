using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static IReadOnlyList<SourceBackedAgentToolDefinition>
        BuildCandidateBatchAuditTool(
            IReadOnlyList<EvidenceItem> candidates)
        => new[]
        {
            new SourceBackedAgentToolDefinition(
                SubmitCandidateBatchAuditToolName,
                "Soumet dans l'ordre du lot le rejet ou l'index de libelle choisi pour chaque candidat.",
                BuildCandidateBatchAuditSchema(candidates))
        };

    private static LlmStructuredOutputContract
        BuildCandidateBatchAuditContract(
            IReadOnlyList<EvidenceItem> candidates)
        => new(
            "source_backed_candidate_batch_audit_v5",
            BuildCandidateBatchOrderedAuditSchema(candidates));

    private static JsonElement BuildCandidateBatchOrderedAuditSchema(
        IReadOnlyList<EvidenceItem> candidates)
    {
        var maximumLabelIndex = candidates.Count == 0
            ? 0
            : candidates.Max(candidate =>
                BuildCandidateBatchInitialDisplayValueOptions(candidate).Count);

        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["decisions"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["description"] =
                            "Une decision par candidat, dans l'ordre c1, c2, etc. 0 rejette; n choisit le libelle n.",
                        ["items"] = new Dictionary<string, object?>
                        {
                            ["type"] = "integer",
                            ["minimum"] = 0,
                            ["maximum"] = maximumLabelIndex
                        },
                        ["minItems"] = candidates.Count,
                        ["maxItems"] = candidates.Count
                    }
                },
                ["required"] = new[] { "decisions" },
                ["additionalProperties"] = false
            },
            ClientJson.CamelCase);
    }

    private static JsonElement BuildCandidateBatchDirectLabelAuditSchema(
        IReadOnlyList<EvidenceItem> candidates)
    {
        var labelProperties = new Dictionary<string, object?>(
            StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            labelProperties[CandidateAuditKey(index)] =
                new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = new[] { string.Empty }
                        .Concat(BuildCandidateBatchDisplayValueOptions(
                            candidates[index]))
                        .ToArray()
                };
        }

        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["selectedLabelsByCandidateKey"] =
                        new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["description"] =
                                "Une decision par cle courte c1, c2, etc. Chaine vide rejette; sinon le libelle exact est recopie.",
                            ["properties"] = labelProperties,
                            ["required"] = Enumerable.Range(0, candidates.Count)
                                .Select(CandidateAuditKey)
                                .ToArray(),
                            ["additionalProperties"] = false
                        }
                },
                ["required"] = new[] { "selectedLabelsByCandidateKey" },
                ["additionalProperties"] = false
            },
            ClientJson.CamelCase);
    }

    private static JsonElement BuildCandidateBatchAuditSchema(
        IReadOnlyList<EvidenceItem> candidates)
    {
        var labelProperties = new Dictionary<string, object?>(
            StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            labelProperties[CandidateAuditKey(index)] =
                new Dictionary<string, object?>
                {
                    ["type"] = "integer",
                    ["minimum"] = 0,
                    ["maximum"] = Math.Max(
                        0,
                        BuildCandidateBatchInitialDisplayValueOptions(
                            candidates[index]).Count)
                };
        }

        return JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["labelIndexesByCandidateKey"] =
                        new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["description"] =
                                "Une decision par cle courte c1, c2, etc. 0 rejette; n choisit le libelle n.",
                            ["properties"] = labelProperties,
                            ["required"] = Enumerable.Range(0, candidates.Count)
                                .Select(CandidateAuditKey)
                                .ToArray(),
                            ["additionalProperties"] = false
                        }
                },
                ["required"] = new[] { "labelIndexesByCandidateKey" },
                ["additionalProperties"] = false
            },
            ClientJson.CamelCase);
    }

    private static CandidateCollectionAuditDecision
        ReadCandidateBatchAuditDecision(
            SourceBackedAgentCompletion completion,
            IReadOnlyList<EvidenceItem> candidates)
    {
        JsonElement arguments;
        JsonDocument? structuredDocument = null;
        if (completion.ToolCalls.Count == 1
            && string.Equals(
                completion.ToolCalls[0].Name,
                SubmitCandidateBatchAuditToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            arguments = completion.ToolCalls[0].Arguments;
        }
        else if (completion.ToolCalls.Count == 0
                 && TryParseCandidateBatchAuditContent(
                     completion.Content,
                     out structuredDocument))
        {
            arguments = structuredDocument!.RootElement;
        }
        else
        {
            return InvalidCandidateBatchDecision(
                "candidate_batch_audit_structured_object_required");
        }

        try
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return InvalidCandidateBatchDecision(
                    "candidate_batch_audit_labels_missing");
            }

            var usesOrderedDecisions = arguments.TryGetProperty(
                    "decisions",
                    out var orderedDecisions)
                && orderedDecisions.ValueKind == JsonValueKind.Array;
            if (usesOrderedDecisions
                && orderedDecisions.GetArrayLength() != candidates.Count)
            {
                return InvalidCandidateBatchDecision(
                    "candidate_batch_audit_decision_count_mismatch");
            }

            JsonElement labels = default;
            var usesDirectLabels = !usesOrderedDecisions
                && arguments.TryGetProperty(
                    "selectedLabelsByCandidateKey",
                    out labels)
                && labels.ValueKind == JsonValueKind.Object;
            var usesCandidateKeys = !usesOrderedDecisions
                && !usesDirectLabels
                && arguments.TryGetProperty(
                    "labelIndexesByCandidateKey",
                    out labels)
                && labels.ValueKind == JsonValueKind.Object;
            var usesLegacyEvidenceMap = !usesOrderedDecisions
                && !usesDirectLabels
                && !usesCandidateKeys
                && arguments.TryGetProperty(
                    "labelIndexesByEvidenceId",
                    out labels)
                && labels.ValueKind == JsonValueKind.Object;
            if (!usesOrderedDecisions
                && !usesDirectLabels
                && !usesCandidateKeys
                && !usesLegacyEvidenceMap)
            {
                return InvalidCandidateBatchDecision(
                    "candidate_batch_audit_labels_missing");
            }
            if (usesDirectLabels || usesCandidateKeys)
            {
                var expectedKeys = Enumerable.Range(0, candidates.Count)
                    .Select(CandidateAuditKey)
                    .ToHashSet(StringComparer.Ordinal);
                var returnedKeys = labels.EnumerateObject()
                    .Select(static property => property.Name)
                    .ToArray();
                if (returnedKeys.Length != expectedKeys.Count
                    || returnedKeys.Any(key => !expectedKeys.Contains(key)))
                {
                    return InvalidCandidateBatchDecision(
                        "candidate_batch_audit_candidate_key_set_mismatch");
                }
            }
            if (usesLegacyEvidenceMap)
            {
                var expectedIds = candidates
                    .Select(static candidate => candidate.EvidenceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var returnedIds = labels.EnumerateObject()
                    .Select(static property => property.Name)
                    .ToArray();
                if (returnedIds.Length != expectedIds.Count
                    || returnedIds.Any(id => !expectedIds.Contains(id)))
                {
                    return InvalidCandidateBatchDecision(
                        "candidate_batch_audit_evidence_set_mismatch");
                }
            }

            var approvals = new List<CandidateCollectionApproval>();
            var rejectedIds = new List<string>();
            var classifications = new List<CandidateCollectionClassification>();
            for (var candidateIndex = 0;
                 candidateIndex < candidates.Count;
                 candidateIndex++)
            {
                var candidate = candidates[candidateIndex];
                var labelPropertyName = usesDirectLabels || usesCandidateKeys
                    ? CandidateAuditKey(candidateIndex)
                    : candidate.EvidenceId;
                var hasLabel = usesOrderedDecisions
                    || TryGetPropertyIgnoreCase(
                        labels,
                        labelPropertyName,
                        out _);
                var labelValue = usesOrderedDecisions
                    ? orderedDecisions[candidateIndex]
                    : GetPropertyIgnoreCase(labels, labelPropertyName);
                if (!hasLabel)
                {
                    return InvalidCandidateBatchDecision(
                        "candidate_batch_audit_label_invalid:"
                        + candidate.EvidenceId);
                }

                var displayValues = usesDirectLabels
                    ? BuildCandidateBatchDisplayValueOptions(candidate)
                    : BuildCandidateBatchInitialDisplayValueOptions(candidate);
                int labelIndex;
                if (usesDirectLabels)
                {
                    if (labelValue.ValueKind != JsonValueKind.String)
                    {
                        return InvalidCandidateBatchDecision(
                            "candidate_batch_audit_label_invalid:"
                            + candidate.EvidenceId);
                    }
                    var selectedLabel = labelValue.GetString() ?? string.Empty;
                    labelIndex = selectedLabel.Length == 0
                        ? 0
                        : FindCandidateDisplayValueIndex(
                            displayValues,
                            selectedLabel);
                    if (labelIndex < 0)
                    {
                        return InvalidCandidateBatchDecision(
                            "candidate_batch_audit_label_unknown:"
                            + candidate.EvidenceId);
                    }
                }
                else if (labelValue.ValueKind != JsonValueKind.Number
                         || !labelValue.TryGetInt32(out labelIndex))
                {
                    return InvalidCandidateBatchDecision(
                        "candidate_batch_audit_label_invalid:"
                        + candidate.EvidenceId);
                }
                if (labelIndex < 0 || labelIndex > displayValues.Count)
                {
                    return InvalidCandidateBatchDecision(
                        "candidate_batch_audit_label_out_of_range:"
                        + candidate.EvidenceId);
                }
                if (labelIndex == 0)
                {
                    rejectedIds.Add(candidate.EvidenceId);
                    classifications.Add(new CandidateCollectionClassification(
                        candidate.EvidenceId,
                        OtherWrongTypeClassification,
                        GetEvidenceDisplayValue(candidate)));
                    continue;
                }

                var displayValue = displayValues[labelIndex - 1];
                approvals.Add(new CandidateCollectionApproval(
                    candidate.EvidenceId,
                    displayValue));
                classifications.Add(new CandidateCollectionClassification(
                    candidate.EvidenceId,
                    NamedSourceItemClassification,
                    displayValue,
                    !string.Equals(
                        displayValue,
                        GetEvidenceDisplayValue(candidate),
                        StringComparison.OrdinalIgnoreCase)));
            }

            return new CandidateCollectionAuditDecision(
                true,
                approvals,
                rejectedIds,
                classifications,
                null);
        }
        finally
        {
            structuredDocument?.Dispose();
        }
    }

    private static bool TryParseCandidateBatchAuditContent(
        string? content,
        out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(content))
            return false;
        try
        {
            document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            document?.Dispose();
            document = null;
            return false;
        }
    }

    private static string CandidateAuditKey(int zeroBasedIndex)
        => "c" + (zeroBasedIndex + 1);

    private static int FindCandidateDisplayValueIndex(
        IReadOnlyList<string> displayValues,
        string selectedLabel)
    {
        for (var index = 0; index < displayValues.Count; index++)
        {
            if (string.Equals(
                    displayValues[index],
                    selectedLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index + 1;
            }
        }

        return -1;
    }

    private static JsonElement GetPropertyIgnoreCase(
        JsonElement element,
        string name)
        => TryGetPropertyIgnoreCase(element, name, out var value)
            ? value
            : default;

    private static CandidateCollectionAuditDecision
        InvalidCandidateBatchDecision(string reason)
        => new(
            false,
            Array.Empty<CandidateCollectionApproval>(),
            Array.Empty<string>(),
            Array.Empty<CandidateCollectionClassification>(),
            reason);
}
