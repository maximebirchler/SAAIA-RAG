using System.Net;
using System.Text;
using Xunit;

public sealed class TeiInputAdmissionTests
{
    [Theory]
    [InlineData("{\"message\":\"`inputs` must have less than 128000 characters. Given: 130000\"}")]
    [InlineData("{\"error\":\"Input validation error: `inputs` must have less than 128000 characters. Given: 130000\"}")]
    public async Task Character_limit_refusal_does_not_invent_a_token_measurement(string response)
    {
        using var client = new HttpClient(new ReplyHandler(response)) { BaseAddress = new Uri("http://127.0.0.1:1") };
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            TeiClient.CountInputTokensAsync(client, "private-source-sentinel", CancellationToken.None));
        Assert.Equal("tei_input_character_limit_exceeded", error.Data["error_code"]);
        Assert.Equal(128000, error.Data["max_characters"]);
        Assert.Equal(130000, error.Data["observed_characters"]);
        Assert.Null(error.Data["observed_tokens"]);
        Assert.DoesNotContain("private-source-sentinel", error.ToString());
    }

    [Theory]
    [InlineData("{\"message\":\"`inputs` must have less than 512 tokens. Given: 551\",\"type\":\"validation\"}")]
    [InlineData("{\"error\":\"`inputs` must have less than 512 tokens. Given: 551\",\"error_type\":\"validation\"}")]
    [InlineData("{\"message\":\"Input validation error: `inputs` must have less than 512 tokens. Given: 551\",\"type\":\"validation\"}")]
    [InlineData("{\"error\":\"Input validation error: `inputs` must have less than 512 tokens. Given: 551\",\"error_type\":\"validation\"}")]
    public async Task Token_limit_refusal_retains_only_typed_safe_counts(string response)
    {
        using var client = new HttpClient(new ReplyHandler(response)) { BaseAddress = new Uri("http://127.0.0.1:1") };
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            TeiClient.EmbedAsync(client, "model", ["private-source-sentinel"], CancellationToken.None));

        Assert.Equal("tei_input_token_limit_exceeded", error.Data["error_code"]);
        Assert.Equal(512, error.Data["max_tokens"]);
        Assert.Equal(551, error.Data["observed_tokens"]);
        Assert.DoesNotContain("private-source-sentinel", error.ToString());
    }

    [Fact]
    public async Task Arbitrary_error_body_is_not_a_diagnostic_or_an_inner_exception()
    {
        using var client = new HttpClient(new ReplyHandler("{\"message\":\"private-source-sentinel\"}")) { BaseAddress = new Uri("http://127.0.0.1:1") };
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            TeiClient.EmbedAsync(client, "model", ["source"], CancellationToken.None));
        Assert.DoesNotContain("private-source-sentinel", error.ToString());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, error.StatusCode);
    }

    private sealed class ReplyHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
    }
}
