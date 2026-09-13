using Microsoft.Extensions.Logging.Abstractions;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisCanonicalPageReadTests
{
    [Theory]
    [InlineData("unobserved-document")]
    [InlineData("unobserved-revision")]
    [InlineData("different-path")]
    [InlineData("wide-window")]
    [InlineData("unknown-operation")]
    public async Task Native_page_read_rejects_invalid_or_unobserved_scope_without_database_access(string mutation)
    {
        var evidence = new AdvancedAnalysisResolvedEvidence(new AdvancedAnalysisResultEvidence {
            EvidenceId="E1", DocId=Guid.NewGuid().ToString(), RevisionId=Guid.NewGuid().ToString(),
            DocPath="Documents/manual.pdf", SourceHash="test", PageStart=61, PageEnd=61 }, "Procedure heading");
        var request = new AdvancedAnalysisSearchRequest("Canonical source physical pages 61-62", TopK:60,
            DocId:evidence.Reference.DocId, DocPath:evidence.Reference.DocPath, RevisionId:evidence.Reference.RevisionId,
            PageStart:61, PageEnd:62, Operation:"read_source");
        request = mutation switch {
            "unobserved-document" => request with {DocId=Guid.NewGuid().ToString()},
            "unobserved-revision" => request with {RevisionId=Guid.NewGuid().ToString()},
            "different-path" => request with {DocPath="Documents/other.pdf"},
            "wide-window" => request with {PageEnd=65},
            _ => request with {Operation="unknown"}
        };
        var bulkhead = new RagSearchBulkhead(Microsoft.Extensions.Options.Options.Create(new RagOptions()), NullLogger<RagSearchBulkhead>.Instance);
        var gateway = new AdvancedAnalysisToolGateway(Guid.NewGuid(), Guid.NewGuid(), [evidence], null!, new RagOptions(), null!, null!, bulkhead, null!, new AdvancedAnalysisOptions());
        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() => gateway.SearchAsync(request, CancellationToken.None));
        Assert.Equal(mutation switch { "wide-window"=>"canonical_read_scope_or_window_invalid", "unknown-operation"=>"research_operation_invalid", _=>"canonical_read_source_not_observed" }, error.ErrorCode);
        Assert.Single(gateway.Evidence); Assert.Equal(0, bulkhead.GetSnapshot().Active);
    }
}
