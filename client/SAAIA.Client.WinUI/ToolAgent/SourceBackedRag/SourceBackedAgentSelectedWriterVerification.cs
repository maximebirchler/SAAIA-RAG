namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record SelectedWriterVerification(
        SourceVerificationResult Verification,
        bool CitedContextEvidenceBeyondSemanticSelection,
        IReadOnlyList<string> AllowedEvidenceIds,
        int RequiredEvidenceGroupCount);

    private static SelectedWriterVerification VerifySelectedWriterDraft(
        WriterDraft draft,
        EvidenceBundle bundle,
        SourceBackedIntake intake,
        IReadOnlyList<string>? semanticSelectionIds,
        string atomicEvidenceMode,
        bool enforceRequestedShape)
    {
        var writerContext = semanticSelectionIds is { Count: > 0 }
            ? BuildSelectedEvidenceWriterContext(
                bundle,
                semanticSelectionIds,
                atomicEvidenceMode)
            : null;
        var citedContextEvidenceBeyondSemanticSelection =
            semanticSelectionIds is { Count: > 0 }
            && draft.CitedEvidenceIds
                .Except(
                    semanticSelectionIds,
                    StringComparer.OrdinalIgnoreCase)
                .Any();
        var contentClaimMode = string.Equals(
            atomicEvidenceMode,
            "content_claim",
            StringComparison.OrdinalIgnoreCase);
        var verification = SourceContractVerifier.Verify(
            draft,
            bundle,
            intake,
            allowedEvidenceIds:
                writerContext?.AllowedEvidenceIds ?? semanticSelectionIds,
            enforceRequestedShape: enforceRequestedShape,
            requireEveryAllowedEvidenceIdExactlyOnce: false,
            allowMultipleEvidencePerVisibleSource:
                contentClaimMode
                || writerContext?.Groups.Any(static group =>
                    group.CitableEvidence.Count > 1) == true,
            requireSeparateAtomicClaims:
                semanticSelectionIds is { Count: > 1 }
                && contentClaimMode,
            requiredEvidenceIdGroups:
                writerContext?.RequiredEvidenceIdGroups,
            requireCitedAllowedEvidenceIdsAtMostOnce:
                writerContext?.RequiredEvidenceIdGroups is { Count: > 1 }
                && !contentClaimMode);
        return new SelectedWriterVerification(
            verification,
            citedContextEvidenceBeyondSemanticSelection,
            writerContext?.AllowedEvidenceIds
            ?? semanticSelectionIds
            ?? Array.Empty<string>(),
            writerContext?.RequiredEvidenceIdGroups.Count ?? 0);
    }
}
