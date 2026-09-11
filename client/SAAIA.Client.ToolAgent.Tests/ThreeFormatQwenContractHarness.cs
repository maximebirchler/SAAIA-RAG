using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.ToolAgent.Tests;

// Offline A657 infrastructure only. No model client, process, or network transport.
public static partial class ThreeFormatQwenContractHarness
{
    public const string Direct = "DIRECT_NATIVE_PALETTE";
    public const string Routed = "ROUTE_THEN_SPECIALIZED_PAYLOAD";
    public const string Grammar = "GRAMMAR_CONSTRAINED_JSON";
    public const string ContractHash = "3190AA1C431EABF052FF7398280EE88F1EDC3DD90D6FD7332F767BBE348F667D";
    public const string CasesHash = "D1CCCA29AC3704B3058D934202379BADBAFEA80CB7865D084520E45CBDC3B7F6";
    public const string OrderHash = "577C007590AED08E40E348565A8D91642D887C1A4BD018B90835EF2D0F904FF0";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] Families = [Direct, Routed, Grammar];
    private static readonly Dictionary<string, (string Tool, string Schema)> Custom = new(StringComparer.Ordinal)
    {
        ["answer"] = ("submit_source_backed_answer_draft", "answerDraft"),
        ["clarify"] = ("request_source_backed_clarification", "clarification"),
        ["insufficient"] = ("declare_source_backed_insufficiency", "insufficiency"),
        ["accept"] = ("accept_source_backed_answer", "accept"),
        ["block"] = ("block_source_backed_answer", "block")
    };
    private static readonly Dictionary<string, string> DocumentTools = new(StringComparer.Ordinal)
    {
        ["search"] = "rag_search", ["navigation"] = "documents_navigation",
        ["content_cards"] = "documents_content_cards", ["context"] = "documents_context"
    };

    public sealed record Evidence(string Id, string DocId, string RevisionId, string SourceHash,
        string DocName, string DocPath, int PageStart, int PageEnd, string ChunkId,
        string Locator, string Kind, string Excerpt);
    public sealed record ModelState(int CasePosition, string CaseId, string Json, string Sha256,
        IReadOnlyList<Evidence> Evidence, IReadOnlySet<string> RejectedEvidenceIds);
    public sealed record ScheduleItem(int Sequence, int CasePosition, string Variant);
    public sealed record FrozenInputs(JsonElement Contracts, IReadOnlyList<ModelState> States,
        IReadOnlyDictionary<int, JsonElement> ScoringCases, IReadOnlyList<ScheduleItem> Schedule);
    public sealed record Request(string Variant, string Role, string Stage, string? SelectedAction,
        string SnapshotJson, string SnapshotSha256, JsonElement Body, string SchemaSha256);
    public sealed record ParsedAction(string Role, string Action, string Tool, JsonElement Arguments);

    public static FrozenInputs LoadFrozenInputs(string contractsPath, string casesPath, string orderPath)
    {
        var contracts = ReadFrozen(contractsPath, ContractHash);
        var cases = ReadFrozen(casesPath, CasesHash);
        var order = ReadFrozen(orderPath, OrderHash);
        var catalog = SourceBackedAgentToolCatalog.Build(useConstrainedContextDescriptions: true);
        foreach (var tool in contracts.GetProperty("authoritativeDocumentTools").EnumerateArray())
        {
            var actual = catalog.Single(t => t.Name == Text(tool, "name"));
            Require(actual.Description == Text(tool, "description")
                && SameJson(actual.Parameters, tool.GetProperty("parameters")), "catalog_drift");
        }
        var originals = cases.GetProperty("cases").EnumerateArray().OrderBy(c => c.GetProperty("casePosition").GetInt32()).ToArray();
        Require(originals.Length == 25 && originals.Select(c => c.GetProperty("casePosition").GetInt32()).SequenceEqual(Enumerable.Range(1, 25)), "case_positions");
        var states = originals.Select(BuildState).ToArray();
        var schedule = new List<ScheduleItem>();
        foreach (var row in order.GetProperty("schedule").EnumerateArray())
        {
            var position = row.GetProperty("casePosition").GetInt32();
            var rotation = row.GetProperty("variantOrder").EnumerateArray().Select(v => v.GetString()!).ToArray();
            Require(rotation.SequenceEqual(Enumerable.Range(0, 3).Select(n => Families[(position - 1 + n) % 3])), "schedule_rotation");
            foreach (var family in rotation) schedule.Add(new(schedule.Count + 1, position, family));
        }
        Require(schedule.Count == 75 && schedule.Select(s => s.CasePosition).Distinct().SequenceEqual(Enumerable.Range(1, 25)), "schedule_count");
        return new(contracts, states, originals.ToDictionary(c => c.GetProperty("casePosition").GetInt32(), c => c.Clone()), schedule);
    }

    private static ModelState BuildState(JsonElement fixture)
    {
        var docId = Text(fixture, "docId");
        var revision = Text(fixture, "revisionId");
        var file = Text(fixture, "docPath");
        var name = Text(fixture, "docName");
        var evidence = fixture.GetProperty("excerpts").EnumerateArray().Select((v, n) =>
            new Evidence($"E{n + 1}", docId, revision, Hash(docId + "|" + revision), name, file,
                n + 1, n + 1, $"fixture:{docId}:page:{n + 1}", name + "#page=" + (n + 1), "chunk", v.GetString()!)).ToArray();
        // This is a diagnostic fixture, not a claim about real PDF hashes/pages.
        var json = Serialize(new
        {
            version = "a657_fixture_v1", identityOrigin = "frozen_fixture_excerpt_order_not_verified_real_pdf",
            question = Text(fixture, "question"), language = Text(fixture, "language"), focus = Text(fixture, "questionFocus"),
            provisionalSemanticPlan = new { authority = "non_authoritative", text = Text(fixture, "semanticPlan") },
            document = new { docId, revisionId = revision, docName = name, docPath = file }, evidence,
            rejectedEvidence = Array.Empty<object>(), actions = Array.Empty<object>(), executedRoutes = Array.Empty<object>(),
            budgets = new { contextTokens = 4096, controllerInput = 3392, controllerOutput = 512, reviewerInput = 3648,
                reviewerOutput = 256, reserve = 64, headroom = 128, modelCallsRemaining = 4, toolCallsRemaining = 4, millisecondsRemaining = 180000 }
        });
        return new(fixture.GetProperty("casePosition").GetInt32(), Text(fixture, "id"), json, Hash(json), evidence, new HashSet<string>(StringComparer.Ordinal));
    }

    public static Request BuildDirectRequest(FrozenInputs inputs, ModelState state, string role = "controller", string? draftJson = null, string? sourceContractJson = null)
        => Build(inputs, state, Direct, role, "direct", null, draftJson, sourceContractJson);
    public static Request BuildRouteRequest(FrozenInputs inputs, ModelState state, string role = "controller", string? draftJson = null, string? sourceContractJson = null)
        => Build(inputs, state, Routed, role, "route", null, draftJson, sourceContractJson);
    public static Request BuildSpecializedPayloadRequest(FrozenInputs inputs, ModelState state, string action, string role = "controller", string? draftJson = null, string? sourceContractJson = null)
        => Build(inputs, state, Routed, role, "payload", action, draftJson, sourceContractJson);
    public static Request BuildGrammarRequest(FrozenInputs inputs, ModelState state, string role = "controller", string? draftJson = null, string? sourceContractJson = null)
        => Build(inputs, state, Grammar, role, "grammar", null, draftJson, sourceContractJson);

    private static Request Build(FrozenInputs inputs, ModelState state, string variant, string role, string stage, string? action, string? draft, string? verifier)
    {
        Require(role is "controller" or "reviewer", "role");
        ValidateState(state);
        if (role == "reviewer")
        {
            Require(draft is not null && verifier is not null, "reviewer_inputs_missing");
            ValidateAction(inputs, state, "controller", "answer", ParseJson(draft!));
            var verification = ParseJson(verifier!);
            Require(verification.ValueKind == JsonValueKind.Object
                && verification.TryGetProperty("valid", out var valid) && valid.ValueKind is JsonValueKind.True or JsonValueKind.False, "verifier_result");
        }
        else Require(draft is null && verifier is null, "controller_reviewer_inputs");
        var content = "SNAPSHOT\n" + state.Json;
        if (draft is not null) content += "\nFROZEN_DRAFT\n" + draft + "\nSOURCE_CONTRACT\n" + verifier;
        if (action is not null)
        {
            Require(Actions(inputs, role).Contains(action), "selected_action");
            content += "\nSELECTED_ACTION\n" + action;
        }
        var instruction = role == "controller"
            ? "Choose the next action using only the observed evidence. The provisional plan is not authoritative. Documentary facts require observed evidence IDs. Do not invent identities."
            : "Review the frozen draft against the same evidence and Source Contract. Only accept allows publication. Do not rewrite the answer. Do not invent identities.";
        instruction += stage switch
        {
            "route" => " Call only select_source_backed_action with the action enum, without any payload.",
            "payload" => " Call the sole specialized tool for SELECTED_ACTION exactly once.",
            "grammar" => " Return content matching the strict JSON schema, without tool calls. Use empty strings, arrays and objects for inactive fields; usefulEvidenceIds holds clarification evidence IDs.",
            _ => " Call exactly one of the supplied tools. Do not emit answer text outside the tool."
        };
        var body = new JsonObject
        {
            ["messages"] = JsonSerializer.SerializeToNode(new[] { new { role = "system", content = instruction }, new { role = "user", content } }),
            ["temperature"] = 0, ["max_tokens"] = role == "controller" ? 512 : 256, ["stream"] = false
        };
        string schemaJson;
        if (stage == "grammar")
        {
            var schema = inputs.Contracts.GetProperty("grammarSchemas").GetProperty(role);
            schemaJson = Serialize(schema);
            body["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject
                { ["name"] = "a656_" + role, ["strict"] = true, ["schema"] = JsonNode.Parse(schemaJson) } };
        }
        else
        {
            var tools = stage == "route"
                ? new[] { Tool("select_source_backed_action", "Select only the next action.", inputs.Contracts.GetProperty("customSchemas").GetProperty(role + "Route")) }
                : (stage == "payload" ? new[] { action! } : Actions(inputs, role)).Select(a => ToolFor(inputs, a)).ToArray();
            schemaJson = Serialize(tools);
            body["tools"] = JsonNode.Parse(schemaJson);
            body["tool_choice"] = "required";
            body["parallel_tool_calls"] = false;
        }
        return new(variant, role, stage, action, state.Json, state.Sha256, ParseJson(body.ToJsonString()), Hash(schemaJson));
    }

    private static string[] Actions(FrozenInputs inputs, string role) => inputs.Contracts.GetProperty("customSchemas").GetProperty(role + "Route")
        .GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
    private static object ToolFor(FrozenInputs inputs, string action)
    {
        if (DocumentTools.TryGetValue(action, out var name))
        {
            var tool = inputs.Contracts.GetProperty("authoritativeDocumentTools").EnumerateArray().Single(t => Text(t, "name") == name);
            return Tool(name, Text(tool, "description"), tool.GetProperty("parameters"));
        }
        var custom = Custom[action];
        return Tool(custom.Tool, action, inputs.Contracts.GetProperty("customSchemas").GetProperty(custom.Schema));
    }
    private static object Tool(string name, string description, JsonElement schema) => new { type = "function", function = new { name, description, parameters = schema } };
    private static JsonElement ReadFrozen(string path, string hash)
    {
        var bytes = File.ReadAllBytes(path);
        Require(Convert.ToHexString(SHA256.HashData(bytes)) == hash, "frozen_input_drift:" + Path.GetFileName(path));
        return ParseJson(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidOperationException("A657_missing_string:" + name);
    private static bool SameJson(JsonElement a, JsonElement b) => JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()), JsonNode.Parse(b.GetRawText()));
    private static void ValidateState(ModelState state)
    {
        Require(Hash(state.Json) == state.Sha256, "snapshot_hash");
        var observed = ParseJson(state.Json).GetProperty("evidence");
        Require(SameJson(observed, JsonSerializer.SerializeToElement(state.Evidence, JsonOptions)), "snapshot_evidence_drift");
        Require(state.Evidence.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() == state.Evidence.Count, "snapshot_duplicate_evidence");
    }
    private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException("A657_" + error); }
}
