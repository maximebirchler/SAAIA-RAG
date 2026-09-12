using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal static class AdvancedAnalysisResultValidator
{
    private const int MaximumAnswerCharacters = 200_000;
    private const int MaximumClaims = 512;
    private const int MaximumClaimCharacters = 8_000;
    private const int MaximumClaimEvidence = 64;
    private static readonly HashSet<string> AllowedOutcomes = new(
        ["answered", "insufficient_documentation", "clarification_required"],
        StringComparer.Ordinal);

    public static AdvancedAnalysisResultValidation ValidateAndBuild(
        string providerKey,
        AdvancedAnalysisProviderResult? providerResult,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        long elapsedMilliseconds,
        DateTimeOffset completedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(providerKey)
            || providerKey.Length > 100
            || providerResult is null)
        {
            return AdvancedAnalysisResultValidation.Invalid(
                "provider_result_required");
        }

        var outcome = providerResult.Outcome?.Trim() ?? string.Empty;
        var answer = providerResult.AnswerText?.Trim() ?? string.Empty;
        if (!AllowedOutcomes.Contains(outcome))
            return AdvancedAnalysisResultValidation.Invalid("provider_outcome_invalid");
        if (answer.Length is 0 or > MaximumAnswerCharacters)
            return AdvancedAnalysisResultValidation.Invalid("provider_answer_invalid");
        if (providerResult.Claims is null
            || providerResult.Claims.Count > MaximumClaims)
        {
            return AdvancedAnalysisResultValidation.Invalid(
                "provider_claim_limit_exceeded");
        }
        var providerModel = providerResult.ModelId?.Trim() ?? string.Empty;
        if (providerModel.Length > 256
            || providerResult.ProviderCallCount is < 0 or > 1_024
            || !IsValidUsage(providerResult.InputTokens)
            || !IsValidUsage(providerResult.OutputTokens)
            || !IsValidUsage(providerResult.CachedInputTokens)
            || (providerResult.CachedInputTokens.HasValue
                && providerResult.InputTokens.HasValue
                && providerResult.CachedInputTokens > providerResult.InputTokens)
            || providerResult.EstimatedCostUsd is < 0 or > 1_000_000m)
        {
            return AdvancedAnalysisResultValidation.Invalid(
                "provider_metrics_invalid");
        }

        var evidenceById = new Dictionary<string, AdvancedAnalysisResolvedEvidence>(
            StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item.Reference.EvidenceId)
                || !evidenceById.TryAdd(item.Reference.EvidenceId, item))
            {
                return AdvancedAnalysisResultValidation.Invalid(
                    "revalidated_evidence_identity_invalid");
            }
        }

        if (outcome == "answered"
            && (evidenceById.Count == 0 || providerResult.Claims.Count == 0))
        {
            return AdvancedAnalysisResultValidation.Invalid(
                "answered_result_requires_evidence");
        }

        var claimIds = new HashSet<string>(StringComparer.Ordinal);
        var citedEvidenceIds = new HashSet<string>(StringComparer.Ordinal);
        var claims = new List<AdvancedAnalysisResultClaim>(
            providerResult.Claims.Count);
        foreach (var claim in providerResult.Claims)
        {
            var claimId = claim?.ClaimId?.Trim() ?? string.Empty;
            var selectedItem = claim?.SelectedItem?.Trim();
            var text = claim?.Text?.Trim() ?? string.Empty;
            if (claimId.Length is 0 or > 100 || !claimIds.Add(claimId))
                return AdvancedAnalysisResultValidation.Invalid("claim_id_invalid");
            if (text.Length is 0 or > MaximumClaimCharacters)
                return AdvancedAnalysisResultValidation.Invalid("claim_text_invalid");
            if (selectedItem?.Length > 1_000)
            {
                return AdvancedAnalysisResultValidation.Invalid(
                    "claim_selected_item_invalid");
            }
            if (claim!.EvidenceIds is null
                || claim.EvidenceIds.Count is 0 or > MaximumClaimEvidence)
            {
                return AdvancedAnalysisResultValidation.Invalid(
                    "claim_evidence_required");
            }

            var cited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawEvidenceId in claim.EvidenceIds)
            {
                var evidenceId = rawEvidenceId?.Trim() ?? string.Empty;
                if (!evidenceById.ContainsKey(evidenceId))
                    return AdvancedAnalysisResultValidation.Invalid("citation_unknown");
                if (!cited.Add(evidenceId))
                    return AdvancedAnalysisResultValidation.Invalid("citation_duplicate");
                citedEvidenceIds.Add(evidenceId);
            }

            claims.Add(new AdvancedAnalysisResultClaim
            {
                ClaimId = claimId,
                SelectedItem = selectedItem,
                Text = text,
                EvidenceIds = cited.ToList()
            });
        }

        return AdvancedAnalysisResultValidation.Valid(new AdvancedAnalysisResultEnvelope
        {
            Outcome = outcome,
            AnswerText = answer,
            ProviderKey = providerKey,
            ProviderModel = providerModel,
            ProviderCallCount = providerResult.ProviderCallCount,
            InputTokens = providerResult.InputTokens,
            OutputTokens = providerResult.OutputTokens,
            CachedInputTokens = providerResult.CachedInputTokens,
            EstimatedCostUsd = providerResult.EstimatedCostUsd,
            CompletedAtUtc = completedAtUtc,
            ElapsedMilliseconds = Math.Max(0, elapsedMilliseconds),
            Evidence = evidence
                .Where(item => citedEvidenceIds.Contains(
                    item.Reference.EvidenceId ?? string.Empty))
                .Select(static item => item.Reference)
                .ToList(),
            Claims = claims
        });
    }

    private static bool IsValidUsage(int? value)
        => value is null or >= 0;
}

internal sealed record AdvancedAnalysisResultValidation(
    bool IsValid,
    string? ErrorCode,
    AdvancedAnalysisResultEnvelope? Result)
{
    public static AdvancedAnalysisResultValidation Valid(
        AdvancedAnalysisResultEnvelope result)
        => new(true, null, result);

    public static AdvancedAnalysisResultValidation Invalid(string errorCode)
        => new(false, errorCode, null);
}
