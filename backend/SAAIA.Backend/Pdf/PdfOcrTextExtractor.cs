using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

internal static class PdfOcrTextExtractor
{
    private const int OcrProcessOutputDiagnosticsMaxLength = 4096;

    internal static async Task<PdfExtractionResult?> TryExtractAsync(
        string pdfPath,
        IngestionOptions options,
        CancellationToken ct,
        string? languagesOverride = null,
        bool forceOcr = false)
    {
        var result = await TryExtractWithDiagnosticsAsync(
            pdfPath,
            options,
            ct,
            languagesOverride,
            forceOcr).ConfigureAwait(false);

        return result?.Extraction;
    }

    internal static async Task<PdfDocumentOcrResult?> TryExtractWithDiagnosticsAsync(
        string pdfPath, IngestionOptions options, CancellationToken ct,
        string? languagesOverride = null, bool forceOcr = false)
    {
        if (!options.OcrEnabled)
            return null;
        var timeoutSeconds = Math.Clamp(options.OcrTimeoutSeconds, 30, 7200);
        if (string.IsNullOrWhiteSpace(options.OcrCommand))
            return new(null, BuildFullDocumentOcrDiagnostics(forceOcr,
                timeoutSeconds: timeoutSeconds, failureReason: "command_missing"));
        var languages = string.IsNullOrWhiteSpace(languagesOverride)
            ? ResolveLanguagesForDocument(pdfPath, options, null) : languagesOverride.Trim();
        var tempDir = Path.Combine(Path.GetTempPath(), "saaia-ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sidecar = Path.Combine(tempDir, "ocr.txt");
            var outputPdf = Path.Combine(tempDir, "ocr.pdf");
            var process = await OcrProcessRunner.RunAsync(options.OcrCommand.Trim(),
                BuildArguments(options, pdfPath, outputPdf, sidecar, languages, forceOcr),
                TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false);
            var failure = process.Succeeded ? !File.Exists(sidecar) ? "sidecar_missing" : null
                : process.FailureReason ?? "exit_code_non_zero";
            var diagnostics = BuildFullDocumentOcrDiagnostics(forceOcr, process.ExitCode,
                process.TimedOut, timeoutSeconds, process.Stderr, failure);
            if (failure is not null)
                return new(null, diagnostics);
            var text = await OcrProcessRunner.ReadTextFileAsync(sidecar, ct).ConfigureAwait(false);
            var sidecarResult = BuildExtractionFromSidecarText(text, languages,
                forceOcr ? "ocr_sidecar_force" : "ocr_sidecar");
            // OCRmyPDF --skip-text may omit native text from its sidecar. Its output PDF
            // preserves physical pagination and is therefore the primary extraction candidate.
            var result = File.Exists(outputPdf)
                ? OcrSourceReconciler.FromProcessedPdf(PdfExtractor.Extract(outputPdf, ct), sidecarResult,
                    Path.GetFileName(options.OcrCommand.Trim()), languages)
                : sidecarResult;
            // A short recognized value is still an observation; density is assessed downstream.
            if (result.Tokens.Count == 0)
                return new(null, diagnostics with { FailureReason = "no_text" });
            return new(result with { OcrDiagnostics = diagnostics }, diagnostics);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(null, BuildFullDocumentOcrDiagnostics(forceOcr,
                timeoutSeconds: timeoutSeconds, stderr: ex.GetType().Name,
                failureReason: ex is InvalidDataException ? "output_size_limit" : "exception"));
        }
        finally { TryDeleteDirectory(tempDir); }
    }

    internal static PdfExtractionResult BuildExtractionFromSidecarText(string sidecarText)
        => BuildExtractionFromSidecarText(sidecarText, ocrLanguages: null);

    internal static PdfExtractionResult BuildExtractionFromSidecarText(string sidecarText, string? ocrLanguages)
        => BuildExtractionFromSidecarText(sidecarText, ocrLanguages, source: "ocr_sidecar");

    private static PdfExtractionResult BuildExtractionFromSidecarText(string sidecarText, string? ocrLanguages, string source)
    {
        var normalized = sidecarText.Replace("\r\n", "\n").Replace('\r', '\n');
        // One terminal separator is an engine terminator; additional empty pages are physical pages.
        if (normalized.EndsWith('\f'))
            normalized = normalized[..^1];
        var rawPages = normalized.Split('\f');
        var pages = rawPages
            .Select(static page => PdfTextSanitizer.ForStorage(page))
            .ToArray();

        if (pages.Length == 0)
            pages = [string.Empty];

        var tokens = new List<WordToken>();
        var extractedPages = new List<ExtractedPdfPage>(pages.Length);
        for (var i = 0; i < pages.Length; i++)
        {
            var text = pages[i];
            var words = SplitWords(text).ToArray();
            var pageNumber = i + 1;
            foreach (var word in words)
                tokens.Add(new WordToken(word, pageNumber));

            var quality = PdfPageExtractionQuality.FromText(text, words.Length, text.Length);
            extractedPages.Add(new ExtractedPdfPage(
                PageNumber: pageNumber,
                Text: text,
                WordCount: words.Length,
                CharCount: text.Length,
                Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(text)),
                Quality: quality with
                {
                    Signals = quality.Signals
                        .Concat(["ocr_text_extracted"])
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                },
                RawText: rawPages[i],
                OcrObservations: [new(rawPages[i], "ocrmypdf", ocrLanguages, [])]));
        }

        var summary = PdfExtractionQualitySummary.FromPages(extractedPages);
        return new PdfExtractionResult(
            tokens,
            extractedPages,
            summary with
            {
                Signals = summary.Signals
                    .Concat(["ocr_text_extracted"])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            },
            Source: source,
            OcrLanguages: string.IsNullOrWhiteSpace(ocrLanguages) ? null : ocrLanguages.Trim());
    }

    internal static bool ShouldForceOcrNativeText(PdfExtractionResult nativeExtraction)
    {
        var quality = nativeExtraction.Quality;
        if (!quality.OcrRecommended)
            return false;
        if (quality.TotalWordCount <= 0)
            return false;
        if (quality.Signals.Contains(
                "invalid_control_chars_detected",
                StringComparer.Ordinal))
        {
            return true;
        }
        if (quality.TotalCharCount < 200)
            return false;

        var averageCharsPerWord = (double)quality.TotalCharCount / Math.Max(1, quality.TotalWordCount);
        return averageCharsPerWord >= 30
            || (quality.AverageWordsPerPage < 10 && quality.AverageCharsPerPage >= 200);
    }

    internal static bool ShouldApplyOcrExtraction(
        PdfExtractionResult nativeExtraction,
        PdfExtractionResult ocrExtraction,
        bool fullDocumentOcrRecommended,
        bool forceFullDocumentOcr)
    {
        return ocrExtraction.Tokens.Count > 0
            && OcrSourceReconciler.RetainsNativePages(nativeExtraction, ocrExtraction);
    }

    internal static async Task<PdfImagePageOcrResult?> TryMergeImagePageOcrAsync(
        string pdfPath,
        IngestionOptions options,
        PdfExtractionResult nativeExtraction,
        CancellationToken ct,
        string? languagesOverride = null,
        PdfImagePageOcrCallbacks? callbacks = null)
    {
        if (!options.OcrEnabled || !options.OcrImagePageEnabled)
            return null;

        var plan = BuildImagePageOcrPlan(nativeExtraction, options);
        if (plan.CandidatePageCount == 0)
            return null;

        callbacks ??= PdfImagePageOcrCallbacks.None;
        var languages = string.IsNullOrWhiteSpace(languagesOverride)
            ? ResolveLanguagesForDocument(pdfPath, options, nativeExtraction)
            : languagesOverride.Trim();
        var pageTimeout = TimeSpan.FromSeconds(Math.Clamp(options.OcrImagePageTimeoutSeconds, 10, 900));
        var totalBudget = ResolveImagePageTotalBudget(options);
        var totalStopwatch = Stopwatch.StartNew();
        var tempDir = Path.Combine(Path.GetTempPath(), "saaia-ocr-images", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var pageText = new Dictionary<int, string>();
        var observations = new Dictionary<int, PdfOcrPageObservation>();
        var pageDiagnostics = new Dictionary<int, PdfImagePageOcrDiagnostic>();
        var attemptedPages = new List<int>(plan.AttemptedPages.Length);
        var skippedPages = new HashSet<int>(plan.SkippedPages);
        var timeBudgetExhausted = false;

        try
        {
            await callbacks.ThrowIfCancellationRequestedAsync(ct).ConfigureAwait(false);
            await callbacks.ReportAsync(0, plan.AttemptedPages.Length, ct).ConfigureAwait(false);

            for (var pageIndex = 0; pageIndex < plan.AttemptedPages.Length; pageIndex++)
            {
                await callbacks.ThrowIfCancellationRequestedAsync(ct).ConfigureAwait(false);
                if (IsImagePageTotalBudgetExceeded(totalBudget, totalStopwatch.Elapsed))
                {
                    timeBudgetExhausted = true;
                    foreach (var skippedPageNumber in plan.AttemptedPages.Skip(pageIndex))
                    {
                        skippedPages.Add(skippedPageNumber);
                        pageDiagnostics[skippedPageNumber] = new PdfImagePageOcrDiagnostic(
                            skippedPageNumber,
                            "skipped",
                            "time_budget");
                    }

                    break;
                }

                var pageNumber = plan.AttemptedPages[pageIndex];
                var pageClock = Stopwatch.StartNew();
                await callbacks.ReportAsync(pageIndex + 1, plan.AttemptedPages.Length, ct).ConfigureAwait(false);
                var imagePath = Path.Combine(tempDir, $"page-{pageNumber:D5}.png");
                var outputBase = Path.Combine(tempDir, $"page-{pageNumber:D5}");
                var outputText = outputBase + ".txt";
                var dpi = Math.Clamp(options.OcrImagePageRenderDpi, 120, 400);
                var renderTimeout = ResolveImagePageProcessTimeout(pageTimeout, totalBudget, totalStopwatch.Elapsed);
                if (renderTimeout is null)
                {
                    timeBudgetExhausted = true;
                    skippedPages.Add(pageNumber);
                    pageDiagnostics[pageNumber] = new PdfImagePageOcrDiagnostic(
                        pageNumber,
                        "skipped",
                        "time_budget");
                    continue;
                }

                attemptedPages.Add(pageNumber);
                var rendered = await RunProcessWithDiagnosticsAsync(
                    ResolveImageRendererCommand(options),
                    $"-q -dNOPAUSE -dBATCH -sDEVICE=pnggray -r{dpi} -dFirstPage={pageNumber} -dLastPage={pageNumber} -sOutputFile={QuoteArgument(imagePath)} {QuoteArgument(pdfPath)}",
                    renderTimeout.Value,
                    ct).ConfigureAwait(false);
                await callbacks.ThrowIfCancellationRequestedAsync(ct).ConfigureAwait(false);
                if (!rendered.Succeeded)
                {
                    pageDiagnostics[pageNumber] = BuildImagePageProcessDiagnostic(pageNumber, "render_failed", rendered);
                    continue;
                }

                var ocrTimeout = ResolveImagePageProcessTimeout(pageTimeout - pageClock.Elapsed, totalBudget, totalStopwatch.Elapsed);
                if (ocrTimeout is null)
                {
                    if (IsImagePageTotalBudgetExceeded(totalBudget, totalStopwatch.Elapsed))
                    {
                        timeBudgetExhausted = true;
                        skippedPages.Add(pageNumber);
                        pageDiagnostics[pageNumber] = new(pageNumber, "skipped", "time_budget");
                    }
                    else
                    {
                        pageDiagnostics[pageNumber] = new(pageNumber, "ocr_failed", "page_timeout", TimedOut: true);
                    }
                    continue;
                }

                var pageSegmentationMode = Math.Clamp(options.OcrImagePageSegmentationMode, 3, 13);
                var ocrDone = await RunProcessWithDiagnosticsAsync(
                    ResolveImageTextCommand(options),
                    $"{QuoteArgument(imagePath)} {QuoteArgument(outputBase)} -l {QuoteArgument(languages)} --psm {pageSegmentationMode} txt tsv",
                    ocrTimeout.Value,
                    ct).ConfigureAwait(false);
                await callbacks.ThrowIfCancellationRequestedAsync(ct).ConfigureAwait(false);
                if (!ocrDone.Succeeded)
                {
                    pageDiagnostics[pageNumber] = BuildImagePageProcessDiagnostic(pageNumber, "ocr_failed", ocrDone);
                    continue;
                }

                if (!File.Exists(outputText))
                {
                    pageDiagnostics[pageNumber] = new PdfImagePageOcrDiagnostic(
                        pageNumber,
                        "ocr_output_missing",
                        "output_missing",
                        ExitCode: ocrDone.ExitCode,
                        TimedOut: ocrDone.TimedOut);
                    continue;
                }

                var text = await OcrProcessRunner.ReadTextFileAsync(outputText, ct).ConfigureAwait(false);
                var cleanedText = PdfTextSanitizer.ForStorage(text);
                var outputTsv = outputBase + ".tsv";
                string? tsvText = null;
                if (File.Exists(outputTsv))
                {
                    tsvText = await OcrProcessRunner.ReadTextFileAsync(outputTsv, ct).ConfigureAwait(false);
                    cleanedText = FilterLowConfidenceImageOcrText(cleanedText, tsvText);
                }
                if (string.IsNullOrWhiteSpace(cleanedText))
                {
                    pageDiagnostics[pageNumber] = new PdfImagePageOcrDiagnostic(
                        pageNumber,
                        "ocr_empty",
                        "no_text",
                        OcrWordCount: 0,
                        OcrCharCount: 0,
                        ExitCode: ocrDone.ExitCode,
                        TimedOut: ocrDone.TimedOut);
                    continue;
                }

                observations[pageNumber] = OcrObservationParser.Parse(text, tsvText,
                    Path.GetFileName(ResolveImageTextCommand(options)), languages);
                pageText[pageNumber] = cleanedText;
                pageDiagnostics[pageNumber] = new PdfImagePageOcrDiagnostic(
                    pageNumber,
                    "ocr_text_extracted",
                    OcrWordCount: SplitWords(cleanedText).Count(),
                    OcrCharCount: cleanedText.Length,
                    ExitCode: ocrDone.ExitCode,
                    TimedOut: ocrDone.TimedOut);
            }

            await callbacks.ReportAsync(
                Math.Min(attemptedPages.Count, plan.AttemptedPages.Length),
                plan.AttemptedPages.Length,
                ct).ConfigureAwait(false);
            var effectivePlan = BuildEffectiveImagePageOcrPlan(plan, attemptedPages, skippedPages);
            var merged = MergeImageOcrText(nativeExtraction, pageText, languages, Math.Max(1, options.OcrImagePageMinWords), observations);
            var diagnostics = BuildImagePageOcrDiagnostics(
                effectivePlan,
                pageText,
                nativeExtraction,
                merged,
                pageDiagnostics,
                failureReason: timeBudgetExhausted ? "timeout_budget" : null,
                timedOut: timeBudgetExhausted,
                timeoutSeconds: ResolveImagePageTotalBudgetSeconds(options));

            return new PdfImagePageOcrResult(
                merged is null ? null : merged with { OcrDiagnostics = diagnostics },
                diagnostics);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested || ex.CancellationToken == ct)
        {
            throw;
        }
        catch (Exception ex)
        {
            foreach (var unattempted in plan.AttemptedPages.Except(attemptedPages))
            {
                skippedPages.Add(unattempted);
                pageDiagnostics[unattempted] = new(unattempted, "skipped", "prior_page_failure");
            }
            foreach (var failed in attemptedPages.Where(page => !pageText.ContainsKey(page)
                         && !pageDiagnostics.ContainsKey(page)))
                pageDiagnostics[failed] = new(failed, "output_read_failed", ex.GetType().Name);
            var partial = MergeImageOcrText(nativeExtraction, pageText, languages, 1, observations);
            var diagnostics = BuildImagePageOcrDiagnostics(
                BuildEffectiveImagePageOcrPlan(plan, attemptedPages, skippedPages),
                pageText,
                nativeExtraction,
                mergedExtraction: partial,
                pageDiagnostics,
                failureReason: timeBudgetExhausted ? "timeout_budget" : "exception",
                timedOut: timeBudgetExhausted,
                timeoutSeconds: ResolveImagePageTotalBudgetSeconds(options),
                stderr: ex.GetType().Name);
            return new PdfImagePageOcrResult(partial is null ? null : partial with { OcrDiagnostics = diagnostics }, diagnostics);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    internal static PdfImagePageOcrPlan BuildImagePageOcrPlan(
        PdfExtractionResult nativeExtraction,
        IngestionOptions options)
    {
        var candidatePages = nativeExtraction.Pages
            .Where(static page => page.ImageCount > 0 || HasReplacementSignal(page) || HasTextRecoverySignal(page))
            .Select(static page => page.PageNumber)
            .Distinct()
            .Order()
            .ToArray();

        if (candidatePages.Length == 0)
        {
            return new PdfImagePageOcrPlan(
                CandidatePageCount: 0,
                AttemptedPageCount: 0,
                SkippedPageCount: 0,
                MaxPages: ResolveImagePageMaxPages(options),
                CandidatePages: [],
                AttemptedPages: [],
                SkippedPages: []);
        }

        var maxPages = ResolveImagePageMaxPages(options);
        var attemptedPages = SelectImagePageOcrAttemptPages(nativeExtraction.Pages, candidatePages, maxPages);
        var skippedPages = candidatePages.Except(attemptedPages).Order().ToArray();
        return new PdfImagePageOcrPlan(
            CandidatePageCount: candidatePages.Length,
            AttemptedPageCount: attemptedPages.Length,
            SkippedPageCount: skippedPages.Length,
            MaxPages: maxPages,
            CandidatePages: candidatePages,
            AttemptedPages: attemptedPages,
            SkippedPages: skippedPages);
    }

    private static int ResolveImagePageMaxPages(IngestionOptions options)
        => options.OcrImagePageMaxPages <= 0
            ? int.MaxValue
            : Math.Clamp(options.OcrImagePageMaxPages, 1, 500);

    private static TimeSpan? ResolveImagePageTotalBudget(IngestionOptions options)
        => options.OcrImagePageMaxTotalSeconds <= 0
            ? null
            : TimeSpan.FromSeconds(Math.Clamp(options.OcrImagePageMaxTotalSeconds, 30, 86400));

    private static int? ResolveImagePageTotalBudgetSeconds(IngestionOptions options)
        => options.OcrImagePageMaxTotalSeconds <= 0
            ? null
            : Math.Clamp(options.OcrImagePageMaxTotalSeconds, 30, 86400);

    private static bool IsImagePageTotalBudgetExceeded(TimeSpan? totalBudget, TimeSpan elapsed)
        => totalBudget.HasValue && elapsed >= totalBudget.Value;

    private static TimeSpan? ResolveImagePageProcessTimeout(TimeSpan pageTimeout, TimeSpan? totalBudget, TimeSpan elapsed)
    {
        if (pageTimeout <= TimeSpan.Zero)
            return null;
        if (!totalBudget.HasValue)
            return pageTimeout;

        var remaining = totalBudget.Value - elapsed;
        if (remaining <= TimeSpan.Zero)
            return null;

        return remaining < pageTimeout ? remaining : pageTimeout;
    }

    private static PdfImagePageOcrPlan BuildEffectiveImagePageOcrPlan(
        PdfImagePageOcrPlan plan,
        IReadOnlyCollection<int> attemptedPages,
        IReadOnlyCollection<int> skippedPages)
    {
        var attempted = attemptedPages
            .Distinct()
            .Order()
            .ToArray();
        var skipped = skippedPages
            .Where(pageNumber => !attempted.Contains(pageNumber))
            .Distinct()
            .Order()
            .ToArray();

        return plan with
        {
            AttemptedPageCount = attempted.Length,
            SkippedPageCount = skipped.Length,
            AttemptedPages = attempted,
            SkippedPages = skipped
        };
    }

    private static int[] SelectImagePageOcrAttemptPages(
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<int> candidatePages,
        int maxPages)
    {
        if (candidatePages.Count <= maxPages)
            return candidatePages.Order().ToArray();

        var pagesByNumber = pages.ToDictionary(static page => page.PageNumber);
        var sparseCandidatePages = candidatePages
            .Where(pageNumber =>
            {
                if (!pagesByNumber.TryGetValue(pageNumber, out var page))
                    return false;

                var quality = page.Quality ?? PdfPageExtractionQuality.FromText(page.Text, page.WordCount, page.CharCount);
                return quality.TextEmpty || quality.TextSparse || quality.OcrCandidate;
            })
            .Order()
            .ToArray();

        var selected = new HashSet<int>();
        foreach (var pageNumber in PickEvenlyDistributedPages(sparseCandidatePages, maxPages))
            selected.Add(pageNumber);

        if (selected.Count < maxPages)
        {
            var remainingCandidates = candidatePages
                .Where(pageNumber => !selected.Contains(pageNumber))
                .Order()
                .ToArray();
            foreach (var pageNumber in PickEvenlyDistributedPages(remainingCandidates, maxPages - selected.Count))
                selected.Add(pageNumber);
        }

        return selected.Order().ToArray();
    }

    private static IEnumerable<int> PickEvenlyDistributedPages(IReadOnlyList<int> pages, int maxCount)
    {
        if (maxCount <= 0 || pages.Count == 0)
            yield break;

        if (pages.Count <= maxCount)
        {
            foreach (var page in pages)
                yield return page;
            yield break;
        }

        if (maxCount == 1)
        {
            yield return pages[0];
            yield break;
        }

        var seenIndexes = new HashSet<int>();
        for (var i = 0; i < maxCount; i++)
        {
            var index = (int)Math.Round((double)i * (pages.Count - 1) / (maxCount - 1), MidpointRounding.AwayFromZero);
            if (seenIndexes.Add(index))
                yield return pages[index];
        }

        if (seenIndexes.Count >= maxCount)
            yield break;

        for (var i = 0; i < pages.Count && seenIndexes.Count < maxCount; i++)
        {
            if (seenIndexes.Add(i))
                yield return pages[i];
        }
    }

    private static int[] ResolvePagesWithNovelText(PdfExtractionResult nativeExtraction, PdfExtractionResult? mergedExtraction)
    {
        if (mergedExtraction is null)
            return [];

        var nativePages = nativeExtraction.Pages.ToDictionary(static page => page.PageNumber);
        return mergedExtraction.Pages
            .Where(page =>
                nativePages.TryGetValue(page.PageNumber, out var nativePage)
                && !string.Equals(page.Text, nativePage.Text, StringComparison.Ordinal))
            .Select(static page => page.PageNumber)
            .Order()
            .ToArray();
    }

    internal static PdfOcrDiagnostics BuildImagePageOcrDiagnostics(
        PdfImagePageOcrPlan plan,
        IReadOnlyDictionary<int, string> pageText,
        PdfExtractionResult nativeExtraction,
        PdfExtractionResult? mergedExtraction,
        IReadOnlyDictionary<int, PdfImagePageOcrDiagnostic>? attemptedPageDiagnostics = null,
        string? failureReason = null,
        string? stderr = null,
        bool timedOut = false,
        int? timeoutSeconds = null)
    {
        var pagesWithOcrText = pageText
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Value))
            .Select(static entry => entry.Key)
            .Order()
            .ToArray();
        var pagesWithNovelText = ResolvePagesWithNovelText(nativeExtraction, mergedExtraction);
        var novelPages = pagesWithNovelText.ToHashSet();
        var diagnosticsByPage = new Dictionary<int, PdfImagePageOcrDiagnostic>();

        foreach (var pageNumber in plan.SkippedPages)
        {
            diagnosticsByPage[pageNumber] = attemptedPageDiagnostics is not null
                && attemptedPageDiagnostics.TryGetValue(pageNumber, out var diagnostic)
                    ? NormalizeImagePageDiagnostic(diagnostic)
                    : new PdfImagePageOcrDiagnostic(
                        pageNumber,
                        "skipped",
                        "budget");
        }

        foreach (var pageNumber in plan.AttemptedPages)
        {
            if (pageText.TryGetValue(pageNumber, out var text) && !string.IsNullOrWhiteSpace(text))
            {
                var cleaned = PdfTextSanitizer.ForStorage(text);
                var existing = attemptedPageDiagnostics is not null
                    && attemptedPageDiagnostics.TryGetValue(pageNumber, out var current)
                        ? current
                        : null;
                var novel = novelPages.Contains(pageNumber);
                diagnosticsByPage[pageNumber] = new PdfImagePageOcrDiagnostic(
                    pageNumber,
                    novel ? "novel_text_applied" : "no_novel_text",
                    novel ? null : "duplicate_or_below_threshold",
                    OcrWordCount: SplitWords(cleaned).Count(),
                    OcrCharCount: cleaned.Length,
                    ExitCode: existing?.ExitCode,
                    TimedOut: existing?.TimedOut ?? false);
                continue;
            }

            if (attemptedPageDiagnostics is not null
                && attemptedPageDiagnostics.TryGetValue(pageNumber, out var diagnostic))
            {
                diagnosticsByPage[pageNumber] = NormalizeImagePageDiagnostic(diagnostic);
                continue;
            }

            diagnosticsByPage[pageNumber] = new PdfImagePageOcrDiagnostic(
                pageNumber,
                "ocr_empty",
                "no_text",
                OcrWordCount: 0,
                OcrCharCount: 0);
        }

        var failedPages = diagnosticsByPage.Values.Any(static page =>
            page.Status is "render_failed" or "ocr_failed" or "ocr_output_missing" or "output_read_failed");
        var emptyPages = diagnosticsByPage.Values.Any(static page => page.Status == "ocr_empty");
        return new PdfOcrDiagnostics(
            Mode: "image_page",
            CandidatePageCount: plan.CandidatePageCount,
            AttemptedPageCount: plan.AttemptedPageCount,
            SkippedPageCount: plan.SkippedPageCount,
            MaxPages: plan.MaxPages,
            CandidatePages: plan.CandidatePages,
            AttemptedPages: plan.AttemptedPages,
            SkippedPages: plan.SkippedPages,
            PagesWithOcrText: pagesWithOcrText,
            PagesWithNovelText: pagesWithNovelText,
            TimedOut: timedOut || diagnosticsByPage.Values.Any(static page => page.TimedOut),
            TimeoutSeconds: timeoutSeconds,
            Stderr: TruncateOcrProcessOutputForDiagnostics(stderr),
            FailureReason: NormalizeOcrDiagnosticString(failureReason) ?? (failedPages ? "page_failures" : null),
            CoverageStatus: failedPages ? "partial_error" : plan.SkippedPageCount > 0 ? "partial_budget"
                : emptyPages ? "complete_no_text" : ResolveImagePageOcrCoverageStatus(plan),
            ImagePageDiagnostics: diagnosticsByPage.Values
                .OrderBy(static diagnostic => diagnostic.PageNumber)
                .ToArray());
    }

    private static string ResolveImagePageOcrCoverageStatus(PdfImagePageOcrPlan plan)
        => plan.CandidatePageCount == 0 ? "not_applicable" :
            plan.SkippedPageCount == 0 ? "complete" :
            "partial_budget";

    private static PdfImagePageOcrDiagnostic BuildImagePageProcessDiagnostic(
        int pageNumber,
        string status,
        OcrProcessResult processResult)
    {
        var reason = processResult.FailureReason ?? (processResult.TimedOut
            ? "timeout"
            : processResult.ExitCode is null
                ? "process_start_failed"
                : processResult.ExitCode == 0
                    ? "process_failed"
                    : "exit_code_non_zero");

        return new PdfImagePageOcrDiagnostic(
            pageNumber,
            status,
            reason,
            ExitCode: processResult.ExitCode,
            TimedOut: processResult.TimedOut);
    }

    private static PdfImagePageOcrDiagnostic NormalizeImagePageDiagnostic(PdfImagePageOcrDiagnostic diagnostic)
        => diagnostic with
        {
            Status = NormalizeOcrDiagnosticString(diagnostic.Status) ?? "unknown",
            Reason = NormalizeOcrDiagnosticString(diagnostic.Reason)
        };

    internal static PdfExtractionResult? MergeImageOcrText(
        PdfExtractionResult nativeExtraction, IReadOnlyDictionary<int, string> ocrTextByPage,
        string? ocrLanguages, int minWords,
        IReadOnlyDictionary<int, PdfOcrPageObservation>? observations = null)
    {
        if (ocrTextByPage.Count == 0)
            return null;
        var pages = nativeExtraction.Pages.Select(page =>
        {
            if (!ocrTextByPage.TryGetValue(page.PageNumber, out var text))
                return page;
            var novel = OcrSourceReconciler.NovelLines(page.Text, text);
            var merged = novel.Count == 0 ? page.Text : string.IsNullOrWhiteSpace(page.Text)
                ? string.Join('\n', novel) : page.Text.TrimEnd() + "\n" + string.Join('\n', novel);
            var observation = observations is not null && observations.TryGetValue(page.PageNumber, out var observed)
                ? observed : OcrObservationParser.Parse(text, null, "tesseract", ocrLanguages);
            var words = SplitWords(merged).Count();
            var quality = PdfPageExtractionQuality.FromText(merged, words, merged.Length);
            quality = quality with
            {
                Signals = quality.Signals.Concat(page.Quality?.Signals ?? [])
                    .Concat(["image_ocr_text_extracted", "ocr_observations_page_anchored", "ocr_region_association_unverified"])
                    .Concat(observation.HasLowConfidenceWords ? ["ocr_low_confidence_observation"] : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal).ToArray()
            };
            return page with
            {
                Text = merged, WordCount = words, CharCount = merged.Length,
                Checksum = SHA256.HashData(Encoding.UTF8.GetBytes(merged)), Quality = quality,
                // RawText remains the native observation, including an explicitly empty layer.
                RawText = page.RawText ?? page.Text,
                OcrObservations = (page.OcrObservations ?? []).Append(observation).ToArray()
            };
        }).ToList();
        var tokens = pages.SelectMany(page => SplitWords(page.Text)
            .Select(word => new WordToken(word, page.PageNumber))).ToList();
        var summary = PdfExtractionQualitySummary.FromPages(pages);
        return new(tokens, pages, summary with
            { Signals = summary.Signals.Concat(["image_ocr_text_extracted"]).Distinct(StringComparer.Ordinal).ToArray() },
            Source: "pdf_text_plus_image_ocr", OcrLanguages: ocrLanguages);
    }

    internal static bool HasReplacementCharacters(string? text)
        => !string.IsNullOrEmpty(text) && text.Contains('\uFFFD', StringComparison.Ordinal);

    internal static bool HasReplacementSignal(ExtractedPdfPage page)
    {
        if (HasReplacementCharacters(page.Text))
            return true;

        var quality = page.Quality;
        return quality is not null
               && (quality.RawReplacementCharCount > 0
                   || quality.SanitizedReplacementCharCount > 0
                   || quality.Signals.Contains("replacement_chars_detected", StringComparer.Ordinal));
    }

    internal static bool HasTextRecoverySignal(ExtractedPdfPage page)
    {
        var quality = page.Quality ?? PdfPageExtractionQuality.FromText(page.Text, page.WordCount, page.CharCount);
        return quality.TextEmpty || quality.TextSparse || quality.OcrCandidate;
    }

    // Confidence is an observation, not permission to delete a source line.
    internal static string FilterLowConfidenceImageOcrText(string cleanedText, string? tsvText)
        => cleanedText;

    internal static string ResolveLanguagesForDocument(
        string pdfPath,
        IngestionOptions options,
        PdfExtractionResult? nativeExtraction)
    {
        var configured = ResolveConfiguredOcrLanguages(options);
        if (configured.Count == 0)
            return "eng";

        if (!options.OcrAutoDetectLanguages || configured.Count == 1)
            return string.Join('+', configured);
        var detected = DetectDominantLanguage(BuildLanguageCorpus(pdfPath, nativeExtraction));
        var preferred = string.IsNullOrWhiteSpace(detected) ? null : NormalizeConfiguredOcrLanguage(detected);
        if (preferred is not null && IsAutoOrAllOcrLanguageMode(options.OcrLanguages, out _))
        {
            var prioritized = ResolveDetectedAutoOcrLanguages(preferred, configured, LoadInstalledTesseractLanguages(options));
            if (prioritized.Count > 0)
                return string.Join('+', prioritized.Take(ResolveOcrMaxLanguages(options)));
        }
        if (preferred is not null && configured.Contains(preferred))
            return string.Join('+', new[] { preferred }.Concat(configured.Where(language => language != preferred)));
        return string.Join('+', configured);
    }

    private static string BuildArguments(IngestionOptions options, string inputPdfPath, string outputPdfPath, string sidecarPath, string languages, bool forceOcr)
    {
        var template = forceOcr
            ? ResolveForceOcrArgumentsTemplate(options)
            : ResolveOcrArgumentsTemplate(options);

        return template
            .Replace("{languages}", QuoteArgument(languages), StringComparison.Ordinal)
            .Replace("{sidecar}", QuoteArgument(sidecarPath), StringComparison.Ordinal)
            .Replace("{input}", QuoteArgument(inputPdfPath), StringComparison.Ordinal)
            .Replace("{output}", QuoteArgument(outputPdfPath), StringComparison.Ordinal);
    }

    private static string ResolveOcrArgumentsTemplate(IngestionOptions options)
        => string.IsNullOrWhiteSpace(options.OcrArguments)
            ? "--rotate-pages --deskew --clean --skip-text --tesseract-pagesegmode 3 -l {languages} --sidecar {sidecar} {input} {output}"
            : options.OcrArguments.Trim();

    internal static string ResolveImageRendererCommand(IngestionOptions options)
        => string.IsNullOrWhiteSpace(options.OcrImageRendererCommand)
            ? "gs"
            : options.OcrImageRendererCommand.Trim();

    internal static string ResolveImageTextCommand(IngestionOptions options)
        => string.IsNullOrWhiteSpace(options.OcrImageTextCommand)
            ? "tesseract"
            : options.OcrImageTextCommand.Trim();

    private static string ResolveForceOcrArgumentsTemplate(IngestionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OcrForceArguments))
            return options.OcrForceArguments.Trim();

        var template = ResolveOcrArgumentsTemplate(options);
        return template.Contains("--skip-text", StringComparison.Ordinal)
            ? template.Replace("--skip-text", "--force-ocr", StringComparison.Ordinal)
            : "--force-ocr " + template;
    }

    private static List<string> ResolveConfiguredOcrLanguages(IngestionOptions options)
    {
        var languages = options.OcrLanguages;
        if (string.IsNullOrWhiteSpace(languages))
            return ["eng"];

        if (IsAutoOrAllOcrLanguageMode(languages, out _))
        {
            var installed = LoadInstalledTesseractLanguages(options);
            return ResolveAutoConfiguredOcrLanguages(options, installed).ToList();
        }

        return SplitOcrLanguageTokens(languages)
            .Select(NormalizeConfiguredOcrLanguage)
            .Where(IsPlausibleTesseractLanguage)
            .Where(static language => !language.Equals("osd", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<string> ResolveAutoConfiguredOcrLanguages(
        IngestionOptions options,
        IReadOnlyList<string> installedLanguages)
    {
        var maxLanguages = ResolveOcrMaxLanguages(options);
        var fallback = ParseConfiguredOcrLanguages(options.OcrAutoFallbackLanguages)
            .DefaultIfEmpty("eng")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxLanguages)
            .ToArray();
        var installed = installedLanguages
            .Select(NormalizeConfiguredOcrLanguage)
            .Where(IsPlausibleTesseractLanguage)
            .Where(static language => !language.Equals("osd", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (IsAutoOrAllOcrLanguageMode(options.OcrLanguages, out var allMode) && allMode && installed.Length > 0)
            return installed.Take(maxLanguages).ToArray();

        if (installed.Length == 0)
            return fallback.Length > 0 ? fallback : ["eng"];

        var installedSet = installed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = fallback
            .Where(installedSet.Contains)
            .Take(maxLanguages)
            .ToList();
        if (selected.Count > 0)
        {
            foreach (var language in installed)
            {
                if (selected.Count >= maxLanguages)
                    break;
                if (!selected.Contains(language, StringComparer.OrdinalIgnoreCase))
                    selected.Add(language);
            }

            return selected;
        }

        if (installedSet.Contains("eng"))
            return ["eng"];

        return installed.Take(maxLanguages).ToArray();
    }

    internal static IReadOnlyList<string> ResolveDetectedAutoOcrLanguages(
        string preferredLanguage,
        IReadOnlyList<string> configuredLanguages,
        IReadOnlyList<string> installedLanguages)
    {
        var preferred = NormalizeConfiguredOcrLanguage(preferredLanguage);
        if (!IsPlausibleTesseractLanguage(preferred)
            || preferred.Equals("osd", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var configured = configuredLanguages
            .Select(NormalizeConfiguredOcrLanguage)
            .Where(IsPlausibleTesseractLanguage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = installedLanguages
            .Select(NormalizeConfiguredOcrLanguage)
            .Where(IsPlausibleTesseractLanguage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!installed.Contains(preferred))
            return [];

        var selected = new List<string> { preferred };
        if (!preferred.Equals("eng", StringComparison.OrdinalIgnoreCase)
            && (installed.Contains("eng") || configured.Contains("eng")))
        {
            selected.Add("eng");
        }

        return selected.Concat(configuredLanguages.Select(NormalizeConfiguredOcrLanguage))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<string> ResolveExplicitConfiguredOcrLanguagesForReadiness(IngestionOptions options)
    {
        var languages = options.OcrLanguages;
        if (string.IsNullOrWhiteSpace(languages))
            return ["eng"];

        var rawTokens = SplitOcrLanguageTokens(languages);
        if (rawTokens.Any(static token => token.Equals("auto", StringComparison.OrdinalIgnoreCase) || token.Equals("all", StringComparison.OrdinalIgnoreCase)))
            return [];

        return rawTokens
            .Select(NormalizeConfiguredOcrLanguage)
            .Where(IsPlausibleTesseractLanguage)
            .Where(static language => !language.Equals("osd", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] SplitOcrLanguageTokens(string languages)
        => languages
            .Split(['+', ',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

    private static bool IsAutoOrAllOcrLanguageMode(string? languages, out bool allMode)
    {
        allMode = false;
        if (string.IsNullOrWhiteSpace(languages))
            return false;

        foreach (var token in SplitOcrLanguageTokens(languages))
        {
            if (token.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                allMode = true;
                return true;
            }

            if (token.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> ParseConfiguredOcrLanguages(string? languages)
    {
        if (string.IsNullOrWhiteSpace(languages))
            yield break;

        foreach (var token in SplitOcrLanguageTokens(languages))
        {
            var normalized = NormalizeConfiguredOcrLanguage(token);
            if (IsPlausibleTesseractLanguage(normalized)
                && !normalized.Equals("osd", StringComparison.OrdinalIgnoreCase)
                && !normalized.Equals("auto", StringComparison.OrdinalIgnoreCase)
                && !normalized.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                yield return normalized;
            }
        }
    }

    internal static int ResolveAutoOcrLanguageLimit(IngestionOptions options)
        => Math.Clamp(
            options.OcrMaxLanguages <= 0
                ? IngestionOptions.DefaultAutoOcrMaxLanguages
                : options.OcrMaxLanguages,
            1,
            IngestionOptions.AbsoluteAutoOcrMaxLanguages);

    private static int ResolveOcrMaxLanguages(IngestionOptions options)
        => ResolveAutoOcrLanguageLimit(options);

    internal static IReadOnlyList<string> LoadInstalledTesseractLanguagesForReadiness(IngestionOptions options)
        => LoadInstalledTesseractLanguages(options);

    private static string NormalizeConfiguredOcrLanguage(string language)
    {
        var trimmed = language.Trim().Replace('_', '-');
        if (trimmed.Length == 0)
            return string.Empty;
        if (trimmed.Contains('/', StringComparison.Ordinal))
            return trimmed;

        var lower = trimmed.ToLowerInvariant();
        if (TesseractLanguageAliasMap.TryGetValue(lower, out var mapped))
            return mapped;

        var baseTag = lower.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? lower;
        if (TesseractLanguageAliasMap.TryGetValue(baseTag, out mapped))
            return mapped;

        if (baseTag.Length == 2)
        {
            try
            {
                return CultureInfo.GetCultureInfo(baseTag).ThreeLetterISOLanguageName.ToLowerInvariant();
            }
            catch (CultureNotFoundException)
            {
                return string.Empty;
            }
        }

        return lower.Replace('-', '_');
    }

    private static bool IsPlausibleTesseractLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Length is < 2 or > 40)
            return false;

        return language.All(static ch =>
            char.IsLetterOrDigit(ch)
            || ch is '_' or '-' or '/');
    }

    private static List<string> LoadInstalledTesseractLanguages(IngestionOptions options)
    {
        var result = OcrProcessRunner.RunAsync(ResolveImageTextCommand(options), "--list-langs",
            TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Succeeded)
            return [];
        return ((result.Stdout ?? "") + "\n" + result.Stderr)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !line.Contains("list of available languages", StringComparison.OrdinalIgnoreCase))
            .Where(IsPlausibleTesseractLanguage)
            .Where(static language => !language.Equals("osd", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly IReadOnlyDictionary<string, string> TesseractLanguageAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "ara",
        ["ara"] = "ara",
        ["cs"] = "ces",
        ["ces"] = "ces",
        ["da"] = "dan",
        ["dan"] = "dan",
        ["de"] = "deu",
        ["deu"] = "deu",
        ["el"] = "ell",
        ["ell"] = "ell",
        ["en"] = "eng",
        ["eng"] = "eng",
        ["es"] = "spa",
        ["spa"] = "spa",
        ["fi"] = "fin",
        ["fin"] = "fin",
        ["fr"] = "fra",
        ["fra"] = "fra",
        ["he"] = "heb",
        ["heb"] = "heb",
        ["hi"] = "hin",
        ["hin"] = "hin",
        ["id"] = "ind",
        ["ind"] = "ind",
        ["it"] = "ita",
        ["ita"] = "ita",
        ["ja"] = "jpn",
        ["jpn"] = "jpn",
        ["ko"] = "kor",
        ["kor"] = "kor",
        ["nl"] = "nld",
        ["nld"] = "nld",
        ["no"] = "nor",
        ["nor"] = "nor",
        ["pl"] = "pol",
        ["pol"] = "pol",
        ["pt"] = "por",
        ["por"] = "por",
        ["ro"] = "ron",
        ["ron"] = "ron",
        ["ru"] = "rus",
        ["rus"] = "rus",
        ["sv"] = "swe",
        ["swe"] = "swe",
        ["th"] = "tha",
        ["tha"] = "tha",
        ["tr"] = "tur",
        ["tur"] = "tur",
        ["uk"] = "ukr",
        ["ukr"] = "ukr",
        ["vi"] = "vie",
        ["vie"] = "vie",
        ["zh"] = "chi_sim",
        ["zh-cn"] = "chi_sim",
        ["zh-hans"] = "chi_sim",
        ["zh-sg"] = "chi_sim",
        ["zh-tw"] = "chi_tra",
        ["zh-hant"] = "chi_tra",
        ["zh-hk"] = "chi_tra",
        ["zh-mo"] = "chi_tra"
    };

    private static string BuildLanguageCorpus(string pdfPath, PdfExtractionResult? nativeExtraction)
    {
        var sb = new StringBuilder();
        sb.Append(Path.GetFileNameWithoutExtension(pdfPath));
        sb.Append(' ');
        sb.Append(Path.GetFileName(Path.GetDirectoryName(pdfPath)));

        if (nativeExtraction is not null)
        {
            foreach (var page in nativeExtraction.Pages.Take(8))
            {
                if (page.Text.Length > 0)
                    sb.Append(' ').Append(page.Text);
            }
        }

        return sb.ToString();
    }

    private static string? DetectDominantLanguage(string corpus)
        => DocumentLanguageResolver.DetectDominantLanguage(corpus);

    private static PdfOcrDiagnostics BuildFullDocumentOcrDiagnostics(
        bool forceOcr,
        int? exitCode = null,
        bool timedOut = false,
        int? timeoutSeconds = null,
        string? stderr = null,
        string? failureReason = null,
        string? appliedReason = null)
        => new(
            Mode: forceOcr ? "full_document_force" : "full_document",
            CandidatePageCount: 0,
            AttemptedPageCount: 0,
            SkippedPageCount: 0,
            MaxPages: 0,
            CandidatePages: [],
            AttemptedPages: [],
            SkippedPages: [],
            PagesWithOcrText: [],
            PagesWithNovelText: [],
            ExitCode: exitCode,
            TimedOut: timedOut,
            TimeoutSeconds: timeoutSeconds,
            Stderr: TruncateOcrProcessOutputForDiagnostics(stderr),
            FailureReason: NormalizeOcrDiagnosticString(failureReason),
            AppliedReason: NormalizeOcrDiagnosticString(appliedReason),
            CoverageStatus: "not_applicable");

    internal static PdfOcrDiagnostics? WithAppliedReason(PdfOcrDiagnostics? diagnostics, string appliedReason)
        => diagnostics is null
            ? null
            : diagnostics with { AppliedReason = NormalizeOcrDiagnosticString(appliedReason) };

    internal static PdfOcrDiagnostics BuildOcrDisabledDiagnostics(
        IngestionOptions options,
        PdfExtractionResult nativeExtraction,
        bool imagePageOcrRecommended)
    {
        var candidatePages = Array.Empty<int>();
        var maxPages = 0;
        if (imagePageOcrRecommended)
        {
            var plan = BuildImagePageOcrPlan(nativeExtraction, options);
            candidatePages = plan.CandidatePages;
            maxPages = plan.MaxPages;
        }

        return new PdfOcrDiagnostics(
            Mode: "ocr_disabled",
            CandidatePageCount: candidatePages.Length,
            AttemptedPageCount: 0,
            SkippedPageCount: candidatePages.Length,
            MaxPages: maxPages,
            CandidatePages: candidatePages,
            AttemptedPages: [],
            SkippedPages: candidatePages,
            PagesWithOcrText: [],
            PagesWithNovelText: [],
            FailureReason: "ocr_required_but_disabled",
            AppliedReason: "ocr_disabled",
            CoverageStatus: "disabled",
            ImagePageDiagnostics: candidatePages.Length == 0
                ? null
                : candidatePages
                    .Select(static page => new PdfImagePageOcrDiagnostic(page, "skipped", "ocr_disabled"))
                    .ToArray());
    }

    internal static PdfExtractionResult WithImageCountsFromNative(
        PdfExtractionResult extraction,
        PdfExtractionResult nativeExtraction)
    {
        var nativePages = nativeExtraction.Pages.ToDictionary(static page => page.PageNumber);
        var pages = extraction.Pages
            .Select(page => nativePages.TryGetValue(page.PageNumber, out var nativePage)
                ? page with
                {
                    ImageCount = nativePage.ImageCount,
                    RawText = nativePage.RawText ?? nativePage.Text,
                    WidthPoints = page.WidthPoints ?? nativePage.WidthPoints,
                    HeightPoints = page.HeightPoints ?? nativePage.HeightPoints,
                    NativeImageRegions = nativePage.NativeImageRegions
                }
                : page)
            .ToList();

        return extraction with { Pages = pages };
    }

    internal static PdfOcrDiagnostics? CombineOcrDiagnostics(
        PdfOcrDiagnostics? fullDocumentDiagnostics,
        PdfOcrDiagnostics? imagePageDiagnostics)
    {
        if (fullDocumentDiagnostics is null)
            return imagePageDiagnostics;
        if (imagePageDiagnostics is null)
            return fullDocumentDiagnostics;

        var mode = fullDocumentDiagnostics.Mode switch
        {
            "full_document_force" => "full_document_force_plus_image_page",
            _ => "full_document_plus_image_page"
        };
        var stderr = string.Join(
            "\n",
            new[]
            {
                PrefixDiagnosticOutput("full_document", fullDocumentDiagnostics.Stderr),
                PrefixDiagnosticOutput("image_page", imagePageDiagnostics.Stderr)
            }.Where(static value => !string.IsNullOrWhiteSpace(value)));

        return imagePageDiagnostics with
        {
            Mode = mode,
            ExitCode = imagePageDiagnostics.ExitCode ?? fullDocumentDiagnostics.ExitCode,
            TimedOut = imagePageDiagnostics.TimedOut || fullDocumentDiagnostics.TimedOut,
            TimeoutSeconds = imagePageDiagnostics.TimeoutSeconds ?? fullDocumentDiagnostics.TimeoutSeconds,
            Stderr = TruncateOcrProcessOutputForDiagnostics(stderr),
            FailureReason = imagePageDiagnostics.FailureReason ?? fullDocumentDiagnostics.FailureReason,
            CoverageStatus = imagePageDiagnostics.CoverageStatus
                             ?? ResolveOcrCoverageStatus(imagePageDiagnostics)
                             ?? fullDocumentDiagnostics.CoverageStatus
        };
    }

    private static string? ResolveOcrCoverageStatus(PdfOcrDiagnostics diagnostics)
        => diagnostics.CandidatePageCount <= 0 ? diagnostics.CoverageStatus :
            diagnostics.SkippedPageCount == 0 ? "complete" :
            "partial_budget";

    private static string? PrefixDiagnosticOutput(string source, string? value)
    {
        var normalized = NormalizeOcrDiagnosticString(value);
        return normalized is null ? null : $"{source}: {normalized}";
    }

    internal static string? TruncateOcrProcessOutputForDiagnostics(string? value)
    {
        var normalized = NormalizeOcrDiagnosticString(value);
        if (normalized is null)
            return null;

        if (normalized.Length <= OcrProcessOutputDiagnosticsMaxLength)
            return normalized;

        const string suffix = "...[truncated]";
        return normalized[..Math.Max(0, OcrProcessOutputDiagnosticsMaxLength - suffix.Length)] + suffix;
    }

    private static string? NormalizeOcrDiagnosticString(string? value)
    {
        var cleaned = PdfTextSanitizer.ForStorage(value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static IEnumerable<string> SplitWords(string value)
        => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static Task<OcrProcessResult> RunProcessWithDiagnosticsAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
        => OcrProcessRunner.RunAsync(fileName, arguments, timeout, ct);

    private static string QuoteArgument(string? value)
    {
        var text = value ?? string.Empty;
        return "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

}

sealed record PdfDocumentOcrResult(
    PdfExtractionResult? Extraction,
    PdfOcrDiagnostics Diagnostics);
