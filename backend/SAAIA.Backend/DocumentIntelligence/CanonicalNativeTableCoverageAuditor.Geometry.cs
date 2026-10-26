using SAAIA.Contracts.DocumentIntelligence;

internal static partial class CanonicalNativeTableCoverageAuditor
{
    private readonly record struct Bounds(double Left, double Top, double Right, double Bottom)
    {
        internal bool Contains(Bounds other) => other.Left >= Left && other.Right <= Right
            && other.Top >= Top && other.Bottom <= Bottom;
        internal bool Intersects(Bounds other, double tolerance) => other.Right >= Left - tolerance
            && other.Left <= Right + tolerance && other.Bottom >= Top - tolerance && other.Top <= Bottom + tolerance;
    }

    private static bool TryBounds(CanonicalPolygon? polygon, double width, double height, out Bounds bounds)
    {
        bounds = default;
        if (polygon?.Points is not { Count: >= 4 } points || points.Any(point => !double.IsFinite(point.X)
                || !double.IsFinite(point.Y) || point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1))
            return false;
        var left = points.Min(point => point.X); var right = points.Max(point => point.X);
        var top = points.Min(point => point.Y); var bottom = points.Max(point => point.Y);
        if (left >= right || top >= bottom || points.Select(point => (point.X, point.Y)).Distinct().Count() != 4
            || points.Any(point => point.X != left && point.X != right || point.Y != top && point.Y != bottom))
            return false; // No bounding-box substitute for an unsupported polygon.
        bounds = new(left * width, top * height, right * width, bottom * height);
        return true;
    }

    private static bool TryWordBounds(PdfNativeObservedWord word, double width, double height, out Bounds bounds)
    {
        bounds = new(word.Left, height - word.Top, word.Right, height - word.Bottom);
        return double.IsFinite(word.Left) && double.IsFinite(word.Right) && double.IsFinite(word.Top)
            && double.IsFinite(word.Bottom) && word.Left >= -1 && word.Right <= width + 1
            && word.Bottom >= -1 && word.Top <= height + 1 && word.Right > word.Left && word.Top > word.Bottom;
    }

    private static string? PageRefusal(CanonicalDocument document, CanonicalPage page,
        ExtractedPdfPage? native, int maximumWordsPerPage, ref int remainingDocumentWords, CancellationToken ct)
    {
        if (native?.NativeWordObservation is not { } observation) return "native_words_unavailable";
        if (!string.Equals(observation.SourceSha256, document.Source.Sha256, StringComparison.OrdinalIgnoreCase))
            return "source_identity_mismatch";
        if (observation.CoordinateFrameAvailability != "available"
            || observation.PageRotationDegrees is not { } rotation
            || observation.CropBoxLeft is not { } cropLeft || observation.CropBoxBottom is not { } cropBottom
            || observation.MediaBoxLeft is not { } mediaLeft || observation.MediaBoxBottom is not { } mediaBottom)
            return "native_coordinate_frame_unavailable";
        if (rotation != 0) return "unsupported_native_rotation";
        if (!double.IsFinite(cropLeft) || !double.IsFinite(cropBottom)
            || !double.IsFinite(mediaLeft) || !double.IsFinite(mediaBottom))
            return "native_coordinate_frame_unavailable";
        if (cropLeft != 0 || cropBottom != 0) return "unsupported_native_crop_origin";
        if (mediaLeft != 0 || mediaBottom != 0) return "unsupported_native_media_origin";
        if (observation.Availability != "available") return observation.Reason is
            "page_word_limit" or "document_word_limit" or "invalid_word_geometry" or "native_word_extraction_error"
                ? observation.Reason : "native_words_unavailable";
        if (observation.Words is null) return "native_words_unavailable";
        if (maximumWordsPerPage <= 0 || observation.Words.Count > maximumWordsPerPage) return "page_word_limit";
        if (remainingDocumentWords <= 0 || observation.Words.Count > remainingDocumentWords) return "document_word_limit";
        remainingDocumentWords -= observation.Words.Count;
        if (page.Geometry.RotationDegrees != 0) return "unsupported_rotation";
        if (page.Geometry.Unit != "pt" || native.WidthPoints is not { } width || native.HeightPoints is not { } height
            || page.Geometry.Width is not { } canonicalWidth || page.Geometry.Height is not { } canonicalHeight
            || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || !double.IsFinite(canonicalWidth) || !double.IsFinite(canonicalHeight)
            || canonicalWidth <= 0 || canonicalHeight <= 0
            || Math.Abs(canonicalWidth - width) > 1 || Math.Abs(canonicalHeight - height) > 1)
            return "invalid_page_geometry";
        var ordinals = new HashSet<int>();
        foreach (var word in observation.Words)
        {
            ct.ThrowIfCancellationRequested();
            if (word is null || word.Ordinal < 0 || !ordinals.Add(word.Ordinal) || !TryWordBounds(word, width, height, out _))
                return "invalid_word_geometry";
        }
        if (observation.GlyphGeometryAvailability != "available")
            return observation.GlyphGeometryReason ?? "native_glyph_geometry_unavailable";
        return null;
    }
}
