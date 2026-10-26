using System.Security.Cryptography;
using System.Text;

internal sealed record EmbeddingInputProvenance(
    int ParentChunkIndex, int? SourceTextStart, int? SourceTextLength,
    int InputTokens, bool ContextReduced, bool SourcePartitioned, bool StructuredPartition);

internal sealed record PreparedEmbeddingProjection(
    IReadOnlyList<ProjectedRetrievalChunk> Chunks,
    IReadOnlyDictionary<int, string> InputsByChunkIndex,
    IReadOnlyDictionary<int, EmbeddingInputProvenance> ProvenanceByChunkIndex,
    int SplitChunks, int ContextReducedInputs, int MaxObservedTokens, int TokenizationRequests,
    IReadOnlyDictionary<int, string> BasisTextsByChunkIndex,
    int CharacterLimitRefusals = 0);

/// <summary>
/// Admission precedes publication and vector-plan creation. Context can be reduced explicitly;
/// source is either preserved in full or partitioned into fully measured, source-bound chunks.
/// </summary>
internal static class IngestionEmbeddingInputStage
{
    internal static async Task<PreparedEmbeddingProjection> PrepareAsync(
        IReadOnlyList<ProjectedRetrievalChunk> chunks,
        Func<ProjectedRetrievalChunk, bool> shouldEmbed,
        Func<ProjectedRetrievalChunk, string> buildContext,
        Func<string, string> formatFinalInput,
        Func<string, CancellationToken, Task<int>> countFinalTokens,
        TeiInputBudget budget,
        CancellationToken ct,
        Func<ProjectedRetrievalChunk, bool>? isStructuredChunk = null,
        Func<ProjectedRetrievalChunk, IReadOnlyList<ProjectedRetrievalChunk>?>? splitStructuredChunk = null,
        Func<string, string>? normalizeBasisInput = null,
        Func<ProjectedRetrievalChunk, string>? buildSourceBasis = null)
    {
        if (budget.MaximumInputTokens < 1) throw new ArgumentOutOfRangeException(nameof(budget));
        var output = new List<ProjectedRetrievalChunk>(chunks.Count);
        var inputs = new Dictionary<int, string>();
        var bases = new Dictionary<int, string>();
        var provenance = new Dictionary<int, EmbeddingInputProvenance>();
        var splitParents = new HashSet<int>();
        var reducedInputs = 0;
        var requests = 0;
        var maximumObserved = 0;
        var characterRefusals = 0;
        foreach (var original in chunks)
        {
            if (!shouldEmbed(original)) output.Add(original with { ChunkIndex = output.Count });
            else await PreparePartAsync(original, original.ChunkIndex, 0, original.Text.Length, false, false, 0);
        }
        return new(output, inputs, provenance, splitParents.Count, reducedInputs, maximumObserved, requests, bases, characterRefusals);

        async Task<int?> CountAsync(string input)
        {
            ct.ThrowIfCancellationRequested();
            requests++;
            int count;
            try { count = await countFinalTokens(input, ct); }
            catch (TeiRequestException refusal) when (refusal.ErrorCode == "tei_input_character_limit_exceeded")
            {
                // This is a refused measurement, not zero tokens. Only the
                // known typed character refusal permits lossless partitioning.
                characterRefusals++;
                return null;
            }
            if (count < 1) throw new InvalidDataException("tei_tokenize_count_invalid");
            maximumObserved = Math.Max(maximumObserved, count);
            return count;
        }

        async Task PreparePartAsync(ProjectedRetrievalChunk part, int parentIndex, int? start, int? length,
            bool partitioned, bool structuredPartition, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 32) throw new TeiRequestException("tei_input_partition_depth_exceeded");
            var basisInput = buildContext(part);
            basisInput = normalizeBasisInput?.Invoke(basisInput) ?? basisInput;
            var contextualInput = formatFinalInput(basisInput);
            var count = await CountAsync(contextualInput);
            var finalInput = contextualInput;
            var reduced = false;
            if (!count.HasValue || count.Value > budget.MaximumInputTokens)
            {
                // This removes auxiliary embedding context only. Text, SQL contextual
                // entries, canonical observations and source citations are not truncated.
                var literalBasis = buildSourceBasis?.Invoke(part) ?? part.Text;
                var sourceBasis = normalizeBasisInput?.Invoke(literalBasis) ?? literalBasis;
                finalInput = formatFinalInput(sourceBasis);
                reduced = !string.Equals(finalInput, contextualInput, StringComparison.Ordinal);
                count = reduced ? await CountAsync(finalInput) : count;
                basisInput = sourceBasis;
            }
            if (count is { } measuredTokens && measuredTokens <= budget.MaximumInputTokens)
            {
                var index = output.Count;
                var admission = new EmbeddingInputProvenance(parentIndex, start, length, measuredTokens, reduced, partitioned, structuredPartition);
                output.Add(part with { ChunkIndex = index, EmbeddingAdmission = admission });
                inputs.Add(index, finalInput);
                bases.Add(index, basisInput);
                provenance.Add(index, admission);
                if (reduced) reducedInputs++;
                return;
            }

            splitParents.Add(parentIndex);
            if (isStructuredChunk?.Invoke(part) == true)
            {
                var children = splitStructuredChunk?.Invoke(part);
                if (children is null || children.Count < 2
                    || children.Any(child => string.IsNullOrWhiteSpace(child.Text)
                        || string.Equals(child.Text, part.Text, StringComparison.Ordinal)))
                    throw new TeiRequestException("tei_structured_input_indivisible", maximumTokens: budget.MaximumInputTokens, observedTokens: count);
                foreach (var child in children)
                    await PreparePartAsync(child, parentIndex, null, null, true, true, depth + 1);
                return;
            }

            var halves = TeiLiteralInputPartitioner.Split(part.Text);
            if (!halves.HasValue)
                throw new TeiRequestException("tei_source_element_exceeds_input_budget", maximumTokens: budget.MaximumInputTokens, observedTokens: count);
            foreach (var (text, delta) in new[] { (halves.Value.Left, 0), (halves.Value.Right, halves.Value.Left.Length) })
            {
                var child = part with
                {
                    Text = text,
                    TokenCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                    Checksum = SHA256.HashData(Encoding.UTF8.GetBytes(text))
                };
                // Original document offsets and IDs remain conservative parent provenance.
                // The exact literal fragment range is explicit in separate stage metadata.
                await PreparePartAsync(child, parentIndex, start + delta, text.Length, true, structuredPartition, depth + 1);
            }
        }
    }
}
