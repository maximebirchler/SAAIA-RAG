using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class SourceContractVerifier
{
    private static readonly Regex AtomicClaimBoundaryPattern = new(
        @"(?:\r?\n)+|(?<=[.!?;])\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static IReadOnlyList<string> FindAtomicClaimCitationClusters(
        string? answer,
        IReadOnlySet<string> allowedIds)
    {
        if (string.IsNullOrWhiteSpace(answer) || allowedIds.Count <= 1)
            return Array.Empty<string>();

        return AtomicClaimBoundaryPattern
            .Split(answer)
            .Select(segment => EvidenceIdPattern
                .Matches(segment)
                .Select(static match =>
                    match.Groups[1].Value.ToUpperInvariant())
                .Where(allowedIds.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .Where(static ids => ids.Length > 1)
            .Select(static ids => string.Join(", ", ids))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
