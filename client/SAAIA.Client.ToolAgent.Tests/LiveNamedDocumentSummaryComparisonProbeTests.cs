using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveNamedDocumentSummaryComparisonProbeTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_named_document_summary_path_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_NAMED_DOCUMENT_SUMMARY_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_NAMED_DOCUMENT_SUMMARY_PROBE to measure rag.summarize_live directly.");
            return;
        }

        var settings = AppSettings.Load();
        var backendUrl = Require(
            "backend URL",
            FirstNonBlank(
                Environment.GetEnvironmentVariable("SAAIA_VALIDATION_BACKEND_URL"),
                settings.BackendUrl));
        var apiKey = Require(
            "configured backend API key",
            FirstNonBlank(
                Environment.GetEnvironmentVariable("SAAIA_API_KEY"),
                SecureLocalStore.GetServerApiKey()));
        var llmBaseUrl = NormalizeLlmHost(Require(
            "LLM base URL",
            FirstNonBlank(
                Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
                settings.LlmBaseUrl)));
        var llmModel = Require(
            "LLM model",
            FirstNonBlank(
                Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
                settings.ModelId));
        var docId = Require(
            "SAAIA_NAMED_DOCUMENT_SUMMARY_DOC_ID",
            Environment.GetEnvironmentVariable("SAAIA_NAMED_DOCUMENT_SUMMARY_DOC_ID"));
        var docPath = Require(
            "SAAIA_NAMED_DOCUMENT_SUMMARY_DOC_PATH",
            Environment.GetEnvironmentVariable("SAAIA_NAMED_DOCUMENT_SUMMARY_DOC_PATH"));
        var docName = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_NAMED_DOCUMENT_SUMMARY_DOC_NAME"),
            Path.GetFileName(docPath)) ?? Path.GetFileName(docPath);
        var artifact = Require(
            "SAAIA_NAMED_DOCUMENT_SUMMARY_ARTIFACT",
            Environment.GetEnvironmentVariable("SAAIA_NAMED_DOCUMENT_SUMMARY_ARTIFACT"));
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);

        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));
        using var llmHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var llm = new OpenAiCompatLlmClient(llmHttp, llmBaseUrl, llmModel);
        var memory = new ToolMemory { LastLanguage = "fr" };
        memory.PdfMap["PROBE_DOC"] = new ToolMemory.DocumentItem
        {
            DocId = docId,
            DocPath = docPath,
            DocName = docName,
            Category = "Normes",
            CategoryPath = "Normes/PDF",
            PdfRef = "PROBE_DOC"
        };

        var orchestrator = new ToolAgentOrchestrator(api, llm, memory);
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            "ExecRagSummarizeLiveAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var args = JsonSerializer.SerializeToElement(new
        {
            docRef = "PROBE_DOC",
            level = "medium",
            strategy = FirstNonBlank(
                Environment.GetEnvironmentVariable(
                    "SAAIA_NAMED_DOCUMENT_SUMMARY_STRATEGY"),
                "summary"),
            language = "fr",
            responseLanguage = "fr",
            userRequest = FirstNonBlank(
                Environment.GetEnvironmentVariable(
                    "SAAIA_NAMED_DOCUMENT_SUMMARY_USER_REQUEST"),
                "Résume le document nommé en 7 points utiles à une décision."),
            requestedPointCount = ReadPositiveInt(
                "SAAIA_NAMED_DOCUMENT_SUMMARY_REQUESTED_POINTS",
                7),
            sampleCount = ReadPositiveInt(
                "SAAIA_NAMED_DOCUMENT_SUMMARY_SAMPLE_COUNT",
                8),
            maxWords = ReadPositiveInt(
                "SAAIA_NAMED_DOCUMENT_SUMMARY_MAX_WORDS",
                220),
            maxChunks = ReadPositiveInt(
                "SAAIA_NAMED_DOCUMENT_SUMMARY_MAX_CHUNKS",
                18),
            maxBatches = ReadPositiveInt(
                "SAAIA_NAMED_DOCUMENT_SUMMARY_MAX_BATCHES",
                4),
            maxCharsPerBatch = ReadPositiveInt(
                "SAAIA_NAMED_DOCUMENT_SUMMARY_MAX_CHARS_PER_BATCH",
                6500)
        });
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(ReadPositiveInt(
                "SAAIA_NAMED_DOCUMENT_SUMMARY_TIMEOUT_SECONDS",
                300)));

        var sw = Stopwatch.StartNew();
        JsonElement? result = null;
        string? executionError = null;
        try
        {
            var task = (Task<JsonElement>)method!.Invoke(
                orchestrator,
                new object[] { args.Clone(), cts.Token })!;
            result = await task;
        }
        catch (Exception ex)
        {
            executionError = ex.GetType().Name + ": " + ex.Message;
        }
        sw.Stop();

        var report = new
        {
            probe = "rag.summarize_live.direct",
            capturedAtUtc = DateTimeOffset.UtcNow,
            elapsedMs = sw.ElapsedMilliseconds,
            backendUrl,
            llmBaseUrl,
            llmModel,
            request = JsonSerializer.Deserialize<object>(args.GetRawText()),
            result = result.HasValue
                ? JsonSerializer.Deserialize<object>(result.Value.GetRawText())
                : null,
            executionError
        };
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("elapsed_ms=" + sw.ElapsedMilliseconds);
        output.WriteLine("summary_chars=" + (result.HasValue ? ReadSummaryLength(result.Value) : 0));
        output.WriteLine("source_metadata_total=" + (result.HasValue ? ReadInt(result.Value, "sourceMetadataTotal") : 0));
        output.WriteLine("artifact=" + artifact);
        Assert.Null(executionError);
        Assert.True(result.HasValue);
        if (result.Value.TryGetProperty("error", out var error))
        {
            Assert.Fail(error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : error.GetRawText());
        }
        Assert.True(ReadSummaryLength(result.Value) >= 40);
        Assert.True(ReadInt(result.Value, "sourceMetadataTotal") > 0);
    }

    private static int ReadSummaryLength(JsonElement result)
        => result.TryGetProperty("summaryText", out var summary)
           && summary.ValueKind == JsonValueKind.String
            ? (summary.GetString() ?? string.Empty).Length
            : 0;

    private static int ReadInt(JsonElement result, string propertyName)
        => result.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static int ReadPositiveInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
           && parsed > 0
            ? parsed
            : fallback;

    private static string NormalizeLlmHost(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        return normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^3].TrimEnd('/')
            : normalized;
    }

    private static string Require(string name, string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("Missing " + name + ".")
            : value.Trim();

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value =>
            !string.IsNullOrWhiteSpace(value))?.Trim();
}
