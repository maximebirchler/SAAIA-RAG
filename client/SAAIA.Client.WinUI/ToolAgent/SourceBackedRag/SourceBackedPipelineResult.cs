namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record SourceBackedPipelineResult(
    string ResultId,
    SourceBackedIntake Intake,
    RetrievalPlan? RetrievalPlan,
    EvidenceBundle EvidenceBundle,
    EvidenceJudgeDecision JudgeDecision,
    WriterDraft? Draft,
    WriterDraft? FinalDraft,
    SourceVerificationResult? Verification,
    bool WasRepaired,
    IReadOnlyList<SourceBackedTraceEvent> TraceEvents,
    SourceBackedConversationTurnMemory? ConversationMemory = null,
    SourceBackedClarificationDecision? Clarification = null)
{
    public bool IsSourceVerified => Verification?.IsValid == true;

    public string? Answer => FinalDraft?.Answer;

    public IReadOnlyList<EvidenceItem> CitedEvidence
        => Verification?.CitedEvidence ?? Array.Empty<EvidenceItem>();
}

public sealed record SourceBackedClarificationDecision(
    string Message,
    IReadOnlyList<string> Options,
    string ExecutionImpact,
    string AmbiguityKind);
