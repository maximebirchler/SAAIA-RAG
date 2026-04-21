using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CapabilityBBackofficeSummaryServiceTests
{
    [Fact]
    public async Task BuildSummaryAsync_uses_llm_summary_when_runtime_returns_text()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "Operational summary for IND570: Scope and Purpose frames the control perimeter. Operators should review the PLC exchange guardrails before deployment."
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "ATEX/IND570.pdf",
                DocName: "IND570.pdf",
                Category: "atex",
                PageCount: 12,
                IndexedVersion: 1),
            ["Scope and Purpose", "PLC Integration"],
            ["The operational perimeter describes the required safety controls for deployment."],
            CancellationToken.None);

        Assert.Contains("Operational summary for IND570", payload.SummaryText, StringComparison.Ordinal);
        Assert.False(payload.Meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.Equal("llm_document_foundation", payload.Meta.GetProperty("strategy").GetString());
        Assert.True(payload.Meta.GetProperty("llmDurationMs").GetInt64() >= 0);
        Assert.True(payload.Meta.GetProperty("llmResponseHeadersMs").GetInt64() >= 0);
        Assert.True(payload.Meta.GetProperty("llmFirstResponseMs").GetInt64() >= 0);
        Assert.True(payload.Meta.GetProperty("llmBytesRead").GetInt64() > 0);
        Assert.InRange(payload.Meta.GetProperty("qualityScore").GetDouble(), 0.0, 1.0);
        var qualitySignals = payload.Meta.GetProperty("qualitySignals");
        Assert.True(qualitySignals.GetProperty("lineCount").GetInt32() >= 1);
        Assert.True(qualitySignals.GetProperty("sectionCoverageScore").GetDouble() >= 0.0);
        Assert.True(qualitySignals.GetProperty("keywordCoverageScore").GetDouble() >= 0.0);
    }

    [Fact]
    public async Task BuildSummaryAsync_falls_back_to_deterministic_summary_when_runtime_is_unavailable()
    {
        var service = CreateService(
            """
            {
              "error": "runtime_unavailable"
            }
            """,
            HttpStatusCode.ServiceUnavailable);

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "ATEX/IND570.pdf",
                DocName: "IND570.pdf",
                Category: "atex",
                PageCount: 12,
                IndexedVersion: 1),
            ["Scope and Purpose"],
            ["The operational perimeter describes the required safety controls for deployment."],
            CancellationToken.None);

        Assert.Contains("IND570.pdf is an indexed atex document", payload.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.True(payload.Meta.GetProperty("fallbackUsed").GetBoolean());
        Assert.Equal("llm_empty_response", payload.Meta.GetProperty("fallbackReason").GetString());
        Assert.InRange(payload.Meta.GetProperty("qualityScore").GetDouble(), 0.0, 1.0);
        Assert.True(payload.Meta.GetProperty("qualitySignals").GetProperty("matchedSectionCount").GetInt32() >= 1);
    }

    private static CapabilityBBackofficeSummaryService CreateService(string body, HttpStatusCode statusCode)
        => new(
            new LocalLlmChatClient(
                new StubHttpClientFactory(body, statusCode),
                new ChatOptions
                {
                    LlmBaseUrl = "http://llm.test/",
                    LlmModel = "local"
                }),
            new ChatOptions
            {
                LlmBaseUrl = "http://llm.test/",
                LlmModel = "local"
            });

    private sealed class StubHttpClientFactory(string body, HttpStatusCode statusCode) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(body, statusCode))
            {
                BaseAddress = new Uri("http://llm.test/")
            };
    }

    private sealed class StubHttpMessageHandler(string body, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
