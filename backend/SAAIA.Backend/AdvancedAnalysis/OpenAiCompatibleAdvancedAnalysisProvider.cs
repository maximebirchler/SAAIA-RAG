using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        new(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(
                System.Text.Unicode.UnicodeRanges.All)
        };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AdvancedAnalysisOptions _options;
    private readonly string? _apiKey;
    private readonly AdvancedAnalysisExternalBudgetGuard? _budget;

    private sealed record CompletionResult(
        string Content,
        AdvancedAnalysisLlmUsage Usage,
        decimal? EstimatedCostUsd,
        string? NativeToolCallsJson = null,
        string? NativeResponseOutputJson = null);

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
            var priorSearches = request.PreviousToolEvents.Select(static item => item.Request).ToList();
            var previouslyExecuted = priorSearches.Select(BuildSearchIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await ExecuteSearchBatchAsync(
                    tools,
                    plannedQueries,
                    previouslyExecuted,
                    priorSearches,
                    evidenceGroups,
                    retrievalQueriesByEvidenceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (_options.AdaptiveResearchEnabled
                && !(_options.NativeResearchToolsEnabled && _options.NativeResearchTopology == "agent"))
            {
                var maximumCalls = Math.Clamp(
                    _options.ExternalMaximumCallsPerJob,
                    1,
                    1_024);
                var reserveSynthesisRecovery = runSemanticCritic
                    && request.Handoff.Load.StructuredLayout;
                var reservedFinalCalls = runSemanticCritic
                    ? reserveSynthesisRecovery ? 4 : 3
                    : 1;
                var reviewRound = 0;
                var reviewArgumentRecovery = 0;
                AdvancedAnalysisResearchArgumentFeedback? reviewArgumentFeedback = null;
                while (completions.Count < maximumCalls - reservedFinalCalls)
                {
                    reviewRound++;
                    var currentEvidence = FilterEvidenceToRequestedDocumentSet(
                        request,
                        OrderEvidenceForPrompt(tools.Evidence, evidenceGroups));
                    var observations = BuildPromptEvidence(
                        request, currentEvidence, retrievalQueriesByEvidenceId,
                        PrioritizeFocusedEvidenceForPrompt(currentEvidence, retrievalQueriesByEvidenceId,
                            priorSearches.Where(IsSourceScopedResearch).TakeLast(20).ToArray()));
                    var reviewUserPrompt = BuildResearchReviewUserPrompt(request, observations, priorSearches, availableCategories);
                    if (reviewArgumentFeedback is not null)
                    {
                        var payload = JsonNode.Parse(reviewUserPrompt)!.AsObject();
                        payload["researchArgumentFeedback"] = JsonSerializer.SerializeToNode(
                            BuildResearchArgumentFeedbackForPrompt(reviewArgumentFeedback), JsonOptions);
                        reviewUserPrompt = payload.ToJsonString(JsonOptions);
                    }
                    var researchReview = await CompleteJsonAsync(
                            request.JobId,
                            reviewArgumentFeedback is not null ? "research-review-argument-recovery" : reviewRound == 1
                                ? "research-review"
                                : $"research-review-{reviewRound}",
                            BuildResearchReviewSystemPrompt(),
                            reviewUserPrompt,
                            Math.Clamp(_options.PlannerMaxTokens, 256, 4_096),
                            cancellationToken)
                        .ConfigureAwait(false);
                    completions.Add(researchReview);
                    IReadOnlyList<AdvancedAnalysisSearchRequest> followUpQueries;
                    try
                    {
                        followUpQueries = ParseResearchReview(researchReview.Content, request, availableCategories, observations);
                    }
                    catch (AdvancedAnalysisProviderException error) when (error.ResearchArgumentFeedback is not null
                        && reviewArgumentRecovery == 0 && completions.Count < maximumCalls - reservedFinalCalls)
                    {
                        reviewArgumentFeedback = error.ResearchArgumentFeedback;
                        reviewArgumentRecovery++;
                        continue;
                    }
                    reviewArgumentFeedback = null;
                    if (followUpQueries.Count == 0)
                        break;
                    await ExecuteSearchBatchAsync(
                            tools,
                            followUpQueries,
                            previouslyExecuted,
                            priorSearches,
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
            var synthesisResearch = new SynthesisResearchContext(tools, availableCategories,
                priorSearches, previouslyExecuted, evidenceGroups, retrievalQueriesByEvidenceId);
            synthesisResearch.FocusSearches.AddRange(priorSearches
                .Where(IsSourceScopedResearch).TakeLast(20));
            var writerRound = await CompleteWithCorpusResearchAsync(
                    request,
                    "writer",
                    BuildWriterSystemPrompt(request),
                    observations => BuildWriterUserPrompt(request, observations),
                    Math.Clamp(_options.WriterMaxTokens, 512, 16_384),
                    runSemanticCritic ? 1 : 0,
                    synthesisResearch,
                    completions,
                    cancellationToken)
                .ConfigureAwait(false);
            var writer = writerRound.Completion;
            evidence = writerRound.Evidence;
            promptEvidence = writerRound.PromptEvidence;
            completions.Add(writer);
            AdvancedAnalysisProviderResult parsed;
            try
            {
                parsed = ParseResult(writer.Content, evidence, request);
            }
            catch (AdvancedAnalysisProviderException ex) when (
                (ex.ErrorCode is "advanced_writer_claim_markers_invalid"
                    or "advanced_writer_protocol_invalid"
                    or "advanced_writer_evidence_id_invalid"
                    or "advanced_writer_claim_count_invalid"
                || (ex.ErrorCode == "advanced_writer_duplicate_claims"
                    && request.Handoff.Load.StructuredLayout))
                && completions.Count < Math.Clamp(_options.ExternalMaximumCallsPerJob, 1, 1_024)
                    - (runSemanticCritic ? 1 : 0))
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
                (parsed, synthesisRecoveryErrorCode) =
                    await AttemptSynthesisRecoveryAsync(
                            request,
                            parsed,
                            evidence,
                            promptEvidence,
                            completions,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            if (runSemanticCritic
                && completions.Count < Math.Clamp(
                    _options.ExternalMaximumCallsPerJob,
                    1,
                    1_024))
            {
                var criticRound = await CompleteWithCorpusResearchAsync(
                        request,
                        "critic",
                        BuildCriticSystemPrompt(),
                        observations => BuildCriticUserPrompt(request, parsed, observations),
                        Math.Clamp(_options.CriticMaxTokens, 512, 16_384),
                        0,
                        synthesisResearch,
                        completions,
                        cancellationToken)
                    .ConfigureAwait(false);
                var critic = criticRound.Completion;
                evidence = criticRound.Evidence;
                promptEvidence = criticRound.PromptEvidence;
                completions.Add(critic);
                try
                {
                    parsed = ParseResult(critic.Content, evidence, request);
                }
                catch (AdvancedAnalysisProviderException) when (
                    completions.Count < Math.Clamp(
                        _options.ExternalMaximumCallsPerJob,
                        1,
                        1_024))
                {
                    var criticRepair = await CompleteJsonAsync(
                            request.JobId,
                            "critic-repair",
                            BuildCriticRepairSystemPrompt(),
                            BuildCriticRepairUserPrompt(
                                request,
                                parsed,
                                critic.Content,
                                promptEvidence),
                            Math.Clamp(
                                _options.CriticMaxTokens,
                                512,
                                16_384),
                            cancellationToken)
                        .ConfigureAwait(false);
                    completions.Add(criticRepair);
                    try
                    {
                        parsed = ParseResult(
                            criticRepair.Content,
                            evidence,
                            request);
                    }
                    catch (AdvancedAnalysisProviderException)
                    {
                        throw new AdvancedAnalysisProviderException(
                            "advanced_critic_protocol_invalid");
                    }
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
            if (runSemanticCritic
                && ShouldAttemptSynthesisRecovery(
                    request,
                    parsed,
                    promptEvidence)
                && completions.Count < Math.Clamp(
                    _options.ExternalMaximumCallsPerJob,
                    1,
                    1_024))
            {
                (parsed, synthesisRecoveryErrorCode) =
                    await AttemptSynthesisRecoveryAsync(
                            request,
                            parsed,
                            evidence,
                            promptEvidence,
                            completions,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            parsed = EnforceDistinctStructuredSelection(
                request,
                parsed,
                promptEvidence,
                synthesisRecoveryErrorCode);
            if (FindIdentityOnlyCandidateClaims(parsed, evidence, request).Count > 0)
                throw new AdvancedAnalysisProviderException("advanced_synthesis_candidate_body_not_supported");
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

    private async Task<(AdvancedAnalysisProviderResult Result, string? ErrorCode)>
        AttemptSynthesisRecoveryAsync(
            AdvancedAnalysisProviderRequest request,
            AdvancedAnalysisProviderResult candidate,
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
            IReadOnlyList<PromptEvidenceItem> promptEvidence,
            ICollection<CompletionResult> completions,
            CancellationToken cancellationToken)
    {
        try
        {
            var recovery = await CompleteJsonAsync(
                    request.JobId,
                    "synthesis-recovery",
                    BuildSynthesisRecoverySystemPrompt(),
                    BuildSynthesisRecoveryUserPrompt(
                        request,
                        candidate,
                        promptEvidence),
                    Math.Clamp(_options.WriterMaxTokens, 512, 16_384),
                    cancellationToken)
                .ConfigureAwait(false);
            completions.Add(recovery);
            var recovered = ParseResult(recovery.Content, evidence, request);
            recovered = CanonicalizeDistinctSelectedItems(
                request,
                recovered,
                promptEvidence);
            recovered = RebindDistinctSelectedItemsToSupportingEvidence(
                request,
                recovered,
                promptEvidence);
            return (recovered, null);
        }
        catch (AdvancedAnalysisProviderException ex)
        {
            // Final structural enforcement converts an unresolved recovery defect
            // to a safe terminal insufficiency.
            return (candidate, ex.ErrorCode);
        }
    }

    private static async Task ExecuteSearchBatchAsync(
        IAdvancedAnalysisToolGateway tools,
        IReadOnlyList<AdvancedAnalysisSearchRequest> plannedQueries,
        HashSet<string> previouslyExecuted,
        List<AdvancedAnalysisSearchRequest> priorSearches,
        List<IReadOnlyList<AdvancedAnalysisResolvedEvidence>> evidenceGroups,
        Dictionary<string, HashSet<string>> retrievalQueriesByEvidenceId,
        CancellationToken cancellationToken,
        Dictionary<string, AdvancedAnalysisSearchObservation>? observationsBySearch = null)
    {
        foreach (var planned in plannedQueries)
        {
            if (!previouslyExecuted.Add(BuildSearchIdentity(planned)))
                continue;
            var searchIndex=priorSearches.Count;
            priorSearches.Add(planned);
            var observation = await tools.SearchAsync(
                    planned,
                    cancellationToken)
                .ConfigureAwait(false);
            if (observationsBySearch is not null)
                observationsBySearch[BuildSearchIdentity(planned)] = observation;
            if(observation.ReadDiagnostic is not null)
                priorSearches[searchIndex]=planned with {ReadDiagnostic=observation.ReadDiagnostic};
            if (observation.FindDiagnostic is not null)
                priorSearches[searchIndex] = planned with { FindDiagnostic = observation.FindDiagnostic };
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
        ValidateDevelopmentTraceDirectory();
        if (!string.IsNullOrWhiteSpace(_options.SynthesisReasoningEffort)
            && _options.SynthesisReasoningEffort.Trim().ToLowerInvariant() is not ("low" or "medium" or "high"))
            throw new AdvancedAnalysisProviderException("advanced_synthesis_reasoning_effort_invalid");
        if (_options.NativeResearchToolsEnabled && _options.NativeResearchApiProtocol is not ("chat-completions" or "responses"))
            throw new AdvancedAnalysisProviderException("advanced_native_api_protocol_invalid");
        if (_options.NativeResearchToolsEnabled && _options.NativeResearchTopology is not ("reviewed" or "agent"))
            throw new AdvancedAnalysisProviderException("advanced_native_research_topology_invalid");
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
        CancellationToken cancellationToken,
        bool allowNativeResearch = false,
        IReadOnlyList<object>? nativeToolTurnMessages = null)
    {
        var nativeFunctions = allowNativeResearch && _options.NativeResearchToolsEnabled
            ? BuildNativeResearchFunctions(userPrompt) : null;
        var nativeResponses = allowNativeResearch && _options.NativeResearchToolsEnabled && UsesNativeResponses;
        if (nativeFunctions is not null)
            systemPrompt = systemPrompt.Replace(BuildSynthesisResearchContract(), NativeResearchContract, StringComparison.Ordinal);
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        if (nativeToolTurnMessages is { Count: > 0 }) messages.AddRange(nativeToolTurnMessages);
        messages.Add(new { role = "user", content = userPrompt });
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _options.LlmModel.Trim(),
            [IsOpenAiDev ? "max_completion_tokens" : "max_tokens"] = maximumOutputTokens,
            ["response_format"] = new { type = "json_object" },
            ["stream"] = false,
            ["messages"] = messages
        };
        if (nativeFunctions is not null)
        {
            payload["tools"] = nativeFunctions;
            payload["tool_choice"] = "auto";
            payload["parallel_tool_calls"] = true;
        }
        if (IsOpenAiDev)
        {
            var effort = allowNativeResearch && !string.IsNullOrWhiteSpace(_options.SynthesisReasoningEffort)
                ? _options.SynthesisReasoningEffort : _options.ReasoningEffort;
            if (!string.IsNullOrWhiteSpace(effort))
                payload["reasoning_effort"] = effort.Trim().ToLowerInvariant();
        }
        else
        {
            payload["temperature"] = 0;
        }
        if (nativeResponses) ConvertNativePayloadToResponses(payload);
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
                nativeResponses || nativeFunctions is not null || nativeToolTurnMessages is { Count: > 0 }
                    ? JsonSerializer.Serialize(payload, JsonOptions).Length
                    : systemPrompt.Length + userPrompt.Length,
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
                    nativeResponses ? ResolveNativeResponsesUri() : ResolveChatCompletionsUri())
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
                    await WriteDevelopmentHttpRejectionAsync(jobId, role, payload, response,
                        rejectedErrorCode, httpAttemptCount, stopwatch.ElapsedMilliseconds, timeout.Token).ConfigureAwait(false);
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
                    var usage = nativeResponses ? ReadNativeResponsesUsage(document.RootElement) : ReadUsage(document.RootElement);
                    var observedModelId = ReadObservedModelId(
                        document.RootElement);
                    var hasAnswer = document.RootElement.TryGetProperty(
                            "choices",
                            out var choices)
                        && choices.ValueKind == JsonValueKind.Array
                        && choices.GetArrayLength() > 0;
                    var answer = hasAnswer && choices[0].TryGetProperty("message", out var answerValue)
                        ? answerValue : default;
                    string? completionContent = null;
                    string? nativeCallsJson = null;
                    string? nativeOutputJson = null;
                    string? normalizationError = null;
                    var completionOrigin = "message.content";
                    if (nativeResponses)
                    {
                        (completionContent, nativeCallsJson, nativeOutputJson, completionOrigin, normalizationError) =
                            ReadNativeResponsesCompletion(document.RootElement, userPrompt, nativeFunctions is not null);
                    }
                    else if (answer.ValueKind == JsonValueKind.Object)
                    {
                        if (answer.TryGetProperty("tool_calls", out var calls)
                            && calls.ValueKind != JsonValueKind.Null && !(calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() == 0))
                        {
                            if (nativeFunctions is null) normalizationError = "advanced_native_tool_not_available";
                            else
                            {
                                try
                                {
                                    completionContent = NormalizeNativeResearchCalls(calls, userPrompt);
                                    nativeCallsJson = calls.GetRawText();
                                }
                                catch (Exception error) when (error is JsonException or AdvancedAnalysisProviderException or InvalidOperationException)
                                { normalizationError = "advanced_native_tool_protocol_invalid"; }
                            }
                        }
                        else if (answer.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                            completionContent = content.GetString();
                    }
                    if (string.IsNullOrWhiteSpace(completionContent) || normalizationError is not null
                        || (!nativeResponses && nativeCallsJson is not null && ReadString(choices[0], "finish_reason") == "length"))
                    {
                        var errorCode = choices.ValueKind == JsonValueKind.Array
                                        && choices.GetArrayLength() > 0
                                        && string.Equals(
                                            ReadString(choices[0], "finish_reason"),
                                            "length",
                                            StringComparison.OrdinalIgnoreCase)
                            ? "advanced_llm_output_limit"
                            : normalizationError ?? "advanced_llm_content_missing";
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
                        if (nativeResponses || normalizationError is not null || nativeCallsJson is not null)
                            await WriteDevelopmentTraceAsync(jobId, role, payload, document.RootElement,
                                completionContent ?? string.Empty, observedModelId, null,
                                httpAttemptCount, stopwatch.ElapsedMilliseconds, cancellationToken,
                                nativeResponses ? completionOrigin : "tool_calls.rejected", errorCode).ConfigureAwait(false);
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
                    await WriteDevelopmentTraceAsync(jobId, role, payload, document.RootElement,
                        completionContent!, observedModelId, charge?.CostUsd,
                        httpAttemptCount, stopwatch.ElapsedMilliseconds, cancellationToken,
                        nativeResponses ? completionOrigin : nativeCallsJson is null ? "message.content" : "tool_calls.normalized").ConfigureAwait(false);
                    return new CompletionResult(
                        completionContent!.Trim(),
                        usage,
                        charge?.CostUsd,
                        nativeCallsJson,
                        nativeOutputJson);
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
