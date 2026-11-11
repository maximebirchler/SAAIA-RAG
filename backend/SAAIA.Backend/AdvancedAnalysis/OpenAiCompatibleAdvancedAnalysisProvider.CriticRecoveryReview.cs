using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private async Task<AdvancedAnalysisProviderResult> ReviewRecoveredCriticAnswerAsync(
        AdvancedAnalysisProviderRequest request, AdvancedAnalysisProviderResult candidate,
        SynthesisCompletion round, List<CompletionResult> completions, CancellationToken cancellationToken)
    {
        if (candidate.Outcome != "answered" || !round.InsufficiencyReviewed
            || round.PriorCriticPartial is not { Outcome: "insufficient_documentation" } prior)
            return candidate;

        // A repair/research proposal is not the independent acceptance of that
        // proposal. Keep the last reviewed partial available if the gate cannot
        // complete; it must still bind to the current, visible evidence.
        AdvancedAnalysisProviderResult RetainPartial()
        {
            var retained = ParseResult(JsonSerializer.Serialize(BuildCriticCandidate(prior), JsonOptions),
                round.Evidence, request);
            EnsureVisibleClaimCitations(retained, round.PromptEvidence.Where(item => item.EvidenceId is not null
                    && GetVisibleEvidenceContents(item).Any(content => !string.IsNullOrWhiteSpace(content)))
                .Select(item => item.EvidenceId!).ToArray());
            return retained;
        }

        using var probe = BackendDiagnostics.Start("llm.critic_recovery_review", _logger);
        probe.Set("job_id", request.JobId);
        probe.Set("proposed_claim_count", candidate.Claims.Count);
        probe.Set("prior_partial_claim_count", prior.Claims.Count);
        if (completions.Count >= Math.Clamp(_options.ExternalMaximumCallsPerJob, 1, 1_024))
        {
            var retained = RetainPartial();
            probe.Complete("retained_partial", "final_review_call_budget_unavailable");
            return retained;
        }

        var payload = JsonNode.Parse(BuildCriticUserPrompt(request, candidate, round.PromptEvidence))!.AsObject();
        payload["priorCriticReview"] = JsonSerializer.SerializeToNode(new
        {
            documentaryEvidence = false,
            priorAssessment = BuildCriticCandidate(prior),
            instruction = "The candidate was newly proposed after this partial review. Independently check whether the previous concerns are actually resolved. The prior assessment is model output to assess, not a source or a binding verdict. Rewording, relabeling or rearranging an unsuitable choice does not resolve its suitability. A complete count and valid citations do not establish fitness for the user's requested purpose. Preserve all useful supported choices in an explicit partial result if concerns remain; completion is not required."
        }, JsonOptions);
        CompletionResult review;
        try
        {
            // A fresh review context: no research/recovery conversation replay,
            // no new tools, and no recursive recovery from the final verdict.
            review = await CompleteJsonAsync(request.JobId, "critic-final-review", BuildCriticSystemPrompt(),
                    payload.ToJsonString(JsonOptions), Math.Clamp(_options.CriticMaxTokens, 512, 16_384),
                    cancellationToken, canonicalProjection: round.PromptEvidence)
                .ConfigureAwait(false);
        }
        catch (AdvancedAnalysisProviderException error) when (error.ErrorCode == "advanced_llm_output_limit")
        {
            completions.Add(new CompletionResult(string.Empty,
                error.ProviderUsage ?? new(null, null, null), error.EstimatedCostUsd));
            var retained = RetainPartial();
            probe.Set("failed_call_usage_known", error.ProviderUsage?.OutputTokens is not null);
            probe.Complete("retained_partial", "final_review_output_limit");
            return retained;
        }
        completions.Add(review);
        AdvancedAnalysisProviderResult result;
        try
        {
            result = ParseResult(review.Content, round.Evidence, request);
            EnsureVisibleClaimCitations(result, round.PromptEvidence.Where(item => item.EvidenceId is not null
                    && GetVisibleEvidenceContents(item).Any(content => !string.IsNullOrWhiteSpace(content)))
                .Select(item => item.EvidenceId!).ToArray());
        }
        catch (AdvancedAnalysisProviderException error)
        {
            var retained = RetainPartial();
            probe.Set("validation_error", error.ErrorCode);
            probe.Complete("retained_partial", "final_review_protocol_invalid");
            return retained;
        }
        probe.Set("reviewed_claim_count", result.Claims.Count);
        probe.Complete(result.Outcome, "recovered_proposal_independently_reviewed");
        return result;
    }
}
