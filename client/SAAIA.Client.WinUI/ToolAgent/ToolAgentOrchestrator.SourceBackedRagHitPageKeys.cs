namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string BuildRagHitVisiblePageMergeKey(RagHitSummary hit)
    {
        var page = Math.Max(1, hit.PageStart);

        var path = NormalizeLexicalLookup(hit.DocPath);
        if (!string.IsNullOrWhiteSpace(path) && LooksLikeQualifiedDocumentPath(hit.DocPath))
            return $"path:{path}|p:{page}";

        var hash = CollapseWhitespace(hit.SourceHash ?? string.Empty).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(hash))
            return $"hash:{hash}|p:{page}";

        if (!string.IsNullOrWhiteSpace(path))
            return $"path:{path}|p:{page}";

        var docId = CollapseWhitespace(hit.DocId ?? string.Empty).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(docId))
            return $"id:{docId}|p:{page}";

        var name = NormalizeLexicalLookup(hit.DocName);
        return $"name:{name}|p:{page}";
    }
}
