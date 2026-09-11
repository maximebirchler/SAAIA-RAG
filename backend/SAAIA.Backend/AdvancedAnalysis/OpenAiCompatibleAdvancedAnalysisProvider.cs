using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
        if (IsOpenAiDev)
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
            return configured.Length <= 100
                ? configured
                : configured[..100];
        }
    }

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
            var plannedQueries = ParsePlan(planner.Content, request);
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
                await tools.SearchAsync(planned, cancellationToken)
                    .ConfigureAwait(false);
            }

            var evidence = tools.Evidence
                .Where(static item =>
                    !string.IsNullOrWhiteSpace(item.Reference.EvidenceId))
                .ToArray();
            if (evidence.Length == 0)
            {
                return WithMetrics(
                    new AdvancedAnalysisProviderResult
                    {
                        Outcome = "insufficient_documentation",
                        AnswerText = BuildNoEvidenceAnswer(request.Handoff.Language)
                    },
                    completions);
            }

            var writer = await CompleteJsonAsync(
                    request.JobId,
                    "writer",
                    BuildWriterSystemPrompt(request),
                    BuildWriterUserPrompt(request, evidence),
                    Math.Clamp(_options.WriterMaxTokens, 512, 16_384),
                    cancellationToken)
                .ConfigureAwait(false);
            completions.Add(writer);
            return WithMetrics(
                ParseResult(writer.Content, evidence),
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

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                Math.Clamp(_options.LlmTimeoutSeconds, 5, 3_600)));
            HttpResponseMessage response;
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

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new AdvancedAnalysisProviderException(
                        $"advanced_llm_http_{(int)response.StatusCode}");
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
                        throw new AdvancedAnalysisProviderException(
                            "advanced_llm_content_missing");
                    }
                    var usage = ReadUsage(document.RootElement);
                    AdvancedAnalysisBudgetCharge? charge = null;
                    if (reservation is not null)
                    {
                        try
                        {
                            charge = _budget!.Complete(reservation, usage);
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
        catch
        {
            if (reservation is not null)
            {
                _budget!.Fail(reservation, "advanced_llm_call_failed");
            }
            throw;
        }
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

            var maximumQueries = Math.Clamp(
                _options.MaximumPlanQueries,
                1,
                32);
            var result = new List<AdvancedAnalysisSearchRequest>();
            var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in queries.EnumerateArray())
            {
                if (result.Count >= maximumQueries
                    || item.ValueKind != JsonValueKind.Object)
                {
                    break;
                }
                var query = ReadString(item, "query");
                if (string.IsNullOrWhiteSpace(query)
                    || query.Length > 8_000
                    || !dedupe.Add(query))
                {
                    continue;
                }
                var category = ReadString(item, "category");
                var topK = item.TryGetProperty("topK", out var topKValue)
                           && topKValue.TryGetInt32(out var parsedTopK)
                    ? Math.Clamp(parsedTopK, 1, 60)
                    : 20;
                result.Add(new AdvancedAnalysisSearchRequest(
                    query,
                    string.IsNullOrWhiteSpace(category) ? null : category,
                    topK));
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

    private static AdvancedAnalysisProviderResult ParseResult(
        string raw,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
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
                        throw new JsonException();
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
           JSON object and no prose. Plan focused corpus searches; never answer
           the user. Prefer complementary queries that can cover every requested
           output unit. For a repeated grid, plan searches for each semantic
           column or item family rather than repeating the same broad query.
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
            maximumQueries = Math.Clamp(
                _options.MaximumPlanQueries,
                1,
                32)
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
           format. If the evidence cannot support all mandatory units and partial
           answers are not allowed, choose insufficient_documentation.
           """
           + "\nRequested output shape: "
           + JsonSerializer.Serialize(
               BuildPromptLoad(request.Handoff.Load),
               JsonOptions);

    private string BuildWriterUserPrompt(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence)
    {
        var remaining = Math.Clamp(
            _options.MaximumEvidencePromptCharacters,
            16_000,
            1_000_000);
        var promptEvidence = new List<object>();
        foreach (var item in evidence)
        {
            if (remaining <= 0)
                break;
            var content = item.Content ?? string.Empty;
            if (content.Length > remaining)
                content = content[..remaining];
            remaining -= content.Length;
            promptEvidence.Add(new
            {
                item.Reference.EvidenceId,
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

    private static string BuildNoEvidenceAnswer(string language)
        => language.Trim().ToLowerInvariant() switch
        {
            "en" => "The available documentation does not contain enough revalidated evidence to answer this request.",
            "de" => "Die verfügbare Dokumentation enthält nicht genügend erneut validierte Belege für diese Anfrage.",
            _ => "La documentation disponible ne contient pas assez de preuves revalidées pour répondre à cette demande."
        };

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
