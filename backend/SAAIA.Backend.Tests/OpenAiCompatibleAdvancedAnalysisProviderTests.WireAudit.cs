using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public async Task Wire_audit_responses_protocol_does_not_accept_an_explicit_chat_envelope_for_planner()
    {
        using var factory = new QueuedHttpClientFactory(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                choices = new[] { new { message = new { role = "assistant",
                    content = "{\"queries\":[{\"query\":\"overview\",\"topK\":12}]}" } } }
            })
        });
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions("responses"), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_llm_response_invalid", error.ErrorCode);
        Assert.Empty(gateway.Searches);
        Assert.Equal("/v1/responses", Assert.Single(factory.Requests).Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("malformed_arguments", "advanced_native_tool_protocol_invalid")]
    [InlineData("incomplete_call", "advanced_llm_response_invalid")]
    [InlineData("incomplete_message", "advanced_llm_response_invalid")]
    [InlineData("refusal", "advanced_llm_content_filtered")]
    public async Task Wire_audit_responses_reject_unreplayable_or_nonterminal_output_items(
        string mutation, string expected)
    {
        object item = mutation switch
        {
            "malformed_arguments" => new { type = "function_call", status = "completed", call_id = "call_1",
                name = "read_evidence", arguments = 123 },
            "incomplete_call" => new { type = "function_call", status = "in_progress", call_id = "call_1",
                name = "read_evidence", arguments = "{}" },
            "incomplete_message" => new { type = "message", status = "incomplete", role = "assistant",
                content = new[] { new { type = "output_text", text = NativeAnswered } } },
            _ => new { type = "message", role = "assistant", content = new object[]
                { new { type = "output_text", text = NativeAnswered }, new { type = "refusal", refusal = "Refused." } } }
        };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { status = "completed", output = new[] { item },
                    usage = new { input_tokens = 100, output_tokens = 50 } })
            });
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions("responses"), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal(expected, error.ErrorCode);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Single(gateway.Searches);
    }

    [Theory]
    [InlineData("length", "advanced_llm_output_limit")]
    [InlineData("content_filter", "advanced_llm_content_filtered")]
    public async Task Wire_audit_chat_nonterminal_text_cannot_publish_parseable_json(
        string finishReason, string expected)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[] { new { finish_reason = finishReason,
                        message = new { role = "assistant", content = NativeAnswered } } },
                    usage = new { prompt_tokens = 100, completion_tokens = 50 }
                })
            });
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, CreateOptions(), null)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")),
                    CancellationToken.None));
        Assert.Equal(expected, error.ErrorCode);
        Assert.Equal(2, factory.Requests.Count);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Wire_audit_functions_not_offered_this_turn_cannot_bypass_documentary_budget(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("search_corpus", WorkspaceSearch)
                : NativeCompletion(("search_corpus", WorkspaceSearch)));
        var gateway = new BudgetedToolGateway(BuildEvidence("E1", new string('x', 3_000)), 1, 1_000);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_native_tool_protocol_invalid", error.ErrorCode);
        Assert.Single(gateway.Searches);
        using var request = JsonDocument.Parse(factory.Requests[1].Body);
        var names = request.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => (protocol == "responses" ? tool : tool.GetProperty("function")).GetProperty("name").GetString());
        Assert.Equal(new[] { "read_evidence" }, names);
    }

    [Theory]
    [InlineData("chat-completions", "{\"evidenceId\":\"E1\",\"offset\":")]
    [InlineData("responses", "{\"evidenceId\":\"E1\",\"offset\":")]
    [InlineData("chat-completions", "{\"evidenceId\":\"E1\",\"offset\":99999,\"length\":512}")]
    [InlineData("responses", "{\"evidenceId\":\"E1\",\"offset\":99999,\"length\":512}")]
    public async Task Wire_audit_invalid_arguments_get_linked_feedback_and_a_bounded_correction(
        string protocol, string invalid)
    {
        const string valid = """{"evidenceId":"E1","offset":3000,"length":512}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_evidence", invalid)
                : NativeCompletion(("read_evidence", invalid)),
            protocol == "responses" ? NativeResponseFunction("read_evidence", valid)
                : NativeCompletion(("read_evidence", valid)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var gateway = new RecordingToolGateway(BuildEvidence("E1", new string('x', 3_000) + "Le seuil est 17 bar."));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Single(gateway.Searches);
        Assert.Equal(0, WorkspaceUser(factory.Requests[2].Body).GetProperty("evidence")[0].GetProperty("contentOffset").GetInt32());
        Assert.Equal(3_000, WorkspaceUser(factory.Requests[3].Body).GetProperty("evidence")[0].GetProperty("contentOffset").GetInt32());
        using var correction = JsonDocument.Parse(factory.Requests[2].Body);
        var messages = correction.RootElement.GetProperty(protocol == "responses" ? "input" : "messages");
        var output = messages.EnumerateArray().Single(item => protocol == "responses"
            ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            : item.TryGetProperty("role", out var role) && role.GetString() == "tool");
        Assert.Equal(protocol == "responses" ? "call_1" : "call_0",
            output.GetProperty(protocol == "responses" ? "call_id" : "tool_call_id").GetString());
        using var feedback = JsonDocument.Parse(output.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        Assert.Equal("batch_rejected_before_execution", feedback.RootElement.GetProperty("status").GetString());
        Assert.Equal("read_evidence", feedback.RootElement.GetProperty("operation").GetString());
        if (protocol == "responses")
            Assert.Contains(messages.EnumerateArray(), item => item.TryGetProperty("encrypted_content", out var encrypted)
                && encrypted.GetString() == "synthetic-sealed-reasoning");
    }
}
