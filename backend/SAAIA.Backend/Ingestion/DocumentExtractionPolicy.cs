internal static class DocumentExtractionPolicy
{
    // Typography can acquire the same PDF handle for its auxiliary channel without
    // granting its result to policies/audits that did not request native extraction.
    internal static bool RequiresExistingNativeAudit(DocumentIntelligenceOptions options)
        => options.NativeTextCoverageReconciliationEnabled || options.NativePdfImageInventoryEnabled
            || options.RegionalTableAlternativeReadingsEnabled;

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
        PdfExtractionResult extraction,
        bool forceOcr = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(extraction);
        response.Timings.TryGetValue("ocr", out var timing);
        return new PdfOcrDiagnostics(
            Mode: forceOcr ? "docling_force" : "docling_auto",
            CandidatePageCount: 0,
            AttemptedPageCount: 0,
            SkippedPageCount: 0,
            MaxPages: extraction.Pages.Count,
            CandidatePages: [], AttemptedPages: [], SkippedPages: [],
            PagesWithOcrText: [], PagesWithNovelText: [],
            TimeoutSeconds: options.TimeoutSeconds,
            AppliedReason: !options.DoOcr ? "docling_ocr_disabled"
                : timing is null ? "docling_ocr_task_not_observed"
                : "docling_ocr_task_observed_output_delta_unknown",
            CoverageStatus: options.DoOcr ? "engine_managed_page_counts_unknown" : "disabled")
        {
            PageCountsAvailable = !options.DoOcr,
            EngineTimingCount = timing is null ? null : Math.Max(0, timing.Count),
            EngineTimingScope = timing?.Scope
        };
    }

    internal static bool ResolveDoclingForceOcr(
        DocumentIntelligenceOptions options,
        PdfExtractionResult? nativeTextLayerExtraction)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ForceOcr)
            return true;

        return options.DoOcr
            && nativeTextLayerExtraction is not null
            && PdfOcrTextExtractor.ShouldForceOcrNativeText(
                nativeTextLayerExtraction);
    }
}
