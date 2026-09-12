using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

/// <summary>
/// Executes durable advanced-analysis jobs against the configured large-model
/// endpoint. OpenAI DEV, RunPod BENCH and the final customer server use the
/// same SAAIA-owned planning, retrieval, evidence and result contracts.
/// </summary>
internal sealed class OpenAiCompatibleAdvancedAnalysisProvider :
    IAdvancedAnalysisProvider
{
    private const string HttpClientName = "advanced-analysis-llm";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AdvancedAnalysisOptions _options;
    private readonly string? _apiKey;
    private readonly AdvancedAnalysisExternalBudgetGuard? _budget;

    private sealed record CompletionResult(
        string Content,
        AdvancedAnalysisLlmUsage Usage,
        decimal? EstimatedCostUsd);

    internal OpenAiCompatibleAdvancedAnalysisProvider(
        IHttpClientFactory httpClientFactory,
        AdvancedAnalysisOptions options,
        string? apiKey)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (IsExternalProvider)
        {
            _budget = new AdvancedAnalysisExternalBudgetGuard(
                options,
                ProviderKey,
                options.LlmModel);
        }
    }

    public string ProviderKey
    {
        get
        {
            var configured = string.IsNullOrWhiteSpace(_options.ProviderKey)
                ? NormalizeProvider(_options.Provider)
                : _options.ProviderKey.Trim();
            return configured;
        }
    }

    public string ModelId
        => (_options.LlmModel ?? string.Empty).Trim();

    public AdvancedAnalysisProviderLocation Location =>
        NormalizeLocation(_options.LlmLocation) switch
        {
            "external-service" => AdvancedAnalysisProviderLocation.ExternalService,
            _ => AdvancedAnalysisProviderLocation.Internal
        };

    private bool IsOpenAiDev => NormalizeProvider(_options.Provider) == "openai-dev";

    private bool IsExternalProvider =>
        Location == AdvancedAnalysisProviderLocation.ExternalService;

    public async Task<AdvancedAnalysisProviderResult> ExecuteAsync(
        AdvancedAnalysisProviderRequest request,
        IAdvancedAnalysisToolGateway tools,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        var completions = new List<CompletionResult>(2);
        var evidenceGroups = new List<
            IReadOnlyList<AdvancedAnalysisResolvedEvidence>>();
        var retrievalQueriesByEvidenceId = new Dictionary<
            string,
            HashSet<string>>(StringComparer.Ordinal);
        if (request.Evidence.Count > 0)
            evidenceGroups.Add(request.Evidence);
        try
        {
            var planner = await CompleteJsonAsync(
                    request.JobId,
                    "planner",
                    BuildPlannerSystemPrompt(),
                    BuildPlannerUserPrompt(request),
                    Math.Clamp(_options.PlannerMaxTokens, 256, 4_096),
                    cancellationToken)
                .ConfigureAwait(false);
            completions.Add(planner);
            var plannedQueries = AddRequiredDocumentQueries(
                request,
                ParsePlan(planner.Content, request));
            var previouslyExecuted = request.Handoff.ResearchState.ExecutedQueries
                .Concat(request.PreviousToolEvents.Select(static item =>
                    item.Request.Query))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var planned in plannedQueries)
            {
                if (!previouslyExecuted.Add(planned.Query))
                    continue;
                var observation = await tools.SearchAsync(
                        planned,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (observation.Evidence.Count > 0)
                {
                    evidenceGroups.Add(OrderEvidenceForQuery(
                        planned.Query,
                        planned.DocumentHint,
                        observation.Evidence));
                    foreach (var item in observation.Evidence)
                    {
                        var evidenceId = item.Reference.EvidenceId?.Trim();
                        if (string.IsNullOrWhiteSpace(evidenceId))
                            continue;
                        if (!retrievalQueriesByEvidenceId.TryGetValue(
                                evidenceId,
                                out var retrievalQueries))
                        {
                            retrievalQueries = new HashSet<string>(
                                StringComparer.OrdinalIgnoreCase);
                            retrievalQueriesByEvidenceId.Add(
                                evidenceId,
                                retrievalQueries);
                        }
                        retrievalQueries.Add(planned.Query);
                    }
                }
            }

            var evidence = FilterEvidenceToRequestedDocumentSet(
                request,
                OrderEvidenceForPrompt(
                    tools.Evidence,
                    evidenceGroups));
            if (evidence.Count == 0)
            {
                return WithMetrics(
                    new AdvancedAnalysisProviderResult
                    {
                        Outcome = "insufficient_documentation",
                        AnswerText = BuildNoEvidenceAnswer(request)
                    },
                    completions);
            }

            var writer = await CompleteJsonAsync(
                    request.JobId,
                    "writer",
                    BuildWriterSystemPrompt(request),
                    BuildWriterUserPrompt(
                        request,
                        evidence,
                        retrievalQueriesByEvidenceId),
                    Math.Clamp(_options.WriterMaxTokens, 512, 16_384),
                    cancellationToken)
                .ConfigureAwait(false);
            completions.Add(writer);
            AdvancedAnalysisProviderResult parsed;
            try
            {
                parsed = ParseResult(writer.Content, evidence, request);
            }
            catch (AdvancedAnalysisProviderException ex) when (
                ex.ErrorCode is "advanced_writer_claim_markers_invalid"
                    or "advanced_writer_protocol_invalid")
            {
                var repair = await CompleteJsonAsync(
                        request.JobId,
                        "writer-repair",
                        BuildWriterRepairSystemPrompt(),
                        BuildWriterRepairUserPrompt(
                            request,
                            writer.Content,
                            evidence),
                        Math.Clamp(_options.WriterMaxTokens, 512, 16_384),
                        cancellationToken)
                    .ConfigureAwait(false);
                completions.Add(repair);
                parsed = ParseResult(repair.Content, evidence, request);
            }
            return WithMetrics(
                parsed,
                completions);
        }
        finally
        {
            _budget?.EndJob(request.JobId);
        }
    }

    private void ValidateConfiguration()
    {
        var provider = NormalizeProvider(_options.Provider);
        var location = NormalizeLocation(_options.LlmLocation);
        if (location is not ("internal" or "external-service"))
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_llm_location_invalid");
        }
        if (((provider is "openai-dev" or "runpod-bench")
                && location != "external-service")
            || (provider == "customer-server" && location != "internal"))
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_llm_profile_location_mismatch");
        }
        if (ProviderKey.Length > 100)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_llm_provider_key_invalid");
        }
        if (!Uri.TryCreate(_options.LlmBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_llm_endpoint_invalid");
        }
        if (IsExternalProvider && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_external_llm_requires_https");
        }
        if (string.IsNullOrWhiteSpace(_options.LlmModel))
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_llm_model_missing");
        }
        if (_options.LlmModel.Trim().Length > 256)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_llm_model_invalid");
        }
        if (IsExternalProvider && _apiKey is null)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_external_llm_api_key_missing");
        }
    }

    private async Task<CompletionResult> CompleteJsonAsync(
        Guid jobId,
        string role,
        string systemPrompt,
        string userPrompt,
        int maximumOutputTokens,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _options.LlmModel.Trim(),
            [IsOpenAiDev ? "max_completion_tokens" : "max_tokens"] = maximumOutputTokens,
            ["response_format"] = new { type = "json_object" },
            ["stream"] = false,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };
        if (IsOpenAiDev)
        {
            if (!string.IsNullOrWhiteSpace(_options.ReasoningEffort))
                payload["reasoning_effort"] = _options.ReasoningEffort.Trim();
        }
        else
        {
            payload["temperature"] = 0;
        }
        AdvancedAnalysisExternalBudgetGuard.Reservation? reservation = null;
        string? rejectedErrorCode = null;
        AdvancedAnalysisRateLimitTelemetry? responseRateLimit = null;
        var stopwatch = Stopwatch.StartNew();
        var httpAttemptCount = 0;
        if (_budget is not null)
        {
            reservation = _budget.Reserve(
                jobId,
                role,
                systemPrompt.Length + userPrompt.Length,
                maximumOutputTokens);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                Math.Clamp(_options.LlmTimeoutSeconds, 5, 3_600)));
            var maximumHttpAttempts = Math.Clamp(
                _options.LlmMaximumHttpAttempts,
                1,
                5);
            HttpResponseMessage? response = null;
            for (var attempt = 1; attempt <= maximumHttpAttempts; attempt++)
            {
                httpAttemptCount = attempt;
                using var message = new HttpRequestMessage(
                    HttpMethod.Post,
                    ResolveChatCompletionsUri())
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload, JsonOptions),
                        Encoding.UTF8,
                        "application/json")
                };
                if (_apiKey is not null)
                {
                    message.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", _apiKey);
                }
                try
                {
                    response = await _httpClientFactory
                        .CreateClient(HttpClientName)
                        .SendAsync(
                            message,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested)
                {
                    throw new AdvancedAnalysisProviderException(
                        "advanced_llm_timeout");
                }
                catch (HttpRequestException)
                {
                    throw new AdvancedAnalysisProviderException(
                        "advanced_llm_transport_error");
                }

                if ((int)response.StatusCode != 429
                    || attempt >= maximumHttpAttempts
                    || !TryResolveRateLimitRetryDelay(
                        response,
                        attempt,
                        out var retryDelay))
                {
                    break;
                }
                response.Dispose();
                response = null;
                await Task.Delay(retryDelay, timeout.Token)
                    .ConfigureAwait(false);
            }

            if (response is null)
                throw new AdvancedAnalysisProviderException(
                    "advanced_llm_transport_error");
            using (response)
            {
                responseRateLimit = ReadRateLimitTelemetry(response);
                if (!response.IsSuccessStatusCode)
                {
                    rejectedErrorCode =
                        $"advanced_llm_http_{(int)response.StatusCode}";
                    throw new AdvancedAnalysisProviderException(
                        rejectedErrorCode,
                        isRetryable: (int)response.StatusCode == 429,
                        retryAfterMilliseconds:
                            responseRateLimit?.RetryAfterMilliseconds);
                }

                try
                {
                    await using var body = await response.Content
                        .ReadAsStreamAsync(timeout.Token)
                        .ConfigureAwait(false);
                    using var document = await JsonDocument.ParseAsync(
                            body,
                            cancellationToken: timeout.Token)
                        .ConfigureAwait(false);
                    var usage = ReadUsage(document.RootElement);
                    var observedModelId = ReadObservedModelId(
                        document.RootElement);
                    if (!document.RootElement.TryGetProperty(
                            "choices",
                            out var choices)
                        || choices.ValueKind != JsonValueKind.Array
                        || choices.GetArrayLength() == 0
                        || !choices[0].TryGetProperty("message", out var answer)
                        || !answer.TryGetProperty("content", out var content)
                        || content.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(content.GetString()))
                    {
                        if (reservation is not null)
                        {
                            _budget!.Fail(
                                reservation,
                                "advanced_llm_content_missing",
                                usage,
                                stopwatch.ElapsedMilliseconds,
                                httpAttemptCount);
                            reservation = null;
                        }
                        throw new AdvancedAnalysisProviderException(
                            "advanced_llm_content_missing");
                    }
                    AdvancedAnalysisBudgetCharge? charge = null;
                    if (reservation is not null)
                    {
                        try
                        {
                            charge = _budget!.Complete(
                                reservation,
                                usage,
                                stopwatch.ElapsedMilliseconds,
                                httpAttemptCount,
                                observedModelId,
                                responseRateLimit);
                        }
                        finally
                        {
                            reservation = null;
                        }
                    }
                    return new CompletionResult(
                        content.GetString()!.Trim(),
                        usage,
                        charge?.CostUsd);
                }
                catch (JsonException)
                {
                    throw new AdvancedAnalysisProviderException(
                        "advanced_llm_response_invalid");
                }
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            if (reservation is not null)
            {
                _budget!.Fail(
                    reservation,
                    "advanced_llm_timeout",
                    stopwatch.ElapsedMilliseconds,
                    httpAttemptCount);
            }
            throw new AdvancedAnalysisProviderException(
                "advanced_llm_timeout");
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            if (reservation is not null)
            {
                _budget!.Fail(
                    reservation,
                    "advanced_llm_canceled",
                    stopwatch.ElapsedMilliseconds,
                    httpAttemptCount);
            }
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            if (reservation is not null)
            {
                _budget!.Fail(
                    reservation,
                    "advanced_llm_transport_error",
                    stopwatch.ElapsedMilliseconds,
                    httpAttemptCount);
            }
            throw new AdvancedAnalysisProviderException(
                "advanced_llm_transport_error");
        }
        catch (AdvancedAnalysisProviderException ex)
        {
            if (reservation is not null)
            {
                if (rejectedErrorCode is not null)
                    _budget!.Reject(
                        reservation,
                        rejectedErrorCode,
                        stopwatch.ElapsedMilliseconds,
                        httpAttemptCount,
                        responseRateLimit);
                else
                    _budget!.Fail(
                        reservation,
                        ex.ErrorCode,
                        stopwatch.ElapsedMilliseconds,
                        httpAttemptCount);
            }
            throw;
        }
        catch
        {
            if (reservation is not null)
            {
                _budget!.Fail(
                    reservation,
                    "advanced_llm_call_failed",
                    stopwatch.ElapsedMilliseconds,
                    httpAttemptCount);
            }
            throw;
        }
    }

    private static string? ReadObservedModelId(JsonElement root)
    {
        if (!root.TryGetProperty("model", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var model = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(model) || model.Length > 256
            ? null
            : model;
    }

    private static AdvancedAnalysisRateLimitTelemetry ReadRateLimitTelemetry(
        HttpResponseMessage response)
        => new(
            ReadHeader(response, "x-request-id", 512),
            ReadNonNegativeLongHeader(response, "x-ratelimit-limit-requests"),
            ReadNonNegativeLongHeader(response, "x-ratelimit-remaining-requests"),
            ReadHeader(response, "x-ratelimit-reset-requests", 64),
            ReadNonNegativeLongHeader(response, "x-ratelimit-limit-tokens"),
            ReadNonNegativeLongHeader(response, "x-ratelimit-remaining-tokens"),
            ReadHeader(response, "x-ratelimit-reset-tokens", 64),
            ReadRetryAfterMilliseconds(response));

    private static string? ReadHeader(
        HttpResponseMessage response,
        string name,
        int maximumLength)
    {
        if (!response.Headers.TryGetValues(name, out var values))
            return null;
        var value = values.FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Length > maximumLength
            ? null
            : value;
    }

    private static long? ReadNonNegativeLongHeader(
        HttpResponseMessage response,
        string name)
        => long.TryParse(
                ReadHeader(response, name, 32),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
           && value >= 0
            ? value
            : null;

    private static long? ReadRetryAfterMilliseconds(
        HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var milliseconds = retryAfter?.Delta?.TotalMilliseconds
                           ?? (retryAfter?.Date - DateTimeOffset.UtcNow)
                           ?.TotalMilliseconds;
        return milliseconds is >= 0 and <= long.MaxValue
            ? (long)Math.Ceiling(milliseconds.Value)
            : null;
    }

    private bool TryResolveRateLimitRetryDelay(
        HttpResponseMessage response,
        int attempt,
        out TimeSpan delay)
    {
        var maximumMilliseconds = Math.Clamp(
            _options.LlmMaximumRetryDelayMilliseconds,
            100,
            300_000);
        var retryAfter = response.Headers.RetryAfter;
        var serverDelayMilliseconds = retryAfter?.Delta?.TotalMilliseconds
            ?? (retryAfter?.Date - DateTimeOffset.UtcNow)?.TotalMilliseconds;
        if (serverDelayMilliseconds is > 0
            && serverDelayMilliseconds > maximumMilliseconds)
        {
            delay = default;
            return false;
        }

        var milliseconds = serverDelayMilliseconds
            ?? Math.Clamp(
                _options.LlmRetryBaseDelayMilliseconds,
                100,
                maximumMilliseconds)
            * Math.Pow(2, Math.Max(0, attempt - 1));
        delay = TimeSpan.FromMilliseconds(Math.Clamp(
            milliseconds,
            100,
            maximumMilliseconds));
        return true;
    }

    private Uri ResolveChatCompletionsUri()
    {
        var baseUrl = _options.LlmBaseUrl.Trim().TrimEnd('/');
        var suffix = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? "/chat/completions"
            : "/v1/chat/completions";
        return new Uri(baseUrl + suffix, UriKind.Absolute);
    }

    private IReadOnlyList<AdvancedAnalysisSearchRequest> ParsePlan(
        string raw,
        AdvancedAnalysisProviderRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            if (!document.RootElement.TryGetProperty("queries", out var queries)
                || queries.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException();
            }

            var maximumQueries = ResolveMaximumPlanQueries(request);
            var result = new List<AdvancedAnalysisSearchRequest>();
            var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowedCategories = BuildAllowedPlannerCategories(request);
            foreach (var item in queries.EnumerateArray())
            {
                if (result.Count >= maximumQueries
                    || item.ValueKind != JsonValueKind.Object)
                {
                    break;
                }
                var query = NormalizeCorpusSearchQuery(
                    ReadString(item, "query"));
                if (string.IsNullOrWhiteSpace(query)
                    || query.Length > 8_000
                    || !dedupe.Add(query))
                {
                    continue;
                }
                var category = ReadString(item, "category");
                if (string.IsNullOrWhiteSpace(category)
                    || !allowedCategories.Contains(category))
                {
                    category = string.Empty;
                }
                var topK = item.TryGetProperty("topK", out var topKValue)
                           && topKValue.TryGetInt32(out var parsedTopK)
                    ? Math.Clamp(parsedTopK, 1, 60)
                    : 20;
                result.Add(new AdvancedAnalysisSearchRequest(
                    query,
                    string.IsNullOrWhiteSpace(category) ? null : category,
                    topK,
                    MaxPerDocument: request.Handoff.Load.StructuredLayout
                        ? 8
                        : null,
                    MaxPerPage: request.Handoff.Load.StructuredLayout
                        ? 2
                        : null));
            }

            if (result.Count == 0)
            {
                result.Add(new AdvancedAnalysisSearchRequest(
                    request.Handoff.RequestText,
                    Category: null,
                    TopK: Math.Clamp(
                        Math.Max(12, request.Handoff.Load.AtomicEvidenceCount),
                        1,
                        60)));
            }
            return result;
        }
        catch (JsonException)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_planner_protocol_invalid");
        }
    }

    private static HashSet<string> BuildAllowedPlannerCategories(
        AdvancedAnalysisProviderRequest request)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in request.Handoff.Load.CandidateScopePaths)
        {
            var normalized = (rawPath ?? string.Empty)
                .Trim()
                .Replace('\\', '/')
                .Trim('/');
            if (normalized.Length == 0)
                continue;
            var separator = normalized.IndexOf('/');
            var firstSegment = separator < 0
                ? normalized
                : normalized[..separator];
            if (firstSegment.Length > 0
                && !firstSegment.Contains('.', StringComparison.Ordinal))
            {
                allowed.Add(firstSegment);
            }
        }
        return allowed;
    }

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence>
        OrderEvidenceForPrompt(
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> allEvidence,
            IReadOnlyList<IReadOnlyList<AdvancedAnalysisResolvedEvidence>> groups)
    {
        var ordered = new List<AdvancedAnalysisResolvedEvidence>(
            allEvidence.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maximumGroupSize = groups.Count == 0
            ? 0
            : groups.Max(static group => group.Count);
        for (var index = 0; index < maximumGroupSize; index++)
        {
            foreach (var group in groups)
            {
                if (index >= group.Count)
                    continue;
                var item = group[index];
                var evidenceId = item.Reference.EvidenceId?.Trim();
                if (!string.IsNullOrWhiteSpace(evidenceId)
                    && seen.Add(evidenceId))
                {
                    ordered.Add(item);
                }
            }
        }
        foreach (var item in allEvidence)
        {
            var evidenceId = item.Reference.EvidenceId?.Trim();
            if (!string.IsNullOrWhiteSpace(evidenceId)
                && seen.Add(evidenceId))
            {
                ordered.Add(item);
            }
        }
        return ordered;
    }

    internal static IReadOnlyList<AdvancedAnalysisResolvedEvidence>
        OrderEvidenceForQuery(
            string query,
            string? documentHint,
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
    {
        var queryTokens = ExtractEvidenceRankingTokens(query, documentHint);
        if (queryTokens.Count == 0 || evidence.Count < 2)
            return evidence;
        return evidence
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                Score = ScoreEvidenceText(item.Content, queryTokens)
            })
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Item)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractEvidenceRankingTokens(
        string query,
        string? documentHint)
    {
        var documentTokens = Regex.Matches(
                documentHint ?? string.Empty,
                @"[\p{L}\p{N}]+")
            .Select(static match => match.Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        return Regex.Matches(query ?? string.Empty, @"[\p{L}\p{N}]+")
            .Select(static match => match.Value.ToLowerInvariant())
            .Where(static token => token.Length >= 3)
            .Where(static token => !EvidenceRankingStopwords.Contains(token))
            .Where(token => !documentTokens.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static int ScoreEvidenceText(
        string content,
        IReadOnlyList<string> queryTokens)
    {
        if (string.IsNullOrWhiteSpace(content))
            return 0;
        var tokens = Regex.Matches(
                content.ToLowerInvariant(),
                @"[\p{L}\p{N}]+")
            .Select(static match => match.Value)
            .ToArray();
        if (tokens.Length == 0)
            return 0;
        var frequencies = tokens
            .GroupBy(static token => token, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
        var matched = 0;
        var repeated = 0;
        foreach (var token in queryTokens)
        {
            if (!frequencies.TryGetValue(token, out var count))
                continue;
            matched++;
            repeated += Math.Min(count, 3);
        }
        return checked(matched * 100 + repeated * 10);
    }

    private static readonly HashSet<string> EvidenceRankingStopwords = new(
        [
            "avec", "dans", "pour", "sans", "sous", "entre", "depuis",
            "cela", "cette", "celui", "celle", "ceux", "elles", "leurs",
            "quel", "quelle", "quels", "quelles", "dont", "quoi", "être",
            "avoir", "faire", "dire", "exige", "exiger", "compare",
            "comparaison", "document", "norme", "standard", "from", "with",
            "into", "that", "this", "these", "those", "what", "which",
            "where", "when", "have", "does", "must", "documented"
        ],
        StringComparer.Ordinal);

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence>
        FilterEvidenceToRequestedDocumentSet(
            AdvancedAnalysisProviderRequest request,
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
    {
        var load = request.Handoff.Load;
        var isExplicitDocumentSet = load.BoundedNamedDocumentExtraction
                                    || !string.IsNullOrWhiteSpace(
                                        load.RequestedDocumentName)
                                    || (load.SelectionPolicy.Contains(
                                            "explicit",
                                            StringComparison.OrdinalIgnoreCase)
                                        && (load.AtomicEvidenceType.Contains(
                                                "compar",
                                                StringComparison.OrdinalIgnoreCase)
                                            || load.PlanKind.Contains(
                                                "compar",
                                                StringComparison.OrdinalIgnoreCase)));
        if (!isExplicitDocumentSet)
            return evidence;

        var requestedDocuments = GetRequestedDocumentIdentifiers(request)
            .Select(NormalizeDocumentIdentifier)
            .ToArray();
        if (requestedDocuments.Length == 0)
            return evidence;

        return evidence.Where(item =>
        {
            var fileName = NormalizeDocumentIdentifier(
                item.Reference.FileName
                ?? item.Reference.DocPath
                ?? string.Empty);
            return requestedDocuments.Any(requested =>
                fileName.Contains(requested, StringComparison.Ordinal)
                || requested.Contains(fileName, StringComparison.Ordinal));
        }).ToArray();
    }

    private IReadOnlyList<AdvancedAnalysisSearchRequest>
        AddRequiredDocumentQueries(
            AdvancedAnalysisProviderRequest request,
            IReadOnlyList<AdvancedAnalysisSearchRequest> plannedQueries)
    {
        var load = request.Handoff.Load;
        var documentScoped = load.BoundedNamedDocumentExtraction
                             || !string.IsNullOrWhiteSpace(
                                 load.RequestedDocumentName)
                             || load.SelectionPolicy.Contains(
                                 "explicit",
                                 StringComparison.OrdinalIgnoreCase);
        if (!documentScoped)
            return plannedQueries;

        var documents = GetRequestedDocumentIdentifiers(request);
        if (documents.Count == 0)
            return plannedQueries;

        var maximumQueries = ResolveMaximumPlanQueries(request);
        var result = new List<AdvancedAnalysisSearchRequest>(maximumQueries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            var context = BuildDocumentQueryContext(
                request.Handoff.RequestText,
                document,
                documents);
            var query = NormalizeCorpusSearchQuery(
                string.Join(' ', document, context));
            if (query.Length == 0 || !seen.Add(query))
                continue;
            result.Add(new AdvancedAnalysisSearchRequest(
                query,
                Category: null,
                TopK: Math.Clamp(
                    Math.Max(20, load.AtomicEvidenceCount * 4),
                    1,
                    60),
                DocumentHint: document));
            if (result.Count >= maximumQueries)
                return result;
        }
        foreach (var planned in plannedQueries)
        {
            var documentHint = ResolvePlannedDocumentHint(
                planned.Query,
                documents);
            var scopedPlanned = documentHint is null
                ? planned
                : planned with { DocumentHint = documentHint };
            if (!seen.Add(scopedPlanned.Query))
                continue;
            result.Add(scopedPlanned);
            if (result.Count >= maximumQueries)
                break;
        }
        return result;
    }

    private static string? ResolvePlannedDocumentHint(
        string query,
        IReadOnlyList<string> documents)
    {
        if (documents.Count == 1)
            return documents[0];

        var normalizedQuery = NormalizeDocumentIdentifier(query);
        var matches = documents
            .Where(document => PlannedQueryIdentifiesDocument(
                normalizedQuery,
                query,
                document))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool PlannedQueryIdentifiesDocument(
        string normalizedQuery,
        string rawQuery,
        string document)
    {
        var normalizedDocument = NormalizeDocumentIdentifier(document);
        if (normalizedDocument.Length >= 6
            && normalizedQuery.Contains(
                normalizedDocument,
                StringComparison.Ordinal))
        {
            return true;
        }

        var queryTokens = Regex.Matches(rawQuery ?? string.Empty, @"[\p{L}\p{N}]+")
            .Select(static match => match.Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var identifierTokens = Regex.Matches(
                document ?? string.Empty,
                @"[\p{L}\p{N}]+")
            .Select(static match => match.Value.ToLowerInvariant())
            .Where(static token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (identifierTokens.Length < 2)
            return false;
        var matched = identifierTokens.Count(queryTokens.Contains);
        return matched >= Math.Min(3, identifierTokens.Length);
    }

    private static string BuildDocumentQueryContext(
        string requestText,
        string document,
        IReadOnlyList<string> allDocuments)
    {
        var source = requestText ?? string.Empty;
        var start = source.IndexOf(document, StringComparison.OrdinalIgnoreCase);
        var segment = source;
        if (start >= 0)
        {
            var end = source.Length;
            foreach (var other in allDocuments)
            {
                if (string.Equals(
                        other,
                        document,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var candidate = source.IndexOf(
                    other,
                    start + document.Length,
                    StringComparison.OrdinalIgnoreCase);
                if (candidate >= 0 && candidate < end)
                    end = candidate;
            }
            segment = source[start..end];
        }
        foreach (var candidate in allDocuments)
        {
            segment = Regex.Replace(
                segment,
                Regex.Escape(candidate),
                " ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return NormalizeCorpusSearchQuery(segment);
    }

    private static IReadOnlyList<string> GetRequestedDocumentIdentifiers(
        AdvancedAnalysisProviderRequest request)
    {
        var candidates = (string.IsNullOrWhiteSpace(
                request.Handoff.Load.RequestedDocumentName)
                ? Array.Empty<string>()
                : [request.Handoff.Load.RequestedDocumentName])
            .Concat(ExtractDocumentIdentifiers(request.Handoff.RequestText));
        var documents = new List<string>();
        foreach (var raw in candidates)
        {
            var value = raw.Trim();
            var normalized = NormalizeDocumentIdentifier(value);
            if (normalized.Length < 6)
                continue;
            if (documents.Any(existing =>
            {
                var existingNormalized = NormalizeDocumentIdentifier(existing);
                return normalized == existingNormalized
                       || normalized.EndsWith(
                           existingNormalized,
                           StringComparison.Ordinal)
                       || existingNormalized.EndsWith(
                           normalized,
                           StringComparison.Ordinal);
            }))
            {
                continue;
            }
            documents.Add(value);
        }
        return documents;
    }

    private static IEnumerable<string> ExtractDocumentIdentifiers(string text)
    {
        const string formalIdentifierPattern =
            @"\b(?:[A-Z]{2,8}[\s/]+){1,4}\d{2,6}(?:[-:/.]\d{1,6})*(?:\s+(?:19|20)\d{2})?\b";
        const string fileReferencePattern =
            @"(?:^|[\s""'(])(?<file>[\p{L}\p{N}_()+&.,'’\-]+(?:\s+[\p{L}\p{N}_()+&.,'’\-]+){0,12}\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv))(?=$|[\s""'),;])";
        var source = text ?? string.Empty;
        return Regex.Matches(
                source,
                formalIdentifierPattern,
                RegexOptions.CultureInvariant)
            .Select(static match => match.Value.Trim())
            .Concat(Regex.Matches(
                    source,
                    fileReferencePattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(static match => match.Groups["file"].Value.Trim()));
    }

    private static string NormalizeDocumentIdentifier(string value)
        => Regex.Replace(
            value ?? string.Empty,
            @"[^\p{L}\p{N}]",
            string.Empty,
            RegexOptions.CultureInvariant).ToLowerInvariant();

    private int ResolveMaximumPlanQueries(
        AdvancedAnalysisProviderRequest request)
    {
        var configured = Math.Clamp(_options.MaximumPlanQueries, 1, 32);
        var load = request.Handoff.Load;
        if (GetRequestedDocumentIdentifiers(request).Count >= 2)
            return Math.Min(configured, 4);
        if (load.BoundedNamedDocumentExtraction)
            return Math.Min(configured, 2);
        if (load.StructuredLayout && load.ColumnCount > 0)
            return Math.Min(configured, Math.Clamp(load.ColumnCount, 2, 6));
        if (load.PlanKind.Contains("comparison", StringComparison.OrdinalIgnoreCase))
            return Math.Min(configured, 4);
        return Math.Min(configured, 6);
    }

    private static string NormalizeCorpusSearchQuery(string raw)
    {
        var query = Regex.Replace(
            raw ?? string.Empty,
            @"(?i)\bsite:\S+",
            " ");
        query = Regex.Replace(query, @"(?i)\bhttps?://\S+", " ");
        query = Regex.Replace(query, @"(?i)\bwww\.\S+", " ");
        query = Regex.Replace(query, @"\s+", " ").Trim();
        return query.Trim(' ', ',', ';', ':', '-', '|');
    }

    private static AdvancedAnalysisProviderResult ParseResult(
        string raw,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        AdvancedAnalysisProviderRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            var root = document.RootElement;
            var outcome = ReadString(root, "outcome");
            var answerText = ReadString(root, "answerText");
            if (outcome is not (
                    "answered"
                    or "insufficient_documentation"
                    or "clarification_required")
                || string.IsNullOrWhiteSpace(answerText))
            {
                throw new JsonException();
            }

            var knownEvidence = evidence
                .Select(static item => item.Reference.EvidenceId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            var claims = new List<AdvancedAnalysisResultClaim>();
            if (root.TryGetProperty("claims", out var rawClaims)
                && rawClaims.ValueKind == JsonValueKind.Array)
            {
                foreach (var rawClaim in rawClaims.EnumerateArray())
                {
                    var claimId = ReadString(rawClaim, "claimId");
                    var text = ReadString(rawClaim, "text");
                    if (string.IsNullOrWhiteSpace(claimId)
                        || string.IsNullOrWhiteSpace(text)
                        || !rawClaim.TryGetProperty(
                            "evidenceIds",
                            out var rawEvidenceIds)
                        || rawEvidenceIds.ValueKind != JsonValueKind.Array)
                    {
                        throw new JsonException();
                    }
                    var evidenceIds = rawEvidenceIds
                        .EnumerateArray()
                        .Where(static item =>
                            item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString()?.Trim())
                        .Where(static id => !string.IsNullOrWhiteSpace(id))
                        .Select(static id => id!)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (evidenceIds.Count == 0
                        || evidenceIds.Any(id => !knownEvidence.Contains(id)))
                    {
                        throw new AdvancedAnalysisProviderException(
                            "advanced_writer_evidence_id_invalid");
                    }
                    claims.Add(new AdvancedAnalysisResultClaim
                    {
                        ClaimId = claimId,
                        Text = text,
                        EvidenceIds = evidenceIds
                    });
                }
            }
            if (outcome == "answered" && claims.Count == 0)
                throw new JsonException();
            if (outcome == "answered"
                && request.Handoff.Load.AnswerUnitCount > 0
                && claims.Count != request.Handoff.Load.AnswerUnitCount)
            {
                throw new AdvancedAnalysisProviderException(
                    "advanced_writer_claim_count_invalid");
            }
            if (outcome == "answered"
                && claims
                    .Select(static claim => NormalizeClaimText(claim.Text))
                    .GroupBy(static text => text, StringComparer.Ordinal)
                    .Any(static group => group.Count() > 1))
            {
                throw new AdvancedAnalysisProviderException(
                    "advanced_writer_duplicate_claims");
            }
            if (outcome == "answered"
                && claims.Any(claim => Regex.Matches(
                        answerText,
                        Regex.Escape($"[{claim.ClaimId}]"),
                        RegexOptions.CultureInvariant).Count != 1))
            {
                throw new AdvancedAnalysisProviderException(
                    "advanced_writer_claim_markers_invalid");
            }

            return new AdvancedAnalysisProviderResult
            {
                Outcome = outcome,
                AnswerText = answerText,
                Claims = claims
            };
        }
        catch (JsonException)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_writer_protocol_invalid");
        }
    }

    private static string NormalizeClaimText(string value)
        => Regex.Replace(
                value ?? string.Empty,
                @"[^\p{L}\p{N}]+",
                " ",
                RegexOptions.CultureInvariant)
            .Trim()
            .ToLowerInvariant();

    private AdvancedAnalysisProviderResult WithMetrics(
        AdvancedAnalysisProviderResult result,
        IReadOnlyList<CompletionResult> completions)
        => new()
        {
            Outcome = result.Outcome,
            AnswerText = result.AnswerText,
            Claims = result.Claims,
            ModelId = _options.LlmModel.Trim(),
            ProviderCallCount = completions.Count,
            InputTokens = SumKnownUsage(
                completions.Select(static item => item.Usage.InputTokens)),
            OutputTokens = SumKnownUsage(
                completions.Select(static item => item.Usage.OutputTokens)),
            CachedInputTokens = SumKnownUsage(
                completions.Select(static item => item.Usage.CachedInputTokens)),
            EstimatedCostUsd = completions.Any(static item =>
                    item.EstimatedCostUsd.HasValue)
                ? completions.Sum(static item => item.EstimatedCostUsd ?? 0m)
                : null
        };

    private static int? SumKnownUsage(IEnumerable<int?> values)
    {
        var known = values.Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();
        return known.Length == 0 ? null : known.Sum();
    }

    private static AdvancedAnalysisLlmUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return new AdvancedAnalysisLlmUsage(null, null, null);
        }
        var input = ReadNonNegativeInt(usage, "prompt_tokens")
                    ?? ReadNonNegativeInt(usage, "input_tokens");
        var output = ReadNonNegativeInt(usage, "completion_tokens")
                     ?? ReadNonNegativeInt(usage, "output_tokens");
        int? cached = null;
        if (usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            cached = ReadNonNegativeInt(details, "cached_tokens");
        }
        return new AdvancedAnalysisLlmUsage(input, output, cached);
    }

    private static int? ReadNonNegativeInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.TryGetInt32(out var parsed)
           && parsed >= 0
            ? parsed
            : null;

    private static string BuildPlannerSystemPrompt()
        => """
           You are the research planner for SAAIA advanced analysis. Return one
           JSON object and no prose. Every query is executed only inside the
           private SAAIA document corpus; internet and web search are unavailable.
           Never use site:, a URL, a domain name or outside-source wording. Never
           answer the user. Prefer complementary corpus queries that can cover
           every requested output unit. For a repeated grid, plan searches for
           concrete candidates in each semantic column or item family. Search
           for the content that will fill the cells, not instructions or blank
           templates for producing the requested deliverable. Omit row labels,
           weekdays and schedule/planning terms when they do not describe the
           needed content itself. Use compact retrieval phrases with useful
           synonyms. When a semantic column has common alternate terminology,
           use complementary queries for those variants within the query limit;
           do not rely on the user's single label to cover the corpus vocabulary.
           Do not add health, diet, price, speed or other constraints
           that the user did not request. For a list of named candidates, search
           for names, headings, indexes or examples; do not append generic words
           such as ingredients or preparation because they rank fragments whose
           item name may be outside the chunk. Keep category empty unless the input contains an exact
           candidate scope category; never invent or infer a category name.
           When the user explicitly names documents, emit at least one focused
           query per document. Retain that document's complete identifier in the
           query and add only the subject terms needed to answer the request.
           Shape: {"queries":[{"query":"...","category":"... or empty","topK":20}]}.
           """;

    private string BuildPlannerUserPrompt(
        AdvancedAnalysisProviderRequest request)
        => JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            load = BuildPromptLoad(request.Handoff.Load),
            priorQueries = request.Handoff.ResearchState.ExecutedQueries
                .Concat(request.PreviousToolEvents.Select(static item =>
                    item.Request.Query))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            revalidatedEvidenceCount = request.Evidence.Count,
            maximumQueries = ResolveMaximumPlanQueries(request)
        }, JsonOptions);

    private string BuildWriterSystemPrompt(
        AdvancedAnalysisProviderRequest request)
        => """
           You are the SAAIA advanced-analysis Writer. Use only the supplied
           revalidated evidence. Return one JSON object and no prose with shape
           {"outcome":"answered|insufficient_documentation|clarification_required",
           "answerText":"...","claims":[{"claimId":"C1","text":"...",
           "evidenceIds":["E1"]}]}. Every factual answer unit must have a claim
           backed by one or more supplied evidenceIds. Do not invent a value,
           title, procedure or source. Keep the user's requested language and
           format. In answerText, append [claimId] directly to the factual unit
           it supports and use every claimId exactly once. For a synthesis or
           grid, distinguish neutral presentation coordinates from semantic
           relationships. Neutral coordinates such as weekdays, sequence numbers
           or arbitrary ordering may organize supported candidates without a source
           prescribing that coordinate, unless the user explicitly asks for the
           source's schedule or ordering. A semantic column, role, category or
           constraint such as breakfast suitability is part of the factual unit:
           state it in the claim and cite evidence that supports it. Every mandatory
           qualifier in the request must remain explicit and supported; never
           silently drop qualifiers such as audience, simplicity, compatibility or
           intended use. Each evidence item includes an opaque sourceKey. Equal
           sourceKeys mean that the items come from the same canonical document
           revision. A document-level scope statement may support a qualifier for
           a named item listed elsewhere in that same source only when its wording
           clearly applies to the document's item collection; cite both evidence
           items in that claim. Never carry a scope statement across different
           sourceKeys. A source index or heading can support the existence and
           spelling of a named item, and a heading can support a semantic category
           only when it explicitly names that category. It cannot support an
           unstated relationship or absent details about that item. Distinct
           cells may cite the same evidence when it documents several distinct
           candidates. When the request requires distinct units, every claim must
           describe a distinct concrete unit; do not repeat generic guidance to
           fill a grid. If the evidence cannot support all mandatory units and
           partial answers are not allowed, choose insufficient_documentation and
           identify the exact rows, columns or item types that remain unsupported.
           """
           + "\nRequested output shape: "
           + JsonSerializer.Serialize(
               BuildPromptLoad(request.Handoff.Load),
               JsonOptions);

    private static string BuildWriterRepairSystemPrompt()
        => """
           Repair one SAAIA Writer JSON object and return only the repaired JSON.
           Preserve the original answer facts, outcome and evidence mappings.
           Do not add a fact or evidence id. For an answered outcome, append each
           [claimId] directly to its factual unit in answerText and use every
           claimId exactly once. If the original object is malformed or truncated,
           recover only information that is explicitly present. Keep the same JSON
           schema.
           """;

    private string BuildWriterRepairUserPrompt(
        AdvancedAnalysisProviderRequest request,
        string originalWriterJson,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
        => JsonSerializer.Serialize(new
        {
            language = request.Handoff.Language,
            load = BuildPromptLoad(request.Handoff.Load),
            allowedEvidenceIds = evidence
                .Select(static item => item.Reference.EvidenceId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToArray(),
            originalWriterJson
        }, JsonOptions);

    private string BuildWriterUserPrompt(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyDictionary<string, HashSet<string>> retrievalQueriesByEvidenceId)
    {
        var remaining = Math.Clamp(
            _options.MaximumEvidencePromptCharacters,
            8_000,
            1_000_000);
        const int maximumCharactersPerPromptEvidence = 700;
        var promptEvidence = new List<object>();
        var sourceKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            if (remaining <= 0)
                break;
            var content = item.Content ?? string.Empty;
            if (content.Length > maximumCharactersPerPromptEvidence)
                content = content[..maximumCharactersPerPromptEvidence];
            if (content.Length > remaining)
                content = content[..remaining];
            remaining -= content.Length;
            var evidenceId = item.Reference.EvidenceId;
            var sourceIdentity = string.Join(
                "|",
                item.Reference.DocId ?? string.Empty,
                item.Reference.RevisionId ?? string.Empty,
                item.Reference.SourceHash ?? string.Empty);
            if (sourceIdentity == "||")
                sourceIdentity = "evidence:" + (evidenceId ?? sourceKeys.Count.ToString());
            if (!sourceKeys.TryGetValue(sourceIdentity, out var sourceKey))
            {
                sourceKey = $"S{sourceKeys.Count + 1}";
                sourceKeys.Add(sourceIdentity, sourceKey);
            }
            promptEvidence.Add(new
            {
                evidenceId,
                sourceKey,
                retrievedFor = evidenceId is not null
                               && retrievalQueriesByEvidenceId.TryGetValue(
                                   evidenceId,
                                   out var queries)
                    ? queries.Order(StringComparer.OrdinalIgnoreCase).ToArray()
                    : [],
                content
            });
        }
        return JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            allowsPartialAnswer = false,
            load = BuildPromptLoad(request.Handoff.Load),
            evidence = promptEvidence
        }, JsonOptions);
    }

    private object BuildPromptLoad(AdvancedAnalysisLoadDescriptor load)
        => new
        {
            load.PlanKind,
            load.Deliverable,
            load.AnswerUnitCount,
            load.AtomicEvidenceCount,
            load.RowCount,
            load.ColumnCount,
            load.StructuredLayout,
            load.AtomicEvidenceType,
            load.AtomicEvidenceMode,
            load.SelectionPolicy,
            load.QuestionFocus,
            load.RequestedDocumentName,
            load.BoundedNamedDocumentExtraction,
            candidateScopePaths = IsExternalProvider
                ? Array.Empty<string>()
                : load.CandidateScopePaths.ToArray(),
            load.RowLabels,
            load.Columns
        };

    private static string BuildNoEvidenceAnswer(
        AdvancedAnalysisProviderRequest request)
    {
        var requestedDocument = request.Handoff.Load.RequestedDocumentName
            ?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedDocument))
        {
            return request.Handoff.Language.Trim().ToLowerInvariant() switch
            {
                "en" => $"The available documentation contains no revalidated evidence from \"{requestedDocument}\" that can answer this request.",
                "de" => $"Die verfügbare Dokumentation enthält keine erneut validierten Belege aus \"{requestedDocument}\", die diese Anfrage beantworten können.",
                _ => $"La documentation disponible ne contient pas de preuve revalidée exploitable dans « {requestedDocument} » pour répondre à cette demande."
            };
        }
        return request.Handoff.Language.Trim().ToLowerInvariant() switch
        {
            "en" => "The available documentation does not contain enough revalidated evidence to answer this request.",
            "de" => "Die verfügbare Dokumentation enthält nicht genügend erneut validierte Belege für diese Anfrage.",
            _ => "La documentation disponible ne contient pas assez de preuves revalidées pour répondre à cette demande."
        };
    }

    private static string ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string UnwrapJson(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal))
            return value;
        var firstNewLine = value.IndexOf('\n');
        var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && lastFence > firstNewLine
            ? value[(firstNewLine + 1)..lastFence].Trim()
            : value;
    }

    private static string NormalizeProvider(string? provider)
        => (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "openaidev" or "openai-dev" => "openai-dev",
            "runpodbench" or "runpod-bench" => "runpod-bench",
            "customerserver" or "customer-server" => "customer-server",
            _ => (provider ?? string.Empty).Trim().ToLowerInvariant()
        };

    private static string NormalizeLocation(string? location)
        => (location ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "internal" or "on-prem" or "onprem" => "internal",
            "external" or "external-service" or "cloud" => "external-service",
            _ => (location ?? string.Empty).Trim().ToLowerInvariant()
        };
}
