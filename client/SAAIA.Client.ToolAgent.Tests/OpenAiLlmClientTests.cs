using System.Net;
using System.Net.Http;
using System.Text;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class OpenAiLlmClientTests
{
    [Fact]
    public async Task ListModelsAsync_reads_openai_data_format()
    {
        var sut = CreateClient(
            """
            {
              "data": [
                { "id": "qwen2.5-3b-instruct-q4-k-m" },
                { "id": "mistral-7b-instruct-v0.3-q4-k-m" }
              ]
            }
            """);

        sut.Configure("http://localhost:1234/v1/", "qwen2.5-3b-instruct-q4-k-m");
        var models = await sut.ListModelsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "qwen2.5-3b-instruct-q4-k-m", "mistral-7b-instruct-v0.3-q4-k-m" },
            models);
    }

    [Fact]
    public async Task ListModelsAsync_reads_llama_cpp_models_format_and_deduplicates_case_insensitively()
    {
        var sut = CreateClient(
            """
            {
              "models": [
                { "name": "Qwen2.5-3B-Instruct-Q4_K_M.gguf" },
                { "model": "qwen2.5-3b-instruct-q4_k_m.gguf" },
                { "name": "gemma-4-E2B-it-Q4_K_M.gguf" }
              ]
            }
            """);

        sut.Configure("http://localhost:1234/v1", "Qwen2.5-3B-Instruct-Q4_K_M.gguf");
        var models = await sut.ListModelsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "Qwen2.5-3B-Instruct-Q4_K_M.gguf", "gemma-4-E2B-it-Q4_K_M.gguf" },
            models);
    }

    [Fact]
    public async Task ListModelsAsync_merges_data_and_models_payloads()
    {
        var sut = CreateClient(
            """
            {
              "data": [
                { "id": "qwen2.5-3b-instruct-q4-k-m" }
              ],
              "models": [
                { "name": "gemma-4-E2B-it-Q4_K_M.gguf" }
              ]
            }
            """);

        var models = await sut.ListModelsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "qwen2.5-3b-instruct-q4-k-m", "gemma-4-E2B-it-Q4_K_M.gguf" },
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
        Action? assertBeforeResponse = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            assertBeforeResponse?.Invoke();
            Assert.Equal(expectedMethod, request.Method);
            if (expectedMethod == HttpMethod.Get)
                Assert.EndsWith("/models", request.RequestUri!.AbsoluteUri);
            else
                Assert.EndsWith("/chat/completions", request.RequestUri!.AbsoluteUri);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
