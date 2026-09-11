using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static SAAIA.Client.ToolAgent.Tests.ThreeFormatQwenContractHarness;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ThreeFormatQwenContractHarnessReauditTests
{
    [Fact]
    public void Full_blind_review_includes_agreement_and_machine_green_outputs()
    {
        var inputs = Load(); var state = inputs.States[0];
        var action = new ParsedAction("controller", "clarify", "request_source_backed_clarification",
            ParseJson("{\"message\":\"QUESTION_REPEATED_CANARY\",\"evidenceIds\":[]}"));
        var outcome = new RunOutcome(action, null, null, false, true, true, 100, null, 20, 2);
        var records = new[] { Direct, Routed }.Select((family, n) =>
            new RunRecord(new ScheduleItem(n + 1, state.CasePosition, family), outcome, true, [])).ToArray();
        Assert.Equal(0, ParseJson(BuildBlindPacket(inputs, records, "blind-review-coverage-salt").PublicJson).GetArrayLength());
        var all = BuildBlindPacket(inputs, records, "blind-review-coverage-salt", includeAll: true);
        Assert.Equal(2, ParseJson(all.PublicJson).GetArrayLength());
        Assert.Contains("QUESTION_REPEATED_CANARY", all.PublicJson);
        Assert.DoesNotContain(Direct, all.PublicJson);
        Assert.DoesNotContain(Routed, all.PublicJson);
    }

    [Fact]
    public async Task A_reviewer_protocol_rejection_preserves_the_draft_and_cannot_hide_a_dangerous_false_answer()
    {
        var inputs = Load();
        var state = inputs.States.First(s => inputs.ScoringCases[s.CasePosition].GetProperty("isDangerous").GetBoolean()
            && inputs.ScoringCases[s.CasePosition].GetProperty("expectedDecision").GetString() != "answer");
        var partial = new ParsedAction("controller", "answer", "submit_source_backed_answer_draft",
            JsonSerializer.SerializeToElement(new { presentation = "paragraph", claims = new[] { new { text = "DRAFT_ONLY_CANARY", evidenceIds = new[] { state.Evidence[0].Id } } } }));
        var parent = Path.Combine(Path.GetTempPath(), "saaia-a666-" + Guid.NewGuid().ToString("N"));
        var oneCase = inputs with { Schedule = new[] { new ScheduleItem(1, state.CasePosition, Direct) } };
        var result = await RunFailFast(oneCase, new HashSet<string> { Direct }, parent,
            (scheduled, current, ct) => throw new ProtocolRejectedException(
                new ProtocolRejection("reviewer/direct", "A657_native_content_fallback", "accept", 100, 20, 2, partial)));
        Assert.Contains("dangerous_false_answer", Assert.Single(result.Runs).FatalReasons);
        var packet = BuildBlindPacket(oneCase, result.Runs, "a666-before-reveal-salt");
        Assert.Contains("DRAFT_ONLY_CANARY", packet.PublicJson);
        Assert.Contains("protocol_invalid", packet.PublicJson);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(parent, recursive: true);
    }

    [Theory]
    [InlineData("A657_schema_required:query", false)]
    [InlineData("invalid_json", false)]
    [InlineData("A657_unobserved_identity:docRef", true)]
    [InlineData("transport_failure", true)]
    public async Task Ordinary_schema_rejections_are_collected_but_safety_failures_cannot_be_disguised(string code, bool fatal)
    {
        var parent = Path.Combine(Path.GetTempPath(), "saaia-a659-preflight-" + Guid.NewGuid().ToString("N"));
        var result = await RunFailFast(Load(), new HashSet<string> { Direct }, parent,
            (scheduled, state, ct) => throw new ProtocolRejectedException(
                new ProtocolRejection("controller/direct", code, "{\"wrong\":true}", 100, 10, 1)));
        Assert.Equal(fatal ? 1 : 25, result.Runs.Count);
        Assert.All(result.Runs, record => { Assert.False(record.Strict); Assert.Null(record.Outcome); Assert.NotNull(record.ProtocolFailure); });
        Assert.Equal(fatal, result.DisqualifiedFamilies.Contains(Direct));
        Assert.Equal(result.Runs.Count, File.ReadAllLines(Path.Combine(parent, "runs.jsonl")).Length);
        if (!fatal)
        {
            var packet = BuildBlindPacket(Load(), result.Runs, "a659-test-blind-salt");
            Assert.Contains("protocol_invalid", packet.PublicJson);
            Assert.DoesNotContain("execution_failed", packet.PublicJson);
            Assert.DoesNotContain(Direct, packet.PublicJson);
        }
        // Only this test's newly created directory is removed; no fixture or official run is reused.
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(parent, recursive: true);
    }

    private static FrozenInputs Load()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RAG.sln"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "artifacts", "goal-rag-product-20260827-1041", "phase5");
        return LoadFrozenInputs(Path.Combine(path, "A656-MANIFESTE-CONTRATS-TROIS-FORMATS.json"),
            Path.Combine(path, "A630-CASES-NORMALISES-ORACLES.json"), Path.Combine(path, "A656-ORDRE-MICROCAMPAGNE-25-CAS.json"));
    }

    [Theory]
    [InlineData("documents.navigation", "id")]
    [InlineData("documents.navigation", "path")]
    [InlineData("documents.navigation", "name")]
    [InlineData("documents.content_cards", "id")]
    [InlineData("documents.content_cards", "path")]
    [InlineData("documents.content_cards", "name")]
    [InlineData("documents.context", "id")]
    [InlineData("documents.context", "path")]
    [InlineData("documents.context", "name")]
    public void Exact_observed_document_references_work_on_every_declared_route(string route, string referenceKind)
    {
        var inputs = Load();
        foreach (var state in inputs.States)
        {
            var source = state.Evidence[0];
            var reference = referenceKind switch { "id" => source.DocId, "path" => source.DocPath, _ => source.DocName };
            var args = new JsonObject { ["docRef"] = reference };
            if (route == "documents.context") args["chunkId"] = source.ChunkId;
            var executor = new FrozenFixtureExecutor(inputs, state);
            var observation = executor.Execute(route, ParseJson(args.ToJsonString()));
            Assert.NotEmpty(executor.Rematerialize(observation).Items);
            Assert.Equal(route, Assert.Single(executor.Ledger).Tool);

            args["docRef"] = "UNOBSERVED-DOCUMENT";
            Assert.Throws<InvalidOperationException>(() => new FrozenFixtureExecutor(inputs, state)
                .Execute(route, ParseJson(args.ToJsonString())));
        }
    }

    [Theory]
    [InlineData("rag.search")]
    [InlineData("documents.content_cards")]
    [InlineData("documents.context")]
    public void Rematerialization_rejects_changed_source_text_even_with_valid_source_identities(string route)
    {
        var inputs = Load();
        var state = inputs.States[0];
        var executor = new FrozenFixtureExecutor(inputs, state);
        var args = route switch
        {
            "rag.search" => JsonSerializer.Serialize(new { query = state.Evidence[0].Excerpt }),
            "documents.context" => JsonSerializer.Serialize(new { chunkId = state.Evidence[0].ChunkId }),
            _ => "{}"
        };
        var observation = executor.Execute(route, ParseJson(args));
        Assert.NotEmpty(executor.Rematerialize(observation).Items);
        var raw = JsonNode.Parse(observation.Items[0].Result.GetRawText())!;
        var item = raw[route == "rag.search" ? "hits" : "items"]![0]!;
        item["excerpt"] = "A new unobserved factual claim with unchanged document identity.";
        if (route == "documents.content_cards") item["evidence"]!["sourceText"] = item["excerpt"]!.DeepClone();
        observation.Items[0].Result = ParseJson(raw.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => executor.Rematerialize(observation));
    }
}
