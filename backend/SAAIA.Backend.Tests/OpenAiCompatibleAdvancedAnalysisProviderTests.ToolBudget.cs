using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", "excess")]
    [InlineData("chat-completions", "excess")]
    [InlineData("responses", "zero")]
    [InlineData("chat-completions", "zero")]
    [InlineData("responses", "exact")]
    [InlineData("chat-completions", "exact")]
    [InlineData("responses", "time")]
    [InlineData("chat-completions", "time")]
    public async Task Documentary_budget_admits_complete_batches_or_returns_model_feedback(
        string protocol, string scenario)
    {
        const string read = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":12}""";
        const string next = """{"sourceKey":"internal-source-1","pageStart":2,"pageEnd":2,"topK":12}""";
        var responses = new List<HttpResponseMessage>
        {
            Completion("""{"queries":[{"query":"overview","topK":12}]}""")
        };
        if (scenario is "excess" or "exact")
            responses.Add(protocol == "responses"
                ? scenario == "excess" ? BudgetNativeResponseFunctions(("read_source", read), ("read_source", next))
                    : NativeResponseFunction("read_source", read)
                : scenario == "excess" ? NativeCompletion(("read_source", read), ("read_source", next))
                    : NativeCompletion(("read_source", read)));
        if (scenario == "excess")
            responses.Add(protocol == "responses" ? NativeResponseFunction("read_source", read)
                : NativeCompletion(("read_source", read)));
        responses.Add(protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        responses.Add(protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var options = WorkspaceOptions(protocol);
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 6;
        var gateway = new BudgetedToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."),
            scenario == "zero" ? 1 : 2, scenario == "time" ? 1 : 1000);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(scenario is "zero" or "time" ? 1 : 2, gateway.Searches.Count);
        Assert.Equal(scenario == "excess" ? 5 : scenario == "exact" ? 4 : 3,
            result.ProviderCallCount);
        using var first = JsonDocument.Parse(factory.Requests[1].Body);
        var inputName = protocol == "responses" ? "input" : "messages";
        using var prompt = JsonDocument.Parse(first.RootElement.GetProperty(inputName)
            .EnumerateArray().First(m => m.GetProperty("role").GetString() == "user")
            .GetProperty("content").GetString()!);
        var research = prompt.RootElement.GetProperty("researchTools");
        var budget = research.GetProperty("documentaryBudget");
        Assert.Equal(1, budget.GetProperty("consumedCalls").GetInt32());
        Assert.Equal(scenario == "zero" ? 0 : 1, budget.GetProperty("remainingCalls").GetInt32());
        Assert.Equal(scenario is "excess" or "exact", research.GetProperty("researchAllowed").GetBoolean());
        if (scenario == "excess")
        {
            Assert.Contains("research_tool_call_budget_exceeded", factory.Requests[2].Body,
                StringComparison.Ordinal);
            Assert.Contains("research_batch_rejected_before_execution", factory.Requests[2].Body,
                StringComparison.Ordinal);
        }
        if (scenario == "time")
            Assert.Equal(0, budget.GetProperty("remainingElapsedMilliseconds").GetInt64());
        Assert.Equal("E1", Assert.Single(result.Claims).EvidenceIds[0]);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Documentary_budget_failure_has_no_partial_io_or_unfunded_correction(string protocol)
    {
        const string read = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":12}""";
        const string next = """{"sourceKey":"internal-source-1","pageStart":2,"pageEnd":2,"topK":12}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? BudgetNativeResponseFunctions(("read_source", read), ("read_source", next))
                : NativeCompletion(("read_source", read), ("read_source", next)));
        var options = WorkspaceOptions(protocol);
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 3;
        var gateway = new BudgetedToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."), 2, 1000);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_synthesis_research_tool_budget_exceeded", error.ErrorCode);
        Assert.Single(gateway.Searches);
        Assert.Equal(2, factory.Requests.Count);
    }

    private static HttpResponseMessage BudgetNativeResponseFunctions(
        params (string Name, string Arguments)[] calls) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            id = "response-budget-batch", status = "completed", model = "Qwen3-4B",
            output = calls.Select((call, i) => new
            {
                type = "function_call", id = "function-budget-" + i,
                call_id = "call-budget-" + i, name = call.Name, arguments = call.Arguments
            }),
            usage = new { input_tokens = 10, output_tokens = 10 }
        }))
    };

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Initial_documentary_batch_over_budget_has_no_partial_io(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""
            {"selectionMode":"distinct_named_items","queries":[{"query":"first procedure","topK":12},{"query":"second procedure","topK":12}]}
            """));
        var gateway = new BudgetedToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."), 1, 1000);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
                .ExecuteAsync(BuildRequest(answerUnitCount: 2), gateway, CancellationToken.None));
        Assert.Equal("advanced_synthesis_research_tool_budget_exceeded", error.ErrorCode);
        Assert.Empty(gateway.Searches);
        Assert.Single(factory.Requests);
    }

    private sealed class BudgetedToolGateway(
        AdvancedAnalysisResolvedEvidence item, int maximumCalls, long maximumElapsed) : IAdvancedAnalysisToolGateway
    {
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => Searches.Count == 0 ? [] : [item];
        public AdvancedAnalysisToolBudget Budget => new(maximumCalls, Searches.Count, maximumElapsed, Searches.Count);

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Budget.RemainingCalls == 0 || Budget.RemainingElapsedMilliseconds == 0)
                throw new AdvancedAnalysisToolException("unexpected_unadmitted_test_io");
            Searches.Add(request);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [item], [], 1, Searches.Count));
        }
    }
}
