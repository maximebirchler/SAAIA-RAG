using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LlmProviderArchitectureTests
{
    [Fact]
    public void RagChatAgent_requires_an_explicit_provider_abstraction()
    {
        var constructor = Assert.Single(typeof(RagChatAgent).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(ApiClient), parameters[0].ParameterType);
        Assert.Equal(typeof(ILlmProvider), parameters[1].ParameterType);
    }

    [Fact]
    public void Committed_configuration_is_local_by_default_and_caps_authorized_budget_at_25_usd()
    {
        var path = FindRepositoryFile("config", "llm-providers.dev.json");

        var configuration = LlmProviderConfiguration.Load(path);

        Assert.Equal(LlmProviderMode.Local, configuration.Mode);
        Assert.Equal(LlmExternalExecutionPolicy.ProductionLocal, configuration.ExternalPolicy);
        Assert.Equal("gpt-5.6-terra", configuration.OpenAi.ModelId);
        Assert.Equal(25m, configuration.OpenAi.Budget.AuthorizedBudgetUsd);
        Assert.Equal(24m, configuration.OpenAi.Budget.HardLimitUsd);
        Assert.Equal(string.Empty, configuration.RunPod.Runtime);
    }

    [Fact]
    public void OpenAiDev_is_rejected_by_production_local_policy()
    {
        var configuration = CreateConfiguration(
            LlmProviderMode.OpenAiDev,
            LlmExternalExecutionPolicy.ProductionLocal);

        var error = Assert.Throws<InvalidOperationException>(configuration.Validate);

        Assert.Contains("DevelopmentExternalAllowed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAiDev_without_key_fails_before_any_http_request()
    {
        var previous = Environment.GetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable);
        try
        {
            // A present but blank value explicitly suppresses the optional DPAPI fallback.
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, " ");
            var error = Assert.Throws<InvalidOperationException>(() =>
                LlmProviderFactory.Create(
                    new OpenAiLlmClient(new HttpClient(new DelegateHandler(_ =>
                        throw new InvalidOperationException("HTTP must not run.")))),
                    new AppSettings(),
                    CreateConfiguration(
                        LlmProviderMode.OpenAiDev,
                        LlmExternalExecutionPolicy.DevelopmentExternalAllowed)));

            Assert.Contains(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void RunPodBench_rejects_missing_endpoint_and_model_configuration()
    {
        var missingEndpoint = CreateConfiguration(
            LlmProviderMode.RunPodBench,
            LlmExternalExecutionPolicy.BenchmarkExternalAllowed,
            runPodBaseUrl: string.Empty);
        var endpointError = Assert.Throws<InvalidOperationException>(missingEndpoint.Validate);
        Assert.Contains("baseUrl", endpointError.Message, StringComparison.OrdinalIgnoreCase);

        var missingModel = CreateConfiguration(
            LlmProviderMode.RunPodBench,
            LlmExternalExecutionPolicy.BenchmarkExternalAllowed,
            runPodModel: string.Empty);
        var modelError = Assert.Throws<InvalidOperationException>(missingModel.Validate);
        Assert.Contains("modelId", modelError.Message, StringComparison.OrdinalIgnoreCase);

        var missingRuntime = CreateConfiguration(
            LlmProviderMode.RunPodBench,
            LlmExternalExecutionPolicy.BenchmarkExternalAllowed,
            runPodRuntime: string.Empty);
        var runtimeError = Assert.Throws<InvalidOperationException>(
            missingRuntime.Validate);
        Assert.Contains("runtime", runtimeError.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAiDev_uses_bearer_auth_openai_token_parameter_and_records_usage_without_secret()
    {
        const string secret = "test-secret-must-never-be-persisted";
        var previous = Environment.GetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable);
        var ledger = Path.Combine(Path.GetTempPath(), "saaia-openai-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
        HttpRequestMessage? captured = null;
        string? requestBody = null;
        try
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, secret);
            var handler = new DelegateHandler(async request =>
            {
                captured = request;
                requestBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync();
                return JsonResponse("""
                {
                  "choices": [{"message":{"content":"réponse"},"finish_reason":"stop"}],
                  "usage": {
                    "prompt_tokens": 7000,
                    "completion_tokens": 1000,
                    "prompt_tokens_details": {"cached_tokens": 0, "cache_write_tokens": 0},
                    "completion_tokens_details": {"reasoning_tokens": 250}
                  }
                }
                """);
            });
            var transport = new OpenAiLlmClient(new HttpClient(handler));
            var configuration = CreateConfiguration(
                LlmProviderMode.OpenAiDev,
                LlmExternalExecutionPolicy.DevelopmentExternalAllowed,
                ledger);
            var provider = LlmProviderFactory.Create(transport, new AppSettings(), configuration);
            LlmCallMetrics? metrics = null;
            provider.CallCompleted += value => metrics = value;
            provider.ConfigureGeneration(0.2, 1000);

            Assert.Equal(1_050_000, await provider.GetRuntimeContextTokensAsync(CancellationToken.None));
            var orchestrator = new ToolAgentOrchestrator(
                new ApiClient(),
                provider,
                new ToolMemory(),
                new AppSettings());
            var budgetMethod = typeof(ToolAgentOrchestrator).GetMethod(
                "ResolveWriterPromptBudget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(budgetMethod);
            var writerBudget = budgetMethod!.Invoke(orchestrator, null);
            Assert.NotNull(writerBudget);
            Assert.Equal(
                1_050_000,
                writerBudget!.GetType().GetProperty("ContextTokens")!.GetValue(writerBudget));

            using (provider.BeginTurn("terra-test"))
            {
                var answer = await provider.CompleteAsync(
                    new[] { (role: "user", content: "Question directe") },
                    forceJson: false,
                    CancellationToken.None);
                Assert.Equal("réponse", answer);
            }

            Assert.NotNull(captured);
            Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
            Assert.Equal(secret, captured.Headers.Authorization?.Parameter);
            using var payload = JsonDocument.Parse(requestBody!);
            Assert.Equal(1000, payload.RootElement.GetProperty("max_completion_tokens").GetInt32());
            Assert.False(payload.RootElement.TryGetProperty("max_tokens", out _));
            Assert.Equal("low", payload.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.False(payload.RootElement.TryGetProperty("temperature", out _));

            Assert.NotNull(metrics);
            Assert.Equal(LlmProviderMode.OpenAiDev, metrics!.Mode);
            Assert.Equal(7000, metrics.Usage.InputTokens);
            Assert.Equal(1000, metrics.Usage.OutputTokens);
            Assert.Equal(250, metrics.Usage.ReasoningTokens);
            Assert.Equal(0.026m, metrics.EstimatedCostUsd);
            var ledgerText = File.ReadAllText(ledger);
            Assert.DoesNotContain(secret, ledgerText, StringComparison.Ordinal);
            Assert.DoesNotContain("Question directe", ledgerText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, previous);
            if (File.Exists(ledger))
                File.Delete(ledger);
        }
    }

    [Fact]
    public async Task OpenAiDev_streaming_normalizes_deltas_and_collects_final_usage_chunk()
    {
        const string secret = "stream-secret";
        var previous = Environment.GetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable);
        var ledger = Path.Combine(Path.GetTempPath(), "saaia-openai-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
        string? requestBody = null;
        try
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, secret);
            var handler = new DelegateHandler(async request =>
            {
                requestBody = await request.Content!.ReadAsStringAsync();
                var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Bon\"}}]}\n\n"
                          + "data: {\"choices\":[{\"delta\":{\"content\":\"jour\"}}]}\n\n"
                          + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":12,"
                          + "\"prompt_tokens_details\":{\"cached_tokens\":80,\"cache_write_tokens\":0},"
                          + "\"completion_tokens_details\":{\"reasoning_tokens\":4}}}\n\n"
                          + "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            });
            var provider = LlmProviderFactory.Create(
                new OpenAiLlmClient(new HttpClient(handler)),
                new AppSettings(),
                CreateConfiguration(
                    LlmProviderMode.OpenAiDev,
                    LlmExternalExecutionPolicy.DevelopmentExternalAllowed,
                    ledger));
            provider.ConfigureGeneration(0.2, 256);
            var answer = new StringBuilder();
            LlmCallMetrics? metrics = null;
            provider.CallCompleted += value => metrics = value;

            using (provider.BeginTurn("stream-test"))
            {
                await provider.StreamAsync(
                    new[] { (role: "user", content: "Salue-moi") },
                    forceJson: false,
                    chunk => answer.Append(chunk),
                    CancellationToken.None);
            }

            Assert.Equal("Bonjour", answer.ToString());
            using var payload = JsonDocument.Parse(requestBody!);
            Assert.True(payload.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            Assert.Equal(256, payload.RootElement.GetProperty("max_completion_tokens").GetInt32());
            Assert.NotNull(metrics?.TimeToFirstTokenMilliseconds);
            Assert.Equal(120, metrics?.Usage.InputTokens);
            Assert.Equal(80, metrics?.Usage.CachedInputTokens);
            Assert.Equal(12, metrics?.Usage.OutputTokens);
            Assert.Equal(4, metrics?.Usage.ReasoningTokens);
            Assert.Equal(0.000240m, metrics?.EstimatedCostUsd);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, previous);
            if (File.Exists(ledger))
                File.Delete(ledger);
        }
    }

    [Fact]
    public async Task Local_mode_keeps_llama_cpp_payload_and_requires_no_external_secret()
    {
        HttpRequestMessage? captured = null;
        string? requestBody = null;
        var transport = new OpenAiLlmClient(new HttpClient(new DelegateHandler(async request =>
        {
            captured = request;
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {"choices":[{"message":{"content":"local-ok"},"finish_reason":"stop"}]}
                """);
        })));
        var settings = new AppSettings
        {
            Host = "127.0.0.1",
            Port = 1234,
            ModelId = "qwen-local-test"
        };
        var configuration = CreateConfiguration(
            LlmProviderMode.Local,
            LlmExternalExecutionPolicy.ProductionLocal);
        var provider = LlmProviderFactory.Create(transport, settings, configuration);
        provider.ConfigureGeneration(0.35, 320);

        var answer = await provider.CompleteAsync(
            new[] { (role: "user", content: "test local") },
            false,
            CancellationToken.None);

        Assert.Equal("local-ok", answer);
        Assert.Equal(LlmProviderMode.Local, provider.Descriptor.Mode);
        Assert.False(provider.Descriptor.IsExternal);
        Assert.Null(captured!.Headers.Authorization);
        using var payload = JsonDocument.Parse(requestBody!);
        Assert.Equal("qwen-local-test", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal(320, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.35, payload.RootElement.GetProperty("temperature").GetDouble());
        Assert.False(payload.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.False(payload.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task RunPodBench_uses_configured_openai_compatible_endpoint_model_and_bearer()
    {
        const string secret = "runpod-test-secret";
        var previous = Environment.GetEnvironmentVariable(LlmProviderConfiguration.RunPodKeyEnvironmentVariable);
        HttpRequestMessage? captured = null;
        string? requestBody = null;
        try
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.RunPodKeyEnvironmentVariable, secret);
            var transport = new OpenAiLlmClient(new HttpClient(new DelegateHandler(async request =>
            {
                captured = request;
                requestBody = await request.Content!.ReadAsStringAsync();
                return JsonResponse("""
                    {"choices":[{"message":{"content":"runpod-ok"},"finish_reason":"stop"}]}
                    """);
            })));
            var configuration = CreateConfiguration(
                LlmProviderMode.RunPodBench,
                LlmExternalExecutionPolicy.BenchmarkExternalAllowed);
            var provider = LlmProviderFactory.Create(transport, new AppSettings(), configuration);
            provider.ConfigureGeneration(0.15, 448);

            var answer = await provider.CompleteAsync(
                new[] { (role: "user", content: "test runpod") },
                false,
                CancellationToken.None);

            Assert.Equal("runpod-ok", answer);
            Assert.Equal(LlmProviderMode.RunPodBench, provider.Descriptor.Mode);
            Assert.Equal("runpod", provider.Descriptor.Provider);
            Assert.Equal("llama.cpp", provider.Descriptor.Runtime);
            Assert.Equal("Q5_K_M", provider.Descriptor.RuntimeParameters?.Quantization);
            Assert.Equal(32768, provider.Descriptor.ContextWindowTokens);
            Assert.Equal(32768, await provider.GetRuntimeContextTokensAsync(CancellationToken.None));
            Assert.True(provider.Descriptor.IsExternal);
            Assert.Equal("example.runpod.net", captured!.RequestUri!.Host);
            Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
            Assert.Equal(secret, captured.Headers.Authorization?.Parameter);
            using var payload = JsonDocument.Parse(requestBody!);
            Assert.Equal("qwen-test", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal(448, payload.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal(0.15, payload.RootElement.GetProperty("temperature").GetDouble());
            Assert.False(payload.RootElement.TryGetProperty("reasoning_effort", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.RunPodKeyEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task One_provider_instance_executes_structured_router_native_tool_and_streaming_writer()
    {
        const string secret = "single-provider-contract-secret";
        var previous = Environment.GetEnvironmentVariable(
            LlmProviderConfiguration.RunPodKeyEnvironmentVariable);
        var requests = new List<JsonElement>();
        try
        {
            Environment.SetEnvironmentVariable(
                LlmProviderConfiguration.RunPodKeyEnvironmentVariable,
                secret);
            var handler = new DelegateHandler(async request =>
            {
                using var requestJson = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync());
                requests.Add(requestJson.RootElement.Clone());
                return requests.Count switch
                {
                    1 => JsonResponse(
                        """
                        {
                          "choices": [{
                            "message": {"content": "{\"intent\":\"rag.answer\",\"language\":\"fr\"}"},
                            "finish_reason": "stop"
                          }],
                          "usage": {"prompt_tokens": 80, "completion_tokens": 12}
                        }
                        """),
                    2 => JsonResponse(
                        """
                        {
                          "choices": [{
                            "message": {
                              "content": null,
                              "tool_calls": [{
                                "id": "call_search",
                                "type": "function",
                                "function": {
                                  "name": "rag_search",
                                  "arguments": "{\"query\":\"definition PTS\"}"
                                }
                              }]
                            },
                            "finish_reason": "tool_calls"
                          }],
                          "usage": {"prompt_tokens": 110, "completion_tokens": 18}
                        }
                        """),
                    3 => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "data: {\"choices\":[{\"delta\":{\"content\":\"Réponse \"}}]}\n\n"
                            + "data: {\"choices\":[{\"delta\":{\"content\":\"sourcée.\"}}]}\n\n"
                            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":150,\"completion_tokens\":20}}\n\n"
                            + "data: [DONE]\n\n",
                            Encoding.UTF8,
                            "text/event-stream")
                    },
                    _ => throw new InvalidOperationException(
                        "The same-provider contract must issue exactly three calls.")
                };
            });
            var provider = LlmProviderFactory.Create(
                new OpenAiLlmClient(new HttpClient(handler)),
                new AppSettings(),
                CreateConfiguration(
                    LlmProviderMode.RunPodBench,
                    LlmExternalExecutionPolicy.BenchmarkExternalAllowed));
            provider.ConfigureGeneration(0.1, 512);
            var metrics = new List<LlmCallMetrics>();
            provider.CallCompleted += metrics.Add;
            var contract = LlmStructuredOutputContract.Parse(
                "saaia_mock_router_v1",
                """
                {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "intent": { "type": "string" },
                    "language": { "type": "string" }
                  },
                  "required": ["intent", "language"]
                }
                """);
            var searchTool = new SourceBackedAgentToolDefinition(
                "rag_search",
                "Search the SAAIA evidence store.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        query = new { type = "string" }
                    },
                    required = new[] { "query" }
                }));
            var writer = new StringBuilder();

            using (provider.BeginTurn("single-provider-mock"))
            {
                var routerJson = await provider.CompleteStructuredAsync(
                    new[]
                    {
                        (role: "system", content: "Router SAAIA."),
                        (role: "user", content: "Qu'est-ce qu'une PTS ?")
                    },
                    contract,
                    CancellationToken.None);
                using var router = JsonDocument.Parse(routerJson);
                Assert.Equal(
                    "rag.answer",
                    router.RootElement.GetProperty("intent").GetString());

                var toolDecision = await provider.CompleteAsync(
                    new[]
                    {
                        SourceBackedAgentMessage.System("Choose one retrieval tool."),
                        SourceBackedAgentMessage.User("Qu'est-ce qu'une PTS ?")
                    },
                    new[] { searchTool },
                    maxTokens: 128,
                    CancellationToken.None,
                    requireToolCall: true);
                var toolCall = Assert.Single(toolDecision.ToolCalls);
                Assert.Equal("rag_search", toolCall.Name);
                Assert.Equal(
                    "definition PTS",
                    toolCall.Arguments.GetProperty("query").GetString());

                await provider.StreamAsync(
                    new[]
                    {
                        (role: "system", content: "Writer SAAIA."),
                        (role: "tool", content: "PTS: procédure de travail sécurisée."),
                        (role: "user", content: "Rédige la réponse sourcée.")
                    },
                    forceJson: false,
                    chunk => writer.Append(chunk),
                    CancellationToken.None);
            }

            Assert.Equal("Réponse sourcée.", writer.ToString());
            Assert.Equal(3, requests.Count);
            Assert.Equal(3, metrics.Count);
            Assert.All(metrics, metric =>
            {
                Assert.True(metric.Success);
                Assert.Equal(LlmProviderMode.RunPodBench, metric.Mode);
                Assert.Equal("runpod", metric.Provider);
                Assert.Equal("qwen-test", metric.ModelId);
            });
            Assert.True(requests[0].TryGetProperty("response_format", out _));
            Assert.Equal("required", requests[1].GetProperty("tool_choice").GetString());
            Assert.True(requests[2].GetProperty("stream").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                LlmProviderConfiguration.RunPodKeyEnvironmentVariable,
                previous);
        }
    }

    [Fact]
    public async Task External_model_listing_uses_the_provider_credential()
    {
        const string secret = "model-list-secret";
        var previous = Environment.GetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, secret);
            HttpRequestMessage? captured = null;
            var transport = new OpenAiLlmClient(new HttpClient(new DelegateHandler(request =>
            {
                captured = request;
                return Task.FromResult(JsonResponse("""
                    {"data":[{"id":"gpt-5.6-terra"}]}
                    """));
            })));
            var provider = LlmProviderFactory.Create(
                transport,
                new AppSettings(),
                CreateConfiguration(
                    LlmProviderMode.OpenAiDev,
                    LlmExternalExecutionPolicy.DevelopmentExternalAllowed));

            var models = await provider.ListModelsAsync(CancellationToken.None);

            Assert.Contains("gpt-5.6-terra", models);
            Assert.Equal(HttpMethod.Get, captured!.Method);
            Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
            Assert.Equal(secret, captured.Headers.Authorization?.Parameter);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task External_request_timeout_is_typed_and_recorded()
    {
        const string secret = "timeout-test-secret";
        var previous = Environment.GetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable);
        var ledger = Path.Combine(Path.GetTempPath(), "saaia-openai-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, secret);
            var transport = new OpenAiLlmClient(new HttpClient(new DelegateHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            })));
            var provider = LlmProviderFactory.Create(
                transport,
                new AppSettings(),
                CreateConfiguration(
                    LlmProviderMode.OpenAiDev,
                    LlmExternalExecutionPolicy.DevelopmentExternalAllowed,
                    ledger,
                    openAiTimeoutSeconds: 1));
            LlmCallMetrics? metrics = null;
            provider.CallCompleted += value => metrics = value;

            var error = await Assert.ThrowsAsync<LlmProviderRequestTimeoutException>(() =>
                provider.CompleteAsync(
                    new[] { (role: "user", content: "timeout") },
                    false,
                    CancellationToken.None));

            Assert.Contains("1-second", error.Message, StringComparison.Ordinal);
            Assert.False(metrics?.Success);
            Assert.Equal("timeout", metrics?.ErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, previous);
            if (File.Exists(ledger))
                File.Delete(ledger);
        }
    }

    [Fact]
    public async Task Caller_cancellation_remains_distinct_from_provider_timeout()
    {
        const string secret = "cancellation-test-secret";
        var previous = Environment.GetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable);
        var ledger = Path.Combine(Path.GetTempPath(), "saaia-openai-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, secret);
            var transport = new OpenAiLlmClient(new HttpClient(new DelegateHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            })));
            var provider = LlmProviderFactory.Create(
                transport,
                new AppSettings(),
                CreateConfiguration(
                    LlmProviderMode.OpenAiDev,
                    LlmExternalExecutionPolicy.DevelopmentExternalAllowed,
                    ledger));
            LlmCallMetrics? metrics = null;
            provider.CallCompleted += value => metrics = value;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.CompleteAsync(
                    new[] { (role: "user", content: "cancel") },
                    false,
                    cancellation.Token));

            Assert.False(metrics?.Success);
            Assert.Equal("cancelled", metrics?.ErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, previous);
            if (File.Exists(ledger))
                File.Delete(ledger);
        }
    }

    [Fact]
    public async Task Interrupted_openai_stream_is_rejected_instead_of_marked_successful()
    {
        const string secret = "interrupted-stream-secret";
        var previous = Environment.GetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable);
        var ledger = Path.Combine(Path.GetTempPath(), "saaia-openai-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, secret);
            var transport = new OpenAiLlmClient(new HttpClient(new DelegateHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n",
                        Encoding.UTF8,
                        "text/event-stream")
                }))));
            var provider = LlmProviderFactory.Create(
                transport,
                new AppSettings(),
                CreateConfiguration(
                    LlmProviderMode.OpenAiDev,
                    LlmExternalExecutionPolicy.DevelopmentExternalAllowed,
                    ledger));
            var output = new StringBuilder();
            LlmCallMetrics? metrics = null;
            provider.CallCompleted += value => metrics = value;

            await Assert.ThrowsAsync<IOException>(() =>
                provider.StreamAsync(
                    new[] { (role: "user", content: "stream") },
                    false,
                    chunk => output.Append(chunk),
                    CancellationToken.None));

            Assert.Equal("partial", output.ToString());
            Assert.False(metrics?.Success);
            Assert.Equal(nameof(IOException), metrics?.ErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, previous);
            if (File.Exists(ledger))
                File.Delete(ledger);
        }
    }

    [Fact]
    public async Task Network_failure_is_normalized_without_retrying_or_switching_provider()
    {
        const string secret = "network-test-secret";
        var previous = Environment.GetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable);
        var ledger = Path.Combine(Path.GetTempPath(), "saaia-openai-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var calls = 0;
        try
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, secret);
            var transport = new OpenAiLlmClient(new HttpClient(new DelegateHandler(_ =>
            {
                calls++;
                throw new HttpRequestException("network unavailable");
            })));
            var provider = LlmProviderFactory.Create(
                transport,
                new AppSettings(),
                CreateConfiguration(
                    LlmProviderMode.OpenAiDev,
                    LlmExternalExecutionPolicy.DevelopmentExternalAllowed,
                    ledger));
            LlmCallMetrics? metrics = null;
            provider.CallCompleted += value => metrics = value;

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                provider.CompleteAsync(
                    new[] { (role: "user", content: "network") },
                    false,
                    CancellationToken.None));

            Assert.Equal(1, calls);
            Assert.Equal(LlmProviderMode.OpenAiDev, metrics?.Mode);
            Assert.Equal(nameof(HttpRequestException), metrics?.ErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, previous);
            if (File.Exists(ledger))
                File.Delete(ledger);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "http_401")]
    [InlineData(HttpStatusCode.TooManyRequests, "http_429")]
    [InlineData(HttpStatusCode.InternalServerError, "http_500")]
    public async Task External_http_errors_are_typed_and_metrics_never_expose_the_key(
        HttpStatusCode status,
        string expectedCode)
    {
        const string secret = "provider-secret-redaction-check";
        var previous = Environment.GetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable);
        var ledger = Path.Combine(Path.GetTempPath(), "saaia-openai-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, secret);
            var transport = new OpenAiLlmClient(new HttpClient(new DelegateHandler(_ =>
                Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("failure echoed " + secret, Encoding.UTF8, "text/plain")
                }))));
            var provider = LlmProviderFactory.Create(
                transport,
                new AppSettings(),
                CreateConfiguration(
                    LlmProviderMode.OpenAiDev,
                    LlmExternalExecutionPolicy.DevelopmentExternalAllowed,
                    ledger));
            LlmCallMetrics? metrics = null;
            provider.CallCompleted += value => metrics = value;

            var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                provider.CompleteAsync(
                    new[] { (role: "user", content: "test") },
                    false,
                    CancellationToken.None));

            Assert.Equal(status, error.StatusCode);
            Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
            Assert.Equal(expectedCode, metrics?.ErrorCode);
            Assert.DoesNotContain(secret, File.ReadAllText(ledger), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProviderConfiguration.OpenAiKeyEnvironmentVariable, previous);
            if (File.Exists(ledger))
                File.Delete(ledger);
        }
    }

    [Fact]
    public void Budget_guard_rejects_a_call_before_it_can_cross_the_hard_limit()
    {
        var ledger = Path.Combine(Path.GetTempPath(), "saaia-openai-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var descriptor = new LlmProviderDescriptor(
            LlmProviderMode.OpenAiDev,
            "openai",
            "openai-api",
            "gpt-5.6-terra",
            null,
            true,
            true);
        var guard = new LlmCostBudgetGuard(
            descriptor,
            new LlmPricingMetadata(2m, 0.2m, 2.5m, 12m),
            new LlmBudgetOptions(0.02m, 0.01m, 0.02m, 0.02m, 4, ledger));

        var error = Assert.Throws<LlmBudgetExceededException>(() =>
            guard.Reserve(LlmLogicalRole.Writer, 7000, 1000));

        Assert.Equal("campaign_hard_limit", error.Reason);
        Assert.False(File.Exists(ledger));
    }

    [Fact]
    public void RunPodBench_requires_benchmark_policy()
    {
        var configuration = CreateConfiguration(
            LlmProviderMode.RunPodBench,
            LlmExternalExecutionPolicy.DevelopmentExternalAllowed);

        var error = Assert.Throws<InvalidOperationException>(configuration.Validate);

        Assert.Contains("BenchmarkExternalAllowed", error.Message, StringComparison.Ordinal);
    }

    private static LlmProviderConfiguration CreateConfiguration(
        LlmProviderMode mode,
        LlmExternalExecutionPolicy policy,
        string? ledger = null,
        int openAiTimeoutSeconds = 120,
        string runPodBaseUrl = "https://example.runpod.net/v1",
        string runPodModel = "qwen-test",
        string runPodRuntime = "llama.cpp")
        => new()
        {
            Mode = mode,
            ExternalPolicy = policy,
            SourcePath = "test",
            OpenAi = new LlmProviderConfiguration.OpenAiConfiguration(
                "https://api.openai.com/v1",
                "gpt-5.6-terra",
                LlmProviderConfiguration.OpenAiKeyEnvironmentVariable,
                "low",
                openAiTimeoutSeconds,
                new LlmPricingMetadata(2m, 0.2m, 2.5m, 12m),
                new LlmBudgetOptions(
                    25m,
                    20m,
                    24m,
                    0.50m,
                    32,
                    ledger ?? Path.Combine(Path.GetTempPath(), "unused-ledger.jsonl"))),
            RunPod = new LlmProviderConfiguration.RunPodConfiguration(
                runPodBaseUrl,
                runPodModel,
                LlmProviderConfiguration.RunPodKeyEnvironmentVariable,
                runPodRuntime,
                "qwen-test-profile",
                new LlmRuntimeProfileMetadata(
                    "/models/qwen.gguf",
                    "Q5_K_M",
                    32768,
                    512,
                    128,
                    8,
                    8,
                    99,
                    true),
                300)
        };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string FindRepositoryFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = segments.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(path))
                return path;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        internal DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            : this((request, _) => handler(request))
        {
        }

        internal DelegateHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
