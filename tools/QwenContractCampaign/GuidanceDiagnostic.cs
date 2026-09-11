using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SAAIA.Client.ToolAgent.Tests.ThreeFormatQwenContractHarness;

internal static class GuidanceDiagnostic
{
    private const string DataHash = "9B1825FA56088129B42D6A73728E2E169140901FE1CE5138E69BCA31297BEE1C";
    private const string PrefixDataHash = "516DFB1FFFE715CF81960D75712400B4585DD6029B86E5C43382F4B36E4E5C2D";
    private const string Control = "SHORT_GUIDANCE_CONTROL";
    private const string Treatment = "EXPLICIT_GENERAL_GUIDANCE";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Measurement(int CasePosition, string Arm, string Role, string Form, string Stage, string? Action, JsonObject Body);
    private sealed record Counted(Measurement Request, int InputTokens);

    // Task-level guidance only: no case names, values, domains, language rules or tool argument repairs.
    private const string ControllerGuidance = """

You are completing a documentary task. CURRENT SNAPSHOT evidence has ALREADY been retrieved and is available for your reasoning now.
Decide whether this evidence satisfies the actual requested deliverable before choosing a tool.
answer: the visible evidence supports the requested facts and the complete requested deliverable. Use claims with exact observed evidence IDs. A requested comparison or calculation can be reasoned from visible values; do not search merely because the question asks for one.
search: a documentary fact is missing and further retrieval could supply it.
context: an observed passage or pointer needs its surrounding text, continuation or table details. Reuse its observed document/chunk identity.
navigation: discover document structure or a relevant location not yet known.
content_cards: inspect an inventory of source-backed content items when useful.
clarify: a USER choice, preference, referent or constraint is missing and prevents the requested unique result. Do not ask the user to supply the documentary fact they asked you to find. Repeating their question is not clarification.
insufficient: the documented available search opportunities are exhausted and required facts remain unavailable. Do not invent them.
A conditionally listed set of alternatives does not satisfy a request for exactly one choice when the user's selection is unknown.
Scope fields have distinct types: a document path is not a category. Use categoryPath/categoryRef only for an exact value in observedCategories; otherwise omit those optional filters. Never derive a category from a filename or invent an ID.
The response must contain exactly the required native tool call, with no separate answer or control text.
""";
    private const string ReviewerGuidance = """

Review both factual support AND fulfillment of the user's actual deliverable. Individually true statements are not enough when an essential user choice is missing or the requested unique conclusion is unsupported.
accept only a fully supported draft that satisfies the request. Use clarify for a genuinely missing user choice; use documentary tools for facts missing from sources; block an unsupported draft.
You cannot rewrite the draft, silently add claims or publish your own text. Even the word accept must not appear outside the native tool call.
A file path is not a category. Category filters require exact observedCategories values; otherwise omit them. Preserve observed identities exactly.
""";

    private static void Apply(JsonObject body, string role)
        => body["messages"]![0]!["content"] = body["messages"]![0]!["content"]!.GetValue<string>()
           + (role == "controller" ? ControllerGuidance : ReviewerGuidance);

    private static void ApplyForArm(JsonObject body, string role, string arm, bool nativePrefix)
    {
        if (arm == Treatment) Apply(body, role);
        if (nativePrefix) ApplyNativeToolPrefix(body);
    }

    public static async Task Run(string root, string directory, FrozenInputs basis, bool offline, bool nativePrefix = false)
    {
        var campaign = nativePrefix ? "A669" : "A666";
        var dataHash = nativePrefix ? PrefixDataHash : DataHash;
        var prep = Path.Combine(root, "artifacts", "reprise-pc-20260908", campaign.ToLowerInvariant() + "-preparation");
        if (nativePrefix && ParseJson(File.ReadAllText(Path.Combine(root, "artifacts", "reprise-pc-20260908",
            "a668-native-prefix", "live-01", "verdict.json"))).GetProperty("verdict").GetString() != "PASS_MECHANICAL_ONLY")
            throw new InvalidOperationException("A668 mechanical qualification required");
        var bytes = File.ReadAllBytes(Path.Combine(prep, "cases.json"));
        if (Convert.ToHexString(SHA256.HashData(bytes)) != dataHash) throw new InvalidOperationException("Diagnostic case drift");
        var fixtures = ParseJson(Encoding.UTF8.GetString(bytes)).GetProperty("cases").EnumerateArray().ToArray();
        if (fixtures.Length != 18 || fixtures.Count(f => f.GetProperty("expectedDecision").GetString() == "answer") != 6)
            throw new InvalidOperationException("A666 fixture coverage");
        var states = fixtures.Select((fixture, i) => BuildState(fixture, i + 1)).ToArray();
        var scoring = fixtures.Select((f, i) => (f, position: i + 1)).ToDictionary(p => p.position, p => p.f);
        var inputs = basis with { States = states, ScoringCases = scoring };
        var measurements = BuildMeasurements(inputs, nativePrefix);
        if (measurements.Count != 864) throw new InvalidOperationException("A666 measurement coverage");
        foreach (var control in measurements.Where(m => m.Arm == Control))
        {
            var variant = measurements.Single(m => m.Arm == Treatment && m.CasePosition == control.CasePosition
                && m.Role == control.Role && m.Form == control.Form && m.Stage == control.Stage && m.Action == control.Action);
            var left = control.Body.DeepClone(); var right = variant.Body.DeepClone();
            left["messages"]![0]!["content"] = ""; right["messages"]![0]!["content"] = "";
            if (!JsonNode.DeepEquals(left, right)) throw new InvalidOperationException("A666 more than system guidance changed");
        }
        if (Directory.Exists(directory)) throw new InvalidOperationException("A666 directory already exists");
        Directory.CreateDirectory(directory);
        var serialized = JsonSerializer.Serialize(measurements, Json);
        if (!offline)
        {
            var preflightSeal = ParseJson(File.ReadAllText(Path.Combine(prep, "offline-run-01", "input-seal.json")));
            if (preflightSeal.GetProperty("requestSha256").GetString() != Hash(serialized)) throw new InvalidOperationException("A666 request drift since offline preflight");
        }
        WriteCheckpointAtomically(Path.Combine(directory, "input-seal.json"), new { dataHash, requestSha256 = Hash(serialized), requests = measurements.Count, nativePrefix,
            controllerGuidance = ControllerGuidance, reviewerGuidance = ReviewerGuidance, status = "sealed_before_network" });
        File.WriteAllText(Path.Combine(directory, "frozen-requests.json"), serialized, new UTF8Encoding(false));
        if (offline)
        {
            WriteCheckpointAtomically(Path.Combine(directory, "offline-result.json"), new { status = "PASS", cases = 18, requests = 864, onlySystemGuidanceChanges = true, networkCalls = 0 });
            Console.WriteLine(campaign + " offline fixtures/builders and single-factor request equality PASS; zero network.");
            return;
        }
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1234"), Timeout = Timeout.InfiniteTimeSpan };
        using var preflightDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var counted = new List<Counted>();
        foreach (var m in measurements)
        {
            int? first = null;
            for (var repetition = 1; repetition <= 2; repetition++)
            {
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(preflightDeadline.Token);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await http.PostAsync("/v1/chat/completions/input_tokens", new StringContent(m.Body.ToJsonString(), Encoding.UTF8, "application/json"), requestDeadline.Token);
                var raw = await response.Content.ReadAsStringAsync(requestDeadline.Token);
                AppendJsonLine(Path.Combine(directory, "preflight-counts.jsonl"), new { m.CasePosition, m.Arm, m.Role, m.Form, m.Stage, m.Action, repetition,
                    requestSha256 = Hash(m.Body.ToJsonString()), status = (int)response.StatusCode, response = raw });
                response.EnsureSuccessStatusCode();
                var count = ParseJson(raw).GetProperty("input_tokens").GetInt32();
                if (count <= 0 || first is not null && count != first) throw new InvalidOperationException("A666 unstable native count");
                first = count;
            }
            counted.Add(new(m, first!.Value));
        }
        foreach (var arm in new[] { Control, Treatment })
        {
            var armRows = counted.Where(c => c.Request.Arm == arm).ToArray();
            var maxController = armRows.Where(c => c.Request.Role == "controller").Max(c => c.InputTokens);
            var worstReview = armRows.Where(c => c.Request.Form == "representative").Max(rep =>
            {
                var minimum = armRows.Single(c => c.Request.Form == "minimal" && c.Request.CasePosition == rep.Request.CasePosition
                    && c.Request.Stage == rep.Request.Stage && c.Request.Action == rep.Request.Action);
                return Math.Max(rep.InputTokens, minimum.InputTokens + 512) + 32;
            });
            AppendJsonLine(Path.Combine(directory, "preflight-gates.jsonl"), new { arm, maxController, worstReview, eligible = maxController <= 3392 && worstReview <= 3648 });
            if (maxController > 3392 || worstReview > 3648) throw new InvalidOperationException("A666 preflight input budget; no generation allowed");
        }
        // Each transaction receives a fresh counter/session wrapper; server profile remains fixed.
        var records = new List<RunRecord>();
        var stopped = new HashSet<string>();
        var runtimeHealthy = true;
        var sequence = 0;
        var httpLedger = new List<object>();
        foreach (var state in states)
        foreach (var arm in state.CasePosition % 2 == 1 ? new[] { Control, Treatment } : new[] { Treatment, Control })
        {
            sequence++;
            if (!runtimeHealthy || stopped.Contains(arm)) continue;
            RunOutcome? outcome = null; ProtocolRejection? rejection = null; var fatal = new List<string>(); var strict = false;
            try
            {
                var runner = new NativeTransaction(http, directory, httpLedger, (body, role) => ApplyForArm(body, role, arm, nativePrefix));
                outcome = await runner.Execute(inputs, new ScheduleItem(sequence, state.CasePosition, Routed), state, CancellationToken.None);
                strict = ScoreStrictCase(scoring[state.CasePosition], outcome);
                if (outcome.Controller.Action == "answer" && !strict && scoring[state.CasePosition].GetProperty("isDangerous").GetBoolean()) fatal.Add("dangerous_false_answer");
                if (outcome.ModelCalls != (outcome.Reviewer is null ? 2 : 4)) fatal.Add("call_count_or_retry");
                if (outcome.PublishedText is not null && (outcome.Reviewer?.Action != "accept" || !outcome.SourceContractValid)) fatal.Add("publication_without_review");
            }
            catch (ProtocolRejectedException error)
            {
                rejection = error.Rejection;
                if (!IsOrdinaryProtocolRejection(rejection.Code)) fatal.Add("misclassified_protocol_rejection");
                if (rejection.PartialController?.Action == "answer" && scoring[state.CasePosition].GetProperty("isDangerous").GetBoolean()) fatal.Add("dangerous_false_answer");
            }
            catch (Exception error)
            {
                fatal.Add(error.GetType().Name + ":" + error.Message);
                if (error is HttpRequestException or OperationCanceledException || error.Message.Contains("runtime", StringComparison.Ordinal))
                {
                    try { await new NativeTransaction(http, directory, httpLedger).CheckHealth(); }
                    catch { runtimeHealthy = false; }
                }
            }
            if (fatal.Count > 0) { strict = false; stopped.Add(arm); }
            var record = new RunRecord(new ScheduleItem(sequence, state.CasePosition, arm), outcome, strict, fatal, rejection);
            records.Add(record);
            AppendJsonLine(Path.Combine(directory, "runs.jsonl"), record);
            WriteCheckpointAtomically(Path.Combine(directory, "checkpoint.json"), new CampaignResult(records, stopped.Order().ToArray(), runtimeHealthy));
        }
        WriteCheckpointAtomically(Path.Combine(directory, "http-ledger.private.json"), httpLedger);
        var packet = BuildBlindPacket(inputs, records, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), includeAll: nativePrefix);
        File.WriteAllText(Path.Combine(directory, "blind-packet.json"), packet.PublicJson, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "blind-key.private.json"), packet.PrivateKeyJson, new UTF8Encoding(false));
        WriteCheckpointAtomically(Path.Combine(directory, "blind-seal.json"), new { packet.PacketSha256, packet.KeySha256, transactions = records.Count,
            status = "awaiting_judgment_before_reveal", calls = httpLedger.Count });
        Console.WriteLine(campaign + " collection complete. Read the blind packet before the private key or family results.");
    }

    private static ModelState BuildState(JsonElement fixture, int position)
    {
        var docId = "fixture-document-" + position.ToString("D2");
        var docPath = fixture.GetProperty("document").GetString()!;
        var revision = "synthetic-revision-1";
        var evidence = fixture.GetProperty("excerpts").EnumerateArray().Select((e, n) => new Evidence("E" + (n + 1), docId, revision,
            Hash(docId + "|" + revision), Path.GetFileName(docPath), docPath, n + 1, n + 1, "fixture:" + docId + ":chunk:" + (n + 1),
            docPath + "#page=" + (n + 1), "chunk", e.GetString()!)).ToArray();
        var value = JsonSerializer.Serialize(new { version = "a666_synthetic_fixture_v1", identityOrigin = "synthetic_not_real_pdf",
            question = fixture.GetProperty("question").GetString(), language = fixture.GetProperty("language").GetString(),
            focus = fixture.GetProperty("question").GetString(), provisionalSemanticPlan = new { authority = "non_authoritative", text = "" },
            document = new { docId, revisionId = revision, docName = Path.GetFileName(docPath), docPath }, observedCategories = Array.Empty<string>(),
            evidence, rejectedEvidence = Array.Empty<object>(), actions = Array.Empty<object>(), executedRoutes = Array.Empty<object>(),
            budgets = new { contextTokens = 4096, controllerInput = 3392, controllerOutput = 512, reviewerInput = 3648, reviewerOutput = 256,
                reserve = 64, headroom = 128, modelCallsRemaining = 4, toolCallsRemaining = 4, millisecondsRemaining = 180000 } }, Json);
        return new(position, fixture.GetProperty("id").GetString()!, value, Hash(value), evidence, new HashSet<string>());
    }

    private static List<Measurement> BuildMeasurements(FrozenInputs inputs, bool nativePrefix)
    {
        var result = new List<Measurement>();
        foreach (var state in inputs.States)
        foreach (var arm in new[] { Control, Treatment })
        {
            AddRole("controller", "controller", null);
            var representative = JsonSerializer.Serialize(new { presentation = "paragraphs", claims = state.Evidence.Select(e => new { text = e.Excerpt, evidenceIds = new[] { e.Id } }) });
            var minimal = JsonSerializer.Serialize(new { presentation = "paragraphs", claims = new[] { new { text = ".", evidenceIds = new[] { "E1" } } } });
            AddRole("reviewer", "representative", representative);
            AddRole("reviewer", "minimal", minimal);
            void AddRole(string role, string form, string? draft)
            {
                var verifier = role == "reviewer" ? "{\"valid\":true,\"errors\":[]}" : null;
                Add(BuildRouteRequest(inputs, state, role, draft, verifier));
                foreach (var action in inputs.Contracts.GetProperty("customSchemas").GetProperty(role + "Route").GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray())
                    Add(BuildSpecializedPayloadRequest(inputs, state, action.GetString()!, role, draft, verifier));
                void Add(Request request)
                {
                    var body = JsonNode.Parse(request.Body.GetRawText())!.AsObject();
                    body["model"] = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf";
                    ApplyForArm(body, role, arm, nativePrefix);
                    var raw = body.ToJsonString();
                    foreach (var forbidden in new[] { "expectedDecision", "expectedEvidenceIds", "requiredPatterns", "isDangerous" })
                        if (raw.Contains(forbidden, StringComparison.Ordinal)) throw new InvalidOperationException("Oracle in request");
                    result.Add(new(state.CasePosition, arm, role, form, request.Stage, request.SelectedAction, body));
                }
            }
        }
        return result;
    }
}
