using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;


namespace SAAIA.Client.WinUI.Services;

public sealed class OpenAiLlmClient
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _http;

    public event Action? RuntimeActivityStarted;
    public event Action? RuntimeActivityFinished;

    // Exemple llama.cpp server OpenAI compat : http://127.0.0.1:8080/v1
    private string _baseUrl = "http://127.0.0.1:8080/v1";
    private string _model = "local";

    private static readonly JsonSerializerOptions JsonOpts = ClientJson.CamelCase;

    public event Func<CancellationToken, Task>? RuntimeEnsureReady;
    public OpenAiLlmClient()
        : this(httpClient: null)
    {
    }

    internal OpenAiLlmClient(HttpClient? httpClient)
    {
        _http = httpClient ?? SharedHttpClient;
    }


    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { ClientLog.Warn($"OpenAI-compatible error body unreadable: {ex.Message}"); }

        var msg = $"LLM request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}";
        throw new HttpRequestException(msg, null, resp.StatusCode);
    }

    public void Configure(string baseUrl, string model)
    {
        _baseUrl = baseUrl.Trim().TrimEnd('/');
        _model = model.Trim();
    }

    private async Task EnsureRuntimeReadyAsync(CancellationToken ct)
    {
        var handlers = RuntimeEnsureReady;
        if (handlers is null)
            return;

        foreach (Func<CancellationToken, Task> handler in handlers.GetInvocationList())
            await handler(ct).ConfigureAwait(false);
    }

    public async Task<string> ChatOnceAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        CancellationToken ct,
        bool forceJson = false)
        => await ChatOnceCoreAsync(
                messages,
                temperature,
                maxTokens,
                ct,
                forceJson,
                structuredOutput: null)
            .ConfigureAwait(false);

    public async Task<string> ChatOnceStructuredAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        LlmStructuredOutputContract contract,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return await ChatOnceCoreAsync(
                messages,
                temperature,
                maxTokens,
                ct,
                forceJson: true,
                contract)
            .ConfigureAwait(false);
    }

    public async Task<SourceBackedAgentCompletion> ChatOnceNativeAsync(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        double temperature,
        int maxTokens,
        CancellationToken ct,
        bool requireToolCall = false)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        await EnsureRuntimeReadyAsync(ct).ConfigureAwait(false);
        RuntimeActivityStarted?.Invoke();
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = _model,
                ["max_tokens"] = maxTokens,
                ["stream"] = false,
                ["messages"] = messages.Select(ToNativeToolMessagePayload).ToArray()
            };
            if (tools.Count > 0)
            {
                payload["tools"] = tools.Select(static tool => new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.Parameters
                    }
                }).ToArray();
                payload["tool_choice"] = requireToolCall ? "required" : "auto";
                payload["parallel_tool_calls"] = !requireToolCall;
            }
            LlmSamplingConfiguration.ResolveFromEnvironment().Apply(
                payload,
                temperature,
                structuredOutput: false);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseNativeToolCompletion(json);
        }
        finally
        {
            RuntimeActivityFinished?.Invoke();
        }
    }

    private static Dictionary<string, object?> ToNativeToolMessagePayload(SourceBackedAgentMessage message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = message.Role
        };

        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            payload["content"] = string.IsNullOrEmpty(message.Content) ? null : message.Content;
            if (message.ToolCalls is { Count: > 0 })
            {
                payload["tool_calls"] = message.ToolCalls.Select(static call => new Dictionary<string, object?>
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.GetRawText()
                    }
                }).ToArray();
            }

            return payload;
        }

        payload["content"] = message.Content ?? string.Empty;
        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            payload["tool_call_id"] = message.ToolCallId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(message.Name))
                payload["name"] = message.Name;
        }

        return payload;
    }

    private static SourceBackedAgentCompletion ParseNativeToolCompletion(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement)
                      && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;
        var calls = new List<SourceBackedAgentToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                index++;
                if (!toolCall.TryGetProperty("function", out var function)
                    || function.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = toolCall.TryGetProperty("id", out var idElement)
                         && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;
                var name = function.TryGetProperty("name", out var nameElement)
                           && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                var argumentText = function.TryGetProperty("arguments", out var argumentsElement)
                    ? argumentsElement.ValueKind == JsonValueKind.String
                        ? argumentsElement.GetString()
                        : argumentsElement.GetRawText()
                    : "{}";

                var argumentError = default(string);
                JsonElement arguments;
                try
                {
                    using var argumentDocument = JsonDocument.Parse(
                        string.IsNullOrWhiteSpace(argumentText) ? "{}" : argumentText);
                    arguments = argumentDocument.RootElement.Clone();
                    if (arguments.ValueKind != JsonValueKind.Object)
                    {
                        argumentError = "tool_arguments_must_be_an_object";
                        arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
                    }
                }
                catch (JsonException ex)
                {
                    argumentError = "invalid_tool_arguments_json: " + ex.Message;
                    arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
                }

                calls.Add(new SourceBackedAgentToolCall(
                    string.IsNullOrWhiteSpace(id) ? $"tool-call-{index}" : id!,
                    name?.Trim() ?? string.Empty,
                    arguments,
                    argumentError));
            }
        }

        var finishReason = choice.TryGetProperty("finish_reason", out var finish)
                           && finish.ValueKind == JsonValueKind.String
            ? finish.GetString() ?? string.Empty
            : string.Empty;
        int? promptTokens = null;
        int? completionTokens = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var prompt)
                && prompt.TryGetInt32(out var promptValue))
            {
                promptTokens = promptValue;
            }

            if (usage.TryGetProperty("completion_tokens", out var completion)
                && completion.TryGetInt32(out var completionValue))
            {
                completionTokens = completionValue;
            }
        }

        return new SourceBackedAgentCompletion(
            content,
            calls,
            finishReason,
            promptTokens,
            completionTokens);
    }

    private async Task<string> ChatOnceCoreAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        CancellationToken ct,
        bool forceJson,
        LlmStructuredOutputContract? structuredOutput)
    {
        await EnsureRuntimeReadyAsync(ct).ConfigureAwait(false);
        messages = OpenAiChatMessageNormalizer.MergeSystemMessages(messages);
        RuntimeActivityStarted?.Invoke();
        try
        {
            var resp = await SendChatRequestAsync(
                    messages,
                    temperature,
                    maxTokens,
                    forceJson,
                    structuredOutput,
                    useLegacyLlamaStructuredFormat: false,
                    ct)
                .ConfigureAwait(false);
            if (structuredOutput is not null
                && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422))
            {
                ClientLog.Warn(
                    $"OpenAI-compatible endpoint rejected response_format=json_schema ({(int)resp.StatusCode}); retrying with llama.cpp json_object+schema.");
                resp.Dispose();
                resp = await SendChatRequestAsync(
                        messages,
                        temperature,
                        maxTokens,
                        forceJson: true,
                        structuredOutput,
                        useLegacyLlamaStructuredFormat: true,
                        ct)
                    .ConfigureAwait(false);
            }

            if (structuredOutput is not null
                && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422))
            {
                ClientLog.Warn(
                    $"OpenAI-compatible endpoint rejected legacy schema response format ({(int)resp.StatusCode}); retrying with unconstrained json_object.");
                resp.Dispose();
                resp = await SendChatRequestAsync(
                        messages,
                        temperature,
                        maxTokens,
                        forceJson: true,
                        structuredOutput: null,
                        useLegacyLlamaStructuredFormat: false,
                        ct)
                    .ConfigureAwait(false);
            }
            else if (structuredOutput is null
                     && forceJson
                && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422))
            {
                ClientLog.Warn(
                    $"OpenAI-compatible endpoint rejected response_format=json_object ({(int)resp.StatusCode}); retrying with prompt-only JSON enforcement.");
                resp.Dispose();
                resp = await SendChatRequestAsync(
                        messages,
                        temperature,
                        maxTokens,
                        forceJson: false,
                        structuredOutput: null,
                        useLegacyLlamaStructuredFormat: false,
                        ct)
                    .ConfigureAwait(false);
            }

            using (resp)
            {
                await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                return content ?? "";
            }
        }
        finally
        {
            RuntimeActivityFinished?.Invoke();
        }
    }

    private async Task<HttpResponseMessage> SendChatRequestAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        bool forceJson,
        LlmStructuredOutputContract? structuredOutput,
        bool useLegacyLlamaStructuredFormat,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
            ["stop"] = new[] { "\nUSER_MESSAGE:", "\nTOOL_RESULTS", "\nDRAFT_ANSWER:", "\nCHAT_TAIL:" },
            ["messages"] = messages.Select(m => new { role = m.role, content = m.content }).ToArray()
        };
        LlmSamplingConfiguration.ResolveFromEnvironment().Apply(
            payload,
            temperature,
            structuredOutput is not null);
        if (structuredOutput is not null)
        {
            payload["response_format"] = useLegacyLlamaStructuredFormat
                ? new Dictionary<string, object?>
                {
                    ["type"] = "json_object",
                    ["schema"] = structuredOutput.Schema
                }
                : new Dictionary<string, object?>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object?>
                    {
                        ["name"] = structuredOutput.Name,
                        ["strict"] = true,
                        ["schema"] = structuredOutput.Schema
                    }
                };
        }
        else if (forceJson)
            payload["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    public async Task ChatStreamAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        Action<string> onDelta,
        CancellationToken ct)
    {
        await EnsureRuntimeReadyAsync(ct).ConfigureAwait(false);
        messages = OpenAiChatMessageNormalizer.MergeSystemMessages(messages);
        RuntimeActivityStarted?.Invoke();
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = _model,
                ["max_tokens"] = maxTokens,
                ["stream"] = true,
                ["stop"] = new[] { "\nUSER_MESSAGE:", "\nTOOL_RESULTS", "\nDRAFT_ANSWER:", "\nCHAT_TAIL:" },
                ["messages"] = messages.Select(m => new { role = m.role, content = m.content }).ToArray()
            };
            LlmSamplingConfiguration.ResolveFromEnvironment().Apply(
                payload,
                temperature,
                structuredOutput: false);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";

            // Some OpenAI-compatible servers ignore SSE and return a normal JSON response.
            // Spec requires progressive UX anyway => simulate streaming client-side.
            if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var full = TryExtractChatContent(json);
                if (!string.IsNullOrEmpty(full))
                    await SimulateStreamingAsync(full, onDelta, ct).ConfigureAwait(false);
                return;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            // Robuste: certains serveurs streament du "delta", d'autres du "contenu cumulatif"
            var emittedSoFar = "";
            var sawData = false;

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().WaitAsync(ct);
                if (line is null) break;                // stream fermé
                if (line.Length == 0) continue;         // ligne vide SSE

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                sawData = true;
                var data = line.Substring("data:".Length).Trim();
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    var delta = root.GetProperty("choices")[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var chunk = c.GetString() ?? "";
                        if (chunk.Length == 0) continue;

                        // Si "chunk" est cumulatif et contient déjà ce qu'on a émis, on n'émet que le "diff"
                        if (chunk.StartsWith(emittedSoFar, StringComparison.Ordinal))
                        {
                            var diff = chunk.Substring(emittedSoFar.Length);
                            if (diff.Length > 0)
                            {
                                onDelta(diff);
                                emittedSoFar += diff;
                            }
                        }
                        else
                        {
                            // Sinon, on considère que c'est un vrai delta
                            onDelta(chunk);
                            emittedSoFar += chunk;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    ClientLog.Warn($"OpenAI-compatible SSE chunk ignored: {ex.Message}");
                }
            }

            // Safety net: if server claimed SSE but never sent data lines, fall back to JSON parsing and simulate.
            if (!sawData || string.IsNullOrEmpty(emittedSoFar))
            {
                try
                {
                    // NOTE: at this point the stream might be consumed; we can only do best-effort by reading the remaining buffer.
                    var remaining = await reader.ReadToEndAsync().WaitAsync(ct);
                    var full = TryExtractChatContent(remaining);
                    if (!string.IsNullOrEmpty(full))
                        await SimulateStreamingAsync(full, onDelta, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ClientLog.Warn($"OpenAI-compatible SSE fallback ignored: {ex.Message}");
                }
            }
        }
        finally
        {
            RuntimeActivityFinished?.Invoke();
        }
    }

    private static string TryExtractChatContent(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var ch0 = choices[0];

                // Non-stream: choices[0].message.content
                if (ch0.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                    return content.GetString() ?? "";

                // Some servers return "text"
                if (ch0.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    return txt.GetString() ?? "";

                // Fallback: delta.content
                if (ch0.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var dc) &&
                    dc.ValueKind == JsonValueKind.String)
                    return dc.GetString() ?? "";
            }
        }
        catch (JsonException ex)
        {
            ClientLog.Warn($"OpenAI-compatible JSON payload ignored: {ex.Message}");
        }

        return "";
    }

    private static async Task SimulateStreamingAsync(string full, Action<string> onDelta, CancellationToken ct)
    {
        // Chunking without artificial slowness; Task.Yield lets the UI breathe between chunks.
        const int chunkSize = 48;

        for (int i = 0; i < full.Length; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var len = Math.Min(chunkSize, full.Length - i);
            var chunk = full.Substring(i, len);
            if (chunk.Length > 0) onDelta(chunk);
            await Task.Yield();
        }
    }


    public async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<string>();
        var root = doc.RootElement;

        // Common OpenAI format: { data: [ { id: "..." } ] }
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
            {
                if (el.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var s = id.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                }
            }
        }

        // llama.cpp may also return { models: [ { name|model: "..." } ] }
        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in models.EnumerateArray())
            {
                string? v = null;
                if (el.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) v = name.GetString();
                else if (el.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String) v = model.GetString();

                if (!string.IsNullOrWhiteSpace(v)) list.Add(v!);
            }
        }

        return list
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}
