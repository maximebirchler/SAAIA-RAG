using System.Collections.ObjectModel;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class EvidenceBundleBuilder
{
    private static EvidenceItem EnrichParentWithEmbeddedRagSourceWindow(
        EvidenceItem parent,
        JsonElement hit,
        ToolResults.Item toolItem,
        int toolSequence)
    {
        if (!TryGetProperty(hit, "sourceWindow", out var sourceWindow)
            || sourceWindow.ValueKind != JsonValueKind.Array)
        {
            return parent;
        }

        var anchorChunkId =
            GetString(hit, "sourceWindowAnchorChunkId")
            ?? parent.ChunkId
            ?? string.Empty;
        var windowChunkIds = sourceWindow
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => GetString(item, "chunkId", "chunk_id"))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (windowChunkIds.Length == 0)
            return parent;
        var anchorChunkIndex = sourceWindow
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Where(item => string.Equals(
                GetString(item, "chunkId", "chunk_id"),
                anchorChunkId,
                StringComparison.OrdinalIgnoreCase))
            .Select(static item => GetInt(
                item,
                "chunkIndex",
                "chunk_index"))
            .FirstOrDefault(static index => index.HasValue);

        var hints = new Dictionary<string, string>(
            parent.CodeHints,
            StringComparer.OrdinalIgnoreCase)
        {
            ["source_window"] = "true",
            ["source_window_anchor_chunk_id"] = anchorChunkId,
            ["source_window_chunk_count"] =
                windowChunkIds.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["source_window_chunk_ids"] = string.Join(",", windowChunkIds)
        };
        if (anchorChunkIndex.HasValue)
        {
            hints["source_window_chunk_index"] =
                anchorChunkIndex.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
        }
        return parent with
        {
            CodeHints = new ReadOnlyDictionary<string, string>(hints),
            Lineage = parent.Lineage
                .Concat(new[]
                {
                    $"sourceWindowAnchor:{anchorChunkId}"
                })
                .Concat(windowChunkIds.Select(static id =>
                    $"sourceWindowChunk:{id}"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static void AddEmbeddedRagSourceWindowItems(
        List<EvidenceItem> items,
        EvidenceItem parent,
        JsonElement hit,
        ToolResults.Item toolItem,
        int toolSequence)
    {
        if (!TryGetProperty(hit, "sourceWindow", out var sourceWindow)
            || sourceWindow.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var anchorChunkId =
            GetString(hit, "sourceWindowAnchorChunkId")
            ?? parent.ChunkId
            ?? string.Empty;
        foreach (var windowItem in sourceWindow.EnumerateArray())
        {
            if (windowItem.ValueKind != JsonValueKind.Object)
                continue;

            var excerpt = GetString(
                windowItem,
                "text",
                "fullText",
                "excerpt",
                "snippet",
                "content");
            var chunkId = GetString(
                windowItem,
                "chunkId",
                "chunk_id");
            if (string.IsNullOrWhiteSpace(excerpt)
                || string.IsNullOrWhiteSpace(chunkId))
            {
                continue;
            }

            var hints = new Dictionary<string, string>(
                BuildCodeHints(
                    toolItem,
                    toolSequence,
                    windowItem),
                StringComparer.OrdinalIgnoreCase)
            {
                ["source_window"] = "true",
                ["source_window_anchor_chunk_id"] = anchorChunkId
            };
            var chunkIndex = GetInt(
                windowItem,
                "chunkIndex",
                "chunk_index");
            if (chunkIndex.HasValue)
            {
                hints["source_window_chunk_index"] =
                    chunkIndex.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
            }
            var pageStart =
                GetInt(
                    windowItem,
                    "pageStart",
                    "page_start",
                    "page",
                    "pageNumber")
                ?? parent.PageStart;
            var pageEnd =
                GetInt(windowItem, "pageEnd", "page_end")
                ?? pageStart;

            items.Add(new EvidenceItem(
                EvidenceId: string.Empty,
                SourceKind: "document_context",
                ToolName: parent.ToolName,
                QueryUsed: parent.QueryUsed,
                DocId: parent.DocId,
                DocName: parent.DocName,
                DocPath: parent.DocPath,
                SourceHash: parent.SourceHash,
                RevisionId: parent.RevisionId,
                PageStart: pageStart,
                PageEnd: pageEnd,
                ChunkId: chunkId,
                Excerpt: excerpt,
                NormalizedExcerpt: NormalizeEvidenceText(excerpt),
                Score: parent.Score,
                Rank: parent.Rank,
                CategoryPath: parent.CategoryPath,
                DocLanguage: parent.DocLanguage,
                ProfileLanguage: parent.ProfileLanguage,
                ExtractionQuality: parent.ExtractionQuality,
                MatchedContentCards: null,
                SelectionHints: parent.SelectionHints,
                CodeHints:
                    new ReadOnlyDictionary<string, string>(hints),
                RiskFlags: BuildRiskFlags(windowItem),
                Lineage: new[]
                {
                    $"tool:{parent.ToolName}",
                    $"toolSequence:{toolSequence}",
                    $"sourceWindowAnchor:{anchorChunkId}",
                    $"sourceWindowChunk:{chunkId}"
                }));
        }
    }
}
