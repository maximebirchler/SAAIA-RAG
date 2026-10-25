using System.Net;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(429, false)]
    [InlineData(408, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    public async Task Ambiguous_http_failure_keeps_a_conservative_charge_while_known_rejections_are_zero(int status, bool uncertain)
    {
        using var factory = new QueuedHttpClientFactory(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("{\"error\":{\"code\":\"synthetic_failure\"}}")
        });
        var options = CreateOptions();
        options.Provider = "openai-dev";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "https://api.example/v1";
        options.LlmMaximumHttpAttempts = 1;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-test-key");
        await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => provider.ExecuteAsync(BuildRequest(), new RecordingToolGateway(), CancellationToken.None));
        using var charge = JsonDocument.Parse(Assert.Single(File.ReadAllLines(options.ExternalUsageLedgerPath)));
        Assert.Equal(uncertain, charge.RootElement.GetProperty("costUsd").GetDecimal() > 0);
        Assert.Equal(uncertain ? "reserved_upper_bound" : "provider_http_rejected", charge.RootElement.GetProperty("usageSource").GetString());
        Assert.Single(factory.Requests);
    }
}
