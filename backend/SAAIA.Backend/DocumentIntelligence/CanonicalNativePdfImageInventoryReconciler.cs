using System.Globalization;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record NativePdfImageInventorySummary(
    bool Enabled,
    int NativePageCount,
    int CandidateImageCount,
    int AddedFigureCount,
    int EnrichedPageCount,
    long DurationMs,
    string EngineVersion);

internal static class CanonicalNativePdfImageInventoryReconciler
{
    internal const string StageId = "native_pdf_image_inventory";
    internal const string FigureType = "embedded_raster";
    internal const string QualityFlag = "canonical_native_pdf_image_inventory";

    public static NativePdfImageInventorySummary Apply(
        CanonicalDocument document,
        PdfExtractionResult? nativeExtraction,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var engineVersion = ResolvePdfPigVersion();
        if (!enabled || nativeExtraction is null)
        {
            started.Stop();
            return new(
                enabled,
                nativeExtraction?.Pages.Count ?? 0,
                0,
                0,
                0,
                started.ElapsedMilliseconds,
                engineVersion);
        }

        var canonicalPages = document.Pages.ToDictionary(
            static page => page.PageNumber);
        var candidateImageCount = 0;
        var addedFigureCount = 0;
        var enrichedPageCount = 0;

        foreach (var nativePage in nativeExtraction.Pages
                     .OrderBy(static page => page.PageNumber))
        {
            if (!canonicalPages.TryGetValue(
                    nativePage.PageNumber,
                    out var canonicalPage))
            {
                continue;
            }

            var images = nativePage.NativeImageRegions
                ?? Array.Empty<ExtractedPdfImageRegion>();
            candidateImageCount += images.Count;
            var addedOnPage = 0;
            foreach (var image in images.OrderBy(static image => image.SourceIndex))
            {
                var polygon = ToCanonicalPolygon(
                    image,
                    nativePage.WidthPoints,
                    nativePage.HeightPoints);
                if (polygon is null)
                    continue;

                var figureId = CanonicalStableId.Create(
                    "figure",
                    document.Source.Sha256,
                    StageId,
                    nativePage.PageNumber.ToString(CultureInfo.InvariantCulture),
                    image.SourceIndex.ToString(CultureInfo.InvariantCulture),
                    image.Left.ToString("R", CultureInfo.InvariantCulture),
                    image.Right.ToString("R", CultureInfo.InvariantCulture),
                    image.Top.ToString("R", CultureInfo.InvariantCulture),
                    image.Bottom.ToString("R", CultureInfo.InvariantCulture));
                if (canonicalPage.Figures.Any(figure =>
                        string.Equals(
                            figure.FigureId,
                            figureId,
                            StringComparison.Ordinal)))
                {
                    continue;
                }

                var ordinal = canonicalPage.Figures.Count == 0
                    ? 0
                    : canonicalPage.Figures.Max(
                        static figure => figure.Ordinal) + 1;
                canonicalPage.Figures.Add(new()
                {
                    FigureId = figureId,
                    FigureType = FigureType,
                    Ordinal = ordinal,
                    Polygon = polygon,
                    Provenance = new()
                    {
                        StageId = StageId,
                        Method = "native_pdf_image_placement_inventory",
                        Engine = "PdfPig",
                        EngineVersion = engineVersion,
                        Attributes = new(StringComparer.Ordinal)
                        {
                            ["sourceType"] = "native_pdf_embedded_image",
                            ["sourcePage"] = nativePage.PageNumber.ToString(
                                CultureInfo.InvariantCulture),
                            ["sourceImageIndex"] = image.SourceIndex.ToString(
                                CultureInfo.InvariantCulture),
                            ["widthInSamples"] = image.WidthInSamples.ToString(
                                CultureInfo.InvariantCulture),
                            ["heightInSamples"] = image.HeightInSamples.ToString(
                                CultureInfo.InvariantCulture),
                            ["semanticDecisionOwner"] = "llm_client"
                        }
                    }
                });
                addedFigureCount++;
                addedOnPage++;
            }

            if (addedOnPage == 0)
                continue;
            if (!canonicalPage.QualityFlags.Contains(
                    QualityFlag,
                    StringComparer.Ordinal))
            {
                canonicalPage.QualityFlags.Add(QualityFlag);
            }
            enrichedPageCount++;
        }

        CanonicalContractValidator.ValidateOrThrow(document);
        started.Stop();
        return new(
            true,
            nativeExtraction.Pages.Count,
            candidateImageCount,
            addedFigureCount,
            enrichedPageCount,
            started.ElapsedMilliseconds,
            engineVersion);
    }

    private static CanonicalPolygon? ToCanonicalPolygon(
        ExtractedPdfImageRegion image,
        double? pageWidth,
        double? pageHeight)
    {
        if (!image.HasUsableGeometry
            || pageWidth is not > 0
            || pageHeight is not > 0
            || !double.IsFinite(pageWidth.Value)
            || !double.IsFinite(pageHeight.Value))
        {
            return null;
        }

        var left = Clamp01(Math.Min(image.Left, image.Right) / pageWidth.Value);
        var right = Clamp01(Math.Max(image.Left, image.Right) / pageWidth.Value);
        var top = Clamp01(
            (pageHeight.Value - Math.Max(image.Top, image.Bottom))
            / pageHeight.Value);
        var bottom = Clamp01(
            (pageHeight.Value - Math.Min(image.Top, image.Bottom))
            / pageHeight.Value);
        if (right <= left || bottom <= top)
            return null;

        return new()
        {
            Points =
            [
                new() { X = left, Y = top },
                new() { X = right, Y = top },
                new() { X = right, Y = bottom },
                new() { X = left, Y = bottom }
            ]
        };
    }

    private static double Clamp01(double value)
        => Math.Clamp(value, 0d, 1d);

    private static string ResolvePdfPigVersion()
        => typeof(UglyToad.PdfPig.PdfDocument)
            .Assembly
            .GetName()
            .Version?
            .ToString()
            ?? "unknown";
}
