using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record NativeTableCoverageSummary(int TableCount, int AuditedTableCount,
    int UnresolvedTableCount, int UnavailableTableCount, int RefusedTableCount,
    int ObservedUnrepresentedWordCount, int? UnrepresentedWordCount, long DurationMs);

/// <summary>
/// Audits real native words against observed cell geometry. It never adds a row,
/// changes cell text, infers a relationship, or equates blank area with lost data.
/// </summary>
internal static partial class CanonicalNativeTableCoverageAuditor
{
    internal const string UnresolvedFlag = "canonical_table_native_region_coverage_unresolved";
    internal const string UnavailableFlag = "canonical_table_native_region_coverage_unavailable";
    internal const string RefusedFlag = "canonical_table_native_region_coverage_refused";
    internal const string ExternalGlyphFlag = "canonical_table_native_glyph_metrics_external";
    private const long MaximumComparisonsPerTable = 5_000_000;
    private const long MaximumComparisonsPerDocument = 20_000_000;

    internal static NativeTableCoverageSummary Apply(CanonicalDocument document, PdfExtractionResult? extraction,
        CancellationToken ct = default, int maximumWordsPerPage = PdfNativeWordExtractor.MaximumWordsPerPage,
        int maximumWordsPerDocument = PdfNativeWordExtractor.MaximumWordsPerDocument)
    {
        ct.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var audited = 0; var unresolved = 0; var unavailable = 0; var refused = 0; var observedMissing = 0;
        var remainingWords = Math.Clamp(maximumWordsPerDocument, 0, PdfNativeWordExtractor.MaximumWordsPerDocument);
        maximumWordsPerPage = Math.Clamp(maximumWordsPerPage, 0, PdfNativeWordExtractor.MaximumWordsPerPage);
        long remainingComparisons = MaximumComparisonsPerDocument;
        var nativePages = extraction?.Pages.GroupBy(page => page.PageNumber).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var page in document.Pages.OrderBy(page => page.PageNumber))
        {
            ct.ThrowIfCancellationRequested();
            page.QualityFlags.RemoveAll(flag => flag is UnresolvedFlag or UnavailableFlag or RefusedFlag or ExternalGlyphFlag);
            ExtractedPdfPage? native = null;
            if (nativePages is not null && nativePages.TryGetValue(page.PageNumber, out var candidates) && candidates.Length == 1)
                native = candidates[0];
            var pageReason = PageRefusal(document, page, native, maximumWordsPerPage, ref remainingWords, ct);
            if (native?.NativeWordObservation is { GlyphGeometryAvailability: "available", GlyphGeometryExternalFontCount: > 0 })
                AddFlag(page, ExternalGlyphFlag);
            foreach (var table in page.Tables)
            {
                ct.ThrowIfCancellationRequested();
                var attributes = table.Provenance.Attributes;
                foreach (var key in attributes.Keys.Where(key => key.StartsWith("nativeTableCoverage", StringComparison.Ordinal)).ToArray())
                    attributes.Remove(key);
                attributes["nativeTableCoverageSchema"] = "native_table_physical_word_coverage_v1";
                attributes["nativeTableCoverageFidelity"] = "not_asserted";
                attributes["nativeTableCoverageRelations"] = "not_asserted";
                attributes["nativeTableCoverageTextLoss"] = "not_asserted";
                attributes["nativeTableCoverageGlyphGeometryAvailability"] = native?.NativeWordObservation?.GlyphGeometryAvailability ?? "unknown";
                attributes["nativeTableCoverageGlyphGeometryEngineVersion"] = native?.NativeWordObservation?.GlyphGeometryEngineVersion ?? "unknown";
                attributes["nativeTableCoverageGlyphGeometryMethod"] = native?.NativeWordObservation?.GlyphGeometryExternalFontCount > 0
                    ? "external_or_substitute_program" : native?.NativeWordObservation?.GlyphGeometryStandard14FontCount > 0
                        ? "standard14_afm" : "unverified";
                if (pageReason is not null)
                {
                    var frameUnavailable = pageReason == "native_coordinate_frame_unavailable"
                        || pageReason.StartsWith("unsupported_native_", StringComparison.Ordinal);
                    Unknown(pageReason, frameUnavailable ? null : native?.NativeWordObservation?.Availability);
                    continue;
                }
                var width = native!.WidthPoints!.Value; var height = native.HeightPoints!.Value;
                if (!TryBounds(table.Polygon, width, height, out var tableBounds)) { Unknown("invalid_table_geometry"); continue; }
                var cells = new List<Bounds>();
                foreach (var cell in table.Cells)
                {
                    if (!TryBounds(cell.Polygon, width, height, out var bounds)) break;
                    cells.Add(bounds);
                }
                if (cells.Count != table.Cells.Count || cells.Count == 0) { Unknown("invalid_cell_geometry"); continue; }
                var inside = native.NativeWordObservation!.Words.Where(word => !string.IsNullOrWhiteSpace(word.Text))
                    .Where(word => TryWordBounds(word, width, height, out var box) && tableBounds.Contains(box)).ToArray();
                if (inside.Length == 0) { Unknown("no_native_words_in_table_region"); continue; }
                var comparisons = (long)inside.Length * cells.Count;
                if (comparisons > MaximumComparisonsPerTable || comparisons > remainingComparisons)
                { Unknown("comparison_limit", "refused"); continue; }
                remainingComparisons -= comparisons;
                var missing = new List<PdfNativeObservedWord>();
                foreach (var word in inside)
                {
                    ct.ThrowIfCancellationRequested();
                    TryWordBounds(word, width, height, out var box);
                    // Positive gap evidence requires disjoint real word/cell
                    // geometry after a 1pt tolerance, not an uncovered area ratio.
                    if (!cells.Any(cell => cell.Intersects(box, 1))) missing.Add(word);
                }
                audited++;
                observedMissing += missing.Count;
                attributes["nativeTableCoverageAvailability"] = "available";
                attributes["nativeTableCoverageObservedWordCount"] = inside.Length.ToString(CultureInfo.InvariantCulture);
                attributes["nativeTableCoverageUnrepresentedWordCount"] = missing.Count.ToString(CultureInfo.InvariantCulture);
                attributes["nativeTableCoverageOutcome"] = missing.Count > 0
                    ? "unrepresented_native_words_observed" : "observed_native_words_geometrically_represented";
                attributes["nativeTableCoverageEvidenceSha256"] = EvidenceHash(document, page, table, inside);
                if (missing.Count > 0)
                {
                    unresolved++;
                    AddFlag(page, UnresolvedFlag);
                }

                void Unknown(string reason, string? observedAvailability = null)
                {
                    var isRefused = observedAvailability == "refused" || reason.EndsWith("_limit", StringComparison.Ordinal);
                    var availability = isRefused ? "refused" : observedAvailability == "partial" ? "partial" : "unavailable";
                    attributes["nativeTableCoverageAvailability"] = availability;
                    attributes["nativeTableCoverageOutcome"] = reason;
                    AddFlag(page, isRefused ? RefusedFlag : UnavailableFlag);
                    if (isRefused) refused++; else unavailable++;
                    // Unknown/refused counts and evidence digests are absent,
                    // never zero-valued successful coverage measurements.
                }
            }
        }
        var total = document.Pages.Sum(page => page.Tables.Count);
        return new(total, audited, unresolved, unavailable, refused, observedMissing,
            total > 0 && unavailable == 0 && refused == 0 ? observedMissing : null,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static void AddFlag(CanonicalPage page, string flag)
    { if (!page.QualityFlags.Contains(flag, StringComparer.Ordinal)) page.QualityFlags.Add(flag); }

    private static string EvidenceHash(CanonicalDocument document, CanonicalPage page,
        CanonicalTable table, IEnumerable<PdfNativeObservedWord> words)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"native_table_physical_word_coverage_v1|{document.Source.Sha256}|{page.PageNumber}|{table.TableId}\n"));
        foreach (var word in words.OrderBy(word => word.Ordinal))
        {
            var textSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(word.Text)));
            hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"{word.Ordinal}|{textSha}|{word.Left:R}|{word.Right:R}|{word.Top:R}|{word.Bottom:R}\n")));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
