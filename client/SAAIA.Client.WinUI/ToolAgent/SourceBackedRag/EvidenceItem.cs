using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record EvidenceItem(
    string EvidenceId,
    string SourceKind,
    string ToolName,
    string QueryUsed,
    string? DocId,
    string? DocName,
    string? DocPath,
    string? SourceHash,
    string? RevisionId,
    int? PageStart,
    int? PageEnd,
    string? ChunkId,
    string? Excerpt,
    string? NormalizedExcerpt,
    double? Score,
    int Rank,
    string? CategoryPath,
    string? DocLanguage,
    string? ProfileLanguage,
    string? ExtractionQuality,
    JsonElement? MatchedContentCards,
    IReadOnlyDictionary<string, string> SelectionHints,
    IReadOnlyDictionary<string, string> CodeHints,
    IReadOnlyList<string> RiskFlags,
    IReadOnlyList<string> Lineage)
{
    public string? AnchorId { get; init; }

    public string? ContentCardId { get; init; }

    public string VisibleSourceKey
    {
        get
        {
            var path = string.IsNullOrWhiteSpace(DocPath) ? DocName ?? string.Empty : DocPath;
            var pageStart = PageStart?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            var pageEnd = PageEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? pageStart;
            var sourceKey = $"{path.Trim().ToLowerInvariant()}|{pageStart}|{pageEnd}";
            if (string.Equals(
                    SourceKind,
                    "canonical_content_card",
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ContentCardId))
            {
                // Two immutable cards can legitimately share a PDF page. Their
                // backend content-card ids distinguish factual source items
                // without weakening ordinary document/page deduplication.
                sourceKey += "|content-card:" + ContentCardId.Trim().ToLowerInvariant();
            }

            return sourceKey;
        }
    }
}
