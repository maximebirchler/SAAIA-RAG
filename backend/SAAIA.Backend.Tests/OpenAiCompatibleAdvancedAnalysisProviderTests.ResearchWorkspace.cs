using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string WorkspaceOne = """{"items":[{"key":"threshold","label":"Pressure threshold","purpose":"comparison","status":"selected","note":"Keep the observed value","evidenceIds":["E1"]}]}""";
    private const string WorkspaceSearch = """{"query":"other observed values","sourceKey":"","category":"","documentHint":"","topK":20}""";

    private static AdvancedAnalysisOptions WorkspaceOptions(string protocol = "chat-completions")
    {
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.NativeResearchTopology = "agent"; options.NativeResearchApiProtocol = protocol;
        options.NativeResearchWorkspaceEnabled = true; options.ExternalMaximumCallsPerJob = 6;
        return options;
    }

    private static JsonElement WorkspaceUser(string request)
    {
        using var document = JsonDocument.Parse(request);
        using var user = JsonDocument.Parse(document.RootElement.GetProperty(document.RootElement.TryGetProperty("input", out _) ? "input" : "messages")
            .EnumerateArray().Single(m => m.TryGetProperty("role", out var role) && role.GetString() == "user").GetProperty("content").GetString()!);
        return user.RootElement.Clone();
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_workspace_save_alone_consumes_a_model_call_without_documentary_io(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("save_research_state", WorkspaceOne) : NativeCompletion(("save_research_state", WorkspaceOne)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal(3, result.ProviderCallCount); Assert.Single(gateway.Searches); Assert.Equal("answered", result.Outcome);
        var prompt = WorkspaceUser(factory.Requests[2].Body);
        Assert.Equal("Pressure threshold", prompt.GetProperty("researchWorkspace").GetProperty("items")[0].GetProperty("label").GetString());
        using var request = JsonDocument.Parse(factory.Requests[2].Body);
        var messages = request.RootElement.GetProperty(protocol == "responses" ? "input" : "messages");
        var linked = messages.EnumerateArray().Single(m => protocol == "responses"
            ? m.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            : m.TryGetProperty("role", out var role) && role.GetString() == "tool");
        using var output = JsonDocument.Parse(linked.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        Assert.Equal("stored", output.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("Le seuil est 17 bar.", linked.GetRawText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Workspace_and_research_in_either_native_order_preserve_call_result_associations(bool saveFirst)
    {
        var calls = saveFirst ? new[] { ("save_research_state", WorkspaceOne), ("search_corpus", WorkspaceSearch) }
            : new[] { ("search_corpus", WorkspaceSearch), ("save_research_state", WorkspaceOne) };
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(calls), Completion(NativeAnswered));
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal(2, gateway.Searches.Count); Assert.Equal("other observed values", gateway.Searches[1].Query);
        using var next = JsonDocument.Parse(factory.Requests[2].Body);
        var outputs = next.RootElement.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").ToArray();
        for (var i = 0; i < outputs.Length; i++)
        {
            Assert.Equal("call_" + i, outputs[i].GetProperty("tool_call_id").GetString());
            using var output = JsonDocument.Parse(outputs[i].GetProperty("content").GetString()!);
            Assert.Equal(calls[i].Item1, output.RootElement.GetProperty("operation").GetString());
            Assert.Equal(calls[i].Item1 == "save_research_state" ? "stored" : "executed", output.RootElement.GetProperty("status").GetString());
        }
    }

    [Theory]
    [InlineData("invisible")]
    [InlineData("duplicate_key")]
    [InlineData("duplicate_property")]
    [InlineData("long_label")]
    [InlineData("invalid_status")]
    [InlineData("empty_references")]
    [InlineData("duplicate_references")]
    [InlineData("too_many_items")]
    [InlineData("serialized_size")]
    public async Task Invalid_workspace_in_a_mixed_batch_rejects_every_documentary_operation(string mutation)
    {
        var state = JsonNode.Parse(WorkspaceOne)!.AsObject(); var items = state["items"]!.AsArray();
        var item = items[0]!.AsObject();
        switch (mutation)
        {
            case "invisible": item["evidenceIds"] = new JsonArray("E-HIDDEN"); break;
            case "duplicate_key": items.Add(item.DeepClone()); break;
            case "long_label": item["label"] = new string('x', 201); break;
            case "invalid_status": item["status"] = "approved_fact"; break;
            case "empty_references": item["evidenceIds"] = new JsonArray(); break;
            case "duplicate_references": item["evidenceIds"] = new JsonArray("E1", "E1"); break;
            case "too_many_items": for (var i=1;i<33;i++) { var copy=item.DeepClone();copy["key"]="key"+i;items.Add(copy); } break;
            case "serialized_size": item["label"]=new string('x',200);item["note"]=new string('x',160);
                for(var i=1;i<32;i++){var copy=item.DeepClone();copy["key"]="key"+i;items.Add(copy);} break;
        }
        var arguments = state.ToJsonString();
        if (mutation == "duplicate_property") arguments = arguments.Replace("\"label\":", "\"label\":\"duplicate\",\"label\":");
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("read_source", """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":4,"topK":20}"""), ("save_research_state", arguments)));
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_native_tool_protocol_invalid", error.ErrorCode);
        Assert.Single(gateway.Searches); Assert.Equal(2, factory.Requests.Count);
    }

    [Theory]
    [InlineData("selected", "E1")]
    [InlineData("rejected", "E2")]
    public async Task Model_selected_workspace_reprojects_canonical_content_within_the_existing_evidence_budget(string status, string finalId)
    {
        var original = BuildEvidence("E1", "Le seuil est 17 bar. Canonical original body.");
        var newcomers = Enumerable.Range(2, 5).Select(i => BuildEvidence("E"+i, "Le seuil est 17 bar. " + new string('x', 2300))).ToArray();
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("save_research_state", WorkspaceOne.Replace("selected", status)), ("search_corpus", WorkspaceSearch)),
            Completion(NativeAnswered.Replace("E1", finalId)));
        var options=WorkspaceOptions(); options.MaximumEvidencePromptCharacters=8000;
        var gateway = new SequencedToolGateway([original], newcomers);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(6, gateway.Evidence.Count);
        var projected=WorkspaceUser(factory.Requests[2].Body).GetProperty("evidence");
        Assert.Equal(status, WorkspaceUser(factory.Requests[2].Body).GetProperty("researchWorkspace").GetProperty("items")[0].GetProperty("status").GetString());
        Assert.True(projected.GetRawText().Length<=8000);
        Assert.Equal(finalId, projected[0].GetProperty("evidenceId").GetString());
        if (status=="selected") Assert.Equal(original.Content, projected[0].GetProperty("content").GetString());
        else Assert.DoesNotContain(projected.EnumerateArray(), e=>e.GetProperty("evidenceId").GetString()=="E1");
    }

    [Fact]
    public async Task Workspace_never_leaks_to_the_next_job_on_the_same_provider_instance()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("save_research_state", WorkspaceOne)), Completion(NativeAnswered),
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        var provider=new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null);
        await provider.ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1","Le seuil est 17 bar.")), CancellationToken.None);
        await provider.ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1","Le seuil est 17 bar.")), CancellationToken.None);
        Assert.Empty(WorkspaceUser(factory.Requests[4].Body).GetProperty("researchWorkspace").GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Disabled_workspace_rejects_state_saves_before_documentary_io()
    {
        using var factory=new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("save_research_state",WorkspaceOne)));
        var options=WorkspaceOptions();options.NativeResearchWorkspaceEnabled=false;
        var gateway=new RecordingToolGateway(BuildEvidence("E1","Le seuil est 17 bar."));
        await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(()=>new OpenAiCompatibleAdvancedAnalysisProvider(factory,options,null)
            .ExecuteAsync(BuildDirectRequest(),gateway,CancellationToken.None));
        Assert.Single(gateway.Searches);Assert.False(WorkspaceUser(factory.Requests[1].Body).TryGetProperty("researchWorkspace",out _));
    }

    [Fact]
    public async Task Rejected_read_batch_does_not_commit_workspace_before_argument_recovery()
    {
        using var factory=new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("save_research_state",WorkspaceOne),("read_source","""{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":5,"topK":20}""")),
            NativeCompletion(("read_source","""{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":4,"topK":20}""")),Completion(NativeAnswered));
        var gateway=new RecordingToolGateway(BuildEvidence("E1","Le seuil est 17 bar."));
        var result=await new OpenAiCompatibleAdvancedAnalysisProvider(factory,WorkspaceOptions(),null)
            .ExecuteAsync(BuildDirectRequest(),gateway,CancellationToken.None);
        Assert.Equal("answered",result.Outcome);Assert.Equal(4,result.ProviderCallCount);
        Assert.Equal(2,gateway.Searches.Count);Assert.Equal(4,gateway.Searches[1].PageEnd);
        Assert.Empty(WorkspaceUser(factory.Requests[2].Body).GetProperty("researchWorkspace").GetProperty("items").EnumerateArray());
        Assert.Empty(WorkspaceUser(factory.Requests[3].Body).GetProperty("researchWorkspace").GetProperty("items").EnumerateArray());
        using var recovery=JsonDocument.Parse(factory.Requests[2].Body);
        foreach(var tool in recovery.RootElement.GetProperty("messages").EnumerateArray().Where(m=>m.GetProperty("role").GetString()=="tool"))
        {
            using var output=JsonDocument.Parse(tool.GetProperty("content").GetString()!);
            Assert.Equal("batch_rejected_before_execution",output.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Empty_corpus_stops_before_declaring_workspace_functions()
    {
        using var factory=new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""));
        var result=await new OpenAiCompatibleAdvancedAnalysisProvider(factory,WorkspaceOptions(),null)
            .ExecuteAsync(BuildDirectRequest(),new RecordingToolGateway(),CancellationToken.None);
        Assert.Equal("insufficient_documentation",result.Outcome);
        Assert.Single(factory.Requests);
        using var request=JsonDocument.Parse(factory.Requests[0].Body);
        Assert.False(request.RootElement.TryGetProperty("tools",out _));
    }
}
