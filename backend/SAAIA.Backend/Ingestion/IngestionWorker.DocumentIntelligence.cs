sealed partial class IngestionWorker
{
    internal static bool ResolveUseDocling(DocumentIntelligenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            return false;
        if (string.Equals(options.Provider?.Trim(), "docling", StringComparison.OrdinalIgnoreCase))
            return true;

        throw new InvalidOperationException(
            $"Unsupported document-intelligence provider '{options.Provider}'.");
    }

    internal static int ResolveDoclingTimingCount(
        DoclingConvertResponse? response,
        string timingName)
    {
        if (response is null
            || !response.Timings.TryGetValue(timingName, out var timing))
        {
            return 0;
        }

        return Math.Max(0, timing.Count);
    }

    internal static long ResolveDoclingTimingMs(
        DoclingConvertResponse response,
        string timingName)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.Timings.TryGetValue(timingName, out var timing))
            return 0;

        var seconds = timing.Times
            .Where(static value => double.IsFinite(value) && value >= 0)
            .Sum();
        return (long)Math.Round(seconds * 1_000, MidpointRounding.AwayFromZero);
    }

    internal static PdfOcrDiagnostics BuildDoclingOcrDiagnostics(
        DocumentIntelligenceOptions options,
        DoclingConvertResponse response,
        PdfExtractionResult extraction)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(extraction);
        var attemptedPageCount = options.DoOcr
            ? ResolveDoclingTimingCount(response, "ocr")
            : 0;
        return new(
            Mode: "docling_auto",
            CandidatePageCount: 0,
            AttemptedPageCount: attemptedPageCount,
            SkippedPageCount: 0,
            MaxPages: extraction.Pages.Count,
            CandidatePages: [],
            AttemptedPages: [],
            SkippedPages: [],
            PagesWithOcrText: [],
            PagesWithNovelText: [],
            TimeoutSeconds: options.TimeoutSeconds,
            AppliedReason: attemptedPageCount > 0
                ? "docling_ocr_stage_completed"
                : options.DoOcr
                    ? "docling_ocr_stage_not_observed"
                    : "docling_ocr_disabled",
            CoverageStatus: options.DoOcr ? "engine_managed" : "disabled");
    }
}
