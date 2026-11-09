using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record ReadRetryRequest(string SourceKey, int PageStart, int PageCount, int TopK);
    private sealed record ReadRetryHint(int PageStart, int PageEnd, int PageCount, int? SuggestedTopK,
        ReadRetryRequest? SuggestedRequest, string Instruction);

    private static ReadRetryHint? BuildReadRetryHint(AdvancedAnalysisSearchRequest? request,
        AdvancedAnalysisResearchResourceLimit? limit, string? currentSourceKey)
    {
        if (request?.Operation != "read_source"
            || limit is not { ReasonCode: "canonical_read_window_result_limit_exceeded", StopsResearch: false }
            || request.PageStart is not int start || request.PageEnd is not int end)
            return null;

        // The gateway probes TopK + 1 chunks. This is a lower bound, not the
        // complete size. A retry is a suggestion, never a claim that it ran.
        const int maximumReadTopK = 60;
        var suggestedTopK = start == end && request.TopK < maximumReadTopK
            ? Math.Min(maximumReadTopK, Math.Max(request.TopK * 2, limit.ObservedAtLeastChunkCount ?? 0))
            : (int?)null;
        var suggested = !string.IsNullOrWhiteSpace(currentSourceKey) && (start != end || suggestedTopK is not null)
            ? new ReadRetryRequest(currentSourceKey, start, 1, suggestedTopK ?? request.TopK) : null;
        return new(start, end, end - start + 1, suggestedTopK, suggested,
            "No text was returned. The page bounds above describe the failed attempt, not a completed read. "
            + (currentSourceKey is null
                ? "The source is no longer in the current catalogue; no retry request is authorized by this history. Use current source discovery first. "
                : start != end
                    ? "If still useful, retry a smaller page window using suggestedRequest with the current sourceKey; other pages in the failed window remain unread. "
                    : suggestedTopK is not null
                        ? "If still useful, retry the same page using suggestedRequest with a larger topK. "
                        : "This single page already used the maximum topK. Use find_source_text with an observed phrase or investigate another source. ")
            + "The observed chunk count is a lower bound. This failure does not establish absence.");
    }

    private static string? CurrentSearchSourceKey(AdvancedAnalysisSearchRequest search,
        IReadOnlyList<PromptEvidenceItem> observations)
    {
        bool Matches(AdvancedAnalysisResultEvidence source) =>
            !string.IsNullOrWhiteSpace(search.DocId) && !string.IsNullOrWhiteSpace(search.RevisionId)
            && !string.IsNullOrWhiteSpace(search.ObservedSourceHash)
            && string.Equals(search.DocId, source.DocId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(search.RevisionId, source.RevisionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(search.ObservedSourceHash, source.SourceHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(search.DocPath, source.DocPath, StringComparison.Ordinal);
        return observations is PromptEvidenceProjection { SourceCatalogueEnabled: true } projection
            ? projection.SourceCatalogue.FirstOrDefault(item => Matches(item.SourceReference))?.SourceKey
            : observations.FirstOrDefault(item => Matches(item.SourceReference))?.SourceKey;
    }
}
