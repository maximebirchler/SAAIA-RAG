using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public async Task Evidence_read_window_survives_checkpoint_resume_without_repeating_the_tool_call()
    {
        var canonical = BuildEvidence("E1", new string('x', 3_000) + "\nLe seuil est 17 bar.\n" + new string('y', 500));
        var gateway = new CheckpointToolGateway(canonical);
        using (var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("read_evidence", """{"evidenceId":"E1","offset":3000,"length":512}""")),
            Completion(NativeAnswered)))
        {
            await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        }
        var stored = Assert.Single(gateway.Checkpoint!.EvidenceReadWindows!);
        Assert.Equal("E1", stored.Key);
        Assert.Equal(3_000, stored.Value.Offset);
        Assert.Equal(512, stored.Value.Length);
        using var resumed = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        await new OpenAiCompatibleAdvancedAnalysisProvider(resumed, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        var visible = Assert.Single(WorkspaceUser(resumed.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        Assert.Equal(3_000, visible.GetProperty("contentOffset").GetInt32());
        Assert.Equal(canonical.Content.Substring(3_000, 512), visible.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("unknown", 0, 512)]
    [InlineData("E1", -1, 512)]
    [InlineData("E1", 3_000, 512)]
    [InlineData("E1", 0, 8_001)]
    public async Task Invalid_checkpoint_read_window_is_rejected_before_synthesis(
        string id, int offset, int length)
    {
        var gateway = new CheckpointToolGateway(BuildEvidence("E1", new string('x', 3_000)));
        await gateway.SaveResearchCheckpointAsync(new AdvancedAnalysisResearchCheckpoint(
            AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion, [], new Dictionary<string, string>(),
            EvidenceReadWindows: new Dictionary<string, AdvancedAnalysisEvidenceReadCheckpoint>
            {
                [id] = new(offset, length)
            }), CancellationToken.None);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_research_checkpoint_invalid", error.ErrorCode);
        Assert.Single(factory.Requests);
    }

    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    public async Task Native_evidence_read_exposes_canonical_tail_without_new_retrieval(
        string protocol, bool documentaryBudgetExhausted)
    {
        var canonical = BuildEvidence("E1", new string('x', 3_000) + "\nLe seuil est 17 bar.\n" + new string('y', 500));
        const string read = """{"evidenceId":"E1","offset":3000,"length":512}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_evidence", read)
                : NativeCompletion(("read_evidence", read)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        IAdvancedAnalysisToolGateway gateway = documentaryBudgetExhausted
            ? new BudgetedToolGateway(canonical, 1, 1_000)
            : new RecordingToolGateway(canonical);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Equal("E1", Assert.Single(result.Claims).EvidenceIds.Single());
        if (gateway is RecordingToolGateway recording) Assert.Single(recording.Searches);
        if (gateway is BudgetedToolGateway budgeted) Assert.Single(budgeted.Searches);
        var initial = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        Assert.DoesNotContain("17 bar", initial.GetProperty("content").GetString());
        Assert.Equal(2_400, initial.GetProperty("nextContentOffset").GetInt32());
        var projected = Assert.Single(WorkspaceUser(factory.Requests[2].Body).GetProperty("evidence").EnumerateArray());
        Assert.Equal("E1", projected.GetProperty("evidenceId").GetString());
        Assert.Equal(3_000, projected.GetProperty("contentOffset").GetInt32());
        Assert.Contains("Le seuil est 17 bar.", projected.GetProperty("content").GetString());
        Assert.Equal(canonical.Content.Substring(3_000, 512), projected.GetProperty("content").GetString());
        Assert.Equal(canonical.Content.Length, projected.GetProperty("originalContentLength").GetInt32());
        Assert.True(projected.GetProperty("contentTruncated").GetBoolean());
        Assert.Equal(3_512, projected.GetProperty("nextContentOffset").GetInt32());
        Assert.Same(canonical, Assert.Single(gateway.Evidence));

        using var next = JsonDocument.Parse(factory.Requests[2].Body);
        var toolResult = next.RootElement.GetProperty(protocol == "responses" ? "input" : "messages")
            .EnumerateArray().Single(item => protocol == "responses"
                ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : item.TryGetProperty("role", out var role) && role.GetString() == "tool");
        using var tool = JsonDocument.Parse(toolResult.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        Assert.Equal("projected", tool.RootElement.GetProperty("status").GetString());
        Assert.Equal("read_evidence", tool.RootElement.GetProperty("operation").GetString());
    }

    [Theory]
    [InlineData("{\"evidenceId\":\"unknown\",\"offset\":0,\"length\":512}")]
    [InlineData("{\"evidenceId\":\"E1\",\"offset\":-1,\"length\":512}")]
    [InlineData("{\"evidenceId\":\"E1\",\"offset\":99999,\"length\":512}")]
    [InlineData("{\"evidenceId\":\"E1\",\"offset\":0,\"length\":8001}")]
    [InlineData("{\"evidenceId\":\"E1\",\"offset\":\"0\",\"length\":512}")]
    [InlineData("{\"evidenceId\":\"E1\",\"offset\":0,\"offset\":1,\"length\":512}")]
    public async Task Invalid_evidence_read_rejects_entire_mixed_batch_before_documentary_io(string arguments)
    {
        var canonical = BuildEvidence("E1", new string('x', 3_000));
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("search_corpus", WorkspaceSearch), ("read_evidence", arguments)),
            Completion("""{"outcome":"insufficient_documentation","answerText":"La preuve est insuffisante.","claims":[]}"""));
        var gateway = new RecordingToolGateway(canonical);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Single(gateway.Searches);
        Assert.Equal(3, factory.Requests.Count);
        using var correction = JsonDocument.Parse(factory.Requests[2].Body);
        var outputs = correction.RootElement.GetProperty("messages").EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "tool").ToArray();
        Assert.Equal(2, outputs.Length);
        foreach (var output in outputs)
        {
            using var rejection = JsonDocument.Parse(output.GetProperty("content").GetString()!);
            Assert.Equal("batch_rejected_before_execution", rejection.RootElement.GetProperty("status").GetString());
        }
    }
}
