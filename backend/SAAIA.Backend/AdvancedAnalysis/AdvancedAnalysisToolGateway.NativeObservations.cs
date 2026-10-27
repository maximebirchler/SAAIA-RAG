namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class AdvancedAnalysisToolGateway
{
    private static AdvancedAnalysisResolvedEvidence AttachNativeRead(AdvancedAnalysisResolvedEvidence item,
        AdvancedAnalysisAlternativeNativeReadings readings)
    {
        if (!string.Equals(item.Reference.DocId, readings.DocId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(item.Reference.RevisionId, readings.RevisionId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(item.Reference.SourceHash, readings.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return item;
        var overview = item.SourceOverview ?? new AdvancedAnalysisSourceOverview(item.Reference.PageStart,
            item.Reference.PageEnd, item.NativeObservation is not null || item.SourceObservation is not null ? 0 : 1);
        var source = item.NativeObservation;
        if (source is not null && readings.View is { } view
            && source.Reference.PhysicalPage == readings.PhysicalPage
            && source.Reference.BlockId == view.BlockId
            && source.Reference.RawTextSha256 == view.RawTextSha256
            && view.Lines.Any(line => line.Source.LineId == source.Reference.LineId))
            source = source with { ViewLineCount = view.RawLineCount, LineOffset = view.Offset,
                LineLimit = view.Limit, NextLineOffset = view.NextOffset };
        // Source overviews can be attached to several primary excerpts. Keep
        // only the cursor/identity here: literal lines live once in evidence.
        var metadata = readings with { View = readings.View is { } raw ? raw with { Lines = [] } : null };
        return item with { NativeObservation = source, SourceOverview = overview with { NativeObservations = metadata } };
    }
}
