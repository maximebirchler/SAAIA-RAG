namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

/// <summary>
/// Evidence accumulated by exactly one agent execution. Session memory and diagnostics do not own these mutable collections.
/// Existing retrieval policies operate on this owner's collections while their contracts are migrated separately.
/// </summary>
internal sealed class SourceBackedEvidenceExecutionState
{
    internal readonly ToolResults CumulativeResults = new();
    internal readonly List<RetrievalRequest> ExecutedRequests = new();
    internal readonly HashSet<string> ExecutedCallKeys = new(StringComparer.Ordinal);
    internal readonly HashSet<string> ResolvedNavigationEvidenceIds = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> AuditedNavigationEvidenceIds = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> ObservedEvidenceIds = new();
    internal readonly HashSet<string> ObservedEvidenceIdSet = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> SemanticallyRejectedEvidenceIds = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> SemanticallyAuditedEvidenceIds = new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> PendingSemanticCandidateIds = new(StringComparer.OrdinalIgnoreCase);
}
