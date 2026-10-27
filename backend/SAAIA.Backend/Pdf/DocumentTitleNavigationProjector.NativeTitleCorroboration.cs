using System.Text;
using SAAIA.Contracts.DocumentIntelligence;

internal static partial class DocumentTitleNavigationProjector
{
    private sealed class NativeTitleNavigationContext(CanonicalDocument document,
        IReadOnlyDictionary<string, List<int>> publishedCellChunks, CancellationToken ct)
    {
        private const int MaximumCandidates = 32;
        private const int MaximumSlices = 64;
        private const long MaximumScanUnits = 2_000_000;
        private const int MaximumEvidenceBytes = 16 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly Dictionary<int, NativeTitlePage> pages = [];
        private readonly Dictionary<string, int> reasons = new(StringComparer.Ordinal);
        private int candidates, links, refused, pagesValidated;
        private long scanUnits;

        internal CanonicalNavigationNativeTitleDiagnostics? Snapshot() => candidates == 0 ? null
            : new(candidates, links, refused, pagesValidated, scanUnits,
                new Dictionary<string, int>(reasons, StringComparer.Ordinal));

        internal void RecordLink() => links++;
        internal void RefuseMetadata() => Refuse("native_title_metadata_byte_limit");
        internal void RefuseCombinedLimit(int omitted)
        {
            links -= omitted;
            for (var i = 0; i < omitted; i++) Refuse("native_title_combined_entry_limit");
        }

        internal bool TryCorroborate(CanonicalPage page, CanonicalNavigationRowEvidence row,
            CanonicalNavigationTarget? target, bool targetInventoryComplete,
            out CanonicalNavigationNativeTitleEvidence? evidence)
        {
            evidence = null;
            ct.ThrowIfCancellationRequested();
            // An absent channel with no refusal flag is the legacy/OFF path. It gains
            // neither additional metadata nor an apparent observation of zero glyphs.
            if (page.NativeTypographyObservations is null
                && !page.QualityFlags.Any(flag => flag.StartsWith("native_typography_", StringComparison.Ordinal)))
                return false;
            candidates++;
            if (candidates > MaximumCandidates) return Refuse("native_title_candidate_limit");
            if (!targetInventoryComplete) return Refuse("native_title_target_inventory_incomplete");
            if (target is null) return Refuse("native_title_target_not_unique");
            try { _ = StrictUtf8.GetByteCount(row.Title); }
            catch (EncoderFallbackException) { return Refuse("native_title_invalid_unicode"); }
            if (!pages.TryGetValue(page.PageNumber, out var native))
            {
                native = ReadPage(page);
                pages.Add(page.PageNumber, native);
            }
            if (native.Reason is not null) return Refuse(native.Reason);
            // Reserve both complete ordinal searches before certifying uniqueness.
            // The deterministic counter is cumulative, including building page maps.
            if (!Charge(2L * native.Text.Length)) return Refuse("native_title_scan_limit");
            var first = native.Text.IndexOf(row.Title, StringComparison.Ordinal);
            if (first < 0) return Refuse("native_title_literal_not_found");
            if (native.Text.IndexOf(row.Title, first + 1, StringComparison.Ordinal) >= 0)
                return Refuse("native_title_literal_ambiguous");
            var end = first + row.Title.Length;
            if (first > 0 && !char.IsWhiteSpace(native.Text[first - 1])
                || !CompleteTitleEnd(native.Text, end))
                return Refuse("native_title_literal_boundary_unproven");
            var firstCharacter = native.Characters[first];
            var lastCharacter = native.Characters[end - 1];
            var firstGlyph = firstCharacter.Glyph;
            var lastGlyph = lastCharacter.Glyph;
            var slices = new List<CanonicalNavigationNativeRunSlice>();
            var reconstructed = new StringBuilder();
            var titleCellIds = row.TitleCells.Select(cell => cell.CellId).ToHashSet(StringComparer.Ordinal);
            if (!CompleteNativeCellTitle(page, native, titleCellIds, row, out var completenessReason))
                return Refuse(completenessReason);
            foreach (var run in native.Runs)
            {
                ct.ThrowIfCancellationRequested();
                if (run.SourceLetterOrdinalEnd < firstGlyph.NativeLetterOrdinal
                    || run.SourceLetterOrdinalStart > lastGlyph.NativeLetterOrdinal) continue;
                var start = run == firstCharacter.Run ? firstGlyph.RunTextOffsetUtf16 : 0;
                var finish = run == lastCharacter.Run
                    ? lastGlyph.RunTextOffsetUtf16 + lastGlyph.RunTextLengthUtf16 : run.Text.Length;
                if (slices.Count == MaximumSlices) return Refuse("native_title_slice_limit");
                slices.Add(new(run.RunId, run.TextSha256, start, finish - start));
                reconstructed.Append(run.Text.AsSpan(start, finish - start));
                foreach (var glyph in run.Glyphs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (glyph.NativeLetterOrdinal < firstGlyph.NativeLetterOrdinal
                        || glyph.NativeLetterOrdinal > lastGlyph.NativeLetterOrdinal) continue;
                    var glyphText = run.Text.AsSpan(glyph.RunTextOffsetUtf16, glyph.RunTextLengthUtf16);
                    if (AllWhitespace(glyphText)) continue;
                    if (!BaselineMembershipIsUnique(page, titleCellIds, native.Cells, run, glyph, out var reason))
                        return Refuse(reason);
                }
            }
            // Expand to complete glyphs, then compare again. A glyph containing an
            // unmatched letter can never be sliced into a convenient partial witness.
            if (!string.Equals(CanonicalNavigationWhitespace(reconstructed.ToString()), row.Title, StringComparison.Ordinal))
                return Refuse("native_title_partial_glyph");
            var proof = new CanonicalNavigationNativeTitleEvidence("native_title_navigation_v1", page.PageNumber,
                document.Source.Sha256, CanonicalNativeTypographyIdentity.Hash(row.Title), "ordinal_whitespace_v1",
                "baseline_points_only", "source_row_physical_overlap_observed", "not_corroborated", slices);
            if (Encoding.UTF8.GetByteCount(CanonicalContractJson.SerializeCanonical(proof)) > MaximumEvidenceBytes)
                return Refuse("native_title_evidence_byte_limit");
            ct.ThrowIfCancellationRequested();
            evidence = proof;
            return true;
        }

        private NativeTitlePage ReadPage(CanonicalPage page)
        {
            ct.ThrowIfCancellationRequested();
            var set = page.NativeTypographyObservations;
            if (set is null) return Unavailable("native_title_channel_unavailable_or_refused");
            if (set.RunAvailability != "observed") return Unavailable("native_title_runs_unavailable_or_refused");
            if (set.Runs is not { Count: > 0 }) return Unavailable("native_title_no_observed_runs");
            if (set.DocumentId != document.DocumentId || set.RevisionId != document.RevisionId
                || set.SourceSha256 != document.Source.Sha256 || set.PhysicalPage != page.PageNumber
                || set.SourceHandleSha256Before != set.SourceSha256 || set.SourceHandleSha256After != set.SourceSha256)
                return Unavailable("native_title_source_identity_mismatch");
            var frame = set.CoordinateFrame;
            if (page.Geometry.Unit != "pt" || page.Geometry.RotationDegrees != 0
                || page.Geometry.CoordinateSpace != CanonicalSchema.NormalizedTopLeftCoordinateSpace
                || frame.Availability != "available" || frame.RawOrigin != "BOTTOMLEFT"
                || frame.ObservedRotationDegrees != 0 || frame.ObservedCropBoxLeft != 0
                || frame.ObservedCropBoxBottom != 0 || frame.ObservedMediaBoxLeft != 0
                || frame.ObservedMediaBoxBottom != 0 || frame.PageWidthPoints != page.Geometry.Width
                || frame.PageHeightPoints != page.Geometry.Height)
                return Unavailable("native_title_coordinate_frame_unavailable");
            try
            {
                // Once per physical page, never once per row or glyph. This includes
                // IDs, text hashes, complete glyph counters and literal UTF16 offsets.
                if (CanonicalNativeTypographyValidator.Validate(set).Count != 0)
                    return Unavailable("native_title_native_contract_invalid");
                pagesValidated++;
                ct.ThrowIfCancellationRequested();
                var totalCharacters = set.Runs.Sum(run => (long)run.Text.Length);
                if (!Charge(totalCharacters)) return Unavailable("native_title_scan_limit");
                var text = new StringBuilder();
                var characters = new List<NativeTitleCharacter>();
                var cells = new List<NativeTitleCell>();
                foreach (var table in page.Tables)
                {
                    if (table.Provenance.Attributes.GetValueOrDefault("doclingLabel") != "document_index") continue;
                    foreach (var cell in table.Cells)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!publishedCellChunks.ContainsKey(cell.CellId)) continue;
                        if (!Charge(1)) return Unavailable("native_title_scan_limit");
                        if (!TryNavigationBox(cell.Polygon, out var box))
                            return Unavailable("native_title_source_cell_geometry_unavailable");
                        cells.Add(new(cell.CellId, box, table.TableId, cell.RowIndex, cell.Text.Raw));
                    }
                }
                NativeTitleCharacter? pendingWhitespace = null;
                foreach (var run in set.Runs)
                {
                    ct.ThrowIfCancellationRequested();
                    _ = StrictUtf8.GetByteCount(run.Text);
                    foreach (var glyph in run.Glyphs)
                    {
                        ct.ThrowIfCancellationRequested();
                        var start = glyph.RunTextOffsetUtf16;
                        var end = start + glyph.RunTextLengthUtf16;
                        if (!ScalarBoundary(run.Text, start) || !ScalarBoundary(run.Text, end))
                            return Unavailable("native_title_split_unicode_scalar");
                        var character = new NativeTitleCharacter(run, glyph);
                        for (var offset = start; offset < end; offset++)
                        {
                            var value = run.Text[offset];
                            if (char.IsWhiteSpace(value))
                            {
                                if (text.Length > 0) pendingWhitespace ??= character;
                                continue;
                            }
                            if (pendingWhitespace is not null)
                            {
                                text.Append(' ');
                                characters.Add(pendingWhitespace);
                                pendingWhitespace = null;
                            }
                            text.Append(value);
                            characters.Add(character);
                        }
                    }
                }
                return new(text.ToString(), characters, set.Runs, cells, null);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return Unavailable("native_title_native_contract_invalid");
            }
        }

        private bool BaselineMembershipIsUnique(CanonicalPage page, HashSet<string> titleCellIds,
            IReadOnlyList<NativeTitleCell> cells, CanonicalNativeTypographyRun run, CanonicalNativeTypographyGlyph glyph,
            out string refusal)
        {
            refusal = "native_title_baseline_membership_unproven";
            if (glyph.StartBaselineX is not double x1 || glyph.EndBaselineX is not double x2
                || run.BaselineStartY is not double y1 || run.BaselineEndY is not double y2
                || !double.IsFinite(x1) || !double.IsFinite(x2) || !double.IsFinite(y1) || !double.IsFinite(y2)) return false;
            if (!Charge(cells.Count))
            {
                refusal = "native_title_scan_limit";
                return false;
            }
            var firstX = x1 / page.Geometry.Width;
            var lastX = x2 / page.Geometry.Width;
            var firstY = (page.Geometry.Height - y1) / page.Geometry.Height;
            var lastY = (page.Geometry.Height - y2) / page.Geometry.Height;
            var matches = 0;
            string? owner = null;
            foreach (var cell in cells)
            {
                ct.ThrowIfCancellationRequested();
                var box = cell.Box;
                if (firstX >= box.L && firstX <= box.R && lastX >= box.L && lastX <= box.R
                    && firstY >= box.T && firstY <= box.B && lastY >= box.T && lastY <= box.B)
                {
                    matches++;
                    owner = cell.CellId;
                }
            }
            return matches == 1 && owner is not null && titleCellIds.Contains(owner);
        }

        private bool CompleteNativeCellTitle(CanonicalPage page, NativeTitlePage native,
            HashSet<string> titleCellIds, CanonicalNavigationRowEvidence row, out string refusal)
        {
            refusal = "native_title_cell_text_not_complete";
            var width = page.Geometry.Width.GetValueOrDefault();
            var height = page.Geometry.Height.GetValueOrDefault();
            if (width <= 0 || height <= 0) return false;
            var text = new StringBuilder();
            var pendingWhitespace = false;
            var source = native.Cells.First(cell => titleCellIds.Contains(cell.CellId));
            var ownedText = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            var excludedOwners = new Dictionary<string, NativeTitleCell>(StringComparer.Ordinal);
            foreach (var run in native.Runs)
            foreach (var glyph in run.Glyphs)
            {
                ct.ThrowIfCancellationRequested();
                var literal = run.Text.AsSpan(glyph.RunTextOffsetUtf16, glyph.RunTextLengthUtf16);
                var whitespace = AllWhitespace(literal);
                if (whitespace) pendingWhitespace = text.Length > 0;
                if (!Charge(native.Cells.Count))
                {
                    refusal = "native_title_scan_limit";
                    return false;
                }
                if (glyph.StartBaselineX is not double x1 || glyph.EndBaselineX is not double x2
                    || run.BaselineStartY is not double y1 || run.BaselineEndY is not double y2
                    || !double.IsFinite(x1) || !double.IsFinite(x2) || !double.IsFinite(y1) || !double.IsFinite(y2))
                    return false;
                var firstX = x1 / width;
                var lastX = x2 / width;
                var firstY = (height - y1) / height;
                var lastY = (height - y2) / height;
                var matches = 0;
                var touchesTitle = false;
                var fullyInTitle = false;
                NativeTitleCell? owner = null;
                foreach (var cell in native.Cells)
                {
                    ct.ThrowIfCancellationRequested();
                    var box = cell.Box;
                    var titleCell = titleCellIds.Contains(cell.CellId);
                    // A baseline extending across a title-cell boundary is ambiguous.
                    // Its enclosing segment bounds only refuse evidence; they never
                    // manufacture glyph geometry or establish positive membership.
                    if (titleCell && Math.Max(firstX, lastX) > box.L && Math.Min(firstX, lastX) < box.R
                        && Math.Max(firstY, lastY) >= box.T && Math.Min(firstY, lastY) <= box.B)
                        touchesTitle = true;
                    if (firstX < box.L || firstX > box.R || lastX < box.L || lastX > box.R
                        || firstY < box.T || firstY > box.B || lastY < box.T || lastY > box.B) continue;
                    matches++;
                    fullyInTitle |= titleCell;
                    owner = cell;
                }
                if (matches == 1 && owner is not null)
                {
                    if (!ownedText.TryGetValue(owner.CellId, out var nativeText))
                        ownedText.Add(owner.CellId, nativeText = new StringBuilder());
                    nativeText.Append(literal);
                }
                if (whitespace) continue;
                if (!touchesTitle && !fullyInTitle) continue;
                if (matches != 1 || owner is null) return false;
                if (!fullyInTitle)
                {
                    // An exact positive owner in this same published source row may
                    // exclude the glyph from the title. Full owner text is checked
                    // below; merely intersecting a neighboring box never suffices.
                    if (owner.TableId != source.TableId || owner.RowIndex != source.RowIndex) return false;
                    excludedOwners[owner.CellId] = owner;
                    continue;
                }
                if (pendingWhitespace) text.Append(' ');
                pendingWhitespace = false;
                text.Append(literal);
            }
            var complete = CanonicalNavigationWhitespace(text.ToString());
            foreach (var owner in excludedOwners.Values)
                if (!ownedText.TryGetValue(owner.CellId, out var literal)
                    || !string.Equals(CanonicalNavigationWhitespace(literal.ToString()),
                        CanonicalNavigationWhitespace(owner.Text), StringComparison.Ordinal)) return false;
            if (!complete.StartsWith(row.Title, StringComparison.Ordinal)) return false;
            var end = row.Title.Length;
            if (!CompleteTitleEnd(complete, end)) return false;
            // All remaining content belonging to the title cells must be leaders.
            // This prevents a shorter canonical heading from certifying a native
            // title such as "Scope of application" merely by finding "Scope".
            for (var i = end; i < complete.Length; i++)
                if (!char.IsWhiteSpace(complete[i]) && complete[i] is not ('.' or '·' or '•')) return false;
            return true;
        }

        private bool Charge(long units)
        {
            if (units < 0 || units > MaximumScanUnits - scanUnits) return false;
            scanUnits += units;
            return true;
        }

        private bool Refuse(string reason)
        {
            refused++;
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
            return false;
        }

        private static NativeTitlePage Unavailable(string reason) => new("", [], [], [], reason);
        private static bool ScalarBoundary(string text, int offset) => offset >= 0 && offset <= text.Length
            && (offset == 0 || offset == text.Length || !char.IsLowSurrogate(text[offset]) || !char.IsHighSurrogate(text[offset - 1]));
        private static bool CompleteTitleEnd(string text, int end)
        {
            if (end == text.Length || char.IsWhiteSpace(text[end])) return true;
            // Point leaders are a complete separator only as a run ending at a
            // whitespace boundary or page-text end. A dot inside a word is not one.
            var offset = end;
            while (offset < text.Length && text[offset] is '.' or '·' or '•') offset++;
            return offset - end >= 2 && (offset == text.Length || char.IsWhiteSpace(text[offset]));
        }
        private static bool AllWhitespace(ReadOnlySpan<char> value)
        {
            foreach (var character in value) if (!char.IsWhiteSpace(character)) return false;
            return true;
        }

        private sealed record NativeTitleCharacter(CanonicalNativeTypographyRun Run, CanonicalNativeTypographyGlyph Glyph);
        private sealed record NativeTitleCell(string CellId, (double L, double T, double R, double B) Box,
            string TableId, int RowIndex, string Text);
        private sealed record NativeTitlePage(string Text, List<NativeTitleCharacter> Characters,
            IReadOnlyList<CanonicalNativeTypographyRun> Runs, IReadOnlyList<NativeTitleCell> Cells, string? Reason);
    }
}
