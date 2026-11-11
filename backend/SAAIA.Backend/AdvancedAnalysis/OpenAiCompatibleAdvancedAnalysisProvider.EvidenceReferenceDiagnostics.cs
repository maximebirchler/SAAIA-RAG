using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    internal sealed record EvidenceReferenceIssue(string Path, string Reason,
        string? SuppliedEvidenceId, int? SuppliedIdCharacterCount);

    internal sealed record EvidenceReferenceDiagnostics(string Status, string Scope,
        int IndexBase, int TotalIssueCount, int OmittedIssueCount,
        IReadOnlyList<EvidenceReferenceIssue> Issues);

    internal static EvidenceReferenceDiagnostics DiagnoseEvidenceReferences(
        string raw, IReadOnlyCollection<string> currentEvidenceIds)
    {
        const int maximumIssues = 64;
        const int maximumIdCharacters = 160;
        var issues = new List<EvidenceReferenceIssue>();
        var total = 0;
        EvidenceReferenceDiagnostics Result(string status) => new(status,
            "current_visible_evidence", 0, total, total - issues.Count, issues);
        void Add(string path, string reason, string? id = null)
        {
            total++;
            if (issues.Count == maximumIssues) return;
            issues.Add(new(path, reason, id is null ? null
                : id[..BoundEvidenceTextLength(id, 0, Math.Min(id.Length, maximumIdCharacters))], id?.Length));
        }

        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("claims", out var claims)
                || claims.ValueKind != JsonValueKind.Array)
                return Result("claims_unavailable");
            var allowed = currentEvidenceIds.ToHashSet(StringComparer.Ordinal);
            var claimIndex = 0;
            foreach (var claim in claims.EnumerateArray())
            {
                var path = $"claims[{claimIndex++}].evidenceIds";
                if (claim.ValueKind != JsonValueKind.Object
                    || !claim.TryGetProperty("evidenceIds", out var ids)
                    || ids.ValueKind != JsonValueKind.Array)
                {
                    Add(path, "reference_array_missing_or_invalid");
                    continue;
                }
                var referenceIndex = 0;
                var usableCount = 0;
                foreach (var value in ids.EnumerateArray())
                {
                    // Mirror ParseResult's treatment of whitespace and ignored
                    // non-string values; this diagnostic does not change parsing.
                    var id = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        usableCount++;
                        if (!allowed.Contains(id))
                            Add($"{path}[{referenceIndex}]", "id_not_in_current_evidence", id);
                    }
                    referenceIndex++;
                }
                if (usableCount == 0) Add(path, "no_usable_reference");
            }
            return Result("observed");
        }
        catch (JsonException)
        {
            return Result("invalid_json");
        }
    }
}
