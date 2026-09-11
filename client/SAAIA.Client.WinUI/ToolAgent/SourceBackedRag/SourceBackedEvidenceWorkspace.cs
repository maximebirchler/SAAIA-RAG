namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record SourceBackedEvidenceWorkspace(
    int Round,
    int MaxRounds,
    int RemainingRounds,
    int EvidenceItemCount,
    int DistinctDocumentCount,
    int DistinctSourcePageCount,
    IReadOnlyList<string> TriedRequests,
    IReadOnlyList<string> TriedQueries,
    IReadOnlyList<string> TriedDocumentReads,
    IReadOnlyList<string> SeenSourcePages,
    IReadOnlyList<string> SeenNavigationSourcePages,
    IReadOnlyList<string> SeenCategories,
    IReadOnlyList<string> SeenSourceKinds,
    IReadOnlyList<string> SeenRiskFlags,
    IReadOnlyList<SourceBackedVerificationFailureMemory>? VerificationFailures = null)
{
    public static SourceBackedEvidenceWorkspace From(
        int round,
        int maxRounds,
        IReadOnlyList<RetrievalRequest> requests,
        EvidenceBundle bundle,
        IReadOnlyList<SourceBackedVerificationFailureMemory>? verificationFailures = null)
    {
        var requestArray = requests.ToArray();
        var items = bundle.Items;
        return new SourceBackedEvidenceWorkspace(
            round,
            maxRounds,
            Math.Max(0, maxRounds - round),
            items.Count,
            items
                .Select(static item => string.IsNullOrWhiteSpace(item.DocPath) ? item.DocName : item.DocPath)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            items
                .Select(BuildSourcePageKey)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            requestArray
                .Select(FormatRequestMemory)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(48)
                .ToArray(),
            requestArray
                .Select(static request => request.Query?.Trim() ?? string.Empty)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToArray(),
            requestArray
                .Where(static request => (request.ToolName ?? string.Empty).StartsWith("documents.", StringComparison.OrdinalIgnoreCase))
                .Select(FormatRequestMemory)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToArray(),
            items
                .Select(BuildSourcePageKey)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(48)
                .ToArray(),
            items
                .Where(IsNavigationMapItem)
                .Select(BuildSourcePageKey)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToArray(),
            items
                .Select(static item => item.CategoryPath?.Trim() ?? string.Empty)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToArray(),
            items
                .Select(static item => item.SourceKind?.Trim() ?? string.Empty)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToArray(),
            items
                .SelectMany(static item => item.RiskFlags)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToArray(),
            verificationFailures?.TakeLast(4).ToArray()
                ?? Array.Empty<SourceBackedVerificationFailureMemory>());
    }

    private static string FormatRequestMemory(RetrievalRequest request)
    {
        var toolName = (request.ToolName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(toolName))
            return string.Empty;

        var isDocumentTool = toolName.StartsWith("documents.", StringComparison.OrdinalIgnoreCase);
        var detail = isDocumentTool
            ? FirstNonBlank(
                request.DocPath,
                request.DocRef,
                request.DocId,
                request.ChunkId,
                request.CategoryPath,
                request.Query)
            : FirstNonBlank(
                request.Query,
                request.CategoryPath,
                request.DocPath,
                request.DocRef,
                request.DocId,
                request.ChunkId);
        if (string.IsNullOrWhiteSpace(detail))
            return toolName;

        var page = FormatPageRange(request.PageStart, request.PageEnd);
        if (!string.IsNullOrWhiteSpace(page))
            detail += " " + page;

        var categorySuffix = string.IsNullOrWhiteSpace(request.CategoryPath)
            ? string.Empty
            : $"; categoryPath={Trim(request.CategoryPath, 100)}";
        return $"{toolName}:{Trim(detail, 180)}{categorySuffix}";
    }

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string BuildSourcePageKey(EvidenceItem item)
    {
        var doc = string.IsNullOrWhiteSpace(item.DocPath) ? item.DocName : item.DocPath;
        if (string.IsNullOrWhiteSpace(doc))
            return string.Empty;

        var page = item.PageStart.HasValue
            ? item.PageEnd.HasValue && item.PageEnd != item.PageStart
                ? $"p.{item.PageStart}-{item.PageEnd}"
                : $"p.{item.PageStart}"
            : "p.?";
        return $"{doc} {page}";
    }

    private static bool IsNavigationMapItem(EvidenceItem item)
        => string.Equals(item.ToolName, "documents.navigation", StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.SourceKind, "navigation_map", StringComparison.OrdinalIgnoreCase)
           || item.RiskFlags.Any(static flag => string.Equals(flag, "orientation_only", StringComparison.OrdinalIgnoreCase));

    private static string FormatPageRange(int? pageStart, int? pageEnd)
    {
        if (pageStart is null)
            return string.Empty;

        return pageEnd.HasValue && pageEnd != pageStart
            ? $"p.{pageStart}-{pageEnd}"
            : $"p.{pageStart}";
    }

    private static string Trim(string value, int maxLength)
    {
        var text = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "...";
    }
}

public sealed record SourceBackedVerificationFailureMemory(
    int AttemptNumber,
    IReadOnlyList<string> SelectedEvidenceIds,
    IReadOnlyList<string> SelectedSourceKeys,
    IReadOnlyList<string> VerificationErrorCodes,
    string FailedDraftExcerpt);
