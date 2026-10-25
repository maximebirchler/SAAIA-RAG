using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.NativeRouting;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class NativeDocumentPathIdentityTests
{
    [Theory]
    [InlineData("Validation/Guide-Validation.pdf")]
    [InlineData("Validation\\Guide-Validation.pdf")]
    [InlineData("Internal Docs/Guide-Validation.pdf")]
    public async Task Explicit_unique_user_path_survives_a_basename_only_model_route_and_reaches_the_resolver(string path)
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(new ToolMemory(),
            "Dans le document « " + path + " », quel est le code ?", "submit_source_backed_route",
            JsonSerializer.Serialize(new
            {
                tool = "search", intent = "answer", query = "code", answerUnitType = "code",
                answerUnitMode = "content_claim", selectionPolicy = "single_item", useFocusedDocument = false,
                questionFocus = "content", namedReferenceKind = "document", document = "Guide-Validation.pdf", pool = 4, count = 1
            }));
        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal(path, result.Plan.SourceBackedMission!.RequestedDocumentName);
        var intakeReference = ToolAgentOrchestrator.ResolveSourceBackedRequestedDocumentNameForTests(
            "Dans le document « " + path + " », quel est le code ?", result.Plan);
        var intake = new SourceBackedIntake("question", "rag.answer", [], [], true, "fr",
            RequestedDocumentName: intakeReference);
        Assert.Equal(path, intake.RequestedDocumentName);
        var catalog = new DuplicateFilenameCatalog(path.Replace('\\', '/'));
        var resolution = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync(intake.RequestedDocumentName!, CancellationToken.None);
        Assert.Equal(path, catalog.Queries[0]);
        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, resolution.Status);
        Assert.Equal("target", Assert.Single(resolution.Candidates).DocId);
    }

    [Theory]
    [InlineData("Guide-Validation.pdf", false)]
    [InlineData("Guide-Validation.pdf", true)]
    [InlineData("Validation/Guide-Validation.pdf", false)]
    [InlineData("Validation/Guide-Validation.pdf", true)]
    [InlineData("Validation\\Guide-Validation.pdf", false)]
    [InlineData("Validation\\Guide-Validation.pdf", true)]
    [InlineData("C:\\published\\Validation\\Guide-Validation.pdf", false)]
    [InlineData("C:\\published\\Validation\\Guide-Validation.pdf", true)]
    public async Task Executed_mission_to_intake_transport_retains_canonical_identity_and_ambiguity(string reference, bool focused)
    {
        var request = "Dans le document « " + reference + " », quel est le code ?";
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(new ToolMemory(), request,
            "submit_source_backed_route", JsonSerializer.Serialize(new
            {
                tool = "context", intent = "answer", query = "code", answerUnitType = "code",
                answerUnitMode = "content_claim", selectionPolicy = "single_item", useFocusedDocument = focused,
                questionFocus = "content", namedReferenceKind = "document", document = reference, pool = 4, count = 1
            }));
        Assert.True(result.Accepted, result.FailureReason);
        var intake = new SourceBackedIntake(request, "rag.answer", [], [], true, "fr", RequestedDocumentName:
            ToolAgentOrchestrator.ResolveSourceBackedRequestedDocumentNameForTests(request, result.Plan));
        Assert.Equal(reference, intake.RequestedDocumentName);
        var catalog = new DuplicateFilenameCatalog(reference.Contains('\\') || reference.Contains('/')
            ? reference.Replace('\\', '/') : "Validation/Guide-Validation.pdf");
        var resolution = await new SourceBackedNamedDocumentResolver(catalog)
            .ResolveAsync(intake.RequestedDocumentName!, CancellationToken.None);
        Assert.Equal(reference, catalog.Queries[0]);
        Assert.Equal(reference.Contains('\\') || reference.Contains('/')
            ? SourceBackedDocumentResolutionStatus.Resolved : SourceBackedDocumentResolutionStatus.Ambiguous, resolution.Status);
    }

    [Theory]
    [InlineData("Lis Validation/Guide.pdf.", "Guide.pdf", "Validation/Guide.pdf")]
    [InlineData("Lis « Internal Docs/Guide.pdf ».", "Guide.pdf", "Internal Docs/Guide.pdf")]
    [InlineData("Lis Validation/Guide.pdf puis Archive/Guide.pdf.", "Guide.pdf", "Guide.pdf")]
    [InlineData("Lis Guide.pdf.", "Guide.pdf", "Guide.pdf")]
    [InlineData("Lis ../Validation/Guide.pdf.", "Guide.pdf", "Guide.pdf")]
    [InlineData("Lis Validation/Guide.pdf.backup.", "Guide.pdf", "Guide.pdf")]
    [InlineData("Lis https://host/Guide.pdf puis Validation/Guide.pdf.", "Guide.pdf", "Guide.pdf")]
    [InlineData("Lis Validation/Guide.pdf.", "Archive/Guide.pdf", "Archive/Guide.pdf")]
    [InlineData("Lis Validation/OtherGuide.pdf.", "Guide.pdf", "Guide.pdf")]
    public void Path_recovery_is_exact_unique_and_never_guesses_or_expands_scope(string request, string announced, string expected)
        => Assert.Equal(expected, ExplicitDocumentPath.PreserveUniquePath(request, announced));

    [Theory]
    [InlineData("Guide-Validation.pdf", false)]
    [InlineData("Validation/Guide-Validation.pdf", true)]
    [InlineData("Validation\\Guide-Validation.pdf", true)]
    public async Task Router_mission_preserves_document_reference_until_exact_catalog_resolution(string reference, bool unique)
    {
        var result = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(new ToolMemory(),
            "Dans le document « " + reference + " », quel est le code ORION ?", "submit_source_backed_route",
            JsonSerializer.Serialize(new
            {
                tool = "search", intent = "answer", query = "code ORION", answerUnitType = "code",
                answerUnitMode = "content_claim", selectionPolicy = "single_item", useFocusedDocument = false,
                questionFocus = "content", namedReferenceKind = "document", document = reference, pool = 4, count = 1
            }));
        Assert.True(result.Accepted, result.FailureReason);
        Assert.Equal(reference, result.Plan.SourceBackedMission!.RequestedDocumentName);
        var observation = await new SourceBackedNamedDocumentResolver(new DuplicateFilenameCatalog())
            .ResolveAsync(result.Plan.SourceBackedMission.RequestedDocumentName, CancellationToken.None);
        Assert.Equal(unique ? SourceBackedDocumentResolutionStatus.Resolved : SourceBackedDocumentResolutionStatus.Ambiguous, observation.Status);
        if (unique) Assert.Equal("target", Assert.Single(observation.Candidates).DocId);
        else Assert.Equal(2, observation.Candidates.Count);
    }

    [Theory]
    [InlineData("Guide-Validation.pdf")]
    [InlineData("Validation/Guide-Validation.pdf")]
    [InlineData("Validation\\Guide-Validation.pdf")]
    public void Router_repair_preserves_the_complete_explicit_reference(string reference)
    {
        var completion = new SourceBackedAgentCompletion("", new[]
        {
            new SourceBackedAgentToolCall("route", "submit_source_backed_route",
                JsonSerializer.SerializeToElement(new { document = reference, namedReferenceKind = "document" }))
        }, "tool_calls");
        var method = typeof(ToolAgentOrchestrator).GetMethod("ExtractExplicitDocumentFromInvalidNativeRoute", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(reference, method.Invoke(null, new object[] { "Lis « " + reference + " ».", completion }));
    }

    private sealed class DuplicateFilenameCatalog(string targetPath = "Validation/Guide-Validation.pdf") : ISourceBackedNamedDocumentCatalogClient
    {
        internal readonly List<string> Queries = new();
        public Task<SourceBackedDocumentCatalogPage> SearchAsync(string query, int limit, int offset, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(new SourceBackedDocumentCatalogPage(new[]
            {
                new SourceBackedDocumentResolutionCandidate("target", targetPath, "Guide-Validation.pdf"),
                new SourceBackedDocumentResolutionCandidate("other", "Archive/Guide-Validation.pdf", "Guide-Validation.pdf")
            }, offset, limit, 2, true));
        }
    }
}
