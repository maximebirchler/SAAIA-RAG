using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class StructuredFamilyClassifierTests
{
    [Theory]
    [InlineData("strict", true)]
    [InlineData(" STRICT ", true)]
    [InlineData("auto", false)]
    [InlineData("standard", false)]
    public async Task Structured_capability_classifies_strict_mode_then_uses_the_existing_second_stage(string mode, bool expectedStructured)
    {
        var llm = new RecordingRouter();
        var orchestrator = new ToolAgentOrchestrator(new ApiClient(), llm, new ToolMemory(), new AppSettings { ActiveMode = mode });
        var plan = await orchestrator.RouteOnlyForTests("Bonjour, comment vas-tu ?", CancellationToken.None);
        Assert.Equal("chat.general", plan.Intent);
        Assert.Equal(expectedStructured ? 1 : 0, llm.StructuredCalls);
        Assert.Equal(expectedStructured ? 1 : 2, llm.NativeCalls);
        Assert.Equal(new[] { "submit_operational_route", "request_user_clarification" }, llm.LastToolNames);
        if (expectedStructured)
        {
            Assert.Equal("saaia_work_family_v1", llm.Contract!.Name);
            var properties = llm.Contract.Schema.GetProperty("properties");
            Assert.Single(properties.EnumerateObject());
            Assert.Equal(new[] { "answer", "overview", "grid", "operation" },
                properties.GetProperty("family").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
            Assert.False(llm.Terminal);
            Assert.Equal(64, llm.MaximumOutput);
        }
    }

    [Theory]
    [InlineData("{\"family\":\"answer\"}", "stop", "submit_source_backed_route")]
    [InlineData("{\"family\":\"overview\"}", "stop", "submit_document_overview_route")]
    [InlineData("{\"family\":\"grid\"}", "stop", "submit_source_backed_grid_route")]
    [InlineData("{\"family\":\"operation\"}", "stop", "submit_operational_route")]
    [InlineData("{\"family\":\"answer\"}", "length", "")]
    [InlineData("{\"family\":\"answer\"", "length", "")]
    [InlineData("{\"family\":\"unknown\"}", "stop", "")]
    [InlineData("{\"family\":\"answer\",\"query\":\"invented\"}", "stop", "")]
    [InlineData("{\"family\":\"answer\",\"family\":\"operation\"}", "stop", "")]
    [InlineData("Here is the result: {\"family\":\"answer\"}", "stop", "")]
    public void Only_a_complete_unambiguous_family_is_mapped_to_a_route(string json, string finish, string expected)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod("TryResolveStructuredRouterFamily", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] args = [new SourceBackedAgentCompletion(json, [], finish), null];
        var accepted = (bool)method.Invoke(null, args)!;
        Assert.Equal(expected.Length > 0, accepted);
        Assert.Equal(expected, args[1]);
    }

    [Fact]
    public async Task Invalid_structured_family_leaves_all_existing_route_families_available()
    {
        var llm = new RecordingRouter { FamilyJson = "{\"family\":\"answer\",\"unexpected\":true}" };
        var orchestrator = new ToolAgentOrchestrator(new ApiClient(), llm, new ToolMemory(), new AppSettings { ActiveMode = "strict" });
        var plan = await orchestrator.RouteOnlyForTests("Bonjour.", CancellationToken.None);
        Assert.Equal(1, llm.StructuredCalls);
        Assert.Equal("chat.general", plan.Intent);
        Assert.Equal(6, llm.LastToolNames.Count);
        Assert.Contains("submit_source_backed_route", llm.LastToolNames);
        Assert.Contains("request_missing_user_input", llm.LastToolNames);
    }

    private sealed class RecordingRouter : ILlmClient, ISourceBackedAgentLlmClient, ISourceBackedAgentStructuredLlmClient
    {
        public string FamilyJson { get; init; } = "{\"family\":\"operation\"}";
        public int NativeCalls { get; private set; }
        public int StructuredCalls { get; private set; }
        public int MaximumOutput { get; private set; }
        public bool Terminal { get; private set; }
        public LlmStructuredOutputContract? Contract { get; private set; }
        public IReadOnlyList<string> LastToolNames { get; private set; } = [];
        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract, int maxTokens, CancellationToken ct, double? temperatureOverride = null)
        {
            StructuredCalls++;
            Assert.Equal(1, StructuredCalls);
            Assert.Contains(messages, m => m.Role == "system" && m.Content!.Contains("Active mode: strict", StringComparison.Ordinal));
            Assert.Contains(messages, m => m.Role == "user" && m.Content!.Contains("Bonjour", StringComparison.Ordinal));
            Contract = contract; MaximumOutput = maxTokens;
            Terminal = SourceBackedLlmCumulativeBudgetContext.IsTerminalStructuredCall(contract.Name);
            return Task.FromResult(new SourceBackedAgentCompletion(FamilyJson, [], "stop", 279, 10));
        }
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false)
        {
            NativeCalls++;
            Assert.True(NativeCalls <= 2);
            LastToolNames = tools.Select(t => t.Name).ToArray();
            var args = NativeCalls == 1 && StructuredCalls == 0 ? "{}" : "{\"intent\":\"chat.general\",\"toolName\":\"chat.general\",\"toolArgs\":{}}";
            return Task.FromResult(new SourceBackedAgentCompletion("", [new SourceBackedAgentToolCall("route", "submit_operational_route", JsonDocument.Parse(args).RootElement.Clone())], "tool_calls"));
        }
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct) => throw new NotSupportedException();
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct) => throw new NotSupportedException();
    }
}
