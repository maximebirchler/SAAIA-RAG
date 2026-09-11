using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using static SAAIA.Client.ToolAgent.Tests.ThreeFormatQwenContractHarness;

// Manual A659 infrastructure; never linked into the product. No documentary endpoint.
if (args.Length != 2 || args[0] is not ("--a659" or "--self-test" or "--a666" or "--a666-offline" or "--a669" or "--a669-offline")) throw new ArgumentException("Explicit mode and a new output directory are required.");
var directory = Path.GetFullPath(args[1]);
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "RAG.sln"))) root = root.Parent;
var phase = Path.Combine(root!.FullName, "artifacts", "goal-rag-product-20260827-1041", "phase5");
var input = LoadFrozenInputs(Path.Combine(phase, "A656-MANIFESTE-CONTRATS-TROIS-FORMATS.json"),
    Path.Combine(phase, "A630-CASES-NORMALISES-ORACLES.json"), Path.Combine(phase, "A656-ORDRE-MICROCAMPAGNE-25-CAS.json"));
if (args[0].StartsWith("--a666", StringComparison.Ordinal) || args[0].StartsWith("--a669", StringComparison.Ordinal))
{
    await GuidanceDiagnostic.Run(root.FullName, directory, input, args[0].EndsWith("-offline", StringComparison.Ordinal),
        nativePrefix: args[0].StartsWith("--a669", StringComparison.Ordinal));
    return;
}
var eligibility = ParseJson(File.ReadAllText(Path.Combine(root.FullName, "artifacts", "reprise-pc-20260908", "a658-native-pc-01", "measurements.json")));
if (eligibility.GetProperty("verdict").GetString() != "PASS_A658_AT_LEAST_TWO_FORMATS_NATIVE_BUDGET_ELIGIBLE") throw new InvalidOperationException("A658 not eligible");
var eligible = eligibility.GetProperty("eligibleFamilies").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
if (!eligible.SetEquals(new[] { Direct, Routed })) throw new InvalidOperationException("Campaign requires the frozen two eligible families.");
if (args[0] == "--self-test")
{
    if (Directory.Exists(directory)) throw new InvalidOperationException("Self-test directory already exists");
    Directory.CreateDirectory(directory);
    using var fakeHttp = new HttpClient(new OfflineResponses(input.States[0])) { BaseAddress = new Uri("http://127.0.0.1:1234") };
    var fakeLedger = new List<object>();
    var test = new NativeTransaction(fakeHttp, directory, fakeLedger);
    foreach (var family in new[] { Direct, Routed })
    {
        var actual = await test.Execute(input, new ScheduleItem(1, 1, family), input.States[0], CancellationToken.None);
        if (!actual.SourceContractValid || actual.Reviewer?.Action != "accept" || string.IsNullOrWhiteSpace(actual.PublishedText)
            || !actual.PublishedText.Contains("[E1]") || actual.ModelCalls != (family == Direct ? 2 : 4))
            throw new InvalidOperationException("Offline transaction parity failed");
    }
    WriteCheckpointAtomically(Path.Combine(directory, "offline-result.json"), new { status = "PASS", networkCalls = 0,
        mockCompletionCalls = test.CompletionCalls, productSourceContractVerified = true });
    Console.WriteLine("Offline transport, route/payload, product Source Contract and publication gates PASS; no network.");
    return;
}
using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1234"), Timeout = Timeout.InfiniteTimeSpan };
var metadata = new List<object>();
var measurement = new NativeTransaction(http, directory, metadata);
await measurement.CheckHealth();
var campaign = await RunFailFast(input, eligible, directory, (scheduled, state, ct) => measurement.Execute(input, scheduled, state, ct));
WriteCheckpointAtomically(Path.Combine(directory, "http-ledger.json"), metadata);
var salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
var packet = BuildBlindPacket(input, campaign.Runs, salt);
File.WriteAllText(Path.Combine(directory, "blind-packet.json"), packet.PublicJson, new UTF8Encoding(false));
File.WriteAllText(Path.Combine(directory, "blind-key.private.json"), packet.PrivateKeyJson, new UTF8Encoding(false));
WriteCheckpointAtomically(Path.Combine(directory, "blind-seal.json"), new { packet.PacketSha256, packet.KeySha256,
    status = "awaiting_judgments_before_reveal", calls = measurement.CompletionCalls, executedTransactions = campaign.Runs.Count });
// Do not reveal family scores or execution order before the blind review.
Console.WriteLine("A659 collection finished; blind packet and sealed private ledger are available.");

internal sealed class NativeTransaction(HttpClient http, string directory, List<object> ledger,
    Action<JsonObject, string>? transform = null)
{
    public int CompletionCalls { get; private set; }
    private string? model;
    private int transactionCalls;
    private int controllerTokens;
    private int? reviewerTokens;
    private Stopwatch transaction = new();
    private readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task CheckHealth()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Send(HttpMethod.Get, "/health", null, timeout.Token, "health");
        var models = ParseJson(await Send(HttpMethod.Get, "/v1/models", null, timeout.Token, "models"));
        var next = models.GetProperty("data")[0].GetProperty("id").GetString()!;
        if (Path.GetFileName(next) != "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf" || model is not null && next != model)
            throw new InvalidOperationException("runtime_model_drift");
        model = next;
    }

    public async Task<RunOutcome> Execute(FrozenInputs inputs, ScheduleItem scheduled, ModelState state, CancellationToken cancellation)
    {
        await CheckHealth();
        transaction = Stopwatch.StartNew(); transactionCalls = 0; controllerTokens = 0; reviewerTokens = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        ParsedAction? knownController = null;
        var controller = await Decide("controller", null, null);
        knownController = controller;
        ParsedAction? reviewer = null;
        var valid = false;
        string? published = null;
        if (controller.Action == "answer")
        {
            var claims = controller.Arguments.GetProperty("claims").EnumerateArray().ToArray();
            var renderedClaims = claims.Select(claim => claim.GetProperty("text").GetString() + " "
                + string.Join(" ", claim.GetProperty("evidenceIds").EnumerateArray().Select(e => "[" + e.GetString() + "]"))).ToArray();
            var rendered = controller.Arguments.GetProperty("presentation").GetString() == "bullets"
                ? string.Join("\n", renderedClaims.Select(line => "- " + line)) : string.Join("\n\n", renderedClaims);
            var ids = claims.SelectMany(c => c.GetProperty("evidenceIds").EnumerateArray().Select(e => e.GetString()!)).Distinct().ToArray();
            var evidence = state.Evidence.Select(e => new EvidenceItem(e.Id, e.Kind, "rag.search", "", e.DocId, e.DocName, e.DocPath,
                e.SourceHash, e.RevisionId, e.PageStart, e.PageEnd, e.ChunkId, e.Excerpt, e.Excerpt, null, 0, null, null, null, null, null,
                new Dictionary<string, string>(), new Dictionary<string, string>(), [], [])).ToArray();
            var bundle = new EvidenceBundle("a659-" + state.Sha256, ParseJson(state.Json).GetProperty("question").GetString()!, evidence, []);
            var verification = SourceContractVerifier.Verify(new WriterDraft(rendered, ids), bundle,
                allowedEvidenceIds: state.Evidence.Select(e => e.Id).ToArray(), enforceRequestedShape: false,
                allowMultipleEvidencePerVisibleSource: true);
            valid = verification.IsValid;
            var sourceContract = JsonSerializer.Serialize(new { valid, errors = verification.Errors }, json);
            reviewer = await Decide("reviewer", controller.Arguments.GetRawText(), sourceContract);
            if (valid && reviewer.Action == "accept") published = rendered;
        }
        return new RunOutcome(controller, reviewer, published, valid, true, true, controllerTokens, reviewerTokens,
            transaction.ElapsedMilliseconds, transactionCalls);

        async Task<ParsedAction> Decide(string role, string? draft, string? sourceContract)
        {
            if (scheduled.Variant == Direct)
            {
                var request = BuildDirectRequest(inputs, state, role, draft, sourceContract);
                var response = await Complete(request);
                return ParseModel(() => ParseDirectResponse(inputs, state, response, role), request, response);
            }
            var routeRequest = BuildRouteRequest(inputs, state, role, draft, sourceContract);
            var routeResponse = await Complete(routeRequest);
            var action = ParseModel(() => ParseRouteResponse(inputs, routeResponse, role), routeRequest, routeResponse);
            var payloadRequest = BuildSpecializedPayloadRequest(inputs, state, action, role, draft, sourceContract);
            var payloadResponse = await Complete(payloadRequest);
            return ParseModel(() => ParseSpecializedPayloadResponse(inputs, state, action, payloadResponse, role), payloadRequest, payloadResponse);
        }

        async Task<string> Complete(Request request)
        {
            var body = JsonNode.Parse(request.Body.GetRawText())!.AsObject(); body["model"] = model;
            transform?.Invoke(body, request.Role);
            var bytes = body.ToJsonString();
            var tokensResponse = ParseJson(await Send(HttpMethod.Post, "/v1/chat/completions/input_tokens", bytes, deadline.Token, request.Role + "/" + request.Stage + "/count"));
            var tokens = tokensResponse.GetProperty("input_tokens").GetInt32();
            if (tokens <= 0 || tokens > (request.Role == "controller" ? 3392 : 3648)) throw new InvalidOperationException("context_budget_exceeded");
            if (request.Role == "controller") controllerTokens = Math.Max(controllerTokens, tokens);
            else reviewerTokens = Math.Max(reviewerTokens ?? 0, tokens);
            transactionCalls++; CompletionCalls++;
            var response = await Send(HttpMethod.Post, "/v1/chat/completions", bytes, deadline.Token, request.Role + "/" + request.Stage);
            var result = ParseJson(response);
            var usage = result.GetProperty("usage");
            if (usage.GetProperty("prompt_tokens").GetInt32() != tokens) throw new InvalidOperationException("native_usage_count_drift");
            if (usage.GetProperty("completion_tokens").GetInt32() > request.Body.GetProperty("max_tokens").GetInt32()) throw new InvalidOperationException("output_budget_exceeded");
            return response;
        }

        T ParseModel<T>(Func<T> parse, Request request, string response)
        {
            try { return parse(); }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                var code = error switch { JsonException => "invalid_json", KeyNotFoundException => "missing_response_property", _ => error.Message };
                if (!IsOrdinaryProtocolRejection(code)) throw;
                var message = ParseJson(response).GetProperty("choices")[0].GetProperty("message");
                var payload = message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0
                    ? calls[0].GetProperty("function").GetProperty("arguments").GetString() ?? "" : message.GetRawText();
                throw new ProtocolRejectedException(new ProtocolRejection(request.Role + "/" + request.Stage, code, payload,
                    request.Role == "controller" ? controllerTokens : reviewerTokens ?? 0, transaction.ElapsedMilliseconds, transactionCalls, knownController));
            }
        }
    }

    private async Task<string> Send(HttpMethod method, string path, string? body, CancellationToken ct, string label)
    {
        if (!(method == HttpMethod.Get && path is "/health" or "/v1/models"
            || method == HttpMethod.Post && path is "/v1/chat/completions/input_tokens" or "/v1/chat/completions"))
            throw new InvalidOperationException("forbidden_endpoint");
        var started = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        string? text = null; int? status = null; string? failure = null;
        try
        {
            using var response = await http.SendAsync(request, ct);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(ct);
            response.EnsureSuccessStatusCode();
            return text;
        }
        catch (Exception error) { failure = error.GetType().Name + ":" + error.Message; throw; }
        finally
        {
            var entry = new { sequence = ledger.Count + 1, method = method.Method, path, label, requestSha256 = Hash(body ?? ""), body,
                responseSha256 = text is null ? null : Hash(text), response = text, status, error = failure, milliseconds = started.ElapsedMilliseconds };
            ledger.Add(entry);
            if (Directory.Exists(directory)) AppendJsonLine(Path.Combine(directory, "http-requests.private.jsonl"), entry);
        }
    }
}

internal sealed class OfflineResponses(ModelState state) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        object value;
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/health": value = new { status = "ok" }; break;
            case "/v1/models": value = new { data = new[] { new { id = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf" } } }; break;
            case "/v1/chat/completions/input_tokens": value = new { input_tokens = 100 }; break;
            case "/v1/chat/completions":
                var body = ParseJson(await request.Content!.ReadAsStringAsync(ct));
                var review = body.GetProperty("messages")[0].GetProperty("content").GetString()!.StartsWith("Review", StringComparison.Ordinal);
                var name = body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString()!;
                var args = name == "select_source_backed_action" ? JsonSerializer.Serialize(new { action = review ? "accept" : "answer" })
                    : review ? "{}" : JsonSerializer.Serialize(new { presentation = "paragraphs", claims = new[] { new { text = state.Evidence[0].Excerpt, evidenceIds = new[] { "E1" } } } });
                var message = new
                {
                    role = "assistant", content = (string?)null,
                    tool_calls = new[] { new { id = "offline-only", type = "function", function = new { name, arguments = args } } }
                };
                value = new
                {
                    choices = new[] { new { finish_reason = "tool_calls", message } },
                    usage = new { prompt_tokens = 100, completion_tokens = 20 }
                };
                break;
            default: throw new InvalidOperationException("Offline unexpected endpoint");
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
