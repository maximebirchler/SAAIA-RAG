using System.Security.Cryptography;
using UglyToad.PdfPig.Content;

internal sealed record PdfNativeObservedWord(int Ordinal, string Text,
    double Left, double Right, double Top, double Bottom);

internal sealed record PdfNativeWordObservation(string SourceSha256, string Availability,
    IReadOnlyList<PdfNativeObservedWord> Words, int? ObservedWordCount,
    string? Reason = null, int InspectedWordCount = 0)
{
    public string Algorithm => "pdfpig_default_word_geometry_v1";
    public string CoordinateOrigin => "BOTTOMLEFT";
    public string CoordinateFrameAvailability { get; init; } = "unavailable";
    public int? PageRotationDegrees { get; init; }
    public double? CropBoxLeft { get; init; }
    public double? CropBoxBottom { get; init; }
    public double? MediaBoxLeft { get; init; }
    public double? MediaBoxBottom { get; init; }
}

internal static class PdfNativeWordExtractor
{
    internal const int MaximumWordsPerPage = 100_000;
    internal const int MaximumWordsPerDocument = 1_000_000;

    internal static PdfNativeWordObservation Extract(Page page, string sourceSha,
        int remainingDocumentWords, CancellationToken ct, int maximumWordsPerPage = MaximumWordsPerPage,
        UglyToad.PdfPig.Structure? sourceStructure = null)
    {
        ct.ThrowIfCancellationRequested();
        var frame = PdfNativeCoordinateFrame.Observe(page, sourceStructure, ct);
        if (remainingDocumentWords <= 0 || maximumWordsPerPage <= 0)
            return Observation("refused", [], null, "document_word_limit");
        var limit = Math.Min(remainingDocumentWords, maximumWordsPerPage);
        var words = new List<PdfNativeObservedWord>();
        var inspected = 0;
        var invalid = false;
        try
        {
            foreach (var word in page.GetWords())
            {
                ct.ThrowIfCancellationRequested();
                // One look-ahead establishes a refusal; no excess word is
                // retained or reported as a complete word-count observation.
                if (inspected == limit)
                    return Observation("refused", [], null,
                        remainingDocumentWords <= maximumWordsPerPage ? "document_word_limit" : "page_word_limit", inspected);
                var ordinal = inspected++;
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                var box = word.BoundingBox;
                if (!double.IsFinite(box.Left) || !double.IsFinite(box.Right)
                    || !double.IsFinite(box.Top) || !double.IsFinite(box.Bottom)
                    || box.Right <= box.Left || box.Top <= box.Bottom)
                {
                    invalid = true;
                    continue;
                }
                words.Add(new(ordinal, word.Text, box.Left, box.Right, box.Top, box.Bottom));
            }
            return Observation(invalid ? "partial" : "available", words, inspected,
                invalid ? "invalid_word_geometry" : null, inspected);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Missing observations are not silently represented by an empty
            // successful inventory. No private exception text is retained.
            return Observation(inspected == 0 ? "unavailable" : "partial", words, null,
                "native_word_extraction_error", inspected);
        }

        PdfNativeWordObservation Observation(string availability, IReadOnlyList<PdfNativeObservedWord> observedWords,
            int? observedWordCount, string? reason = null, int inspectedWordCount = 0)
            => new(sourceSha, availability, observedWords, observedWordCount, reason, inspectedWordCount)
            {
                CoordinateFrameAvailability = frame.Availability,
                PageRotationDegrees = frame.RotationDegrees,
                CropBoxLeft = frame.CropBoxLeft,
                CropBoxBottom = frame.CropBoxBottom,
                MediaBoxLeft = frame.MediaBoxLeft,
                MediaBoxBottom = frame.MediaBoxBottom
            };
    }

    internal static string HashOpenedSource(Stream source, CancellationToken ct)
    {
        if (!source.CanRead || !source.CanSeek)
            throw new InvalidDataException("native_pdf_source_stream_must_be_readable_and_seekable");
        var position = source.Position;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        try
        {
            source.Position = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                digest.AppendData(buffer, 0, count);
            }
            ct.ThrowIfCancellationRequested();
            return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        }
        finally { source.Position = position; }
    }
}
