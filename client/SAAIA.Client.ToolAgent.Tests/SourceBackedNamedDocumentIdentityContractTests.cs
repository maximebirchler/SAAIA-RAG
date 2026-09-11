using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedNamedDocumentIdentityContractTests
{
    [Fact]
    public void Requested_document_scope_marks_another_doc_id_unselectable()
    {
        var intake = Intake(SourceBackedDocumentScope.RequestedDocument);
        var guarded = SourceBackedAgentV2Runner
            .ApplyNamedDocumentIdentityContractForTests(
                intake,
                Bundle("doc-neighbor"));

        Assert.Contains(
            "requested_document_identity_mismatch",
            Assert.Single(guarded.Items).RiskFlags,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Alternative_source_scope_preserves_the_real_alternative_identity()
    {
        var intake = Intake(SourceBackedDocumentScope.AlternativeSources) with
        {
            DocumentScopeDisclosure =
                "The requested file was not found; this uses another source."
        };
        var guarded = SourceBackedAgentV2Runner
            .ApplyNamedDocumentIdentityContractForTests(
                intake,
                Bundle("doc-neighbor"));

        Assert.DoesNotContain(
            "requested_document_identity_mismatch",
            Assert.Single(guarded.Items).RiskFlags,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("doc-neighbor", guarded.Items[0].DocId);
    }

    [Fact]
    public void Requested_document_scope_rejects_a_stale_revision_of_same_doc_id()
    {
        var staleBundle = Bundle("doc-target") with
        {
            Items = Bundle("doc-target").Items
                .Select(static item => item with
                {
                    RevisionId = "rev-stale"
                })
                .ToArray()
        };

        var guarded = SourceBackedAgentV2Runner
            .ApplyNamedDocumentIdentityContractForTests(
                Intake(SourceBackedDocumentScope.RequestedDocument),
                staleBundle);

        Assert.Contains(
            "requested_document_identity_mismatch",
            Assert.Single(guarded.Items).RiskFlags,
            StringComparer.OrdinalIgnoreCase);
    }

    private static SourceBackedIntake Intake(SourceBackedDocumentScope scope)
        => new(
            "Read Service Bulletin HX-42.pdf.",
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "en",
            RequestedDocumentName: "Service Bulletin HX-42.pdf",
            RequestedDocumentResolution:
                new SourceBackedDocumentResolutionObservation(
                    "Service Bulletin HX-42.pdf",
                    SourceBackedDocumentResolutionStatus.Resolved,
                    CatalogObservationComplete: true,
                    new[]
                    {
                        new SourceBackedDocumentResolutionCandidate(
                            "doc-target",
                            "Operations/Service Bulletin HX-42.pdf",
                            "Service Bulletin HX-42.pdf",
                            "rev-current",
                            new string('c', 64))
                    },
                    "resolved_exact_current_catalog",
                    PagesObserved: 1,
                    ItemsObserved: 1,
                    ReachedSafetyLimit: false,
                    ElapsedMilliseconds: 1),
            DocumentScope: scope);

    private static EvidenceBundle Bundle(string docId)
        => new(
            "bundle-named-document",
            "Read Service Bulletin HX-42.pdf.",
            new[]
            {
                new EvidenceItem(
                    "E1",
                    "rag_hit",
                    "rag.search",
                    "inspection interval",
                    docId,
                    "Service Bulletin HX-42.pdf",
                    "Operations/Service Bulletin HX-42.pdf",
                    new string('c', 64),
                    "rev-current",
                    4,
                    4,
                    "chunk-4",
                    "The documented inspection interval is stated here.",
                    "the documented inspection interval is stated here",
                    0.9,
                    1,
                    "Operations",
                    "en",
                    "en",
                    "native_text",
                    (JsonElement?)null,
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>())
            },
            Array.Empty<SourceBackedTraceEvent>());
}
