using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", false, false)]
    [InlineData("responses", true, false)]
    [InlineData("chat-completions", false, false)]
    [InlineData("chat-completions", true, false)]
    [InlineData("responses", false, true)]
    [InlineData("responses", true, true)]
    public async Task Interrupted_call_captures_exact_prepared_request_without_inventing_response_or_cost(
        string protocol, bool afterHeaders, bool callerCanceled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "saaia-interrupted-capture-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        string? body = null;
        using var factory = new OmittedHandleHttpFactory(async request =>
        {
            attempts++;
            body = await request.Content!.ReadAsStringAsync();
            if (callerCanceled) cancellation.Cancel();
            var error = new OperationCanceledException("private failure detail", cancellation.Token);
            if (afterHeaders) return CompletionStream(error);
            throw error;
        });
        var options = BudgetDiagnosticOptions(Path.Combine(directory, "usage.jsonl"));
        options.NativeResearchToolsEnabled = protocol == "responses";
        options.NativeResearchApiProtocol = protocol;
        options.DevelopmentTraceDirectory = Path.Combine(directory, "traces");
        options.DevelopmentTraceStrict = true;
        var logger = new WireBudgetLogger();
        try
        {
            var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "server-secret", logger);
            var error = await Record.ExceptionAsync(() => provider.ExecuteAsync(BuildRequest(), new RecordingToolGateway(), cancellation.Token));
            if (callerCanceled) Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.Equal("advanced_llm_timeout", Assert.IsType<AdvancedAnalysisProviderException>(error).ErrorCode);
            Assert.Equal(1, attempts);
            var path = Assert.Single(Directory.GetFiles(options.DevelopmentTraceDirectory, "*.json"));
            using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var trace = capture.RootElement;
            Assert.Equal(body, trace.GetProperty("requestJson").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body!))), trace.GetProperty("requestSha256").GetString());
            Assert.Equal("transport.failure", trace.GetProperty("completionOrigin").GetString());
            Assert.Equal(afterHeaders ? "response_processing" : "awaiting_response_headers", trace.GetProperty("transportPhase").GetString());
            Assert.Equal(callerCanceled ? "advanced_llm_canceled" : "advanced_llm_timeout", trace.GetProperty("normalizationError").GetString());
            Assert.Equal("null", trace.GetProperty("responseEnvelopeJson").GetString());
            Assert.Equal(string.Empty, trace.GetProperty("completionJson").GetString());
            Assert.Equal(JsonValueKind.Null, trace.GetProperty("chargedCostUsd").ValueKind);
            Assert.Equal(JsonValueKind.Null, trace.GetProperty("observedModelId").ValueKind);
            Assert.DoesNotContain("server-secret", trace.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("private failure detail", trace.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("private failure detail", string.Join('\n', logger.Lines), StringComparison.Ordinal);
            // A missing response is not a provider refund. Existing settlement
            // stays conservative even though no measured usage was returned.
            var ledger = Assert.Single(File.ReadAllLines(Path.Combine(directory, "usage.jsonl")));
            using var charge = JsonDocument.Parse(ledger);
            Assert.True(charge.RootElement.GetProperty("costUsd").GetDecimal() > 0m);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
