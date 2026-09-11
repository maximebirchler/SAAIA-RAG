namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class SourceContractVerifier
{
    private static void AddNamedDocumentScopeDisclosureError(
        List<SourceVerificationError> errors,
        WriterDraft draft,
        SourceBackedIntake? intake)
    {
        if (intake?.DocumentScope
                != SourceBackedDocumentScope.AlternativeSources
            || (!string.IsNullOrWhiteSpace(intake.DocumentScopeDisclosure)
                && draft.Answer.Contains(
                    intake.DocumentScopeDisclosure.Trim(),
                    StringComparison.Ordinal)))
        {
            return;
        }

        errors.Add(new SourceVerificationError(
            "alternative_source_scope_disclosure_missing",
            "The answer uses an explicit alternative-source scope but does not reproduce its required disclosure.",
            null,
            SourceBackedPipelineStep.SourceVerifier));
    }

    private static void AddNamedDocumentIdentityError(
        List<SourceVerificationError> errors,
        EvidenceItem item,
        SourceBackedIntake? intake)
    {
        var mismatch = item.RiskFlags.Contains(
            SourceBackedNamedDocumentIdentity.MismatchRiskFlag,
            StringComparer.OrdinalIgnoreCase);
        if (intake is not null
            && intake.DocumentScope == SourceBackedDocumentScope.RequestedDocument)
        {
            if (SourceBackedNamedDocumentIdentity
                    .TryGetResolvedRequestedIdentity(intake, out var expected))
            {
                mismatch |= !SourceBackedNamedDocumentIdentity.Matches(
                    expected,
                    item);
            }
            else if (SourceBackedQuestionFocus.MentionsSpecificDocument(intake))
            {
                mismatch |= !SourceBackedQuestionFocus.MatchesSpecificDocument(
                    intake,
                    item);
            }
        }
        if (!mismatch)
            return;

        var requested = intake is null
            ? string.Empty
            : SourceBackedQuestionFocus.ExtractFirstSpecificDocumentName(intake)
              ?? intake.RequestedDocumentName
              ?? string.Empty;
        errors.Add(new SourceVerificationError(
            "requested_document_source_mismatch",
            $"Evidence id '{item.EvidenceId}' cites '{item.DocName ?? item.DocPath}' but the user explicitly requested '{requested}'.",
            item.EvidenceId,
            SourceBackedPipelineStep.SourceVerifier));
    }
}
