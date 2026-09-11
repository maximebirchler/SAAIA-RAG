using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("EnvironmentVariables")]
public sealed class OpenAiLlmClientTests
{
    [Fact]
    public async Task ListModelsAsync_reads_openai_data_format()
    {
        var sut = CreateClient(
            """
            {
              "data": [
                { "id": "qwen3-4b-instruct-2507-q5-k-m" },
                { "id": "qwen3-4b-instruct-2507-q4-k-m" }
              ]
            }
            """);

        sut.Configure("http://localhost:1234/v1/", "qwen3-4b-instruct-2507-q5-k-m");
        var models = await sut.ListModelsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "qwen3-4b-instruct-2507-q5-k-m", "qwen3-4b-instruct-2507-q4-k-m" },
            models);
    }

    [Fact]
    public async Task ListModelsAsync_reads_llama_cpp_models_format_and_deduplicates_case_insensitively()
    {
        var sut = CreateClient(
            """
            {
              "models": [
                { "name": "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf" },
                { "name": "Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf" },
                { "model": "qwen_qwen3-4b-instruct-2507-q4_k_m.gguf" }
              ]
            }
            """);

        sut.Configure("http://localhost:1234/v1", "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf");
        var models = await sut.ListModelsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf", "Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf" },
            models);
    }

    [Fact]
    public async Task ListModelsAsync_merges_data_and_models_payloads()
    {
        var sut = CreateClient(
            """
            {
              "data": [
                { "id": "qwen3-4b-instruct-2507-q5-k-m" }
              ],
              "models": [
                { "name": "Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf" }
              ]
            }
            """);

        var models = await sut.ListModelsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "qwen3-4b-instruct-2507-q5-k-m", "Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf" },
            models);
    }

    [Fact]
    public async Task ChatOnceAsync_ensures_managed_runtime_before_request()
    {
        var ensureCalled = false;
        var handler = new StubHttpHandler(
            """
            {
              "choices": [
                { "message": { "content": "ok" } }
              ]
            }
            """,
            HttpMethod.Post,
            assertBeforeResponse: () => Assert.True(ensureCalled));

        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure("http://localhost:1234/v1", "model");
        sut.RuntimeEnsureReady += _ =>
        {
            ensureCalled = true;
            return Task.CompletedTask;
        };

        var answer = await sut.ChatOnceAsync(
            new[] { ("user", "hello") },
            temperature: 0.1,
            maxTokens: 16,
            CancellationToken.None);

        Assert.Equal("ok", answer);
        Assert.True(ensureCalled);
    }

    [Fact]
    public async Task CountNativeInputTokensAsync_uses_the_runtime_chat_template_with_tools()
    {
        string? requestBody = null;
        var handler = new InputTokenCountHandler(
            request => requestBody = request.Content!
                .ReadAsStringAsync()
                .GetAwaiter()
                .GetResult());
        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure("http://localhost:1234/v1", "qwen3");
        var tool = new SourceBackedAgentToolDefinition(
            "rag_search",
            "Search indexed evidence.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string" }
                }
            }));

        var count = await sut.CountNativeInputTokensAsync(
            new[]
            {
                SourceBackedAgentMessage.System("System contract."),
                SourceBackedAgentMessage.User("Find the source.")
            },
            new[] { tool },
            CancellationToken.None,
            requireToolCall: true);

        Assert.Equal(321, count);
        using var payload = JsonDocument.Parse(requestBody ?? "{}");
        Assert.Equal("qwen3", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Single(payload.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal(
            "required",
            payload.RootElement.GetProperty("tool_choice").GetString());
        Assert.False(payload.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(payload.RootElement.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task ChatOnceNativeAsync_recovers_one_well_formed_embedded_call_when_a_tool_is_required()
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content =
                            "<tool_call>{\"name\":\"submit_selection\",\"arguments\":{\"evidenceIds\":[\"E1\",\"E2\"]}}</tool_call>texte parasite"
                    },
                    finish_reason = "length"
                }
            }
        });
        var handler = new StubHttpHandler(body, HttpMethod.Post);
        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure("http://localhost:1234/v1", "qwen3");
        var tool = new SourceBackedAgentToolDefinition(
            "submit_selection",
            "Submit evidence.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    evidenceIds = new
                    {
                        type = "array",
                        items = new { type = "string" }
                    }
                }
            }));

        var completion = await sut.ChatOnceNativeAsync(
            new[] { SourceBackedAgentMessage.User("Select.") },
            new[] { tool },
            temperature: 0,
            maxTokens: 96,
            CancellationToken.None,
            requireToolCall: true);

        var call = Assert.Single(completion.ToolCalls);
        Assert.Equal("submit_selection", call.Name);
        Assert.Equal(
            new[] { "E1", "E2" },
            call.Arguments.GetProperty("evidenceIds")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
    }

    [Fact]
    public async Task ChatOnceNativeAsync_does_not_promote_embedded_text_when_a_tool_is_optional()
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content =
                            "<tool_call>{\"name\":\"submit_selection\",\"arguments\":{}}</tool_call>"
                    },
                    finish_reason = "stop"
                }
            }
        });
        var handler = new StubHttpHandler(body, HttpMethod.Post);
        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure("http://localhost:1234/v1", "qwen3");
        var tool = new SourceBackedAgentToolDefinition(
            "submit_selection",
            "Submit evidence.",
            JsonSerializer.SerializeToElement(new { type = "object" }));

        var completion = await sut.ChatOnceNativeAsync(
            new[] { SourceBackedAgentMessage.User("Maybe select.") },
            new[] { tool },
            temperature: 0,
            maxTokens: 96,
            CancellationToken.None,
            requireToolCall: false);

        Assert.Empty(completion.ToolCalls);
    }

    [Fact]
    public async Task GetNativeRuntimeContextTokensAsync_reads_llama_cpp_active_context()
    {
        var handler = new StubHttpHandler(
            """
            {
              "data": [{
                "id": "C:\\Models\\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
                "meta": {
                  "n_ctx": 8192,
                  "n_ctx_train": 262144
                }
              }]
            }
            """,
            HttpMethod.Get);
        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure(
            "http://localhost:1234/v1",
            "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf");

        var contextTokens = await sut.GetNativeRuntimeContextTokensAsync(
            CancellationToken.None);

        Assert.Equal(8192, contextTokens);
    }

    [Fact]
    public async Task ChatOnceAsync_sends_conservative_generation_controls()
    {
        string? requestBody = null;
        var handler = new StubHttpHandler(
            """
            {
              "choices": [
                { "message": { "content": "ok" } }
              ]
            }
            """,
            HttpMethod.Post,
            assertRequest: request => requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure("http://localhost:1234/v1", "model");

        _ = await sut.ChatOnceAsync(
            new[] { ("user", "hello") },
            temperature: 0.1,
            maxTokens: 16,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(requestBody ?? "{}");
        var root = payload.RootElement;
        Assert.Equal(0.85, root.GetProperty("top_p").GetDouble(), precision: 3);
        Assert.Equal(0.2, root.GetProperty("frequency_penalty").GetDouble(), precision: 3);
        Assert.Equal(0.05, root.GetProperty("presence_penalty").GetDouble(), precision: 3);
        Assert.Contains(root.GetProperty("stop").EnumerateArray(), item => item.GetString() == "\nTOOL_RESULTS");
        Assert.False(root.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task ChatOnceAsync_merges_multiple_system_messages_for_strict_model_templates()
    {
        string? requestBody = null;
        var handler = new StubHttpHandler(
            """{"choices":[{"message":{"content":"ok"}}]}""",
            HttpMethod.Post,
            assertRequest: request => requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure("http://localhost:1234/v1", "model");

        await sut.ChatOnceAsync(
            new[]
            {
                ("system", "Return valid JSON."),
                ("system", "Apply the focused planner contract."),
                ("user", "Plan this request.")
            },
            temperature: 0.1,
            maxTokens: 64,
            CancellationToken.None,
            forceJson: true);

        using var payload = JsonDocument.Parse(requestBody ?? "{}");
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(
            "Return valid JSON.\n\nApply the focused planner contract.",
            messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task ChatOnceAsync_requests_json_object_response_format_when_forced()
    {
        string? requestBody = null;
        var handler = new StubHttpHandler(
            """
            {
              "choices": [
                { "message": { "content": "{\"status\":\"ok\"}" } }
              ]
            }
            """,
            HttpMethod.Post,
            assertRequest: request => requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure("http://localhost:1234/v1", "model");

        var answer = await sut.ChatOnceAsync(
            new[] { ("user", "return a status object") },
            temperature: 0.1,
            maxTokens: 64,
            CancellationToken.None,
            forceJson: true);

        using var payload = JsonDocument.Parse(requestBody ?? "{}");
        Assert.Equal("json_object", payload.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("{\"status\":\"ok\"}", answer);
    }

    [Fact]
    public async Task ChatOnceStructuredAsync_requests_named_strict_json_schema()
    {
        string? requestBody = null;
        var handler = new StubHttpHandler(
            """
            {
              "choices": [
                { "message": { "content": "{\"status\":\"ok\"}" } }
              ]
            }
            """,
            HttpMethod.Post,
            assertRequest: request => requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure("http://localhost:1234/v1", "model");
        var contract = LlmStructuredOutputContract.Parse(
            "status_contract",
            """{"type":"object","properties":{"status":{"type":"string","enum":["ok"]}},"required":["status"],"additionalProperties":false}""");

        var answer = await sut.ChatOnceStructuredAsync(
            new[] { ("user", "Return status.") },
            temperature: 0.1,
            maxTokens: 64,
            contract,
            CancellationToken.None);

        Assert.Equal("{\"status\":\"ok\"}", answer);
        using var payload = JsonDocument.Parse(requestBody ?? "{}");
        var format = payload.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var namedSchema = format.GetProperty("json_schema");
        Assert.Equal("status_contract", namedSchema.GetProperty("name").GetString());
        Assert.True(namedSchema.GetProperty("strict").GetBoolean());
        Assert.Equal("object", namedSchema.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(0d, payload.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(1d, payload.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(0d, payload.RootElement.GetProperty("frequency_penalty").GetDouble());
        Assert.Equal(0d, payload.RootElement.GetProperty("presence_penalty").GetDouble());
    }

    [Fact]
    public async Task ChatOnceStructuredCompletionAsync_preserves_usage_and_server_timings()
    {
        var handler = new StubHttpHandler(
            """
            {
              "choices": [
                {
                  "message": { "content": "{\"status\":\"ok\"}" },
                  "finish_reason": "stop"
                }
              ],
              "usage": { "prompt_tokens": 397, "completion_tokens": 131 },
              "timings": {
                "cache_n": 23,
                "prompt_n": 374,
                "prompt_ms": 2917.86,
                "predicted_n": 131,
                "predicted_ms": 15279.95
              }
            }
            """,
            HttpMethod.Post);
        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure("http://localhost:1234/v1", "model");
        var contract = LlmStructuredOutputContract.Parse(
            "status_contract",
            """{"type":"object","properties":{"status":{"type":"string","enum":["ok"]}},"required":["status"],"additionalProperties":false}""");

        var method = typeof(OpenAiLlmClient).GetMethods()
            .SingleOrDefault(static candidate =>
                candidate.Name == "ChatOnceStructuredCompletionAsync");
        Assert.NotNull(method);
        var completionTask = Assert.IsType<Task<SourceBackedAgentCompletion>>(
            method!.Invoke(
                sut,
                new object[]
                {
                    new[] { ("user", "Return status.") },
                    0.1,
                    64,
                    contract,
                    CancellationToken.None
                }));
        var completion = await completionTask;

        Assert.Equal("{\"status\":\"ok\"}", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(397, completion.PromptTokens);
        Assert.Equal(131, completion.CompletionTokens);
        Assert.Equal(23, completion.ServerCacheTokens);
        Assert.Equal(374, completion.ServerPromptTokensEvaluated);
        Assert.Equal(2917.86, completion.ServerPromptMilliseconds);
        Assert.Equal(131, completion.ServerPredictedTokens);
        Assert.Equal(15279.95, completion.ServerPredictedMilliseconds);
    }

    [Fact]
    public async Task ChatOnceStructuredAsync_honors_explicit_native_sampling_profile()
    {
        var names = new[]
        {
            LlmSamplingConfiguration.TemperatureEnvironmentVariable,
            LlmSamplingConfiguration.TopPEnvironmentVariable,
            LlmSamplingConfiguration.TopKEnvironmentVariable,
            LlmSamplingConfiguration.MinPEnvironmentVariable,
            LlmSamplingConfiguration.FrequencyPenaltyEnvironmentVariable,
            LlmSamplingConfiguration.PresencePenaltyEnvironmentVariable,
            LlmSamplingConfiguration.StructuredSamplingEnvironmentVariable
        };
        var previous = names.ToDictionary(
            static name => name,
            static name => Environment.GetEnvironmentVariable(name));
        string? requestBody = null;

        try
        {
            Environment.SetEnvironmentVariable(LlmSamplingConfiguration.TemperatureEnvironmentVariable, "0.7");
            Environment.SetEnvironmentVariable(LlmSamplingConfiguration.TopPEnvironmentVariable, "0.8");
            Environment.SetEnvironmentVariable(LlmSamplingConfiguration.TopKEnvironmentVariable, "20");
            Environment.SetEnvironmentVariable(LlmSamplingConfiguration.MinPEnvironmentVariable, "0");
            Environment.SetEnvironmentVariable(LlmSamplingConfiguration.FrequencyPenaltyEnvironmentVariable, "0");
            Environment.SetEnvironmentVariable(LlmSamplingConfiguration.PresencePenaltyEnvironmentVariable, "0");
            Environment.SetEnvironmentVariable(LlmSamplingConfiguration.StructuredSamplingEnvironmentVariable, "native");

            var handler = new StubHttpHandler(
                """{"choices":[{"message":{"content":"{\"status\":\"ok\"}"}}]}""",
                HttpMethod.Post,
                assertRequest: request => requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var sut = new OpenAiLlmClient(new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:1234")
            });
            sut.Configure("http://localhost:1234/v1", "model");
            var contract = LlmStructuredOutputContract.Parse(
                "status_contract",
                """{"type":"object","properties":{"status":{"type":"string","enum":["ok"]}},"required":["status"],"additionalProperties":false}""");

            await sut.ChatOnceStructuredAsync(
                new[] { ("user", "Return status.") },
                temperature: 0.1,
                maxTokens: 64,
                contract,
                CancellationToken.None);

            using var payload = JsonDocument.Parse(requestBody ?? "{}");
            var root = payload.RootElement;
            Assert.Equal(0.7d, root.GetProperty("temperature").GetDouble(), precision: 3);
            Assert.Equal(0.8d, root.GetProperty("top_p").GetDouble(), precision: 3);
            Assert.Equal(20, root.GetProperty("top_k").GetInt32());
            Assert.Equal(0d, root.GetProperty("min_p").GetDouble());
            Assert.Equal(0d, root.GetProperty("frequency_penalty").GetDouble());
            Assert.Equal(0d, root.GetProperty("presence_penalty").GetDouble());
        }
        finally
        {
            foreach (var entry in previous)
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }

    [Fact]
    public async Task ChatOnceAsync_retries_without_response_format_when_endpoint_rejects_it()
    {
        var requestBodies = new List<string>();
        var handler = new SequencedChatHandler(requestBodies);
        var sut = new OpenAiLlmClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:1234")
        });
        sut.Configure("http://localhost:1234/v1", "model");

        var answer = await sut.ChatOnceAsync(
            new[] { ("user", "return a status object") },
            temperature: 0.1,
            maxTokens: 64,
            CancellationToken.None,
            forceJson: true);

        Assert.Equal("{\"status\":\"fallback\"}", answer);
        Assert.Equal(2, requestBodies.Count);
        using var first = JsonDocument.Parse(requestBodies[0]);
        using var second = JsonDocument.Parse(requestBodies[1]);
        Assert.True(first.RootElement.TryGetProperty("response_format", out _));
        Assert.False(second.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public void LlmAdapter_keeps_broad_documentary_writer_budget_within_configured_output_limit()
    {
        var prompt = """
PRIVATE_SOURCE_COVERAGE_NOTE:
The request needs a broad structured answer.

PRIVATE_SOURCE_WRITING_BRIEF:
Use the retrieved evidence to write a clean final answer.

REQUESTED_STRUCTURE:
weekly plan with several slots
""";

        var broadBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: false,
            prompt);
        var normalBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: false,
            prompt: "simple chat answer");
        var jsonBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 3600,
            forceJson: true,
            prompt);
        var lowConfiguredJsonBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 350,
            forceJson: true,
            prompt);

        Assert.Equal(1600, broadBudget);
        Assert.Equal(1600, normalBudget);
        Assert.Equal(1600, jsonBudget);
        Assert.Equal(512, lowConfiguredJsonBudget);
    }

    [Fact]
    public void LlmAdapter_bounds_document_overview_writer_on_the_real_agent_path()
    {
        var overviewBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: false,
            prompt: "SAAIA_DOCUMENT_OVERVIEW_WRITER\nCANONICAL_EVIDENCE:");
        var alreadyLowerBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 180,
            forceJson: false,
            prompt: "SAAIA_DOCUMENT_OVERVIEW_WRITER");
        var selectorBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: false,
            prompt: "SAAIA_DOCUMENT_OVERVIEW_SELECTOR");
        var repairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: false,
            prompt: "SAAIA_DOCUMENT_OVERVIEW_CANDIDATE_REPAIR");

        Assert.Equal(320, overviewBudget);
        Assert.Equal(180, alreadyLowerBudget);
        Assert.Equal(64, selectorBudget);
        Assert.Equal(96, repairBudget);
    }

    [Fact]
    public void LlmAdapter_uses_smaller_json_budgets_for_source_backed_control_steps()
    {
        var plannerBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=Planner");
        var plannerDiversityRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerDiversityRepair");
        var plannerIntakeReviewBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeReview");
        var plannerColumnAxisAdjudicationBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisAdjudication");
        var plannerColumnAxisContractRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisContractRepair");
        var plannerIntakeShapeBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeShapeAdjudication");
        var plannerColumnConflictSelectionBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisSelectionPatch");
        var plannerRowHeaderRoleBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeRowAxisHeaderRolePatch");
        var plannerColumnAnchorPatchBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisAnchorPatch");
        var plannerColumnCompletenessBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisCompletenessReview");
        var plannerColumnMissingAnchorsBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisMissingAnchors");
        var plannerFacetAssignmentPatchBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerFacetAssignmentPatch");
        var plannerCompactFacetPlanBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerCompactFacetPlan");
        var plannerCompactFacetPlanRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerCompactFacetPlanFormatRepair");
        var plannerCompactFacetPlanContractRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerCompactFacetPlanContractRepair");
        var plannerAcceptedQueryAuditBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryAudit");
        var plannerAcceptedQueryAuditRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryAuditContractRepair");
        var plannerAcceptedQuerySourceDomainBriefBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBrief");
        var plannerAcceptedQuerySourceDomainSemanticRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBriefSemanticRepair");
        var plannerAcceptedQuerySourceDomainDeliverableReviewBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBriefDeliverableReviewFinal");
        var plannerAcceptedQuerySourceDomainReusableItemClassReviewBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBriefReusableItemClassReviewFinal");
        var plannerAcceptedQuerySourceDomainFacetCoverageReviewBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBriefFacetCoverageReviewFinal");
        var plannerAcceptedQuerySourceDomainAttributesReviewBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBriefAttributesReviewFinal");
        var plannerAcceptedScopeCoherenceBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedScopeCoherenceReview");
        var plannerAcceptedScopeCoherenceRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedScopeCoherenceReviewContractRepair");
        var plannerAcceptedQueryYieldReviewBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryYieldReview");
        var plannerAcceptedQueryYieldRetryReviewBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryYieldRetryReview");
        var plannerAcceptedQueryYieldRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryYieldRepair");
        var plannerRowIndependentQueryRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerRowIndependentQueryRepair");
        var plannerFormatRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerFormatRepair");
        var plannerDiversityRetryBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=PlannerDiversityRetry");
        var judgeBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=EvidenceJudge");
        var actionRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=EvidenceJudgeActionRepair");
        var statusBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=EvidenceStatusReview");
        var statusActionRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=EvidenceStatusReviewActionRepair");
        var statusRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=EvidenceStatusReviewFormatRepair");
        var selectionRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=EvidenceJudgeSelectionRepair");
        var finalSelectionAtomicRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=EvidenceJudgeFinalSelectionAtomicRepair");
        var structuredValueTypeFitBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=StructuredValueTypeFitJudge");
        var structuredValueTypeAtomicRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=StructuredValueTypeFitAtomicRepair");
        var structuredThinCellAtomicRepairBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=StructuredThinCellAtomicRepair");
        var writerBudget = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
            configuredMaxTokens: 1600,
            forceJson: true,
            prompt: "SAAIA_SOURCE_BACKED_STEP=Writer");

        Assert.Equal(480, plannerBudget);
        Assert.Equal(640, plannerDiversityRepairBudget);
        Assert.Equal(480, plannerIntakeReviewBudget);
        Assert.Equal(256, plannerColumnAxisAdjudicationBudget);
        Assert.Equal(256, plannerColumnAxisContractRepairBudget);
        Assert.Equal(256, plannerIntakeShapeBudget);
        Assert.Equal(256, plannerColumnConflictSelectionBudget);
        Assert.Equal(256, plannerRowHeaderRoleBudget);
        Assert.Equal(480, plannerColumnAnchorPatchBudget);
        Assert.Equal(256, plannerColumnCompletenessBudget);
        Assert.Equal(256, plannerColumnMissingAnchorsBudget);
        Assert.Equal(256, plannerFacetAssignmentPatchBudget);
        Assert.Equal(640, plannerCompactFacetPlanBudget);
        Assert.Equal(640, plannerCompactFacetPlanRepairBudget);
        Assert.Equal(640, plannerCompactFacetPlanContractRepairBudget);
        Assert.Equal(640, plannerAcceptedQueryAuditBudget);
        Assert.Equal(640, plannerAcceptedQueryAuditRepairBudget);
        Assert.Equal(256, plannerAcceptedQuerySourceDomainBriefBudget);
        Assert.Equal(256, plannerAcceptedQuerySourceDomainSemanticRepairBudget);
        Assert.Equal(256, plannerAcceptedQuerySourceDomainDeliverableReviewBudget);
        Assert.Equal(256, plannerAcceptedQuerySourceDomainReusableItemClassReviewBudget);
        Assert.Equal(256, plannerAcceptedQuerySourceDomainFacetCoverageReviewBudget);
        Assert.Equal(256, plannerAcceptedQuerySourceDomainAttributesReviewBudget);
        Assert.Equal(256, plannerAcceptedScopeCoherenceBudget);
        Assert.Equal(256, plannerAcceptedScopeCoherenceRepairBudget);
        Assert.Equal(256, plannerAcceptedQueryYieldReviewBudget);
        Assert.Equal(256, plannerAcceptedQueryYieldRetryReviewBudget);
        Assert.Equal(480, plannerAcceptedQueryYieldRepairBudget);
        Assert.Equal(480, plannerRowIndependentQueryRepairBudget);
        Assert.Equal(640, plannerFormatRepairBudget);
        Assert.Equal(640, plannerDiversityRetryBudget);
        Assert.Equal(400, judgeBudget);
        Assert.Equal(400, actionRepairBudget);
        Assert.Equal(320, statusBudget);
        Assert.Equal(96, statusActionRepairBudget);
        Assert.Equal(320, statusRepairBudget);
        Assert.Equal(400, selectionRepairBudget);
        Assert.Equal(256, finalSelectionAtomicRepairBudget);
        Assert.Equal(640, structuredValueTypeFitBudget);
        Assert.Equal(160, structuredValueTypeAtomicRepairBudget);
        Assert.Equal(480, structuredThinCellAtomicRepairBudget);
        Assert.Equal(900, writerBudget);
    }

    [Fact]
    public void LlmAdapter_scopes_native_json_response_format_to_evidence_status_steps()
    {
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=EvidenceStatusReview"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=EvidenceStatusReviewActionRepair"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=EvidenceStatusReviewFormatRepair"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=EvidenceStatusReviewDecisionContractRepair"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=StructuredValueTypeFitJudge"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=StructuredValueTypeFitAtomicRepair"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=EvidenceJudgeFinalSelectionAtomicRepair"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=StructuredThinCellAtomicRepair"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryYieldReview"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryYieldReviewContractRepair"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryYieldReviewFinal"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBrief"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBriefContractRepair"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBriefDeliverableReviewFinal"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBriefReusableItemClassReviewFinal"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBriefFacetCoverageReviewFinal"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQuerySourceDomainBriefAttributesReviewFinal"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedScopeAudit"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedScopeAuditContractRepair"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedScopeAuditContractRetry"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedScopeCoherenceReview"));
        Assert.True(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedScopeCoherenceReviewContractRepair"));
        Assert.False(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=Planner"));
        Assert.False(RagChatAgent.ShouldUseLlmAdapterJsonResponseFormatForTests(
            "SAAIA_SOURCE_BACKED_STEP=WriterStructuredTable"));
    }

    private static OpenAiLlmClient CreateClient(string body)
    {
        var http = new HttpClient(new StubHttpHandler(body, HttpMethod.Get))
        {
            BaseAddress = new Uri("http://localhost:1234")
        };

        return new OpenAiLlmClient(http);
    }

    private sealed class StubHttpHandler(
        string body,
        HttpMethod expectedMethod,
        Action? assertBeforeResponse = null,
        Action<HttpRequestMessage>? assertRequest = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            assertBeforeResponse?.Invoke();
            Assert.Equal(expectedMethod, request.Method);
            if (expectedMethod == HttpMethod.Get)
                Assert.EndsWith("/models", request.RequestUri!.AbsoluteUri);
            else
                Assert.EndsWith("/chat/completions", request.RequestUri!.AbsoluteUri);
            assertRequest?.Invoke(request);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SequencedChatHandler(List<string> requestBodies) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requestBodies.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            _requestCount++;
            if (_requestCount == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("response_format unsupported", Encoding.UTF8, "text/plain")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"{\\\"status\\\":\\\"fallback\\\"}\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class InputTokenCountHandler(
        Action<HttpRequestMessage> assertRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith(
                "/chat/completions/input_tokens",
                request.RequestUri!.AbsoluteUri);
            assertRequest(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"object":"response.input_tokens","input_tokens":321}""",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}

[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
