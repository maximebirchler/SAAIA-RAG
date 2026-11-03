using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public void Cross_batch_explicit_reads_keep_the_current_primary_round_then_alternate_old_and_secondary()
    {
        var current = BalancedExplicitBodies(false);
        var old = AccumulatedReadBodies("A1", "A2", "A3");
        var active = old.Concat(current.SelectMany(group => group)).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        AccumulatedRemember(context, "read_source", old);
        RememberBalancedExplicitBatch(context, current);

        Assert.Equal(current.Select(group => group[0].Reference.EvidenceId), AccumulatedReadIds(context).Take(6));
        Assert.Equal(new[] { "A1", "Q1H2", "A2", "Q2H2", "A3", "Q3H2" }, AccumulatedReadIds(context).Skip(6).Take(6));
        Assert.Equal(active.Length, AccumulatedReadIds(context).Count);
        Assert.Equal(active.Length, AccumulatedReadIds(context).Distinct(StringComparer.Ordinal).Count());
        var beforeEmpty = AccumulatedReadIds(context).ToArray();
        AccumulatedRemember(context, "find_source_text", []);
        Assert.Equal(beforeEmpty, AccumulatedReadIds(context));
    }

    [Fact]
    public void Cross_batch_explicit_reads_deduplicate_overlap_filter_retired_ids_and_rebind_current_bodies()
    {
        var bodies = AccumulatedReadBodies("A1", "A2", "B1", "B2", "B3");
        var gateway = new AccumulatedReadGateway(bodies);
        var context = AccumulatedReadContext(gateway);
        AccumulatedRemember(context, "read_source", bodies[..2]);
        RememberBalancedExplicitBatch(context, [[bodies[2], bodies[3]], [bodies[2], bodies[4]]]);
        Assert.Equal(new[] { "B1", "B3", "A1", "B2", "A2" }, AccumulatedReadIds(context));
        var currentA2 = bodies[1] with { Content = "Current revalidated A2 body; the old object must never reappear." };
        gateway.Active = [currentA2, bodies[2], bodies[3], bodies[4]];
        AccumulatedRemember(context, "find_source_text", []);
        Assert.DoesNotContain("A1", AccumulatedReadIds(context));
        var focused = AccumulatedReadPriority(context, gateway.Active, bodies);
        Assert.Same(currentA2, Assert.Single(focused, item => item.Reference.EvidenceId == "A2"));
        Assert.DoesNotContain(focused, item => item.Reference.EvidenceId == "A1");
    }

    [Fact]
    public void Cross_batch_targeted_read_stays_first_without_restoring_expired_content()
    {
        var groups = BalancedExplicitBodies(false);
        var old = AccumulatedReadBodies("A1", "A2");
        var gateway = new AccumulatedReadGateway(old.Concat(groups.SelectMany(group => group)).ToArray());
        var context = AccumulatedReadContext(gateway);
        AccumulatedRemember(context, "read_source", old);
        RememberBalancedExplicitBatch(context, groups);
        var windowType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("EvidenceReadWindow", BindingFlags.NonPublic)!;
        var window = Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [0, 512, null], null)!;
        var updates = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), windowType))!;
        updates.Add("A2", window);
        Assert.Equal(1, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", updates, context)!);
        Assert.Equal("A2", AccumulatedReadIds(context)[0]);
        Assert.Equal(0, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", updates, context)!);
        gateway.Active = gateway.Active.Where(item => item.Reference.EvidenceId != "A2").ToArray();
        AccumulatedRemember(context, "find_source_text", []);
        Assert.DoesNotContain(AccumulatedReadPriority(context, gateway.Active, old), item => item.Reference.EvidenceId == "A2");
    }

    [Fact]
    public void Cross_batch_ordering_does_not_claim_an_oversized_retained_body_will_fit()
    {
        var groups = BalancedExplicitBodies(false);
        var huge = AccumulatedReadBody("A-HUGE", new string('x', 500_000));
        var active = new[] { huge }.Concat(groups.SelectMany(group => group)).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        AccumulatedRemember(context, "read_source", [huge]);
        RememberBalancedExplicitBatch(context, groups);
        var focused = AccumulatedReadPriority(context, active, active);
        Assert.Equal(groups.Select(group => group[0].Reference.EvidenceId), focused.Take(6).Select(item => item.Reference.EvidenceId));
        Assert.Same(huge, focused[6]);
        Assert.Equal("Q1H2", focused[7].Reference.EvidenceId);
        Assert.Equal(500_000, focused[6].Content.Length);
        // Ordering does not cache, shorten or admit this body. The unchanged
        // shared-budget projector still decides whether any window fits. An
        // oversized metadata-only floor can still stop that greedy projector;
        // this patch deliberately does not change that separate boundary.
    }

    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Cross_batch_explicit_bodies_coexist_on_actual_wire_after_the_current_six_primary_results(
        string protocol, bool escaped)
    {
        const int cap = 65_536;
        var special = escaped ? " <tag q=\"v\"> & \\path\\ 漢字 🧪 e\u0301" : string.Empty;
        var fileName = "canonical-" + new string('p', 80) + special + ".pdf";
        var quotedPadding = string.Concat(Enumerable.Repeat("\"path\\x\" ", 160));
        AdvancedAnalysisResolvedEvidence Body(string id, string text, string? title = null)
            => TextQualityFixture(BuildEvidence(id, text, fileName,
                "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222",
                exactTitle: title), "ok", false, ["source_conditions_retained"]);
        var old = Enumerable.Range(1, 8).Select(index => Body("A" + index,
            "Earlier documented alternative A" + index + ": retain its mandatory source condition. " + special + new string('a', 300))).ToArray();
        var groups = Enumerable.Range(1, 6).Select(query => Enumerable.Range(1, 3).Select(hit =>
        {
            var id = $"B{query}H{hit}";
            return query == 1 && hit == 1
                ? Body(id, "Table of contents\nChoice Beta ........ 15\nChoice Gamma ........ 25\nChoice Delta ........ 35\n", "Table of contents")
                : Body(id, $"Documented choice {id}: isolate the assembly before applying this value. " + special + quotedPadding, "Documented choice " + id);
        }).ToArray()).ToArray();
        var current = groups.SelectMany(group => group).ToArray();
        var noise = Enumerable.Range(0, 128 - old.Length - current.Length).Select(index => Body("N" + index,
            "Unrelated active canonical context " + index + new string('n', 1_000))).ToArray();
        var active = old.Concat(current).Concat(noise).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        AccumulatedRemember(context, "read_source", old);
        var requests = groups.Select((group, index) => new AdvancedAnalysisSearchRequest("Current literal " + index,
            TopK: 3, DocId: group[0].Reference.DocId, RevisionId: group[0].Reference.RevisionId,
            DocPath: group[0].Reference.DocPath, Operation: "find_source_text", Offset: 0)).ToArray();
        var results = requests.Select((request, index) => new
        {
            Key = (string)InvokeSourceCatalogue(null, "BuildSearchIdentity", request)!,
            Value = new AdvancedAnalysisSearchObservation(request.Query, groups[index], [], 1, 3)
        }).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        InvokeSourceCatalogue(null, "RememberLatestExplicitRead", context, requests, results);
        var calls = requests.Select((request, index) => new
        {
            id = "current-" + index, type = "function", function = new { name = "find_source_text",
                arguments = JsonSerializer.Serialize(new { sourceKey = "internal-source-1", query = request.Query, offset = 0, topK = 3 }) }
        }).ToArray();
        var responseItems = protocol == "responses" ? JsonSerializer.Serialize(calls.Select(call => new
        {
            type = "function_call", id = "fc_" + call.id, call_id = call.id, name = call.function.name,
            arguments = call.function.arguments, status = "completed"
        })) : null;
        var turnType = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("NativeResearchTurn", BindingFlags.NonPublic)!;
        var turn = turnType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(ctor => ctor.GetParameters().Length == 8)
            .Invoke([JsonSerializer.Serialize(calls), requests, results, null, responseItems, false, false, false]);
        BindAdmittedNativeFixtureCalls(turn, calls.Select((call, index) => (call.id, requests[index])).ToArray());

        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = 1_000_000;
        options.NativeObservationReadEnabled = false; options.VisualSourceCitationsEnabled = false;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        // Source-catalogue context is separate from the oracle's body records.
        // Never derive its candidates from an already budget-projected native
        // batch: native history has an independent unchanged 65k guard.
        var fullProjection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(), active,
            new Dictionary<string, HashSet<string>>(), active, new Dictionary<string, string>(), null, null)!;
        var fullItems = old.Concat(current).ToDictionary(item => item.Reference.EvidenceId!, item =>
        {
            // Format exactly one raw canonical record at a time, with no tool
            // turn and no prior selection. Every oracle body must remain whole.
            var formatted = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(),
                new[] { item }, new Dictionary<string, HashSet<string>>(), new[] { item }, new Dictionary<string, string>(), null, null)!;
            var record = Assert.Single(((IEnumerable)formatted).Cast<object>());
            Assert.Equal(item.Content, record.GetType().GetProperty("Content")!.GetValue(record));
            Assert.Equal(false, record.GetType().GetProperty("ContentTruncated")!.GetValue(record));
            return record;
        }, StringComparer.Ordinal);
        // The first physical result B1H1 is an index. Find projection now leads
        // with the substantive hit; spell out the independent expected round.
        var primaryIds = new[] { "B1H2", "B2H1", "B3H1", "B4H1", "B5H1", "B6H1" };
        var neededIds = primaryIds.Concat(new[] { "A1", "B1H3" }).ToArray();
        var neededWire = CrossBatchOraclePayload(provider, turn, fullProjection, fullItems, neededIds, protocol);
        var allCurrentWire = CrossBatchOraclePayload(provider, turn, fullProjection, fullItems,
            current.Select(item => item.Reference.EvidenceId!).ToArray(), protocol);
        // Keep legacy inner documentary JSON, but measure the actual compact
        // outer HTTP representation in both independent full/control oracles.
        var neededCharacters = CrossBatchDocumentaryDifference(neededWire, compact: true);
        var allCurrentCharacters = CrossBatchDocumentaryDifference(allCurrentWire, compact: true);
        Assert.InRange(neededCharacters, 1, cap);
        Assert.True(allCurrentCharacters > cap,
            $"All current hits must saturate the compact cap: measured={allCurrentCharacters}, cap={cap}.");

        options.MaximumEvidencePromptCharacters = cap;
        var focused = AccumulatedReadPriority(context, active, active);
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", BuildDirectRequest(), active,
            new Dictionary<string, HashSet<string>>(), focused, new Dictionary<string, string>(), null, turn)!;
        var history = InvokeSourceCatalogue(provider, "BuildNativeToolTurnMessages", turn, projection, protocol == "responses")!;
        var prompt = CrossBatchUserPrompt(provider, projection);
        await Assert.IsAssignableFrom<Task>(InvokeSourceCatalogue(provider, "CompleteJsonAsync", Guid.NewGuid(), "writer",
            "Return JSON.", prompt, 128, CancellationToken.None, true, history, projection));
        var sent = Assert.Single(factory.Requests).Body;
        var visible = WorkspaceUser(sent).GetProperty("evidence").EnumerateArray().ToArray();
        // These assertions, including the actual HTTP boundary, compile on the
        // old implementation and fail there because all A IDs follow all B hits.
        foreach (var id in neededIds)
        {
            var item = Assert.Single(visible, item => item.GetProperty("evidenceId").GetString() == id);
            var canonical = Assert.Single(active, item => item.Reference.EvidenceId == id);
            Assert.Equal(canonical.Content, item.GetProperty("content").GetString());
            Assert.False(item.GetProperty("contentTruncated").GetBoolean());
        }
        Assert.Equal(primaryIds, focused.Take(6).Select(item => item.Reference.EvidenceId));
        Assert.Equal(new[] { "A1", "B1H3" }, focused.Skip(6).Take(2).Select(item => item.Reference.EvidenceId));
        // The index remains a canonical result in its original physical order;
        // it is a locator, not one of the six required substantive primaries.
        Assert.Equal("navigation", JsonSerializer.SerializeToElement(fullItems["B1H1"], CrossBatchInnerJson)
            .GetProperty("contentRole").GetString());
        for (var index = 0; index < requests.Length; index++)
        {
            var key = (string)InvokeSourceCatalogue(null, "BuildSearchIdentity", requests[index])!;
            Assert.Equal(groups[index].Select(item => item.Reference.EvidenceId),
                results[key].Evidence.Select(item => item.Reference.EvidenceId));
            for (var hit = 0; hit < groups[index].Length; hit++)
                Assert.Same(groups[index][hit], results[key].Evidence[hit]);
        }
        Assert.Contains("isolate the assembly", Assert.Single(visible, item => item.GetProperty("evidenceId").GetString() == "B1H3")
            .GetProperty("content").GetString());
        var actual = CrossBatchDocumentaryDifference(sent, compact: true);
        Assert.InRange(actual, 1, cap);
        Assert.Equal(actual, OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(sent));
        Assert.True(visible.Length < active.Length);
        var outputs = NativeCorrelationOutputs(sent, protocol);
        Assert.Equal(6, outputs.Length);
        Assert.All(outputs, output =>
        {
            var ids = output.Body.GetProperty("visibleEvidenceIds").EnumerateArray().ToArray();
            Assert.NotEmpty(ids);
            Assert.All(ids, id => Assert.StartsWith("B", id.GetString()!));
        });
        Assert.DoesNotContain("Earlier documented alternative", JsonSerializer.Serialize(outputs.Select(output => output.Body)));
    }

    private static readonly JsonSerializerOptions CrossBatchInnerJson = new(JsonSerializerDefaults.Web)
    { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    private static string CrossBatchUserPrompt(OpenAiCompatibleAdvancedAnalysisProvider provider, object projection)
    {
        var node = JsonNode.Parse(SourceCataloguePrompt(provider, projection))!.AsObject();
        node["researchTools"] = JsonSerializer.SerializeToNode(new
        {
            researchAllowed = true, evidenceReadingAllowed = true, maximumQueries = 8, availableCategories = Array.Empty<string>(),
            priorSearches = new[] { new { query = "Synthetic operational history", diagnostics = new string('r', 12_000) + " \"quoted\" 漢字 🧪" } }
        }, CrossBatchInnerJson);
        return (string)InvokeSourceCatalogue(null, "AddSourceCatalogueToPrompt", node.ToJsonString(CrossBatchInnerJson), projection)!;
    }

    private static string CrossBatchOraclePayload(OpenAiCompatibleAdvancedAnalysisProvider provider, object turn, object fullProjection,
        IReadOnlyDictionary<string, object> items, IReadOnlyList<string> ids, string protocol)
    {
        var element = items.Values.First().GetType();
        var subset = Array.CreateInstance(element, ids.Count);
        for (var index = 0; index < ids.Count; index++) subset.SetValue(items[ids[index]], index);
        var user = JsonNode.Parse(CrossBatchUserPrompt(provider, fullProjection))!.AsObject();
        user["evidence"] = JsonSerializer.SerializeToNode(subset, CrossBatchInnerJson);
        var history = (IReadOnlyList<object>)InvokeSourceCatalogue(provider, "BuildNativeToolTurnMessages", turn, subset, protocol == "responses")!;
        var messages = new List<object> { new { role = "system", content = "Return JSON." } };
        messages.AddRange(history); messages.Add(new { role = "user", content = user.ToJsonString(CrossBatchInnerJson) });
        return JsonSerializer.Serialize(new Dictionary<string, object> { [protocol == "responses" ? "input" : "messages"] = messages }, CrossBatchInnerJson);
    }

    private static int CrossBatchDocumentaryDifference(string payload, bool compact)
    {
        var outer = new JsonSerializerOptions(CrossBatchInnerJson)
        { Encoder = compact ? JavaScriptEncoder.UnsafeRelaxedJsonEscaping : JavaScriptEncoder.Create(UnicodeRanges.All) };
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var field = root.TryGetProperty("input", out _) ? "input" : "messages";
        var controlRows = new List<object>();
        foreach (var row in root.GetProperty(field).EnumerateArray())
        {
            var role = row.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : null;
            var type = row.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            if (role == "tool" || type is "function_call" or "function_call_output" || row.TryGetProperty("tool_calls", out _)) continue;
            var values = row.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
            if (role == "user")
            {
                using var user = JsonDocument.Parse(row.GetProperty("content").GetString()!);
                var controlUser = user.RootElement.EnumerateObject().Where(property => property.Name is not
                    ("evidence" or "observations" or "structuredTables" or "alternativeTableReadings" or "visualSources"
                     or "visualSourceCapabilities" or "alternativeNativeReadings" or "nativeObservationCapabilities" or "sourceCatalogue"))
                    .ToDictionary(property => property.Name, property => property.Value.Clone());
                values["content"] = JsonSerializer.Serialize(controlUser, CrossBatchInnerJson);
            }
            controlRows.Add(values);
        }
        var control = root.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        control[field] = controlRows;
        return JsonSerializer.Serialize(root, outer).Length - JsonSerializer.Serialize(control, outer).Length;
    }
}
