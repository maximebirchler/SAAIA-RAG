using System.Collections.ObjectModel;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class EvidenceBundleBuilder
{
    private static void AddDocumentNavigationItems(
        List<EvidenceItem> items,
        ToolResults.Item toolItem,
        string userQuestion,
        int toolSequence)
    {
        if (toolItem.Result.ValueKind != JsonValueKind.Object
            || !TryGetProperty(toolItem.Result, "items", out var navigationItems)
            || navigationItems.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var queryUsed = GetString(toolItem.Result, "query", "q") ?? userQuestion;
        for (var i = 0; i < navigationItems.GetArrayLength(); i++)
        {
            var navigationItem = navigationItems[i];
            if (navigationItem.ValueKind != JsonValueKind.Object)
                continue;

            var label = GetString(navigationItem, "label", "title", "name");
            if (string.IsNullOrWhiteSpace(label))
                continue;

            var pageStart = GetInt(
                navigationItem,
                "targetPageStart",
                "target_page_start",
                "sourcePage",
                "source_page");
            var pageEnd = GetInt(
                              navigationItem,
                              "targetPageEnd",
                              "target_page_end")
                          ?? pageStart;

            items.Add(new EvidenceItem(
                EvidenceId: string.Empty,
                SourceKind: "navigation_map",
                ToolName: toolItem.ToolName,
                QueryUsed: queryUsed,
                DocId: GetString(navigationItem, "docId", "doc_id", "documentId"),
                DocName: GetString(navigationItem, "docName", "doc_name", "documentName", "fileName"),
                DocPath: GetString(navigationItem, "docPath", "doc_path", "path", "documentPath"),
                SourceHash: GetString(navigationItem, "sourceHash", "source_hash"),
                RevisionId: GetString(navigationItem, "revisionId", "revision_id"),
                PageStart: pageStart,
                PageEnd: pageEnd,
                // A navigation item remains orientation-only, but preserving its
                // resolved target lets the LLM turn the anchor into factual
                // evidence with documents.context without guessing the source.
                ChunkId: GetString(
                    navigationItem,
                    "targetChunkId",
                    "target_chunk_id"),
                Excerpt: label,
                NormalizedExcerpt: NormalizeEvidenceText(label),
                Score: GetDouble(navigationItem, "confidence", "score"),
                Rank: i + 1,
                CategoryPath: GetString(navigationItem, "categoryPath", "category_path", "category"),
                DocLanguage: null,
                ProfileLanguage: null,
                ExtractionQuality: null,
                MatchedContentCards: null,
                SelectionHints: BuildNavigationHints(navigationItem),
                CodeHints: BuildCodeHints(toolItem, toolSequence),
                RiskFlags: new[] { "orientation_only" },
                Lineage: new[]
                {
                    $"tool:{toolItem.ToolName}",
                    $"toolSequence:{toolSequence}",
                    "navigation_map_only"
                })
            {
                AnchorId = GetString(
                    navigationItem,
                    "targetAnchorId",
                    "target_anchor_id")
            });
        }
    }

    private static IReadOnlyDictionary<string, string> BuildNavigationHints(JsonElement item)
    {
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
                 {
                     "kind",
                     "resolutionMethod",
                     "sourcePage",
                     "targetPageStart",
                     "targetPageEnd",
                     "confidence",
                     "navigationEntryId",
                     "targetChunkId",
                     "targetAnchorId",
                     "hasTargetChunk",
                     "hasTargetAnchor"
                 })
        {
            if (!TryGetProperty(item, name, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                continue;
            }

            hints[name] = value.ToString();
        }

        return new ReadOnlyDictionary<string, string>(hints);
    }
}
