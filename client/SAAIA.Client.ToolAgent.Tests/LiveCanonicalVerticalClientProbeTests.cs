using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("RuntimeRootSerial")]
public sealed class LiveCanonicalVerticalClientProbeTests
{
    [Fact]
    public async Task Collect_real_agent_responses_and_exact_source_cards()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SAAIA_RUN_LOCAL_VERTICAL_PROBE"));
        var output = Environment.GetEnvironmentVariable("SAAIA_PROBE_OUTPUT")!;
        Assert.True(Path.IsPathFullyQualified(output) && Directory.Exists(output));
        var backend = Environment.GetEnvironmentVariable("SAAIA_PROBE_BACKEND_URL")!;
        var key = Environment.GetEnvironmentVariable("SAAIA_PROBE_API_KEY")!;
        Assert.True(new Uri(backend).IsLoopback && !string.IsNullOrWhiteSpace(key));
        var manifest = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(
            Environment.GetEnvironmentVariable("SAAIA_PROBE_PDF_MANIFEST")!));
        var expectedDoc = manifest.GetProperty("docId").GetString();
        var expectedRevision = manifest.GetProperty("revisionId").GetString();
        var expectedHash = manifest.GetProperty("sourceHash").GetString();
        var chunks = manifest.GetProperty("chunks").EnumerateArray().ToDictionary(
            chunk => chunk.GetProperty("chunkId").GetString()!, StringComparer.OrdinalIgnoreCase);
        var contentCards = manifest.GetProperty("cards").EnumerateArray().ToDictionary(
            card => card.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);
        var anchors = manifest.GetProperty("anchors").EnumerateArray().ToDictionary(
            anchor => anchor.GetProperty("id").GetString()!, StringComparer.OrdinalIgnoreCase);
        var prefsBefore = UserPrefsStore.FilePathOverrideForTests;
        var logField = typeof(ClientLog).GetField("_logDir", BindingFlags.Static | BindingFlags.NonPublic)!;
        var logBefore = logField.GetValue(null);
        UserPrefsStore.FilePathOverrideForTests = Path.Combine(output, "isolated-user-prefs.bin");
        logField.SetValue(null, Path.Combine(output, "logs"));
        var observations = new List<object>();
        string[] questions =
        [
            "What calibration measurement is recorded for the amber instrument?",
            "In Calibration-record.pdf, what measurements are recorded for the amber and cobalt instruments, and what condition applies before each value is valid?",
            "What annual maintenance interval does Calibration-record.pdf specify for the cobalt instrument?"
        ];
        try
        {
            for (var index = 0; index < questions.Length; index++)
            {
                var question = questions[index];
                var caseDirectory = Path.Combine(output, $"case-{index + 1:D2}");
                Directory.CreateDirectory(caseDirectory);
                using var backendHttp = new HttpClient(new CaptureHandler(caseDirectory, "backend"))
                { Timeout = Timeout.InfiniteTimeSpan };
                using var llmHttp = new HttpClient(new CaptureHandler(caseDirectory, "llm"))
                { Timeout = Timeout.InfiniteTimeSpan };
                var api = new ApiClient();
                var httpField = typeof(ApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;
                ((HttpClient)httpField.GetValue(api)!).Dispose();
                httpField.SetValue(api, backendHttp);
                api.Configure(backend, key, Guid.NewGuid().ToString("D"));
                var llm = new OpenAiLlmClient(llmHttp);
                llm.Configure("http://127.0.0.1:1234/v1", "local");
                var settings = new AppSettings
                {
                    BackendUrl = backend,
                    UseLocalLlm = true,
                    ManageLocalLlmProcess = false,
                    ActiveMode = "strict",
                    RagQualityPreset = "balanced",
                    UiLanguage = "en",
                    LlmTemperature = 0,
                    LlmMaxOutputTokens = 900,
                    ExtraArgs = "--ctx-size 4096"
                };
                var agent = new RagChatAgent(api, llm);
                agent.ApplySettings(settings);
                var deltas = new StringBuilder();
                string? answer = null;
                object? sourcesPayload = null;
                string? error = null;
                var watch = Stopwatch.StartNew();
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                    var result = await agent.RunAsync(question, "", Array.Empty<ChatMessageItem>(),
                        delta => deltas.Append(delta), deadline.Token);
                    answer = result.finalAnswer;
                    sourcesPayload = result.sourcesPayload;
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                }
                watch.Stop();
                var memory = (ToolMemory)typeof(RagChatAgent).GetField("_mem", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent)!;
                var cards = SourceCardParser.Parse(JsonSerializer.Serialize(sourcesPayload));
                var cardChecks = cards.Select(card =>
                {
                    var failures = new List<string>();
                    if (!string.Equals(card.DocId, expectedDoc, StringComparison.OrdinalIgnoreCase)) failures.Add("doc_id_mismatch");
                    if (!string.Equals(card.RevisionId, expectedRevision, StringComparison.OrdinalIgnoreCase)) failures.Add("revision_mismatch");
                    if (!string.Equals(card.SourceHash, expectedHash, StringComparison.OrdinalIgnoreCase)) failures.Add("hash_mismatch");
                    if (card.DocPath != manifest.GetProperty("docPath").GetString()) failures.Add("path_mismatch");
                    if (string.IsNullOrWhiteSpace(card.EvidenceId)) failures.Add("missing_evidence_id");
                    if (card.PageStart is not (1 or 2) || card.PageEnd != card.PageStart) failures.Add("page_mismatch");
                    if (string.IsNullOrWhiteSpace(card.ChunkId) && string.IsNullOrWhiteSpace(card.AnchorId)
                        && string.IsNullOrWhiteSpace(card.ContentCardId)) failures.Add("missing_source_locator");
                    if (!string.IsNullOrWhiteSpace(card.ChunkId))
                    {
                        if (!chunks.TryGetValue(card.ChunkId, out var chunk)) failures.Add("chunk_identity_not_in_manifest");
                        else if (card.PageStart != chunk.GetProperty("PageStart").GetInt32()) failures.Add("chunk_page_mismatch");
                    }
                    CheckPageLocator(card.AnchorId, anchors, "anchor", card.PageStart, failures);
                    CheckPageLocator(card.ContentCardId, contentCards, "content_card", card.PageStart, failures);
                    var path = DocumentPathResolver.ResolveExactRevision(card.DocPath, card.SourceHash);
                    if (path is null) failures.Add("exact_local_file_unresolved");
                    var pageUri = path is not null && card.PageStart is > 0
                        ? DocumentLauncher.BuildPdfPageUriForTests(path, card.PageStart.Value) : null;
                    return new { card, failures, resolvedLocalFile = path, pageUri, actualWindowOpened = false };
                }).ToArray();
                var observation = new
                {
                    caseId = index + 1,
                    question,
                    elapsedMs = watch.ElapsedMilliseconds,
                    answer,
                    streamedDeltas = deltas.ToString(),
                    error,
                    sourcesPayload,
                    cardChecks,
                    diagnostics = new
                    {
                        memory.LastRouterIntent,
                        memory.LastAnswerSource,
                        memory.LastToolNames,
                        memory.LastSourcesUsed,
                        memory.LastRagQueries,
                        memory.LastRagHitLabels,
                        memory.LastReasoningTracePublic,
                        memory.LastRagTraceEvents,
                        memory.SourceBackedConversationTurns
                    },
                    semanticApproval = "PENDING_INSPECTION"
                };
                observations.Add(observation);
                await File.WriteAllTextAsync(Path.Combine(caseDirectory, "observation.json"), Serialize(observation));
                await File.WriteAllTextAsync(Path.Combine(output, "observations.json"), Serialize(observations));
                var startupLog = Path.Combine(output, "logs", "client_startup.log");
                if (File.Exists(startupLog))
                    await File.WriteAllTextAsync(startupLog, Redact(await File.ReadAllTextAsync(startupLog)));
            }
        }
        finally
        {
            UserPrefsStore.FilePathOverrideForTests = prefsBefore;
            logField.SetValue(null, logBefore);
        }
        // Collection success is separate from correctness. Errors and semantic
        // outcomes are reviewed in the frozen observations, never hidden here.
        Assert.Equal(questions.Length, observations.Count);
    }

    private static string Serialize(object value)
        => Redact(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private static string Redact(string text)
    {
        var ephemeralKey = Environment.GetEnvironmentVariable("SAAIA_PROBE_API_KEY");
        return string.IsNullOrEmpty(ephemeralKey) ? text
            : text.Replace(ephemeralKey, "[REDACTED_EPHEMERAL_TEST_KEY]", StringComparison.Ordinal);
    }

    private static void CheckPageLocator(string? id, IReadOnlyDictionary<string, JsonElement> locators,
        string kind, int? page, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!locators.TryGetValue(id, out var locator)) failures.Add(kind + "_identity_not_in_manifest");
        else if (locator.GetProperty("page_start").ValueKind == JsonValueKind.Number
            && page != locator.GetProperty("page_start").GetInt32()) failures.Add(kind + "_page_mismatch");
    }

    private sealed class CaptureHandler(string output, string service) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        private int _sequence;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _sequence);
            var path = Path.Combine(output, $"{service}-{id:D3}.json");
            var requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var started = DateTimeOffset.UtcNow;
            try
            {
                var response = await base.SendAsync(request, ct);
                var isStream = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
                var responseBody = isStream ? null : await response.Content.ReadAsStringAsync(ct);
                await File.WriteAllTextAsync(path, Serialize(new
                {
                    started,
                    ended = DateTimeOffset.UtcNow,
                    method = request.Method.Method,
                    url = request.RequestUri!.AbsoluteUri,
                    requestBody,
                    status = (int)response.StatusCode,
                    responseBody,
                    streamedResponseNotCaptured = isStream
                }), CancellationToken.None);
                return response;
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(path, Serialize(new
                {
                    started,
                    ended = DateTimeOffset.UtcNow,
                    method = request.Method.Method,
                    url = request.RequestUri!.AbsoluteUri,
                    requestBody,
                    error = ex.GetType().Name + ": " + ex.Message
                }), CancellationToken.None);
                throw;
            }
        }
    }
}
