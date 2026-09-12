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
internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider :
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
        var completions = new List<CompletionResult>(4);
        var runSemanticCritic = _options.SemanticCriticEnabled;
        var evidenceGroups = new List<
            IReadOnlyList<AdvancedAnalysisResolvedEvidence>>();
        var retrievalQueriesByEvidenceId = new Dictionary<
            string,
            HashSet<string>>(StringComparer.Ordinal);
        if (request.Evidence.Count > 0)
            evidenceGroups.Add(request.Evidence);
        try
        {
            var availableCategories = await tools
                .ListCategoriesAsync(cancellationToken)
                .ConfigureAwait(false);
            var planner = await CompleteJsonAsync(
                    request.JobId,
                    "planner",
                    BuildPlannerSystemPrompt(),
                    BuildPlannerUserPrompt(request, availableCategories),
                    Math.Clamp(_options.PlannerMaxTokens, 256, 4_096),
                    cancellationToken)
                .ConfigureAwait(false);
            completions.Add(planner);
            request = ApplyPlannerSelectionMode(request, planner.Content);
            var plannedQueries = AddRequiredDocumentQueries(
                request,
                ParsePlan(planner.Content, request, availableCategories));
            var previouslyExecuted = request.Handoff.ResearchState.ExecutedQueries
                .Concat(request.PreviousToolEvents.Select(static item =>
                    item.Request.Query))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await ExecuteSearchBatchAsync(
                    tools,
                    plannedQueries,
                    previouslyExecuted,
                    evidenceGroups,
                    retrievalQueriesByEvidenceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (_options.AdaptiveResearchEnabled
                && ShouldRunAdaptiveResearch(request)
                && tools.Evidence.Count > 0)
            {
                var maximumCalls = Math.Clamp(
                    _options.ExternalMaximumCallsPerJob,
                    1,
                    1_024);
                var reservedFinalCalls = runSemanticCritic ? 2 : 1;
                var reviewRound = 0;
                while (completions.Count < maximumCalls - reservedFinalCalls)
                {
                    reviewRound++;
                    var currentEvidence = FilterEvidenceToRequestedDocumentSet(
                        request,
                        OrderEvidenceForPrompt(tools.Evidence, evidenceGroups));
                    var researchReview = await CompleteJsonAsync(
                            request.JobId,
                            reviewRound == 1
                                ? "research-review"
                                : $"research-review-{reviewRound}",
                            BuildResearchReviewSystemPrompt(),
                            BuildResearchReviewUserPrompt(
                                request,
                                BuildPromptEvidence(
                                    request,
                                    currentEvidence,
                                    retrievalQueriesByEvidenceId),
                                previouslyExecuted,
                                availableCategories),
                            Math.Clamp(_options.PlannerMaxTokens, 256, 4_096),
                            cancellationToken)
                        .ConfigureAwait(false);
                    completions.Add(researchReview);
                    var followUpQueries = ParseResearchReview(
                        researchReview.Content,
                        request,
                        availableCategories);
                    if (followUpQueries.Count == 0)
                        break;
                    await ExecuteSearchBatchAsync(
                            tools,
                            followUpQueries,
                            previouslyExecuted,
                            evidenceGroups,
                            retrievalQueriesByEvidenceId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!RequiresDistinctStructuredSelection(
                            request.Handoff.Load))
                    {
                        break;
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
                    completions,
                    request.Handoff.Load);
            }

            var promptEvidence = BuildPromptEvidence(
                request,
                evidence,
                retrievalQueriesByEvidenceId);
            var writer = await CompleteJsonAsync(
                    request.JobId,
                    "writer",
                    BuildWriterSystemPrompt(request),
                    BuildWriterUserPrompt(request, promptEvidence),
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
                    or "advanced_writer_protocol_invalid"
                || (ex.ErrorCode == "advanced_writer_duplicate_claims"
                    && request.Handoff.Load.StructuredLayout))
            {
                var repair = await CompleteJsonAsync(
                        request.JobId,
                        "writer-repair",
                        BuildWriterRepairSystemPrompt(),
                        BuildWriterRepairUserPrompt(
                            request,
                            writer.Content,
                            promptEvidence),
                        Math.Clamp(_options.WriterMaxTokens, 512, 16_384),
                        cancellationToken)
                    .ConfigureAwait(false);
                completions.Add(repair);
                parsed = ParseResult(repair.Content, evidence, request);
            }
            parsed = CanonicalizeDistinctSelectedItems(
                request,
                parsed,
                promptEvidence);
            parsed = RebindDistinctSelectedItemsToSupportingEvidence(
                request,
                parsed,
                promptEvidence);
            string? synthesisRecoveryErrorCode = null;
            if (!runSemanticCritic
                && ShouldAttemptSynthesisRecovery(
                    request,
                    parsed,
                    promptEvidence)
                && completions.Count < Math.Clamp(
                    _options.ExternalMaximumCallsPerJob,
                    1,
                    1_024))
            {
                try
                {
                    var recovery = await CompleteJsonAsync(
                            request.JobId,
                            "synthesis-recovery",
                            BuildSynthesisRecoverySystemPrompt(),
                            BuildSynthesisRecoveryUserPrompt(
                                request,
                                parsed,
                                promptEvidence),
                            Math.Clamp(_options.WriterMaxTokens, 512, 16_384),
                            cancellationToken)
                        .ConfigureAwait(false);
                    completions.Add(recovery);
                    parsed = ParseResult(recovery.Content, evidence, request);
                    parsed = CanonicalizeDistinctSelectedItems(
                        request,
                        parsed,
                        promptEvidence);
                    parsed = RebindDistinctSelectedItemsToSupportingEvidence(
                        request,
                        parsed,
                        promptEvidence);
                }
                catch (AdvancedAnalysisProviderException ex)
                {
                    synthesisRecoveryErrorCode = ex.ErrorCode;
                    // Final structural enforcement below converts any unresolved
                    // distinct-selection defect to a safe terminal insufficiency.
                }
            }
            if (runSemanticCritic
                && completions.Count < Math.Clamp(
                    _options.ExternalMaximumCallsPerJob,
                    1,
                    1_024))
            {
                var critic = await CompleteJsonAsync(
                        request.JobId,
                        "critic",
                        BuildCriticSystemPrompt(),
                        BuildCriticUserPrompt(request, parsed, promptEvidence),
                        Math.Clamp(_options.CriticMaxTokens, 512, 16_384),
                        cancellationToken)
                    .ConfigureAwait(false);
                completions.Add(critic);
                try
                {
                    parsed = ParseResult(critic.Content, evidence, request);
                }
                catch (AdvancedAnalysisProviderException)
                {
                    throw new AdvancedAnalysisProviderException(
                        "advanced_critic_protocol_invalid");
                }
                parsed = CanonicalizeDistinctSelectedItems(
                    request,
                    parsed,
                    promptEvidence);
                parsed = RebindDistinctSelectedItemsToSupportingEvidence(
                    request,
                    parsed,
                    promptEvidence);
            }
            parsed = EnforceDistinctStructuredSelection(
                request,
                parsed,
                promptEvidence,
                synthesisRecoveryErrorCode);
            return WithMetrics(
                parsed,
                completions,
                request.Handoff.Load);
        }
        finally
        {
            _budget?.EndJob(request.JobId);
        }
    }

    private static async Task ExecuteSearchBatchAsync(
        IAdvancedAnalysisToolGateway tools,
        IReadOnlyList<AdvancedAnalysisSearchRequest> plannedQueries,
        HashSet<string> previouslyExecuted,
        List<IReadOnlyList<AdvancedAnalysisResolvedEvidence>> evidenceGroups,
        Dictionary<string, HashSet<string>> retrievalQueriesByEvidenceId,
        CancellationToken cancellationToken)
    {
        foreach (var planned in plannedQueries)
        {
            if (!previouslyExecuted.Add(planned.Query))
                continue;
            var observation = await tools.SearchAsync(
                    planned,
                    cancellationToken)
                .ConfigureAwait(false);
            if (observation.Evidence.Count == 0)
                continue;

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
                        var errorCode = choices.ValueKind == JsonValueKind.Array
                                        && choices.GetArrayLength() > 0
                                        && string.Equals(
                                            ReadString(choices[0], "finish_reason"),
                                            "length",
                                            StringComparison.OrdinalIgnoreCase)
                            ? "advanced_llm_output_limit"
                            : "advanced_llm_content_missing";
                        if (reservation is not null)
                        {
                            _budget!.Fail(
                                reservation,
                                errorCode,
                                usage,
                                stopwatch.ElapsedMilliseconds,
                                httpAttemptCount);
                            reservation = null;
                        }
                        throw new AdvancedAnalysisProviderException(
                            errorCode);
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
