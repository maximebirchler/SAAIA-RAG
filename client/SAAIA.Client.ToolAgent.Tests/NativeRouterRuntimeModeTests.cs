using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class NativeRouterRuntimeModeTests
{
    [Theory]
    [InlineData("strict", "strict", true)]
    [InlineData("standard", "standard", false)]
    [InlineData("auto", "auto", false)]
    [InlineData(" STRICT ", "strict", true)]
    public async Task Current_mode_reaches_both_router_stages_outside_truncated_history(
        string setting, string normalizedMode, bool corpusRequired)
    {
        var llm = new CapturingRouter();
        var orchestrator = new ToolAgentOrchestrator(new ApiClient(), llm,
            new ToolMemory(), new AppSettings { ActiveMode = setting });
        var history = new List<(string role, string content)>
        {
            ("system", "STALE_MODE_MARKER: prior preference, not the current setting.")
        };
        for (var i = 0; i < 12; i++) history.Add((i % 2 == 0 ? "user" : "assistant", $"history-{i}"));
        const string question = "Bonjour, comment vas-tu ?";
        var method = typeof(ToolAgentOrchestrator).GetMethod("RouterAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<RouterPlan>)method.Invoke(orchestrator,
            new object[] { history, question, CancellationToken.None, false })!;
        var plan = await task;

        Assert.Equal(2, llm.Calls.Count);
        Assert.All(llm.Calls, messages =>
        {
            var system = string.Join("\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
            Assert.Contains("Active mode: " + normalizedMode, system, StringComparison.Ordinal);
            Assert.Equal(corpusRequired, system.Contains("factual questions and recommendations", StringComparison.Ordinal));
            Assert.DoesNotContain("STALE_MODE_MARKER", string.Join("\n", messages.Select(m => m.Content)), StringComparison.Ordinal);
            Assert.Contains(messages, m => m.Role == "user" && m.Content!.Contains(question, StringComparison.Ordinal));
        });
        // The LLM's operational decision is still honored in strict mode.
        Assert.Equal("chat.general", plan.Intent);
    }

    private sealed class CapturingRouter : ILlmClient, ISourceBackedAgentLlmClient
    {
        public List<IReadOnlyList<SourceBackedAgentMessage>> Calls { get; } = new();
        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens,
            CancellationToken ct, double? temperatureOverride = null, bool requireToolCall = false)
        {
            Calls.Add(messages.ToArray());
            Assert.True(Calls.Count <= 2, "Unexpected router repair in this contract test.");
            var args = Calls.Count == 1 ? "{}" : "{\"intent\":\"chat.general\",\"toolName\":\"chat.general\",\"toolArgs\":{}}";
            return Task.FromResult(new SourceBackedAgentCompletion("", new[] {
                new SourceBackedAgentToolCall("route-" + Calls.Count, "submit_operational_route", JsonDocument.Parse(args).RootElement.Clone())
            }, "tool_calls"));
        }
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages,
            bool forceJson, CancellationToken ct) => throw new NotSupportedException();
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages,
            bool forceJson, Action<string> onDelta, CancellationToken ct) => throw new NotSupportedException();
    }
}
