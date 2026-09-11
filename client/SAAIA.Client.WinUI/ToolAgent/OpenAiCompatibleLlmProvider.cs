using System.Diagnostics;
using System.Net;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

internal abstract class OpenAiCompatibleLlmProviderBase : ILlmProvider
{
    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }

    private readonly OpenAiLlmClient _transport;
    private readonly LlmCostBudgetGuard? _budget;
    private readonly TimeSpan? _requestTimeout;
    private double _temperature = 0.2;
    private int _maximumOutputTokens = 1600;

    protected OpenAiCompatibleLlmProviderBase(
        OpenAiLlmClient transport,
        LlmProviderDescriptor descriptor,
        LlmCostBudgetGuard? budget = null,
        TimeSpan? requestTimeout = null)
    {
        _transport = transport;
        Descriptor = descriptor;
        _budget = budget;
        _requestTimeout = requestTimeout;
    }

    public LlmProviderDescriptor Descriptor { get; }
    internal LlmBudgetSnapshot? BudgetSnapshot => _budget?.GetSnapshot();
    public bool SupportsStructuredOutput => true;
    public event Action<LlmCallMetrics>? CallCompleted;

    public void ConfigureGeneration(double temperature, int maximumOutputTokens)
    {
        _temperature = double.IsFinite(temperature)
            ? Math.Clamp(temperature, 0, 1)
            : 0.2;
        _maximumOutputTokens = Math.Clamp(maximumOutputTokens, 128, 4096);
    }

    public IDisposable BeginTurn(string? correlationId = null)
        => _budget?.BeginTurn(correlationId) ?? EmptyScope.Instance;

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        => ListModelsCoreAsync(ct);

    private async Task<IReadOnlyList<string>> ListModelsCoreAsync(CancellationToken ct)
        => await _transport.ListModelsAsync(ct).ConfigureAwait(false);

    public async Task<string> CompleteAsync(
        IReadOnlyList<(string role, string content)> messages,
        bool forceJson,
        CancellationToken ct)
    {
        var list = messages?.ToList() ?? new List<(string role, string content)>();
        if (forceJson)
            list.Insert(0, ("system", "Return ONLY valid JSON. No markdown. No extra text."));
        var joinedPrompt = string.Join('\n', list.Select(static message => message.content ?? string.Empty));
        var useJsonResponseFormat = forceJson
                                    && RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(joinedPrompt);
        var maximumTokens = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            _maximumOutputTokens,
            forceJson,
            joinedPrompt);
        var visibleMessages = SourceBackedLlmPromptSanitizer.RemoveControlMetadata(list);
        var role = ResolveRole(visibleMessages, forceJson, structuredContract: null);
        var completion = await ExecuteCompletionAsync(
                role,
                LlmCostBudgetGuard.EstimateInputTokens(visibleMessages),
                maximumTokens,
                token => _transport.ChatOnceCompletionAsync(
                    visibleMessages,
                    _temperature,
                    maximumTokens,
                    token,
                    useJsonResponseFormat),
                ct)
            .ConfigureAwait(false);
        return completion.Content;
    }

    public async Task<string> CompleteStructuredAsync(
        IReadOnlyList<(string role, string content)> messages,
        LlmStructuredOutputContract contract,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var list = messages?.ToList() ?? new List<(string role, string content)>();
        list.Insert(0, ("system", "Return ONLY JSON matching the supplied schema. No markdown or extra text."));
        var joined = string.Join('\n', list.Select(static message => message.content ?? string.Empty));
        var maximumTokens = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            _maximumOutputTokens,
            forceJson: true,
            joined);
        var visibleMessages = SourceBackedLlmPromptSanitizer.RemoveControlMetadata(list);
        var completion = await ExecuteCompletionAsync(
                ResolveRole(visibleMessages, forceJson: true, contract.Name),
                LlmCostBudgetGuard.EstimateInputTokens(visibleMessages),
                maximumTokens,
                token => _transport.ChatOnceStructuredCompletionAsync(
                    visibleMessages,
                    _temperature,
                    maximumTokens,
                    contract,
                    token),
                ct)
            .ConfigureAwait(false);
        return completion.Content;
    }

    public async Task StreamAsync(
        IReadOnlyList<(string role, string content)> messages,
        bool forceJson,
        Action<string> onDelta,
        CancellationToken ct)
    {
        var list = messages?.ToList() ?? new List<(string role, string content)>();
        if (forceJson)
        {
            var full = await CompleteAsync(list, forceJson: true, ct).ConfigureAwait(false);
            await EmitChunksAsync(full, onDelta, ct).ConfigureAwait(false);
            return;
        }

        var joined = string.Join('\n', list.Select(static message => message.content ?? string.Empty));
        var maximumTokens = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            _maximumOutputTokens,
            forceJson: false,
            joined);
        var visibleMessages = SourceBackedLlmPromptSanitizer.RemoveControlMetadata(list);
        var role = ResolveRole(visibleMessages, forceJson: false, structuredContract: null);
        var reservation = _budget?.Reserve(
            role,
            LlmCostBudgetGuard.EstimateInputTokens(visibleMessages),
            maximumTokens);
        var stopwatch = Stopwatch.StartNew();
        LlmStreamResult result;
        using var timeout = CreateRequestTimeout(ct);
        var requestToken = timeout?.Token ?? ct;
        try
        {
            result = await _transport.ChatStreamAsync(
                    visibleMessages,
                    _temperature,
                    maximumTokens,
                    onDelta,
                    requestToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (!ct.IsCancellationRequested && timeout?.IsCancellationRequested == true)
        {
            var timeoutError = new LlmProviderRequestTimeoutException(
                Descriptor.Provider,
                _requestTimeout!.Value,
                ex);
            var errorCode = NormalizeErrorCode(timeoutError);
            decimal? cost = reservation is null ? null : _budget!.Fail(reservation, errorCode);
            EmitMetrics(
                reservation,
                role,
                stopwatch.ElapsedMilliseconds,
                null,
                new LlmTokenUsage(null, null),
                cost,
                success: false,
                retryCount: 0,
                errorCode);
            throw timeoutError;
        }
        catch (Exception ex)
        {
            decimal? cost = reservation is null ? null : _budget!.Fail(reservation, NormalizeErrorCode(ex));
            EmitMetrics(
                reservation,
                role,
                stopwatch.ElapsedMilliseconds,
                null,
                new LlmTokenUsage(null, null),
                cost,
                success: false,
                retryCount: 0,
                NormalizeErrorCode(ex));
            throw;
        }

        var usage = result.Usage;
        decimal? completedCost = reservation is null ? null : _budget!.Complete(reservation, usage);
        EmitMetrics(
            reservation,
            role,
            stopwatch.ElapsedMilliseconds,
            result.TimeToFirstTokenMilliseconds,
            usage,
            completedCost,
            success: true,
            retryCount: 0,
            errorCode: null);
    }

    public async Task<SourceBackedAgentCompletion> CompleteAsync(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        int maxTokens,
        CancellationToken ct,
        double? temperatureOverride = null,
        bool requireToolCall = false)
    {
        var effectiveMaxTokens = Math.Clamp(maxTokens, 64, 4096);
        return await SourceBackedLlmCumulativeBudgetContext.ExecuteAsync(
                SourceBackedLlmCumulativeBudgetContext.ResolveNativeCallClass(tools),
                SourceBackedLlmCumulativeBudgetContext.IsTerminalNativeCall(tools),
                messages,
                tools,
                effectiveMaxTokens,
                token => CountInputTokensAsync(messages, tools, token, requireToolCall),
                token => ExecuteCompletionAsync(
                    ResolveRole(messages, tools),
                    LlmCostBudgetGuard.EstimateInputTokens(messages, tools),
                    effectiveMaxTokens,
                    inner => _transport.ChatOnceNativeAsync(
                        messages,
                        tools,
                        temperatureOverride ?? _temperature,
                        effectiveMaxTokens,
                        inner,
                        requireToolCall),
                    token),
                ct)
            .ConfigureAwait(false);
    }

    async Task<SourceBackedAgentCompletion>
        ISourceBackedAgentStructuredLlmClient.CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride)
    {
        var visibleMessages = SourceBackedLlmPromptSanitizer
            .RemoveControlMetadata(messages.Select(static message =>
                (message.Role, message.Content ?? string.Empty)).ToArray());
        var nativeMessages = visibleMessages
            .Select(static message => new SourceBackedAgentMessage(
                message.role,
                message.content))
            .ToArray();
        var noTools = Array.Empty<SourceBackedAgentToolDefinition>();
        var effectiveMaxTokens = Math.Clamp(maxTokens, 64, 4096);
        return await SourceBackedLlmCumulativeBudgetContext.ExecuteAsync(
                contract.Name,
                SourceBackedLlmCumulativeBudgetContext.IsTerminalStructuredCall(contract.Name),
                nativeMessages,
                noTools,
                effectiveMaxTokens,
                token => CountInputTokensAsync(nativeMessages, noTools, token),
                token => ExecuteCompletionAsync(
                    ResolveRole(visibleMessages, forceJson: true, contract.Name),
                    LlmCostBudgetGuard.EstimateInputTokens(visibleMessages),
                    effectiveMaxTokens,
                    inner => _transport.ChatOnceStructuredCompletionAsync(
                        visibleMessages,
                        temperatureOverride ?? _temperature,
                        effectiveMaxTokens,
                        contract,
                        inner),
                    token),
                ct)
            .ConfigureAwait(false);
    }

    public Task<int?> CountInputTokensAsync(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        CancellationToken ct,
        bool requireToolCall = false)
    {
        if (Descriptor.Mode != LlmProviderMode.Local)
        {
            return Task.FromResult<int?>(
                LlmCostBudgetGuard.EstimateInputTokens(messages, tools));
        }
        return _transport.CountNativeInputTokensAsync(messages, tools, ct, requireToolCall);
    }

    public Task<int?> GetRuntimeContextTokensAsync(CancellationToken ct)
        => Descriptor.ContextWindowTokens is > 0
            ? Task.FromResult(Descriptor.ContextWindowTokens)
            : _transport.GetNativeRuntimeContextTokensAsync(ct);

    private async Task<SourceBackedAgentCompletion> ExecuteCompletionAsync(
        LlmLogicalRole role,
        int estimatedInputTokens,
        int maximumOutputTokens,
        Func<CancellationToken, Task<SourceBackedAgentCompletion>> operation,
        CancellationToken ct)
    {
        var reservation = _budget?.Reserve(role, estimatedInputTokens, maximumOutputTokens);
        var stopwatch = Stopwatch.StartNew();
        SourceBackedAgentCompletion completion;
        using var timeout = CreateRequestTimeout(ct);
        var requestToken = timeout?.Token ?? ct;
        try
        {
            completion = await operation(requestToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (!ct.IsCancellationRequested && timeout?.IsCancellationRequested == true)
        {
            var timeoutError = new LlmProviderRequestTimeoutException(
                Descriptor.Provider,
                _requestTimeout!.Value,
                ex);
            var errorCode = NormalizeErrorCode(timeoutError);
            decimal? cost = reservation is null ? null : _budget!.Fail(reservation, errorCode);
            EmitMetrics(
                reservation,
                role,
                stopwatch.ElapsedMilliseconds,
                null,
                new LlmTokenUsage(null, null),
                cost,
                success: false,
                retryCount: 0,
                errorCode);
            throw timeoutError;
        }
        catch (Exception ex)
        {
            var errorCode = NormalizeErrorCode(ex);
            decimal? cost = reservation is null ? null : _budget!.Fail(reservation, errorCode);
            EmitMetrics(
                reservation,
                role,
                stopwatch.ElapsedMilliseconds,
                null,
                new LlmTokenUsage(null, null),
                cost,
                success: false,
                retryCount: 0,
                errorCode);
            throw;
        }


        var usage = ToUsage(completion);
        decimal? completedCost = reservation is null ? null : _budget!.Complete(reservation, usage);
        EmitMetrics(
            reservation,
            role,
            stopwatch.ElapsedMilliseconds,
            null,
            usage,
            completedCost,
            success: true,
            completion.RetryCount,
            completion.ProtocolError);
        return completion;
    }

    private void EmitMetrics(
        LlmCostBudgetGuard.Reservation? reservation,
        LlmLogicalRole role,
        long elapsedMilliseconds,
        long? ttftMilliseconds,
        LlmTokenUsage usage,
        decimal? cost,
        bool success,
        int retryCount,
        string? errorCode)
    {
        var metrics = new LlmCallMetrics(
            DateTimeOffset.UtcNow,
            reservation?.RequestId ?? "llm-" + Guid.NewGuid().ToString("N")[..12],
            reservation?.TraceId ?? SourceBackedTelemetryContext.TraceId ?? "unscoped",
            Descriptor.Mode,
            Descriptor.Provider,
            Descriptor.ModelId,
            role,
            elapsedMilliseconds,
            ttftMilliseconds,
            usage,
            cost,
            success,
            retryCount,
            errorCode);
        ClientLog.Info(
            "[LLM_PROVIDER event=call.end" +
            $" trace_id={metrics.TraceId} request_id={metrics.RequestId}" +
            $" provider={metrics.Provider} mode={metrics.Mode} model={metrics.ModelId}" +
            $" role={metrics.Role} success={metrics.Success}" +
            $" elapsed_ms={metrics.ElapsedMilliseconds}" +
            $" ttft_ms={metrics.TimeToFirstTokenMilliseconds?.ToString() ?? "unknown"}" +
            $" input_tokens={metrics.Usage.InputTokens?.ToString() ?? "unknown"}" +
            $" cached_input_tokens={metrics.Usage.CachedInputTokens?.ToString() ?? "unknown"}" +
            $" cache_write_tokens={metrics.Usage.CacheWriteTokens?.ToString() ?? "unknown"}" +
            $" output_tokens={metrics.Usage.OutputTokens?.ToString() ?? "unknown"}" +
            $" reasoning_tokens={metrics.Usage.ReasoningTokens?.ToString() ?? "unknown"}" +
            $" cost_usd={metrics.EstimatedCostUsd?.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}" +
            $" retries={metrics.RetryCount} error_code={metrics.ErrorCode ?? "none"}]");
        CallCompleted?.Invoke(metrics);
    }

    private static LlmTokenUsage ToUsage(SourceBackedAgentCompletion completion)
        => new(
            completion.PromptTokens,
            completion.CompletionTokens,
            completion.CachedInputTokens,
            completion.CacheWriteTokens,
            completion.ReasoningTokens);

    private static LlmLogicalRole ResolveRole(
        IReadOnlyList<(string role, string content)> messages,
        bool forceJson,
        string? structuredContract)
    {
        var text = string.Join('\n', messages.Select(static message => message.content ?? string.Empty));
        if (text.Contains("critic", StringComparison.OrdinalIgnoreCase)
            || text.Contains("critique", StringComparison.OrdinalIgnoreCase)
            || text.Contains("review", StringComparison.OrdinalIgnoreCase))
            return LlmLogicalRole.Critic;
        if (text.Contains("router", StringComparison.OrdinalIgnoreCase)
            || text.Contains("routeur", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SAAIA_SOURCE_BACKED_STEP=Planner", StringComparison.OrdinalIgnoreCase))
            return LlmLogicalRole.Router;
        if (!string.IsNullOrWhiteSpace(structuredContract)
            && structuredContract.Contains("router", StringComparison.OrdinalIgnoreCase))
            return LlmLogicalRole.Router;
        if (text.Contains("writer", StringComparison.OrdinalIgnoreCase)
            || text.Contains("redacteur", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rédacteur", StringComparison.OrdinalIgnoreCase))
            return LlmLogicalRole.Writer;
        return forceJson ? LlmLogicalRole.Router : LlmLogicalRole.Writer;
    }

    private static LlmLogicalRole ResolveRole(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
    {
        if (tools.Count > 0)
            return LlmLogicalRole.Router;
        return ResolveRole(
            messages.Select(static message =>
                (message.Role, message.Content ?? string.Empty)).ToArray(),
            forceJson: false,
            structuredContract: null);
    }

    private static string NormalizeErrorCode(Exception exception)
        => exception switch
        {
            LlmProviderRequestTimeoutException => "timeout",
            OperationCanceledException => "cancelled",
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "http_401",
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "http_429",
            HttpRequestException { StatusCode: { } status } => "http_" + (int)status,
            LlmBudgetExceededException budget => "budget_" + budget.Reason,
            _ => exception.GetType().Name
        };

    private CancellationTokenSource? CreateRequestTimeout(CancellationToken callerToken)
    {
        if (_requestTimeout is null)
            return null;
        var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        source.CancelAfter(_requestTimeout.Value);
        return source;
    }

    private static async Task EmitChunksAsync(
        string text,
        Action<string> onDelta,
        CancellationToken ct)
    {
        if (onDelta is null || string.IsNullOrEmpty(text))
            return;
        const int chunkSize = 48;
        for (var index = 0; index < text.Length; index += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var length = Math.Min(chunkSize, text.Length - index);
            onDelta(text.Substring(index, length));
            await Task.Yield();
        }
    }
}

internal sealed class LocalLlmProvider(
    OpenAiLlmClient transport,
    LlmProviderDescriptor descriptor)
    : OpenAiCompatibleLlmProviderBase(transport, descriptor);

internal sealed class OpenAiDevLlmProvider(
    OpenAiLlmClient transport,
    LlmProviderDescriptor descriptor,
    LlmCostBudgetGuard budget,
    TimeSpan requestTimeout)
    : OpenAiCompatibleLlmProviderBase(transport, descriptor, budget, requestTimeout);

internal sealed class RunPodBenchLlmProvider(
    OpenAiLlmClient transport,
    LlmProviderDescriptor descriptor,
    TimeSpan requestTimeout)
    : OpenAiCompatibleLlmProviderBase(transport, descriptor, requestTimeout: requestTimeout);
