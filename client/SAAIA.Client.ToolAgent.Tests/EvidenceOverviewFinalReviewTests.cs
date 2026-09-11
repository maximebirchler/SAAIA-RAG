using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class EvidenceOverviewFinalReviewTests
{
    private const string DocId = "dddddddd-1111-2222-3333-444444444444";
    private const string RevisionId = "aaaaaaaa-1111-2222-3333-444444444444";
    private static readonly string Hash = new('a', 64);
    private const string Question = "Résume en deux phrases les paramètres documentés du module Atlas.";

    [Fact]
    public async Task Overview_cannot_publish_new_text_without_independent_review_of_its_sources()
    {
        var llm = new ReviewCanaryLlm();
        var sut = Create(llm);
        JsonElement? returned = null;
        var error = await Record.ExceptionAsync(async () => returned = await RunAsync(sut));
        var summary = returned is { } payload && payload.TryGetProperty("summaryText", out var value)
            ? value.GetString() : "NO_SUMMARY";
        Assert.True(error is InvalidOperationException,
            $"No independent review: writerCalls={llm.WriterCalls}; returnedSummary={summary}");
        Assert.Equal("Overview final-text review reached.", error!.Message);
        Assert.Equal(1, llm.WriterCalls);
        Assert.Equal(1, llm.ReviewCalls);
        Assert.Contains(Question, llm.ReviewContext, StringComparison.Ordinal);
        Assert.Contains("999 unités fictives", llm.ReviewContext, StringComparison.Ordinal);
        Assert.Contains("73 unités fictives", llm.ReviewContext, StringComparison.Ordinal);
        Assert.Contains("12 minutes", llm.ReviewContext, StringComparison.Ordinal);
        Assert.True(llm.ReviewWasTerminal);
    }

    [Theory]
    [InlineData("accept", true)]
    [InlineData("revise", false)]
    public async Task Overview_publication_honors_the_independent_semantic_decision(string decision, bool publish)
    {
        var llm = new ReviewCanaryLlm { Decision = decision };
        var result = await RunAsync(Create(llm));
        Assert.Equal(publish ? 1 : 2, llm.WriterCalls);
        Assert.Equal(publish ? 1 : 2, llm.ReviewCalls);
        Assert.True(llm.ReviewWasTerminal);
        Assert.Equal(publish, result.TryGetProperty("summaryText", out var summary));
        if (publish)
        {
            Assert.Contains("73 unités fictives", summary.GetString(), StringComparison.Ordinal);
            Assert.Equal(2, result.GetProperty("anchors").GetArrayLength());
        }
        else
        {
            Assert.Equal("document_overview_semantic_review_failed", result.GetProperty("error").GetString());
            Assert.Equal("revise", result.GetProperty("decision").GetString());
            Assert.False(result.TryGetProperty("anchors", out _));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overview_revises_once_and_reviews_the_new_text_before_publication(bool correctionWorks)
    {
        var llm = new ReviewCanaryLlm { Decision = "revise", CorrectOnRevision = correctionWorks };
        var result = await RunAsync(Create(llm));
        Assert.Equal(2, llm.WriterCalls);
        Assert.Equal(2, llm.ReviewCalls);
        Assert.Equal(0, llm.GenericWriterCalls);
        Assert.True(llm.WriterWasTerminal);
        Assert.Contains(Question, llm.RevisionContext, StringComparison.Ordinal);
        Assert.Contains("999", llm.RevisionContext, StringComparison.Ordinal);
        Assert.Contains("73 unités fictives", llm.RevisionContext, StringComparison.Ordinal);
        Assert.Contains("La pression 999 contredit la valeur documentée 73.", llm.RevisionContext, StringComparison.Ordinal);
        Assert.Equal(correctionWorks, result.TryGetProperty("summaryText", out var summary));
        if (correctionWorks)
        {
            Assert.Contains("73 unités fictives", summary.GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("999", summary.GetString(), StringComparison.Ordinal);
            Assert.Contains("73 unités fictives", llm.ReviewContext, StringComparison.Ordinal);
            Assert.DoesNotContain("999", llm.ReviewContext, StringComparison.Ordinal);
            Assert.Equal(2, result.GetProperty("anchors").GetArrayLength());
        }
        else
            Assert.Equal("document_overview_semantic_review_failed", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Overview_does_not_publish_or_review_a_revision_with_an_invalid_source_contract()
    {
        var llm = new ReviewCanaryLlm { Decision = "revise", MalformedRevision = true };
        var result = await RunAsync(Create(llm));
        Assert.Equal(2, llm.WriterCalls);
        Assert.Equal(1, llm.ReviewCalls);
        Assert.False(result.TryGetProperty("summaryText", out _));
        Assert.False(result.TryGetProperty("anchors", out _));
    }

    [Fact]
    public async Task Overview_contract_and_semantic_repairs_share_one_writer_attempt()
    {
        var llm = new ReviewCanaryLlm { Decision = "revise", InitialUnsupportedObligation = true };
        var result = await RunAsync(Create(llm));
        Assert.Equal(2, llm.WriterCalls);
        Assert.Equal(1, llm.ReviewCalls);
        Assert.Equal(0, llm.GenericWriterCalls);
        Assert.False(result.TryGetProperty("summaryText", out _));
        Assert.Equal("document_overview_semantic_review_failed", result.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(3712, 4096, true)]
    [InlineData(3713, 4096, false)]
    [InlineData(1800, 2048, false)]
    public async Task Overview_writer_checks_measured_runtime_context_before_generation(int inputTokens, int runtimeContext, bool fits)
    {
        var llm = new ReviewCanaryLlm { Decision = "accept", CountedWriterInputTokens = inputTokens, RuntimeContextTokens = runtimeContext };
        JsonElement? result = null;
        var error = await Record.ExceptionAsync(async () => result = await RunAsync(Create(llm)));
        if (fits)
        {
            Assert.Null(error);
            Assert.True(result!.Value.TryGetProperty("summaryText", out _));
            Assert.Equal(1, llm.WriterCalls);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("measured context budget", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, llm.WriterCalls);
            Assert.Equal(0, llm.ReviewCalls);
        }
        Assert.Equal(0, llm.GenericWriterCalls);
    }

    [Fact]
    public async Task Overview_rejects_an_invented_citation_even_when_the_text_would_be_semantically_accepted()
    {
        var llm = new ReviewCanaryLlm { Decision = "accept", InitialUnknownCitation = true };
        var result = await RunAsync(Create(llm));
        Assert.Equal(1, llm.WriterCalls);
        Assert.Equal(0, llm.ReviewCalls);
        Assert.False(result.TryGetProperty("summaryText", out _));
        Assert.False(result.TryGetProperty("anchors", out _));
        Assert.Equal("document_overview_candidate_contract_failed", result.GetProperty("error").GetString());
    }

    private static Task<JsonElement> RunAsync(ToolAgentOrchestrator sut)
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            docRef = "ATLAS", strategy = "evidence_overview", language = "fr", responseLanguage = "fr",
            requestedPointCount = 2, sampleCount = 2, userRequest = Question
        });
        var method = typeof(ToolAgentOrchestrator).GetMethod("ExecRagSummarizeLiveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<JsonElement>)method.Invoke(sut, [args, CancellationToken.None])!;
    }

    private static ToolAgentOrchestrator Create(ReviewCanaryLlm llm)
    {
        var api = new ApiClient();
        api.Configure("http://localhost:5122", "synthetic-api-key", "synthetic-user");
        typeof(ApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(api, new HttpClient(new SourceHandler()) { BaseAddress = new Uri("http://localhost:5122") });
        var memory = new ToolMemory { LastLanguage = "fr" };
        memory.PdfMap["ATLAS"] = new ToolMemory.DocumentItem
        {
            DocId = DocId, DocName = "Atlas.pdf", DocPath = "Knowledge/Atlas.pdf", SourceHash = Hash,
            DocLanguage = "fr", ProfileLanguage = "fr", Pages = 2, PdfRef = "ATLAS"
        };
        return new ToolAgentOrchestrator(api, llm, memory, new AppSettings { ActiveMode = "strict" });
    }

    private sealed class SourceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var document = new { docId = DocId, docName = "Atlas.pdf", docPath = "Knowledge/Atlas.pdf", sourceHash = Hash,
                revisionId = RevisionId, docLanguage = "fr", profileLanguage = "fr", pageCount = 2 };
            object payload;
            if (request.RequestUri!.AbsolutePath == "/sources/resolve")
                payload = new { source = document };
            else if (request.RequestUri.AbsolutePath == "/documents/context")
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var page = int.Parse(query["pageStart"]!);
                var text = page == 1
                    ? "La pression du module Atlas est de 73 unités fictives. Ce paramètre décrit le réglage documenté du module."
                    : "La durée documentée du cycle du module Atlas est de 12 minutes. Ce paramètre concerne le cycle complet.";
                payload = new { document, items = new[] { new { docId = DocId, docName = "Atlas.pdf", docPath = "Knowledge/Atlas.pdf",
                    revisionId = RevisionId, sourceHash = Hash, chunkId = "atlas-chunk-" + page, chunkIndex = page,
                    pageStart = page, pageEnd = page, text, fullText = text, contentRole = "content", tokenCount = 70 } } };
            }
            else throw new InvalidOperationException("Unexpected fixture endpoint: " + request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ReviewCanaryLlm : ILlmClient, ISourceBackedAgentLlmClient,
        ISourceBackedAgentInputTokenCounter, ISourceBackedAgentRuntimeContextProvider
    {
        public string? Decision { get; init; }
        public bool? CorrectOnRevision { get; init; }
        public bool MalformedRevision { get; init; }
        public bool InitialUnsupportedObligation { get; init; }
        public bool InitialUnknownCitation { get; init; }
        public int CountedWriterInputTokens { get; init; } = 100;
        public int RuntimeContextTokens { get; init; } = 4096;
        public int WriterCalls { get; private set; }
        public int GenericWriterCalls { get; private set; }
        public int ReviewCalls { get; private set; }
        public string RevisionContext { get; private set; } = "";
        public bool WriterWasTerminal { get; private set; }
        public string ReviewContext { get; private set; } = "";
        public bool ReviewWasTerminal { get; private set; }
        public Task<int?> GetRuntimeContextTokensAsync(CancellationToken ct) => Task.FromResult<int?>(RuntimeContextTokens);
        public Task<int?> CountInputTokensAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, CancellationToken ct, bool requireToolCall = false)
            => Task.FromResult<int?>(tools.Count == 0 ? CountedWriterInputTokens : 100);
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
        {
            GenericWriterCalls++;
            Assert.False(forceJson);
            return Task.FromResult(Write(messages.Select(message => message.content)));
        }
        private string Write(IEnumerable<string?> messages)
        {
            WriterCalls++;
            Assert.InRange(WriterCalls, 1, 2);
            if (WriterCalls == 2) RevisionContext = string.Join("\n", messages);
            if (WriterCalls == 1 && InitialUnknownCitation)
                return "La pression du module Atlas est de 73 unités fictives [E999].\nLa durée documentée du cycle est de 12 minutes [E2].";
            if (WriterCalls == 2 && MalformedRevision) return "Une valeur documentaire incorrectement citée [E999].";
            if (InitialUnsupportedObligation)
                return WriterCalls == 1
                    ? "Le module Atlas doit utiliser une pression de 73 unités fictives [E1].\nLa durée documentée du cycle est de 12 minutes [E2]."
                    : "La pression du module Atlas est de 73 unités fictives [E1].";
            var pressure = Decision == "accept" || (CorrectOnRevision == true && WriterCalls == 2) ? "73" : "999";
            return $"La pression du module Atlas est de {pressure} unités fictives.\nLa durée documentée du cycle est de 12 minutes.";
        }
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false)
        {
            if (tools.Count == 0)
            {
                WriterWasTerminal = SourceBackedLlmCumulativeBudgetContext.IsTerminalNativeCall(tools);
                Assert.Equal(InitialUnsupportedObligation && WriterCalls == 1 ? 96 : 320, maxTokens);
                Assert.False(requireToolCall);
                return Task.FromResult(new SourceBackedAgentCompletion(Write(messages.Select(message => message.Content)), [], "stop"));
            }
            ReviewCalls++;
            Assert.Equal("submit_semantic_review", Assert.Single(tools).Name);
            ReviewContext = string.Join("\n", messages.Select(message => message.Content));
            ReviewWasTerminal = SourceBackedLlmCumulativeBudgetContext.IsTerminalNativeCall(tools);
            if (Decision is null)
                throw new InvalidOperationException("Overview final-text review reached.");
            var decision = CorrectOnRevision == true && WriterCalls == 2 ? "accept" : Decision;
            return Task.FromResult(new SourceBackedAgentCompletion("", [new SourceBackedAgentToolCall(
                "review", "submit_semantic_review", JsonSerializer.SerializeToElement(new
                {
                    decision,
                    reasons = new[] { decision == "accept" ? "Les deux paramètres sont fidèles aux preuves." : "La pression 999 contredit la valeur documentée 73." },
                    rejectedEvidenceIds = Array.Empty<string>(), preferredAlternativeEvidenceIds = Array.Empty<string>()
                }))], "tool_calls"));
        }
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
