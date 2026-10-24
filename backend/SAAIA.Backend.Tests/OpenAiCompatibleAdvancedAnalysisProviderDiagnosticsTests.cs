using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string DiagnosticAnswer = """{"outcome":"answered","answerText":"Le seuil est 17 bar [C1].","claims":[{"claimId":"C1","text":"Le seuil est 17 bar.","evidenceIds":["E1"]}]}""";

    [Theory]
    [InlineData(400, false, false)]
    [InlineData(429, false, false)]
    [InlineData(400, true, false)]
    [InlineData(429, true, false)]
    [InlineData(400, false, true)]
    [InlineData(429, false, true)]
    [InlineData(400, true, true)]
    [InlineData(429, true, true)]
    public async Task Unreadable_http_rejection_preserves_status_unless_strict_capture_is_requested(int status, bool strict, bool timeout)
    {
        var directory = Path.Combine(Path.GetTempPath(), "saaia-capture-unreadable-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = CreateOptions();
            options.DevelopmentTraceDirectory = directory;
            options.DevelopmentTraceStrict = strict;
            options.LlmMaximumHttpAttempts = 1;
            var state = new AdvancedAnalysisRuntimeState();
            using var factory = new QueuedHttpClientFactory(new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new UnreadableProviderErrorContent(timeout)
            });
            var exception = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, runtimeState: state)
                    .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(), CancellationToken.None));
            Assert.Equal(strict ? "advanced_development_trace_read_failed" : $"advanced_llm_http_{status}", exception.ErrorCode);
            Assert.Single(factory.Requests);
            var health = state.GetSnapshot(TimeSpan.FromSeconds(30));
            Assert.Equal(1, health.CapturesFailed);
            Assert.Equal("advanced_development_trace_read_failed", health.LastCaptureFailureCode);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory); }
    }

    [Fact]
    public async Task Terminal_quota_error_is_classified_from_one_bounded_read_and_still_captured()
    {
        var directory = Path.Combine(Path.GetTempPath(), "saaia-capture-quota-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = CreateOptions(); options.DevelopmentTraceDirectory = directory;
            var state = new AdvancedAnalysisRuntimeState();
            using var factory = new QueuedHttpClientFactory(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"code":"insufficient_quota","message":"Quota exhausted"}}""")
            });
            var exception = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, runtimeState: state)
                    .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(), CancellationToken.None));
            Assert.Equal("advanced_llm_quota_exhausted", exception.ErrorCode);
            Assert.Single(factory.Requests);
            Assert.Equal(1, state.GetSnapshot(TimeSpan.FromSeconds(30)).CapturesSucceeded);
            using var trace = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory))));
            Assert.Contains("insufficient_quota", trace.RootElement.GetProperty("responseEnvelopeJson").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.GetFiles(directory)) File.Delete(path);
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public async Task Error_body_deadline_does_not_replace_a_known_429_with_a_generation_timeout_during_retry()
    {
        var options = CreateOptions();
        options.LlmTimeoutSeconds = 5;
        options.LlmMaximumHttpAttempts = 3;
        var rejection = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        {
            Content = new DeadlineProviderErrorContent()
        };
        rejection.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
        using var factory = new QueuedHttpClientFactory(rejection);
        var exception = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(), CancellationToken.None));
        Assert.Equal("advanced_llm_http_429", exception.ErrorCode);
        Assert.Single(factory.Requests);
    }

    private sealed class DeadlineProviderErrorContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new NotSupportedException();
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("A deadline was expected.");
        }
    }

    [Fact]
    public async Task Caller_cancellation_during_an_http_error_body_is_not_reported_as_a_collector_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var options = CreateOptions();
        options.LlmMaximumHttpAttempts = 1;
        var state = new AdvancedAnalysisRuntimeState();
        using var factory = new QueuedHttpClientFactory(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        {
            Content = new UnreadableProviderErrorContent(timeout: true, cancellation.Cancel)
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, runtimeState: state)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(), cancellation.Token));
        Assert.Equal(0, state.GetSnapshot(TimeSpan.FromSeconds(30)).CapturesFailed);
    }

    private sealed class UnreadableProviderErrorContent(bool timeout = false, Action? beforeThrow = null) : HttpContent
    {
        private Exception Failure()
        {
            beforeThrow?.Invoke();
            return timeout ? new OperationCanceledException("Injected error-body read timeout.")
                : new IOException("Injected broken provider error body.");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            Task.FromException(Failure());
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromException<Stream>(Failure());
    }

    [Fact]
    public async Task Production_exact_capture_failure_does_not_turn_successful_generation_into_failure()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "saaia-capture-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "not a directory");
        try
        {
            var options = CreateOptions(); options.DevelopmentTraceDirectory = blocker;
            var state = new AdvancedAnalysisRuntimeState();
            using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(DiagnosticAnswer));
            var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, runtimeState: state)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
            Assert.Equal("answered", result.Outcome);
            Assert.Equal(2, factory.Requests.Count);
            var health = state.GetSnapshot(TimeSpan.FromSeconds(30));
            Assert.True(health.CapturesFailed >= 2);
            Assert.Equal(0, health.CapturesSucceeded);
            Assert.Equal("advanced_development_trace_write_failed", health.LastCaptureFailureCode);
        }
        finally { File.Delete(blocker); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_capture_limit_is_diagnostic_unless_strict_acceptance_mode_is_requested(bool strict)
    {
        var directory = Path.Combine(Path.GetTempPath(), "saaia-capture-limit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = CreateOptions(); options.DevelopmentTraceDirectory = directory;
            options.DevelopmentTraceStrict = strict; options.DevelopmentTraceMaximumCharacters = 1_024;
            var state = new AdvancedAnalysisRuntimeState();
            using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(DiagnosticAnswer));
            var task = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, runtimeState: state)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
            if (strict)
                Assert.Equal("advanced_development_trace_limit_exceeded", (await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => task)).ErrorCode);
            else Assert.Equal("answered", (await task).Outcome);
            Assert.True(state.GetSnapshot(TimeSpan.FromSeconds(30)).CapturesFailed > 0);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory); }
    }

    [Fact]
    public async Task Exact_capture_retention_is_bounded_without_deleting_unrelated_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), "saaia-capture-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var unrelated = Path.Combine(directory, "operator-notes.json");
        await File.WriteAllTextAsync(unrelated, "{}");
        var uuidNotes = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-notes.json");
        var looksLikeLegacy = Path.Combine(directory, $"{Guid.NewGuid():N}-notes-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(uuidNotes, "{}");
        await File.WriteAllTextAsync(looksLikeLegacy, "{}");
        File.SetLastWriteTimeUtc(uuidNotes, DateTime.UtcNow.AddYears(-1));
        File.SetLastWriteTimeUtc(looksLikeLegacy, DateTime.UtcNow.AddYears(-1));
        try
        {
            var options = CreateOptions(); options.DevelopmentTraceDirectory = directory;
            options.DevelopmentTraceMaximumFiles = 1;
            using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(DiagnosticAnswer));
            await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(uuidNotes));
            Assert.True(File.Exists(looksLikeLegacy));
            var trace = Assert.Single(Directory.GetFiles(directory, "saaia-exact-v1-*.json"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(trace));
            Assert.True(document.RootElement.TryGetProperty("traceId", out _));
        }
        finally
        {
            foreach (var path in Directory.GetFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Internal_provider_has_a_content_free_call_journal_even_without_an_external_budget()
    {
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var options = CreateOptions();
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(DiagnosticAnswer));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "secret-auth-marker", logger)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Sensitive-evidence-marker. Le seuil est 17 bar.")), CancellationToken.None);
        var calls = logger.Events.Where(item => item.Values.TryGetValue("Operation", out var operation) && Equals(operation, "llm.call")).ToArray();
        Assert.Equal(2, calls.Length);
        foreach (var call in calls)
        {
            Assert.Equal("success", call.Values["Outcome"]);
            using var measurements = JsonDocument.Parse((string)call.Values["Measurements"]!);
            Assert.Equal(1, measurements.RootElement.GetProperty("http_attempt_count").GetInt32());
            Assert.True(measurements.RootElement.GetProperty("request_characters").GetInt32() > 0);
            Assert.Equal(64, measurements.RootElement.GetProperty("request_sha256").GetString()!.Length);
            Assert.True(measurements.RootElement.TryGetProperty("input_tokens", out _));
            Assert.DoesNotContain("Sensitive-evidence-marker", call.Text);
            Assert.DoesNotContain("secret-auth-marker", call.Text);
            Assert.DoesNotContain("Le seuil", call.Text);
        }
    }
}
