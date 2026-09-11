using System.Text.Json.Nodes;
using Xunit;
using static SAAIA.Client.ToolAgent.Tests.ThreeFormatQwenContractHarness;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ThreeFormatQwenNativePrefixTests
{
    private static JsonObject Body() => JsonNode.Parse("""
        {"messages":[{"role":"system","content":"SYSTEM"},{"role":"user","content":"IMMUTABLE_EVIDENCE"}],
         "tools":[{"type":"function","function":{"name":"choose","parameters":{"type":"object","properties":{"action":{"enum":["answer","search"]}}}}}],
         "tool_choice":"required","parallel_tool_calls":false,"max_tokens":512,"temperature":0}
        """)!.AsObject();

    [Fact]
    public void Prefix_preserves_all_original_inputs_and_supplies_no_semantic_choice()
    {
        var body = Body();
        var original = body.DeepClone();
        ApplyNativeToolPrefix(body);
        var assistant = body["messages"]!.AsArray()[2]!;
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        Assert.Equal("<tool_call>\n", assistant["content"]!.GetValue<string>());
        Assert.Equal("content", body["continue_final_message"]!.GetValue<string>());
        Assert.False(body["add_generation_prompt"]!.GetValue<bool>());
        body["messages"]!.AsArray().RemoveAt(2);
        body.Remove("continue_final_message");
        body.Remove("add_generation_prompt");
        Assert.True(JsonNode.DeepEquals(original, body));
    }

    [Theory]
    [InlineData("response_format")]
    [InlineData("grammar")]
    [InlineData("continue_final_message")]
    [InlineData("add_generation_prompt")]
    public void Conflicting_formats_are_rejected_before_mutating_the_request(string key)
    {
        var body = Body(); body[key] = "existing";
        var original = body.DeepClone();
        Assert.Throws<InvalidOperationException>(() => ApplyNativeToolPrefix(body));
        Assert.True(JsonNode.DeepEquals(original, body));
    }

    [Fact]
    public void Existing_assistant_content_is_never_replaced_or_silently_appended()
    {
        var body = Body(); body["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = "existing" });
        var original = body.DeepClone();
        Assert.Throws<InvalidOperationException>(() => ApplyNativeToolPrefix(body));
        Assert.True(JsonNode.DeepEquals(original, body));
    }

    [Fact]
    public void Optional_or_parallel_tool_contracts_are_rejected()
    {
        var optional = Body(); optional["tool_choice"] = "auto";
        Assert.Throws<InvalidOperationException>(() => ApplyNativeToolPrefix(optional));
        var parallel = Body(); parallel["parallel_tool_calls"] = true;
        Assert.Throws<InvalidOperationException>(() => ApplyNativeToolPrefix(parallel));
    }
}
