using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class Bug139CanonicalNamedReferenceTransportTests
{
    [Theory]
    [InlineData(
        "According to Atlas_Service_Manual.pdf, which interval is specified?",
        "Atlas_Service_Manual.pdf")]
    [InlineData(
        "Selon Guide_Orion_2025.pdf, quelle limite est documentée ?",
        "Guide_Orion_2025.pdf")]
    public void Bug139_LlmDocumentMissionWinsOverABroaderRawQuestionCapture(
        string question,
        string canonicalDocument)
    {
        var rawExtraction = ToolAgentOrchestrator
            .ExtractExplicitDocumentFileReferenceQueriesForTests(question);
        Assert.Contains(rawExtraction, candidate =>
            !string.Equals(
                candidate,
                canonicalDocument,
                StringComparison.Ordinal)
            && candidate.EndsWith(
                canonicalDocument,
                StringComparison.Ordinal));
        var plan = LlmMission(
            namedReferenceKind: "document",
            requestedDocumentName: canonicalDocument);

        var transported = ToolAgentOrchestrator
            .ResolveSourceBackedRequestedDocumentNameForTests(
                question,
                plan);
        var kind = ToolAgentOrchestrator
            .ResolveSourceBackedNamedReferenceKindForTests(
                question,
                plan);

        Assert.Equal(canonicalDocument, transported);
        Assert.Equal("document", kind);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("none")]
    public void Bug139_LlmNonDocumentMissionCannotBePromotedByRawFilenameSurface(
        string namedReferenceKind)
    {
        const string question =
            "Assess the AX-17 subject and compare the result with Decoy_File.pdf.";
        var plan = LlmMission(
            namedReferenceKind,
            requestedDocumentName: string.Empty);

        var transported = ToolAgentOrchestrator
            .ResolveSourceBackedRequestedDocumentNameForTests(
                question,
                plan);
        var kind = ToolAgentOrchestrator
            .ResolveSourceBackedNamedReferenceKindForTests(
                question,
                plan);

        Assert.Null(transported);
        Assert.Equal(namedReferenceKind, kind);
    }

    [Fact]
    public void Bug139_FocusedLlmDocumentIdentityCannotBeReplacedByQuestionCapture()
    {
        const string question =
            "Continue with the focused document, not Decoy_Appendix.pdf.";
        var plan = LlmMission(
            namedReferenceKind: "none",
            requestedDocumentName: "Focused_Operations_Guide.pdf",
            usesFocusedDocument: true);

        var transported = ToolAgentOrchestrator
            .ResolveSourceBackedRequestedDocumentNameForTests(
                question,
                plan);
        var kind = ToolAgentOrchestrator
            .ResolveSourceBackedNamedReferenceKindForTests(
                question,
                plan);

        Assert.Equal("Focused_Operations_Guide.pdf", transported);
        Assert.Equal("document", kind);
    }

    [Fact]
    public void Bug139_LlmMissionDocumentUsesOnlyMechanicalBasenameNormalization()
    {
        var plan = LlmMission(
            namedReferenceKind: "document",
            requestedDocumentName:
                @"C:\tenant\published\Canonical_Safety_Standard.pdf");

        var transported = ToolAgentOrchestrator
            .ResolveSourceBackedRequestedDocumentNameForTests(
                "Summarize the explicitly selected artifact.",
                plan);

        Assert.Equal("Canonical_Safety_Standard.pdf", transported);
    }

    [Fact]
    public void Bug139_LocalFallbackStillExtractsAnExplicitDocumentFromTheQuestion()
    {
        const string question = "Open \"Legacy_Field_Notes.pdf\".";
        var plan = new RouterPlan
        {
            Origin = RouterPlanOrigin.LocalFallback
        };

        var transported = ToolAgentOrchestrator
            .ResolveSourceBackedRequestedDocumentNameForTests(
                question,
                plan);
        var kind = ToolAgentOrchestrator
            .ResolveSourceBackedNamedReferenceKindForTests(
                question,
                plan);

        Assert.Equal("Legacy_Field_Notes.pdf", transported);
        Assert.Equal("document", kind);
    }

    [Theory]
    [InlineData("docPath")]
    [InlineData("docRef")]
    [InlineData("document")]
    public void Bug139_LocalFallbackStillReadsLegacyDocumentToolArguments(
        string propertyName)
    {
        var arguments = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                [propertyName] = @"C:\legacy\Archive_Manual.pdf"
            });
        var plan = new RouterPlan
        {
            Origin = RouterPlanOrigin.LocalFallback,
            ToolCalls = new List<RouterPlan.ToolCall>
            {
                new()
                {
                    Name = "documents.context",
                    Args = arguments
                }
            }
        };

        var transported = ToolAgentOrchestrator
            .ResolveSourceBackedRequestedDocumentNameForTests(
                "Continue with the selected artifact.",
                plan);

        Assert.Equal("Archive_Manual.pdf", transported);
    }

    [Fact]
    public void Bug139_InvalidEmptyLlmDocumentMissionIsNotReconstructedFromRawText()
    {
        const string question =
            "According to Untrusted_Reconstruction.pdf, list one requirement.";
        var impossibleAfterCanonicalParser = LlmMission(
            namedReferenceKind: "document",
            requestedDocumentName: string.Empty);

        var transported = ToolAgentOrchestrator
            .ResolveSourceBackedRequestedDocumentNameForTests(
                question,
                impossibleAfterCanonicalParser);

        Assert.Null(transported);
    }

    private static RouterPlan LlmMission(
        string namedReferenceKind,
        string requestedDocumentName,
        bool usesFocusedDocument = false)
        => new()
        {
            Origin = RouterPlanOrigin.Llm,
            SourceBackedMission = new RouterPlan.SourceBackedMissionPlan
            {
                NamedReferenceKind = namedReferenceKind,
                RequestedDocumentName = requestedDocumentName,
                UsesFocusedDocument = usesFocusedDocument
            }
        };
}
