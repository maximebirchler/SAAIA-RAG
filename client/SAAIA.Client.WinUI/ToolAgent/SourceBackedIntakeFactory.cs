using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

/// <summary>Maps routing metadata to intake without relabelling risk flags as user constraints.</summary>
internal static class SourceBackedIntakeFactory
{
    internal static SourceBackedIntake Create(string question, string intent, RouterPlan plan,
        bool allowsPartialAnswer, IReadOnlyList<SourceBackedCatalogHint> catalog,
        SourceBackedMemoryContext? memory, string? requestedDocument, string? namedReferenceKind)
        => new(question, intent, ExplicitConstraints: Array.Empty<string>(), RequestedAxes: Array.Empty<string>(),
            AllowsPartialAnswer: allowsPartialAnswer, Language: plan.Language, CatalogHints: catalog, MemoryContext: memory,
            QuestionFocus: string.IsNullOrWhiteSpace(plan.SourceBackedMission?.QuestionFocus) ? null : plan.SourceBackedMission.QuestionFocus,
            RequestedDocumentName: requestedDocument, NamedReferenceKind: namedReferenceKind,
            RiskFlags: plan.RiskFlags?.ToArray() ?? Array.Empty<string>(),
            RequiredCategoryPaths: RequiredScope(question, plan.SourceBackedMission));

    private static IReadOnlyList<string> RequiredScope(string question, RouterPlan.SourceBackedMissionPlan? mission)
    {
        if (mission?.CategoryScopePolicy != "user_restriction") return Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(mission.CategoryScopeUserAnchor)
            || !question.Contains(mission.CategoryScopeUserAnchor, StringComparison.OrdinalIgnoreCase)
            || mission.RequiredCategoryPaths.Count == 0)
            throw new InvalidOperationException("mandatory_scope_user_anchor_invalid");
        return mission.RequiredCategoryPaths.ToArray();
    }
}
