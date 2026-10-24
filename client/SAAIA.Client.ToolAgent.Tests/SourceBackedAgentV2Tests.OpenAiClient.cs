using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Fact]
    public async Task OpenAiClient_SerializesNativeToolHistoryAndParsesToolCalls()
    {
        var requestBodies = new List<string>();
        var handler = new DelegateHandler(async request =>
        {
            requestBodies.Add(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [{
                        "finish_reason": "tool_calls",
                        "message": {
                          "content": null,
                          "tool_calls": [{
                            "id": "call-42",
                            "type": "function",
                            "function": {
                              "name": "rag_search",
                              "arguments": "{\"query\":\"HX-42 torque\"}"
                            }
                          }]
                        }
                      }],
                      "usage": { "prompt_tokens": 120, "completion_tokens": 18 },
                      "timings": {
                        "cache_n": 119,
                        "prompt_n": 1,
                        "prompt_ms": 12.5,
                        "predicted_n": 18,
                        "predicted_ms": 2400.25
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = new OpenAiLlmClient(new HttpClient(handler));
        client.Configure("http://localhost:12662/v1", "qwen3");

        var completion = await client.ChatOnceNativeAsync(
            new[]
            {
                SourceBackedAgentMessage.System("orchestrate"),
                SourceBackedAgentMessage.User("question")
            },
            SourceBackedAgentToolCatalog.Build(),
            temperature: 0.7,
            maxTokens: 900,
            CancellationToken.None);

        Assert.Equal("tool_calls", completion.FinishReason);
        var call = Assert.Single(completion.ToolCalls);
        Assert.Equal("call-42", call.Id);
        Assert.Equal("rag_search", call.Name);
        Assert.Equal("HX-42 torque", call.Arguments.GetProperty("query").GetString());
        Assert.Equal(120, completion.PromptTokens);
        Assert.Equal(119, completion.ServerCacheTokens);
        Assert.Equal(1, completion.ServerPromptTokensEvaluated);
        Assert.Equal(12.5, completion.ServerPromptMilliseconds);
        Assert.Equal(18, completion.ServerPredictedTokens);
        Assert.Equal(2400.25, completion.ServerPredictedMilliseconds);
        Assert.Contains("\"tool_choice\":\"auto\"", requestBodies[0]);
        Assert.Contains("\"parallel_tool_calls\":true", requestBodies[0]);
        Assert.Contains("\"name\":\"documents_navigation\"", requestBodies[0]);

        await client.ChatOnceNativeAsync(
            new[] { SourceBackedAgentMessage.User("audit") },
            new[]
            {
                new SourceBackedAgentToolDefinition(
                    "submit_semantic_review",
                    "Submit the semantic review.",
                    JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new { decision = new { type = "string" } },
                        required = new[] { "decision" }
                    }))
            },
            temperature: 0,
            maxTokens: 128,
            CancellationToken.None,
            requireToolCall: true);

        Assert.Contains(
            "\"tool_choice\":\"required\"",
            requestBodies[1]);
        Assert.Contains("\"parallel_tool_calls\":false", requestBodies[1]);
        using var requiredToolPayload = JsonDocument.Parse(requestBodies[1]);
        Assert.Equal(
            0d,
            requiredToolPayload.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(
            1d,
            requiredToolPayload.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(
            0d,
            requiredToolPayload.RootElement.GetProperty("frequency_penalty").GetDouble());
        Assert.Equal(
            0d,
            requiredToolPayload.RootElement.GetProperty("presence_penalty").GetDouble());
    }
}
