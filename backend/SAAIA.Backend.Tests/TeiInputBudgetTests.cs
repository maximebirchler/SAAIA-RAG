using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed class TeiInputBudgetTests
{
    [Fact]
    public async Task Runtime_limit_is_observed_not_inferred_from_model_name_or_words()
    {
        using var client = Client("{\"max_input_length\":384,\"auto_truncate\":false,\"max_client_batch_size\":16}");
        var budget = await TeiClient.GetInputBudgetAsync(client, CancellationToken.None);
        Assert.Equal(384, budget.MaximumInputTokens);
        Assert.Equal(16, budget.MaximumClientBatchSize);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"max_input_length\":512}")]
    [InlineData("{\"max_input_length\":\"512\",\"auto_truncate\":false}")]
    [InlineData("{\"max_input_length\":0,\"auto_truncate\":false}")]
    [InlineData("{\"max_input_length\":512,\"auto_truncate\":\"false\"}")]
    [InlineData("{\"max_input_length\":512,\"auto_truncate\":false,\"max_client_batch_size\":0}")]
    public async Task Missing_or_invalid_admission_contract_is_not_an_estimated_tokenizer(string response)
    {
        using var client = Client(response);
        await Assert.ThrowsAsync<InvalidDataException>(() => TeiClient.GetInputBudgetAsync(client, CancellationToken.None));
    }

    [Fact]
    public async Task Runtime_silent_truncation_is_refused_before_source_publication()
    {
        using var client = Client("{\"max_input_length\":512,\"auto_truncate\":true}");
        var error = await Assert.ThrowsAsync<TeiRequestException>(() => TeiClient.GetInputBudgetAsync(client, CancellationToken.None));
        Assert.Equal("tei_silent_truncation_runtime_refused", error.ErrorCode);
    }

    [Fact]
    public async Task Tokenize_receives_final_input_and_includes_special_tokens_without_truncation()
    {
        string? received = null;
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("/tokenize", request.RequestUri!.AbsolutePath);
            received = await request.Content!.ReadAsStringAsync();
            return Json("[[{\"id\":0,\"special\":true},{\"id\":4,\"special\":false},{\"id\":2,\"special\":true}]]");
        })) { BaseAddress = new Uri("http://127.0.0.1:1") };
        Assert.Equal(3, await TeiClient.CountInputTokensAsync(client, "passage: source with context", CancellationToken.None));
        using var body = JsonDocument.Parse(received!);
        Assert.Equal("passage: source with context", body.RootElement.GetProperty("inputs").GetString());
        Assert.True(body.RootElement.GetProperty("add_special_tokens").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("truncate", out _));
        Assert.False(body.RootElement.TryGetProperty("prompt_name", out _));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[[]]")]
    [InlineData("[[],[]]")]
    [InlineData("[[{\"id\":1}]]")]
    [InlineData("[[{\"id\":\"1\",\"special\":true}]]")]
    [InlineData("[[{\"id\":1,\"special\":\"true\"}]]")]
    public async Task Tokenization_protocol_must_bind_one_complete_input(string response)
    {
        using var client = Client(response);
        await Assert.ThrowsAsync<InvalidDataException>(() => TeiClient.CountInputTokensAsync(client, "source", CancellationToken.None));
    }

    [Fact]
    public async Task Tokenize_missing_does_not_fall_back_to_word_count()
    {
        using var client = Client("private-source-sentinel", HttpStatusCode.NotFound);
        var error = await Assert.ThrowsAsync<TeiRequestException>(() => TeiClient.CountInputTokensAsync(client, "source", CancellationToken.None));
        Assert.Equal("tei_tokenize_http_error", error.ErrorCode);
        Assert.DoesNotContain("private-source-sentinel", error.ToString());
    }

    [Fact]
    public async Task Oversized_error_is_bounded_and_does_not_leak_its_body()
    {
        using var client = Client("private-source-sentinel" + new string('x', 20_000), HttpStatusCode.RequestEntityTooLarge);
        var error = await Assert.ThrowsAsync<TeiRequestException>(() => TeiClient.EmbedAsync(client, "model", ["source"], CancellationToken.None));
        Assert.Equal("tei_embeddings_http_error", error.ErrorCode);
        Assert.DoesNotContain("private-source-sentinel", error.ToString());
    }

    private static HttpClient Client(string response, HttpStatusCode status = HttpStatusCode.OK)
        => new(new Handler(_ => Task.FromResult(Json(response, status)))) { BaseAddress = new Uri("http://127.0.0.1:1") };
    private static HttpResponseMessage Json(string response, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
    }
}
