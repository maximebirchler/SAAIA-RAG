using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const string CriticCitationReviewPolicy = """
        Review the selection's requested purpose before enriching its description.
        claimCoordinates describes the requested complete answer. Its positional
        claim IDs locate an answered candidate, but do not assign positions to a
        partial list: partial claim IDs are citation labels, not grid placements.
        For a partial candidate, assess only placements explicitly stated in its
        text; never infer a placement by matching its ID to claimCoordinates.
        citationReview is review context outside the candidate response schema;
        do not copy review metadata into output claims. For a proposed placement, use claim text
        to explain briefly why the documented choice serves that requested unit,
        preserving any conditions. Ordinary suitability may be reasoned from the
        content; it need not be a source-prescribed relationship. An item name or
        a description of its composition alone does not establish that fit.
        Reassign or research weak choices instead of merely polishing their text.

        Audit the final wording against the final evidenceIds of each claim.
        Reading a fact elsewhere in current evidence does not support a claim
        that cites only another passage. After research, cite the actual passages
        supporting added facts, or remove unnecessary details. Do not expand an
        adequate concise claim with unrequested factual detail. A title-bearing
        passage can identify the item while a separate cited passage supplies a
        property; both IDs may be needed. Do not replace that property passage
        with another copy of the introduction. citationReview identifies only
        exact complete-text duplicates in the same source and physical pages;
        it is not a semantic verdict, a merge of IDs or proof of independence
        for unlisted passages. Identical passages add no different factual support.
        """;

    private static object BuildCriticCandidate(
        AdvancedAnalysisProviderResult candidate)
    {
        return new
        {
            outcome = candidate.Outcome,
            answerText = candidate.AnswerText,
            claims = candidate.Claims.Select(claim => new
            {
                claimId = claim.ClaimId,
                selectedItem = claim.SelectedItem,
                text = claim.Text,
                evidenceIds = claim.EvidenceIds
            }).ToArray()
        };
    }

    private static object BuildCriticCitationReview(
        AdvancedAnalysisProviderResult candidate,
        IReadOnlyList<PromptEvidenceItem> evidence)
        => new
        {
            status = "mechanical_context_only",
            candidatePlacement = candidate.Outcome == "answered"
                ? "answered_contract_positions"
                : "not_inferred_for_partial_answer",
            // Compare complete current bodies only. Matching prefixes or separate
            // retained windows cannot establish equality of unread canonical text.
            identicalCompletePassages = evidence.Where(e => !string.IsNullOrWhiteSpace(e.EvidenceId)
                    && !string.IsNullOrWhiteSpace(e.Content) && !e.ContentTruncated
                    && e.ContentOffset == 0 && e.Content.Length == e.OriginalContentLength
                    && e.SupportingContentWindows.Count == 0)
                .GroupBy(e => new
                {
                    e.SourceKey, e.SourceReference.DocId, e.SourceReference.RevisionId,
                    e.SourceReference.SourceHash, e.PhysicalPageStart, e.PhysicalPageEnd, e.Content
                })
                .Where(g => g.Select(e => e.EvidenceId).Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(g => new
                {
                    evidenceIds = g.Select(e => e.EvidenceId).Distinct(StringComparer.Ordinal).ToArray(),
                    g.Key.SourceKey, g.Key.PhysicalPageStart, g.Key.PhysicalPageEnd
                }).ToArray()
        };
}
