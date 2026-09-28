using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const int CandidateJudgeMaximumBatchSize = 12;

    private sealed record CandidateJudgeBatch(
        IReadOnlyList<CandidateInventoryItem> Candidates,
        IReadOnlyList<PromptEvidenceItem> Evidence);

    private static CandidateJudgeBatch BuildCandidateJudgeBatch(
        IReadOnlyList<CandidateInventoryItem> inventory,
        IReadOnlyList<PromptEvidenceItem> observations)
    {
        var visible = observations.Where(item => !string.IsNullOrWhiteSpace(item.EvidenceId))
            .ToDictionary(item => item.EvidenceId!, StringComparer.Ordinal);
        var candidates = inventory.Where(item =>
                item.Status == "body_verified"
                && item.TargetRoles.Count == 0
                && item.BodyEvidenceIds.Any(visible.ContainsKey))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Take(CandidateJudgeMaximumBatchSize)
            .ToArray();
        var evidenceIds = candidates.SelectMany(item => item.BodyEvidenceIds)
            .Where(visible.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        return new CandidateJudgeBatch(
            candidates,
            observations.Where(item => item.EvidenceId is not null
                                       && evidenceIds.Contains(item.EvidenceId))
                .ToArray());
    }

    private string BuildCandidateJudgeUserPrompt(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<CandidateInventoryItem> inventory,
        CandidateExplorerCoverage coverage,
        CandidateJudgeBatch batch)
        => JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            load = BuildPromptLoad(request.Handoff.Load),
            claimCoordinates = BuildStructuredClaimCoordinates(request.Handoff.Load),
            assignmentGap = new
            {
                coverage.RequiredDistinctCount,
                coverage.MaximumAssignableCount,
                coverage.MissingByRole
            },
            candidateInventory = new
            {
                enabled = true,
                items = batch.Candidates,
                instruction = "Return exactly one updated item for every supplied key. Preserve key and sourceKey. Use only the supplied body evidence."
            },
            evidence = batch.Evidence,
            allowedTargetRoles = request.Handoff.Load.Columns,
            outputContract = new
            {
                shape = new { items = "array" },
                allowedStatus = new[] { "body_verified", "rejected" },
                instruction = "A body_verified item needs at least one semantically justified targetRole. A rejected item uses an empty targetRoles array."
            },
            currentEligibleCandidateKeys = inventory.Where(item =>
                    item.Status is "body_verified" or "selected"
                    && item.TargetRoles.Count > 0)
                .Select(item => item.Key)
                .ToArray()
        }, JsonOptions);

    private static string BuildCandidateJudgeSystemPrompt()
        => """
           You are the SAAIA Candidate Judge. Assess only the small batch of
           substantive canonical bodies in the user JSON. For every supplied
           candidate key, decide whether the body represents one complete,
           standalone named item suitable for one or more requested target roles.
           Preserve the opaque key and sourceKey. Keep the source-exact title, or
           refine a subordinate title only to the complete identity displayed as
           an exact line in that same body. Reject components, ingredient groups,
           schedules, menu templates, category headings and incomplete fragments.

           Assign targetRoles by semantic judgment from the body and request. The
           application performs only identity validation and assignment counting;
           it does not decide suitability. Use body_verified with one or more
           roles, or rejected with no roles. selectedRoles is always empty. Cite
           only supplied evidence IDs in bodyEvidenceIds; locatorEvidenceIds is
           empty for this body batch. Return exactly one item per supplied key and
           only this JSON object: {"items":[...]}. Do not research, draft the user
           answer, count the whole dossier or invent missing evidence.
           """;

    private IReadOnlyList<CandidateInventoryItem> ParseCandidateJudgeUpdates(
        string raw,
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<CandidateInventoryItem> inventory,
        CandidateExplorerCoverage coverage,
        CandidateJudgeBatch batch)
    {
        var prompt = BuildCandidateJudgeUserPrompt(
            request,
            inventory,
            coverage,
            batch);
        try
        {
            using var output = JsonDocument.Parse(UnwrapJson(raw));
            using var promptDocument = JsonDocument.Parse(prompt);
            var updates = ParseCandidateInventoryUpdates(
                output.RootElement,
                promptDocument.RootElement);
            var expectedKeys = batch.Candidates.Select(item => item.Key)
                .ToHashSet(StringComparer.Ordinal);
            var actualKeys = updates.Select(item => item.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (!expectedKeys.SetEquals(actualKeys)
                || updates.Any(item => item.Status is not ("body_verified" or "rejected")
                                       || item.SelectedRoles.Count > 0
                                       || item.Status == "rejected" && item.TargetRoles.Count > 0))
            {
                throw new JsonException();
            }
            return updates;
        }
        catch (Exception error) when (error is JsonException or AdvancedAnalysisProviderException)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_candidate_judge_protocol_invalid");
        }
    }
}
