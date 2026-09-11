namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class SourceContractVerifier
{
    private static void AddSemanticSelectionCitationErrors(
        ICollection<SourceVerificationError> errors,
        string? answer,
        HashSet<string>? allowedIds,
        bool requireEveryAllowedEvidenceIdExactlyOnce,
        IReadOnlyList<IReadOnlyList<string>>? requiredEvidenceIdGroups,
        bool requireCitedAllowedEvidenceIdsAtMostOnce,
        bool requireSeparateAtomicClaims)
    {
        var answerEvidenceIdMatches = EvidenceIdPattern
            .Matches(answer ?? string.Empty)
            .ToArray();
        var answerIdOccurrences = answerEvidenceIdMatches
            .GroupBy(
                static match => match.Groups[1].Value.ToUpperInvariant(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group =>
                {
                    var canonicalMarkerCount = group.Count(static match =>
                        match.Value.Length >= 3
                        && match.Value[0] == '['
                        && match.Value[^1] == ']');
                    return canonicalMarkerCount > 0
                        ? canonicalMarkerCount
                        : group.Count();
                },
                StringComparer.OrdinalIgnoreCase);
        if (requireEveryAllowedEvidenceIdExactlyOnce && allowedIds is not null)
        {
            var missingAllowedIds = allowedIds
                .Where(id => !answerIdOccurrences.ContainsKey(id))
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var repeatedAllowedIds = answerIdOccurrences
                .Where(pair => allowedIds.Contains(pair.Key) && pair.Value != 1)
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => $"{pair.Key}({pair.Value}x)")
                .ToArray();
            if (missingAllowedIds.Length > 0 || repeatedAllowedIds.Length > 0)
            {
                errors.Add(new SourceVerificationError(
                    "semantic_selection_not_realized_exactly_once",
                    "The writer must cite every EvidenceId selected by the LLM exactly once."
                    + (missingAllowedIds.Length > 0
                        ? " Missing: " + string.Join(", ", missingAllowedIds) + "."
                        : string.Empty)
                    + (repeatedAllowedIds.Length > 0
                        ? " Repeated: " + string.Join(", ", repeatedAllowedIds) + "."
                        : string.Empty),
                    null,
                    SourceBackedPipelineStep.SourceVerifier));
            }
        }

        if (requireCitedAllowedEvidenceIdsAtMostOnce && allowedIds is not null)
        {
            var repeatedAllowedIds = answerIdOccurrences
                .Where(pair => allowedIds.Contains(pair.Key) && pair.Value > 1)
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => $"{pair.Key}({pair.Value}x)")
                .ToArray();
            if (repeatedAllowedIds.Length > 0)
            {
                errors.Add(new SourceVerificationError(
                    "semantic_selection_citation_repeated",
                    "A citable EvidenceId may appear at most once in named-item mode. Repeated: "
                    + string.Join(", ", repeatedAllowedIds) + ".",
                    null,
                    SourceBackedPipelineStep.SourceVerifier));
            }
        }

        if (requiredEvidenceIdGroups is { Count: > 0 })
        {
            var missingGroups = requiredEvidenceIdGroups
                .Select((group, index) => new
                {
                    Index = index + 1,
                    EvidenceIds = group
                        .SelectMany(NormalizeDeclaredEvidenceIds)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                })
                .Where(group =>
                    group.EvidenceIds.Length == 0
                    || !group.EvidenceIds.Any(answerIdOccurrences.ContainsKey))
                .Select(group =>
                    $"group {group.Index} ({string.Join(", ", group.EvidenceIds)})")
                .ToArray();
            if (missingGroups.Length > 0)
            {
                errors.Add(new SourceVerificationError(
                    "semantic_selection_group_not_realized",
                    "The writer must cite at least one exact EvidenceId from every "
                    + "semantic item selected by the LLM. Missing: "
                    + string.Join("; ", missingGroups) + ".",
                    null,
                    SourceBackedPipelineStep.SourceVerifier));
            }
        }

        if (requireSeparateAtomicClaims && allowedIds is { Count: > 1 })
        {
            var clusteredClaims = FindAtomicClaimCitationClusters(answer, allowedIds);
            if (clusteredClaims.Count > 0)
            {
                errors.Add(new SourceVerificationError(
                    "atomic_claim_citations_not_localized",
                    "Each EvidenceId selected as an atomic content claim must be attached "
                    + "to its own sentence, list item, or semicolon-delimited claim. "
                    + "Do not place several selected citations in one claim: "
                    + string.Join("; ", clusteredClaims.Take(6)),
                    null,
                    SourceBackedPipelineStep.SourceVerifier));
            }
        }
    }
}
