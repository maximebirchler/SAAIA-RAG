using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>OCR candidates use the existing canonical artifact, never a parallel evidence store.</summary>
internal static class LegacyOcrObservationProjector
{
    internal static IReadOnlyList<CanonicalBlock> Project(LegacyCanonicalProjectionContext context,
        ExtractedPdfPage page)
    {
        return (page.OcrObservations ?? []).Select((observation, index) =>
        {
            var id = CanonicalStableId.Create("ocr-observation", context.SourceSha256,
                page.PageNumber.ToString(CultureInfo.InvariantCulture), index.ToString(CultureInfo.InvariantCulture),
                observation.Engine, Hash(observation.RawText));
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sourcePage"] = page.PageNumber.ToString(CultureInfo.InvariantCulture),
                ["decision"] = "retained_source_observation",
                ["sectionAssociation"] = "not_asserted",
                ["readingOrder"] = "engine_output_unverified",
                ["engineVersionStatus"] = "not_attested",
                ["coordinateMapping"] = "raster_pixels_not_verified_pdf_polygon"
            };
            if (observation.RawTsv is not null)
                attributes["rawTsv"] = observation.RawTsv;
            if (observation.ImageWidth.HasValue && observation.ImageHeight.HasValue)
            {
                attributes["rasterWidth"] = observation.ImageWidth.Value.ToString(CultureInfo.InvariantCulture);
                attributes["rasterHeight"] = observation.ImageHeight.Value.ToString(CultureInfo.InvariantCulture);
            }
            return new CanonicalBlock
            {
                BlockId = id, BlockType = "ocr_source_observation", Ordinal = index + 1, ReadingOrder = index + 1,
                Text = Variants(observation.RawText),
                QualityFlags = ["ocr_source_observation", "physical_page_only_anchor",
                    "ocr_region_association_unverified", "ocr_reading_order_unverified",
                    observation.HasLowConfidenceWords ? "ocr_low_confidence_observation" : "ocr_confidence_not_calibrated"],
                Provenance = Provenance(context, observation, attributes),
                Spans = observation.Words.GroupBy(static word => (word.Block, word.Paragraph, word.Line))
                    .Select((line, lineIndex) => new CanonicalSpan
                    {
                        SpanId = CanonicalStableId.Create("ocr-line", id, lineIndex.ToString(CultureInfo.InvariantCulture)),
                        Ordinal = lineIndex,
                        Text = Variants(string.Join(' ', line.Select(static word => word.Text))),
                        Provenance = Provenance(context, observation, new(StringComparer.Ordinal)
                        {
                            ["rasterWordBounds"] = JsonSerializer.Serialize(line.Select(static word => new
                            { word.Left, word.Top, word.Width, word.Height })),
                            ["coordinateMapping"] = "raster_pixels_not_verified_pdf_polygon"
                        }),
                        Words = line.Select((word, wordIndex) => new CanonicalWord
                        {
                            WordId = CanonicalStableId.Create("ocr-word", id,
                                lineIndex.ToString(CultureInfo.InvariantCulture), wordIndex.ToString(CultureInfo.InvariantCulture)),
                            Ordinal = wordIndex, Text = word.Text, Confidence = word.Confidence
                        }).ToList()
                    }).ToList()
            };
        }).ToList();
    }

    private static CanonicalExtractionProvenance Provenance(LegacyCanonicalProjectionContext context,
        PdfOcrPageObservation observation, Dictionary<string, string> attributes) => new()
    {
        StageId = context.StageId, Method = "ocr_source_observation", Engine = observation.Engine,
        EngineVersion = "not_attested", Language = observation.Language, Attributes = attributes
    };

    private static CanonicalTextVariants Variants(string raw) => new()
    {
        Raw = raw, RawSha256 = Hash(raw), Canonical = PdfTextSanitizer.ForStorage(raw),
        Normalized = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
        // The selected page already contains retained observations. Do not index a second copy.
        Retrieval = "", Display = PdfTextSanitizer.ForStorage(raw)
    };

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
