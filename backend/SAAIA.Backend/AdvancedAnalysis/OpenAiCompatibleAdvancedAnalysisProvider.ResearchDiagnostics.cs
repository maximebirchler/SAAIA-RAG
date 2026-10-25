using SAAIA.Backend.Models;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static bool HasDocumentaryResearchBudget(AdvancedAnalysisToolBudget? budget)
        => budget is null || budget.RemainingCalls > 0
            && budget.RemainingElapsedMilliseconds > 0
            && budget.RemainingEvidenceItems is not 0;

    // Retrieval candidates may contain snippets and full query copies. Return
    // only bounded operational counts; source facts remain in current evidence.
    private static object? BuildRetrievalDiagnosticForPrompt(RagRetrievalDiagnosticsDto? diagnostic)
        => diagnostic is null ? null : new
        {
            returnedEvidenceCandidates = diagnostic.Selection.Returned,
            phases = diagnostic.Phases.Take(16).Select(phase => new
            {
                name = BoundResearchDiagnosticCode(phase.Name),
                retriever = BoundResearchDiagnosticCode(phase.Retriever),
                phase.Returned,
                phase.DurationMs,
                phase.OmittedCandidates
            }).ToArray(),
            omittedPhases = diagnostic.DroppedPhases + Math.Max(0, diagnostic.Phases.Count - 16),
            rejectionCounts = diagnostic.RejectionCounts?.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Take(16).Select(item => new
                {
                    reasonCode = BoundResearchDiagnosticCode(item.Key),
                    count = item.Value
                }).ToArray(),
            omittedRejectionReasons = Math.Max(0, (diagnostic.RejectionCounts?.Count ?? 0) - 16),
            diagnostic.DroppedRejections
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
