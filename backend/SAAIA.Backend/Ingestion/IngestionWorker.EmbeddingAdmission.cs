using Npgsql;
using SAAIA.Backend;

sealed partial class IngestionWorker
{
    private async Task<(PreparedEmbeddingProjection Projection, TeiInputBudget Budget)> PrepareEmbeddingInputsAsync(
        NpgsqlDataSource ds, IngestionJob job, string workerId, HttpClient tei,
        IngestionOptions ingestion, RagOptions rag, string relativePath,
        IReadOnlyList<ExtractedDocumentSection> sections, IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> chunks, CancellationToken cancellationToken)
    {
        using var probe = BackendDiagnostics.Start("ingestion.embedding_input_budget", _log);
        probe.Set("job_id", job.JobId);
        probe.Set("input_chunks", chunks.Count);
        try
        {
            var result = await RunWithJobHeartbeatAsync(ds, job, workerId, async operationToken =>
            {
                using (await _bulkheads.AcquireHeavyComputeAsync(operationToken))
                using (await _teiGovernor.AcquireIngestionAsync(ingestion.TeiInteractiveQuietPeriodMs, operationToken))
                using (await _bulkheads.AcquireTeiAsync(operationToken))
                using (var deadline = CreateTimeoutCts(operationToken, ingestion.TeiTimeoutSeconds))
                {
                    var token = deadline?.Token ?? operationToken;
                    var budget = await TeiClient.GetInputBudgetAsync(tei, token);
                    var projection = await IngestionEmbeddingInputStage.PrepareAsync(
                        chunks, ShouldEmbedRetrievalChunk,
                        chunk => ContextualTextProjector.Project(relativePath, sections, units, [chunk]).Single().Text,
                        text => TeiClient.FormatEmbeddingInput(rag.EmbeddingsModel, text, TeiClient.EmbeddingInputKind.Passage),
                        (text, ct) => TeiClient.CountInputTokensAsync(tei, text, ct), budget, token,
                        isStructuredChunk: DoclingCanonicalRetrievalProjector.IsStructuredChunk,
                        splitStructuredChunk: DoclingCanonicalRetrievalProjector.TrySplitStructuredChunk,
                        normalizeBasisInput: CleanTextForIndexing,
                        buildSourceBasis: DoclingCanonicalRetrievalProjector.ResolveEmbeddingSourceText);
                    return (Projection: projection, Budget: budget);
                }
            }, cancellationToken);
            probe.Set("runtime_max_input_tokens", result.Budget.MaximumInputTokens);
            probe.Set("runtime_max_client_batch_size", result.Budget.MaximumClientBatchSize);
            probe.Set("output_chunks", result.Projection.Chunks.Count);
            probe.Set("admitted_inputs", result.Projection.InputsByChunkIndex.Count);
            probe.Set("split_source_chunks", result.Projection.SplitChunks);
            probe.Set("context_reduced_inputs", result.Projection.ContextReducedInputs);
            probe.Set("maximum_tokens_before_admission", result.Projection.MaxObservedTokens);
            probe.Set("maximum_admitted_tokens", result.Projection.ProvenanceByChunkIndex.Values.Select(p => p.InputTokens).DefaultIfEmpty().Max());
            probe.Set("tokenization_requests", result.Projection.TokenizationRequests);
            probe.Set("tokenizer_character_limit_refusals", result.Projection.CharacterLimitRefusals);
            probe.Complete();
            return result;
        }
        catch (Exception exception)
        {
            if (exception is TeiRequestException refusal)
            {
                probe.Set("tei_error_code", refusal.ErrorCode);
                probe.Set("runtime_max_input_tokens", refusal.MaximumTokens);
                probe.Set("refused_input_tokens", refusal.ObservedTokens);
                probe.Set("runtime_max_input_characters", refusal.MaximumCharacters);
                probe.Set("refused_input_characters", refusal.ObservedCharacters);
            }
            probe.Fail(exception);
            throw;
        }
    }
}
