using System;
using System.Collections.Generic;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedCitationVisibility
{
    public static WriterDraft EnsureDeclaredIdsAreVisible(
        WriterDraft draft,
        IReadOnlyList<string>? allowedEvidenceIds,
        out IReadOnlyList<string> addedEvidenceIds)
    {
        addedEvidenceIds = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(draft.Answer) || draft.CitedEvidenceIds.Count == 0)
            return draft;

        var visibleIds = SourceContractVerifier.ExtractEvidenceIds(draft.Answer)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declaredIds = SourceContractVerifier.ExtractEvidenceIds(
                string.Join(" ", draft.CitedEvidenceIds ?? Array.Empty<string>()))
            .ToArray();
        if (declaredIds.Length == 0)
            return draft;

        var allowedIds = allowedEvidenceIds is null
            ? null
            : SourceContractVerifier.ExtractEvidenceIds(string.Join(" ", allowedEvidenceIds))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingVisibleIds = declaredIds
            .Where(id => !visibleIds.Contains(id))
            .Where(id => allowedIds is null || allowedIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (missingVisibleIds.Length == 0)
            return draft;

        addedEvidenceIds = missingVisibleIds;
        var citationSuffix = " " + string.Join(" ", missingVisibleIds.Select(static id => $"[{id}]"));
        var answer = draft.Answer.TrimEnd() + citationSuffix;
        var citedIds = SourceContractVerifier.ExtractEvidenceIds(answer)
            .Concat(declaredIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WriterDraft(answer, citedIds);
    }
}
