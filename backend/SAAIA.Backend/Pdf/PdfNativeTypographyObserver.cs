using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

internal sealed record PdfNativeTypographyObservation(CanonicalNativeTypographySet Observation, long ObserverTicks, int InspectedGlyphCount);

/// <summary>Only this observer's elapsed time is charged, never time between PDF pages.</summary>
internal sealed class PdfNativeTypographyBudget
{
    internal int RemainingGlyphs = CanonicalNativeTypographyValidator.MaximumGlyphsPerDocument;
    internal int RemainingRuns = CanonicalNativeTypographyValidator.MaximumRunsPerDocument;
    internal int RemainingResources = CanonicalNativeTypographyValidator.MaximumResourcesPerDocument;
    internal long RemainingBytes = CanonicalNativeTypographyValidator.MaximumDocumentBytes;
    internal long RemainingTicks = 5 * Stopwatch.Frequency;
}

internal static partial class PdfNativeTypographyObserver
{
    private static readonly Lazy<string> AssemblySha = new(() =>
    {
        using var input = File.OpenRead(typeof(Page).Assembly.Location);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    });
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private sealed class Refusal(string code) : Exception { internal string Code { get; } = code; }
    private sealed record StyleKey(string? Font, bool? Bold, bool? Italic, int? Weight,
        double? Size, string Mode, int Sequence, double? BaselineStartY, double? BaselineEndY);

    internal static PdfNativeTypographyObservation Observe(Page page, Structure? structure, string sourceSha,
        PdfNativeWordObservation? geometry, PdfNativeTypographyBudget budget, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp(); var inspected = 0;
        var set = new CanonicalNativeTypographySet
        {
            SourceSha256 = sourceSha, SourceHandleSha256Before = sourceSha, SourceHandleSha256After = sourceSha,
            PhysicalPage = page.Number, PdfPigAssemblyVersion = typeof(Page).Assembly.GetName().Version?.ToString() ?? "unavailable",
            PdfPigAssemblySha256 = AssemblySha.Value,
            CoordinateFrame = new() { PageWidthPoints = page.Width, PageHeightPoints = page.Height },
            ResourcesReason = "native_typography_not_observed", RunReason = "native_typography_not_observed"
        };
        try
        {
            CheckBudget();
            var frame = PdfNativeCoordinateFrame.Observe(page, structure, ct);
            set.CoordinateFrame = new()
            {
                Availability = frame.Availability, PageWidthPoints = page.Width, PageHeightPoints = page.Height,
                ObservedRotationDegrees = frame.RotationDegrees, ObservedCropBoxLeft = frame.CropBoxLeft,
                ObservedCropBoxBottom = frame.CropBoxBottom, ObservedMediaBoxLeft = frame.MediaBoxLeft,
                ObservedMediaBoxBottom = frame.MediaBoxBottom
            };
            try
            {
                set.Resources = ObserveResources(page, structure, sourceSha, budget, CheckBudget);
                set.ResourcesAvailability = "observed"; set.ResourcesReason = null;
            }
            catch (Refusal error) { set.ResourcesAvailability = "refused"; set.ResourcesReason = error.Code; set.Resources = null; }
            catch (OperationCanceledException) { throw; }
            catch { set.ResourcesAvailability = "unavailable"; set.ResourcesReason = "native_typography_resources_unavailable"; set.Resources = null; }

            CheckBudget();
            var candidates = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var font in set.Resources ?? [])
            foreach (var name in new[] { font.BaseFont, font.DescriptorName }.Where(name => name is not null).Distinct(StringComparer.Ordinal))
            {
                if (!candidates.TryGetValue(name!, out var ids)) candidates[name!] = ids = [];
                ids.Add(font.ResourceId);
            }
            var runs = new List<CanonicalNativeTypographyRun>();
            var buffers = new List<StringBuilder>(); StyleKey? previous = null;
            foreach (var letter in page.Letters)
            {
                CheckBudget();
                if (inspected >= CanonicalNativeTypographyValidator.MaximumGlyphsPerPage) throw new Refusal("native_typography_page_glyph_limit");
                if (budget.RemainingGlyphs <= 0) throw new Refusal("native_typography_document_glyph_limit");
                budget.RemainingGlyphs--; var ordinal = inspected++;
                var text = letter.Value ?? "";
                if (text.Length > 32) throw new Refusal("native_typography_glyph_text_limit");
                _ = StrictUtf8.GetByteCount(text);
                var details = letter.FontDetails;
                var fontName = details?.Name;
                if (fontName?.Length > 512) throw new Refusal("native_typography_font_name_limit");
                var size = double.IsFinite(letter.PointSize) && letter.PointSize > 0 ? (double?)letter.PointSize : null;
                var start = Point(letter.StartBaseLine); var end = Point(letter.EndBaseLine);
                var mode = letter.RenderingMode.ToString();
                var key = new StyleKey(fontName, details?.IsBold, details?.IsItalic, details?.Weight, size,
                    mode, letter.TextSequence, start?.Y, end?.Y);
                if (previous != key)
                {
                    if (runs.Count >= CanonicalNativeTypographyValidator.MaximumRunsPerPage) throw new Refusal("native_typography_page_run_limit");
                    if (budget.RemainingRuns <= 0) throw new Refusal("native_typography_document_run_limit");
                    budget.RemainingRuns--;
                    var ids = fontName is not null && candidates.TryGetValue(fontName, out var found)
                        ? found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() : null;
                    var fontGeometry = geometry?.GlyphGeometryFonts.Where(font => font.FontName == fontName).ToArray();
                    var metrics = fontGeometry is { Length: 1 } ? fontGeometry[0] : null;
                    runs.Add(new()
                    {
                        RunOrdinal = runs.Count, SourceLetterOrdinalStart = ordinal, SourceLetterOrdinalEnd = ordinal,
                        ReportedFontName = fontName, ReportedFontDetailsAvailability = details is null ? "unavailable" : "observed",
                        ReportedBold = details?.IsBold, ReportedItalic = details?.IsItalic, ReportedWeight = details?.Weight,
                        ReportedFontSize = size, RenderingMode = mode, TextSequence = letter.TextSequence,
                        BaselineStartY = start?.Y, BaselineEndY = end?.Y,
                        GeometryMetricsAvailability = metrics?.Availability is "available" or "unavailable" ? metrics.Availability : "unknown",
                        GeometryMetricsMethod = metrics?.MetricsMethod ?? "unverified",
                        CandidateResourceIds = ids, ResourceAssociation = ids is null ? "unavailable" : ids.Count == 1 ? "unique_name_candidate" : "ambiguous"
                    });
                    buffers.Add(new()); previous = key;
                }
                var run = runs[^1]; var buffer = buffers[^1];
                run.Glyphs.Add(new()
                {
                    NativeLetterOrdinal = ordinal, RunTextOffsetUtf16 = buffer.Length, RunTextLengthUtf16 = text.Length,
                    StartBaselineX = start?.X, EndBaselineX = end?.X
                });
                buffer.Append(text); run.SourceLetterOrdinalEnd = ordinal;
            }
            for (var index = 0; index < runs.Count; index++)
            {
                CheckBudget(); var run = runs[index]; run.Text = buffers[index].ToString();
                run.TextSha256 = CanonicalNativeTypographyIdentity.Hash(run.Text);
                run.RunId = CanonicalNativeTypographyIdentity.RunId(sourceSha, page.Number, run);
            }
            set.Runs = runs; set.RunAvailability = "observed"; set.RunReason = null;
            set.ObservedGlyphCount = inspected; set.TotalGlyphCount = inspected;
        }
        catch (Refusal error) { RefuseRuns(error.Code); }
        catch (OperationCanceledException) { throw; }
        catch { set.RunAvailability = "unavailable"; set.RunReason = "native_typography_observation_unavailable"; set.Runs = null; set.ObservedGlyphCount = set.TotalGlyphCount = null; }
        try
        {
            CheckBudget();
            var bytes = Encoding.UTF8.GetByteCount(CanonicalContractJson.SerializeCanonical(set));
            if (bytes > CanonicalNativeTypographyValidator.MaximumPageBytes || bytes > budget.RemainingBytes)
            {
                set.Resources = null; set.ResourcesAvailability = "refused"; set.ResourcesReason = "native_typography_serialization_limit";
                RefuseRuns("native_typography_serialization_limit");
                bytes = Encoding.UTF8.GetByteCount(CanonicalContractJson.SerializeCanonical(set));
            }
            budget.RemainingBytes -= bytes;
        }
        catch (Refusal error) { RefuseRuns(error.Code); }
        var ticks = Stopwatch.GetTimestamp() - started; budget.RemainingTicks -= ticks;
        return new(set, ticks, inspected);

        void CheckBudget()
        {
            ct.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() - started >= budget.RemainingTicks) throw new Refusal("native_typography_observer_time_limit");
        }
        void RefuseRuns(string reason)
        { set.Runs = null; set.RunAvailability = "refused"; set.RunReason = reason; set.ObservedGlyphCount = set.TotalGlyphCount = null; }
    }

    private static CanonicalPoint? Point(PdfPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y)
        && Math.Abs(point.X) <= 10_000_000 && Math.Abs(point.Y) <= 10_000_000 ? new() { X = point.X, Y = point.Y } : null;
}
