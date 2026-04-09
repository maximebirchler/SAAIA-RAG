using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdminJobsQuerySupportTests
{
    [Theory]
    [InlineData(null, "finished")]
    [InlineData("", "finished")]
    [InlineData("created", "created")]
    [InlineData("started", "started")]
    [InlineData("FINISHED", "finished")]
    [InlineData("weird", "finished")]
    public void NormalizeDateField_matches_expected_defaults(string? value, string expected)
    {
        var actual = AdminJobsQuerySupport.NormalizeDateField(value);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null, "DESC")]
    [InlineData("", "DESC")]
    [InlineData("asc", "ASC")]
    [InlineData("ASC", "ASC")]
    [InlineData("desc", "DESC")]
    [InlineData("other", "DESC")]
    public void NormalizeSortDirection_matches_expected_defaults(string? value, string expected)
    {
        var actual = AdminJobsQuerySupport.NormalizeSortDirection(value);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseDateParameter_accepts_roundtrip_utc_value()
    {
        var value = "2026-04-09T00:00:00.0000000Z";

        var actual = AdminJobsQuerySupport.ParseDateParameter(value);

        Assert.Equal(DateTimeOffset.Parse(value), actual);
    }

    [Theory]
    [InlineData("running", true, "pause", "upsert", 0, true, "admin_pause", "paused")]
    [InlineData("running", true, "cancel", "upsert", 2, false, null, "cancel_requested")]
    [InlineData("paused", false, null, "upsert", 0, true, "admin_pause", "paused")]
    [InlineData("done", false, null, "upsert", 1, false, null, "done")]
    public void GetVisibleIngestionStatus_preserves_admin_jobs_projection(
        string status,
        bool cancelRequested,
        string? requestedAction,
        string action,
        int indexedVersion,
        bool autoPaused,
        string? pauseReason,
        string expected)
    {
        var actual = AdminJobsQuerySupport.GetVisibleIngestionStatus(
            status,
            cancelRequested,
            requestedAction,
            action,
            indexedVersion,
            autoPaused,
            pauseReason);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(@"General\Accords\", "General/Accords")]
    [InlineData("  General/Accords  ", "General/Accords")]
    [InlineData("/", null)]
    [InlineData(null, null)]
    public void CategoryResolvers_normalize_paths_consistently(string? raw, string? expected)
    {
        Assert.Equal(expected, SummaryCategoryScopeResolver.NormalizeCategoryPathOrNull(raw));
        Assert.Equal(expected, DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(raw));
    }
}
