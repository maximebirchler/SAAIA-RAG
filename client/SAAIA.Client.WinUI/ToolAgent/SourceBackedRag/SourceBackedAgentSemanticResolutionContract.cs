using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static LlmStructuredOutputContract BuildSemanticResolutionContract(
        IReadOnlyList<string> allowedEvidenceIds)
    {
        var schema = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["decision"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "answer", "clarify", "context", "research" }
                    },
                    ["evidenceIds"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["minItems"] = 0,
                        ["maxItems"] = Math.Min(12, allowedEvidenceIds.Count),
                        ["uniqueItems"] = true,
                        ["items"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["enum"] = allowedEvidenceIds
                        }
                    },
                    ["leadEvidenceIds"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["minItems"] = 0,
                        ["maxItems"] = Math.Min(12, allowedEvidenceIds.Count),
                        ["uniqueItems"] = true,
                        ["items"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["enum"] = allowedEvidenceIds
                        }
                    },
                    ["reason"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["minLength"] = 8,
                        ["maxLength"] = 480
                    }
                },
                ["required"] = new[]
                {
                    "decision",
                    "evidenceIds",
                    "leadEvidenceIds",
                    "reason"
                },
                ["additionalProperties"] = false
            });
        return new LlmStructuredOutputContract(
            "source_backed_semantic_resolution_v6",
            schema);
    }

    private static bool TryReadSemanticResolution(
        string rawOutput,
        IReadOnlyList<string> allowedEvidenceIds,
        out string decision,
        out string answerAdequacy,
        out bool requestedDeliverableComplete,
        out bool missingUserInputPreventsUniqueResult,
        out string visibleContextEvidenceId,
        out IReadOnlyList<string> selectedEvidenceIds,
        out IReadOnlyList<string> leadEvidenceIds,
        out string reason,
        out string failureReason)
    {
        decision = answerAdequacy = visibleContextEvidenceId = reason =
            failureReason = string.Empty;
        requestedDeliverableComplete = false;
        missingUserInputPreventsUniqueResult = false;
        selectedEvidenceIds = Array.Empty<string>();
        leadEvidenceIds = Array.Empty<string>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawOutput);
        }
        catch (JsonException)
        {
            failureReason = "semantic_resolution_json_invalid";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasOnlyStructuredFlatWriterProperties(
                    root,
                    "decision",
                    "evidenceIds",
                    "leadEvidenceIds",
                    "reason")
                || !root.TryGetProperty("decision", out var decisionNode)
                || decisionNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("evidenceIds", out var selectionNode)
                || selectionNode.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("leadEvidenceIds", out var leadSelectionNode)
                || leadSelectionNode.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("reason", out var reasonNode)
                || reasonNode.ValueKind != JsonValueKind.String)
            {
                failureReason = "semantic_resolution_root_contract_invalid";
                return false;
            }

            decision = (decisionNode.GetString() ?? string.Empty)
                .Trim().ToLowerInvariant();
            reason = (reasonNode.GetString() ?? string.Empty).Trim();

            var allowedIdSet = allowedEvidenceIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var parsedSelection = new List<string>();
            var parsedSelectionSet = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var node in selectionNode.EnumerateArray())
            {
                var id = node.ValueKind == JsonValueKind.String
                    ? node.GetString()?.Trim().ToUpperInvariant()
                    : null;
                if (string.IsNullOrWhiteSpace(id)
                    || !allowedIdSet.Contains(id)
                    || !parsedSelectionSet.Add(id))
                {
                    failureReason = "semantic_resolution_evidence_id_invalid";
                    return false;
                }
                parsedSelection.Add(id);
            }

            var parsedLeads = new List<string>();
            var parsedLeadSet = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var node in leadSelectionNode.EnumerateArray())
            {
                var id = node.ValueKind == JsonValueKind.String
                    ? node.GetString()?.Trim().ToUpperInvariant()
                    : null;
                if (string.IsNullOrWhiteSpace(id)
                    || !allowedIdSet.Contains(id)
                    || !parsedLeadSet.Add(id))
                {
                    failureReason = "semantic_resolution_lead_evidence_id_invalid";
                    return false;
                }
                parsedLeads.Add(id);
            }

            if (decision is not ("answer" or "clarify" or "context" or "research")
                || reason.Length is < 8 or > 480
                || parsedSelection.Count > Math.Min(12, allowedEvidenceIds.Count)
                || parsedLeads.Count > Math.Min(12, allowedEvidenceIds.Count))
            {
                failureReason = "semantic_resolution_decision_contract_invalid";
                return false;
            }

            var expectedAdequacy = decision switch
            {
                "answer" => "requested_information_present",
                "research" => "requested_information_missing",
                "clarify" => "user_clarification_required",
                "context" => "visible_context_required",
                _ => string.Empty
            };
            var resolutionValid = decision switch
            {
                "answer" => parsedSelection.Count > 0 && parsedLeads.Count > 0,
                "research" or "clarify" =>
                    parsedSelection.Count == 0 && parsedLeads.Count == 0,
                "context" => parsedSelection.Count == 1
                             && parsedLeads.Count == 1
                             && string.Equals(
                                 parsedSelection[0],
                                 parsedLeads[0],
                                 StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!resolutionValid)
            {
                failureReason = "semantic_resolution_resolution_decision_mismatch";
                return false;
            }

            if (decision == "answer"
                && parsedLeads.Any(lead => !parsedSelectionSet.Contains(lead)))
            {
                failureReason = "semantic_resolution_lead_evidence_not_selected";
                return false;
            }

            answerAdequacy = expectedAdequacy;
            requestedDeliverableComplete = decision == "answer";
            missingUserInputPreventsUniqueResult = decision == "clarify";
            visibleContextEvidenceId = decision == "context"
                ? parsedSelection[0]
                : "NONE";
            selectedEvidenceIds = decision switch
            {
                "answer" => parsedLeads
                    .Concat(parsedSelection.Where(id => !parsedLeadSet.Contains(id)))
                    .ToArray(),
                "context" => parsedSelection,
                _ => Array.Empty<string>()
            };
            leadEvidenceIds = decision is "answer" or "context"
                ? parsedLeads
                : Array.Empty<string>();
            return true;
        }
    }
}
