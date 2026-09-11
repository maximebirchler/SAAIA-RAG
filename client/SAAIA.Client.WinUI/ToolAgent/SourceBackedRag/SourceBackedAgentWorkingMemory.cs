namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record SourceBackedAgentWorkingMemory(
    string Objective,
    string AnswerShape,
    IReadOnlyList<string> CoveredRequirements,
    IReadOnlyList<string> MissingRequirements,
    IReadOnlyList<string> UsefulEvidenceIds,
    IReadOnlyList<string> WeakEvidenceIds,
    IReadOnlyList<string> SearchAssessment,
    string DatabaseAssessment,
    string RecommendedNextAction,
    IReadOnlyList<string> RationaleNotes)
{
    public static SourceBackedAgentWorkingMemory Empty { get; } = new(
        string.Empty,
        string.Empty,
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        string.Empty,
        string.Empty,
        Array.Empty<string>());
}
