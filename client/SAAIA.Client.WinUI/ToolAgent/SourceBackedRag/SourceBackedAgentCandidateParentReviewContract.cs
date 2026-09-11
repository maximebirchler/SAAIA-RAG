using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string ParentReviewAcceptPrefix = "accept::";

    private static readonly string[] ParentReviewRejectionDecisions =
    {
        "reject_axis_or_role",
        "reject_category_or_collection",
        "reject_instruction_or_fragment",
        "reject_other_wrong_type"
    };

    private static LlmStructuredOutputContract
        BuildCandidateParentReviewContract(
            IReadOnlyList<EvidenceItem> candidates)
    {
        var decisionProperties = new Dictionary<string, object?>(
            StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            decisionProperties[CandidateAuditKey(index)] =
                new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["description"] =
                        "Examine toutes les options independamment. Accepte le premier libelle valide, y compris un parent qui sauve un fragment direct; rejette seulement si aucune option ne convient.",
                    ["enum"] = ParentReviewRejectionDecisions
                        .Concat(BuildCandidateBatchDisplayValueOptions(
                            candidates[index])
                            .Select(static label =>
                                ParentReviewAcceptPrefix + label))
                        .ToArray()
                };
        }

        var requiredKeys = Enumerable.Range(0, candidates.Count)
            .Select(CandidateAuditKey)
            .ToArray();
        var schema = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["decisionsByCandidateKey"] =
                        new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["description"] =
                                "Une decision compacte par candidat: accept::libelle exact ou motif reject_*.",
                            ["properties"] = decisionProperties,
                            ["required"] = requiredKeys,
                            ["additionalProperties"] = false
                        }
                },
                ["required"] = new[] { "decisionsByCandidateKey" },
                ["additionalProperties"] = false
            },
            ClientJson.CamelCase);
        return new LlmStructuredOutputContract(
            "source_backed_candidate_parent_review_v2",
            schema);
    }

    private static CandidateCollectionAuditDecision
        ReadCandidateParentReviewDecision(
            SourceBackedAgentCompletion completion,
            IReadOnlyList<EvidenceItem> candidates)
    {
        if (completion.ToolCalls.Count > 0)
            return ReadCandidateBatchAuditDecision(completion, candidates);
        if (!TryParseParentReviewContent(
                completion.Content,
                out var document))
        {
            return InvalidCandidateBatchDecision(
                "candidate_parent_review_structured_object_required");
        }

        using (document)
        {
            var root = document!.RootElement;
            if (!TryGetPropertyIgnoreCase(
                    root,
                    "decisionsByCandidateKey",
                    out var decisions)
                || decisions.ValueKind != JsonValueKind.Object)
            {
                return InvalidCandidateBatchDecision(
                    "candidate_parent_review_decisions_missing");
            }

            var expectedKeys = Enumerable.Range(0, candidates.Count)
                .Select(CandidateAuditKey)
                .ToHashSet(StringComparer.Ordinal);
            var returnedKeys = decisions.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray();
            if (returnedKeys.Length != expectedKeys.Count
                || returnedKeys.Any(key => !expectedKeys.Contains(key)))
            {
                return InvalidCandidateBatchDecision(
                    "candidate_parent_review_key_set_mismatch");
            }

            var approvals = new List<CandidateCollectionApproval>();
            var rejectedIds = new List<string>();
            var classifications = new List<CandidateCollectionClassification>();
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var decision = GetString(decisions, CandidateAuditKey(index));
                if (decision?.StartsWith(
                        ParentReviewAcceptPrefix,
                        StringComparison.Ordinal) == true)
                {
                    var label = decision[ParentReviewAcceptPrefix.Length..];
                    if (!BuildCandidateBatchDisplayValueOptions(candidate)
                        .Contains(label, StringComparer.Ordinal))
                    {
                        return InvalidCandidateBatchDecision(
                            "candidate_parent_review_label_invalid:"
                            + candidate.EvidenceId);
                    }
                    approvals.Add(new CandidateCollectionApproval(
                        candidate.EvidenceId,
                        label));
                    classifications.Add(new CandidateCollectionClassification(
                        candidate.EvidenceId,
                        "named_atomic_instance",
                        label));
                    continue;
                }

                if (!ParentReviewRejectionDecisions.Contains(
                        decision,
                        StringComparer.Ordinal))
                {
                    return InvalidCandidateBatchDecision(
                        "candidate_parent_review_decision_invalid:"
                        + candidate.EvidenceId);
                }
                rejectedIds.Add(candidate.EvidenceId);
                classifications.Add(new CandidateCollectionClassification(
                    candidate.EvidenceId,
                    decision!["reject_".Length..],
                    GetEvidenceDisplayValue(candidate) ?? string.Empty));
            }

            return new CandidateCollectionAuditDecision(
                true,
                approvals,
                rejectedIds,
                classifications,
                null);
        }
    }

    private static bool TryParseParentReviewContent(
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
}
