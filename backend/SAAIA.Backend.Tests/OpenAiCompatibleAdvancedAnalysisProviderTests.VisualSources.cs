using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Visual_source_citations_reach_actual_http_and_native_output_without_pixels_even_without_new_text(string protocol, bool emptyRead)
    {
        const string arguments = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":20,"includeVisualSources":true,"visualSourceOffset":0,"visualSourceLimit":1}""";
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_source", arguments) : NativeCompletion(("read_source", arguments)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var options = WorkspaceOptions(protocol);
        options.VisualSourceCitationsEnabled = true;
        var gateway = new VisualCitationGateway(emptyRead);
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal(2, gateway.Searches.Count);
        Assert.True(gateway.Searches[1].IncludeVisualSources);
        var initial = WorkspaceUser(factory.Requests[0].Body);
        Assert.True(initial.GetProperty("visualSourceCapabilities").GetProperty("visualSourceCitationAvailable").GetBoolean());
        Assert.Contains("includeVisualSources=true", initial.GetProperty("visualSourceCitationContract").GetString(), StringComparison.Ordinal);
        var first = WorkspaceUser(factory.Requests[1].Body);
        Assert.True(first.GetProperty("visualSourceCapabilities").GetProperty("visualSourceCitationAvailable").GetBoolean());
        Assert.Contains("includeVisualSources", factory.Requests[1].Body, StringComparison.Ordinal);
        var final = WorkspaceUser(factory.Requests[2].Body);
        var visual = Assert.Single(final.GetProperty("visualSources").EnumerateArray());
        AssertVisual(visual, gateway);
        var item = Assert.Single(final.GetProperty("evidence").EnumerateArray());
        Assert.Contains("structured_transcription_not_verified", item.GetProperty("sourceQuality").GetRawText(), StringComparison.Ordinal);
        using var body = JsonDocument.Parse(factory.Requests[2].Body);
        var tool = body.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray().Single(value => protocol == "responses"
            ? value.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            : value.TryGetProperty("role", out var role) && role.GetString() == "tool");
        using var output = JsonDocument.Parse(tool.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        AssertVisual(output.RootElement.GetProperty("visualSources"), gateway);
        Assert.Equal(emptyRead ? 0 : 1, output.RootElement.GetProperty("returnedEvidenceCount").GetInt32());
        Assert.DoesNotContain("data:image", factory.Requests[2].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("image_url", factory.Requests[2].Body, StringComparison.Ordinal);
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(factory.Requests[2].Body), 1, 8_000);
    }

    [Fact]
    public async Task Visual_source_mode_off_preserves_legacy_roster_and_omits_all_new_capabilities()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
        Assert.DoesNotContain("visualSource", factory.Requests[1].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("source", false)]
    [InlineData("revision", false)]
    [InlineData("chunk", false)]
    [InlineData("anchor", false)]
    public void Visual_source_refresh_preserves_original_text_quality_and_requires_the_exact_current_evidence_identity(
        string mutation, bool expected)
    {
        var original = new VisualCitationGateway(false).Evidence[0];
        var originalReference = original.Reference;
        var currentReference = new AdvancedAnalysisResultEvidence
        {
            EvidenceId = originalReference.EvidenceId,
            DocId = originalReference.DocId,
            RevisionId = mutation == "revision" ? Guid.NewGuid().ToString("D") : originalReference.RevisionId,
            SourceHash = mutation == "source" ? new string('c', 64) : originalReference.SourceHash,
            ChunkId = mutation == "chunk" ? Guid.NewGuid().ToString("D") : originalReference.ChunkId,
            AnchorId = mutation == "anchor" ? "another_anchor" : originalReference.AnchorId,
            ContentCardId = originalReference.ContentCardId
        };
        var visual = AdvancedAnalysisVisualSourceProjector.Project(Guid.Parse(currentReference.DocId),
            Guid.Parse(currentReference.RevisionId), currentReference.SourceHash,
            1, 1, true, 0, [], 0, 4, 20_000);
        var latest = original with
        {
            Reference = currentReference,
            Content = "A replacement text must not overwrite the selected excerpt.",
            SourceQuality = null,
            SourceOverview = original.SourceOverview! with { VisualSources = visual }
        };
        var method = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("OrderEvidenceForPrompt",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var result = (IReadOnlyList<AdvancedAnalysisResolvedEvidence>)method.Invoke(null,
            new object[] { new[] { latest }, new IReadOnlyList<AdvancedAnalysisResolvedEvidence>[] { new[] { original } } })!;
        var retained = Assert.Single(result);
        Assert.Equal(original.Content, retained.Content);
        Assert.Same(original.SourceQuality, retained.SourceQuality);
        Assert.Equal(expected, retained.SourceOverview!.VisualSources is not null);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Visual_source_final_nested_tool_wire_is_measured_and_refused_before_additional_http(string protocol)
    {
        const string arguments = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":20,"includeVisualSources":true,"visualSourceOffset":0,"visualSourceLimit":64}""";
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_source", arguments) : NativeCompletion(("read_source", arguments)),
            Completion(NativeAnswered));
        var options = WorkspaceOptions(protocol);
        options.VisualSourceCitationsEnabled = true;
        options.MaximumEvidencePromptCharacters = 8_000;
        options.NativeResearchMaximumHistoryCharacters = 65_536;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new VisualCitationGateway(false, 16), CancellationToken.None));
        Assert.Equal("advanced_visual_documentary_wire_budget_exceeded", error.ErrorCode);
        Assert.Equal(2, factory.Requests.Count);
    }

    private static void AssertVisual(JsonElement visual, VisualCitationGateway gateway)
    {
        Assert.False(visual.GetProperty("imageToolAvailable").GetBoolean());
        Assert.False(visual.GetProperty("modelPixelsProvided").GetBoolean());
        Assert.Equal(JsonValueKind.Null, visual.GetProperty("humanViewerAvailable").ValueKind);
        var asset = Assert.Single(visual.GetProperty("assets").EnumerateArray());
        Assert.Equal(gateway.Reference.DocId, asset.GetProperty("docId").GetString());
        Assert.Equal(gateway.Reference.RevisionId, asset.GetProperty("revisionId").GetString());
        Assert.Equal(gateway.Reference.SourceHash, asset.GetProperty("sourceSha256").GetString());
        Assert.Equal("not_verified", visual.GetProperty("semanticAccuracy").GetString());
        Assert.StartsWith($"/documents/{gateway.Reference.DocId}/revisions/{gateway.Reference.RevisionId}/assets/", asset.GetProperty("authenticatedApiPath").GetString()!);
    }

    private sealed class VisualCitationGateway : IAdvancedAnalysisToolGateway
    {
        private AdvancedAnalysisResolvedEvidence _item;
        private readonly bool _empty;
        private readonly int _assetCount;
        public VisualCitationGateway(bool empty, int assetCount = 1)
        {
            _empty = empty;
            _assetCount = assetCount;
            var original = BuildEvidence("E1", "Le seuil est 17 bar.");
            _item = original with
            {
                Reference = new AdvancedAnalysisResultEvidence
                {
                    EvidenceId = "E1",
                    DocId = original.Reference.DocId,
                    RevisionId = original.Reference.RevisionId,
                    DocPath = original.Reference.DocPath,
                    FileName = original.Reference.FileName,
                    SourceHash = new string('a', 64),
                    PageStart = 1,
                    PageEnd = 1,
                    ChunkId = original.Reference.ChunkId
                },
                SourceOverview = new(1, 1, 1),
                SourceQuality = new("ok", null, ["observed_block_type_formula", "structured_transcription_not_verified"])
            };
        }
        public AdvancedAnalysisResultEvidence Reference => _item.Reference;
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => [_item];
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken ct)
        {
            Searches.Add(request);
            AdvancedAnalysisVisualSources? visual = null;
            if (request.IncludeVisualSources)
            {
                var assets = Enumerable.Range(0, _assetCount).Select(index => new CanonicalVisualAssetDescriptor("asset_" + index + new string('x', 90), 1, null,
                    Reference.SourceHash, new string('b', 64), "image/png", 640, 900, 1234)).ToArray();
                visual = AdvancedAnalysisVisualSourceProjector.Project(Guid.Parse(Reference.DocId), Guid.Parse(Reference.RevisionId), Reference.SourceHash,
                    1, 1, true, _assetCount, assets, 0, request.VisualSourceLimit ?? 4, 200_000);
                _item = _item with { SourceOverview = _item.SourceOverview! with { VisualSources = visual } };
            }
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> returned = _empty && request.IncludeVisualSources ? [] : [_item];
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, returned, [], 1, Searches.Count, VisualSources: visual));
        }
    }
}
