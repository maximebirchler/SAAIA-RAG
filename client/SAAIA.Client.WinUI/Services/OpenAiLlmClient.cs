using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;


namespace SAAIA.Client.WinUI.Services;

internal enum OpenAiCompatibleDialect
{
    LlamaCpp,
    OpenAi
}

internal sealed class OpenAiCompatibleEndpointOptions
{
    internal OpenAiCompatibleEndpointOptions(
        string baseUrl,
        string modelId,
        string provider,
        string? apiKey,
        OpenAiCompatibleDialect dialect,
        string? reasoningEffort,
        bool ensureLocalRuntime)
    {
        BaseUrl = baseUrl;
        ModelId = modelId;
        Provider = provider;
        ApiKey = apiKey;
        Dialect = dialect;
        ReasoningEffort = reasoningEffort;
        EnsureLocalRuntime = ensureLocalRuntime;
    }

    internal string BaseUrl { get; }
    internal string ModelId { get; }
    internal string Provider { get; }
    internal string? ApiKey { get; }
    internal OpenAiCompatibleDialect Dialect { get; }
    internal string? ReasoningEffort { get; }
    internal bool EnsureLocalRuntime { get; }

    public override string ToString()
        => $"{Provider}:{ModelId}@{BaseUrl} (credential redacted)";
}

public sealed record LlmStreamResult(
    LlmTokenUsage Usage,
    long? TimeToFirstTokenMilliseconds);

public sealed class OpenAiLlmClient
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _http;
    private int _nativeInputTokenCountingAvailability;
    private int _nativeRuntimeContextTokens;
    private string? _apiKey;
    private string _provider = "local";
    private OpenAiCompatibleDialect _dialect = OpenAiCompatibleDialect.LlamaCpp;
    private string? _reasoningEffort;
    private bool _ensureLocalRuntime = true;

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


    private async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { ClientLog.Warn($"OpenAI-compatible error body unreadable: {ex.Message}"); }

        body = RedactCredential(body);
        var msg = $"LLM request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}";
        throw new HttpRequestException(msg, null, resp.StatusCode);
    }

    private string RedactCredential(string? value)
    {
        var result = value ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(_apiKey))
            result = result.Replace(_apiKey, "[REDACTED]", StringComparison.Ordinal);
        return result;
    }

    public void Configure(string baseUrl, string model)
    {
        ConfigureEndpoint(new OpenAiCompatibleEndpointOptions(
            baseUrl,
            model,
            "local",
            apiKey: null,
            OpenAiCompatibleDialect.LlamaCpp,
            reasoningEffort: null,
            ensureLocalRuntime: true));
    }

    internal void ConfigureEndpoint(OpenAiCompatibleEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _baseUrl = options.BaseUrl.Trim().TrimEnd('/');
        _model = options.ModelId.Trim();
        _provider = options.Provider.Trim();
        _apiKey = string.IsNullOrWhiteSpace(options.ApiKey)
            ? null
            : options.ApiKey.Trim();
        _dialect = options.Dialect;
        _reasoningEffort = string.IsNullOrWhiteSpace(options.ReasoningEffort)
            ? null
            : options.ReasoningEffort.Trim().ToLowerInvariant();
        _ensureLocalRuntime = options.EnsureLocalRuntime;
        Volatile.Write(ref _nativeRuntimeContextTokens, 0);
        Volatile.Write(ref _nativeInputTokenCountingAvailability, 0);
    }

    private async Task EnsureRuntimeReadyAsync(CancellationToken ct)
    {
        if (!_ensureLocalRuntime)
            return;
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
        => (await ChatOnceCoreAsync(
                messages,
                temperature,
                maxTokens,
                ct,
                forceJson,
                structuredOutput: null)
            .ConfigureAwait(false)).Content;

    internal Task<SourceBackedAgentCompletion> ChatOnceCompletionAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        CancellationToken ct,
        bool forceJson = false)
        => ChatOnceCoreAsync(
            messages,
            temperature,
            maxTokens,
            ct,
            forceJson,
            structuredOutput: null);

    public async Task<string> ChatOnceStructuredAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        LlmStructuredOutputContract contract,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return (await ChatOnceCoreAsync(
                messages,
                temperature,
                maxTokens,
                ct,
                forceJson: true,
                contract)
            .ConfigureAwait(false)).Content;
    }

    public async Task<SourceBackedAgentCompletion>
        ChatOnceStructuredCompletionAsync(
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
        var callId = "native-" + Guid.NewGuid().ToString("N")[..10];
        var traceId = SourceBackedTelemetryContext.TraceId ?? "unscoped";
        var callKind = ResolveNativeCallKind(messages, tools);
        var stopwatch = Stopwatch.StartNew();
        ClientLog.Info(
            "[LLM_NATIVE event=request.start" +
            $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
            $" messages={messages.Count} prompt_chars={CountMessageCharacters(messages)}" +
            $" tools={tools.Count} tool_names={FormatToolNames(tools)}" +
            $" require_tool={requireToolCall} max_output_tokens={maxTokens}]");
        RuntimeActivityStarted?.Invoke();
        try
        {
            var payload = BuildNativeToolPayload(messages, tools, requireToolCall);
            payload[ResolveMaximumTokensPropertyName()] = maxTokens;
            payload["stream"] = false;
            ApplyGenerationParameters(payload, temperature, requireToolCall);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            ApplyAuthentication(req);
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var completion = ParseNativeToolCompletion(json, requireToolCall);
            ClientLog.Info(
                "[LLM_NATIVE event=request.end" +
                $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                $" finish_reason={completion.FinishReason}" +
                $" tool_calls={completion.ToolCalls.Count}" +
                $" prompt_tokens={completion.PromptTokens?.ToString() ?? "unknown"}" +
                $" completion_tokens={completion.CompletionTokens?.ToString() ?? "unknown"}" +
                $" cache_tokens={completion.ServerCacheTokens?.ToString() ?? "unknown"}" +
                $" prompt_evaluated_tokens={completion.ServerPromptTokensEvaluated?.ToString() ?? "unknown"}" +
                $" prompt_ms={FormatTiming(completion.ServerPromptMilliseconds)}" +
                $" predicted_tokens={completion.ServerPredictedTokens?.ToString() ?? "unknown"}" +
                $" predicted_ms={FormatTiming(completion.ServerPredictedMilliseconds)}" +
                $" output_chars={completion.Content?.Length ?? 0}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            return completion;
        }
        catch (OperationCanceledException)
        {
            ClientLog.Warn(
                "[LLM_NATIVE event=request.cancelled" +
                $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            throw;
        }
        catch (Exception ex)
        {
            ClientLog.Warn(
                "[LLM_NATIVE event=request.error" +
                $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                $" error_type={ex.GetType().Name}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            throw;
        }
        finally
        {
            RuntimeActivityFinished?.Invoke();
        }
    }

    public async Task<int?> CountNativeInputTokensAsync(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        CancellationToken ct,
        bool requireToolCall = false)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        if (Volatile.Read(ref _nativeInputTokenCountingAvailability) < 0)
            return null;

        await EnsureRuntimeReadyAsync(ct).ConfigureAwait(false);
        var callId = "tokens-" + Guid.NewGuid().ToString("N")[..10];
        var traceId = SourceBackedTelemetryContext.TraceId ?? "unscoped";
        var callKind = ResolveNativeCallKind(messages, tools);
        var stopwatch = Stopwatch.StartNew();
        ClientLog.Info(
            "[LLM_NATIVE event=input_tokens.start" +
            $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
            $" messages={messages.Count} prompt_chars={CountMessageCharacters(messages)}" +
            $" tools={tools.Count} tool_names={FormatToolNames(tools)}]");

        try
        {
            var payload = BuildNativeToolPayload(messages, tools, requireToolCall);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_baseUrl}/chat/completions/input_tokens");
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            ApplyAuthentication(request);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOpts),
                Encoding.UTF8,
                "application/json");

            using var response = await _http.SendAsync(request, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.NotFound
                    or HttpStatusCode.MethodNotAllowed
                    or HttpStatusCode.NotImplemented)
                {
                    Volatile.Write(
                        ref _nativeInputTokenCountingAvailability,
                        -1);
                }
                ClientLog.Warn(
                    "[LLM_NATIVE event=input_tokens.unavailable" +
                    $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                    $" status={(int)response.StatusCode}" +
                    $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            int? count = document.RootElement.TryGetProperty(
                       "input_tokens",
                       out var inputTokens)
                   && inputTokens.TryGetInt32(out var parsedCount)
                   && parsedCount >= 0
                ? parsedCount
                : null;
            if (count is not null)
            {
                Volatile.Write(
                    ref _nativeInputTokenCountingAvailability,
                    1);
            }
            ClientLog.Info(
                "[LLM_NATIVE event=input_tokens.end" +
                $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                $" input_tokens={count?.ToString() ?? "unknown"}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            return count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ClientLog.Warn(
                "[LLM_NATIVE event=input_tokens.error" +
                $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                $" error_type={ex.GetType().Name}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            return null;
        }
    }

    public async Task<int?> GetNativeRuntimeContextTokensAsync(
        CancellationToken ct)
    {
        var cached = Volatile.Read(ref _nativeRuntimeContextTokens);
        if (cached != 0)
            return cached > 0 ? cached : null;

        await EnsureRuntimeReadyAsync(ct).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var traceId = SourceBackedTelemetryContext.TraceId ?? "unscoped";
        ClientLog.Info(
            "[LLM_NATIVE event=runtime_context.start" +
            $" trace_id={traceId}]");
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_baseUrl}/models");
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            ApplyAuthentication(request);
            using var response = await _http.SendAsync(request, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ClientLog.Warn(
                    "[LLM_NATIVE event=runtime_context.unavailable" +
                    $" trace_id={traceId} status={(int)response.StatusCode}" +
                    $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var contextTokens = ReadRuntimeContextTokens(
                document.RootElement,
                _model);
            if (contextTokens is > 0)
                Volatile.Write(ref _nativeRuntimeContextTokens, contextTokens.Value);
            else
                Volatile.Write(ref _nativeRuntimeContextTokens, -1);
            ClientLog.Info(
                "[LLM_NATIVE event=runtime_context.end" +
                $" trace_id={traceId}" +
                $" context_tokens={contextTokens?.ToString() ?? "unknown"}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            return contextTokens;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ClientLog.Warn(
                "[LLM_NATIVE event=runtime_context.error" +
                $" trace_id={traceId} error_type={ex.GetType().Name}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            return null;
        }
    }

    private static int? ReadRuntimeContextTokens(
        JsonElement root,
        string configuredModel)
    {
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? fallback = null;
        foreach (var item in data.EnumerateArray())
        {
            fallback ??= item;
            if (item.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && ModelIdentifiersMatch(id.GetString(), configuredModel))
            {
                return ReadContextTokensFromModel(item);
            }
        }

        return fallback is { } first
            ? ReadContextTokensFromModel(first)
            : null;
    }

    private static int? ReadContextTokensFromModel(JsonElement model)
    {
        if (TryReadPositiveInt(model, "n_ctx", out var direct))
            return direct;
        if (!model.TryGetProperty("meta", out var meta)
            || meta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryReadPositiveInt(meta, "n_ctx", out var active))
            return active;
        return TryReadPositiveInt(meta, "n_ctx_train", out var trained)
            ? trained
            : null;
    }

    private static bool TryReadPositiveInt(
        JsonElement parent,
        string propertyName,
        out int value)
    {
        value = 0;
        if (!parent.TryGetProperty(propertyName, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value > 0)
        {
            return true;
        }
        return property.ValueKind == JsonValueKind.String
               && int.TryParse(property.GetString(), out value)
               && value > 0;
    }

    private static bool ModelIdentifiersMatch(
        string? runtimeModel,
        string configuredModel)
    {
        if (string.IsNullOrWhiteSpace(runtimeModel)
            || string.IsNullOrWhiteSpace(configuredModel))
        {
            return false;
        }

        var runtimeName = Path.GetFileName(runtimeModel.Trim());
        var configuredName = Path.GetFileName(configuredModel.Trim());
        return string.Equals(
            runtimeName,
            configuredName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static int CountMessageCharacters(
        IReadOnlyList<SourceBackedAgentMessage> messages)
        => messages.Sum(static message => message.Content?.Length ?? 0);

    private static string FormatToolNames(
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
        => tools.Count == 0
            ? "none"
            : string.Join(",", tools.Select(static tool => tool.Name));

    private static string ResolveNativeCallKind(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
    {
        if (tools.Count == 1
            && tools[0].Name.StartsWith(
                "submit_",
                StringComparison.OrdinalIgnoreCase))
        {
            return tools[0].Name;
        }
        if (tools.Count > 0)
            return "agent_tool_decision";

        var system = messages.FirstOrDefault(static message =>
            string.Equals(
                message.Role,
                "system",
                StringComparison.OrdinalIgnoreCase))?.Content;
        return system?.Contains(
                   "redacteur final",
                   StringComparison.OrdinalIgnoreCase) == true
            ? "dedicated_writer"
            : "completion_without_tools";
    }

    private Dictionary<string, object?> BuildNativeToolPayload(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        bool requireToolCall)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = messages.Select(ToNativeToolMessagePayload).ToArray()
        };
        if (tools.Count == 0)
            return payload;

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
        // llama-server currently accepts the OpenAI-compatible string form
        // here, but ignores the named-function object form. A required call
        // with exactly one exposed tool is mechanically equivalent to naming
        // that tool and remains portable across both runtimes.
        payload["tool_choice"] = requireToolCall
            ? "required"
            : "auto";
        payload["parallel_tool_calls"] = !requireToolCall;
        return payload;
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

    private static SourceBackedAgentCompletion ParseNativeToolCompletion(
        string json,
        bool recoverRequiredEmbeddedToolCall)
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
        if (calls.Count == 0
            && recoverRequiredEmbeddedToolCall
            && TryParseEmbeddedRequiredToolCall(content, out var recoveredCall))
        {
            calls.Add(recoveredCall);
        }

        var finishReason = choice.TryGetProperty("finish_reason", out var finish)
                           && finish.ValueKind == JsonValueKind.String
            ? finish.GetString() ?? string.Empty
            : string.Empty;
        var usage = ReadUsage(root);

        return new SourceBackedAgentCompletion(
            content,
            calls,
            finishReason,
            usage.InputTokens,
            usage.OutputTokens,
            ReadTimingInt(root, "cache_n"),
            ReadTimingInt(root, "prompt_n"),
            ReadTimingDouble(root, "prompt_ms"),
            ReadTimingInt(root, "predicted_n"),
            ReadTimingDouble(root, "predicted_ms"),
            CachedInputTokens: usage.CachedInputTokens,
            CacheWriteTokens: usage.CacheWriteTokens,
            ReasoningTokens: usage.ReasoningTokens);
    }

    private static bool TryParseEmbeddedRequiredToolCall(
        string content,
        out SourceBackedAgentToolCall toolCall)
    {
        toolCall = default!;
        const string openingTag = "<tool_call>";
        const string closingTag = "</tool_call>";
        var start = content.IndexOf(openingTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        start += openingTag.Length;
        var end = content.IndexOf(
            closingTag,
            start,
            StringComparison.OrdinalIgnoreCase);
        if (end <= start
            || content.IndexOf(
                openingTag,
                end + closingTag.Length,
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        try
        {
            using var envelopeDocument = JsonDocument.Parse(
                content[start..end].Trim());
            var envelope = envelopeDocument.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object)
                return false;

            var function = envelope.TryGetProperty("function", out var nestedFunction)
                           && nestedFunction.ValueKind == JsonValueKind.Object
                ? nestedFunction
                : envelope;
            if (!function.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameElement.GetString())
                || !function.TryGetProperty("arguments", out var argumentsElement))
            {
                return false;
            }

            var argumentError = default(string);
            JsonElement arguments;
            try
            {
                if (argumentsElement.ValueKind == JsonValueKind.String)
                {
                    using var argumentsDocument = JsonDocument.Parse(
                        argumentsElement.GetString() ?? "{}");
                    arguments = argumentsDocument.RootElement.Clone();
                }
                else
                {
                    arguments = argumentsElement.Clone();
                }

                if (arguments.ValueKind != JsonValueKind.Object)
                {
                    argumentError = "tool_arguments_must_be_an_object";
                    arguments = JsonSerializer.SerializeToElement(
                        new Dictionary<string, object?>());
                }
            }
            catch (JsonException ex)
            {
                argumentError = "invalid_tool_arguments_json: " + ex.Message;
                arguments = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?>());
            }

            toolCall = new SourceBackedAgentToolCall(
                "embedded-required-tool-call-1",
                nameElement.GetString()!.Trim(),
                arguments,
                argumentError);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<SourceBackedAgentCompletion> ChatOnceCoreAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        CancellationToken ct,
        bool forceJson,
        LlmStructuredOutputContract? structuredOutput)
    {
        await EnsureRuntimeReadyAsync(ct).ConfigureAwait(false);
        messages = OpenAiChatMessageNormalizer.MergeSystemMessages(messages);
        var callId = "chat-" + Guid.NewGuid().ToString("N")[..10];
        var traceId = SourceBackedTelemetryContext.TraceId ?? "unscoped";
        var callKind = ResolveChatCallKind(
            messages,
            forceJson,
            structuredOutput);
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        ClientLog.Info(
            "[LLM_NATIVE event=request.start" +
            $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
            $" messages={messages.Count}" +
            $" prompt_chars={CountChatMessageCharacters(messages)}" +
            $" tools=0 force_json={forceJson}" +
            $" structured_contract={structuredOutput?.Name ?? "none"}" +
            $" max_output_tokens={maxTokens}]");
        RuntimeActivityStarted?.Invoke();
        try
        {
            attempts++;
            var resp = await SendChatRequestAsync(
                    messages,
                    temperature,
                    maxTokens,
                    forceJson,
                    structuredOutput,
                    useLegacyLlamaStructuredFormat: false,
                    ct)
                .ConfigureAwait(false);
            if (_dialect == OpenAiCompatibleDialect.LlamaCpp
                && structuredOutput is not null
                && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422))
            {
                ClientLog.Warn(
                    $"OpenAI-compatible endpoint rejected response_format=json_schema ({(int)resp.StatusCode}); retrying with llama.cpp json_object+schema.");
                resp.Dispose();
                attempts++;
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

            if (_dialect == OpenAiCompatibleDialect.LlamaCpp
                && structuredOutput is not null
                && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422))
            {
                ClientLog.Warn(
                    $"OpenAI-compatible endpoint rejected legacy schema response format ({(int)resp.StatusCode}); retrying with unconstrained json_object.");
                resp.Dispose();
                attempts++;
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
            else if (_dialect == OpenAiCompatibleDialect.LlamaCpp
                     && structuredOutput is null
                     && forceJson
                && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422))
            {
                ClientLog.Warn(
                    $"OpenAI-compatible endpoint rejected response_format=json_object ({(int)resp.StatusCode}); retrying with prompt-only JSON enforcement.");
                resp.Dispose();
                attempts++;
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

                var choice = doc.RootElement.GetProperty("choices")[0];
                var finishReason = choice.TryGetProperty(
                        "finish_reason",
                        out var finish)
                    && finish.ValueKind == JsonValueKind.String
                        ? finish.GetString() ?? "unknown"
                        : "unknown";
                var promptTokens = ReadUsageTokenCount(
                    doc.RootElement,
                    "prompt_tokens");
                var completionTokens = ReadUsageTokenCount(
                    doc.RootElement,
                    "completion_tokens");
                var usage = ReadUsage(doc.RootElement);
                var cacheTokens = ReadTimingInt(doc.RootElement, "cache_n");
                var promptEvaluatedTokens = ReadTimingInt(
                    doc.RootElement,
                    "prompt_n");
                var promptMilliseconds = ReadTimingDouble(
                    doc.RootElement,
                    "prompt_ms");
                var predictedTokens = ReadTimingInt(
                    doc.RootElement,
                    "predicted_n");
                var predictedMilliseconds = ReadTimingDouble(
                    doc.RootElement,
                    "predicted_ms");
                ClientLog.Info(
                    "[LLM_NATIVE event=request.end" +
                    $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                    $" finish_reason={finishReason}" +
                    $" attempts={attempts}" +
                    $" prompt_tokens={promptTokens?.ToString() ?? "unknown"}" +
                    $" completion_tokens={completionTokens?.ToString() ?? "unknown"}" +
                    $" cache_tokens={cacheTokens?.ToString() ?? "unknown"}" +
                    $" prompt_evaluated_tokens={promptEvaluatedTokens?.ToString() ?? "unknown"}" +
                    $" prompt_ms={FormatTiming(promptMilliseconds)}" +
                    $" predicted_tokens={predictedTokens?.ToString() ?? "unknown"}" +
                    $" predicted_ms={FormatTiming(predictedMilliseconds)}" +
                    $" output_chars={content?.Length ?? 0}" +
                    $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
                return new SourceBackedAgentCompletion(
                    content ?? string.Empty,
                    Array.Empty<SourceBackedAgentToolCall>(),
                    finishReason,
                    promptTokens,
                    completionTokens,
                    cacheTokens,
                    promptEvaluatedTokens,
                    promptMilliseconds,
                    predictedTokens,
                    predictedMilliseconds,
                    CachedInputTokens: usage.CachedInputTokens,
                    CacheWriteTokens: usage.CacheWriteTokens,
                    ReasoningTokens: usage.ReasoningTokens,
                    RetryCount: Math.Max(0, attempts - 1));
            }
        }
        catch (OperationCanceledException)
        {
            ClientLog.Warn(
                "[LLM_NATIVE event=request.cancelled" +
                $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                $" attempts={attempts}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            throw;
        }
        catch (Exception ex)
        {
            ClientLog.Warn(
                "[LLM_NATIVE event=request.error" +
                $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                $" attempts={attempts}" +
                $" error_type={ex.GetType().Name}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            throw;
        }
        finally
        {
            RuntimeActivityFinished?.Invoke();
        }
    }

    private static int CountChatMessageCharacters(
        IReadOnlyList<(string role, string content)> messages)
        => messages.Sum(static message => message.content?.Length ?? 0);

    private static string ResolveChatCallKind(
        IReadOnlyList<(string role, string content)> messages,
        bool forceJson,
        LlmStructuredOutputContract? structuredOutput)
    {
        if (structuredOutput is not null)
            return "structured_" + structuredOutput.Name;

        var system = messages.FirstOrDefault(static message =>
            string.Equals(
                message.role,
                "system",
                StringComparison.OrdinalIgnoreCase)).content;
        if (system?.Contains(
                "routeur",
                StringComparison.OrdinalIgnoreCase) == true
            || system?.Contains(
                "router",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return "router";
        }
        if (system?.Contains(
                "redacteur",
                StringComparison.OrdinalIgnoreCase) == true
            || system?.Contains(
                "writer",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return "writer";
        }

        return forceJson ? "json_completion" : "chat_completion";
    }

    private static int? ReadUsageTokenCount(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty(propertyName, out var value)
            || !value.TryGetInt32(out var count)
            || count < 0)
        {
            return null;
        }

        return count;
    }

    private static LlmTokenUsage ReadUsage(JsonElement root)
    {
        var input = ReadUsageTokenCount(root, "prompt_tokens");
        var output = ReadUsageTokenCount(root, "completion_tokens");
        int? cached = null;
        int? cacheWrite = null;
        int? reasoning = null;
        if (root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens_details", out var inputDetails)
                && inputDetails.ValueKind == JsonValueKind.Object)
            {
                cached = ReadOptionalNonNegativeInt(inputDetails, "cached_tokens");
                cacheWrite = ReadOptionalNonNegativeInt(inputDetails, "cache_write_tokens");
            }
            if (usage.TryGetProperty("completion_tokens_details", out var outputDetails)
                && outputDetails.ValueKind == JsonValueKind.Object)
            {
                reasoning = ReadOptionalNonNegativeInt(outputDetails, "reasoning_tokens");
            }
        }
        return new LlmTokenUsage(input, output, cached, cacheWrite, reasoning);
    }

    private static int? ReadOptionalNonNegativeInt(JsonElement parent, string propertyName)
        => parent.TryGetProperty(propertyName, out var value)
           && value.TryGetInt32(out var parsed)
           && parsed >= 0
            ? parsed
            : null;

    private static int? ReadTimingInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty("timings", out var timings)
            || timings.ValueKind != JsonValueKind.Object
            || !timings.TryGetProperty(propertyName, out var value)
            || !value.TryGetInt32(out var parsed)
            || parsed < 0)
        {
            return null;
        }

        return parsed;
    }

    private static double? ReadTimingDouble(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty("timings", out var timings)
            || timings.ValueKind != JsonValueKind.Object
            || !timings.TryGetProperty(propertyName, out var value)
            || !value.TryGetDouble(out var parsed)
            || parsed < 0)
        {
            return null;
        }

        return parsed;
    }

    private static string FormatTiming(double? milliseconds)
        => milliseconds?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
           ?? "unknown";

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
            [ResolveMaximumTokensPropertyName()] = maxTokens,
            ["stream"] = false,
            ["messages"] = messages.Select(m => new { role = m.role, content = m.content }).ToArray()
        };
        if (_dialect == OpenAiCompatibleDialect.LlamaCpp)
        {
            payload["stop"] = new[] { "\nUSER_MESSAGE:", "\nTOOL_RESULTS", "\nDRAFT_ANSWER:", "\nCHAT_TAIL:" };
        }
        ApplyGenerationParameters(payload, temperature, structuredOutput is not null);
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
        ApplyAuthentication(req);
        req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<LlmStreamResult> ChatStreamAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        Action<string> onDelta,
        CancellationToken ct)
    {
        await EnsureRuntimeReadyAsync(ct).ConfigureAwait(false);
        messages = OpenAiChatMessageNormalizer.MergeSystemMessages(messages);
        var callId = "stream-" + Guid.NewGuid().ToString("N")[..10];
        var traceId = SourceBackedTelemetryContext.TraceId ?? "unscoped";
        var callKind = "stream_" + ResolveChatCallKind(
            messages,
            forceJson: false,
            structuredOutput: null);
        var stopwatch = Stopwatch.StartNew();
        var outputCharacters = 0;
        var responseMode = "unknown";
        var completed = false;
        var usage = new LlmTokenUsage(null, null);
        long? timeToFirstTokenMilliseconds = null;
        void EmitDelta(string delta)
        {
            timeToFirstTokenMilliseconds ??= stopwatch.ElapsedMilliseconds;
            outputCharacters += delta.Length;
            onDelta(delta);
        }
        ClientLog.Info(
            "[LLM_NATIVE event=request.start" +
            $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
            $" messages={messages.Count}" +
            $" prompt_chars={CountChatMessageCharacters(messages)}" +
            $" tools=0 force_json=false stream=true" +
            $" max_output_tokens={maxTokens}]");
        RuntimeActivityStarted?.Invoke();
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = _model,
                [ResolveMaximumTokensPropertyName()] = maxTokens,
                ["stream"] = true,
                ["messages"] = messages.Select(m => new { role = m.role, content = m.content }).ToArray()
            };
            if (_dialect == OpenAiCompatibleDialect.LlamaCpp)
            {
                payload["stop"] = new[] { "\nUSER_MESSAGE:", "\nTOOL_RESULTS", "\nDRAFT_ANSWER:", "\nCHAT_TAIL:" };
            }
            else
            {
                payload["stream_options"] = new Dictionary<string, object?>
                {
                    ["include_usage"] = true
                };
            }
            ApplyGenerationParameters(payload, temperature, structuredOutput: false);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            ApplyAuthentication(req);
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";

            // Some OpenAI-compatible servers ignore SSE and return a normal JSON response.
            // Spec requires progressive UX anyway => simulate streaming client-side.
            if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                responseMode = "json_simulated_stream";
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using (var jsonDocument = JsonDocument.Parse(json))
                    usage = ReadUsage(jsonDocument.RootElement);
                var full = TryExtractChatContent(json);
                if (!string.IsNullOrEmpty(full))
                    await SimulateStreamingAsync(full, EmitDelta, ct).ConfigureAwait(false);
                completed = true;
                return new LlmStreamResult(usage, timeToFirstTokenMilliseconds);
            }

            responseMode = "sse";
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            // Robuste: certains serveurs streament du "delta", d'autres du "contenu cumulatif"
            var emittedSoFar = "";
            var sawData = false;
            var sawDone = false;

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().WaitAsync(ct);
                if (line is null) break;                // stream fermé
                if (line.Length == 0) continue;         // ligne vide SSE

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                sawData = true;
                var data = line.Substring("data:".Length).Trim();
                if (data == "[DONE]")
                {
                    sawDone = true;
                    break;
                }

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    var chunkUsage = ReadUsage(root);
                    if (chunkUsage.InputTokens is not null || chunkUsage.OutputTokens is not null)
                        usage = chunkUsage;
                    if (!root.TryGetProperty("choices", out var choices)
                        || choices.ValueKind != JsonValueKind.Array
                        || choices.GetArrayLength() == 0)
                    {
                        continue;
                    }
                    var choice = choices[0];
                    if (!choice.TryGetProperty("delta", out var delta)
                        || delta.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
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
                                EmitDelta(diff);
                                emittedSoFar += diff;
                            }
                        }
                        else
                        {
                            // Sinon, on considère que c'est un vrai delta
                            EmitDelta(chunk);
                            emittedSoFar += chunk;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    ClientLog.Warn($"OpenAI-compatible SSE chunk ignored: {ex.Message}");
                }
            }

            if (_dialect == OpenAiCompatibleDialect.OpenAi && !sawDone)
                throw new IOException("The OpenAI stream ended before its [DONE] marker.");

            // Safety net: if server claimed SSE but never sent data lines, fall back to JSON parsing and simulate.
            if (!sawData || string.IsNullOrEmpty(emittedSoFar))
            {
                try
                {
                    // NOTE: at this point the stream might be consumed; we can only do best-effort by reading the remaining buffer.
                    var remaining = await reader.ReadToEndAsync().WaitAsync(ct);
                    var full = TryExtractChatContent(remaining);
                    if (!string.IsNullOrEmpty(full))
                    {
                        responseMode = "sse_json_fallback";
                        await SimulateStreamingAsync(full, EmitDelta, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    ClientLog.Warn($"OpenAI-compatible SSE fallback ignored: {ex.Message}");
                }
            }
            completed = true;
            return new LlmStreamResult(usage, timeToFirstTokenMilliseconds);
        }
        catch (OperationCanceledException)
        {
            ClientLog.Warn(
                "[LLM_NATIVE event=request.cancelled" +
                $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                $" response_mode={responseMode}" +
                $" output_chars={outputCharacters}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            throw;
        }
        catch (Exception ex)
        {
            ClientLog.Warn(
                "[LLM_NATIVE event=request.error" +
                $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                $" response_mode={responseMode}" +
                $" output_chars={outputCharacters}" +
                $" error_type={ex.GetType().Name}" +
                $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            throw;
        }
        finally
        {
            if (completed)
            {
                ClientLog.Info(
                    "[LLM_NATIVE event=request.end" +
                    $" trace_id={traceId} call_id={callId} call_kind={callKind}" +
                    $" finish_reason=stream_completed" +
                    $" response_mode={responseMode}" +
                    $" prompt_tokens={usage.InputTokens?.ToString() ?? "unknown"}" +
                    $" completion_tokens={usage.OutputTokens?.ToString() ?? "unknown"}" +
                    $" output_chars={outputCharacters}" +
                    $" elapsed_ms={stopwatch.ElapsedMilliseconds}]");
            }
            RuntimeActivityFinished?.Invoke();
        }
    }

    private string ResolveMaximumTokensPropertyName()
        => _dialect == OpenAiCompatibleDialect.OpenAi
            ? "max_completion_tokens"
            : "max_tokens";

    private void ApplyGenerationParameters(
        Dictionary<string, object?> payload,
        double temperature,
        bool structuredOutput)
    {
        if (_dialect == OpenAiCompatibleDialect.OpenAi)
        {
            if (!string.IsNullOrWhiteSpace(_reasoningEffort))
                payload["reasoning_effort"] = _reasoningEffort;
            return;
        }
        LlmSamplingConfiguration.ResolveFromEnvironment().Apply(
            payload,
            temperature,
            structuredOutput);
    }

    private void ApplyAuthentication(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
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
        ApplyAuthentication(req);

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
