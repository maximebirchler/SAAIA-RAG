using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.ToolAgent.Tests;

public static partial class ThreeFormatQwenContractHarness
{
    public sealed record Execution(int Sequence, string Tool, string ArgumentsSha256, string RouteKey,
        int Offset, int? NextOffset, string ResponseSha256, IReadOnlyList<string> CitableEvidenceIds);

    public sealed class FrozenFixtureExecutor(FrozenInputs inputs, ModelState state, int maximumCalls = 4) : ISourceBackedAgentToolExecutor
    {
        private readonly List<Execution> ledger = [];
        public IReadOnlyList<Execution> Ledger => ledger.ToArray();

        public Task<ToolResults> ExecuteToolCallAsync(SourceBackedIntake intake, string toolName, JsonElement arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(toolName, arguments));
        }

        public ToolResults Execute(string toolName, JsonElement arguments)
        {
            Require(ledger.Count < maximumCalls, "executor_call_budget");
            Require(SourceBackedAgentToolCatalog.TryResolveExternalName(toolName, out var external), "executor_unknown_tool");
            var action = DocumentTools.SingleOrDefault(pair => pair.Value == external).Key;
            Require(action is not null, "executor_unexposed_tool");
            var validated = ValidateAction(inputs, state, "controller", action!, arguments);
            Require(string.Equals(validated.Tool, toolName, StringComparison.OrdinalIgnoreCase), "executor_search_route_mismatch");
            arguments = validated.Arguments;
            var argumentHash = Hash(Canonical(arguments));
            Require(!ledger.Any(e => e.Tool == validated.Tool && e.ArgumentsSha256 == argumentHash), "executor_cycle");
            var routeArguments = JsonSerializer.SerializeToElement(arguments.EnumerateObject().Where(p => p.Name is not ("offset" or "limit")).ToDictionary(p => p.Name, p => p.Value));
            var routeKey = validated.Tool + ":" + Hash(Canonical(routeArguments));
            var offset = Integer(arguments, "offset", 0);
            var limit = Integer(arguments, action == "search" ? "topK" : "limit", 8);
            Require(offset >= 0 && limit > 0, "executor_pagination_bounds");
            if (offset != 0) Require(ledger.Any(e => e.RouteKey == routeKey && e.NextOffset == offset), "executor_unobserved_offset");
            IEnumerable<Evidence> selected = state.Evidence.Where(e => !state.RejectedEvidenceIds.Contains(e.Id));
            if (arguments.TryGetProperty("pageStart", out var start)) selected = selected.Where(e => e.PageStart >= start.GetDecimal());
            if (arguments.TryGetProperty("pageEnd", out var end)) selected = selected.Where(e => e.PageEnd <= end.GetDecimal());
            if (arguments.TryGetProperty("pageStart", out start) && arguments.TryGetProperty("pageEnd", out end)) Require(start.GetDecimal() <= end.GetDecimal(), "executor_page_range");

            if (action == "context")
            {
                Require(arguments.TryGetProperty("chunkId", out var chunk) || arguments.TryGetProperty("pageStart", out _), "executor_context_anchor_missing");
                if (chunk.ValueKind == JsonValueKind.String)
                {
                    var anchor = state.Evidence.Single(e => e.ChunkId == chunk.GetString());
                    Require(selected.Contains(anchor), "executor_conflicting_anchor");
                    selected = [anchor];
                }
                var anchors = selected.ToArray();
                var before = Integer(arguments, "before", 0); var after = Integer(arguments, "after", 0);
                Require(before >= 0 && after >= 0 && anchors.Length > 0, "executor_context_window");
                var low = (long)anchors.Min(e => e.PageStart) - before;
                var high = (long)anchors.Max(e => e.PageEnd) + after;
                selected = state.Evidence.Where(e => e.PageStart >= low && e.PageEnd <= high && !state.RejectedEvidenceIds.Contains(e.Id));
            }
            else if (action == "search")
            {
                var queries = arguments.TryGetProperty("queries", out var array)
                    ? array.EnumerateArray().Select(v => v.GetString()!).ToArray()
                    : new[] { Text(arguments, "query") };
                Require(queries.All(q => !string.IsNullOrWhiteSpace(q)), "executor_empty_query");
                // Deterministic lexical ranking of frozen excerpts, not an embedding/search performance claim.
                var tokens = queries.SelectMany(q => Regex.Matches(q, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant).Select(m => m.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                selected = selected.OrderByDescending(e => tokens.Count(t => e.Excerpt.Contains(t, StringComparison.OrdinalIgnoreCase))).ThenBy(e => e.PageStart);
                if (arguments.TryGetProperty("maxPerDoc", out var perDoc)) { Require(perDoc.GetDecimal() > 0, "executor_max_per_doc"); selected = selected.Take(checked((int)perDoc.GetDecimal())); }
                if (arguments.TryGetProperty("maxPerPage", out var perPage)) Require(perPage.GetDecimal() > 0, "executor_max_per_page");
            }
            else
            {
                if (arguments.TryGetProperty("q", out var q) && !string.IsNullOrWhiteSpace(q.GetString())) selected = selected.Where(e => e.Excerpt.Contains(q.GetString()!, StringComparison.OrdinalIgnoreCase));
                if (action == "navigation" && arguments.TryGetProperty("kind", out var kind) && kind.GetString() != "title_anchor") selected = [];
                if (action == "content_cards" && arguments.TryGetProperty("inventoryMode", out var mode) && mode.GetString() == "representative")
                {
                    var all = selected.ToArray();
                    if (all.Length > 0) selected = new[] { 0, all.Length / 2, all.Length - 1 }.Concat(Enumerable.Range(0, all.Length)).Distinct().Select(i => all[i]);
                }
            }
            var candidates = selected.ToArray();
            var page = candidates.Skip(offset).Take(limit).ToArray();
            int? next = (long)offset + page.Length < candidates.Length ? offset + page.Length : null;
            var rawItems = page.Select(e => Item(e, action!)).ToArray();
            var response = new Dictionary<string, object?>
            {
                [action == "search" ? "hits" : "items"] = rawItems,
                ["offset"] = offset,
                ["nextOffset"] = next,
                ["total"] = candidates.Length,
                ["fixtureOrigin"] = "frozen_excerpt_order",
                ["citable"] = action != "navigation"
            };
            if (action == "search" && arguments.TryGetProperty("queries", out var queryArray)) response["queries"] = queryArray;
            if (action == "context" && arguments.TryGetProperty("chunkId", out var anchorChunk)) response["requestedAnchorChunkId"] = anchorChunk.GetString();
            var result = JsonSerializer.SerializeToElement(response, JsonOptions);
            ledger.Add(new(ledger.Count + 1, validated.Tool, argumentHash, routeKey, offset, next,
                Hash(Serialize(result)), action == "navigation" ? [] : page.Select(e => e.Id).ToArray()));
            var tools = new ToolResults();
            tools.Items.Add(new() { ToolName = validated.Tool, Result = result, DurationMs = 0 });
            return tools;
        }

        // The product builder is exercised, then fixture IDs are joined by immutable source identity.
        // No source identity is repaired from an oracle or a model's answer.
        public EvidenceBundle Rematerialize(ToolResults observation)
        {
            // The fixture transport is immutable: a matching PDF identity alone
            // cannot legitimize text replaced after execution.
            Require(observation.Items.Count > 0, "executor_empty_observation");
            foreach (var result in observation.Items)
                Require(ledger.Any(entry => entry.Tool == result.ToolName
                    && entry.ResponseSha256 == Hash(Serialize(result.Result))), "executor_observation_changed");
            var bundle = EvidenceBundleBuilder.FromToolResults(observation, Text(ParseJson(state.Json), "question"), "a657-offline");
            var canonical = bundle.Items.Select(item =>
            {
                var identity = state.Evidence.SingleOrDefault(e => e.DocId == item.DocId && e.RevisionId == item.RevisionId
                    && e.SourceHash == item.SourceHash && e.DocPath == item.DocPath && e.PageStart == item.PageStart && e.PageEnd == item.PageEnd
                    && (item.ChunkId is null || e.ChunkId == item.ChunkId));
                Require(identity is not null, "executor_observation_identity");
                var navigation = item.SourceKind == "navigation_map";
                Require(!state.RejectedEvidenceIds.Contains(identity!.Id), "executor_rejected_observation");
                return item with { EvidenceId = navigation ? "N" + identity.PageStart : identity.Id };
            }).ToArray();
            return bundle with { Items = canonical };
        }

        public ModelState UpdatedState()
        {
            var json = JsonNode.Parse(state.Json)!.AsObject();
            json["actions"] = JsonSerializer.SerializeToNode(ledger, JsonOptions);
            json["executedRoutes"] = JsonSerializer.SerializeToNode(ledger.Select(e => new { e.RouteKey, e.Offset, e.NextOffset }), JsonOptions);
            json["budgets"]!["toolCallsRemaining"] = Math.Max(0, maximumCalls - ledger.Count);
            var serialized = json.ToJsonString();
            return state with { Json = serialized, Sha256 = Hash(serialized) };
        }
        private static object Item(Evidence e, string action)
        {
            var item = new Dictionary<string, object?>
            {
                ["docId"] = e.DocId,
                ["revisionId"] = e.RevisionId,
                ["sourceHash"] = e.SourceHash,
                ["docName"] = e.DocName,
                ["docPath"] = e.DocPath,
                ["pageStart"] = e.PageStart,
                ["pageEnd"] = e.PageEnd,
                ["chunkId"] = e.ChunkId,
                ["locator"] = e.Locator,
                ["citable"] = action != "navigation"
            };
            if (action == "navigation")
            {
                item["label"] = e.Excerpt.Split('\n')[0]; item["kind"] = "title_anchor";
                item["targetPageStart"] = e.PageStart; item["targetPageEnd"] = e.PageEnd;
                item["targetChunkId"] = e.ChunkId; item["targetAnchorId"] = "anchor:" + e.ChunkId;
            }
            else
            {
                item["evidenceId"] = e.Id; item["excerpt"] = e.Excerpt; item["score"] = 1;
                if (action == "content_cards")
                {
                    item["contentCardId"] = "card:" + e.ChunkId; item["title"] = e.Excerpt.Split('\n')[0];
                    item["kind"] = "section"; item["evidence"] = new { sourceText = e.Excerpt };
                }
            }
            return item;
        }
    }
    private static int Integer(JsonElement value, string name, int fallback) => value.TryGetProperty(name, out var p) ? checked((int)p.GetDecimal()) : fallback;
    private static string Canonical(JsonElement value)
    {
        object? ProjectCanonical(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => ProjectCanonical(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ProjectCanonical).ToArray(),
            JsonValueKind.Number => element.GetDecimal(),
            _ => element.Clone()
        };
        return Serialize(ProjectCanonical(value)!);
    }
}
