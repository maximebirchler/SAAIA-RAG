using System.Globalization;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string BuildAgentSourceWindowKey(
        EvidenceItem item)
    {
        var anchor = item.CodeHints.TryGetValue(
            "source_window_anchor_chunk_id",
            out var value)
            ? value?.Trim()
            : null;
        return item.VisibleSourceKey
               + (!string.IsNullOrWhiteSpace(anchor)
                   ? "|window=" + anchor
                   : string.Empty)
               + "|query="
               + (item.QueryUsed ?? string.Empty).Trim();
    }

    private static IOrderedEnumerable<EvidenceItem>
        OrderSourceWindowInDocumentOrder(
            IEnumerable<EvidenceItem> items)
        => items
            .OrderBy(static item =>
                ReadSourceWindowChunkIndex(item))
            .ThenBy(static item => item.PageStart ?? int.MaxValue)
            .ThenBy(static item => item.Rank);

    private static int ReadSourceWindowChunkIndex(
        EvidenceItem item)
        => item.CodeHints.TryGetValue(
               "source_window_chunk_index",
               out var raw)
           && int.TryParse(
               raw,
               NumberStyles.Integer,
               CultureInfo.InvariantCulture,
               out var chunkIndex)
            ? chunkIndex
            : int.MaxValue;
}
