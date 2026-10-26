using UglyToad.PdfPig;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class PdfNativeWordObservationTests
{
    [Fact]
    public void Extraction_hashes_the_same_open_stream_and_leaves_its_lifetime_to_the_caller()
    {
        using var source = new NativeTableCoverageFixture(true);
        using var stream = new MemoryStream(source.PdfBytes);

        var extraction = PdfExtractor.Extract(stream, default);

        Assert.True(stream.CanRead);
        Assert.Equal(source.SourceSha, Assert.Single(extraction.Pages).NativeWordObservation!.SourceSha256);
    }

    [Fact]
    public void A_change_on_the_open_stream_is_detected_by_the_final_hash()
    {
        using var source = new NativeTableCoverageFixture(true);
        var bytes = source.PdfBytes.Concat(System.Text.Encoding.ASCII.GetBytes("\n% A\n")).ToArray();
        using var stream = new ChangeAfterFirstHashStream(bytes);

        var error = Assert.Throws<InvalidDataException>(() => PdfExtractor.Extract(stream, default));

        Assert.Equal("native_pdf_source_changed_on_open_handle", error.Message);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(2, 100, "page_word_limit")]
    [InlineData(100, 2, "document_word_limit")]
    public void Word_extraction_refusal_is_bounded_and_never_returns_an_empty_success(int pageLimit, int documentLimit, string reason)
    {
        using var source = new NativeTableCoverageFixture(true);
        using var pdf = PdfDocument.Open(source.PdfBytes);

        var observed = PdfNativeWordExtractor.Extract(pdf.GetPage(1), source.SourceSha, documentLimit, default, pageLimit);

        Assert.Equal("refused", observed.Availability);
        Assert.Equal(reason, observed.Reason);
        Assert.Null(observed.ObservedWordCount);
        Assert.Empty(observed.Words);
        Assert.InRange(observed.InspectedWordCount, 0, Math.Min(pageLimit, documentLimit));
    }

    [Fact]
    public void Source_hashing_observes_cancellation_during_reads()
    {
        using var source = new NativeTableCoverageFixture(true);
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelAfterReadStream(source.PdfBytes, cancellation);

        Assert.Throws<OperationCanceledException>(() => PdfExtractor.Extract(stream, cancellation.Token));
        Assert.True(stream.CanRead);
    }

    private sealed class CancelAfterReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var result = base.Read(buffer, offset, count);
            cancellation.Cancel();
            return result;
        }
    }

    private sealed class ChangeAfterFirstHashStream(byte[] bytes) : MemoryStream(bytes, 0, bytes.Length, true, true)
    {
        private bool firstHashComplete;
        private bool changed;
        private void Change()
        {
            if (!firstHashComplete || changed) return;
            GetBuffer()[checked((int)Length - 2)] = (byte)'B'; // A trailing PDF comment remains syntactically valid.
            changed = true;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Change();
            var result = base.Read(buffer, offset, count);
            if (result == 0) firstHashComplete = true;
            return result;
        }
        public override int Read(Span<byte> buffer) { Change(); return base.Read(buffer); }
        public override int ReadByte() { Change(); return base.ReadByte(); }
    }
}
