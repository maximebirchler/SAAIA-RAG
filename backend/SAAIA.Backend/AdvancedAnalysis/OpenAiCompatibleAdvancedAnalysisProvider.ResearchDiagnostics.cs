using SAAIA.Backend.Models;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static bool HasDocumentaryResearchBudget(AdvancedAnalysisToolBudget? budget)
        => budget is null || budget.RemainingCalls > 0
            && budget.RemainingElapsedMilliseconds > 0
            && budget.EvidenceOperationsAllowed;

    // The complete diagnostic remains on the observation for SQL and probes.
    // Model context keeps one bounded sparse status so a dense-positive result
    // does not hide lexical zero hits. Phase timings and overlapping counters
    // remain omitted; they cannot be summed into supported distinct candidates.
    private static object? BuildRetrievalDiagnosticForPrompt(
        RagRetrievalDiagnosticsDto? diagnostic,
        IReadOnlyList<string>? degradedRetrievers)
        => diagnostic is null ? null : new
        {
            returnedEvidenceCandidates = diagnostic.Selection.Returned,
            canonicalSparseStatus = BuildCanonicalSparseStatus(diagnostic, degradedRetrievers),
            omittedPhases = diagnostic.DroppedPhases + diagnostic.Phases.Count,
            rejectionCounts = diagnostic.RejectionCounts?.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Take(16).Select(item => new
                {
                    reasonCode = BoundResearchDiagnosticCode(item.Key),
                    count = item.Value
                }).ToArray(),
            omittedRejectionReasons = Math.Max(0, (diagnostic.RejectionCounts?.Count ?? 0) - 16),
            diagnostic.DroppedRejections,
            instruction = "Operation counts: not distinct supported items; not exhaustive corpus coverage. Phases omitted."
        };

    private static string[] BuildDegradedRetrieversForPrompt(IReadOnlyList<string>? retrievers)
        => retrievers?.Select(BoundResearchDiagnosticCode).Distinct(StringComparer.Ordinal)
            .Take(16).ToArray() ?? [];

    private static object? BuildDocumentResolutionForPrompt(AdvancedAnalysisDocumentResolutionDiagnostic? resolution)
    {
        if (resolution is null) return null;
        var pathOmitted = resolution.ResolvedDocPath is { Length: > 2_000 };
        return new
        {
            status = BoundResearchDiagnosticCode(resolution.Status),
            resolution.CandidateCount,
            // A shortened path could be mistaken for another real identity.
            // Omit oversized metadata explicitly; never change the search scope.
            resolvedDocPath = pathOmitted ? null : resolution.ResolvedDocPath,
            resolvedDocPathOmitted = pathOmitted
        };
    }

    private static string BoundResearchDiagnosticCode(string code)
        => code.Length <= 80 ? code : code[..80];
}
