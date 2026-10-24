using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string ProtocolAgnosticFixtureHeader = "X-SAAIA-Test-Protocol-Agnostic";

    private static async Task<HttpResponseMessage> AdaptProtocolAgnosticFixtureAsync(
        HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Completion(string) describes a business result, not a wire envelope.
        // Explicit HTTP fixtures remain untouched, including every malformed,
        // truncated or rejected response used by transport regression tests.
        if (request.RequestUri?.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) != true
            || !response.Headers.Contains(ProtocolAgnosticFixtureHeader)
            || response.Content is not JsonContent)
            return response;
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        var message = root["choices"]![0]!["message"]!;
        var converted = new JsonObject
        {
            ["status"] = "completed",
            ["model"] = root["model"]?.DeepClone(),
            ["output"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "assistant", ["status"] = "completed",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "output_text", ["text"] = message["content"]!.DeepClone()
                })
            })
        };
        if (root["usage"] is JsonObject usage)
            converted["usage"] = new JsonObject
            {
                ["input_tokens"] = usage["prompt_tokens"]?.DeepClone(),
                ["output_tokens"] = usage["completion_tokens"]?.DeepClone(),
                ["input_tokens_details"] = usage["prompt_tokens_details"]?.DeepClone()
            };
        var result = new HttpResponseMessage(response.StatusCode) { Content = JsonContent.Create(converted) };
        foreach (var header in response.Headers.Where(header => header.Key != ProtocolAgnosticFixtureHeader))
            result.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Dispose();
        return result;
    }
}
