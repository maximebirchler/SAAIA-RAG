using System.Text.Json;
using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisAlternativeTableReadings(
    CanonicalTableObservationInventory? Inventory, AdvancedAnalysisTableObservationView? View);

internal sealed record AdvancedAnalysisTableObservationLineSource(
    string ObservationId, string TableId, string LineId, int RawOrdinal, string TextSha256,
    string RawObservationSha256, string View, int ViewOrdinal, int? BandOrdinal,
    int ViewLineCount, int LineOffset, int LineLimit, int? NextLineOffset,
    IReadOnlyList<string> QualitySignals, string SemanticAccuracy = "not_verified",
    bool CellMembershipProven = false, bool SemanticParentLinksAsserted = false);

internal sealed record AdvancedAnalysisTableObservationLineView(
    string LineId, int RawOrdinal, int ViewOrdinal, string Text, string TextSha256,
    string AnchorId, CanonicalPolygon Polygon, double? Confidence);

internal sealed record AdvancedAnalysisTableObservationView(
    string ObservationId, string TableId, int PhysicalPage, string RawObservationSha256,
    string View, int? BandOrdinal, int LineOffset, int LineLimit, int RawLineCount,
    int? ViewLineCount, int? ReturnedLineCount, int? NextLineOffset, bool WindowComplete,
    bool EntireViewReturned, string Status, string PhysicalReadingAvailability,
    IReadOnlyList<AdvancedAnalysisTableObservationLineView>? Lines,
    IReadOnlyList<string> QualitySignals, bool PdfCoverageVerified = false,
    bool CanonicalGridRepaired = false, string SemanticAccuracy = "not_verified");

/// <summary>One exact view of a validated secondary observation, without inventing cell relationships.</summary>
internal static class AdvancedAnalysisTableObservationWindows
{
    internal const int MaximumLineLimit = 64;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static AdvancedAnalysisTableObservationView Build(
        CanonicalTableRegionObservation observation, string view, int? bandOrdinal, int offset, int limit,
        IReadOnlyDictionary<string, string> anchorIds, int maximumCharacters,
        JsonSerializerOptions? serializationOptions = null)
    {
        if (view is not ("raw" or "physical_band" or "unassigned") || offset < 0
            || limit is < 1 or > MaximumLineLimit || (view == "physical_band") != bandOrdinal.HasValue)
            throw new AdvancedAnalysisToolException("table_observation_read_window_invalid");
        var raw = observation.Lines.OrderBy(line => line.RawOrdinal).ToArray();
        if (raw.Length > 5_000 || raw.Select(line => line.LineId).Distinct(StringComparer.Ordinal).Count() != raw.Length)
            throw new AdvancedAnalysisToolException("table_observation_line_identity_invalid");
        var byId = raw.ToDictionary(line => line.LineId, StringComparer.Ordinal);
        var physical = observation.PhysicalReading;
        string[] ids;
        if (view == "raw")
            ids = raw.Select(line => line.LineId).ToArray();
        else if (physical.Availability != "candidate_only")
            return Fits(Create(null, null, null, "physical_reading_unavailable"));
        else if (view == "unassigned")
            ids = physical.UnassignedLineIds.ToArray();
        else
        {
            var bands = (physical.Bands ?? []).Where(band => band.BandOrdinal == bandOrdinal).ToArray();
            if (bands.Length != 1)
                throw new AdvancedAnalysisToolException("table_observation_band_not_observed");
            ids = bands[0].OrderedLineIds.ToArray();
        }
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !byId.ContainsKey(id))
            || offset > ids.Length)
            throw new AdvancedAnalysisToolException("table_observation_read_window_invalid");
        var selected = ids.Skip(offset).Take(limit).Select((id, index) =>
        {
            if (!anchorIds.TryGetValue(id, out var anchor) || string.IsNullOrWhiteSpace(anchor))
                throw new AdvancedAnalysisToolException("table_observation_line_anchor_unavailable");
            var line = byId[id];
            return new AdvancedAnalysisTableObservationLineView(id, line.RawOrdinal, offset + index,
                line.Text, line.TextSha256, anchor, line.Polygon, line.Confidence);
        }).ToList();
        var expected = selected.Count;
        var result = Create(selected.ToArray(), ids.Length, expected, "observed");
        // The serializer, including escaping and metadata, is the size oracle.
        // Only whole terminal lines may leave this window.
        while (Size(result) > maximumCharacters && selected.Count > 0)
        {
            selected.RemoveAt(selected.Count - 1);
            result = Create(selected.ToArray(), ids.Length, expected,
                selected.Count == 0 && expected > 0 ? "line_does_not_fit" : "partial_budget");
        }
        return Fits(result);

        int Size(AdvancedAnalysisTableObservationView value)
            => JsonSerializer.Serialize(value, serializationOptions ?? Web).Length;
        AdvancedAnalysisTableObservationView Fits(AdvancedAnalysisTableObservationView value)
            => Size(value) <= maximumCharacters ? value
                : throw new AdvancedAnalysisToolException("table_observation_budget_diagnostic_does_not_fit");
        AdvancedAnalysisTableObservationView Create(AdvancedAnalysisTableObservationLineView[]? lines,
            int? total, int? expectedCount, string status)
        {
            var returned = lines?.Length;
            var next = total is { } count && (long)offset + (returned ?? 0) < count
                ? offset + (returned ?? 0) : (int?)null;
            return new(observation.ObservationId, observation.TableId, observation.PhysicalPage,
                observation.RawObservationSha256, view, bandOrdinal, offset, limit, raw.Length,
                total, returned, next, returned is not null && returned == expectedCount,
                total is not null && offset == 0 && returned == total, status, physical.Availability,
                lines, observation.QualitySignals.ToArray());
        }
    }
}
