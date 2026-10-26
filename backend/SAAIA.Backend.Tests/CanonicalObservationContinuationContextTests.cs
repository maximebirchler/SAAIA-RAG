using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CanonicalObservationContinuationContextTests
{
    [Fact]
    public void Complete_observation_and_ordered_context_disambiguate_two_known_continuation_words()
    {
        var primary = new[]
        {
            "The operator recorded the costs, including handling charges before release.",
            "The package moved into storage to remain dry."
        };
        var document = NativeContinuationFixture.Document(primary);
        const string native = "The operator recorded the costs, in-\n"
            + "cluding handling charges before release. The package moved into storage to remain dry.";

        NativeContinuationFixture.Reconcile(document, native);

        // Both including and into exist as complete primary words. Only the
        // former preserves both complete observations and their local order;
        // the independent 'to' occurrence must not be consumed as its suffix.
        Assert.Equal(primary, NativeContinuationFixture.Units(document).Select(unit => unit.Text));
        Assert.Equal(primary, document.Pages[0].Blocks.Take(2).Select(block => block.Text.Raw));
    }

    [Fact]
    public void Complete_observation_and_ordered_context_choose_the_correct_repeated_suffix_occurrence()
    {
        const string primary = "The amendment applies to subpart I of part 1 under the regulation.";
        var document = NativeContinuationFixture.Document(primary);

        NativeContinuationFixture.Reconcile(document,
            "The amendment applies to sub-\npart I of part 1 under the regulation.");

        // Both next-line occurrences spell 'part'. The wrong occurrence would
        // change the literal ordered observation to 'subpart I of 1 ...'.
        Assert.Equal(primary, Assert.Single(NativeContinuationFixture.Units(document)).Text);
        Assert.Equal(primary, document.Pages[0].Blocks[0].Text.Raw);
    }

    [Theory]
    [InlineData("Do not open the vessel at -5 °C.")]
    [InlineData("The measured limit is 1,5 bar, not 15 bar.")]
    public void Ordered_continuation_context_never_hides_a_genuine_additional_literal_fact(string additional)
    {
        const string primary = "The amendment applies to subpart I of part 1 under the regulation.";
        var document = NativeContinuationFixture.Document(primary);
        NativeContinuationFixture.Reconcile(document,
            "The amendment applies to sub-\npart I of part 1 under the regulation.\n" + additional);

        Assert.Contains(NativeContinuationFixture.Units(document), unit => unit.Text.Contains(additional, StringComparison.Ordinal));
        Assert.Equal(primary, document.Pages[0].Blocks[0].Text.Raw);
        Assert.Contains(document.Pages[0].Blocks, block => block.BlockType == "native_text_recovery"
            && block.Text.Raw.Contains(additional, StringComparison.Ordinal));
    }
}

internal static class NativeContinuationFixture
{
    internal static IReadOnlyList<ExtractedDocumentUnit> Units(CanonicalDocument document)
        => CanonicalExtractionViewProjector.ProjectUnits(document, new DoclingDocument());

    internal static void Reconcile(CanonicalDocument document, string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var page = new ExtractedPdfPage(1, text, words, text.Length, SHA256.HashData(Encoding.UTF8.GetBytes(text)),
            PdfPageExtractionQuality.FromText(text, words, text.Length));
        CanonicalNativeTextCoverageReconciler.Apply(document,
            new PdfExtractionResult([], [page], PdfExtractionQualitySummary.FromPages([page])));
    }

    internal static CanonicalDocument Document(params string[] texts) => new()
    {
        DocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        RevisionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Source = new() { Sha256 = new string('a', 64), MediaType = "application/pdf" },
        ManifestSha256 = new string('b', 64), PageCount = 1,
        Pages = [new() { PageNumber = 1, Geometry = new() { Width = 600, Height = 800, Unit = "pt" },
            Blocks = texts.Select((text, index) => Block("primary_" + index, "text", text, index)).ToList() }]
    };

    internal static CanonicalBlock Block(string id, string type, string text, int order) => new()
    {
        BlockId = id, BlockType = type, Ordinal = order, ReadingOrder = order,
        Text = new() { Raw = text, Canonical = text, Display = text, Retrieval = text,
            Normalized = text.ToLowerInvariant(), RawSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() },
        Provenance = new() { Engine = type.StartsWith("native_", StringComparison.Ordinal) ? "PdfPig" : "docling" }
    };
}
