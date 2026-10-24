using System.Net;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("Client production diagnostics")]
public sealed class ClientProductionDiagnosticsTests
{
    [Fact]
    public void Source_card_locator_forwards_the_complete_identity_and_exact_hash_policy()
    {
        var card = new SAAIA.Client.WinUI.Models.SourceCard
        {
            DocPath = "book.pdf", PageStart = 3, PageEnd = 6, SourceHash = "hash", RevisionId = "revision",
            ChunkId = "chunk", AnchorId = "anchor", ContentCardId = "card", OriginTraceId = "trace"
        };
        var locator = card.ToSourceLocator();
        Assert.Equal(card.DocPath, locator.DocumentPath);
        Assert.Equal(card.PageStart, locator.Page);
        Assert.Equal(card.PageEnd, locator.PageEnd);
        Assert.Equal(card.SourceHash, locator.SourceHash);
        Assert.Equal(card.RevisionId, locator.RevisionId);
        Assert.Equal(card.ChunkId, locator.ChunkId);
        Assert.Equal(card.AnchorId, locator.AnchorId);
        Assert.Equal(card.ContentCardId, locator.ContentCardId);
        Assert.Equal(card.OriginTraceId, locator.OriginTraceId);
        Assert.True(locator.RequireExactSourceHash);
    }
    [Fact]
    public async Task Standalone_advanced_service_correlates_retry_poll_and_failure_without_llm_or_orchestrator()
    {
        var session = Guid.NewGuid();
        var handoffId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var headers = new List<(string Trace, string Turn, string Session)>();
        var call = 0;
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            headers.Add((request.Headers.GetValues("traceparent").Single(),
                request.Headers.GetValues("X-SAAIA-Turn-Id").Single(),
                request.Headers.GetValues("X-SAAIA-Session-Id").Single()));
            call++;
            if (call is 1 or 3) throw new HttpRequestException("simulated transport interruption");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    jobId, handoffId, sessionId = session, revision = call,
                    status = call == 2 ? "queued" : "failed", lastErrorCode = "provider_failed",
                    availableAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
                }), Encoding.UTF8, "application/json")
            };
        }));
        var api = new ApiClient(http);
        api.Configure("http://in-memory.invalid", "credential-never-log", "test-user");
        var service = new AdvancedAnalysisClientService(api);
        string traceId;
        using (var turn = ClientDiagnostics.Journal.BeginTurn(session.ToString("D")))
        {
            traceId = ClientDiagnostics.Journal.CurrentTraceId!;
            var result = await service.ExecuteAdvancedAnalysisHandoffForTestsAsync(session,
                new AdvancedAnalysisHandoffEnvelope { HandoffId = handoffId, Language = "fr" },
                new AdvancedAnalysisClientPollingOptions { MaximumPolls = 3 },
                static (_, _) => Task.CompletedTask, CancellationToken.None);
            Assert.Equal("failed", result.Outcome);
            turn.Complete("failed", "provider_failed");
        }
        var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TraceId == traceId);
        Assert.Contains(record.Events, item => item.Operation == "advanced.create.retry");
        Assert.Contains(record.Events, item => item.Operation == "advanced.poll.retry");
        Assert.All(headers, header =>
        {
            Assert.StartsWith("00-" + traceId + "-", header.Trace);
            Assert.Equal(record.TurnId, header.Turn);
            Assert.Equal(session.ToString("D"), header.Session);
        });
        Assert.DoesNotContain("credential-never-log", JsonSerializer.Serialize(record));
    }

    [Fact]
    public void Source_parser_reports_malformed_input_and_visible_source_count_without_document_text()
    {
        string traceId;
        using (var turn = ClientDiagnostics.Journal.BeginTurn())
        {
            traceId = ClientDiagnostics.Journal.CurrentTraceId!;
            Assert.Empty(SourceCardParser.Parse("{malformed private text"));
            Assert.Single(SourceCardParser.Parse("""{"sources":[{"docPath":"private-book.pdf","pageStart":3},null]}"""));
            turn.Complete();
        }
        var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TraceId == traceId);
        Assert.Contains(record.Events, item => item.Operation == "sources.parse" && item.Outcome == "failed");
        var parsed = Assert.Single(record.Events, item => item.Operation == "sources.parse" && item.Outcome == "success");
        Assert.Equal(2, parsed.Measurements["received"]);
        Assert.Equal(1, parsed.Measurements["visible"]);
        Assert.DoesNotContain("private-book", JsonSerializer.Serialize(record));
    }

    [Fact]
    public async Task Support_export_includes_common_history_and_declares_optional_collection_failure_without_network()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "saaia-client-diagnostics-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(localRoot, "SAAIA", "logs");
        Directory.CreateDirectory(logs);
        var lockedPath = Path.Combine(logs, "locked.log");
        await File.WriteAllTextAsync(lockedPath, "private question and password");
        string? bundle = null;
        try
        {
            using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            bundle = await SupportBundleBuilder.BuildAsync(new AppSettings
                { BackendUrl = "http://offline.invalid", Host = "offline.invalid", Port = 80 },
                localAppDataRoot: localRoot,
                probeWriter: static (path, _) => File.WriteAllTextAsync(path, """{"status":"not_executed","reason":"offline_test"}"""));
            using var archive = ZipFile.OpenRead(bundle);
            Assert.NotNull(archive.GetEntry("diagnostics/client-turns.json"));
            using var reader = new StreamReader(archive.GetEntry("collection-manifest.json")!.Open());
            using var manifest = JsonDocument.Parse(await reader.ReadToEndAsync());
            Assert.False(manifest.RootElement.GetProperty("collectionComplete").GetBoolean());
            Assert.Equal("injected", manifest.RootElement.GetProperty("probeImplementation").GetString());
            Assert.Contains(manifest.RootElement.GetProperty("pieces").EnumerateArray(),
                piece => piece.GetProperty("Status").GetString() == "failed");
            Assert.Null(archive.GetEntry("logs/locked.log"));
        }
        finally
        {
            if (bundle is not null && File.Exists(bundle)) File.Delete(bundle);
            Directory.Delete(localRoot, true);
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    [Fact]
    public async Task Support_export_survives_readiness_writer_failure_and_manifest_paths_identify_the_piece()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "saaia-support-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localRoot);
        string? bundle = null;
        try
        {
            bundle = await SupportBundleBuilder.BuildAsync(new AppSettings
                { BackendUrl = "http://offline.invalid", Host = "offline.invalid", Port = 80 },
                localAppDataRoot: localRoot, probeWriter: static (path, _) =>
                    Path.GetFileName(path) == "backend_ready.json"
                        ? Task.FromException(new IOException("private readiness details"))
                        : File.WriteAllTextAsync(path, """{"status":"not_executed","reason":"offline_test"}"""));
            using var archive = ZipFile.OpenRead(bundle);
            Assert.NotNull(archive.GetEntry("diagnostics/client-turns.json"));
            Assert.NotNull(archive.GetEntry("llm_models.json"));
            using var reader = new StreamReader(archive.GetEntry("collection-manifest.json")!.Open());
            var text = await reader.ReadToEndAsync();
            using var manifest = JsonDocument.Parse(text);
            Assert.False(manifest.RootElement.GetProperty("collectionComplete").GetBoolean());
            Assert.Contains(manifest.RootElement.GetProperty("pieces").EnumerateArray(), piece =>
                piece.GetProperty("Path").GetString() == "backend_ready.json"
                && piece.GetProperty("Status").GetString() == "failed");
            Assert.Contains(manifest.RootElement.GetProperty("pieces").EnumerateArray(), piece =>
                piece.GetProperty("Path").GetString() == "governance/runtime_event_log.json");
            Assert.Contains(manifest.RootElement.GetProperty("pieces").EnumerateArray(), piece =>
                piece.GetProperty("Path").GetString() == "llm/runtime_event_log.json");
            Assert.DoesNotContain("private readiness details", text);
        }
        finally
        {
            if (bundle is not null && File.Exists(bundle)) File.Delete(bundle);
            Directory.Delete(localRoot, true);
        }
    }
}

[CollectionDefinition("Client production diagnostics", DisableParallelization = true)]
public sealed class ClientProductionDiagnosticsCollection;
