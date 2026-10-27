using System.Text.Json;
using System.Xml.Linq;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisStructuredTableView(string DocId, string RevisionId, string SourceHash,
    int PhysicalPage, int TableOrdinal, int TotalRows, int TotalColumns, IReadOnlyList<int> RowIndices,
    string Html, IReadOnlyList<string> EvidenceIds, IReadOnlyList<string> ChunkIds,
    IReadOnlyList<string> CanonicalTableCellIds, bool Complete, int? NextRowOffset, string Status,
    string CompletenessBasis =  "observed_published_canonical_grid_rows", bool SourceFidelityValidated = false,
    IReadOnlyList<string>? QualitySignals = null, IReadOnlyList<int>? MissingRowIndices = null,
    int MissingRowsOmitted = 0, bool QualityMetadataMalformed = false,
    bool QualityMetadataTruncated = false, int QualitySignalsOmittedCount = 0);

internal static class AdvancedAnalysisStructuredTableAssembler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const int MaximumRowLimit = 200;

    internal static IReadOnlyList<AdvancedAnalysisStructuredTableView> Build(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence, AdvancedAnalysisSearchRequest? request, int maximumCharacters)
    {
        var result = new List<AdvancedAnalysisStructuredTableView>();
        var remaining = Math.Clamp(maximumCharacters, 0, 200_000);
        var candidates = evidence.Where(item => item.TableFragment is not null
             && item.Reference.PageStart>0 && item.Reference.PageStart == item.Reference.PageEnd
             && !string.IsNullOrWhiteSpace(item.Reference.AnchorId) && !string.IsNullOrWhiteSpace(item.Reference.ChunkId));
        foreach (var group in candidates.GroupBy(item => (item.Reference.DocId, item.Reference.RevisionId,
                     item.Reference.SourceHash, item.Reference.PageStart, item.TableFragment!.Ordinal)))
        {
            if (result.Count >= 16) break;
            if (request?.TableOrdinal is { } wanted && wanted != group.Key.Ordinal) continue;
            var members = group.ToArray();
            var first = members[0].TableFragment!;
            var qualitySignals = members.SelectMany(item => item.TableFragment!.QualitySignals ?? [])
                .Distinct(StringComparer.Ordinal).ToArray();
            const int maximumQualitySignals = 64;
            // Count known unique codes after union, not duplicated omissions
            // from each fragment. Unknown contents of refused metadata have no
            // invented count; the separate truncated/malformed flags carry it.
            var qualitySignalsOmitted = Math.Max(0, qualitySignals.Length - maximumQualitySignals);
            var qualityMalformed = members.Any(item => item.TableFragment!.QualityMetadataMalformed);
            var qualityTruncated = qualitySignalsOmitted > 0
                || members.Any(item => item.TableFragment!.QualityMetadataTruncated);
            var rows = new SortedDictionary<int, (AdvancedAnalysisTableRow Row, List<AdvancedAnalysisResolvedEvidence> Sources)>();
            var conflict = first.ErrorCode;
            foreach (var item in members)
            {
                var fragment = item.TableFragment!;
                conflict ??= fragment.ErrorCode;
                if (fragment.TotalRows != first.TotalRows || fragment.TotalColumns != first.TotalColumns)
                    conflict = "structured_table_fragment_conflict";
                foreach (var row in fragment.Rows)
                {
                    if (rows.TryGetValue(row.Index, out var previous))
                    {
                        if (previous.Row.Xml != row.Xml || !previous.Row.CellIds.SequenceEqual(row.CellIds, StringComparer.Ordinal))
                            conflict = "structured_table_row_conflict";
                        previous.Sources.Add(item);
                    }
                    else rows.Add(row.Index, (row, [item]));
                }
            }
            var offset = request?.TableRowOffset ?? 0;
            var limit = request?.TableRowLimit ?? MaximumRowLimit;
            var chosen = new List<int>();
            // Connect every overlapping rowspan transitively. Paging into a
            // spanning cell includes its original leading row, not an orphaned
            // suffix whose columns could be interpreted incorrectly.
            var rowGroups = new List<int[]>();
            for (var start = 0; start<first.TotalRows; )
            {
                var end = start+1;
                for (var index = start; index<end; index++)
                    if (rows.TryGetValue(index, out var value)) end = Math.Max(end, value.Row.EndExclusive);
                rowGroups.Add(Enumerable.Range(start, end-start).ToArray()); start = end;
            }
            var headerGroups = rowGroups.TakeWhile(groupRows => groupRows.Any(index => rows.TryGetValue(index, out var row)
                 && row.Row.IsHeader)).ToArray();
            var headers = headerGroups.SelectMany(groupRows => groupRows).Where(rows.ContainsKey).ToArray();
            var eligible = rows.Keys.Where(index => index >= offset && !headers.Contains(index)).ToArray();
            var boundary = Math.Min(first.TotalRows, (long)offset+limit);
            if (conflict is null)
            {
                chosen.AddRange(headers);
                foreach (var groupRows in rowGroups.Except(headerGroups).Where(groupRows =>
                             groupRows[^1] >= offset && groupRows[0]<boundary))
                {
                    if (groupRows.Length>1 && groupRows.Any(index => !rows.ContainsKey(index)))
                        conflict = "structured_table_rowspan_gap";
                    chosen.AddRange(groupRows.Where(rows.ContainsKey));
                }
                chosen.Sort();
            }
            var view = CreateView(chosen, conflict);
            // Remove complete terminal row groups, rather than slicing XML or
            // discarding a value inside an indivisible row/cell.
            while (JsonSerializer.Serialize(view, JsonOptions).Length>remaining && chosen.Any(index => !headers.Contains(index)))
            {
                var last = chosen.Last(index => !headers.Contains(index));
                var groupStart = rowGroups.First(groupRows => groupRows.Contains(last))[0];
                chosen.RemoveAll(index => index >= groupStart && !headers.Contains(index));
                view = CreateView(chosen, conflict);
            }
            if (!chosen.Any(index => !headers.Contains(index)) && first.TotalRows>headers.Length && conflict is null)
                view = CreateView([], "structured_table_view_budget_exceeded");
            var size = JsonSerializer.Serialize(view, JsonOptions).Length+(result.Count == 0 ? 0 : 1);
            if (size>remaining) break;
            result.Add(view); remaining-= size;

            AdvancedAnalysisStructuredTableView CreateView(IReadOnlyList<int> selected, string? error)
            {
                var selectedRows = error is null ? selected : [];
                var sources = selectedRows.SelectMany(index => rows[index].Sources)
                    .DistinctBy(item => item.Reference.EvidenceId).ToArray();
                var next = eligible.Where(index => !selectedRows.Contains(index)).Select(index => (int?)index).FirstOrDefault();
                var complete = error is null && selectedRows.Count == first.TotalRows
                     && selectedRows.SequenceEqual(Enumerable.Range(0, first.TotalRows));
                var html = selectedRows.Count == 0 ? "" : new XElement("table",
                    new XAttribute("data-source-table-ordinal", group.Key.Ordinal),
                    new XAttribute("data-total-rows", first.TotalRows),
                    new XAttribute("data-source-row-indices", string.Join(",", selectedRows)),
                    selectedRows.Select(index => XElement.Parse(rows[index].Row.Xml))).ToString(SaveOptions.DisableFormatting);
                return new(group.Key.DocId, group.Key.RevisionId, group.Key.SourceHash, group.Key.PageStart,
                    group.Key.Ordinal, first.TotalRows, first.TotalColumns, selectedRows.ToArray(), html,
                    sources.Select(item => item.Reference.EvidenceId).Distinct(StringComparer.Ordinal).ToArray(),
                    sources.Select(item => item.Reference.ChunkId!).Distinct(StringComparer.Ordinal).ToArray(),
                    selectedRows.SelectMany(index => rows[index].Row.CellIds).Distinct(StringComparer.Ordinal).ToArray(),
                    complete, complete ? null : next, error ?? (complete ? "complete" : "partial"),
                    QualitySignals: qualitySignals.Take(maximumQualitySignals).ToArray(),
                    MissingRowIndices: Enumerable.Range(0, first.TotalRows).Where(index => !rows.ContainsKey(index)).Take(200).ToArray(),
                    MissingRowsOmitted: Math.Max(0, first.TotalRows-rows.Count-200),
                    QualityMetadataMalformed: qualityMalformed, QualityMetadataTruncated: qualityTruncated,
                    QualitySignalsOmittedCount: qualitySignalsOmitted);
            }
        }
        return result;
    }
}
