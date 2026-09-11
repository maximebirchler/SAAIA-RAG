namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public enum SourceBackedPipelineStep
{
    UserQuestion,
    Planner,
    RetrievalTools,
    EvidenceBundle,
    EvidenceStatusReview,
    EvidenceJudge,
    IterationController,
    Writer,
    SourceVerifier,
    AnswerAdequacyJudge,
    Repair,
    UiResponse
}
