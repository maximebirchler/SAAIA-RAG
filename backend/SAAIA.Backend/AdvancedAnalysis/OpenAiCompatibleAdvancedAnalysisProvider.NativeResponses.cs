using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private bool UsesNativeResponses => _options.NativeResearchApiProtocol == "responses";

    private Uri ResolveNativeResponsesUri()
    {
        var baseUrl = _options.LlmBaseUrl.Trim().TrimEnd('/');
        return new Uri(baseUrl + (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/responses" : "/v1/responses"), UriKind.Absolute);
    }

    private static void ConvertNativePayloadToResponses(Dictionary<string, object?> payload)
    {
        payload["input"] = payload["messages"]; payload.Remove("messages");
        payload["max_output_tokens"] = payload.TryGetValue("max_completion_tokens", out var maximum)
            ? maximum : payload["max_tokens"];
        payload.Remove("max_completion_tokens"); payload.Remove("max_tokens");
        payload.Remove("response_format");
        payload["text"] = new { format = new { type = "json_object" } };
        payload["store"] = false;
        payload["include"] = new[] { "reasoning.encrypted_content" };
        if (payload.TryGetValue("reasoning_effort", out var effort))
        {
            payload["reasoning"] = new { effort }; payload.Remove("reasoning_effort");
        }
        if (payload.TryGetValue("tools", out var tools))
        {
            var native = JsonSerializer.SerializeToNode(tools, JsonOptions)!.AsArray();
            payload["tools"] = native.Select(tool =>
            {
                var function = tool!["function"]!.DeepClone().AsObject(); function["type"] = "function";
                return function;
            }).ToArray();
        }
    }

    private static AdvancedAnalysisLlmUsage ReadNativeResponsesUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return new(null, null, null);
        int? Number(JsonElement item, string property) => item.TryGetProperty(property, out var number)
            && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var value) && value >= 0 ? value : null;
        var cached = usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? Number(details, "cached_tokens") : null;
        return new(Number(usage, "input_tokens"), Number(usage, "output_tokens"), cached, 0);
    }

    private static (string? Content, string? CallsJson, string? OutputItemsJson, string Origin, string? Error)
        ReadNativeResponsesCompletion(JsonElement root, string userPrompt, bool toolsAvailable)
    {
        if (ReadString(root, "status") == "incomplete")
            return (null, null, null, "response.output.rejected", "advanced_llm_output_limit");
        if (ReadString(root, "status") != "completed")
            return (null, null, null, "response.output.rejected", "advanced_llm_response_invalid");
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return (null, null, null, "response.output.rejected", "advanced_llm_content_missing");
        var calls = new JsonArray();
        var text = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            var type = ReadString(item, "type");
            if (type == "function_call") calls.Add(JsonSerializer.SerializeToNode(new
            {
                id = ReadString(item, "call_id"), type = "function",
                function = new { name = ReadString(item, "name"), arguments = ReadString(item, "arguments") }
            }, JsonOptions));
            else if (type == "message" && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                text.AddRange(content.EnumerateArray().Where(c => ReadString(c, "type") == "output_text")
                    .Select(c => ReadString(c, "text")));
        }
        if (calls.Count == 0) return (string.Concat(text), null, null, "response.output_text", null);
        if (!toolsAvailable) return (null, null, null, "response.output.rejected", "advanced_native_tool_not_available");
        try
        {
            using var document = JsonDocument.Parse(calls.ToJsonString(JsonOptions));
            return (NormalizeNativeResearchCalls(document.RootElement, userPrompt), calls.ToJsonString(JsonOptions),
                output.GetRawText(), "response.output.function_calls.normalized", null);
        }
        catch (Exception error) when (error is JsonException or AdvancedAnalysisProviderException or InvalidOperationException)
        { return (null, null, null, "response.output.rejected", "advanced_native_tool_protocol_invalid"); }
    }
}
