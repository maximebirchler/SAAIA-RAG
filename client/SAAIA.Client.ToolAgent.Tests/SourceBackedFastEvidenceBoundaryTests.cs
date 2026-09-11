using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedFastEvidenceBoundaryTests
{
    [Fact]
    public void Exact_named_document_widens_initial_review_to_six_candidates()
    {
        var intake = BuildIntake(new SourceBackedDocumentResolutionObservation(
            "named-document.pdf",
            SourceBackedDocumentResolutionStatus.Resolved,
            CatalogObservationComplete: true,
            new[]
            {
                new SourceBackedDocumentResolutionCandidate(
                    "doc-1",
                    "Docs/named-document.pdf",
                    "named-document.pdf")
            },
            "resolved_exact_current_catalog",
            PagesObserved: 1,
            ItemsObserved: 1,
            ReachedSafetyLimit: false,
            ElapsedMilliseconds: 1));

        Assert.Equal(
            6,
            SourceBackedAgentV2Runner.ResolveFastEvidenceReviewCandidateLimitForTests(
                intake,
                carriedCandidateCount: 0));
    }

    [Theory]
    [InlineData(SourceBackedDocumentResolutionStatus.NotFound)]
    [InlineData(SourceBackedDocumentResolutionStatus.Ambiguous)]
    [InlineData(SourceBackedDocumentResolutionStatus.Inconclusive)]
    public void Unresolved_named_document_keeps_three_initial_candidates(
        SourceBackedDocumentResolutionStatus status)
    {
        var intake = BuildIntake(new SourceBackedDocumentResolutionObservation(
            "named-document.pdf",
            status,
            CatalogObservationComplete: status != SourceBackedDocumentResolutionStatus.Inconclusive,
            status == SourceBackedDocumentResolutionStatus.Ambiguous
                ? new[]
                {
                    new SourceBackedDocumentResolutionCandidate("doc-1", "Docs/one.pdf", "one.pdf"),
                    new SourceBackedDocumentResolutionCandidate("doc-2", "Docs/two.pdf", "two.pdf")
                }
                : Array.Empty<SourceBackedDocumentResolutionCandidate>(),
            "test",
            PagesObserved: 1,
            ItemsObserved: 1,
            ReachedSafetyLimit: false,
            ElapsedMilliseconds: 1));

        Assert.Equal(
            3,
            SourceBackedAgentV2Runner.ResolveFastEvidenceReviewCandidateLimitForTests(
                intake,
                carriedCandidateCount: 0));
    }

    [Fact]
    public void Protocol_retry_keeps_existing_six_candidate_limit()
    {
        var intake = BuildIntake(resolution: null);

        Assert.Equal(
            6,
            SourceBackedAgentV2Runner.ResolveFastEvidenceReviewCandidateLimitForTests(
                intake,
                carriedCandidateCount: 1));
    }

    private static SourceBackedIntake BuildIntake(
        SourceBackedDocumentResolutionObservation? resolution)
        => new(
            "Question directe",
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "fr",
            RequestedDocumentName: "named-document.pdf",
            RequestedDocumentResolution: resolution);
}
