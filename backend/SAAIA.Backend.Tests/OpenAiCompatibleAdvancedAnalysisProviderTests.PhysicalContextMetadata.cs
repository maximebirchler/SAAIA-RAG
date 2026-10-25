using System.Reflection;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("physical_page_context_unassigned_prefix")]
    [InlineData("physical_page_context_unassigned_suffix")]
    public async Task Physical_context_metadata_reaches_the_model_without_becoming_content_or_a_heading(string composition)
    {
        const string content = "Cycle : 20 minutes\nInspect the installed module and record the verified isolation state.";
        var evidence = BuildEvidence("E1", content) with { SourceStructure = PhysicalContextStructure(composition) };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(evidence), CancellationToken.None);

        var visible = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        var structure = visible.GetProperty("sourceStructure");
        Assert.Equal("physical_page_context_v1", structure.GetProperty("chunkType").GetString());
        Assert.Equal(composition, structure.GetProperty("chunkComposition").GetString());
        Assert.Equal("physical_page", structure.GetProperty("contextScope").GetString());
        Assert.Equal("not_asserted", structure.GetProperty("sectionAssociation").GetString());
        Assert.Equal(content, visible.GetProperty("content").GetString());
        Assert.True(!visible.TryGetProperty("candidateTitle", out var title) || title.ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain("PRIVATE_NEIGHBOR_ID", visible.GetRawText(), StringComparison.Ordinal);
        using var request = JsonDocument.Parse(factory.Requests[1].Body);
        var system = request.RootElement.GetProperty("messages").EnumerateArray()
            .Single(message => message.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;
        Assert.Contains("not_asserted", system, StringComparison.Ordinal);
        Assert.Contains("physical co-location", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Physical_context_scope_remains_explicit_when_oversized_composition_metadata_is_omitted()
    {
        var evidence = BuildEvidence("E1", "Hold below the documented pressure limit.") with
        {
            SourceStructure = PhysicalContextStructure(new string('X', 20_000))
        };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(evidence), CancellationToken.None);

        var visible = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        var structure = visible.GetProperty("sourceStructure");
        Assert.Equal("physical_page", structure.GetProperty("contextScope").GetString());
        Assert.Equal("not_asserted", structure.GetProperty("sectionAssociation").GetString());
        Assert.True(structure.GetProperty("metadataTruncated").GetBoolean());
        Assert.True(!structure.TryGetProperty("chunkComposition", out var composition) || composition.ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain(new string('X', 1_000), factory.Requests[1].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("read_source")]
    [InlineData("find_source_text")]
    public void Canonical_navigation_does_not_promote_an_unassigned_physical_context_first_line_to_an_exact_heading(string operation)
    {
        var evidence = BuildEvidence("E1", "Unassigned value\nInspect the installed module and record the verified isolation state.") with
        {
            SourceStructure = PhysicalContextStructure("physical_page_context_unassigned_prefix")
        };

        var promoted = InvokePhysicalContextTitlePromotion(operation, evidence);

        Assert.Null(promoted.ExactTitle);
        Assert.Equal(evidence.Reference, promoted.Reference);
        Assert.Equal(evidence.Content, promoted.Content);
    }

    [Fact]
    public void Canonical_navigation_still_preserves_an_ordinary_observed_heading()
    {
        var evidence = BuildEvidence("E1", "Observed heading\nInspect the installed module and record the verified isolation state.");

        var promoted = InvokePhysicalContextTitlePromotion("read_source", evidence);

        Assert.Equal("Observed heading", promoted.ExactTitle);
        Assert.Equal(evidence.Reference, promoted.Reference);
        Assert.Equal(evidence.Content, promoted.Content);
    }

    private static AdvancedAnalysisEvidenceStructure PhysicalContextStructure(string composition)
        => JsonSerializer.Deserialize<AdvancedAnalysisEvidenceStructure>(JsonSerializer.Serialize(new
        {
            sectionTitle = (string?)null, headingPath = (string?)null,
            previousChunkId = "PRIVATE_NEIGHBOR_ID", nextChunkId = (string?)null,
            chunkType = "physical_page_context_v1", chunkComposition = composition
        }), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static AdvancedAnalysisResolvedEvidence InvokePhysicalContextTitlePromotion(string operation,
        AdvancedAnalysisResolvedEvidence evidence)
    {
        var method = typeof(AdvancedAnalysisToolGateway).GetMethod(operation == "read_source"
                ? "PromoteLeadingCanonicalReadTitle" : "PromoteLeadingCanonicalFindTitle",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (AdvancedAnalysisResolvedEvidence)method.Invoke(null, operation == "read_source"
            ? [evidence] : ["Unassigned value", evidence])!;
    }
}
