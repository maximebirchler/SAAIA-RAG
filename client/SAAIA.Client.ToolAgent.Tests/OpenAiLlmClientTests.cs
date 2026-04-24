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

    private static OpenAiLlmClient CreateClient(string body)
    {
        var http = new HttpClient(new StubHttpHandler(body))
        {
            BaseAddress = new Uri("http://localhost:1234")
        };

        return new OpenAiLlmClient(http);
    }

    private sealed class StubHttpHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/models", request.RequestUri!.AbsoluteUri);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
