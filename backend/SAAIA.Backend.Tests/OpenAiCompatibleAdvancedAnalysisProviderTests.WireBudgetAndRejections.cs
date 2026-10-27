using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task WireDocumentaryBudget_projection_accounts_for_the_actual_escaped_user_text(string protocol)
    {
        var canonical = "é😀\\\r\n" + new string('"', 4_000);
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseAnswer(CandidateTerminal) : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 14_000;
        var logger = new WireBudgetLogger();
        var run = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", canonical)), CancellationToken.None);
        var error = await Record.ExceptionAsync(async () => await run);
        Assert.True(error is null, error + "\n" + string.Join("\n", logger.Lines.Where(line => line.Contains("documentary_wire_characters", StringComparison.Ordinal))));
        var body = factory.Requests[^1].Body;
        Assert.Equal(2, factory.Requests.Count);
        var item = Assert.Single(WorkspaceUser(body).GetProperty("evidence").EnumerateArray());
        AssertWireTextWindow(item, canonical);
        Assert.True(item.GetProperty("contentTruncated").GetBoolean());
        Assert.Equal(item.GetProperty("content").GetString()!.Length, item.GetProperty("nextContentOffset").GetInt32());
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 14_000);
    }

    private sealed class WireBudgetLogger : ILogger<OpenAiCompatibleAdvancedAnalysisProvider>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    [Theory]
    [InlineData("length", "advanced_llm_output_limit")]
    [InlineData("content_filter", "advanced_llm_content_filtered")]
    public async Task WireRejectionTrace_chat_nonterminal_text_retains_the_exact_rejected_exchange(string finishReason, string expected)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                choices = new[] { new { finish_reason = finishReason,
                    message = new { role = "assistant", content = NativeAnswered } } },
                usage = new { prompt_tokens = 100, completion_tokens = 50 }
            }) });
        var options = CreateOptions();
        options.DevelopmentTraceDirectory = Path.Combine(Path.GetTempPath(), "saaia-rejected-wire-" + Guid.NewGuid().ToString("N"));
        options.DevelopmentTraceStrict = true;
        try
        {
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(),
                    new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None));
            Assert.Equal(expected, error.ErrorCode);
            Assert.Equal(2, factory.Requests.Count);
            Assert.Equal(2, Directory.GetFiles(options.DevelopmentTraceDirectory, "*.json").Length);
            var path = Assert.Single(Directory.GetFiles(options.DevelopmentTraceDirectory, "*-writer-*.json"));
            using var trace = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(factory.Requests[^1].Body, trace.RootElement.GetProperty("requestJson").GetString());
            Assert.Equal("message.content.rejected", trace.RootElement.GetProperty("completionOrigin").GetString());
            Assert.Equal(expected, trace.RootElement.GetProperty("normalizationError").GetString());
            Assert.Equal(NativeAnswered, trace.RootElement.GetProperty("completionJson").GetString());
            using var response = JsonDocument.Parse(trace.RootElement.GetProperty("responseEnvelopeJson").GetString()!);
            Assert.Equal(finishReason, response.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        }
        finally
        {
            if (Directory.Exists(options.DevelopmentTraceDirectory)) Directory.Delete(options.DevelopmentTraceDirectory, true);
        }
    }
}
