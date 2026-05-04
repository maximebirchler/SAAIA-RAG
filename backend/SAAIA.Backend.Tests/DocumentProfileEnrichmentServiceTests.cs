using System.Net;
using System.Text;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DocumentProfileEnrichmentServiceTests
{
    [Fact]
    public async Task BuildEnrichedProfileAsync_uses_llm_json_and_preserves_baseline_terms()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"language\":\"en\",\"summary\":\"LLM profile: IND570 describes PLC exchange controls and operational safety checks.\",\"keywords\":[\"plc exchange\",\"safety checks\"],\"entities\":[\"IND570\"],\"topics\":[\"PLC Integration\"],\"questions\":[\"How does IND570 handle PLC exchange controls?\"],\"limits\":[\"Use page chunks for exact parameters.\"],\"cards\":[{\"title\":\"PLC exchange controls\",\"pageStart\":7,\"pageEnd\":8,\"kind\":\"llm_content_card\",\"signals\":[\"plc exchange\",\"controls\"]}]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            RevisionId: Guid.NewGuid(),
            DocId: Guid.NewGuid(),
            ProfileVersion: "deterministic_v1",
            Language: "en",
            SummaryText: "Deterministic baseline profile.",
            Keywords: ["baseline-keyword"],
            Entities: ["EN 15281"],
            Topics: ["Baseline Topic"],
            HypotheticalQuestions: ["What does the document say about baseline-keyword?"],
            Limits: ["Use page chunks for exact facts."],
            SearchText: "baseline-keyword EN 15281",
            TokenCount: 3,
            ContentCards:
            [
                new DocumentProfileContentCard(
                    "Baseline card",
                    2,
                    2,
                    "section",
                    ["baseline-keyword"])
            ]);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                DocId: baseline.DocId,
                DocPath: "Programmation/IND570.pdf",
                DocName: "IND570.pdf",
                Category: "programmation",
                PageCount: 42,
                IndexedVersion: 1),
            baseline,
            ["PLC Integration"],
            ["The IND570 exchanges PLC control words and status data."],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("llm_backoffice_v1", profile!.ProfileVersion);
        Assert.Equal("en", profile.Language);
        Assert.Contains("LLM profile", profile.SummaryText, StringComparison.Ordinal);
        Assert.Contains("plc exchange", profile.Keywords);
        Assert.Contains("baseline-keyword", profile.Keywords);
        Assert.Contains("IND570", profile.Entities);
        Assert.Contains("EN 15281", profile.Entities);
        Assert.Contains("How does IND570 handle PLC exchange controls?", profile.HypotheticalQuestions);
        Assert.Contains("Use page chunks for exact parameters.", profile.Limits);
        Assert.Contains("plc exchange", profile.SearchText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "PLC exchange controls", StringComparison.Ordinal));
        Assert.Contains(profile.ContentCards, card => string.Equals(card.Title, "Baseline card", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_ignores_schema_language_placeholder()
    {
        var service = CreateService(
            """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"language\":\"fr|en|es|pt|de|it|und\",\"summary\":\"Profil compact.\",\"keywords\":[\"compact\"],\"entities\":[],\"topics\":[],\"questions\":[],\"limits\":[]}"
                  }
                }
              ]
            }
            """,
            HttpStatusCode.OK);

        var baseline = new DocumentProfileSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "deterministic_v1",
            "fr",
            "Profil deterministe.",
            ["base"],
            [],
            [],
            [],
            [],
            "base",
            1);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(baseline.DocId, "Cuisine/Test.pdf", "Test.pdf", "cuisine", 1, 1),
            baseline,
            [],
            [],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Equal("fr", profile!.Language);
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_returns_null_when_runtime_is_unavailable()
    {
        var service = CreateService("""{ "error": "runtime_unavailable" }""", HttpStatusCode.ServiceUnavailable);
        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(Guid.NewGuid(), "A/B.pdf", "B.pdf", "a", 1, 1),
            new DocumentProfileSnapshot(Guid.NewGuid(), Guid.NewGuid(), "deterministic_v1", "und", "baseline", [], [], [], [], [], "baseline", 1),
            [],
            [],
            CancellationToken.None);

        Assert.Null(profile);
    }

    private static DocumentProfileEnrichmentService CreateService(string body, HttpStatusCode statusCode)
        => new(new LocalLlmChatClient(
            new StubHttpClientFactory(body, statusCode),
            new ChatOptions
            {
                LlmBaseUrl = "http://llm.test/",
                LlmModel = "local"
            }));

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
