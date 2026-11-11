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
        property only when their association with that same item is established;
        both IDs may be needed. Similar names, a search query or a shared topic
        do not establish this association. Do not replace a property passage
        with another copy of the introduction.
        citationReview.namedItemSourceAssociations focuses attention on claims
        citing multiple source versions or unassigned physical context. Its
        visibleNameMentionEvidenceIds is a lexical observation of current text,
        not proof that all properties describe the same item; an empty list is
        not proof of a mismatch. Inspect the association in each source group,
        research it if needed, or omit the unsupported detail while preserving
        useful supported information. Multisource synthesis remains allowed.
        citationReview.identicalCompletePassages identifies only exact complete
        duplicates in the same source and physical pages. It is not a semantic
        verdict, a merge of IDs or proof of independence for unlisted passages.
        Identical passages add no different factual support.
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
            namedItemSourceAssociations = BuildNamedItemSourceAssociations(candidate, evidence),
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

    private static object[] BuildNamedItemSourceAssociations(
        AdvancedAnalysisProviderResult candidate, IReadOnlyList<PromptEvidenceItem> evidence)
    {
        var reviews = new List<object>();
        foreach (var claim in candidate.Claims.Where(c => !string.IsNullOrWhiteSpace(c.SelectedItem)))
        {
            var name = NormalizeClaimText(claim.SelectedItem!);
            if (name.Length == 0) continue;
            // Keep each source version separate. Neither retrieval metadata nor
            // a name observed in another source can establish this association.
            var groups = evidence.Where(e => e.EvidenceId is not null
                    && claim.EvidenceIds.Contains(e.EvidenceId, StringComparer.Ordinal))
                .GroupBy(e => new { e.SourceKey, e.SourceReference.DocId,
                    e.SourceReference.RevisionId, e.SourceReference.SourceHash })
                .Select(group => new
                {
                    group.Key.SourceKey,
                    evidenceIds = group.Select(e => e.EvidenceId).ToArray(),
                    visibleNameMentionEvidenceIds = group.Where(e => GetVisibleEvidenceContents(e)
                            .Any(body => (" " + NormalizeClaimText(body) + " ")
                                .Contains(" " + name + " ", StringComparison.Ordinal)))
                        .Select(e => e.EvidenceId).ToArray(),
                    unassignedPhysicalContextEvidenceIds = group
                        .Where(e => e.SourceStructure?.SectionAssociation == "not_asserted")
                        .Select(e => e.EvidenceId).ToArray()
                }).ToArray();
            if (groups.Length > 1 || groups.Any(g => g.unassignedPhysicalContextEvidenceIds.Length > 0))
                reviews.Add(new { claim.ClaimId, multipleSourceVersions = groups.Length > 1, sourceGroups = groups });
        }
        return reviews.ToArray();
    }
}
