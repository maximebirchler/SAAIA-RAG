using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentFoundationRepoTests
{
    [Fact]
    public void Stable_revision_id_is_deterministic_for_same_inputs()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var left = DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 3);
        var right = DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 3);

        Assert.Equal(left, right);
    }

    [Fact]
    public void Stable_revision_id_changes_when_indexed_version_changes()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var previous = DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 2);
        var current = DocumentFoundationRepo.BuildStableRevisionId(tenantId, docId, 3);

        Assert.NotEqual(previous, current);
    }

    [Fact]
    public void Stable_processing_run_id_is_deterministic_for_same_job()
    {
        var jobId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var left = DocumentFoundationRepo.BuildStableProcessingRunId(jobId);
        var right = DocumentFoundationRepo.BuildStableProcessingRunId(jobId);

        Assert.Equal(left, right);
    }
}
