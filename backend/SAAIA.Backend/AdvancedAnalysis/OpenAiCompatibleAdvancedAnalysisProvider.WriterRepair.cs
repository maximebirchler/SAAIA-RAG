namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private async Task<AdvancedAnalysisProviderResult> ParseWriterWithRepairAsync(
        AdvancedAnalysisProviderRequest request,
        CompletionResult writer,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<PromptEvidenceItem> promptEvidence,
        List<CompletionResult> completions,
        bool runSemanticCritic,
        CancellationToken cancellationToken)
    {
        try
        {
            return ParseResult(writer.Content, evidence, request);
        }
        catch (AdvancedAnalysisProviderException error) when (
            IsRepairableWriterFailure(error.ErrorCode, request.Handoff.Load.StructuredLayout))
        {
            var remainingCalls = Math.Max(0,
                Math.Clamp(_options.ExternalMaximumCallsPerJob, 1, 1_024)
                - completions.Count - (runSemanticCritic ? 1 : 0));
            using var probe = BackendDiagnostics.Start("llm.writer_repair", _logger);
            probe.Set("job_id", request.JobId);
            probe.Set("validation_error_code", error.ErrorCode);
            probe.Set("remaining_model_calls", remainingCalls);
            probe.Set("critic_call_reserved", runSemanticCritic);
            probe.Set("repair_scheduled", remainingCalls > 0);
            probe.Set("visible_evidence_count", promptEvidence.Count);
            if (remainingCalls == 0)
            {
                probe.Complete("not_scheduled", "model_budget_unavailable");
                throw;
            }

            try
            {
                var repair = await CompleteJsonAsync(
                        request.JobId,
                        "writer-repair",
                        BuildWriterRepairSystemPrompt(),
                        BuildWriterRepairUserPrompt(
                            request, writer.Content, promptEvidence, error.ErrorCode),
                        Math.Clamp(_options.WriterMaxTokens, 512, 16_384),
                        cancellationToken,
                        canonicalProjection: promptEvidence)
                    .ConfigureAwait(false);
                completions.Add(repair);
                var parsed = ParseResult(repair.Content, evidence, request);
                probe.Complete("repaired", error.ErrorCode);
                return parsed;
            }
            catch (Exception repairError)
            {
                probe.Fail(repairError, "writer_repair_failed");
                throw;
            }
        }
    }

    private static bool IsRepairableWriterFailure(string errorCode, bool structuredLayout)
        => errorCode is "advanced_writer_claim_markers_invalid"
            or "advanced_writer_protocol_invalid"
            or "advanced_writer_evidence_id_invalid"
            or "advanced_writer_claim_count_invalid"
            or "advanced_writer_claim_coordinates_invalid"
            || errorCode == "advanced_writer_duplicate_claims" && structuredLayout;
}
