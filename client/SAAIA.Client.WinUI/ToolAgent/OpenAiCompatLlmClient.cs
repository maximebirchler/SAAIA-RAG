using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed class OpenAiCompatLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private const int JsonMaxTokens = 1600;
    private const int DefaultAnswerMaxTokens = 1000;
    private const int StructuredAnswerMaxTokens = 1800;
    private const int BroadSourceBackedAnswerMaxTokens = 4096;
    private const int SummaryAnswerMaxTokens = 1400;

    public bool SupportsStructuredOutput => true;

    public OpenAiCompatLlmClient(HttpClient http, string baseUrl, string model)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
    }

    public async Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
    {
        var maxTokens = EstimateMaxTokens(messages, forceJson);
        var modelVisibleMessages = OpenAiChatMessageNormalizer.MergeSystemMessages(
            SourceBackedLlmPromptSanitizer.RemoveControlMetadata(messages));
        var payload = BuildPayload(modelVisibleMessages, forceJson, structuredOutput: null, maxTokensOverride: maxTokens);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            if (forceJson && (resp.StatusCode == HttpStatusCode.BadRequest || (int)resp.StatusCode == 422))
            {
                var payloadRetry = BuildPayload(modelVisibleMessages, forceJson: false, structuredOutput: null, maxTokensOverride: maxTokens);
                using var req2 = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payloadRetry), Encoding.UTF8, "application/json")
                };

                using var resp2 = await _http.SendAsync(req2, ct).ConfigureAwait(false);
                await EnsureSuccessWithBodyAsync(resp2, ct).ConfigureAwait(false);
                var json2 = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ExtractContent(json2);
            }

            await EnsureSuccessWithBodyAsync(resp, ct).ConfigureAwait(false);
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ExtractContent(json);
    }

    public async Task<string> CompleteStructuredAsync(
        IReadOnlyList<(string role, string content)> messages,
        LlmStructuredOutputContract contract,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var maxTokens = EstimateMaxTokens(messages, forceJson: true);
        var modelVisibleMessages = OpenAiChatMessageNormalizer.MergeSystemMessages(
            SourceBackedLlmPromptSanitizer.RemoveControlMetadata(messages));
        var response = await SendStructuredRequestAsync(
                modelVisibleMessages,
                contract,
                maxTokens,
                useLegacyLlamaFormat: false,
                ct)
            .ConfigureAwait(false);
        if (IsStructuredFormatRejection(response))
        {
            response.Dispose();
            response = await SendStructuredRequestAsync(
                    modelVisibleMessages,
                    contract,
                    maxTokens,
                    useLegacyLlamaFormat: true,
                    ct)
                .ConfigureAwait(false);
        }

        if (IsStructuredFormatRejection(response))
        {
            response.Dispose();
            var fallback = BuildPayload(
                modelVisibleMessages,
                forceJson: true,
                structuredOutput: null,
                maxTokensOverride: maxTokens,
                deterministicSampling: true);
            using var fallbackRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(fallback), Encoding.UTF8, "application/json")
            };
            response = await _http.SendAsync(fallbackRequest, ct).ConfigureAwait(false);
        }

        using (response)
        {
            await EnsureSuccessWithBodyAsync(response, ct).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ExtractContent(json);
        }
    }

    public async Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
    {
        if (onDelta is null)
            return;

        var maxTokens = EstimateMaxTokens(messages, forceJson);
        var modelVisibleMessages = OpenAiChatMessageNormalizer.MergeSystemMessages(
            SourceBackedLlmPromptSanitizer.RemoveControlMetadata(messages));
        try
        {
            if (await TryStreamInternalAsync(modelVisibleMessages, forceJson, maxTokens, onDelta, ct).ConfigureAwait(false))
                return;
        }
        catch when (!ct.IsCancellationRequested)
        {
            // fallback below
        }

        if (forceJson)
        {
            try
            {
                if (await TryStreamInternalAsync(modelVisibleMessages, forceJson: false, maxTokens, onDelta, ct).ConfigureAwait(false))
                    return;
            }
            catch when (!ct.IsCancellationRequested)
            {
                // fallback below
            }
        }

        var full = await CompleteAsync(modelVisibleMessages, forceJson, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(full))
            return;

        const int chunkSize = 48;
        for (var i = 0; i < full.Length; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var len = Math.Min(chunkSize, full.Length - i);
            var chunk = full.Substring(i, len);
            if (chunk.Length > 0)
                onDelta(chunk);
            await Task.Yield();
        }
    }

    private async Task<bool> TryStreamInternalAsync(
        IReadOnlyList<(string role, string content)> messages,
        bool forceJson,
        int maxTokens,
        Action<string> onDelta,
        CancellationToken ct)
    {
        var payload = BuildPayload(messages, forceJson, structuredOutput: null, stream: true, maxTokensOverride: maxTokens);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if (forceJson && (resp.StatusCode == HttpStatusCode.BadRequest || (int)resp.StatusCode == 422))
                return false;

            await EnsureSuccessWithBodyAsync(resp, ct).ConfigureAwait(false);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var streamedAny = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line.Substring(5).Trim();
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                break;

            var delta = TryExtractStreamDelta(data);
            if (string.IsNullOrEmpty(delta))
                continue;

            streamedAny = true;
            onDelta(delta);
        }

        return streamedAny;
    }

    private Dictionary<string, object?> BuildPayload(
        IReadOnlyList<(string role, string content)> messages,
        bool forceJson,
        LlmStructuredOutputContract? structuredOutput,
        bool stream = false,
        int? maxTokensOverride = null,
        bool useLegacyLlamaStructuredFormat = false,
        bool deterministicSampling = false)
    {
        deterministicSampling |= structuredOutput is not null;
        var sampling = LlmSamplingConfiguration.ResolveFromEnvironment();
        var dict = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["stream"] = stream,
            ["max_tokens"] = maxTokensOverride ?? EstimateMaxTokens(messages, forceJson),
            ["messages"] = messages.Select(m => new Dictionary<string, string>
            {
                ["role"] = m.role,
                ["content"] = m.content
            }).ToArray()
        };
        sampling.Apply(dict, requestedTemperature: 0.2d, structuredOutput: deterministicSampling);

        if (structuredOutput is not null)
        {
            dict["response_format"] = useLegacyLlamaStructuredFormat
                ? BuildLegacyLlamaStructuredResponseFormat(structuredOutput)
                : BuildOpenAiStructuredResponseFormat(structuredOutput);
        }
        else if (forceJson)
            dict["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };
        else
            dict["stop"] = new[] { "\nUSER_MESSAGE:", "\nTOOL_RESULTS", "\nDRAFT_ANSWER:", "\nCHAT_TAIL:" };

        return dict;
    }

    private async Task<HttpResponseMessage> SendStructuredRequestAsync(
        IReadOnlyList<(string role, string content)> messages,
        LlmStructuredOutputContract contract,
        int maxTokens,
        bool useLegacyLlamaFormat,
        CancellationToken ct)
    {
        var payload = BuildPayload(
            messages,
            forceJson: true,
            structuredOutput: contract,
            maxTokensOverride: maxTokens,
            useLegacyLlamaStructuredFormat: useLegacyLlamaFormat,
            deterministicSampling: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static bool IsStructuredFormatRejection(HttpResponseMessage response)
        => response.StatusCode == HttpStatusCode.BadRequest || (int)response.StatusCode == 422;

    private static Dictionary<string, object?> BuildOpenAiStructuredResponseFormat(
        LlmStructuredOutputContract contract)
        => new()
        {
            ["type"] = "json_schema",
            ["json_schema"] = new Dictionary<string, object?>
            {
                ["name"] = contract.Name,
                ["strict"] = true,
                ["schema"] = contract.Schema
            }
        };

    private static Dictionary<string, object?> BuildLegacyLlamaStructuredResponseFormat(
        LlmStructuredOutputContract contract)
        => new()
        {
            ["type"] = "json_object",
            ["schema"] = contract.Schema
        };

    private static int EstimateMaxTokens(IReadOnlyList<(string role, string content)> messages, bool forceJson)
    {
        var joined = string.Join('\n', messages.Select(m => m.content ?? string.Empty));
        if (forceJson)
        {
            var sourceBackedBudget = SourceBackedLlmOutputBudget.TryResolveJsonMaxTokens(joined, JsonMaxTokens);
            if (sourceBackedBudget is not null)
                return sourceBackedBudget.Value;

            return JsonMaxTokens;
        }

        if (LooksLikeBroadSourceBackedWriterPrompt(joined))
            return BroadSourceBackedAnswerMaxTokens;

        if (LooksLikeStructuredWriterPrompt(joined))
            return StructuredAnswerMaxTokens;

        if (LooksLikeSummaryPrompt(joined))
            return SummaryAnswerMaxTokens;

        return DefaultAnswerMaxTokens;
    }

    private static bool LooksLikeBroadSourceBackedWriterPrompt(string prompt)
        => prompt.Contains("PRIVATE_SOURCE_WRITING_BRIEF", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("PRIVATE_SOURCE_COVERAGE_NOTE", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("PRIVATE_SOURCE_EVIDENCE_INVENTORY", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("PRIVATE_SOURCE_RESEARCH_MAP", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("PRIVATE_SOURCE_REFERENCE_INDEX", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("REQUESTED_STRUCTURE", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeStructuredWriterPrompt(string prompt)
        => prompt.Contains("TOOL_RESULTS", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("rag.multi_search", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("rag.search", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("Do not dump raw excerpts", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSummaryPrompt(string prompt)
        => prompt.Contains("SOURCE_SUMMARY:", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("Translate the stored summary", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("summary", StringComparison.OrdinalIgnoreCase);

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
        }

        var message = string.IsNullOrWhiteSpace(body)
            ? $"LLM request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
            : $"LLM request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {body}";
        throw new HttpRequestException(message, null, response.StatusCode);
    }


    private static string? TryExtractStreamDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        return c.GetString();
                }
                else if (delta.ValueKind == JsonValueKind.String)
                {
                    return delta.GetString();
                }
            }

            if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString();
            }

            if (choice.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString();
        }
        catch
        {
        }

        return null;
    }

    private static string ExtractContent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }
}
