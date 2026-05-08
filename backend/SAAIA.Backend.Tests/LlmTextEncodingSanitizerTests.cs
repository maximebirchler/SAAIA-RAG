using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class LlmTextEncodingSanitizerTests
{
    [Fact]
    public async Task BuildSummaryAsync_repairs_mojibake_llm_text_before_payload()
    {
        var service = CreateSummaryService(BuildChatResponse(
            "R\u00c3\u00a9sum\u00c3\u00a9: le contr\u00c3\u00b4le qualit\u00c3\u00a9 contient les proc\u00c3\u00a9dures de pr\u00c3\u00a9paration."));

        var payload = await service.BuildSummaryAsync(
            new CapabilityBDocumentRow(
                DocId: Guid.NewGuid(),
                DocPath: "Qualite/Controle.pdf",
                DocName: "Controle.pdf",
                Category: "qualite",
                PageCount: 3,
                IndexedVersion: 1,
                ProfileLanguage: "fr"),
            ["Controle qualite"],
            ["Le controle qualite contient les procedures de preparation."],
            CancellationToken.None,
            preferredLanguage: "fr");

        Assert.Contains("R\u00e9sum\u00e9", payload.SummaryText, StringComparison.Ordinal);
        Assert.Contains("contr\u00f4le qualit\u00e9", payload.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("\u00c3", payload.SummaryText, StringComparison.Ordinal);
        Assert.False(payload.Meta.GetProperty("fallbackUsed").GetBoolean());
    }

    [Fact]
    public async Task BuildEnrichedProfileAsync_repairs_mojibake_llm_json_strings_before_projection()
    {
        var profileJson = JsonSerializer.Serialize(new
        {
            language = "fr",
            summary = "R\u00c3\u00a9sum\u00c3\u00a9 du contr\u00c3\u00b4le qualit\u00c3\u00a9 et de la pr\u00c3\u00a9paration.",
            keywords = new[] { "pr\u00c3\u00a9paration", "contr\u00c3\u00b4le qualit\u00c3\u00a9" },
            entities = new[] { "Service Qualit\u00c3\u00a9" },
            topics = new[] { "contr\u00c3\u00b4le qualit\u00c3\u00a9" },
            questions = new[] { "Contr\u00c3\u00b4le qualit\u00c3\u00a9 pr\u00c3\u00a9paration ?" },
            limits = new[] { "contr\u00c3\u00b4le qualit\u00c3\u00a9" },
            cards = new[]
            {
                new
                {
                    title = "Contr\u00c3\u00b4le qualit\u00c3\u00a9",
                    pageStart = 1,
                    pageEnd = 1,
                    kind = "content_item",
                    signals = new[] { "pr\u00c3\u00a9paration" }
                }
            }
        });
        var service = CreateProfileService(BuildChatResponse(profileJson));
        var baseline = new DocumentProfileSnapshot(
            RevisionId: Guid.NewGuid(),
            DocId: Guid.NewGuid(),
            ProfileVersion: "deterministic_v1",
            Language: "fr",
            SummaryText: "Profil de base.",
            Keywords: [],
            Entities: [],
            Topics: [],
            HypotheticalQuestions: [],
            Limits: [],
            SearchText: "controle qualite preparation service qualite",
            TokenCount: 5);

        var profile = await service.BuildEnrichedProfileAsync(
            new CapabilityBDocumentRow(
                DocId: baseline.DocId,
                DocPath: "Qualite/Controle.pdf",
                DocName: "Controle.pdf",
                Category: "qualite",
                PageCount: 3,
                IndexedVersion: 1,
                ProfileLanguage: "fr"),
            baseline,
            ["Controle qualite"],
            ["Le controle qualite verifie la preparation avec le service qualite avant validation."],
            CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Contains("R\u00e9sum\u00e9", profile!.SummaryText, StringComparison.Ordinal);
        Assert.Contains("contr\u00f4le qualit\u00e9", profile.SummaryText, StringComparison.Ordinal);
        Assert.Contains("pr\u00e9paration", profile.Keywords);
        Assert.Contains("Service Qualit\u00e9", profile.Entities);
        Assert.Contains("Contr\u00f4le qualit\u00e9", profile.ContentCards.Select(static card => card.Title));
        Assert.DoesNotContain("\u00c3", profile.SearchText, StringComparison.Ordinal);
    }

    private static CapabilityBBackofficeSummaryService CreateSummaryService(string body)
    {
        var options = new ChatOptions
        {
            LlmBaseUrl = "http://llm.test/",
            LlmModel = "local"
        };

        return new CapabilityBBackofficeSummaryService(
            new LocalLlmChatClient(new StubHttpClientFactory(body), options),
            options);
    }

    private static DocumentProfileEnrichmentService CreateProfileService(string body)
        => new(new LocalLlmChatClient(
            new StubHttpClientFactory(body),
            new ChatOptions
            {
                LlmBaseUrl = "http://llm.test/",
                LlmModel = "local"
            }));

    private static string BuildChatResponse(string content)
        => JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content
                    }
                }
            }
        });

    private sealed class StubHttpClientFactory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(body))
            {
                BaseAddress = new Uri("http://llm.test/")
            };
    }

    private sealed class StubHttpMessageHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
