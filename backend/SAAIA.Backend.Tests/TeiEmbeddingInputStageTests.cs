using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;

public sealed class TeiEmbeddingInputStageTests
{
    [Fact]
    public async Task Tokenizer_character_limit_on_context_reduces_context_without_faking_a_token_count()
    {
        var result = await IngestionEmbeddingInputStage.PrepareAsync([Chunk(0, "whole source")], _ => true,
            _ => new string('c', 200), text => text, CharacterBoundedCount, new(512, 1), CancellationToken.None);
        Assert.Single(result.Chunks);
        Assert.Equal("whole source", result.Chunks[0].Text);
        Assert.Equal("whole source", result.InputsByChunkIndex[0]);
        Assert.True(result.ProvenanceByChunkIndex[0].ContextReduced);
        Assert.Equal(14, result.ProvenanceByChunkIndex[0].InputTokens);
        Assert.Equal(14, result.MaxObservedTokens);
        Assert.Equal(1, result.CharacterLimitRefusals);
    }

    [Fact]
    public async Task Tokenizer_character_limit_on_source_partitions_the_entire_source_including_its_tail()
    {
        var source = new string('x', 130) + " END";
        var result = await IngestionEmbeddingInputStage.PrepareAsync([Chunk(0, source)], _ => true,
            chunk => chunk.Text, text => text, CharacterBoundedCount, new(512, 1), CancellationToken.None);
        Assert.Equal(source, string.Concat(result.Chunks.Select(chunk => chunk.Text)));
        Assert.Equal(2, result.Chunks.Count);
        Assert.All(result.ProvenanceByChunkIndex.Values, admission => Assert.InRange(admission.InputTokens, 1, 102));
        Assert.EndsWith(" END", result.Chunks[^1].Text);
        Assert.Equal(1, result.CharacterLimitRefusals);
    }

    [Fact]
    public async Task Unknown_tokenizer_413_is_not_treated_as_a_measured_character_or_token_limit()
    {
        await Assert.ThrowsAsync<TeiRequestException>(() => IngestionEmbeddingInputStage.PrepareAsync([Chunk(0, "source")],
            _ => true, chunk => chunk.Text, text => text,
            (_, _) => throw new TeiRequestException("tei_tokenize_http_error", System.Net.HttpStatusCode.RequestEntityTooLarge),
            new(512, 1), CancellationToken.None));
    }

    private static Task<int> CharacterBoundedCount(string text, CancellationToken ct)
        => text.Length > 100 ? throw new TeiRequestException("tei_input_character_limit_exceeded") : Count(text, ct);

    [Fact]
    public async Task Known_551_token_shape_is_partitioned_before_publication_and_keeps_all_source()
    {
        // One whitespace word can produce hundreds of model tokens. This protocol oracle
        // emulates the observed server count, not an alternative production tokenizer.
        var source = new string('\u03bb', 549);
        var original = Chunk(0, source);
        var result = await Prepare([original], 512);
        Assert.Equal(source, string.Concat(result.Chunks.Select(chunk => chunk.Text)));
        Assert.Equal(2, result.Chunks.Count);
        Assert.Equal(1, result.SplitChunks);
        Assert.Equal(551, result.MaxObservedTokens);
        Assert.All(result.ProvenanceByChunkIndex.Values, item => Assert.InRange(item.InputTokens, 1, 512));
        Assert.All(result.Chunks, chunk => Assert.Equal(original.CanonicalBlockIds, chunk.CanonicalBlockIds));
        Assert.All(result.Chunks, chunk => Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Text)), chunk.Checksum));
        Assert.Equal(Enumerable.Range(0, result.Chunks.Count), result.InputsByChunkIndex.Keys);
    }

    [Fact]
    public async Task Exactly_the_runtime_limit_is_admitted_and_source_is_unchanged()
    {
        var original = Chunk(0, new string('x', 510));
        var result = await Prepare([original], 512);
        Assert.Single(result.Chunks);
        Assert.Equal(original with { EmbeddingAdmission = result.ProvenanceByChunkIndex[0] }, result.Chunks[0]);
        Assert.Equal(512, result.ProvenanceByChunkIndex[0].InputTokens);
        Assert.Equal(0, result.SplitChunks);
    }

    [Fact]
    public async Task Context_only_overflow_is_explicit_without_mutating_source_or_published_context()
    {
        var original = Chunk(0, "entire source");
        var context = new string('c', 600) + original.Text;
        var result = await Prepare([original], 512, _ => context);
        Assert.Single(result.Chunks);
        Assert.Equal(original with { EmbeddingAdmission = result.ProvenanceByChunkIndex[0] }, result.Chunks[0]);
        Assert.Equal(original.Text, result.InputsByChunkIndex[0]);
        Assert.True(result.ProvenanceByChunkIndex[0].ContextReduced);
        Assert.Equal(1, result.ContextReducedInputs);
        Assert.Equal(0, result.SplitChunks);
        Assert.EndsWith(original.Text, context);
    }

    [Fact]
    public async Task Model_specific_prefix_is_included_in_every_measured_final_fragment()
    {
        var original = Chunk(0, new string('x', 502));
        var result = await Prepare([original], 512, format: text => TeiClient.FormatEmbeddingInput("intfloat/multilingual-e5-base", text, TeiClient.EmbeddingInputKind.Passage));
        Assert.Equal(2, result.Chunks.Count);
        Assert.Equal(original.Text, string.Concat(result.Chunks.Select(chunk => chunk.Text)));
        Assert.All(result.InputsByChunkIndex.Values, input =>
        {
            Assert.StartsWith("passage: ", input);
            Assert.InRange(input.EnumerateRunes().Count() + 2, 1, 512);
        });
        Assert.All(result.BasisTextsByChunkIndex, pair => Assert.Equal(result.Chunks[pair.Key].Text, pair.Value));
    }

    [Fact]
    public async Task Unicode_elements_whitespace_and_legitimate_repeated_occurrences_are_not_lost()
    {
        var text = "\ud83d\udc69\ud83c\udffd\u200d\ud83d\udd27e\u0301  \ud83d\udc69\ud83c\udffd\u200d\ud83d\udd27e\u0301\n";
        var result = await Prepare([Chunk(4, text), Chunk(9, text)], 8);
        Assert.Equal(text + text, string.Concat(result.Chunks.Select(chunk => chunk.Text)));
        Assert.Equal(2, result.ProvenanceByChunkIndex.Values.Select(item => item.ParentChunkIndex).Distinct().Count());
        Assert.All(result.Chunks, chunk =>
        {
            Assert.DoesNotContain('\ufffd', chunk.Text);
            Assert.Equal(chunk.Text, string.Concat(Elements(chunk.Text)));
        });
        foreach (var group in result.ProvenanceByChunkIndex.GroupBy(pair => pair.Value.ParentChunkIndex))
        {
            var position = 0;
            foreach (var pair in group)
            {
                Assert.Equal(position, pair.Value.SourceTextStart);
                position += pair.Value.SourceTextLength!.Value;
            }
            Assert.Equal(text.Length, position);
        }
    }

    [Fact]
    public async Task A_single_unfittable_source_element_fails_explicitly_without_truncation()
    {
        var error = await Assert.ThrowsAsync<TeiRequestException>(() => Prepare([Chunk(0, "x")], 2));
        Assert.Equal("tei_source_element_exceeds_input_budget", error.ErrorCode);
    }

    [Fact]
    public async Task Structured_table_cannot_fall_back_to_slicing_markup()
    {
        var table = Chunk(0, "<table><tr><td>complete cell</td></tr></table>");
        var error = await Assert.ThrowsAsync<TeiRequestException>(() => IngestionEmbeddingInputStage.PrepareAsync(
            [table], _ => true, chunk => chunk.Text, input => input, Count, new(12, 1), CancellationToken.None,
            _ => true, _ => null));
        Assert.Equal("tei_structured_input_indivisible", error.ErrorCode);
    }

    [Fact]
    public async Task A_structural_split_is_recounted_without_inventing_literal_source_ranges()
    {
        var original = Chunk(0, "01234567890123456789");
        var result = await IngestionEmbeddingInputStage.PrepareAsync([original], _ => true,
            chunk => chunk.Text, input => input, Count, new(14, 1), CancellationToken.None,
            _ => true, chunk => chunk.Text.Length > 10
                ? [Chunk(0, "0123456789"), Chunk(0, "0123456789")] : null);
        Assert.Equal(2, result.Chunks.Count);
        Assert.All(result.ProvenanceByChunkIndex.Values, item =>
        {
            Assert.True(item.StructuredPartition);
            Assert.Null(item.SourceTextStart);
            Assert.Null(item.SourceTextLength);
            Assert.Equal(12, item.InputTokens);
        });
    }

    [Fact]
    public async Task Tokenizer_failure_is_not_treated_as_an_empty_or_accepted_document()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => IngestionEmbeddingInputStage.PrepareAsync(
            [Chunk(0, "source")], _ => true, chunk => chunk.Text, input => input,
            (_, _) => throw new InvalidDataException("tokenizer_unavailable"), new(512, 1), CancellationToken.None));
    }

    [Fact]
    public async Task Child_eligibility_is_inherited_instead_of_dropping_a_short_source_tail()
    {
        var result = await IngestionEmbeddingInputStage.PrepareAsync([Chunk(0, "whole source must remain")],
            chunk => chunk.Text.Length > 10, chunk => chunk.Text, input => input, Count, new(10, 1), CancellationToken.None);
        Assert.Equal("whole source must remain", string.Concat(result.Chunks.Select(chunk => chunk.Text)));
        Assert.Equal(result.Chunks.Count, result.InputsByChunkIndex.Count);
    }

    [Fact]
    public async Task Clean_basis_and_exact_final_input_are_kept_distinct_even_when_context_is_reduced()
    {
        var original = Chunk(0, "source\ttext");
        var result = await IngestionEmbeddingInputStage.PrepareAsync([original], _ => true,
            _ => new string('c', 100), input => "passage: " + input, Count, new(32, 1), CancellationToken.None,
            normalizeBasisInput: input => input.Replace('\t', ' '));
        Assert.Equal("source text", result.BasisTextsByChunkIndex[0]);
        Assert.Equal("passage: source text", result.InputsByChunkIndex[0]);
        Assert.Equal("source\ttext", result.Chunks[0].Text);
        Assert.True(result.ProvenanceByChunkIndex[0].ContextReduced);
    }

    [Fact]
    public async Task Auxiliary_table_breadcrumbs_can_be_reduced_while_complete_source_grid_is_preserved()
    {
        const string grid = "<table><tr><td>exact cell</td></tr></table>";
        var original = Chunk(0, new string('b', 100) + "\n" + grid);
        var result = await IngestionEmbeddingInputStage.PrepareAsync([original], _ => true,
            chunk => chunk.Text, input => input, Count, new(64, 1), CancellationToken.None,
            isStructuredChunk: _ => true, splitStructuredChunk: _ => null, buildSourceBasis: _ => grid);
        Assert.Single(result.Chunks);
        Assert.Equal(original.Text, result.Chunks[0].Text);
        Assert.Equal(grid, result.InputsByChunkIndex[0]);
        Assert.True(result.ProvenanceByChunkIndex[0].ContextReduced);
        Assert.Equal(0, result.SplitChunks);
    }

    private static Task<PreparedEmbeddingProjection> Prepare(IReadOnlyList<ProjectedRetrievalChunk> chunks,
        int maximum, Func<ProjectedRetrievalChunk, string>? context = null, Func<string, string>? format = null)
        => IngestionEmbeddingInputStage.PrepareAsync(chunks, _ => true, context ?? (chunk => chunk.Text),
            format ?? (input => input), Count, new(maximum, 1), CancellationToken.None);
    private static Task<int> Count(string text, CancellationToken ct) => Task.FromResult(text.EnumerateRunes().Count() + 2);
    private static ProjectedRetrievalChunk Chunk(int index, string text)
        => new(index, null, null, 1, 1, text, 1, SHA256.HashData(Encoding.UTF8.GetBytes(text)),
            "docling_canonical_content_v1", CanonicalBlockIds: ["source-block"]);
    private static IEnumerable<string> Elements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) yield return enumerator.GetTextElement();
    }
}
