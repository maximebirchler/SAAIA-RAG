using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_duplicate_call_keeps_its_own_result_and_does_not_shift_later_call_ids(string protocol)
    {
        var gateway = new NativeCorrelationGateway("Alpha", "Beta", "Gamma");
        var calls = new[] { "Alpha", "Beta", "Beta", "Gamma" }
            .Select(query => ("find_source_text", NativeCorrelationFind("internal-source-1", query))).ToArray();
        using var factory = NativeCorrelationFactory(protocol, calls);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, gateway.Searches.Skip(1).Select(request => request.Query));
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
        Assert.Equal(new[] { "call_0", "call_1", "call_2", "call_3" }, outputs.Select(row => row.CallId));
        Assert.Equal(new[] { "E-Alpha", "E-Beta", "E-Beta", "E-Gamma" }, outputs.Select(NativeCorrelationVisibleId));
        Assert.All(outputs, row => Assert.Equal("executed", row.Body.GetProperty("status").GetString()));
        Assert.False(outputs[1].Body.TryGetProperty("resultReusedWithinBatch", out var firstReplay) && firstReplay.GetBoolean());
        Assert.True(outputs[2].Body.GetProperty("resultReusedWithinBatch").GetBoolean());
        Assert.Equal(3, factory.Requests.Count);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_eight_calls_with_middle_duplicate_preserve_executed_empty_last_result(string protocol)
    {
        var gateway = new NativeCorrelationGateway("Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta");
        var queries = new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Gamma", "Eta" };
        var calls = queries.Select(query => ("find_source_text", NativeCorrelationFind("internal-source-1", query))).ToArray();
        using var factory = NativeCorrelationFactory(protocol, calls, stopAtBoundary: true, structured: true);
        // Four columns admit eight native calls. Stop at the next fake HTTP
        // boundary: these assertions qualify wiring, not a structured answer.
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
                .ExecuteAsync(NativeCorrelationRequest(columns: 4), gateway, CancellationToken.None));
        Assert.Equal("advanced_llm_http_401", error.ErrorCode);
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta" },
            gateway.Searches.Skip(1).Select(request => request.Query));
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
        Assert.Equal(8, outputs.Length);
        Assert.Equal("E-Gamma", NativeCorrelationVisibleId(outputs[6]));
        Assert.Equal("executed", outputs[7].Body.GetProperty("status").GetString());
        Assert.Equal(0, outputs[7].Body.GetProperty("returnedEvidenceCount").GetInt32());
        Assert.Empty(outputs[7].Body.GetProperty("visibleEvidenceIds").EnumerateArray());
        Assert.Equal("no_evidence_returned", outputs[7].Body.GetProperty("projectionFeedback").GetProperty("status").GetString());
        Assert.Equal(3, factory.Requests.Count);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_search_policy_aliases_and_category_defaults_keep_the_executed_identity(string protocol)
    {
        var gateway = new NativeCorrelationGateway("Alpha") { Categories = ["allowed"] };
        var calls = new[]
        {
            ("search_corpus", NativeCorrelationSearch(" Alpha site:example.invalid ", "allowed")),
            ("search_corpus", NativeCorrelationSearch("Alpha", " allowed ")),
            ("search_corpus", NativeCorrelationSearch("Alpha", 17)),
            ("search_corpus", NativeCorrelationSearch("Alpha https://example.invalid/a", "allowed"))
        };
        using var factory = NativeCorrelationFactory(protocol, calls);
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        var executed = gateway.Searches.Skip(1).ToArray();
        Assert.Equal(2, executed.Length);
        Assert.All(executed, request => Assert.Equal("Alpha", request.Query));
        Assert.Equal("allowed", executed[0].Category); Assert.Null(executed[1].Category);
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
        Assert.Equal(new[] { "E-Alpha", "E-Alpha", "E-Alpha", "E-Alpha" }, outputs.Select(NativeCorrelationVisibleId));
        Assert.All(outputs, row => Assert.Equal("executed", row.Body.GetProperty("status").GetString()));
        Assert.False(outputs[2].Body.TryGetProperty("resultReusedWithinBatch", out var uncategorizedReplay) && uncategorizedReplay.GetBoolean());
        Assert.True(outputs[3].Body.GetProperty("resultReusedWithinBatch").GetBoolean());
    }

    [Theory]
    [InlineData("null", 20)]
    [InlineData("\"not-a-number\"", 20)]
    [InlineData("1.5", 20)]
    [InlineData("2147483648", 20)]
    [InlineData("0", 1)]
    [InlineData("61", 60)]
    public async Task Native_topk_default_and_clamp_follow_the_executed_request(string topKJson, int expected)
    {
        var gateway = new NativeCorrelationGateway("Alpha");
        var arguments = JsonNode.Parse(NativeCorrelationFind("internal-source-1", "Alpha"))!.AsObject();
        arguments["topK"] = JsonNode.Parse(topKJson);
        using var factory = NativeCorrelationFactory("chat-completions", [("find_source_text", arguments.ToJsonString())]);
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal(expected, Assert.Single(gateway.Searches.Skip(1)).TopK);
        var output = Assert.Single(NativeCorrelationOutputs(factory.Requests[2].Body, "chat-completions"));
        Assert.Equal("executed", output.Body.GetProperty("status").GetString());
        Assert.Equal("E-Alpha", NativeCorrelationVisibleId(output));
    }

    [Theory]
    [InlineData("chat-completions", "joint Manuel.pdf", "joint")]
    [InlineData("responses", "joint Manuel.pdf", "joint")]
    [InlineData("chat-completions", "joint", "joint")]
    [InlineData("responses", "joint", "joint")]
    public async Task Native_document_scope_and_duplicate_calls_use_the_actual_scoped_request(
        string protocol, string rawQuery, string expectedQuery)
    {
        var gateway = new NativeCorrelationGateway("joint", "Beta", "Gamma", fileName: "Manuel.pdf");
        var calls = new[] { rawQuery, "Beta", rawQuery, "Gamma" }
            .Select(query => ("search_corpus", NativeCorrelationSearch(query, "", sourceKey: ""))).ToArray();
        using var factory = NativeCorrelationFactory(protocol, calls);
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(NativeCorrelationRequest(documentName: "Manuel.pdf"), gateway, CancellationToken.None);
        var executed = gateway.Searches.Skip(1).ToArray();
        Assert.Equal(new[] { expectedQuery, "Beta", "Gamma" }, executed.Select(request => request.Query));
        Assert.All(executed, request => { Assert.Equal("Manuel.pdf", request.DocumentHint); Assert.Null(request.DocId); });
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
        Assert.Equal(new[] { "E-joint", "E-Beta", "E-joint", "E-Gamma" }, outputs.Select(NativeCorrelationVisibleId));
        Assert.All(outputs, row => Assert.Equal("executed", row.Body.GetProperty("status").GetString()));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_same_query_in_two_sources_preserves_the_executed_source_identity(string protocol)
    {
        var gateway = new NativeCorrelationGateway("same literal");
        var other = BuildEvidence("E-OTHER", "Another source explicitly states the same literal.", "other.pdf");
        gateway.InitialEvidence.Add(other);
        gateway.Resolve = request => request.DocId == other.Reference.DocId ? [other] : [gateway.Bodies[0]];
        var calls = new[] { "internal-source-1", "internal-source-2", "internal-source-1", "internal-source-2" }
            .Select(key => ("find_source_text", NativeCorrelationFind(key, "same literal"))).ToArray();
        using var factory = NativeCorrelationFactory(protocol, calls);
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        var executed = gateway.Searches.Skip(1).ToArray();
        Assert.Equal(new[] { gateway.Seed.Reference.DocId, other.Reference.DocId }, executed.Select(request => request.DocId));
        Assert.Equal(new[] { gateway.Seed.Reference.RevisionId, other.Reference.RevisionId }, executed.Select(request => request.RevisionId));
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
        Assert.Equal(new[] { "E-same literal", "E-OTHER", "E-same literal", "E-OTHER" }, outputs.Select(NativeCorrelationVisibleId));
        for (var index = 0; index < outputs.Length; index++)
        {
            var expected = index % 2 == 0 ? gateway.Seed.Reference : other.Reference;
            Assert.Equal(expected.DocId, outputs[index].Body.GetProperty("resolvedSource").GetProperty("docId").GetString());
            Assert.Equal(expected.RevisionId, outputs[index].Body.GetProperty("resolvedSource").GetProperty("revisionId").GetString());
            Assert.Equal(expected.DocPath, outputs[index].Body.GetProperty("resolvedSource").GetProperty("docPath").GetString());
        }
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_cross_source_result_cannot_borrow_another_active_sources_body(string protocol)
    {
        var gateway = new NativeCorrelationGateway("same literal");
        var other = BuildEvidence("E-OTHER", "Another source explicitly states the same literal.", "other.pdf");
        gateway.InitialEvidence.Add(other);
        gateway.Resolve = _ => [other];
        using var factory = NativeCorrelationFactory(protocol,
            [("find_source_text", NativeCorrelationFind("internal-source-1", "same literal")),
                ("find_source_text", NativeCorrelationFind("internal-source-2", "same literal"))]);
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal(new[] { gateway.Seed.Reference.DocId, other.Reference.DocId },
            gateway.Searches.Skip(1).Select(request => request.DocId));
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
        Assert.Equal("result_correlation_unavailable", outputs[0].Body.GetProperty("status").GetString());
        Assert.Empty(outputs[0].Body.GetProperty("visibleEvidenceIds").EnumerateArray());
        Assert.Equal("executed", outputs[1].Body.GetProperty("status").GetString());
        Assert.Equal("E-OTHER", NativeCorrelationVisibleId(outputs[1]));
    }

    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Native_invalid_middle_call_rejects_every_documentary_operation(string protocol, bool nullOffset)
    {
        var gateway = new NativeCorrelationGateway("Alpha", "Beta", "Gamma");
        var malformed = JsonNode.Parse(NativeCorrelationFind("internal-source-1", "Beta"))!.AsObject();
        if (nullOffset) malformed["offset"] = null; else malformed["unexpected"] = true;
        var calls = new[] { ("find_source_text", NativeCorrelationFind("internal-source-1", "Alpha")),
            ("find_source_text", malformed.ToJsonString()), ("find_source_text", NativeCorrelationFind("internal-source-1", "Gamma")) };
        using var factory = NativeCorrelationFactory(protocol, calls, stopAtBoundary: true);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Single(gateway.Searches);
        // An unknown argument is correctable as an offered native batch;
        // invalid parsed find offsets fail admission before any retry.
        Assert.Equal(nullOffset ? "advanced_synthesis_research_protocol_invalid" : "advanced_llm_http_401", error.ErrorCode);
        Assert.Equal(nullOffset ? 2 : 3, factory.Requests.Count);
        if (!nullOffset)
            Assert.All(NativeCorrelationOutputs(factory.Requests[2].Body, protocol), row =>
            {
                Assert.Equal("batch_rejected_before_execution", row.Body.GetProperty("status").GetString());
                Assert.Empty(row.Body.GetProperty("visibleEvidenceIds").EnumerateArray());
            });
        Assert.Equal(new[] { gateway.Seed.Reference.EvidenceId }, gateway.Evidence.Select(item => item.Reference.EvidenceId));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_returned_omitted_bodies_have_bounded_feedback_and_only_final_projection_probes(string protocol)
    {
        var gateway = new NativeCorrelationGateway(Enumerable.Range(0, 12).Select(index => "Title" + index).ToArray());
        for (var index = 0; index < gateway.Bodies.Length; index++)
            gateway.Bodies[index] = gateway.Bodies[index] with { Content = "Private synthetic canonical marker " + index + " " + new string('x', 2_400) };
        gateway.Resolve = _ => gateway.Bodies;
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 8_000;
        var logger = new NativeCorrelationLogger();
        using var factory = NativeCorrelationFactory(protocol,
            [("find_source_text", NativeCorrelationFind("internal-source-1", "Title", 60))], stopAtBoundary: true);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_llm_http_401", error.ErrorCode);
        var body = factory.Requests[2].Body;
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 8_000);
        var output = Assert.Single(NativeCorrelationOutputs(body, protocol)).Body;
        Assert.Equal("executed", output.GetProperty("status").GetString());
        Assert.Equal(12, output.GetProperty("returnedEvidenceCount").GetInt32());
        var feedback = output.GetProperty("projectionFeedback");
        var visible = output.GetProperty("visibleEvidenceIds").GetArrayLength() + output.GetProperty("visibleEvidenceIdsOmitted").GetInt32();
        Assert.InRange(visible, 1, 11);
        Assert.Equal(visible, feedback.GetProperty("visibleEvidenceIdCount").GetInt32());
        Assert.Equal(12 - visible, feedback.GetProperty("omittedEvidenceIdCount").GetInt32());
        Assert.Equal(12 - visible, feedback.GetProperty("omittedEvidenceIds").GetArrayLength());
        Assert.Equal(Math.Min(8, 12 - visible), feedback.GetProperty("omittedPhysicalLocators").GetArrayLength());
        if (12 - visible > 8)
            Assert.Equal(12 - visible - 8, feedback.GetProperty("omittedPhysicalLocatorsOmittedCount").GetInt32());
        Assert.Contains("not documentary facts", output.GetProperty("instruction").GetString());
        var probe = Assert.Single(logger.Entries, entry => entry.Contains("operation=advanced.native_tool_results ", StringComparison.Ordinal));
        Assert.Contains("\"unmatched_retrieval_call_count\":0", probe);
        Assert.DoesNotContain("Private synthetic canonical marker", probe);
        Assert.DoesNotContain("Title", probe);
        Assert.DoesNotContain("E-", probe);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_hidden_source_handles_use_the_full_catalogue_during_budget_fitting(string protocol)
    {
        var gateway = new NativeCorrelationGateway("Unused");
        gateway.InitialEvidence[0] = gateway.Seed with { Content = "Canonical source one. " + new string('x', 3_000) };
        for (var index = 2; index <= 4; index++)
            gateway.InitialEvidence.Add(BuildEvidence("SOURCE-" + index,
                "Canonical source " + index + ". " + new string('x', 3_000), "source" + index + ".pdf"));
        gateway.Resolve = _ => [];
        var calls = Enumerable.Range(1, 4).Select(index =>
            ("find_source_text", NativeCorrelationFind("internal-source-" + index, "missing literal " + index))).ToArray();
        using var factory = NativeCorrelationFactory(protocol, calls, stopAtBoundary: true);
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 8_000;
        var logger = new NativeCorrelationLogger();
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_llm_http_401", error.ErrorCode);
        var before = WorkspaceUser(factory.Requests[1].Body);
        var catalogueKeys = before.GetProperty("sourceCatalogue").GetProperty("sources").EnumerateArray()
            .Select(source => source.GetProperty("sourceKey").GetString()).ToArray();
        var visibleKeys = before.GetProperty("evidence").EnumerateArray()
            .Select(item => item.GetProperty("sourceKey").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(4, catalogueKeys.Length);
        Assert.Contains(catalogueKeys, key => !visibleKeys.Contains(key));
        Assert.Equal(gateway.InitialEvidence.Select(item => item.Reference.DocId),
            gateway.Searches.Skip(1).Select(request => request.DocId));
        var final = factory.Requests[2].Body;
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(final), 1, 8_000);
        var outputs = NativeCorrelationOutputs(final, protocol);
        Assert.Equal(4, outputs.Length);
        Assert.All(outputs, row =>
        {
            Assert.Equal("executed", row.Body.GetProperty("status").GetString());
            Assert.Equal(0, row.Body.GetProperty("returnedEvidenceCount").GetInt32());
        });
        Assert.Single(logger.Entries, entry => entry.Contains("operation=advanced.native_tool_results ", StringComparison.Ordinal));
    }

    private static string NativeCorrelationFind(string sourceKey, string query, int topK = 3)
        => JsonSerializer.Serialize(new { sourceKey, query, offset = 0, topK });

    private static string NativeCorrelationSearch(string query, object category, string sourceKey = "internal-source-1")
        => JsonSerializer.Serialize(new { query, sourceKey, category, documentHint = "", topK = 3 });

    private static string? NativeCorrelationVisibleId((string CallId, JsonElement Body) row)
        => row.Body.GetProperty("visibleEvidenceIds")[0].GetString();

    private static QueuedHttpClientFactory NativeCorrelationFactory(string protocol,
        (string Name, string Arguments)[] calls, bool stopAtBoundary = false, bool structured = false)
        => new(Completion(structured
                ? """{"selectionMode":"content_claims","queries":[{"query":"overview","topK":12}]}"""
                : """{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeCorrelationResponseFunctions(calls) : NativeCompletion(calls),
            stopAtBoundary ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("synthetic boundary stop") }
                : protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));

    private static HttpResponseMessage NativeCorrelationResponseFunctions((string Name, string Arguments)[] calls)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(new
        {
            status = "completed", output = calls.Select((call, index) => (object)new
            {
                id = "fc_" + index, type = "function_call", call_id = "call_" + index,
                name = call.Name, arguments = call.Arguments, status = "completed"
            }).ToArray(), usage = new { input_tokens = 100, output_tokens = 50 }
        }) };

    private static (string CallId, JsonElement Body)[] NativeCorrelationOutputs(string requestBody, string protocol)
    {
        using var request = JsonDocument.Parse(requestBody);
        var responses = protocol == "responses";
        return request.RootElement.GetProperty(responses ? "input" : "messages").EnumerateArray()
            .Where(message => responses ? message.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : message.TryGetProperty("role", out var role) && role.GetString() == "tool")
            .Select(message => (message.GetProperty(responses ? "call_id" : "tool_call_id").GetString()!,
                JsonDocument.Parse(message.GetProperty(responses ? "output" : "content").GetString()!).RootElement.Clone())).ToArray();
    }

    private static AdvancedAnalysisProviderRequest NativeCorrelationRequest(int columns = 0, string documentName = "")
        => new(Guid.NewGuid(), Guid.NewGuid(), "test-user", new AdvancedAnalysisHandoffEnvelope
        {
            HandoffId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow,
            RequestText = documentName.Length == 0 ? "Relève les valeurs documentées." : "Relève les valeurs de " + documentName + ".",
            Language = "fr", OriginIntent = "rag.answer", ReasonCode = "advanced_capacity_required", TransferStage = "pre_retrieval",
            Load = new AdvancedAnalysisLoadDescriptor
            {
                PlanKind = columns > 0 ? "multi_item" : "single_item", Deliverable = "sourced_answer",
                AnswerUnitCount = columns > 0 ? columns : 1, AtomicEvidenceCount = columns > 0 ? columns : 1,
                StructuredLayout = columns > 0, RowCount = columns > 0 ? 1 : 0, ColumnCount = columns,
                AtomicEvidenceType = "documented_fact", AtomicEvidenceMode = columns > 0 ? "one_per_cell" : "content_claim",
                SelectionPolicy = columns > 0 ? "structured_layout" : "single_item",
                RowLabels = columns > 0 ? ["Valeurs"] : [],
                Columns = Enumerable.Range(1, columns).Select(index => "Valeur " + index).ToList(),
                RequestedDocumentName = documentName
            }, ResearchState = new AdvancedAnalysisResearchState { EvidenceRevalidationRequired = true, MemoryIsEvidence = false }
        }, [], []);

    private sealed class NativeCorrelationGateway : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _active = [];
        public NativeCorrelationGateway(params string[] titles) : this(titles, "menus.pdf") { }
        public NativeCorrelationGateway(string title1, string title2, string title3, string fileName)
            : this([title1, title2, title3], fileName) { }
        private NativeCorrelationGateway(string[] titles, string fileName)
        {
            Seed = BuildEvidence("E1", "Le seuil est 17 bar.", fileName);
            InitialEvidence = [Seed];
            Bodies = titles.Select((title, index) => BuildEvidence("E-" + title,
                title + " canonical substantive body. Isolate the assembly before applying this value.", fileName,
                Seed.Reference.DocId, Seed.Reference.RevisionId, index + 1)).ToArray();
        }
        public AdvancedAnalysisResolvedEvidence Seed { get; }
        public AdvancedAnalysisResolvedEvidence[] Bodies { get; }
        public List<AdvancedAnalysisResolvedEvidence> InitialEvidence { get; }
        public Func<AdvancedAnalysisSearchRequest, IReadOnlyList<AdvancedAnalysisResolvedEvidence>>? Resolve { get; set; }
        public IReadOnlyList<string> Categories { get; init; } = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _active;
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken) => Task.FromResult(Categories);
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = Searches.Count == 0 ? InitialEvidence.ToArray()
                : Resolve?.Invoke(request) ?? Bodies.Where(item => string.Equals(item.Reference.EvidenceId, "E-" + request.Query, StringComparison.OrdinalIgnoreCase)).ToArray();
            Searches.Add(request);
            foreach (var item in found)
                if (_active.All(existing => existing.Reference.EvidenceId != item.Reference.EvidenceId)) _active.Add(item);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, found, [], 1, Searches.Count));
        }
    }

    private sealed class NativeCorrelationLogger : ILogger<OpenAiCompatibleAdvancedAnalysisProvider>
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(formatter(state, exception));
    }
}
