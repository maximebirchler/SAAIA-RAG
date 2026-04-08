using System;

internal enum AdminCancelAction
{
    Cancel = 0,
    Pause = 1
}

internal enum ResumeEligibility
{
    Resumable = 0,
    NotPaused = 1,
    WrongJobStatus = 2,
    NotUpsert = 3,
    UnsupportedPauseReason = 4,
    DocumentDeleted = 5
}

internal static class IngestionAdminStatePolicies
{
    public static AdminCancelAction GetRequestedAdminAction(string? action, int documentIndexedVersion)
        => IsInitialUpsert(action, documentIndexedVersion) ? AdminCancelAction.Pause : AdminCancelAction.Cancel;

    public static bool IsInitialUpsert(string? action, int documentIndexedVersion)
        => string.Equals(action, "upsert", StringComparison.OrdinalIgnoreCase)
           && documentIndexedVersion <= 0;

    public static bool ShouldTreatDocumentPauseAsCancellation(string? action, bool documentAutoIngestPaused, string? documentAutoIngestPauseReason)
        => string.Equals(action, "upsert", StringComparison.OrdinalIgnoreCase)
           && documentAutoIngestPaused
           && string.Equals(documentAutoIngestPauseReason, "admin_cancel", StringComparison.OrdinalIgnoreCase);

    public static ResumeEligibility EvaluateResumeEligibility(
        string? action,
        string? jobStatus,
        bool documentAutoIngestPaused,
        string? documentAutoIngestPauseReason,
        string? documentStatus)
    {
        if (!documentAutoIngestPaused)
            return ResumeEligibility.NotPaused;

        if (!string.Equals(action, "upsert", StringComparison.OrdinalIgnoreCase))
            return ResumeEligibility.NotUpsert;

        if (!string.Equals(jobStatus, "paused", StringComparison.OrdinalIgnoreCase))
            return ResumeEligibility.WrongJobStatus;

        if (!string.Equals(documentAutoIngestPauseReason, "admin_cancel", StringComparison.OrdinalIgnoreCase))
            return ResumeEligibility.UnsupportedPauseReason;

        if (string.Equals(documentStatus, "deleted", StringComparison.OrdinalIgnoreCase))
            return ResumeEligibility.DocumentDeleted;

        return ResumeEligibility.Resumable;
    }
}
