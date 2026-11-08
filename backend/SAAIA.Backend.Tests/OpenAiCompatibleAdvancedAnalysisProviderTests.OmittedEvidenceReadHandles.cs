using System.Collections;
using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Omitted_primary_body_reopens_through_the_complete_agent_loop_before_becoming_citable(string protocol, bool duplicate)
    {
        var bodies = OmittedHandleBodies();
        var gateway = new OmittedHandleGateway(bodies);
        string? chosenId = null;
        var calls = 0;
        using var factory = new OmittedHandleHttpFactory(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            var user = WorkspaceUser(body);
            if (calls > 0)
                Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 10_000);
            HttpResponseMessage response;
            switch (calls++)
            {
                case 0:
                    response = Completion("""{"queries":[{"query":"overview","topK":12}]}""");
                    break;
                case 1:
                    response = OmittedHandleFunction(protocol, "primary-read", "read_source",
                        """{"sourceKey":"internal-source-1","pageStart":14,"pageCount":1,"topK":20}""");
                    break;
                case 2:
                    var handle = user.GetProperty("omittedEvidenceReadHandles").GetProperty("items")[0];
                    chosenId = handle.GetProperty("evidenceId").GetString()!;
                    Assert.DoesNotContain(user.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == chosenId);
                    Assert.Equal("executed", Assert.Single(NativeCorrelationOutputs(body, protocol)).Body.GetProperty("status").GetString());
                    var readArguments = JsonSerializer.Serialize(new { evidenceId = chosenId, offset = 0, length = 512 });
                    response = duplicate
                        ? EvidencePrefixReply(protocol, [("read_evidence", readArguments), ("read_evidence", readArguments)])
                        : OmittedHandleFunction(protocol, "omitted-window", "read_evidence", readArguments);
                    break;
                case 3:
                    var reopened = Assert.Single(user.GetProperty("evidence").EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == chosenId);
                    Assert.Equal(Assert.Single(bodies, item => item.Reference.EvidenceId == chosenId).Content[..512], reopened.GetProperty("content").GetString());
                    Assert.DoesNotContain(user.GetProperty("omittedEvidenceReadHandles").GetProperty("items").EnumerateArray(),
                        item => item.GetProperty("evidenceId").GetString() == chosenId);
                    var readOutputs = NativeCorrelationOutputs(body, protocol);
                    Assert.Equal(duplicate ? 2 : 1, readOutputs.Length);
                    Assert.Equal(readOutputs.Length, readOutputs.Select(output => output.CallId).Distinct().Count());
                    Assert.All(readOutputs, output => Assert.Equal("projected", output.Body.GetProperty("status").GetString()));
                    response = Completion(JsonSerializer.Serialize(new
                    {
                        outcome = "answered", answerText = "The documented procedure is available [C1].",
                        claims = new[] { new { claimId = "C1", text = "The procedure is documented.", evidenceIds = new[] { chosenId } } }
                    }));
                    break;
                default: throw new InvalidOperationException("No extra model or retrieval cycle is expected.");
            }
            return await AdaptProtocolAgnosticFixtureAsync(request, response, CancellationToken.None);
        });
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 10_000;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(chosenId, Assert.Single(result.Claims).EvidenceIds.Single());
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Single(gateway.Requests, request => request.Operation == "read_source");
        Assert.DoesNotContain(gateway.Requests, request => request.Operation == "read_evidence");
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public void Omitted_primary_handles_count_exactly_on_the_escaped_documentary_wire(string protocol)
    {
        var user = new JsonObject
        {
            ["operational"] = true,
            ["omittedEvidenceReadHandles"] = JsonSerializer.SerializeToNode(new
            {
                navigationOnly = true, citable = false,
                items = new[] { new { evidenceId = "quoted-\"漢字-🧪", sourceKey = "source-1", originalContentLength = 512,
                    physicalPageStart = 14, physicalPageEnd = 14 } },
                instruction = "Metadata only: quoted \"scope\"; no body."
            }, CrossBatchInnerJson)
        };
        var outer = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var message = new JsonObject { ["role"] = "user", ["content"] = user.ToJsonString(CrossBatchInnerJson) };
        var wire = new JsonObject { [protocol == "responses" ? "input" : "messages"] = new JsonArray(message) };
        var original = wire.ToJsonString(outer);
        user.Remove("omittedEvidenceReadHandles");
        message["content"] = user.ToJsonString(CrossBatchInnerJson);
        var expected = original.Length - wire.ToJsonString(outer).Length;
        Assert.True(expected > 0);
        Assert.Equal(expected, OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(original));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Omitted_primary_handle_payload_cannot_bypass_the_final_documentary_limit(string protocol)
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 8_000;
        options.NativeObservationReadEnabled = false; options.VisualSourceCitationsEnabled = false;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var user = JsonSerializer.Serialize(new
        {
            evidence = Array.Empty<object>(),
            omittedEvidenceReadHandles = new
            {
                navigationOnly = true, citable = false,
                items = Enumerable.Range(0, 64).Select(index => new
                {
                    evidenceId = "active-" + index + new string('x', 160), sourceKey = "source-1",
                    originalContentLength = 512, physicalPageStart = 14, physicalPageEnd = 14
                }).ToArray()
            }
        }, CrossBatchInnerJson);
        var projection = SourceCatalogueProjection(provider, Array.Empty<AdvancedAnalysisResolvedEvidence>());
        var task = Assert.IsAssignableFrom<Task>(InvokeSourceCatalogue(provider, "CompleteJsonAsync", Guid.NewGuid(),
            "writer", "Return JSON.", user, 128, CancellationToken.None, false, null, projection));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(async () => await task);
        Assert.Equal("advanced_documentary_wire_budget_exceeded", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Omitted_primary_body_is_readable_on_critic_recovery_without_repeating_an_already_executed_page(string protocol)
    {
        var bodies = OmittedHandleBodies();
        var gateway = new AccumulatedReadGateway(bodies);
        var context = AccumulatedReadContext(gateway);
        using var emptyFactory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 10_000;
        var preparer = new OpenAiCompatibleAdvancedAnalysisProvider(emptyFactory, options, null);
        var turn = OmittedHandleTurn(bodies);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        var projection = OmittedHandleProjection(preparer, bodies, turn, context);
        var prompt = OmittedHandlePrompt(preparer, projection, context);
        using var initial = JsonDocument.Parse(prompt);
        var handle = initial.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items")[0];
        var id = handle.GetProperty("evidenceId").GetString()!;
        var body = Assert.Single(bodies, item => item.Reference.EvidenceId == id);
        Assert.DoesNotContain(initial.RootElement.GetProperty("evidence").EnumerateArray(), row => row.GetProperty("evidenceId").GetString() == id);
        Assert.True(initial.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("navigationOnly").GetBoolean());
        Assert.False(initial.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("citable").GetBoolean());
        Assert.False(handle.TryGetProperty("content", out _));
        Assert.False(handle.TryGetProperty("sourceReference", out _));
        Assert.Equal(14, handle.GetProperty("physicalPageStart").GetInt32());
        var read = JsonSerializer.Serialize(new { evidenceId = id, offset = 0, length = 512 });
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseFunction("read_evidence", read) : NativeCompletion(("read_evidence", read)),
            Completion("""{"outcome":"insufficient_documentation","answerText":"Further evidence is needed.","claims":[]}"""));
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var history = InvokeSourceCatalogue(provider, "BuildNativeToolTurnMessages", turn, projection, protocol == "responses")!;
        var task = Assert.IsAssignableFrom<Task>(InvokeSourceCatalogue(provider, "CompleteJsonAsync", Guid.NewGuid(),
            "critic-research-recovery-1", "Return JSON.", prompt, 512, CancellationToken.None, true, history, projection));
        await task;
        var completion = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var normalized = (string)completion.GetType().GetProperty("Content")!.GetValue(completion)!;
        using var content = JsonDocument.Parse(normalized);
        var windows = InvokeSourceCatalogue(null, "ParseEvidenceReadRequests", content.RootElement, projection, context)!;
        Assert.Equal(1, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", windows, context)!);

        // The enum is on the actual HTTP boundary, not just the local payload.
        using var firstWire = JsonDocument.Parse(factory.Requests[0].Body);
        var function = firstWire.RootElement.GetProperty("tools").EnumerateArray().Single(tool => protocol == "responses"
            ? tool.GetProperty("name").GetString() == "read_evidence"
            : tool.GetProperty("function").GetProperty("name").GetString() == "read_evidence");
        var schema = protocol == "responses" ? function : function.GetProperty("function");
        Assert.Contains(id, schema.GetProperty("parameters").GetProperty("properties").GetProperty("evidenceId")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        var disclosed = Assert.Single(NativeCorrelationOutputs(factory.Requests[0].Body, protocol)).Body
            .GetProperty("projectionFeedback").GetProperty("omittedEvidenceIds").EnumerateArray().Select(value => value.GetString());
        Assert.Contains(id, disclosed);
        Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(null, "EnsureVisibleClaimCitations",
            new AdvancedAnalysisProviderResult { Outcome = "answered", AnswerText = "Observed.",
                Claims = [new() { ClaimId = "C1", Text = "Observed.", EvidenceIds = [id] }] },
            initial.RootElement.GetProperty("evidence").EnumerateArray().Select(row => row.GetProperty("evidenceId").GetString()!).ToArray()));

        var nextProjection = OmittedHandleProjection(provider, bodies, turn, context);
        using var next = JsonDocument.Parse(SourceCataloguePrompt(provider, nextProjection));
        var reopened = Assert.Single(next.RootElement.GetProperty("evidence").EnumerateArray(), row => row.GetProperty("evidenceId").GetString() == id);
        Assert.Equal(0, reopened.GetProperty("contentOffset").GetInt32());
        Assert.Equal(body.Content[..512], reopened.GetProperty("content").GetString());
        Assert.Equal(body.Content.Length, reopened.GetProperty("originalContentLength").GetInt32());
        InvokeSourceCatalogue(null, "EnsureVisibleClaimCitations",
            new AdvancedAnalysisProviderResult { Outcome = "answered", AnswerText = "Observed.",
                Claims = [new() { ClaimId = "C1", Text = "Observed.", EvidenceIds = [id] }] },
            new[] { id });
        Assert.Empty(emptyFactory.Requests);
        Assert.Equal(bodies, gateway.Active);
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("retired")]
    [InlineData("revision")]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("source")]
    [InlineData("not_in_current_prompt")]
    [InlineData("body_same_length")]
    [InlineData("pages")]
    [InlineData("undisclosed")]
    public void Omitted_primary_read_handle_requires_current_disclosure_and_exact_active_canonical_scope(string defect)
    {
        var bodies = OmittedHandleBodies();
        var gateway = new AccumulatedReadGateway(bodies);
        var context = AccumulatedReadContext(gateway);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 10_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var turn = OmittedHandleTurn(bodies);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        var projection = OmittedHandleProjection(provider, bodies, turn, context);
        using var prompt = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        var id = prompt.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items")[0].GetProperty("evidenceId").GetString()!;
        if (defect == "forged") id = "NEVER-OBSERVED";
        else if (defect == "undisclosed")
        {
            // A valid active fragment outside the executed read has never been
            // offered. Knowing its ID cannot grant a read capability.
            var unlisted = PreviousProjectionBody("UNLISTED-ACTIVE", page: 80);
            gateway.Active = bodies.Append(unlisted).ToArray();
            id = unlisted.Reference.EvidenceId;
        }
        else if (defect == "not_in_current_prompt")
        {
            var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("OmittedEvidenceReadHandle", BindingFlags.NonPublic)!;
            context.GetType().GetProperty("OmittedEvidenceReadHandles")!.SetValue(context, Array.CreateInstance(type, 0));
        }
        else if (defect == "retired") gateway.Active = bodies.Where(item => item.Reference.EvidenceId != id).ToArray();
        else gateway.Active = bodies.Select(item => item.Reference.EvidenceId != id ? item : item with
        {
            Content = defect == "body_same_length" ? new string('z', item.Content.Length) : item.Content,
            Reference = new()
            {
                EvidenceId = id, DocId = defect == "source" ? "other-source" : item.Reference.DocId,
                RevisionId = defect == "revision" ? "other-revision" : item.Reference.RevisionId,
                SourceHash = defect == "hash" ? "other-hash" : item.Reference.SourceHash,
                DocPath = defect == "path" ? "other/path.pdf" : item.Reference.DocPath,
                ChunkId = item.Reference.ChunkId, PageStart = defect == "pages" ? 15 : 14, PageEnd = defect == "pages" ? 15 : 14
            }
        }).ToArray();
        using var read = JsonDocument.Parse(JsonSerializer.Serialize(new { evidenceReads = new[] { new { evidenceId = id, offset = 0, length = 512 } } }));
        var error = Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(null,
            "ParseEvidenceReadRequests", read.RootElement, projection, context));
        Assert.Equal("advanced_native_evidence_read_invalid", error.ErrorCode);
        Assert.Empty((IEnumerable)context.GetType().GetProperty("EvidenceReadWindows")!.GetValue(context)!);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("source")]
    [InlineData("content")]
    [InlineData("empty")]
    [InlineData("null")]
    public void Omitted_primary_read_handle_rejects_ambiguous_active_copies_of_the_same_id(string difference)
    {
        var bodies = OmittedHandleBodies();
        var gateway = new AccumulatedReadGateway(bodies);
        var context = AccumulatedReadContext(gateway);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 10_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var turn = OmittedHandleTurn(bodies);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        var projection = OmittedHandleProjection(provider, bodies, turn, context);
        using var prompt = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        var id = prompt.RootElement.GetProperty("omittedEvidenceReadHandles").GetProperty("items")[0].GetProperty("evidenceId").GetString()!;
        var original = Assert.Single(bodies, item => item.Reference.EvidenceId == id);
        gateway.Active = bodies.Append(original with
        {
            Content = difference == "content" ? original.Content + "Changed canonical bytes."
                : difference == "empty" ? string.Empty : difference == "null" ? null! : original.Content,
            Reference = new()
            {
                EvidenceId = id, DocId = difference == "source" ? "other-source" : original.Reference.DocId,
                RevisionId = original.Reference.RevisionId, SourceHash = difference == "hash" ? "other-hash" : original.Reference.SourceHash,
                DocPath = original.Reference.DocPath, ChunkId = original.Reference.ChunkId, PageStart = 14, PageEnd = 14
            }
        }).ToArray();
        using var read = JsonDocument.Parse(JsonSerializer.Serialize(new { evidenceReads = new[] { new { evidenceId = id, offset = 0, length = 512 } } }));
        Assert.Throws<AdvancedAnalysisProviderException>(() => InvokeSourceCatalogue(null,
            "ParseEvidenceReadRequests", read.RootElement, projection, context));
        var handles = ((IEnumerable)InvokeSourceCatalogue(null, "BuildOmittedEvidenceReadHandles", turn, projection, gateway.Active)!).Cast<object>();
        Assert.DoesNotContain(handles, handle => (string)handle.GetType().GetProperty("EvidenceId")!.GetValue(handle)! == id);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Omitted_primary_read_handle_is_not_disclosed_for_deferred_or_failed_reads(bool deferred, bool failed)
    {
        var bodies = OmittedHandleBodies();
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 10_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var turn = OmittedHandleTurn(bodies, deferred, failed);
        var context = AccumulatedReadContext(new AccumulatedReadGateway(bodies));
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        var projection = OmittedHandleProjection(provider, bodies, turn, context);
        using var prompt = JsonDocument.Parse(OmittedHandlePrompt(provider, projection, context));
        Assert.False(prompt.RootElement.TryGetProperty("omittedEvidenceReadHandles", out _));
    }

    private static AdvancedAnalysisResolvedEvidence[] OmittedHandleBodies()
        => Enumerable.Range(0, 20).Select(index => PrimaryReadBody("CURRENT-" + index,
            "Canonical procedure " + index + ": " + new string('x', 2_100)) with
        {
            Reference = new()
            {
                EvidenceId = "CURRENT-" + index, DocId = "11111111-1111-4111-8111-111111111111",
                RevisionId = "22222222-2222-4222-8222-222222222222", SourceHash = "sha256:test",
                DocPath = "Documents/inspection.pdf", FileName = "inspection.pdf", ChunkId = "chunk-" + index,
                PageStart = 14, PageEnd = 14
            }
        }).ToArray();

    private static object OmittedHandleTurn(IReadOnlyList<AdvancedAnalysisResolvedEvidence> bodies,
        bool deferred = false, bool failed = false)
    {
        var request = new AdvancedAnalysisSearchRequest("", DocId: bodies[0].Reference.DocId,
            RevisionId: bodies[0].Reference.RevisionId, DocPath: bodies[0].Reference.DocPath,
            TopK: 20, PageStart: 14, PageEnd: 14, Operation: "read_source");
        var turn = PrimaryReadProjectionTurn(request, bodies, bodies[0].Reference, deferred, failed);
        var calls = JsonSerializer.Serialize(new[] { new { id = "read", type = "function",
            function = new { name = "read_source", arguments = "{\"sourceKey\":\"internal-source-1\",\"pageStart\":14,\"pageCount\":1,\"topK\":20}" } } });
        turn.GetType().GetProperty("CallsJson")!.SetValue(turn, calls);
        return turn;
    }

    private static object OmittedHandleProjection(OpenAiCompatibleAdvancedAnalysisProvider provider,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> bodies, object turn, object context)
        => InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithOmittedReadRegistry", BuildDirectRequest(), bodies,
            new Dictionary<string, HashSet<string>>(), bodies, new Dictionary<string, string>(),
            context.GetType().GetProperty("EvidenceReadWindows")!.GetValue(context), turn,
            context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context))!;

    private static string OmittedHandlePrompt(OpenAiCompatibleAdvancedAnalysisProvider provider, object projection, object context)
        => (string)InvokeSourceCatalogue(null, "AddOmittedEvidenceReadHandlesToPrompt",
            SourceCataloguePrompt(provider, projection), projection, context)!;

    private static HttpResponseMessage OmittedHandleFunction(string protocol, string id, string name, string arguments)
        => new(HttpStatusCode.OK)
        {
            Content = protocol == "responses" ? JsonContent.Create(new
            {
                status = "completed", output = new object[]
                {
                    new { id = "reason-" + id, type = "reasoning", summary = Array.Empty<object>(), encrypted_content = "synthetic-" + id },
                    new { id = "function-" + id, type = "function_call", call_id = id, name, arguments, status = "completed" }
                }, usage = new { input_tokens = 100, output_tokens = 50 }
            }) : JsonContent.Create(new
            {
                choices = new[] { new { finish_reason = "tool_calls", message = new
                {
                    role = "assistant", content = (string?)null,
                    tool_calls = new[] { new { id, type = "function", function = new { name, arguments } } }
                } } }, usage = new { prompt_tokens = 100, completion_tokens = 50 }
            })
        };

    private sealed class OmittedHandleGateway(IReadOnlyList<AdvancedAnalysisResolvedEvidence> bodies,
        bool exhaustAfterPage = false) : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _active = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _active;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];
        public AdvancedAnalysisToolBudget? Budget => exhaustAfterPage
            ? new(2, Requests.Count, 10_000, Requests.Count, 128, _active.Count) : null;
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var found = Requests.Count == 1 ? bodies.Take(1).ToArray() : bodies;
            if (Requests.Count > 2) throw new InvalidOperationException("A window read must not repeat corpus IO.");
            foreach (var item in found)
                if (_active.All(active => active.Reference.EvidenceId != item.Reference.EvidenceId)) _active.Add(item);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, found, [], 1, Requests.Count));
        }
    }

    private sealed class OmittedHandleHttpFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new OmittedHandleHttpHandler(response));
        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }

    private sealed class OmittedHandleHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => response(request);
    }
}
