using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static SAAIA.Client.ToolAgent.Tests.ThreeFormatQwenContractHarness;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ThreeFormatQwenContractHarnessTests
{
    private static readonly Lazy<FrozenInputs> Frozen = new(() => LoadFrozenInputs(Artifact("A656-MANIFESTE-CONTRATS-TROIS-FORMATS.json"), Artifact("A630-CASES-NORMALISES-ORACLES.json"), Artifact("A656-ORDRE-MICROCAMPAGNE-25-CAS.json")));
    private static FrozenInputs Inputs => Frozen.Value;
    private static ModelState State => Inputs.States[0];
    private static string Json(object value) => JsonSerializer.Serialize(value);
    private static string Native(string tool, string arguments) => Json(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "offline-call", type = "function", function = new { name = tool, arguments } } } } } } });
    private static string GrammarResponse(JsonObject value) => Json(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = value.ToJsonString() } } } });
    private static string Args(string action, ModelState? state = null)
    {
        state ??= State;
        return action switch
        {
            "answer" => Json(new { presentation = "paragraph", claims = new[] { new { text = state.Evidence[0].Excerpt, evidenceIds = new[] { state.Evidence[0].Id } } } }),
            "search" => Json(new { query = state.Evidence[0].Excerpt }),
            "context" => Json(new { docId = state.Evidence[0].DocId, chunkId = state.Evidence[0].ChunkId }),
            "clarify" => Json(new { message = "Which scope should be used?", evidenceIds = Array.Empty<string>() }),
            "insufficient" => Json(new { missingFacts = new[] { "The requested fact is absent." }, usefulEvidenceIds = Array.Empty<string>() }),
            "block" => Json(new { reason = "The draft is unsupported.", usefulEvidenceIds = Array.Empty<string>() }),
            _ => "{}"
        };
    }
    private static string ToolNameFor(string action) => action switch
    {
        "answer" => "submit_source_backed_answer_draft",
        "search" => "rag_search",
        "navigation" => "documents_navigation",
        "content_cards" => "documents_content_cards",
        "context" => "documents_context",
        "clarify" => "request_source_backed_clarification",
        "insufficient" => "declare_source_backed_insufficiency",
        "accept" => "accept_source_backed_answer",
        "block" => "block_source_backed_answer",
        _ => throw new ArgumentException(action)
    };
    private static JsonObject GrammarValue(string action, string role, string args)
    {
        var obj = new JsonObject { ["action"] = action, ["documentTool"] = "", ["documentArguments"] = new JsonObject(), ["message"] = "", ["usefulEvidenceIds"] = new JsonArray() };
        if (role == "controller") { obj["presentation"] = ""; obj["claims"] = new JsonArray(); obj["missingFacts"] = new JsonArray(); }
        else obj["reason"] = "";
        if (action is "search" or "navigation" or "content_cards" or "context")
        { obj["documentTool"] = ToolNameFor(action); obj["documentArguments"] = JsonNode.Parse(args); }
        else foreach (var property in JsonNode.Parse(args)!.AsObject()) obj[property.Key == "evidenceIds" ? "usefulEvidenceIds" : property.Key] = property.Value?.DeepClone();
        return obj;
    }

    [Fact]
    public void Loads_25_states_75_frozen_transactions_and_keeps_original_red_evidence()
    {
        Assert.Equal(25, Inputs.States.Count); Assert.Equal(75, Inputs.Schedule.Count);
        Assert.Equal(Enumerable.Range(1, 75), Inputs.Schedule.Select(s => s.Sequence));
        Assert.All(Inputs.States, state => Assert.Equal(Hash(state.Json), state.Sha256));
        var red = Path.Combine(Root(), "client", "SAAIA.Client.ToolAgent.Tests", "ThreeFormatQwenContractHarnessRedTests.cs");
        Assert.Equal("06FD50E1B1A4A3AFD1FEA68679AA9F865DE35EAE6D48B1C3AFC822BFF1980B88", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(red))));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Any_frozen_input_drift_fails_before_request_building(int changed)
    {
        using var temp = new Temporary();
        var paths = new[] { Artifact("A656-MANIFESTE-CONTRATS-TROIS-FORMATS.json"), Artifact("A630-CASES-NORMALISES-ORACLES.json"), Artifact("A656-ORDRE-MICROCAMPAGNE-25-CAS.json") };
        paths[changed] = Path.Combine(temp.Path, "changed.json"); File.WriteAllText(paths[changed], "{}");
        Assert.Throws<InvalidOperationException>(() => LoadFrozenInputs(paths[0], paths[1], paths[2]));
    }
    [Fact]
    public void All_families_receive_identical_states_and_oracle_changes_cannot_change_messages()
    {
        var poisoned = Inputs with { ScoringCases = Inputs.ScoringCases.ToDictionary(p => p.Key, _ => ParseJson("{\"expectedDecision\":\"ORACLE_ONLY_CANARY\"}")) };
        foreach (var state in Inputs.States)
        {
            var requests = new[] { BuildDirectRequest(Inputs, state), BuildRouteRequest(Inputs, state), BuildGrammarRequest(Inputs, state) };
            Assert.All(requests, r => { Assert.Equal(state.Json, r.SnapshotJson); Assert.Equal(state.Sha256, r.SnapshotSha256); });
            foreach (var request in requests)
            {
                var body = request.Body.GetRawText();
                foreach (var key in new[] { "expectedDecision", "requiredPatterns", "expectedEvidenceIds", "isDangerous", "ORACLE_ONLY_CANARY" }) Assert.DoesNotContain(key, body);
            }
            Assert.Equal(BuildDirectRequest(Inputs, state).Body.GetRawText(), BuildDirectRequest(poisoned, state).Body.GetRawText());
            Assert.Equal(BuildRouteRequest(Inputs, state).Body.GetRawText(), BuildRouteRequest(poisoned, state).Body.GetRawText());
            Assert.Equal(BuildGrammarRequest(Inputs, state).Body.GetRawText(), BuildGrammarRequest(poisoned, state).Body.GetRawText());
        }
    }
    [Fact]
    public void Builders_are_distinct_and_reviewer_requires_the_frozen_draft_and_verifier()
    {
        var direct = BuildDirectRequest(Inputs, State);
        Assert.Equal(7, direct.Body.GetProperty("tools").GetArrayLength());
        var route = BuildRouteRequest(Inputs, State);
        Assert.Equal("select_source_backed_action", route.Body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Single(route.Body.GetProperty("tools").EnumerateArray());
        var payload = BuildSpecializedPayloadRequest(Inputs, State, "context");
        Assert.Equal("documents_context", payload.Body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        var grammar = BuildGrammarRequest(Inputs, State);
        Assert.False(grammar.Body.TryGetProperty("tools", out _));
        Assert.Equal("json_schema", grammar.Body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(grammar.Body.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Throws<InvalidOperationException>(() => BuildDirectRequest(Inputs, State, "reviewer"));
        foreach (var request in new[] { BuildDirectRequest(Inputs, State, "reviewer", Args("answer"), "{\"valid\":true}"), BuildRouteRequest(Inputs, State, "reviewer", Args("answer"), "{\"valid\":true}"), BuildGrammarRequest(Inputs, State, "reviewer", Args("answer"), "{\"valid\":true}") })
        { Assert.Equal(256, request.Body.GetProperty("max_tokens").GetInt32()); Assert.Contains("FROZEN_DRAFT", request.Body.GetRawText()); Assert.Contains("SOURCE_CONTRACT", request.Body.GetRawText()); }
    }
    [Theory]
    [InlineData("controller", "answer")]
    [InlineData("controller", "search")]
    [InlineData("controller", "navigation")]
    [InlineData("controller", "content_cards")]
    [InlineData("controller", "context")]
    [InlineData("controller", "clarify")]
    [InlineData("controller", "insufficient")]
    [InlineData("reviewer", "accept")]
    [InlineData("reviewer", "search")]
    [InlineData("reviewer", "navigation")]
    [InlineData("reviewer", "content_cards")]
    [InlineData("reviewer", "context")]
    [InlineData("reviewer", "clarify")]
    [InlineData("reviewer", "block")]
    public void Three_parsers_agree_for_every_allowed_role_action(string role, string action)
    {
        var response = Native(ToolNameFor(action), Args(action));
        var direct = ParseDirectResponse(Inputs, State, response, role);
        var route = ParseRouteResponse(Inputs, Native("select_source_backed_action", Json(new { action })), role);
        var routed = ParseSpecializedPayloadResponse(Inputs, State, route, response, role);
        var grammar = ParseGrammarResponse(Inputs, State, GrammarResponse(GrammarValue(action, role, Args(action))), role);
        Assert.Equal(action, direct.Action); Assert.Equal(direct.Tool, routed.Tool); Assert.Equal(direct.Tool, grammar.Tool);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(direct.Arguments.GetRawText()), JsonNode.Parse(grammar.Arguments.GetRawText())));
        Assert.Equal(direct.Arguments.GetRawText(), routed.Arguments.GetRawText());
    }
    [Theory]
    [InlineData("{\"title\":\"wrong shape\"}")]
    [InlineData("{\"query\":3}")]
    [InlineData("{\"query\":\"x\",\"unknown\":null}")]
    [InlineData("{\"query\":null}")]
    [InlineData("{\"query\":\"x\",\"query\":\"y\"}")]
    [InlineData("{\"queries\":[]}")]
    [InlineData("{\"query\":\"x\",\"topK\":1.5}")]
    [InlineData("{\"query\":\"x\",\"docId\":\"invented\"}")]
    [InlineData("{\"query\":\"x\",\"docPath\":\"invented.pdf\"}")]
    [InlineData("{\"query\":\"x\",\"mode\":\"invented\"}")]
    public void Native_parser_rejects_invalid_shapes_and_unobserved_identities(string arguments)
        => Assert.ThrowsAny<Exception>(() => ParseDirectResponse(Inputs, State, Native("rag_search", arguments)));
    [Fact]
    public void Optional_nulls_are_removed_and_query_array_uses_the_real_multi_search_mapping()
    {
        var result = ParseDirectResponse(Inputs, State, Native("RAG_SEARCH", "{\"query\":null,\"queries\":[\"x\"],\"docId\":null}"));
        Assert.Equal("rag.multi_search", result.Tool); Assert.False(result.Arguments.TryGetProperty("query", out _)); Assert.False(result.Arguments.TryGetProperty("docId", out _));
    }
    [Fact]
    public void Unknown_and_rejected_evidence_and_duplicate_claim_ids_fail_closed()
    {
        foreach (var ids in new[] { new[] { "E999" }, new[] { "E1", "E1" }, new[] { "N1" }, Array.Empty<string>() })
        {
            var args = Json(new { presentation = "paragraph", claims = new[] { new { text = "claim", evidenceIds = ids } } });
            Assert.ThrowsAny<Exception>(() => ParseDirectResponse(Inputs, State, Native(ToolNameFor("answer"), args)));
        }
        var rejected = State with { RejectedEvidenceIds = new HashSet<string> { "E1" } };
        Assert.Throws<InvalidOperationException>(() => ParseDirectResponse(Inputs, rejected, Native(ToolNameFor("answer"), Args("answer"))));
    }
    [Fact]
    public void Interface_fallbacks_route_mismatch_and_wrong_role_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ParseDirectResponse(Inputs, State, Native("invented_tool", "{}")));
        Assert.Throws<InvalidOperationException>(() => ParseDirectResponse(Inputs, State, Native(ToolNameFor("answer"), Args("answer")), "reviewer"));
        Assert.Throws<InvalidOperationException>(() => ParseSpecializedPayloadResponse(Inputs, State, "context", Native("rag_search", Args("search"))));
        Assert.Throws<InvalidOperationException>(() => ParseRouteResponse(Inputs, Native("select_source_backed_action", "{\"action\":\"search\",\"query\":\"x\"}")));
        Assert.Throws<InvalidOperationException>(() => ParseGrammarResponse(Inputs, State, Native("rag_search", Args("search"))));
        var response = JsonNode.Parse(Native("rag_search", Args("search")))!;
        response["choices"]![0]!["message"]!["content"] = "tolerant fallback";
        Assert.Throws<InvalidOperationException>(() => ParseDirectResponse(Inputs, State, response.ToJsonString()));
        response["choices"]![0]!["message"]!["content"] = null;
        var calls = response["choices"]![0]!["message"]!["tool_calls"]!.AsArray(); calls.Add(calls[0]!.DeepClone());
        Assert.Throws<InvalidOperationException>(() => ParseDirectResponse(Inputs, State, response.ToJsonString()));
        Assert.Throws<InvalidOperationException>(() => ParseDirectResponse(Inputs, State, Native("rag_search", Args("search")).Replace("tool_calls\",\"message", "length\",\"message")));
    }
    [Fact]
    public void Grammar_uses_exact_envelope_and_rejects_inactive_payloads_and_mismatched_document_tools()
    {
        var grammar = GrammarValue("search", "controller", Args("search")); grammar["presentation"] = "hidden answer";
        Assert.Throws<InvalidOperationException>(() => ParseGrammarResponse(Inputs, State, GrammarResponse(grammar)));
        grammar["presentation"] = ""; grammar["documentTool"] = "documents_context";
        Assert.Throws<InvalidOperationException>(() => ParseGrammarResponse(Inputs, State, GrammarResponse(grammar)));
        grammar["documentTool"] = "rag_search"; grammar.Remove("claims");
        Assert.Throws<InvalidOperationException>(() => ParseGrammarResponse(Inputs, State, GrammarResponse(grammar)));
    }

    [Theory]
    [InlineData("rag.search", "search")]
    [InlineData("rag.multi_search", "multi")]
    [InlineData("documents.navigation", "navigation")]
    [InlineData("documents.content_cards", "content_cards")]
    [InlineData("documents.context", "context")]
    public void All_five_routes_produce_product_readable_observations_with_canonical_identities(string route, string action)
    {
        foreach (var state in Inputs.States)
        {
            var executor = new FrozenFixtureExecutor(Inputs, state);
            var args = action == "multi" ? Json(new { queries = new[] { state.Evidence[0].Excerpt, state.Evidence[^1].Excerpt } }) : Args(action, state);
            var result = executor.Execute(route, ParseJson(args));
            var bundle = executor.Rematerialize(result);
            Assert.NotEmpty(bundle.Items); Assert.Single(executor.Ledger);
            foreach (var item in bundle.Items)
            {
                var original = state.Evidence.Single(e => e.PageStart == item.PageStart);
                Assert.Equal(original.DocId, item.DocId); Assert.Equal(original.RevisionId, item.RevisionId);
                Assert.Equal(original.SourceHash, item.SourceHash); Assert.Equal(original.DocPath, item.DocPath);
                Assert.Equal(action == "navigation" ? "N" + original.PageStart : original.Id, item.EvidenceId);
                if (action == "navigation") Assert.Contains("orientation_only", item.RiskFlags);
                else Assert.Contains(original.Id, executor.Ledger[0].CitableEvidenceIds);
            }
            if (action == "navigation") Assert.Empty(executor.Ledger[0].CitableEvidenceIds);
        }
    }
    [Fact]
    public void Pagination_context_and_fail_closed_execution_are_observable()
    {
        var executor = new FrozenFixtureExecutor(Inputs, State);
        var first = executor.Execute("documents.navigation", ParseJson("{\"limit\":1}"));
        var pointer = first.Items[0].Result.GetProperty("items")[0];
        var context = executor.Execute("documents.context", JsonSerializer.SerializeToElement(new { chunkId = pointer.GetProperty("targetChunkId").GetString() }));
        Assert.Equal("E1", Assert.Single(executor.Rematerialize(context).Items).EvidenceId);
        var cards = executor.Execute("documents.content_cards", ParseJson("{\"limit\":1}"));
        var next = cards.Items[0].Result.GetProperty("nextOffset").GetInt32();
        var second = executor.Execute("documents.content_cards", JsonSerializer.SerializeToElement(new { limit = 1, offset = next }));
        Assert.Equal("E2", Assert.Single(executor.Rematerialize(second).Items).EvidenceId);
        Assert.Equal(0, ParseJson(executor.UpdatedState().Json).GetProperty("budgets").GetProperty("toolCallsRemaining").GetInt32());
        Assert.Throws<InvalidOperationException>(() => executor.Execute("rag.search", ParseJson(Args("search"))));
        foreach (var (route, args) in new[] { ("unknown", "{}"), ("documents.context_batch", "{}"), ("documents.context", "{}"), ("documents.context", "{\"chunkId\":\"invented\"}"), ("documents.context", "{\"pageStart\":999}"), ("documents.content_cards", "{\"offset\":2}"), ("documents.navigation", "{\"limit\":0}") })
            Assert.ThrowsAny<Exception>(() => new FrozenFixtureExecutor(Inputs, State).Execute(route, ParseJson(args)));
        var repeated = new FrozenFixtureExecutor(Inputs, State); repeated.Execute("documents.navigation", ParseJson("{}"));
        Assert.Throws<InvalidOperationException>(() => repeated.Execute("documents.navigation", ParseJson("{}")));
    }
    [Fact]
    public void Identical_tool_sequences_have_identical_observations_and_state_in_every_arm()
    {
        var executors = new[] { new FrozenFixtureExecutor(Inputs, State), new FrozenFixtureExecutor(Inputs, State), new FrozenFixtureExecutor(Inputs, State) };
        foreach (var executor in executors) { executor.Execute("documents.navigation", ParseJson("{}")); executor.Execute("documents.context", ParseJson(Args("context"))); }
        Assert.Single(executors.Select(e => e.UpdatedState().Json).Distinct());
        Assert.Single(executors.Select(e => Json(e.Ledger)).Distinct());
        var forged = executors[0].Execute("rag.search", ParseJson(Args("search")));
        var raw = JsonNode.Parse(forged.Items[0].Result.GetRawText())!; raw["hits"]![0]!["sourceHash"] = "invented";
        forged.Items[0].Result = ParseJson(raw.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => executors[0].Rematerialize(forged));
    }

    [Fact]
    public void Atomic_checkpoint_retains_previous_value_on_serialization_failure_and_jsonl_is_append_only()
    {
        using var temp = new Temporary(); var checkpoint = Path.Combine(temp.Path, "checkpoint.json"); var log = Path.Combine(temp.Path, "runs.jsonl");
        WriteCheckpointAtomically(checkpoint, new { progress = 1 }); var original = File.ReadAllBytes(checkpoint);
        Assert.ThrowsAny<Exception>(() => WriteCheckpointAtomically(checkpoint, new BadSerialization()));
        Assert.Equal(original, File.ReadAllBytes(checkpoint)); Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
        WriteCheckpointAtomically(checkpoint, new { progress = 2 }); Assert.Equal(2, ParseJson(File.ReadAllText(checkpoint)).GetProperty("progress").GetInt32());
        AppendJsonLine(log, new { content = "first\nline" }); var prefix = File.ReadAllBytes(log); AppendJsonLine(log, new { content = "second" });
        Assert.Equal(prefix, File.ReadAllBytes(log).Take(prefix.Length)); Assert.Equal(2, File.ReadAllLines(log).Length);
        var broken = Path.Combine(temp.Path, "partial.jsonl"); File.WriteAllText(broken, "{\"partial\":");
        Assert.Throws<InvalidOperationException>(() => AppendJsonLine(broken, new { x = 1 })); Assert.Equal("{\"partial\":", File.ReadAllText(broken));
    }
    [Fact]
    public async Task Frozen_order_is_executed_once_and_official_directory_cannot_be_reused()
    {
        using var temp = new Temporary(); var path = Path.Combine(temp.Path, "official");
        var result = await RunFailFast(Inputs, new HashSet<string> { Direct, Routed, Grammar }, path, SearchRun);
        Assert.Equal(75, result.Runs.Count); Assert.Equal(Inputs.Schedule, result.Runs.Select(r => r.Scheduled)); Assert.Empty(result.DisqualifiedFamilies);
        Assert.Equal(75, File.ReadAllLines(Path.Combine(path, "runs.jsonl")).Length);
        Assert.Equal(75, ParseJson(File.ReadAllText(Path.Combine(path, "checkpoint.json"))).GetProperty("runs").GetArrayLength());
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunFailFast(Inputs, new HashSet<string> { Direct }, path, SearchRun));
    }
    [Fact]
    public async Task Exception_stops_only_its_family_and_blind_packet_covers_non_strict_runs_without_revealing_arm_or_timing()
    {
        using var temp = new Temporary();
        var result = await RunFailFast(Inputs, new HashSet<string> { Direct, Routed, Grammar }, Path.Combine(temp.Path, "official"), (s, state, ct) => s.Variant == Direct ? throw new IOException("private transport trace") : SearchRun(s, state, ct));
        Assert.Equal(51, result.Runs.Count); Assert.Equal(new[] { Direct }, result.DisqualifiedFamilies); Assert.Single(result.Runs, r => r.Scheduled.Variant == Direct);
        var packet = BuildBlindPacket(Inputs, result.Runs, "private-salt-at-least-16-characters");
        Assert.Equal(packet.PublicJson, BuildBlindPacket(Inputs, result.Runs.Reverse().ToArray(), "private-salt-at-least-16-characters").PublicJson);
        foreach (var forbidden in new[] { Direct, Routed, Grammar, "variant", "sequence", "milliseconds", "Tokens", "private transport trace", "Synthetic/A630", "docPath" }) Assert.DoesNotContain(forbidden, packet.PublicJson);
        Assert.Contains(Routed, packet.PrivateKeyJson); Assert.Equal(Hash(packet.PublicJson), packet.PacketSha256); Assert.Equal(Hash(packet.PrivateKeyJson), packet.KeySha256);
        Assert.True(ParseJson(packet.PublicJson).GetArrayLength() >= result.Runs.Count(r => !r.Strict));
    }
    [Theory]
    [InlineData("fallback")]
    [InlineData("publication")]
    [InlineData("budget")]
    [InlineData("identity")]
    [InlineData("endpoint")]
    public async Task Safety_breaks_stop_a_family_before_any_further_run(string fault)
    {
        using var temp = new Temporary();
        var result = await RunFailFast(Inputs, new HashSet<string> { Direct }, Path.Combine(temp.Path, "official"), async (s, state, ct) =>
        {
            var run = await SearchRun(s, state, ct);
            return fault switch
            {
                "fallback" => run with { Fallback = true },
                "publication" => run with { PublishedText = "unreviewed" },
                "budget" => run with { ControllerInputTokens = 3393 },
                "endpoint" => run with { ForbiddenEndpoint = true },
                _ => run with { Controller = run.Controller with { Arguments = ParseJson("{\"query\":\"x\",\"docId\":\"invented\"}") } }
            };
        });
        Assert.Single(result.Runs); Assert.Equal(new[] { Direct }, result.DisqualifiedFamilies); Assert.NotEmpty(result.Runs[0].FatalReasons);
    }
    [Fact]
    public void A_tampered_state_cannot_diverge_from_the_model_observation()
    {
        var changed = State with { Evidence = State.Evidence.Select((e, i) => i == 0 ? e with { Id = "E999" } : e).ToArray() };
        Assert.Throws<InvalidOperationException>(() => BuildDirectRequest(Inputs, changed));
        Assert.Throws<InvalidOperationException>(() => ParseDirectResponse(Inputs, changed, Native("rag_search", Args("search"))));
        Assert.Throws<InvalidOperationException>(() => BuildGrammarRequest(Inputs, State with { Json = State.Json + " " }));
        Assert.Throws<InvalidOperationException>(() => BuildDirectRequest(Inputs, State, "reviewer", Args("answer"), "{}"));
    }
    [Fact]
    public void Schema_bounds_apply_equally_to_grammar_and_native_payloads()
    {
        foreach (var text in new[] { "", new string('x', 501) })
        {
            var args = Json(new { presentation = "paragraph", claims = new[] { new { text, evidenceIds = new[] { "E1" } } } });
            Assert.Throws<InvalidOperationException>(() => ParseDirectResponse(Inputs, State, Native(ToolNameFor("answer"), args)));
            Assert.Throws<InvalidOperationException>(() => ParseGrammarResponse(Inputs, State, GrammarResponse(GrammarValue("answer", "controller", args))));
        }
        Assert.Throws<InvalidOperationException>(() => ParseDirectResponse(Inputs, State, Native(ToolNameFor("clarify"), "{\"message\":\"scope?\"}")));
        var reviewer = GrammarValue("accept", "reviewer", "{}"); reviewer["claims"] = new JsonArray();
        Assert.Throws<InvalidOperationException>(() => ParseGrammarResponse(Inputs, State, GrammarResponse(reviewer), "reviewer"));
    }
    [Theory]
    [InlineData("drift")]
    [InlineData("unhealthy")]
    public async Task An_unusable_runtime_stops_all_families(string fault)
    {
        using var temp = new Temporary();
        var result = await RunFailFast(Inputs, new HashSet<string> { Direct, Routed, Grammar }, Path.Combine(temp.Path, "official"), async (s, state, ct) =>
        {
            var run = await SearchRun(s, state, ct);
            return fault == "drift" ? run with { EnvironmentDrift = true } : run with { RuntimeHealthy = false };
        });
        Assert.Single(result.Runs); Assert.False(result.RuntimeHealthy);
    }
    [Fact]
    public async Task Extra_calls_are_retries_and_are_not_hidden_as_ordinary_non_strict_results()
    {
        using var temp = new Temporary();
        var result = await RunFailFast(Inputs, new HashSet<string> { Routed }, Path.Combine(temp.Path, "official"), async (s, state, ct) =>
            (await SearchRun(s, state, ct)) with { ModelCalls = 3 });
        Assert.Single(result.Runs); Assert.Contains("call_count_or_retry", result.Runs[0].FatalReasons);
    }
    [Fact]
    public async Task A_dangerous_false_answer_stops_immediately_and_is_never_strict()
    {
        using var temp = new Temporary();
        var dangerous = Inputs.States.First(state => Inputs.ScoringCases[state.CasePosition].GetProperty("isDangerous").GetBoolean());
        var result = await RunFailFast(Inputs, new HashSet<string> { Direct }, Path.Combine(temp.Path, "official"), (s, state, ct) =>
        {
            if (state.CasePosition != dangerous.CasePosition) return SearchRun(s, state, ct);
            var answer = ParseDirectResponse(Inputs, state, Native(ToolNameFor("answer"), Args("answer", state)));
            var reviewer = ParseDirectResponse(Inputs, state, Native(ToolNameFor("accept"), "{}"), "reviewer");
            return Task.FromResult(new RunOutcome(answer, reviewer, state.Evidence[0].Excerpt, true, true, true, 100, 100, 20, 2));
        });
        Assert.Equal(dangerous.CasePosition, result.Runs[^1].Scheduled.CasePosition);
        Assert.Contains("dangerous_false_answer", result.Runs[^1].FatalReasons); Assert.False(result.Runs[^1].Strict);
    }
    [Fact]
    public async Task Strict_answer_needs_the_registered_patterns_ids_and_reviewer_acceptance()
    {
        using var temp = new Temporary();
        var state = State; var oracle = Inputs.ScoringCases[state.CasePosition];
        // Oracle access stays solely on the scoring/test side, never in a request builder.
        var expected = oracle.GetProperty("expectedEvidenceIds").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        var proof = state.Evidence.Where(e => expected.Contains(e.Id)).ToArray();
        var args = Json(new { presentation = "paragraph", claims = proof.Select(e => new { text = e.Excerpt, evidenceIds = new[] { e.Id } }).ToArray() });
        var answer = ParseDirectResponse(Inputs, state, Native(ToolNameFor("answer"), args));
        var reviewer = ParseDirectResponse(Inputs, state, Native(ToolNameFor("accept"), "{}"), "reviewer");
        var result = await RunFailFast(Inputs, new HashSet<string> { Direct }, Path.Combine(temp.Path, "official"), (s, fixture, ct) =>
            s.CasePosition == state.CasePosition
                ? Task.FromResult(new RunOutcome(answer, reviewer, string.Join("\n", proof.Select(e => e.Excerpt)), true, true, true, 100, 100, 20, 2))
                : SearchRun(s, fixture, ct));
        Assert.True(result.Runs[0].Strict); Assert.Empty(result.Runs[0].FatalReasons);
        var strict = result.Runs[0];
        var disagreement = strict with
        {
            Scheduled = strict.Scheduled with { Variant = Routed },
            Strict = false,
            Outcome = strict.Outcome! with { SourceContractValid = false }
        };
        var packet = BuildBlindPacket(Inputs, [strict, disagreement], "private-salt-at-least-16-characters");
        Assert.Equal(2, ParseJson(packet.PublicJson).GetArrayLength());
    }
    private static Task<RunOutcome> SearchRun(ScheduleItem scheduled, ModelState state, CancellationToken ct)
    {
        var action = ParseDirectResponse(Inputs, state, Native("rag_search", Args("search", state)));
        return Task.FromResult(new RunOutcome(action, null, null, false, true, true, 100, null, 10, scheduled.Variant == Routed ? 2 : 1));
    }
    private sealed class BadSerialization { public string Value => throw new InvalidOperationException("intentional serialization incident"); }
    private sealed class Temporary : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "saaia-a657-" + Guid.NewGuid().ToString("N"));
        public Temporary() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
    private static string Artifact(string name) => Path.Combine(Root(), "artifacts", "goal-rag-product-20260827-1041", "phase5", name);
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RAG.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root unavailable");
    }
}
