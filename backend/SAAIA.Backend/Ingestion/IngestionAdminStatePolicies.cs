using System;

internal enum AdminJobControlAction
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
    public static AdminJobControlAction GetRequestedAdminAction(string? action, int documentIndexedVersion)
        => IsInitialUpsert(action, documentIndexedVersion) ? AdminJobControlAction.Pause : AdminJobControlAction.Cancel;

    public static bool IsInitialUpsert(string? action, int documentIndexedVersion)
        => string.Equals(action, "upsert", StringComparison.OrdinalIgnoreCase)
           && documentIndexedVersion <= 0;

    public static bool CanPause(string? action, int documentIndexedVersion)
        => IsInitialUpsert(action, documentIndexedVersion);

    public static string ToControlValue(AdminJobControlAction action)
        => action == AdminJobControlAction.Pause ? "pause" : "cancel";

    public static bool IsPauseRequested(string? requestedAction)
        => string.Equals(requestedAction, "pause", StringComparison.OrdinalIgnoreCase);

    public static bool IsCancelRequested(string? requestedAction)
        => string.Equals(requestedAction, "cancel", StringComparison.OrdinalIgnoreCase);

    public static bool IsAdminPauseReason(string? documentAutoIngestPauseReason)
        => string.Equals(documentAutoIngestPauseReason, "admin_cancel", StringComparison.OrdinalIgnoreCase)
           || string.Equals(documentAutoIngestPauseReason, "admin_pause", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldTreatDocumentPauseAsCancellation(string? action, bool documentAutoIngestPaused, string? documentAutoIngestPauseReason)
        => string.Equals(action, "upsert", StringComparison.OrdinalIgnoreCase)
           && documentAutoIngestPaused
           && IsAdminPauseReason(documentAutoIngestPauseReason);

    public static bool ShouldPresentAsPaused(
        string? action,
        int documentIndexedVersion,
        string? requestedAction,
        bool documentAutoIngestPaused,
        string? documentAutoIngestPauseReason)
        => IsInitialUpsert(action, documentIndexedVersion)
           && (IsPauseRequested(requestedAction)
               || ShouldTreatDocumentPauseAsCancellation(action, documentAutoIngestPaused, documentAutoIngestPauseReason));

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

        if (!IsAdminPauseReason(documentAutoIngestPauseReason))
            return ResumeEligibility.UnsupportedPauseReason;

        if (string.Equals(documentStatus, "deleted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(documentStatus, "missing", StringComparison.OrdinalIgnoreCase))
            return ResumeEligibility.DocumentDeleted;

        return ResumeEligibility.Resumable;
    }
}
