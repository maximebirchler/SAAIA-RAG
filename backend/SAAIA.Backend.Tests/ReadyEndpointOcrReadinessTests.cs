using SAAIA.Backend.Endpoints;
using System.Net;
using System.Reflection;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class ReadyEndpointOcrReadinessTests
{
    private static string AvailableCommand
        => Environment.ProcessPath is { Length: > 0 } path && File.Exists(path)
            ? path
            : throw new InvalidOperationException("Could not resolve the current test executable path.");

    [Fact]
    public void ProbeOcrReadiness_returns_ready_when_ocr_is_disabled()
    {
        var details = new Dictionary<string, object?>();

        var ready = ReadyEndpoints.ProbeOcrReadiness(
            new IngestionOptions { OcrEnabled = false },
            details);

        Assert.True(ready);
        Assert.True((bool)details["ocr_ready"]!);
        Assert.False((bool)details["ocr_configured"]!);
        Assert.Equal("disabled", details["ocr_status"]);
        Assert.False((bool)details["ocr_required"]!);
    }

    [Fact]
    public void ProbeOcrReadiness_fails_when_enabled_command_is_missing()
    {
        var details = new Dictionary<string, object?>();

        var ready = ReadyEndpoints.ProbeOcrReadiness(
            new IngestionOptions
            {
                OcrEnabled = true,
                OcrCommand = "saaia-missing-ocr-command-for-test",
                OcrImagePageEnabled = false
            },
            details);

        Assert.False(ready);
        Assert.False((bool)details["ocr_ready"]!);
        Assert.False((bool)details["ocr_command_available"]!);
        Assert.Equal("not_ready", details["ocr_status"]);
        Assert.True((bool)details["ocr_required"]!);
        Assert.Contains("ocr_command_unavailable", (string)details["ocr_error"]!);
    }

    [Fact]
    public void ProbeOcrReadiness_returns_ready_when_enabled_command_is_available_and_image_ocr_is_disabled()
    {
        var details = new Dictionary<string, object?>();
        var availableCommand = AvailableCommand;

        var ready = ReadyEndpoints.ProbeOcrReadiness(
            new IngestionOptions
            {
                OcrEnabled = true,
                OcrCommand = availableCommand,
                OcrImagePageEnabled = false,
                OcrImageTextCommand = "saaia-missing-language-probe-for-test",
                OcrLanguages = "fra+eng"
            },
            details);

        Assert.True(ready);
        Assert.True((bool)details["ocr_ready"]!);
        Assert.True((bool)details["ocr_configured"]!);
        Assert.True((bool)details["ocr_command_available"]!);
        Assert.Equal("ready", details["ocr_status"]);
        Assert.Equal("explicit", details["ocr_language_mode"]);
        Assert.False((bool)details["ocr_image_page_enabled"]!);
        Assert.Null(details["ocr_image_renderer_available"]);
        Assert.Null(details["ocr_image_text_available"]);
        Assert.Equal("fra+eng", details["ocr_languages"]);
        Assert.False((bool)details["ocr_languages_verified"]!);
        Assert.Equal(["fra", "eng"], (string[])details["ocr_language_configured_codes"]!);
        Assert.Equal("ocr_languages_not_verified", details["ocr_warning"]);
        Assert.False(details.ContainsKey("ocr_error"));
    }

    [Fact]
    public void ResolveOcrLanguageReadiness_reports_missing_configured_language_when_verified()
    {
        var readiness = ReadyEndpoints.ResolveOcrLanguageReadiness(
            ["eng", "fra", "deu"],
            ["eng", "deu", "ita"]);

        Assert.False(readiness.Ready);
        Assert.True(readiness.Verified);
        Assert.Equal(3, readiness.InstalledLanguageCount);
        Assert.Equal(["fra"], readiness.MissingLanguages);
    }

    [Fact]
    public void ResolveOcrLanguageReadiness_reports_installed_count_for_auto_language_mode()
    {
        var readiness = ReadyEndpoints.ResolveOcrLanguageReadiness(
            [],
            ["eng", "fra", "deu"]);

        Assert.True(readiness.Ready);
        Assert.True(readiness.Verified);
        Assert.Empty(readiness.ConfiguredLanguages);
        Assert.Equal(3, readiness.InstalledLanguageCount);
        Assert.Empty(readiness.MissingLanguages);
    }

    [Fact]
    public void ProbeOcrReadiness_reports_auto_language_mode_with_bounded_limit()
    {
        var details = new Dictionary<string, object?>();
        var availableCommand = AvailableCommand;

        var ready = ReadyEndpoints.ProbeOcrReadiness(
            new IngestionOptions
            {
                OcrEnabled = true,
                OcrCommand = availableCommand,
                OcrImagePageEnabled = false,
                OcrLanguages = "auto",
                OcrMaxLanguages = 0
            },
            details);

        Assert.True(ready);
        Assert.Equal("ready", details["ocr_status"]);
        Assert.Equal("auto", details["ocr_language_mode"]);
        Assert.Equal(IngestionOptions.DefaultAutoOcrMaxLanguages, details["ocr_auto_max_languages"]);
        Assert.Equal(new IngestionOptions().OcrAutoFallbackLanguages, details["ocr_auto_fallback_languages"]);
    }

    [Fact]
    public void ProbeIngestionReadiness_reports_complete_image_ocr_when_page_budget_is_unlimited()
    {
        var details = new Dictionary<string, object?>();
        var method = typeof(ReadyEndpoints).GetMethod(
            "ProbeIngestionReadiness",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        method!.Invoke(
            null,
            [
                new IngestionOptions
                {
                    OcrImagePageMaxPages = 0,
                    OcrImagePageMaxTotalSeconds = 0,
                    EmbeddingsBatchSize = 16
                },
                details
            ]);

        Assert.Equal(16, details["ingestion_embeddings_batch_size"]);
        Assert.Equal(16, details["ingestion_embeddings_batch_size_configured"]);
        Assert.Equal(IngestionOptions.MinEmbeddingsBatchSize, details["ingestion_embeddings_batch_size_min"]);
        Assert.Equal(IngestionOptions.MaxEmbeddingsBatchSize, details["ingestion_embeddings_batch_size_max"]);
        Assert.True((bool)details["ingestion_embeddings_batch_size_dynamic"]!);
        Assert.True((bool)details["ingestion_embeddings_batch_adaptive_retry_enabled"]!);
        Assert.Equal("all", details["ingestion_ocr_image_page_max_pages"]);
        Assert.Equal("disabled", details["ingestion_ocr_image_page_max_total_seconds"]);
        Assert.Equal("complete", details["ingestion_ocr_image_page_reliability"]);
    }

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(0, 1)]
    [InlineData(16, 16)]
    [InlineData(512, 256)]
    public void ResolveEmbeddingsBatchSize_clamps_installation_values(int configured, int expected)
    {
        Assert.Equal(expected, IngestionOptions.ResolveEmbeddingsBatchSize(configured));

        var options = new IngestionOptions { TeiBatchSize = configured };
        Assert.Equal(expected, options.EmbeddingsBatchSize);
    }

    [Fact]
    public void ProbeIngestionReadiness_reports_time_budgeted_image_ocr_when_total_budget_is_finite()
    {
        var details = new Dictionary<string, object?>();
        var method = typeof(ReadyEndpoints).GetMethod(
            "ProbeIngestionReadiness",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        method!.Invoke(
            null,
            [
                new IngestionOptions
                {
                    OcrImagePageMaxPages = 0,
                    OcrImagePageMaxTotalSeconds = 900,
                    EmbeddingsBatchSize = 16
                },
                details
            ]);

        Assert.Equal("all", details["ingestion_ocr_image_page_max_pages"]);
        Assert.Equal("900", details["ingestion_ocr_image_page_max_total_seconds"]);
        Assert.Equal("time_budgeted", details["ingestion_ocr_image_page_reliability"]);
    }

    [Fact]
    public void ResolveOcrLanguageReadiness_keeps_ready_when_probe_is_unavailable()
    {
        var readiness = ReadyEndpoints.ResolveOcrLanguageReadiness(
            ["eng", "fra"],
            []);

        Assert.True(readiness.Ready);
        Assert.False(readiness.Verified);
        Assert.Equal(0, readiness.InstalledLanguageCount);
        Assert.Empty(readiness.MissingLanguages);
    }

    [Fact]
    public void ProbeOcrReadiness_returns_ready_when_enabled_image_ocr_dependencies_are_available()
    {
        var details = new Dictionary<string, object?>();
        var availableCommand = AvailableCommand;

        var ready = ReadyEndpoints.ProbeOcrReadiness(
            new IngestionOptions
            {
                OcrEnabled = true,
                OcrCommand = availableCommand,
                OcrImagePageEnabled = true,
                OcrImageRendererCommand = availableCommand,
                OcrImageTextCommand = availableCommand
            },
            details);

        Assert.True(ready);
        Assert.True((bool)details["ocr_ready"]!);
        Assert.True((bool)details["ocr_command_available"]!);
        Assert.True((bool)details["ocr_image_renderer_available"]!);
        Assert.True((bool)details["ocr_image_text_available"]!);
        Assert.Equal(availableCommand, details["ocr_image_renderer_command"]);
        Assert.Equal(availableCommand, details["ocr_image_text_command"]);
        Assert.False(details.ContainsKey("ocr_error"));
    }

    [Fact]
    public void ProbeOcrReadiness_checks_image_ocr_dependencies_when_enabled()
    {
        var details = new Dictionary<string, object?>();

        var ready = ReadyEndpoints.ProbeOcrReadiness(
            new IngestionOptions
            {
                OcrEnabled = true,
                OcrCommand = "dotnet",
                OcrImagePageEnabled = true,
                OcrImageRendererCommand = "saaia-missing-renderer-command-for-test",
                OcrImageTextCommand = "saaia-missing-text-command-for-test"
            },
            details);

        Assert.False(ready);
        Assert.False((bool)details["ocr_ready"]!);
        Assert.False((bool)details["ocr_image_renderer_available"]!);
        Assert.False((bool)details["ocr_image_text_available"]!);
        Assert.Contains("ocr_image_renderer_unavailable", (string)details["ocr_error"]!);
        Assert.Contains("ocr_image_text_unavailable", (string)details["ocr_error"]!);
    }

    [Fact]
    public async Task ProbeLlmReadinessAsync_reports_unavailable_server_without_throwing()
    {
        var details = new Dictionary<string, object?>();
        var method = typeof(ReadyEndpoints).GetMethod(
            "ProbeLlmReadinessAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task<bool>)method!.Invoke(
            null,
            [
                new StubHttpClientFactory(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                new ChatOptions
                {
                    LlmBaseUrl = "http://llm.test/",
                    LlmModel = "test-model"
                },
                details,
                CancellationToken.None
            ])!;
        var ready = await task;

        Assert.False(ready);
        Assert.Equal("server-unavailable", details["llm"]);
        Assert.True((bool)details["llm_server_configured"]!);
        Assert.Equal("test-model", details["llm_model"]);
        Assert.Equal(503, details["llm_status"]);
    }

    [Fact]
    public void ApplyBackofficeLlmReadinessPolicy_keeps_llm_optional_when_backoffice_is_disabled()
    {
        var previous = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "false");
        try
        {
            var details = new Dictionary<string, object?>();

            var ready = ReadyEndpoints.ApplyBackofficeLlmReadinessPolicy(
                new ChatOptions(),
                llmReady: false,
                details);

            Assert.True(ready);
            Assert.False((bool)details["llm_backoffice_enabled"]!);
            Assert.False((bool)details["llm_backoffice_required"]!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previous);
        }
    }

    [Fact]
    public void ApplyBackofficeLlmReadinessPolicy_reports_missing_url_without_failing_client_readiness()
    {
        var previous = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");
        try
        {
            var details = new Dictionary<string, object?>();

            var ready = ReadyEndpoints.ApplyBackofficeLlmReadinessPolicy(
                new ChatOptions(),
                llmReady: true,
                details);

            Assert.True(ready);
            Assert.True((bool)details["llm_backoffice_enabled"]!);
            Assert.False((bool)details["llm_backoffice_required"]!);
            Assert.True((bool)details["llm_backoffice_degraded"]!);
            Assert.Equal("server-backoffice-missing-url", details["llm"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previous);
        }
    }

    [Fact]
    public void ApplyBackofficeLlmReadinessPolicy_marks_backoffice_degraded_without_failing_client_readiness()
    {
        var previous = Environment.GetEnvironmentVariable("BACKOFFICE_LLM_ENABLED");
        Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", "true");
        try
        {
            var details = new Dictionary<string, object?>();

            var ready = ReadyEndpoints.ApplyBackofficeLlmReadinessPolicy(
                new ChatOptions { LlmBaseUrl = "http://llm.test/" },
                llmReady: false,
                details);

            Assert.True(ready);
            Assert.True((bool)details["llm_backoffice_enabled"]!);
            Assert.False((bool)details["llm_backoffice_required"]!);
            Assert.True((bool)details["llm_backoffice_degraded"]!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKOFFICE_LLM_ENABLED", previous);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpResponseMessage _response;

        public StubHttpClientFactory(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler(_response))
            {
                BaseAddress = new Uri("http://llm.test/")
            };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }
}
