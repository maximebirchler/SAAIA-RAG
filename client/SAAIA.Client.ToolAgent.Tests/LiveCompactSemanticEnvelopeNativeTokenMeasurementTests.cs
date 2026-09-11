using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveCompactSemanticEnvelopeNativeTokenMeasurementTests
{
    private const string ManifestSha256 =
        "D1CCCA29AC3704B3058D934202379BADBAFEA80CB7865D084520E45CBDC3B7F6";
    private const string ContractsSha256 =
        "BD6ECE287F3AF3F4448F187604725D461097122DA0BE1AD762041DEE01104DA0";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [Fact]
    [Trait("Category", "Live")]
    public async Task A648_measures_all_compact_envelopes_with_native_tokenizer_only()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_A648_RUN"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var outputDirectory = RequiredEnvironment("SAAIA_A648_OUTPUT_DIR");
        var modelPath = RequiredEnvironment("SAAIA_A648_MODEL_PATH");
        var runtimePath = RequiredEnvironment("SAAIA_A648_RUNTIME_PATH");
        var settingsPath = RequiredEnvironment("SAAIA_A648_SETTINGS_PATH");
        var runtimeArguments = RequiredEnvironment("SAAIA_A648_RUNTIME_ARGUMENTS");
        var settingsSha256 = RequiredEnvironment("SAAIA_A648_SETTINGS_SHA256");
        var modelSha256 = RequiredEnvironment("SAAIA_A648_MODEL_SHA256");
        var runtimeSha256 = RequiredEnvironment("SAAIA_A648_RUNTIME_SHA256");
        var runtimePid = int.Parse(
            RequiredEnvironment("SAAIA_A648_RUNTIME_PID"),
            System.Globalization.CultureInfo.InvariantCulture);
        var runtimeStartedAt = RequiredEnvironment("SAAIA_A648_RUNTIME_STARTED_AT");
        var repoRoot = FindRepositoryRoot();
        var phase5Root = Path.Combine(
            repoRoot,
            "artifacts",
            "goal-rag-product-20260827-1041",
            "phase5");
        var manifestPath = Path.Combine(phase5Root, "A630-CASES-NORMALISES-ORACLES.json");
        var contractsPath = Path.Combine(phase5Root, "A645-CONTRATS-COMPACTS-CANDIDATS.json");
        var requestPath = Path.Combine(outputDirectory, "A648-NATIVE-TOKEN-REQUESTS.jsonl");
        var snapshotPath = Path.Combine(outputDirectory, "A648-NATIVE-TOKEN-SNAPSHOTS.jsonl");
        var checkpointPath = Path.Combine(outputDirectory, "A648-NATIVE-TOKEN-CHECKPOINT.json");
        var summaryPath = Path.Combine(outputDirectory, "A648-NATIVE-TOKEN-MEASUREMENTS.json");
        var runtimeProfilePath = Path.Combine(outputDirectory, "runtime-profile.json");

        Assert.True(Directory.Exists(outputDirectory));
        Assert.False(File.Exists(requestPath));
        Assert.False(File.Exists(snapshotPath));
        Assert.False(File.Exists(checkpointPath));
        Assert.False(File.Exists(summaryPath));
        Assert.False(File.Exists(runtimeProfilePath));

        var contracts = CompactSemanticEnvelopeNativeTokenHarness.LoadFrozenContracts(
            contractsPath,
            ContractsSha256);
        var cases = CompactSemanticEnvelopeNativeTokenHarness.LoadFrozenCases(
            manifestPath,
            ManifestSha256);
        var snapshots = CompactSemanticEnvelopeNativeTokenHarness.BuildSnapshots(cases);
        var schedule = CompactSemanticEnvelopeNativeTokenHarness
            .BuildMeasurementSchedule(snapshots);
        Assert.Equal(25, snapshots.Count);
        Assert.Equal(204, schedule.Count);

        WriteJsonLinesAtomically(
            snapshotPath,
            snapshots.Select(snapshot => BuildSnapshotRecord(snapshot)));

        var transport = new HttpClientHandler();
        var guard = new CompactSemanticEnvelopeNativeTokenHarness.EndpointGuardHandler(transport);
        using var http = new HttpClient(guard)
        {
            BaseAddress = new Uri("http://127.0.0.1:1234"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        var healthBody = await GetRequiredBodyAsync(http, "/health", CancellationToken.None);
        var modelsBody = await GetRequiredBodyAsync(http, "/v1/models", CancellationToken.None);
        using var modelsDocument = JsonDocument.Parse(modelsBody);
        var servedModelId = modelsDocument.RootElement
            .GetProperty("data")[0]
            .GetProperty("id")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(servedModelId));

        var capturedRequests = new List<CapturedRequest>();
        var requestIndex = 0;
        var measurementRecords = await CompactSemanticEnvelopeNativeTokenHarness
            .ExecuteMeasurementScheduleAsync(
                servedModelId!,
                snapshots,
                contracts,
                async (payload, ct) =>
                {
                    var item = schedule[requestIndex];
                    var sequence = requestIndex + 1;
                    requestIndex++;
                    using var payloadDocument = JsonDocument.Parse(payload);
                    var root = payloadDocument.RootElement;
                    var messages = root.GetProperty("messages");
                    var tools = root.GetProperty("tools");
                    var messagesJson = messages.GetRawText();
                    var toolsJson = tools.GetRawText();
                    using var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        "/v1/chat/completions/input_tokens");
                    request.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Content = new StringContent(
                        payload,
                        Utf8NoBom,
                        "application/json");
                    var stopwatch = Stopwatch.StartNew();
                    using var response = await http.SendAsync(request, ct);
                    var responseBody = await response.Content.ReadAsStringAsync(ct);
                    stopwatch.Stop();
                    response.EnsureSuccessStatusCode();
                    using var responseDocument = JsonDocument.Parse(responseBody);
                    var inputTokens = responseDocument.RootElement
                        .GetProperty("input_tokens")
                        .GetInt32();
                    Assert.True(inputTokens > 0);

                    var record = new CapturedRequest(
                        sequence,
                        item.Variant,
                        item.Role,
                        item.CasePosition,
                        item.CaseId,
                        item.Form,
                        item.Repetition,
                        messages.GetArrayLength(),
                        messagesJson.Length,
                        Encoding.UTF8.GetByteCount(messagesJson),
                        tools.GetArrayLength(),
                        Encoding.UTF8.GetByteCount(toolsJson),
                        payload.Length,
                        Encoding.UTF8.GetByteCount(payload),
                        Sha256Utf8(payload),
                        inputTokens,
                        stopwatch.ElapsedMilliseconds,
                        (int)response.StatusCode,
                        root.Clone());
                    capturedRequests.Add(record);
                    File.AppendAllText(
                        requestPath,
                        JsonSerializer.Serialize(record, JsonLineOptions) + "\n",
                        Utf8NoBom);
                    CompactSemanticEnvelopeNativeTokenHarness.WriteCheckpointAtomically(
                        checkpointPath,
                        new
                        {
                            schemaVersion = "a648_native_token_checkpoint_v1",
                            status = "running",
                            completedRequests = sequence,
                            totalRequests = schedule.Count,
                            lastCasePosition = item.CasePosition,
                            lastCaseId = item.CaseId,
                            lastVariant = item.Variant,
                            lastRole = item.Role,
                            lastForm = item.Form,
                            lastRepetition = item.Repetition,
                            forbiddenRequestCount = guard.ForbiddenRequestCount,
                            updatedAtUtc = DateTimeOffset.UtcNow
                        });
                    return inputTokens;
                },
                CancellationToken.None);

        Assert.Equal(204, requestIndex);
        Assert.Equal(204, capturedRequests.Count);
        Assert.Equal(204, measurementRecords.Count);
        Assert.Equal(206, guard.Ledger.Count);
        Assert.True(CompactSemanticEnvelopeNativeTokenHarness.ValidateEndpointLedger(guard));
        Assert.Equal(0, guard.ForbiddenRequestCount);
        Assert.DoesNotContain(
            guard.Ledger,
            entry => entry.Method == HttpMethod.Post.Method
                     && entry.Path == "/v1/chat/completions");
        Assert.All(capturedRequests, item => Assert.Equal(200, item.StatusCode));
        Assert.Equal(
            capturedRequests.Select(item => item.PayloadSha256),
            measurementRecords.Select(item => item.PayloadSha256));
        Assert.Equal(
            capturedRequests.Select(item => item.InputTokens),
            measurementRecords.Select(item => item.InputTokens!.Value));

        var v1Records = measurementRecords.Where(item =>
            item.Variant == CompactSemanticEnvelopeNativeTokenHarness.V1).ToArray();
        var v2Records = measurementRecords.Where(item =>
            item.Variant == CompactSemanticEnvelopeNativeTokenHarness.V2).ToArray();
        Assert.True(
            CompactSemanticEnvelopeNativeTokenHarness.ValidateDuplicateMeasurements(v1Records));
        Assert.True(
            CompactSemanticEnvelopeNativeTokenHarness.ValidateDuplicateMeasurements(v2Records));
        var v1 = CompactSemanticEnvelopeNativeTokenHarness.EvaluateVariantGates(
            CompactSemanticEnvelopeNativeTokenHarness.V1,
            v1Records);
        var v2 = CompactSemanticEnvelopeNativeTokenHarness.EvaluateVariantGates(
            CompactSemanticEnvelopeNativeTokenHarness.V2,
            v2Records);

        WriteJsonAtomically(
            runtimeProfilePath,
            new
            {
                schemaVersion = "a648_runtime_profile_v1",
                status = "runtime_observed_measurement_complete_shutdown_pending",
                host = "127.0.0.1",
                port = 1234,
                expectedContextTokens = 4096,
                parallelSlots = 1,
                runtimePid,
                runtimeStartedAt,
                runtimeArguments,
                settingsPath,
                settingsSha256,
                modelPath,
                modelSha256,
                runtimePath,
                runtimeSha256,
                servedModelId,
                health = JsonDocument.Parse(healthBody).RootElement.Clone(),
                models = modelsDocument.RootElement.Clone(),
                observedAtUtc = DateTimeOffset.UtcNow
            });

        var requestFileHash = Sha256File(requestPath);
        var snapshotFileHash = Sha256File(snapshotPath);
        WriteJsonAtomically(
            summaryPath,
            new
            {
                schemaVersion = "a648_native_token_measurement_v1",
                status = "MEASURED_NOT_AUDITED_NOT_SELECTED",
                rawDecision = "A649_AUDIT_REQUIRED_NO_SELECTION_IN_A648",
                model = servedModelId,
                expectedRuntimeContextTokens = 4096,
                controllerInputLimit = CompactSemanticEnvelopeNativeTokenHarness.ControllerInputLimit,
                reviewerInputLimit = CompactSemanticEnvelopeNativeTokenHarness.ReviewerInputLimit,
                requests = new
                {
                    tokenizer = measurementRecords.Count,
                    controller = measurementRecords.Count(item => item.Role == "controller"),
                    reviewerRepresentative = measurementRecords.Count(item =>
                        item.Role == "reviewer" && item.Form == "representative"),
                    reviewerMinimal = measurementRecords.Count(item =>
                        item.Role == "reviewer" && item.Form == "minimal"),
                    uniquePayloads = measurementRecords.Select(item => item.PayloadSha256).Distinct().Count(),
                    endpointLedger = guard.Ledger.Count,
                    forbidden = guard.ForbiddenRequestCount,
                    completion = 0,
                    completionTokens = 0,
                    modelOutputs = 0
                },
                endpointLedger = guard.Ledger,
                variants = new[] { v1, v2 },
                artifacts = new
                {
                    requestsSha256 = requestFileHash,
                    snapshotsSha256 = snapshotFileHash
                },
                completedAtUtc = DateTimeOffset.UtcNow
            });

        CompactSemanticEnvelopeNativeTokenHarness.WriteCheckpointAtomically(
            checkpointPath,
            new
            {
                schemaVersion = "a648_native_token_checkpoint_v1",
                status = "measurement_complete_shutdown_pending",
                completedRequests = measurementRecords.Count,
                totalRequests = schedule.Count,
                uniquePayloads = measurementRecords.Select(item => item.PayloadSha256).Distinct().Count(),
                forbiddenRequestCount = guard.ForbiddenRequestCount,
                summarySha256 = Sha256File(summaryPath),
                requestsSha256 = requestFileHash,
                snapshotsSha256 = snapshotFileHash,
                updatedAtUtc = DateTimeOffset.UtcNow
            });
    }

    private static object BuildSnapshotRecord(
        CompactSemanticEnvelopeNativeTokenHarness.SnapshotEnvelope envelope)
    {
        var representativeDraft = envelope.Case.ExpectedDecision == "answer"
            ? CompactSemanticEnvelopeNativeTokenHarness.BuildRepresentativeDraft(envelope.Case)
            : null;
        var minimalDraft = representativeDraft is null
            ? null
            : CompactSemanticEnvelopeNativeTokenHarness.BuildMinimalDraft();
        return new
        {
            envelope.Case.CasePosition,
            envelope.Case.Id,
            envelope.Case.ExpectedDecision,
            snapshotChars = envelope.Json.Length,
            snapshotBytes = Encoding.UTF8.GetByteCount(envelope.Json),
            snapshotSha256 = envelope.Sha256,
            evidenceCount = envelope.Snapshot.Evidence.Count,
            representativeDraftChars = representativeDraft?.Length,
            representativeDraftBytes = representativeDraft is null
                ? (int?)null
                : Encoding.UTF8.GetByteCount(representativeDraft),
            representativeDraftSha256 = representativeDraft is null
                ? null
                : Sha256Utf8(representativeDraft),
            minimalDraftChars = minimalDraft?.Length,
            minimalDraftBytes = minimalDraft is null
                ? (int?)null
                : Encoding.UTF8.GetByteCount(minimalDraft),
            minimalDraftSha256 = minimalDraft is null
                ? null
                : Sha256Utf8(minimalDraft),
            snapshot = JsonDocument.Parse(envelope.Json).RootElement.Clone()
        };
    }

    private static async Task<string> GetRequiredBodyAsync(
        HttpClient http,
        string path,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        Assert.False(string.IsNullOrWhiteSpace(body));
        return body;
    }

    private static void WriteJsonAtomically(string path, object value)
        => WriteTextAtomically(
            path,
            JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteJsonLinesAtomically(
        string path,
        IEnumerable<object> values)
        => WriteTextAtomically(
            path,
            string.Join(
                "\n",
                values.Select(value => JsonSerializer.Serialize(value, JsonLineOptions))) + "\n");

    private static void WriteTextAtomically(string path, string text)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, text, Utf8NoBom);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string RequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"A648_REQUIRED_ENVIRONMENT_MISSING:{name}");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RAG.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("A648_REPOSITORY_ROOT_NOT_FOUND");
    }

    private static string Sha256Utf8(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record CapturedRequest(
        int Sequence,
        string Variant,
        string Role,
        int CasePosition,
        string CaseId,
        string Form,
        int Repetition,
        int MessageCount,
        int MessagesChars,
        int MessagesBytes,
        int ToolCount,
        int ToolsBytes,
        int PayloadChars,
        int PayloadBytes,
        string PayloadSha256,
        int InputTokens,
        long HttpElapsedMilliseconds,
        int StatusCode,
        JsonElement Body);
}
