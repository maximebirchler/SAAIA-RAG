using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static LlmStructuredOutputContract
        BuildSemanticAnswerTransactionContract(
            IReadOnlyList<string> allowedEvidenceIds,
            int maximumClaimCount)
    {
        var leadEvidenceIds = allowedEvidenceIds
            .Append("NONE")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var schema = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["requestedDeliverableComplete"] = new Dictionary<string, object?>
                    {
                        ["type"] = "boolean"
                    },
                    ["missingUserInputPreventsUniqueResult"] =
                        new Dictionary<string, object?>
                        {
                            ["type"] = "boolean"
                        },
                    ["visibleContextEvidenceId"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = leadEvidenceIds
                    },
                    ["decision"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "answer", "clarify", "context", "research" }
                    },
                    ["answerAdequacy"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new[]
                        {
                            "requested_information_present",
                            "requested_information_missing",
                            "user_clarification_required",
                            "visible_context_required"
                        }
                    },
                    ["leadEvidenceId"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = leadEvidenceIds
                    },
                    ["presentation"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "paragraphs", "bullets" }
                    },
                    ["claims"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["minItems"] = 0,
                        ["maxItems"] = maximumClaimCount,
                        ["items"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["text"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["minLength"] = 1,
                                    ["maxLength"] = 480
                                },
                                ["evidenceIds"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "array",
                                    ["minItems"] = 1,
                                    ["maxItems"] = Math.Min(4, allowedEvidenceIds.Count),
                                    ["uniqueItems"] = true,
                                    ["items"] = new Dictionary<string, object?>
                                    {
                                        ["type"] = "string",
                                        ["enum"] = allowedEvidenceIds
                                    }
                                }
                            },
                            ["required"] = new[] { "text", "evidenceIds" },
                            ["additionalProperties"] = false
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
                    "requestedDeliverableComplete",
                    "missingUserInputPreventsUniqueResult",
                    "visibleContextEvidenceId",
                    "decision", "answerAdequacy", "leadEvidenceId",
                    "presentation", "claims", "reason"
                },
                ["additionalProperties"] = false
            });
        return new LlmStructuredOutputContract(
            "source_backed_semantic_answer_transaction_v3",
            schema);
    }

    private static bool TryReadSemanticAnswerTransaction(
        string rawOutput,
        EvidenceBundle bundle,
        IReadOnlyList<string> allowedEvidenceIds,
        int maximumClaimCount,
        out string decision,
        out string answerAdequacy,
        out bool requestedDeliverableComplete,
        out bool missingUserInputPreventsUniqueResult,
        out string visibleContextEvidenceId,
        out string leadEvidenceId,
        out string presentation,
        out IReadOnlyList<StructuredFlatWriterClaim> claims,
        out string reason,
        out string failureReason)
    {
        decision = answerAdequacy = visibleContextEvidenceId = leadEvidenceId =
            presentation = reason = failureReason = string.Empty;
        requestedDeliverableComplete = false;
        missingUserInputPreventsUniqueResult = false;
        claims = Array.Empty<StructuredFlatWriterClaim>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawOutput);
        }
        catch (JsonException)
        {
            failureReason = "semantic_answer_transaction_json_invalid";
            return false;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasOnlyStructuredFlatWriterProperties(
                    root,
                    "requestedDeliverableComplete",
                    "missingUserInputPreventsUniqueResult",
                    "visibleContextEvidenceId",
                    "decision",
                    "answerAdequacy",
                    "leadEvidenceId",
                    "presentation",
                    "claims",
                    "reason")
                || !root.TryGetProperty(
                    "requestedDeliverableComplete",
                    out var requestedDeliverableCompleteNode)
                || requestedDeliverableCompleteNode.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty(
                    "missingUserInputPreventsUniqueResult",
                    out var missingUserInputNode)
                || missingUserInputNode.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty(
                    "visibleContextEvidenceId",
                    out var visibleContextEvidenceIdNode)
                || visibleContextEvidenceIdNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("decision", out var decisionNode)
                || decisionNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty(
                    "answerAdequacy",
                    out var answerAdequacyNode)
                || answerAdequacyNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("leadEvidenceId", out var leadNode)
                || leadNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("presentation", out var presentationNode)
                || presentationNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("claims", out var claimsNode)
                || claimsNode.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("reason", out var reasonNode)
                || reasonNode.ValueKind != JsonValueKind.String)
            {
                failureReason = "semantic_answer_transaction_root_contract_invalid";
                return false;
            }
            decision = (decisionNode.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            answerAdequacy = (answerAdequacyNode.GetString() ?? string.Empty)
                .Trim()
                .ToLowerInvariant();
            requestedDeliverableComplete =
                requestedDeliverableCompleteNode.GetBoolean();
            missingUserInputPreventsUniqueResult =
                missingUserInputNode.GetBoolean();
            visibleContextEvidenceId =
                (visibleContextEvidenceIdNode.GetString() ?? string.Empty)
                .Trim()
                .ToUpperInvariant();
            leadEvidenceId = (leadNode.GetString() ?? string.Empty).Trim().ToUpperInvariant();
            presentation = (presentationNode.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            reason = (reasonNode.GetString() ?? string.Empty).Trim();
            var allowedIdSet = allowedEvidenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var answering = decision == "answer";
            var decisionValid = decision is "answer" or "clarify" or "context" or "research";
            var answerAdequacyValid = answerAdequacy is
                "requested_information_present"
                or "requested_information_missing"
                or "user_clarification_required"
                or "visible_context_required";
            var leadValid = decision switch
            {
                "answer" or "clarify" or "research" =>
                    leadEvidenceId == "NONE" || allowedIdSet.Contains(leadEvidenceId),
                "context" => allowedIdSet.Contains(leadEvidenceId),
                _ => false
            };
            var visibleContextEvidenceIdValid =
                visibleContextEvidenceId == "NONE"
                || allowedIdSet.Contains(visibleContextEvidenceId);
            if (!decisionValid
                || !answerAdequacyValid
                || presentation is not ("paragraphs" or "bullets")
                || reason.Length is < 8 or > 480
                || !leadValid
                || !visibleContextEvidenceIdValid
                || (answering ? claimsNode.GetArrayLength() < 1 : claimsNode.GetArrayLength() != 0)
                || claimsNode.GetArrayLength() > maximumClaimCount)
            {
                failureReason = "semantic_answer_transaction_decision_contract_invalid";
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
            if (!string.Equals(
                    answerAdequacy,
                    expectedAdequacy,
                    StringComparison.Ordinal))
            {
                failureReason =
                    "semantic_answer_transaction_adequacy_decision_mismatch";
                return false;
            }
            var resolutionDecisionValid = decision switch
            {
                "answer" => requestedDeliverableComplete
                            && !missingUserInputPreventsUniqueResult
                            && visibleContextEvidenceId == "NONE",
                "clarify" => !requestedDeliverableComplete
                             && missingUserInputPreventsUniqueResult
                             && visibleContextEvidenceId == "NONE",
                "context" => !requestedDeliverableComplete
                             && !missingUserInputPreventsUniqueResult
                             && visibleContextEvidenceId != "NONE"
                             && leadEvidenceId == visibleContextEvidenceId,
                "research" => !requestedDeliverableComplete
                              && !missingUserInputPreventsUniqueResult
                              && visibleContextEvidenceId == "NONE",
                _ => false
            };
            if (!resolutionDecisionValid)
            {
                failureReason =
                    "semantic_answer_transaction_resolution_decision_mismatch";
                return false;
            }
            if (!answering)
                return true;

            var writerPayload = JsonSerializer.Serialize(new
            {
                presentation,
                claims = claimsNode
            });
            var writerContext = BuildSelectedEvidenceWriterContext(
                bundle,
                allowedEvidenceIds,
                "content_claim");
            if (!TryReadStructuredFlatWriterOutput(
                    writerPayload,
                    writerContext,
                    "content_claim",
                    maximumClaimCount,
                    null,
                    out presentation,
                    out claims,
                    out failureReason))
            {
                failureReason = failureReason.Replace(
                    "structured_flat_writer_",
                    "semantic_answer_transaction_",
                    StringComparison.Ordinal);
                return false;
            }
            var selectedLeadEvidenceId = leadEvidenceId;
            if (selectedLeadEvidenceId != "NONE"
                && !claims.Any(claim => claim.EvidenceIds.Contains(
                    selectedLeadEvidenceId,
                    StringComparer.OrdinalIgnoreCase)))
            {
                failureReason = "semantic_answer_transaction_lead_not_cited";
                return false;
            }
            return true;
        }
    }
}
