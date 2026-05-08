using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class LocalLlmChatClientTests
{
    [Fact]
    public async Task TryCompleteWithTelemetryAsync_reads_text_content_blocks_from_array_payload()
    {
        var client = CreateClient(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": [
                      { "type": "text", "text": "Line 1" },
                      { "type": "reasoning", "text": "ignore me" },
                      { "type": "text", "text": "Line 2" }
                    ]
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Equal("Line 1" + Environment.NewLine + "Line 2", result.Content);
        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
        Assert.Null(result.Error);
        Assert.True(result.BytesRead > 0);
        Assert.NotNull(result.ResponseHeadersMs);
        Assert.NotNull(result.FirstByteMs);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_returns_choices_missing_when_success_payload_has_no_choices()
    {
        var client = CreateClient("""{ "id": "chatcmpl_test" }""", HttpStatusCode.OK);

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Null(result.Content);
        Assert.Equal("choices_missing", result.Error);
        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
        Assert.True(result.BytesRead > 0);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_returns_content_missing_when_choice_message_has_no_content()
    {
        var client = CreateClient(
            """
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Null(result.Content);
        Assert.Equal("content_missing", result.Error);
        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
        Assert.True(result.BytesRead > 0);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_returns_empty_body_when_success_response_has_no_payload()
    {
        var client = CreateClient(string.Empty, HttpStatusCode.OK);

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Null(result.Content);
        Assert.Equal("empty_body", result.Error);
        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_returns_exception_when_success_payload_is_not_valid_json()
    {
        var client = CreateClient("not-json-at-all", HttpStatusCode.OK);

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Null(result.Content);
        Assert.Equal("exception", result.Error);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_classifies_unrequested_cancellation_as_timeout()
    {
        var client = CreateClient(
            """{ "choices": [ { "message": { "content": "unused" } } ] }""",
            HttpStatusCode.OK,
            (_, _) => throw new TaskCanceledException("llm timeout"));

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Null(result.Content);
        Assert.Equal("llm_timeout", result.Error);
        Assert.Null(result.StatusCode);
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_classifies_http_request_exception_as_transport_error()
    {
        var client = CreateClient(
            """{ "choices": [ { "message": { "content": "unused" } } ] }""",
            HttpStatusCode.OK,
            (_, _) => throw new HttpRequestException("connection refused"));

        var result = await client.TryCompleteWithTelemetryAsync(
            "system",
            "user",
            maxTokens: 128,
            temperature: 0.1,
            CancellationToken.None);

        Assert.Null(result.Content);
        Assert.Equal("llm_transport_error", result.Error);
        Assert.Null(result.StatusCode);
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_sends_minimal_openai_chat_completion_payload()
    {
        HttpMethod? capturedMethod = null;
        string? capturedUri = null;
        string? capturedMediaType = null;
        string? capturedBody = null;
        var client = CreateClient(
            """
            { "choices": [ { "message": { "content": "done" } } ] }
            """,
            HttpStatusCode.OK,
            async (request, _) =>
            {
                capturedMethod = request.Method;
                capturedUri = request.RequestUri?.ToString();
                capturedMediaType = request.Content?.Headers.ContentType?.MediaType;
                capturedBody = await request.Content!.ReadAsStringAsync();
            });

        var result = await client.TryCompleteWithTelemetryAsync(
            "system prompt",
            "user prompt",
            maxTokens: 8192,
            temperature: 0.25,
            CancellationToken.None);

        Assert.Equal("done", result.Content);
        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.Equal("http://llm.test/v1/chat/completions", capturedUri);
        Assert.Equal("application/json", capturedMediaType);

        using var json = JsonDocument.Parse(capturedBody!);
        var root = json.RootElement;
        Assert.Equal(4, root.EnumerateObject().Count());
        Assert.Equal("local", root.GetProperty("model").GetString());
        Assert.Equal(0.25, root.GetProperty("temperature").GetDouble());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());

        var messages = root.GetProperty("messages");
        Assert.Equal(JsonValueKind.Array, messages.ValueKind);
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system prompt", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("user prompt", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_acquires_and_releases_queue_slot_on_success()
    {
        await WithQueuedClientAsync(
            queueLimit: 1,
            async (client, queueManager, plan, factory) =>
            {
                var observedActive = 0;
                var observedAvailableSlots = -1;

                factory.OnSendAsync = (_, _) =>
                {
                    var snapshot = queueManager.GetSnapshot(plan);
                    observedActive = snapshot.Active;
                    observedAvailableSlots = snapshot.AvailableSlots;
                    return Task.CompletedTask;
                };

                var result = await client.TryCompleteWithTelemetryAsync(
                    "system",
                    "user",
                    maxTokens: 128,
                    temperature: 0.1,
                    CancellationToken.None);

                Assert.Equal("ok", result.Content);
                Assert.Null(result.Error);
                Assert.Equal(1, observedActive);
                Assert.Equal(0, observedAvailableSlots);

                var after = queueManager.GetSnapshot(plan);
                Assert.Equal(0, after.Active);
                Assert.Equal(1, after.AvailableSlots);
                Assert.Equal(0, after.Queued);
            },
            """
            { "choices": [ { "message": { "content": "ok" } } ] }
            """,
            HttpStatusCode.OK);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_releases_queue_slot_on_http_error()
    {
        await WithQueuedClientAsync(
            queueLimit: 1,
            async (client, queueManager, plan, factory) =>
            {
                var observedActive = 0;

                factory.OnSendAsync = (_, _) =>
                {
                    observedActive = queueManager.GetSnapshot(plan).Active;
                    return Task.CompletedTask;
                };

                var result = await client.TryCompleteWithTelemetryAsync(
                    "system",
                    "user",
                    maxTokens: 128,
                    temperature: 0.1,
                    CancellationToken.None);

                Assert.Null(result.Content);
                Assert.Equal("http_503", result.Error);
                Assert.Equal((int)HttpStatusCode.ServiceUnavailable, result.StatusCode);
                Assert.Equal(1, observedActive);

                var after = queueManager.GetSnapshot(plan);
                Assert.Equal(0, after.Active);
                Assert.Equal(1, after.AvailableSlots);
                Assert.Equal(0, after.Queued);
            },
            """{ "error": "temporarily unavailable" }""",
            HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_returns_queue_full_when_slot_unavailable_and_queue_is_disabled()
    {
        await WithQueuedClientAsync(
            queueLimit: 0,
            async (client, queueManager, plan, factory) =>
            {
                using var heldLease = queueManager.TryAcquire("other-worker", plan);
                Assert.NotNull(heldLease);
                var sendCount = 0;
                factory.OnSendAsync = (_, _) =>
                {
                    sendCount++;
                    return Task.CompletedTask;
                };

                var result = await client.TryCompleteWithTelemetryAsync(
                    "system",
                    "user",
                    maxTokens: 128,
                    temperature: 0.1,
                    CancellationToken.None);

                Assert.Null(result.Content);
                Assert.Equal("llm_queue_full", result.Error);
                Assert.Equal((int)HttpStatusCode.TooManyRequests, result.StatusCode);
                Assert.Equal(0, result.BytesRead);
                Assert.Equal(0, sendCount);

                var snapshot = queueManager.GetSnapshot(plan);
                Assert.Equal(1, snapshot.Active);
                Assert.Equal(0, snapshot.AvailableSlots);
                Assert.Equal(0, snapshot.Queued);
            },
            """
            { "choices": [ { "message": { "content": "should not be sent" } } ] }
            """,
            HttpStatusCode.OK);
    }

    [Fact]
    public async Task TryCompleteWithTelemetryAsync_rethrows_requested_cancellation_and_releases_queue_slot()
    {
        await WithQueuedClientAsync(
            queueLimit: 1,
            async (client, queueManager, plan, factory) =>
            {
                using var cts = new CancellationTokenSource();
                factory.OnSendAsync = (_, token) =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException(token);
                };

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TryCompleteWithTelemetryAsync(
                    "system",
                    "user",
                    maxTokens: 128,
                    temperature: 0.1,
                    cts.Token));

                var snapshot = queueManager.GetSnapshot(plan);
                Assert.Equal(0, snapshot.Active);
                Assert.Equal(1, snapshot.AvailableSlots);
                Assert.Equal(0, snapshot.Queued);
            },
            """
            { "choices": [ { "message": { "content": "should not be read" } } ] }
            """,
            HttpStatusCode.OK);
    }

    [Fact]
    public async Task CapabilityBLiveRuntimeProbe_uses_configured_chat_model()
    {
        string? capturedBody = null;
        var factory = new StubHttpClientFactory(
            """
            { "choices": [ { "message": { "content": "ready" } } ] }
            """,
            HttpStatusCode.OK)
        {
            OnSendAsync = async (request, _) =>
            {
                capturedBody = await request.Content!.ReadAsStringAsync();
            }
        };

        var result = await CapabilityBLiveRuntimeProbe.ProbeAsync(
            factory,
            new ChatOptions { LlmModel = "configured-backoffice-model" },
            CancellationToken.None);

        Assert.True(result.Available);
        Assert.NotNull(capturedBody);
        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("configured-backoffice-model", body.RootElement.GetProperty("model").GetString());
    }

    private static LocalLlmChatClient CreateClient(
        string body,
        HttpStatusCode statusCode,
        Func<HttpRequestMessage, CancellationToken, Task>? onSendAsync = null)
        => new(
            new StubHttpClientFactory(body, statusCode) { OnSendAsync = onSendAsync },
            new ChatOptions
            {
                LlmBaseUrl = "http://llm.test/",
                LlmModel = "local"
            });

    private static async Task WithQueuedClientAsync(
        int queueLimit,
        Func<LocalLlmChatClient, RuntimeLlmQueueManager, AdminRuntimeLlmCapacityPlanDto, StubHttpClientFactory, Task> test,
        string body,
        HttpStatusCode statusCode)
    {
        var previousPath = Environment.GetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH");
        var tempRoot = Path.Combine(Path.GetTempPath(), "saaia-local-llm-client-" + Guid.NewGuid().ToString("N"));
        var planPath = Path.Combine(tempRoot, "llm.capacity-plan.json");

        try
        {
            Directory.CreateDirectory(tempRoot);
            await File.WriteAllTextAsync(planPath, $$"""
            {
              "version": "test",
              "plannedAt": "2026-05-06T00:00:00Z",
              "licenseSeats": 1,
              "profile": "test",
              "modelId": "local",
              "instances": 1,
              "slotsPerInstance": 1,
              "totalSlots": 1,
              "queueLimit": {{queueLimit}},
              "perUserActiveLimit": 1,
              "perUserQueuedLimit": 1
            }
            """);
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", planPath);

            var queueManager = new RuntimeLlmQueueManager();
            var capacityPlanService = new RuntimeLlmCapacityPlanService(
                new StubHostEnvironment { ContentRootPath = tempRoot },
                Options.Create(new LicenseOptions { Seats = 1 }));
            var plan = await capacityPlanService.GetQueuePlanAsync(CancellationToken.None);
            Assert.NotNull(plan);

            var factory = new StubHttpClientFactory(body, statusCode);
            var client = new LocalLlmChatClient(
                factory,
                new ChatOptions
                {
                    LlmBaseUrl = "http://llm.test/",
                    LlmModel = "local"
                },
                capacityPlanService,
                queueManager);

            await test(client, queueManager, plan!, factory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLM_CAPACITY_PLAN_PATH", previousPath);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class StubHttpClientFactory(string body, HttpStatusCode statusCode) : IHttpClientFactory
    {
        public Func<HttpRequestMessage, CancellationToken, Task>? OnSendAsync { get; set; }

        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(body, statusCode, OnSendAsync))
            {
                BaseAddress = new Uri("http://llm.test/")
            };
    }

    private sealed class StubHttpMessageHandler(
        string body,
        HttpStatusCode statusCode,
        Func<HttpRequestMessage, CancellationToken, Task>? onSendAsync) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (onSendAsync is not null)
                await onSendAsync(request, cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SAAIA.Backend.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
