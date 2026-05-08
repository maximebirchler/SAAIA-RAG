using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionAdminStatePoliciesTests
{
    [Theory]
    [InlineData("upsert", 0, 1)]
    [InlineData("upsert", -1, 1)]
    [InlineData("upsert", 1, 0)]
    [InlineData("delete", 0, 0)]
    [InlineData("summary", 0, 0)]
    public void GetRequestedAdminAction_matches_business_rules(string action, int indexedVersion, int expected)
    {
        var actual = IngestionAdminStatePolicies.GetRequestedAdminAction(action, indexedVersion);

        Assert.Equal((AdminJobControlAction)expected, actual);
    }

    [Theory]
    [InlineData("upsert", true, "admin_cancel", true)]
    [InlineData("upsert", true, "admin_pause", true)]
    [InlineData("upsert", true, "repeated_failures", false)]
    [InlineData("upsert", false, "admin_cancel", false)]
    [InlineData("delete", true, "admin_cancel", false)]
    public void ShouldTreatDocumentPauseAsCancellation_only_applies_to_initial_upsert_pause(
        string action,
        bool autoPaused,
        string reason,
        bool expected)
    {
        var actual = IngestionAdminStatePolicies.ShouldTreatDocumentPauseAsCancellation(action, autoPaused, reason);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("pause", true)]
    [InlineData("cancel", false)]
    [InlineData(null, false)]
    public void IsPauseRequested_matches_explicit_control_value(string? requestedAction, bool expected)
    {
        var actual = IngestionAdminStatePolicies.IsPauseRequested(requestedAction);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("superseded_version", false)]
    [InlineData("canceled_by_admin", true)]
    [InlineData("canceled_by_admin_token", true)]
    [InlineData("timeout_or_canceled", true)]
    [InlineData(null, true)]
    public void ShouldStabilizeDocumentAfterCancel_keeps_superseded_versions_requeueable(
        string? reason,
        bool expected)
    {
        var actual = IngestionWorker.ShouldStabilizeDocumentAfterCancel(reason);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("upsert", "paused", true, "admin_cancel", "pending", 0)]
    [InlineData("upsert", "paused", true, "admin_pause", "pending", 0)]
    [InlineData("upsert", "running", true, "admin_cancel", "pending", 2)]
    [InlineData("delete", "paused", true, "admin_cancel", "pending", 3)]
    [InlineData("upsert", "paused", false, "admin_cancel", "pending", 1)]
    [InlineData("upsert", "paused", true, "repeated_failures", "pending", 4)]
    [InlineData("upsert", "paused", true, "admin_cancel", "deleted", 5)]
    [InlineData("upsert", "paused", true, "admin_cancel", "missing", 5)]
    public void EvaluateResumeEligibility_enforces_resume_invariants(
        string action,
        string jobStatus,
        bool autoPaused,
        string reason,
        string documentStatus,
        int expected)
    {
        var actual = IngestionAdminStatePolicies.EvaluateResumeEligibility(
            action,
            jobStatus,
            autoPaused,
            reason,
            documentStatus);

        Assert.Equal((ResumeEligibility)expected, actual);
    }
}
