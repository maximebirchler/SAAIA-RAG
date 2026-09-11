namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static class SourceBackedUiPayloadMapper
{
    public static SourceBackedUiPayload FromVerifiedResult(SourceBackedPipelineResult result)
    {
        if (!result.IsSourceVerified)
            throw new InvalidOperationException("Cannot build a UI source payload from an unverified source-backed result.");

        var sources = result.CitedEvidence
            .Where(static item => !string.IsNullOrWhiteSpace(item.DocPath) || !string.IsNullOrWhiteSpace(item.DocName))
            .DistinctBy(
                static item => item.EvidenceId,
                StringComparer.OrdinalIgnoreCase)
            .Select(ToSourceRef)
            .ToArray();

        return new SourceBackedUiPayload(result.Answer ?? string.Empty, sources, result.TraceEvents);
    }

    private static ToolMemory.SourceRef ToSourceRef(EvidenceItem item)
    {
        var pageStart = item.PageStart ?? 1;
        var pageEnd = item.PageEnd ?? pageStart;
        return new ToolMemory.SourceRef
        {
            EvidenceId = item.EvidenceId,
            DocId = item.DocId,
            DocPath = item.DocPath ?? string.Empty,
            DocName = item.DocName,
            PageStart = pageStart,
            PageEnd = pageEnd,
            Label = FirstNonBlank(item.DocName, item.DocPath, item.DocId, "source"),
            SourceHash = item.SourceHash,
            RevisionId = item.RevisionId,
            DocLanguage = item.DocLanguage,
            ProfileLanguage = item.ProfileLanguage,
            CategoryPath = item.CategoryPath,
            ChunkId = item.ChunkId,
            AnchorId = item.AnchorId,
            ContentCardId = item.ContentCardId,
            ContentRole = item.SourceKind,
            SelectionHintEvidenceRole = TryGetSelectionHint(item, "evidenceRole", "evidence_role"),
            SelectionHintSupportScore = TryGetIntSelectionHint(item, "supportScore", "support_score"),
            SelectionHintActionabilityScore = TryGetIntSelectionHint(item, "actionabilityScore", "actionability_score")
        };
    }

    private static string? TryGetSelectionHint(EvidenceItem item, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (item.SelectionHints.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static int? TryGetIntSelectionHint(EvidenceItem item, params string[] keys)
        => int.TryParse(TryGetSelectionHint(item, keys), out var value) ? value : null;

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
