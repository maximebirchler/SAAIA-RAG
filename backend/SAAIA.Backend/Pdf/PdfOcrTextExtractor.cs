using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static class PdfOcrTextExtractor
{
    private const int OcrProcessOutputDiagnosticsMaxLength = 4096;

    private enum ShortOcrLineKind
    {
        None,
        Label,
        Code,
        Heading
    }

    private static readonly HashSet<string> ImportantShortOcrLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALARM",
        "AUTO",
        "CLOSE",
        "CLOSED",
        "DOWN",
        "ENTER",
        "ESC",
        "FAULT",
        "LOCAL",
        "LOCK",
        "MANUAL",
        "MODE",
        "OFF",
        "OK",
        "ON",
        "OPEN",
        "POWER",
        "REMOTE",
        "RESET",
        "RUN",
        "SET",
        "START",
        "STOP",
        "TEST",
        "UNLOCK",
        "UP"
    };

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
        string pdfPath,
        IngestionOptions options,
        CancellationToken ct,
        string? languagesOverride = null,
        bool forceOcr = false)
    {
        if (!options.OcrEnabled)
            return null;

        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.OcrTimeoutSeconds, 30, 7200));
        var timeoutSeconds = (int)timeout.TotalSeconds;
        if (string.IsNullOrWhiteSpace(options.OcrCommand))
        {
            var diagnostics = BuildFullDocumentOcrDiagnostics(
                forceOcr,
                timeoutSeconds: timeoutSeconds,
                failureReason: "command_missing");
            return new PdfDocumentOcrResult(null, diagnostics);
        }

        var languages = string.IsNullOrWhiteSpace(languagesOverride)
            ? ResolveLanguagesForDocument(pdfPath, options, nativeExtraction: null)
            : languagesOverride.Trim();
        var tempDir = Path.Combine(Path.GetTempPath(), "saaia-ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var sidecarPath = Path.Combine(tempDir, "ocr.txt");
        var outputPdfPath = Path.Combine(tempDir, "ocr.pdf");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = options.OcrCommand.Trim(),
                Arguments = BuildArguments(options, pdfPath, outputPdfPath, sidecarPath, languages, forceOcr),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                var diagnostics = BuildFullDocumentOcrDiagnostics(
                    forceOcr,
                    timeoutSeconds: timeoutSeconds,
                    failureReason: "process_start_failed");
                return new PdfDocumentOcrResult(null, diagnostics);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            bool exited;
            try
            {
                exited = await WaitForExitAsync(process, timeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryKill(process);
                await TryDrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }

            if (!exited)
            {
                TryKill(process);
                var stderr = await TryReadAsync(stderrTask).ConfigureAwait(false);
                await TryDrainAsync(stdoutTask).ConfigureAwait(false);
                var diagnostics = BuildFullDocumentOcrDiagnostics(
                    forceOcr,
                    timedOut: true,
                    timeoutSeconds: timeoutSeconds,
                    stderr: stderr,
                    failureReason: "timeout");
                return new PdfDocumentOcrResult(null, diagnostics);
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var stderrText = stderrTask.Result;
            if (process.ExitCode != 0)
            {
                var diagnostics = BuildFullDocumentOcrDiagnostics(
                    forceOcr,
                    exitCode: process.ExitCode,
                    timeoutSeconds: timeoutSeconds,
                    stderr: stderrText,
                    failureReason: "exit_code_non_zero");
                return new PdfDocumentOcrResult(null, diagnostics);
            }

            if (!File.Exists(sidecarPath))
            {
                var diagnostics = BuildFullDocumentOcrDiagnostics(
                    forceOcr,
                    exitCode: process.ExitCode,
                    timeoutSeconds: timeoutSeconds,
                    stderr: stderrText,
                    failureReason: "sidecar_missing");
                return new PdfDocumentOcrResult(null, diagnostics);
            }

            var sidecarText = await File.ReadAllTextAsync(sidecarPath, Encoding.UTF8, ct).ConfigureAwait(false);
            var result = BuildExtractionFromSidecarText(sidecarText, languages, source: forceOcr ? "ocr_sidecar_force" : "ocr_sidecar");
            if (result.Tokens.Count < Math.Max(1, options.OcrMinWords))
            {
                var diagnostics = BuildFullDocumentOcrDiagnostics(
                    forceOcr,
                    exitCode: process.ExitCode,
                    timeoutSeconds: timeoutSeconds,
                    stderr: stderrText,
                    failureReason: "below_min_words");
                return new PdfDocumentOcrResult(null, diagnostics);
            }

            var successDiagnostics = BuildFullDocumentOcrDiagnostics(
                forceOcr,
                exitCode: process.ExitCode,
                timeoutSeconds: timeoutSeconds,
                stderr: stderrText);
            return new PdfDocumentOcrResult(
                result with { OcrDiagnostics = successDiagnostics },
                successDiagnostics);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var diagnostics = BuildFullDocumentOcrDiagnostics(
                forceOcr,
                timeoutSeconds: timeoutSeconds,
                stderr: ex.Message,
                failureReason: "exception");
            return new PdfDocumentOcrResult(null, diagnostics);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    internal static PdfExtractionResult BuildExtractionFromSidecarText(string sidecarText)
        => BuildExtractionFromSidecarText(sidecarText, ocrLanguages: null);

    internal static PdfExtractionResult BuildExtractionFromSidecarText(string sidecarText, string? ocrLanguages)
        => BuildExtractionFromSidecarText(sidecarText, ocrLanguages, source: "ocr_sidecar");

    private static PdfExtractionResult BuildExtractionFromSidecarText(string sidecarText, string? ocrLanguages, string source)
    {
        var rawPages = sidecarText
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .TrimEnd('\f')
            .Split('\f');
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

            var quality = PdfPageExtractionQuality.FromCounts(words.Length, text.Length);
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
                RawText: rawPages[i]));
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
        if (ocrExtraction.Tokens.Count <= 0)
            return false;

        if (!fullDocumentOcrRecommended)
            return ocrExtraction.Tokens.Count >= nativeExtraction.Tokens.Count;

        if (ocrExtraction.Tokens.Count > nativeExtraction.Tokens.Count)
            return true;

        var nativeQualityRank = QualityRank(nativeExtraction.Quality.TextStatus);
        var ocrQualityRank = QualityRank(ocrExtraction.Quality.TextStatus);
        if (ocrQualityRank > nativeQualityRank)
            return true;

        return forceFullDocumentOcr
            && ocrQualityRank >= nativeQualityRank
            && ocrExtraction.Tokens.Count >= Math.Max(1, nativeExtraction.Tokens.Count / 2);
    }

    private static int QualityRank(string? textStatus)
        => textStatus switch
        {
            "ok" => 3,
            "low_text" => 2,
            "empty_text" => 1,
            _ => 0
        };

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

                var ocrTimeout = ResolveImagePageProcessTimeout(pageTimeout, totalBudget, totalStopwatch.Elapsed);
                if (ocrTimeout is null)
                {
                    timeBudgetExhausted = true;
                    attemptedPages.Remove(pageNumber);
                    skippedPages.Add(pageNumber);
                    pageDiagnostics[pageNumber] = new PdfImagePageOcrDiagnostic(
                        pageNumber,
                        "skipped",
                        "time_budget");
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

                var text = await File.ReadAllTextAsync(outputText, Encoding.UTF8, ct).ConfigureAwait(false);
                var cleanedText = PdfTextSanitizer.ForStorage(text);
                var outputTsv = outputBase + ".tsv";
                if (File.Exists(outputTsv))
                {
                    var tsvText = await File.ReadAllTextAsync(outputTsv, Encoding.UTF8, ct).ConfigureAwait(false);
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
            var merged = MergeImageOcrText(nativeExtraction, pageText, languages, Math.Max(1, options.OcrImagePageMinWords));
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
            var diagnostics = BuildImagePageOcrDiagnostics(
                BuildEffectiveImagePageOcrPlan(plan, attemptedPages, skippedPages),
                pageText,
                nativeExtraction,
                mergedExtraction: null,
                pageDiagnostics,
                failureReason: timeBudgetExhausted ? "timeout_budget" : "exception",
                timedOut: timeBudgetExhausted,
                timeoutSeconds: ResolveImagePageTotalBudgetSeconds(options),
                stderr: ex.Message);
            return new PdfImagePageOcrResult(null, diagnostics);
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
            TimedOut: timedOut,
            TimeoutSeconds: timeoutSeconds,
            Stderr: TruncateOcrProcessOutputForDiagnostics(stderr),
            FailureReason: NormalizeOcrDiagnosticString(failureReason),
            CoverageStatus: ResolveImagePageOcrCoverageStatus(plan),
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
        var reason = processResult.TimedOut
            ? "timeout"
            : processResult.ExitCode is null
                ? "process_start_failed"
                : processResult.ExitCode == 0
                    ? "process_failed"
                    : "exit_code_non_zero";

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
        PdfExtractionResult nativeExtraction,
        IReadOnlyDictionary<int, string> ocrTextByPage,
        string? ocrLanguages,
        int minWords)
    {
        if (ocrTextByPage.Count == 0)
            return null;

        var tokens = new List<WordToken>();
        var pages = new List<ExtractedPdfPage>(nativeExtraction.Pages.Count);
        var changed = false;

        foreach (var page in nativeExtraction.Pages)
        {
            var text = page.Text;
            var rawText = page.RawText ?? page.Text;
            IReadOnlyList<string> pageAppliedSignals = [];
            if (ocrTextByPage.TryGetValue(page.PageNumber, out var ocrText))
            {
                var cleanedOcrText = CleanOcrReplacementText(ocrText);
                if (ShouldReplaceCorruptNativeText(text, cleanedOcrText, minWords))
                {
                    text = cleanedOcrText;
                    pageAppliedSignals = ["image_ocr_text_extracted", "image_ocr_replaced_corrupt_text"];
                    changed = true;
                }
                else if (ShouldReplaceLayoutCompressedNativeText(text, cleanedOcrText, minWords))
                {
                    text = cleanedOcrText;
                    pageAppliedSignals = ["image_ocr_text_extracted", "image_ocr_replaced_layout_text"];
                    changed = true;
                }
                else
                {
                    var novelLines = ExtractNovelOcrLines(text, ocrText, minWords);
                    if (novelLines.Count > 0)
                    {
                        text = string.IsNullOrWhiteSpace(text)
                            ? string.Join('\n', novelLines)
                            : text.TrimEnd() + "\n" + string.Join('\n', novelLines);
                        pageAppliedSignals = ["image_ocr_text_extracted"];
                        changed = true;
                    }
                }
            }

            var words = SplitWords(text).ToArray();
            foreach (var word in words)
                tokens.Add(new WordToken(word, page.PageNumber));

            var quality = PdfPageExtractionQuality.FromText(text, words.Length, text.Length);
            if (!string.Equals(text, page.Text, StringComparison.Ordinal))
            {
                rawText = text;
                quality = quality with
                {
                    Signals = quality.Signals
                        .Concat(pageAppliedSignals.Count == 0 ? ["image_ocr_text_extracted"] : pageAppliedSignals)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };
            }

            pages.Add(new ExtractedPdfPage(
                PageNumber: page.PageNumber,
                Text: text,
                WordCount: words.Length,
                CharCount: text.Length,
                Checksum: SHA256.HashData(Encoding.UTF8.GetBytes(text)),
                Quality: quality,
                ImageCount: page.ImageCount,
                RawText: rawText,
                WidthPoints: page.WidthPoints,
                HeightPoints: page.HeightPoints,
                NativeLayoutText: page.NativeLayoutText,
                NativeLayoutBlocks: page.NativeLayoutBlocks,
                NativeLayoutAlgorithm: page.NativeLayoutAlgorithm,
                NativeImageRegions: page.NativeImageRegions));
        }

        if (!changed)
            return null;

        var summary = PdfExtractionQualitySummary.FromPages(pages);
        return new PdfExtractionResult(
            tokens,
            pages,
            summary with
            {
                Signals = summary.Signals
                    .Concat(["image_ocr_text_extracted"])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            },
            Source: "pdf_text_plus_image_ocr",
            OcrLanguages: string.IsNullOrWhiteSpace(ocrLanguages) ? null : ocrLanguages.Trim());
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

    private static bool ShouldReplaceCorruptNativeText(string nativeText, string cleanedOcrText, int minWords)
    {
        if (!HasReplacementCharacters(nativeText) || string.IsNullOrWhiteSpace(cleanedOcrText))
            return false;

        var nativeReplacementCount = CountReplacementCharacters(nativeText);
        var ocrReplacementCount = CountReplacementCharacters(cleanedOcrText);
        if (ocrReplacementCount >= nativeReplacementCount)
            return false;

        var words = SplitWords(cleanedOcrText).ToArray();
        if (words.Length < Math.Max(1, minWords))
            return false;

        return HasSufficientNativeCoverageForReplacement(nativeText, cleanedOcrText)
               && !OcrNoiseFilter.LooksLikeProbableNoiseText(cleanedOcrText);
    }

    private static bool ShouldReplaceLayoutCompressedNativeText(string nativeText, string cleanedOcrText, int minWords)
    {
        if (string.IsNullOrWhiteSpace(nativeText) || string.IsNullOrWhiteSpace(cleanedOcrText))
            return false;
        if (HasReplacementCharacters(nativeText))
            return false;

        var nativeWords = SplitWords(nativeText).ToArray();
        var ocrWords = SplitWords(cleanedOcrText).ToArray();
        if (nativeWords.Length < Math.Max(12, minWords * 3))
            return false;
        if (ocrWords.Length < Math.Max(minWords, nativeWords.Length * 65 / 100))
            return false;
        if (!HasStrongBidirectionalCoverageForLayoutReplacement(nativeText, cleanedOcrText))
            return false;

        var nativeLines = GetMeaningfulOcrLayoutLines(nativeText);
        var ocrLines = GetMeaningfulOcrLayoutLines(cleanedOcrText);
        if (ocrLines.Length < 6)
            return false;

        var nativeAverageLineLength = AverageLineLength(nativeLines);
        var ocrAverageLineLength = AverageLineLength(ocrLines);
        var nativeLooksCompressed =
            nativeLines.Length <= Math.Max(3, ocrLines.Length / 3)
            || nativeAverageLineLength >= 120 && ocrAverageLineLength <= nativeAverageLineLength * 0.75;
        if (!nativeLooksCompressed)
            return false;

        return CountDistinctLineStarts(ocrLines) >= Math.Min(6, ocrLines.Length);
    }

    private static int CountReplacementCharacters(string text)
        => text.Count(static ch => ch == '\uFFFD');

    private static string CleanOcrReplacementText(string ocrText)
    {
        var lines = ocrText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\f', '\n')
            .Split('\n')
            .Select(static line => CollapseWhitespace(PdfTextSanitizer.ForStorage(line)))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return string.Join('\n', lines);
    }

    internal static string FilterLowConfidenceImageOcrText(string cleanedText, string? tsvText)
    {
        if (string.IsNullOrWhiteSpace(cleanedText) || string.IsNullOrWhiteSpace(tsvText))
            return cleanedText;

        var lowConfidenceLines = ParseLowConfidenceTsvLines(tsvText)
            .Where(static line => line.Tokens.Count > 0)
            .ToArray();
        if (lowConfidenceLines.Length == 0)
            return cleanedText;

        var filtered = cleanedText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(line => !MatchesLowConfidenceOcrLine(line, lowConfidenceLines))
            .ToArray();

        return string.Join('\n', filtered).Trim();
    }

    private static IReadOnlyList<OcrLineConfidence> ParseLowConfidenceTsvLines(string tsvText)
    {
        var groups = new Dictionary<(string Block, string Paragraph, string Line), List<OcrWordConfidence>>();
        foreach (var rawLine in tsvText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine) || rawLine.StartsWith("level\t", StringComparison.Ordinal))
                continue;

            var parts = rawLine.Split('\t');
            if (parts.Length < 12 || !string.Equals(parts[0], "5", StringComparison.Ordinal))
                continue;
            if (!double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
                continue;

            var text = string.Join('\t', parts.Skip(11)).Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var normalized = NormalizeCompactForOcrMerge(text);
            if (normalized.Length < 2)
                continue;

            var key = (Block: parts[2], Paragraph: parts[3], Line: parts[4]);
            if (!groups.TryGetValue(key, out var words))
            {
                words = [];
                groups[key] = words;
            }

            words.Add(new OcrWordConfidence(normalized, confidence));
        }

        var lines = new List<OcrLineConfidence>();
        foreach (var words in groups.Values)
        {
            var meaningful = words
                .Where(static word => word.Token.Any(char.IsLetter))
                .ToArray();
            if (meaningful.Length < 2)
                continue;

            var average = meaningful.Average(static word => word.Confidence);
            var lowMeaningfulWords = meaningful.Count(static word => word.Confidence < 58);
            var hasVeryLowLongWord = meaningful.Any(static word => word.Token.Length >= 5 && word.Confidence < 45);
            if (!hasVeryLowLongWord && lowMeaningfulWords < 2 && average >= 55)
                continue;

            lines.Add(new OcrLineConfidence(
                meaningful.Select(static word => word.Token).Distinct(StringComparer.Ordinal).ToArray()));
        }

        return lines;
    }

    private static bool MatchesLowConfidenceOcrLine(string line, IReadOnlyList<OcrLineConfidence> lowConfidenceLines)
    {
        var tokens = ExtractComparableTokens(line, skipTokensWithReplacementCharacters: false)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tokens.Length < 2)
            return false;

        foreach (var lowConfidenceLine in lowConfidenceLines)
        {
            var covered = tokens.Count(lowConfidenceLine.Tokens.Contains);
            var coverage = (double)covered / tokens.Length;
            if (coverage >= 0.67 && covered >= 2)
                return true;
        }

        return false;
    }

    private static bool HasSufficientNativeCoverageForReplacement(string nativeText, string ocrText)
    {
        var nativeTokens = ExtractComparableTokens(nativeText, skipTokensWithReplacementCharacters: true)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (nativeTokens.Length == 0)
            return true;

        var ocrTokens = ExtractComparableTokens(ocrText, skipTokensWithReplacementCharacters: false)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (ocrTokens.Count == 0)
            return false;

        var covered = nativeTokens.Count(ocrTokens.Contains);
        var coverage = (double)covered / nativeTokens.Length;
        var relativeTokenVolume = (double)ocrTokens.Count / nativeTokens.Length;

        return coverage >= 0.7 && relativeTokenVolume >= 0.65;
    }

    private static bool HasStrongBidirectionalCoverageForLayoutReplacement(string nativeText, string ocrText)
    {
        var nativeTokens = ExtractComparableTokens(nativeText, skipTokensWithReplacementCharacters: true)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (nativeTokens.Length < 8)
            return false;

        var ocrTokens = ExtractComparableTokens(ocrText, skipTokensWithReplacementCharacters: false)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ocrTokens.Length < 8)
            return false;

        var ocrTokenSet = ocrTokens.ToHashSet(StringComparer.Ordinal);
        var nativeTokenSet = nativeTokens.ToHashSet(StringComparer.Ordinal);
        var nativeCovered = nativeTokens.Count(ocrTokenSet.Contains) / (double)nativeTokens.Length;
        var ocrCovered = ocrTokens.Count(nativeTokenSet.Contains) / (double)ocrTokens.Length;
        var relativeTokenVolume = ocrTokens.Length / (double)nativeTokens.Length;

        return nativeCovered >= 0.82
            && ocrCovered >= 0.70
            && relativeTokenVolume is >= 0.70 and <= 1.35;
    }

    private static string[] GetMeaningfulOcrLayoutLines(string text)
        => text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(CollapseWhitespace)
            .Where(static line => line.Length >= 8)
            .ToArray();

    private static double AverageLineLength(IReadOnlyList<string> lines)
        => lines.Count == 0 ? 0 : lines.Average(static line => line.Length);

    private static int CountDistinctLineStarts(IReadOnlyList<string> lines)
        => lines
            .Select(static line => NormalizeCompactForOcrMerge(line.Length <= 16 ? line : line[..16]))
            .Where(static line => line.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static IEnumerable<string> ExtractComparableTokens(string text, bool skipTokensWithReplacementCharacters)
    {
        foreach (var token in SplitWords(text))
        {
            if (skipTokensWithReplacementCharacters && HasReplacementCharacters(token))
                continue;

            var normalized = NormalizeCompactForOcrMerge(token);
            if (normalized.Length >= 2)
                yield return normalized;
        }
    }

    internal static string ResolveLanguagesForDocument(
        string pdfPath,
        IngestionOptions options,
        PdfExtractionResult? nativeExtraction)
    {
        var isAutoMode = IsAutoOrAllOcrLanguageMode(options.OcrLanguages, out _);
        var configured = ResolveConfiguredOcrLanguages(options);
        if (configured.Count == 0)
            return "eng";

        if (!options.OcrAutoDetectLanguages || configured.Count == 1)
            return string.Join('+', configured);

        var corpus = BuildLanguageCorpus(pdfPath, nativeExtraction);
        var detected = DetectDominantLanguage(corpus);
        var preferred = string.IsNullOrWhiteSpace(detected)
            ? null
            : NormalizeConfiguredOcrLanguage(detected);
        if (isAutoMode && !string.IsNullOrWhiteSpace(preferred))
        {
            var detectedSelection = ResolveDetectedAutoOcrLanguages(
                preferred,
                configured,
                LoadInstalledTesseractLanguages(options));
            if (detectedSelection.Count > 0)
                return string.Join('+', detectedSelection);
        }

        if (preferred is null || !configured.Contains(preferred))
            return string.Join('+', configured);

        var selected = new List<string> { preferred };
        if (!string.Equals(preferred, "eng", StringComparison.Ordinal)
            && configured.Contains("eng"))
        {
            selected.Add("eng");
        }

        return string.Join('+', selected);
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

        return selected;
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
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ResolveImageTextCommand(options),
                Arguments = "--list-langs",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!process.Start())
                return [];

            var exited = process.WaitForExit(2000);
            if (!exited)
            {
                TryKill(process);
                return [];
            }

            var output = process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd();
            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SkipWhile(static line => line.Contains("list of available languages", StringComparison.OrdinalIgnoreCase))
                .Select(static line => line.Trim())
                .Where(IsPlausibleTesseractLanguage)
                .Where(static language => !language.Equals("osd", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static language => language.Equals("eng", StringComparison.OrdinalIgnoreCase))
                .ThenBy(static language => language, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
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

    private static IReadOnlyList<string> ExtractNovelOcrLines(string nativeText, string ocrText, int minWords)
    {
        var nativeNormalized = NormalizeForOcrMerge(nativeText);
        var nativeCompact = NormalizeCompactForOcrMerge(nativeText);
        var nativeComparableTokenSequence = ExtractComparableTokens(nativeText, skipTokensWithReplacementCharacters: true)
            .ToArray();
        var nativeComparableTokens = nativeComparableTokenSequence
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var rawLine in ocrText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = PdfTextSanitizer.ForStorage(rawLine);
            line = CollapseWhitespace(line);
            var comparisonLine = RemoveLeadingOcrListMarkerNoise(line);

            var words = SplitWords(comparisonLine).ToArray();
            var shortLineKind = ClassifyShortImportantOcrLine(line);
            var isImportantShortLine = shortLineKind != ShortOcrLineKind.None;
            if (!isImportantShortLine && comparisonLine.Length < 8)
                continue;
            if (!isImportantShortLine && words.Length < minWords)
                continue;
            if (OcrNoiseFilter.LooksLikeProbableNoiseText(line))
                continue;

            var normalized = NormalizeForOcrMerge(comparisonLine);
            var minimumComparableLength = isImportantShortLine ? 2 : 8;
            if (normalized.Length < minimumComparableLength || !seen.Add(normalized))
                continue;
            if (NativeContainsNormalizedOcrLine(nativeNormalized, normalized, shortLineKind))
                continue;

            var compact = NormalizeCompactForOcrMerge(comparisonLine);
            if (compact.Length < minimumComparableLength)
                continue;
            if (NativeContainsCompactOcrLine(nativeText, nativeCompact, compact, shortLineKind))
                continue;
            if (!isImportantShortLine
                && LooksLikeLowNoveltyOcrLine(
                    nativeComparableTokens,
                    nativeComparableTokenSequence,
                    comparisonLine,
                    HasReplacementCharacters(nativeText)))
                continue;

            lines.Add(comparisonLine);
        }

        return lines;
    }

    private static string RemoveLeadingOcrListMarkerNoise(string line)
        => Regex.Replace(
            line,
            @"^\s*(?:[eE]\s+)(?=(?:\d|[I1l]\p{Ll}|\p{Lu}?\p{Ll}{3,}))",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

    private static bool LooksLikeLowNoveltyOcrLine(
        HashSet<string> nativeComparableTokens,
        IReadOnlyList<string> nativeComparableTokenSequence,
        string line,
        bool nativeHasReplacementCharacters)
    {
        if (nativeComparableTokens.Count < 4 || string.IsNullOrWhiteSpace(line))
            return false;

        var ocrTokens = ExtractComparableTokens(line, skipTokensWithReplacementCharacters: false)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ocrTokens.Length < 3)
            return false;

        var coveredTokens = ocrTokens
            .Select(token => new
            {
                Token = token,
                Covered = IsCoveredByNativeComparableToken(nativeComparableTokens, token)
            })
            .ToArray();
        var covered = coveredTokens.Count(static token => token.Covered);
        if (covered < 3)
            return false;
        if (nativeHasReplacementCharacters
            && coveredTokens.Any(static token => token.Token.Length >= 5 && !token.Covered))
        {
            return false;
        }

        var coverage = (double)covered / ocrTokens.Length;
        var hasMeaningfulUncoveredToken = coveredTokens.Any(static token => token.Token.Length >= 3 && !token.Covered);
        var requiredCoverage = ocrTokens.Length <= 5 && hasMeaningfulUncoveredToken
            ? 0.80
            : ocrTokens.Length <= 5
                ? 0.75
                : ocrTokens.Length <= 10
                    ? 0.67
                    : 0.55;
        if (coverage < requiredCoverage)
            return false;
        if (!HasLocalNativeCoverage(nativeComparableTokenSequence, ocrTokens, requiredCoverage))
            return false;

        if (ocrTokens.Length <= 5)
            return true;
        if (ocrTokens.Length <= 10)
            return true;

        return covered >= 6;
    }

    private static bool HasLocalNativeCoverage(
        IReadOnlyList<string> nativeComparableTokenSequence,
        IReadOnlyList<string> ocrTokens,
        double requiredCoverage)
    {
        if (nativeComparableTokenSequence.Count == 0 || ocrTokens.Count == 0)
            return false;

        var windowLength = Math.Min(nativeComparableTokenSequence.Count, ocrTokens.Count + 2);
        for (var start = 0; start <= nativeComparableTokenSequence.Count - windowLength; start++)
        {
            var window = nativeComparableTokenSequence
                .Skip(start)
                .Take(windowLength)
                .ToHashSet(StringComparer.Ordinal);
            var covered = ocrTokens.Count(token => IsCoveredByNativeComparableToken(window, token));
            if ((double)covered / ocrTokens.Count >= requiredCoverage)
                return true;
        }

        if (nativeComparableTokenSequence.Count <= windowLength)
        {
            var window = nativeComparableTokenSequence.ToHashSet(StringComparer.Ordinal);
            var covered = ocrTokens.Count(token => IsCoveredByNativeComparableToken(window, token));
            return (double)covered / ocrTokens.Count >= requiredCoverage;
        }

        return false;
    }

    private static bool IsCoveredByNativeComparableToken(HashSet<string> nativeComparableTokens, string token)
    {
        if (nativeComparableTokens.Contains(token))
            return true;
        if (token.Length < 5)
            return false;

        if (token[0] is 'i' or 'l' or '1')
        {
            var suffix = token[1..];
            if (suffix.Length >= 4 && nativeComparableTokens.Contains(suffix))
                return true;
        }

        var maxDistance = token.Length >= 9 ? 2 : 1;
        foreach (var nativeToken in nativeComparableTokens)
        {
            if (token[0] is 'i' or 'l' or '1')
            {
                var suffix = token[1..];
                if (suffix.Length >= 4
                    && Math.Abs(nativeToken.Length - suffix.Length) <= maxDistance
                    && nativeToken[0] == suffix[0]
                    && ComputeBoundedEditDistance(nativeToken, suffix, maxDistance) <= maxDistance)
                {
                    return true;
                }
            }

            if (Math.Abs(nativeToken.Length - token.Length) > maxDistance)
                continue;
            if (nativeToken.Length < 5)
                continue;
            if (nativeToken[0] != token[0])
                continue;
            if (token.Length < 8 && nativeToken[^1] != token[^1])
                continue;
            if (ComputeBoundedEditDistance(nativeToken, token, maxDistance) <= maxDistance)
                return true;
        }

        return false;
    }

    private static int ComputeBoundedEditDistance(string left, string right, int maxDistance)
    {
        if (left == right)
            return 0;
        if (Math.Abs(left.Length - right.Length) > maxDistance)
            return maxDistance + 1;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                rowBest = Math.Min(rowBest, current[j]);
            }

            if (rowBest > maxDistance)
                return maxDistance + 1;

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static ShortOcrLineKind ClassifyShortImportantOcrLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 32)
            return ShortOcrLineKind.None;

        var compact = NormalizeCompactForOcrMerge(line);
        if (compact.Length < 2 || compact.Length > 24 || !compact.Any(char.IsLetter))
            return ShortOcrLineKind.None;
        if (!HasOnlyAllowedShortOcrLineCharacters(line))
            return ShortOcrLineKind.None;

        var tokens = SplitAlphaNumericTokens(line);
        if (tokens.Count == 0 || tokens.Count > 4)
            return ShortOcrLineKind.None;
        if (IsKnownShortControlLabel(tokens))
            return ShortOcrLineKind.Label;
        if (IsShortReferenceCode(tokens) || IsSingleCompactCode(tokens))
            return ShortOcrLineKind.Code;
        if (IsShortTitleLikeLine(line, tokens))
            return ShortOcrLineKind.Heading;

        return ShortOcrLineKind.None;
    }

    private static bool HasOnlyAllowedShortOcrLineCharacters(string line)
    {
        foreach (var ch in line)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                continue;
            if (ch is '-' or '/' or '.' or '_' or '+' or ':' or '\'' or '\u2019' or '\uFFFD')
                continue;

            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> SplitAlphaNumericTokens(string value)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                token.Append(ch);
                continue;
            }

            AddCurrentToken(tokens, token);
        }

        AddCurrentToken(tokens, token);
        return tokens;
    }

    private static void AddCurrentToken(List<string> tokens, StringBuilder token)
    {
        if (token.Length == 0)
            return;

        tokens.Add(token.ToString());
        token.Clear();
    }

    private static bool IsKnownShortControlLabel(IReadOnlyList<string> tokens)
        => tokens.Count == 1
            && tokens[0].All(char.IsLetter)
            && ImportantShortOcrLabels.Contains(tokens[0]);

    private static bool IsShortReferenceCode(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2 || tokens.Count > 4)
            return false;

        var designatorIndex = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (IsNumericToken(tokens[i]) || IsUpperAlphaNumericDesignatorToken(tokens[i]))
            {
                designatorIndex = i;
                break;
            }
        }

        if (designatorIndex <= 0 || CountDigits(tokens[designatorIndex]) < 2)
            return false;

        for (var i = 0; i < designatorIndex; i++)
        {
            if (!IsUpperAlphaToken(tokens[i], minLength: 2, maxLength: 5))
                return false;
        }

        for (var i = designatorIndex + 1; i < tokens.Count; i++)
        {
            if (!IsNumericToken(tokens[i]))
                return false;
        }

        return true;
    }

    private static bool IsSingleCompactCode(IReadOnlyList<string> tokens)
    {
        if (tokens.Count != 1)
            return false;

        var token = tokens[0];
        if (token.Length < 4 || token.Length > 24 || !IsUpperAlphaNumericToken(token))
            return false;

        return token.Count(char.IsLetter) >= 2 && token.Count(char.IsDigit) >= 2;
    }

    private static bool IsShortTitleLikeLine(string line, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2 || tokens.Count > 5)
            return false;
        if (tokens.Any(static token => token.Any(static ch => !char.IsLetter(ch))))
            return false;

        var letterCount = tokens.Sum(static token => token.Length);
        if (letterCount < 7)
            return false;

        var strongTokenCount = tokens.Count(static token => token.Length >= 3);
        if (strongTokenCount < 2 && !tokens.Any(static token => token.Length >= 5))
            return false;

        return HasTitleLikeOcrCasing(line, tokens);
    }

    private static bool HasTitleLikeOcrCasing(string line, IReadOnlyList<string> tokens)
    {
        var letters = line.Where(char.IsLetter).ToArray();
        if (letters.Length == 0)
            return false;

        var upperLetters = letters.Count(char.IsUpper);
        if (upperLetters == letters.Length)
            return true;

        var casedTokenCount = 0;
        foreach (var token in tokens)
        {
            if (token.Length <= 2)
                continue;

            var firstLetter = token.FirstOrDefault(char.IsLetter);
            if (firstLetter != default && char.IsUpper(firstLetter))
                casedTokenCount++;
        }

        return casedTokenCount >= Math.Min(2, tokens.Count(static token => token.Length > 2));
    }

    private static bool IsUpperAlphaToken(string token, int minLength, int maxLength)
        => token.Length >= minLength
            && token.Length <= maxLength
            && token.All(char.IsLetter)
            && token.All(static ch => !char.IsLetter(ch) || char.IsUpper(ch));

    private static bool IsNumericToken(string token)
        => token.Length > 0 && token.All(char.IsDigit);

    private static bool IsUpperAlphaNumericToken(string token)
        => token.All(static ch => char.IsDigit(ch) || (char.IsLetter(ch) && char.IsUpper(ch)));

    private static bool IsUpperAlphaNumericDesignatorToken(string token)
        => token.Length is >= 3 and <= 12
            && IsUpperAlphaNumericToken(token)
            && token.Any(char.IsLetter)
            && CountDigits(token) >= 2;

    private static int CountDigits(string token)
        => token.Count(char.IsDigit);

    private static bool NativeContainsNormalizedOcrLine(
        string nativeNormalized,
        string normalized,
        ShortOcrLineKind shortLineKind)
    {
        if (shortLineKind == ShortOcrLineKind.None)
            return nativeNormalized.Contains(normalized, StringComparison.Ordinal);

        return ContainsWithLetterOrDigitBoundaries(nativeNormalized, normalized);
    }

    private static bool NativeContainsCompactOcrLine(
        string nativeText,
        string nativeCompact,
        string compact,
        ShortOcrLineKind shortLineKind)
    {
        if (shortLineKind == ShortOcrLineKind.Heading)
            return false;

        if (shortLineKind != ShortOcrLineKind.Label)
            return nativeCompact.Contains(compact, StringComparison.Ordinal);

        foreach (var rawLine in nativeText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.Equals(NormalizeCompactForOcrMerge(rawLine), compact, StringComparison.Ordinal))
                return true;

            foreach (var token in SplitAlphaNumericTokens(rawLine))
            {
                if (string.Equals(NormalizeCompactForOcrMerge(token), compact, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsWithLetterOrDigitBoundaries(string value, string needle)
    {
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            var startsAtBoundary = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            var endIndex = index + needle.Length;
            var endsAtBoundary = endIndex == value.Length || !char.IsLetterOrDigit(value[endIndex]);
            if (startsAtBoundary && endsAtBoundary)
                return true;

            index++;
        }

        return false;
    }

    private static string NormalizeForOcrMerge(string value)
        => CollapseWhitespace(FoldDiacritics(value).ToLowerInvariant());

    private static string NormalizeCompactForOcrMerge(string value)
    {
        var normalized = FoldDiacritics(value).ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string CollapseWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task TryDrainAsync(params Task<string>[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task<string?> TryReadAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

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

    private static async Task<bool> RunProcessAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
        => (await RunProcessWithDiagnosticsAsync(fileName, arguments, timeout, ct).ConfigureAwait(false)).Succeeded;

    private static async Task<OcrProcessResult> RunProcessWithDiagnosticsAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OcrProcessResult(false, Stderr: TruncateOcrProcessOutputForDiagnostics(ex.Message));
        }

        using (process)
        {
            if (process is null)
                return new OcrProcessResult(false, Stderr: "process_start_failed");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            bool exited;
            try
            {
                exited = await WaitForExitAsync(process, timeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryKill(process);
                await TryDrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }

            if (!exited)
            {
                TryKill(process);
                var stderr = await TryReadAsync(stderrTask).ConfigureAwait(false);
                await TryDrainAsync(stdoutTask).ConfigureAwait(false);
                return new OcrProcessResult(
                    false,
                    ExitCode: null,
                    TimedOut: true,
                    Stderr: TruncateOcrProcessOutputForDiagnostics(stderr));
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new OcrProcessResult(
                process.ExitCode == 0,
                ExitCode: process.ExitCode,
                Stderr: TruncateOcrProcessOutputForDiagnostics(stderrTask.Result));
        }
    }

    private static string QuoteArgument(string? value)
    {
        var text = value ?? string.Empty;
        return "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
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

    private sealed record OcrLineConfidence(IReadOnlyList<string> Tokens);

    private sealed record OcrWordConfidence(string Token, double Confidence);

    private sealed record OcrProcessResult(
        bool Succeeded,
        int? ExitCode = null,
        bool TimedOut = false,
        string? Stderr = null);
}

sealed record PdfDocumentOcrResult(
    PdfExtractionResult? Extraction,
    PdfOcrDiagnostics Diagnostics);
