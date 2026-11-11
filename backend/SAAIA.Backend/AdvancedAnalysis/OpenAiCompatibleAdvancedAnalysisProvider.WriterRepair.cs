namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private async Task<CompletionResult> RepairWriterCompletionIfNeededAsync(
        AdvancedAnalysisProviderRequest request,
        CompletionResult writer,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<PromptEvidenceItem> promptEvidence,
        List<CompletionResult> completions,
        int reservedFinalCalls,
        bool repairAlreadyUsed,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = ParseResult(writer.Content, evidence, request);
            return writer;
        }
        catch (AdvancedAnalysisProviderException error) when (
            IsRepairableWriterFailure(error.ErrorCode, request.Handoff.Load.StructuredLayout))
        {
            if (repairAlreadyUsed)
                throw;
            var remainingCalls = Math.Max(0,
                Math.Clamp(_options.ExternalMaximumCallsPerJob, 1, 1_024)
                - completions.Count - 1 - reservedFinalCalls);
            using var probe = BackendDiagnostics.Start("llm.writer_repair", _logger);
            probe.Set("job_id", request.JobId);
            probe.Set("validation_error_code", error.ErrorCode);
            probe.Set("remaining_model_calls", remainingCalls);
            probe.Set("critic_call_reserved", reservedFinalCalls > 0);
            probe.Set("repair_scheduled", remainingCalls > 0);
            probe.Set("visible_evidence_count", promptEvidence.Count);
            if (remainingCalls == 0)
            {
                probe.Complete("not_scheduled", "model_budget_unavailable");
                throw;
            }

            try
            {
                // The caller accounts for the returned terminal completion.
                // Retain the rejected call here exactly once before replacing it.
                completions.Add(writer);
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
                var parsed = ParseResult(repair.Content, evidence, request);
                probe.Set("repaired_partial", parsed.Outcome == "insufficient_documentation");
                probe.Complete("repaired", error.ErrorCode);
                return repair;
            }
            catch (Exception repairError)
            {
                probe.Fail(repairError, "writer_repair_failed");
                throw;
            }
        }
        catch (AdvancedAnalysisProviderException)
        {
            // Identity/body corrections still belong to the research loop.
            // This helper only repairs the existing protocol failure classes;
            // uncorrected failures remain subject to final result validation.
            return writer;
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
