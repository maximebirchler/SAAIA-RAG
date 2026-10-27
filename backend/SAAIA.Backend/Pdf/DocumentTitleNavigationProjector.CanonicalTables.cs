using System.Text.RegularExpressions;
using SAAIA.Contracts.DocumentIntelligence;

internal static partial class DocumentTitleNavigationProjector
{
    private const int MaxCanonicalNavigationCells = 20_000;
    private const int MaxCanonicalNavigationChunks = 10_000;
    private const int MaxCanonicalNavigationTables = 1_024;
    private const int MaxCanonicalNavigationPages = 10_000;
    private const int MaxCanonicalCellReferences = 100_000;
    private const int MaxCanonicalRowCells = 64;
    private const int MaxCanonicalRowCharacters = 4_096;

    private static bool IsCanonicalNavigationChunk(ProjectedRetrievalChunk chunk)
        => IsNavigationChunk(chunk) && (chunk.StructuredTableProjection is not null
            || chunk.ChunkComposition?.StartsWith("canonical_table_", StringComparison.Ordinal) == true
            || string.Equals(chunk.OriginalChunkType, DoclingCanonicalRetrievalProjector.TableChunkType, StringComparison.Ordinal)
            || string.Equals(chunk.ChunkType, DoclingCanonicalRetrievalProjector.TableChunkType, StringComparison.Ordinal)
            || chunk.CanonicalTableCellIds is { Count: > 0 });

    private static CanonicalNavigationProjection? ProjectCanonicalNavigation(CanonicalDocument? document,
        IReadOnlyList<ProjectedRetrievalChunk> sources, IReadOnlyList<ProjectedRetrievalChunk> published,
        IReadOnlyList<ProjectedDocumentTitleAnchor> anchors, CancellationToken ct)
    {
        var recognized = sources.Where(IsCanonicalNavigationChunk).Take(MaxCanonicalNavigationChunks + 1).ToArray();
        if (recognized.Length == 0)
            return null;
        var audit = new CanonicalNavigationAudit { Chunks = recognized.Length };
        var entries = new List<ProjectedDocumentNavigationEntry>();
        var result = new CanonicalNavigationProjection(entries, audit);
        if (recognized.Length > MaxCanonicalNavigationChunks)
        { audit.ChunkInventoryComplete = false; audit.Refuse("source_chunk_limit", limit: true, row: false); return result; }
        if (document is null)
        { audit.Refuse("canonical_document_unavailable", row: false); return result; }
        if (document.Pages.Count > MaxCanonicalNavigationPages || published.Count > MaxCanonicalNavigationChunks)
        { audit.Refuse("page_or_published_chunk_limit", limit: true, row: false); return result; }

        // No text is read from markup. Cell membership comes from the canonical document and
        // the exact chunks admitted by the publication filter, not the pre-filter source list.
        var cells = new Dictionary<string, (CanonicalPage Page, CanonicalTable Table)>(StringComparer.Ordinal);
        var tables = new List<(CanonicalPage Page, CanonicalTable Table)>();
        foreach (var page in document.Pages.OrderBy(page => page.PageNumber))
        foreach (var table in page.Tables.OrderBy(table => table.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!table.Provenance.Attributes.TryGetValue("doclingLabel", out var label) || label != "document_index")
                continue;
            if (tables.Count == MaxCanonicalNavigationTables || table.Cells.Count > MaxCanonicalNavigationCells - cells.Count)
            { audit.Refuse("source_cell_or_table_limit", limit: true, row: false); return result; }
            tables.Add((page, table));
            foreach (var cell in table.Cells)
                if (string.IsNullOrEmpty(cell.CellId) || !cells.TryAdd(cell.CellId, (page, table)))
                { audit.Refuse("duplicate_or_empty_cell_id", row: false); return result; }
        }
        if (tables.Count == 0) audit.Note("document_index_table_unavailable");
        var publishedByIndex = published.GroupBy(chunk => chunk.ChunkIndex).ToDictionary(group => group.Key, group => group.ToArray());
        var cellChunks = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var references = 0;
        foreach (var chunk in recognized)
        {
            ct.ThrowIfCancellationRequested();
            if (!publishedByIndex.TryGetValue(chunk.ChunkIndex, out var actual) || actual.Length != 1
                || !ReferenceEquals(chunk, actual[0]) && chunk != actual[0])
            { audit.Note("source_chunk_not_published"); continue; }
            var ids = chunk.CanonicalTableCellIds;
            if (ids is not null && ids.Count > MaxCanonicalCellReferences - references)
            { audit.Refuse("source_cell_reference_limit", limit: true, row: false); return result; }
            references += ids?.Count ?? 0;
            if (ids is not { Count: > 0 } || ids.Count > MaxCanonicalNavigationCells
                || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
                || ids.Any(id => !cells.TryGetValue(id, out var owner)
                    || chunk.PageStart != owner.Page.PageNumber || chunk.PageEnd != owner.Page.PageNumber)
                || ids.Select(id => cells[id].Table.TableId).Distinct(StringComparer.Ordinal).Count() != 1)
            { audit.Note("invalid_source_chunk_cells"); continue; }
            foreach (var id in ids)
            {
                if (!cellChunks.TryGetValue(id, out var indices)) cellChunks.Add(id, indices = []);
                indices.Add(chunk.ChunkIndex);
            }
        }
        var targets = ReadCanonicalNavigationTargets(document, ct, audit);
        var nativeTitles = new NativeTitleNavigationContext(document, cellChunks, ct);
        audit.NativeTitles = nativeTitles;
        foreach (var (page, table) in tables)
        {
            ct.ThrowIfCancellationRequested();
            if (!table.Cells.Any(cell => cellChunks.ContainsKey(cell.CellId)))
                continue;
            audit.Tables++;
            foreach (var row in table.Cells.GroupBy(cell => cell.RowIndex).OrderBy(group => group.Key))
            {
                ct.ThrowIfCancellationRequested();
                if (audit.Rows == MaxNavigationEntries)
                { audit.Refuse("source_row_limit", limit: true, row: false); return result; }
                audit.Rows++;
                var ordered = row.OrderBy(cell => cell.ColumnIndex).ThenBy(cell => cell.CellId, StringComparer.Ordinal).ToArray();
                if (ordered.Any(cell => !cellChunks.ContainsKey(cell.CellId)))
                { audit.Refuse("unpublished_row_cell"); continue; }
                CanonicalNavigationRowEvidence? evidence;
                string refusal;
                try
                {
                    if (!TryReadCanonicalNavigationRow(ordered, out evidence, out refusal))
                    { audit.Refuse(refusal, limit: refusal.EndsWith("_limit", StringComparison.Ordinal)); continue; }
                }
                catch (RegexMatchTimeoutException)
                { audit.Refuse("source_row_parser_timeout"); continue; }
                var title = evidence!.Title;
                var matchingTargets = targets?.GetValueOrDefault(title);
                var target = matchingTargets is { Count: 1 } ? matchingTargets[0] : null;
                var method = target is not null ? "canonical_title_exact" : matchingTargets is { Count: > 1 }
                    ? "canonical_title_ambiguous" : "canonical_title_unresolved";
                CanonicalNavigationNativeTitleEvidence? nativeProof = null;
                if (evidence.PhysicalOverlapObserved)
                {
                    // The geometric row is still refused. An independently corroborated
                    // title may support a page link; it never repairs the row or its label.
                    audit.Refuse("ambiguous_source_row");
                    if (!nativeTitles.TryCorroborate(page, evidence, target, targets is not null, out nativeProof))
                        continue;
                    method = "canonical_title_native_corroborated";
                }
                var anchor = target is null ? null : anchors.FirstOrDefault(candidate => candidate.PageStart == target.Page
                    && string.Equals(CanonicalNavigationWhitespace(candidate.Title), title, StringComparison.Ordinal));
                // Legacy title anchors do not carry the canonical block identity needed
                // by this additional proof. Leave them absent instead of fabricating one.
                if (nativeProof is not null) anchor = null;
                var targetChunk = target is null ? null : published.Where(chunk => !IsNavigationChunk(chunk)
                    && chunk.PageStart <= target.Page && target.Page <= chunk.PageEnd
                    && chunk.CanonicalBlockIds?.Contains(target.BlockId, StringComparer.Ordinal) == true)
                    .OrderBy(chunk => chunk.ChunkIndex).FirstOrDefault();
                // A minimal supporting set is sufficient: each full cell is proven in one
                // published chunk. Repeated headers must not amplify metadata to thousands of IDs.
                var chunkIndices = ordered.Select(cell => cellChunks[cell.CellId].Min()).Distinct().Order().ToArray();
                var entry = new ProjectedDocumentNavigationEntry(entries.Count, page.PageNumber, chunkIndices[0], null,
                    title, TitleAnchorNormalizer.NormalizeTitle(title), TitleAnchorNormalizer.BuildTitleTokens(title),
                    target?.Page, target?.Page, anchor?.AnchorIndex, targetChunk?.ChunkIndex, method, target is null ? 0 : 1)
                {
                    CanonicalSource = new(table.TableId, table.Ordinal, row.Key, ordered.Select(cell => cell.CellId).ToArray(),
                        chunkIndices, evidence.PrintedPageLabel, evidence.PrintedPageKind, target?.BlockId)
                    { NativeTitleCorroboration = nativeProof }
                };
                if (nativeProof is not null)
                {
                    // Reserve the widest positive ingestion-version spelling. Storage
                    // uses this exact serializer; a cap refusal cannot truncate proof.
                    var metadata = DocumentFoundationRepo.SerializeNavigationEntryMetadata(entry, int.MaxValue);
                    if (System.Text.Encoding.UTF8.GetByteCount(metadata) > 16 * 1024)
                    {
                        nativeTitles.RefuseMetadata();
                        continue;
                    }
                    nativeTitles.RecordLink();
                }
                entries.Add(entry);
                audit.Produced++;
                if (target is null) { audit.Unresolved++; audit.Note(method); } else audit.Resolved++;
            }
        }
        return result;
    }

    private sealed record CanonicalNavigationRowEvidence(string Title, string PrintedPageLabel, string PrintedPageKind,
        bool PhysicalOverlapObserved, IReadOnlyList<CanonicalTableCell> TitleCells);

    private static bool TryReadCanonicalNavigationRow(CanonicalTableCell[] cells,
        out CanonicalNavigationRowEvidence? evidence, out string refusal)
    {
        evidence = null;
        refusal = "ambiguous_source_row";
        if (cells.Length > MaxCanonicalRowCells) { refusal = "source_row_cell_limit"; return false; }
        if (cells.Length == 0 || cells.Any(cell => cell.RowSpan != 1 || cell.ColumnSpan < 1))
        { refusal = "unsupported_source_row_span"; return false; }
        if (cells.Any(cell => cell.RowIndex < 0 || cell.ColumnIndex < 0))
        { refusal = "invalid_source_cell_bounds"; return false; }
        var boxes = new List<(double L, double T, double R, double B)>();
        foreach (var cell in cells)
        {
            if (!TryNavigationBox(cell.Polygon, out var box)) { refusal = "source_row_geometry_unavailable"; return false; }
            boxes.Add(box);
        }
        var physicalOverlap = false;
        for (var i = 0; i < cells.Length; i++)
        for (var j = i + 1; j < cells.Length; j++)
        {
            if (boxes[i].L >= boxes[j].L) return false;
            if (Math.Min(boxes[i].R, boxes[j].R) > Math.Max(boxes[i].L, boxes[j].L)
                && Math.Min(boxes[i].B, boxes[j].B) > Math.Max(boxes[i].T, boxes[j].T)) physicalOverlap = true;
            if ((long)cells[i].ColumnIndex + cells[i].ColumnSpan > cells[j].ColumnIndex
                && Math.Min(boxes[i].B, boxes[j].B) <= Math.Max(boxes[i].T, boxes[j].T))
                return false;
        }
        // Every fragment must share a vertical interval with an earlier fragment. A wrapped
        // title box may bridge a section number on its first line and page label on its last.
        for (var i = 1; i < boxes.Count; i++)
            if (!boxes.Take(i).Any(prior => Math.Min(prior.B, boxes[i].B) > Math.Max(prior.T, boxes[i].T))) return false;
        if (cells.Any(cell => cell.Text.Raw.Length > MaxCanonicalRowCharacters))
        { refusal = "source_row_character_limit"; return false; }
        var text = CanonicalNavigationWhitespace(string.Join(' ', cells.Select(cell => cell.Text.Raw)));
        if (text.Length > MaxCanonicalRowCharacters)
        { refusal = "source_row_character_limit"; return false; }
        var match = Regex.Match(text, @"(?<label>.+?)(?:[.·•]{2,}\s*|\s+)(?<page>[0-9]{1,12}|[IVXLCDMivxlcdm]{1,15})$", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (!match.Success) { refusal = "printed_page_label_unavailable"; return false; }
        var title = Regex.Replace(match.Groups["label"].Value, @"[.·•]+$", "", RegexOptions.CultureInvariant).Trim();
        var pageLabel = match.Groups["page"].Value;
        string pageKind;
        if (title.Length < 2 || !title.Any(char.IsLetter)) return false;
        if (pageLabel.All(char.IsAsciiDigit)) pageKind = "arabic";
        else if (Regex.IsMatch(pageLabel, @"^(?=.+)M{0,3}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) pageKind = "roman";
        else { refusal = "invalid_printed_page_label"; return false; }
        var titleCells = cells.Where((cell, index) => index != cells.Length - 1 || cells.Length == 1
            || CanonicalNavigationWhitespace(cell.Text.Raw) != pageLabel).ToArray();
        evidence = new(title, pageLabel, pageKind, physicalOverlap, titleCells);
        return true;
    }

    private static bool TryNavigationBox(CanonicalPolygon? polygon, out (double L, double T, double R, double B) box)
    {
        box = default;
        if (polygon?.Points is not { Count: 4 } points || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)
            || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)) return false;
        box = (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        var (l, t, r, b) = box;
        return r > l && b > t && points.Select(p => (p.X, p.Y)).ToHashSet().SetEquals(new[] { (l,t), (r,t), (r,b), (l,b) })
            && Enumerable.Range(0, 4).All(i => points[i].X == points[(i + 1) % 4].X || points[i].Y == points[(i + 1) % 4].Y);
    }

    private static string CanonicalNavigationWhitespace(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record CanonicalNavigationProjection(List<ProjectedDocumentNavigationEntry> Entries, CanonicalNavigationAudit Audit);
    private sealed class CanonicalNavigationAudit
    {
        internal int Chunks, Tables, Rows, Produced, Resolved, Unresolved, Refused;
        internal bool Limit;
        internal bool ChunkInventoryComplete = true;
        internal NativeTitleNavigationContext? NativeTitles;
        private readonly Dictionary<string, int> reasons = new(StringComparer.Ordinal);
        internal void Note(string reason) => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        internal void Refuse(string reason, bool limit = false, bool row = true) { if (row) Refused++; Limit |= limit; Note(reason); }
        internal CanonicalNavigationDiagnostics Snapshot() => new(Chunks, Tables, Rows, Produced, Resolved, Unresolved, Refused,
            Limit, new Dictionary<string, int>(reasons, StringComparer.Ordinal))
        { Availability = Produced == 0 && reasons.Count > 0 ? "unavailable" : Limit || Refused > 0 || Unresolved > 0 ? "partial" : "observed",
            SourceChunkInventoryComplete = ChunkInventoryComplete, NativeTitleCorroboration = NativeTitles?.Snapshot() };
    }
}
