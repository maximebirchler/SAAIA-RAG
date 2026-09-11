using System.Text.Json.Nodes;

namespace SAAIA.Client.ToolAgent.Tests;

public static partial class ThreeFormatQwenContractHarness
{
    // Experimental b10098 transport contract, qualified mechanically by A668.
    // The prefix contains no function name, action, argument or documentary fact.
    public static void ApplyNativeToolPrefix(JsonObject body)
    {
        Require(body["tools"] is JsonArray { Count: > 0 }
            && body["tool_choice"]?.ToString() == "required"
            && body["parallel_tool_calls"]?.ToString() == "false", "native_prefix_tools_contract");
        Require(!body.ContainsKey("response_format") && !body.ContainsKey("grammar")
            && !body.ContainsKey("continue_final_message") && !body.ContainsKey("add_generation_prompt"), "native_prefix_conflicting_format");
        Require(body["messages"] is JsonArray { Count: 2 } messages
            && messages[0]?["role"]?.ToString() == "system"
            && messages[1]?["role"]?.ToString() == "user", "native_prefix_message_contract");
        body["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = "<tool_call>\n" });
        body["continue_final_message"] = "content";
        body["add_generation_prompt"] = false;
    }
}
