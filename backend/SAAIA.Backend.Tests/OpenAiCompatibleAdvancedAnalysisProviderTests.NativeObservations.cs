using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Native_audit_lines_are_discoverable_first_turn_and_current_exact_once_on_actual_wire(string protocol, bool inventoryFirst)
    {
        var gateway = new NativeAuditGateway(exposeInitialInventory: !inventoryFirst);
        var arguments = JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", pageStart = 1, pageEnd = 1, topK = 20,
            nativeObservationView = "raw", nativeObservationBlockId = "native-audit",
            nativeObservationRawSha256 = gateway.RawSha, nativeObservationOffset = 0, nativeObservationLimit = 2
        });
        var responses = new List<HttpResponseMessage> { Completion("""{"queries":[{"query":"overview","topK":12}]}""") };
        if (inventoryFirst)
        {
            const string inventory = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":20,"nativeObservationView":"inventory","nativeObservationOffset":0,"nativeObservationLimit":4}""";
            responses.Add(protocol == "responses" ? NativeResponseFunction("read_source", inventory) : NativeCompletion(("read_source", inventory)));
        }
        responses.Add(protocol == "responses" ? NativeResponseFunction("read_source", arguments) : NativeCompletion(("read_source", arguments)));
        responses.Add(protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var options = WorkspaceOptions(protocol);
        options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 32_000;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.True(WorkspaceUser(factory.Requests[0].Body).GetProperty("nativeObservationCapabilities")
            .GetProperty("nativeObservationReadAvailable").GetBoolean());
        Assert.Equal("raw", gateway.Searches[^1].NativeObservationView);
        Assert.Equal(gateway.RawSha, gateway.Searches[^1].NativeObservationRawSha256);
        if (inventoryFirst)
        {
            var inventory = Assert.Single(WorkspaceUser(factory.Requests[2].Body).GetProperty("alternativeNativeReadings")
                .GetProperty("inventories").EnumerateArray()).GetProperty("inventory");
            Assert.Equal("observed", inventory.GetProperty("availability").GetString());
            Assert.Equal(gateway.RawSha, Assert.Single(inventory.GetProperty("observations").EnumerateArray()).GetProperty("rawTextSha256").GetString());
            Assert.All(gateway.NativeLines, literal => Assert.Equal(0, CountLiteralInJsonStrings(factory.Requests[2].Body, literal)));
        }
        var finalBody = factory.Requests[^1].Body;
        var user = WorkspaceUser(finalBody);
        var native = user.GetProperty("evidence").EnumerateArray()
            .Where(item => item.TryGetProperty("nativeObservation", out _)).ToArray();
        Assert.Equal(2, native.Length);
        Assert.Equal(4, user.GetProperty("evidence").GetArrayLength());
        foreach (var item in native)
        {
            Assert.Equal("secondary_native_audit", item.GetProperty("contentRole").GetString());
            Assert.False(item.GetProperty("contentTruncated").GetBoolean());
            Assert.Empty(item.GetProperty("supportingContentWindows").EnumerateArray());
            Assert.Equal("not_verified", item.GetProperty("sourceQuality").GetProperty("semanticAccuracy").GetString());
            var literal = item.GetProperty("content").GetString()!;
            Assert.Equal(1, CountLiteralInJsonStrings(finalBody, literal));
        }
        Assert.All(user.GetProperty("evidence").EnumerateArray(), item =>
        {
            if (item.TryGetProperty("sourceOverview", out var overview) && overview.ValueKind == JsonValueKind.Object)
                Assert.False(overview.TryGetProperty("nativeObservations", out _));
        });
        var view = Assert.Single(user.GetProperty("alternativeNativeReadings").GetProperty("views").EnumerateArray());
        Assert.Equal("raw_line_ordinal", view.GetProperty("offsetUnit").GetString());
        Assert.False(view.GetProperty("sourceFidelityApproved").GetBoolean());
        foreach (var line in view.GetProperty("lines").EnumerateArray())
        {
            var matching = Assert.Single(native, item => item.GetProperty("evidenceId").GetString() == line.GetProperty("evidenceId").GetString());
            Assert.Equal(matching.GetProperty("nativeObservation").GetProperty("reference").GetProperty("lineId").GetString(), line.GetProperty("lineId").GetString());
            Assert.False(line.TryGetProperty("text", out _));
        }
        using var body = JsonDocument.Parse(finalBody);
        var tool = body.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray()
            .Last(item => protocol == "responses"
                ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : item.TryGetProperty("role", out var role) && role.GetString() == "tool");
        using var output = JsonDocument.Parse(tool.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        Assert.Equal(2, output.RootElement.GetProperty("alternativeNativeReadings").GetProperty("views")[0].GetProperty("lines").GetArrayLength());
        Assert.All(gateway.NativeLines, literal => Assert.Equal(0, CountLiteralInJsonStrings(output.RootElement.GetRawText(), literal)));
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(finalBody), 1, 32_000);
    }

    [Fact]
    public async Task Native_audit_disabled_keeps_legacy_wire_bytes_and_nullable_contract_absent()
    {
        async Task<string[]> Run(bool explicitlyDisabled)
        {
            using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
            var options = WorkspaceOptions();
            if (explicitlyDisabled) options.NativeObservationReadEnabled = false;
            await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.",
                    docId: "11111111-1111-4111-8111-111111111111", revisionId: "22222222-2222-4222-8222-222222222222")), CancellationToken.None);
            return factory.Requests.Select(request => request.Body).ToArray();
        }
        Assert.Equal(await Run(false), await Run(true));
        Assert.DoesNotContain("nativeObservation", string.Join("", await Run(false)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nativeObservation", JsonSerializer.Serialize(new AdvancedAnalysisEvidenceReference(), new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_audit_final_escaped_tool_json_budget_refuses_before_next_http(string protocol)
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(protocol);
        options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 8_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var method = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("CompleteJsonAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var emptyProjection = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildPromptEvidence",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(provider, new object?[] { BuildDirectRequest(), Array.Empty<AdvancedAnalysisResolvedEvidence>(),
                new Dictionary<string, HashSet<string>>(), null });
        var history = new object[] { new { role = "tool", tool_call_id = "native-read-1",
            content = JsonSerializer.Serialize(new { operation = "read_source", nativeReadWindow = new { status = new string('"', 9_000) } }) } };
        var task = (Task)method.Invoke(provider, new object?[] { Guid.NewGuid(), "writer", "Return json.",
            "{\"evidence\":[]}", 128, CancellationToken.None, false, history, emptyProjection })!;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => task);
        Assert.Equal("advanced_native_documentary_wire_budget_exceeded", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_audit_disabled_request_is_rejected_before_any_document_read(string protocol)
    {
        const string arguments = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":20,"nativeObservationView":"inventory"}""";
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_source", arguments) : NativeCompletion(("read_source", arguments)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Single(gateway.Searches);
        Assert.Contains("batch_rejected_before_execution", factory.Requests[^1].Body, StringComparison.Ordinal);
        Assert.False(WorkspaceUser(factory.Requests[1].Body).TryGetProperty("nativeObservationCapabilities", out _));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Native_audit_line_cannot_be_sliced_by_the_primary_read_evidence_tool(string protocol)
    {
        const string arguments = """{"evidenceId":"N0","offset":0,"length":256}""";
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_evidence", arguments) : NativeCompletion(("read_evidence", arguments)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var fixture = new NativeAuditGateway();
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."), fixture.NativeEvidence[0]);
        var options = WorkspaceOptions(protocol);
        options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 32_000;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Single(gateway.Searches);
        Assert.Contains("batch_rejected_before_execution", factory.Requests[^1].Body, StringComparison.Ordinal);
        Assert.Equal(1, CountLiteralInJsonStrings(factory.Requests[^1].Body, fixture.NativeLines[0]));
    }

    private sealed class NativeAuditGateway : IAdvancedAnalysisToolGateway
    {
        private AdvancedAnalysisResolvedEvidence[] _primaries;
        private readonly AdvancedAnalysisResolvedEvidence[] _native;
        private readonly AdvancedAnalysisAlternativeNativeReadings _readings;
        private bool _read;
        public NativeAuditGateway(bool exposeInitialInventory = false)
        {
            var document = new CanonicalDocument
            {
                DocumentId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
                RevisionId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
                Source = new() { Sha256 = new string('a', 64) }, ManifestSha256 = new string('d', 64),
                Pages = [new() { PageNumber = 1, Blocks = [new()
                {
                    BlockId = "native-audit", BlockType = "native_layout_recovery",
                    Text = new() { Raw = "Native’s \"<value>\" \\ -20,5 °C.\r\nSecond 😀 exact line.",
                        RawSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Native’s \"<value>\" \\ -20,5 °C.\r\nSecond 😀 exact line."))).ToLowerInvariant() }
                }] }]
            };
            var window = AdvancedAnalysisNativeObservationWindows.Raw(document, 1, "native-audit", document.Pages[0].Blocks[0].Text.RawSha256, 0, 2, 100_000);
            RawSha = window.RawTextSha256;
            NativeLines = window.Lines.Select(line => line.Text).ToArray();
            _readings = new(document.DocumentId.ToString("D"), document.RevisionId.ToString("D"), document.Source.Sha256, 1, null, window);
            var overview = new AdvancedAnalysisSourceOverview(1, 1, 2);
            _primaries = Enumerable.Range(1, 2).Select(index => new AdvancedAnalysisResolvedEvidence(new()
            {
                EvidenceId = "E" + index, DocId = document.DocumentId.ToString("D"), RevisionId = document.RevisionId.ToString("D"),
                FileName = "menus.pdf", DocPath = "Documents/menus.pdf", SourceHash = document.Source.Sha256,
                PageStart = 1, PageEnd = 1, ChunkId = Guid.NewGuid().ToString("D")
            }, index == 1 ? "Le seuil est 17 bar." : "Another primary.", SourceOverview: overview)).ToArray();
            _native = window.Lines.Select((line, index) => new AdvancedAnalysisResolvedEvidence(new()
            {
                EvidenceId = "N" + index, DocId = document.DocumentId.ToString("D"), RevisionId = document.RevisionId.ToString("D"),
                SourceHash = document.Source.Sha256, FileName = "menus.pdf", DocPath = "Documents/menus.pdf",
                PageStart = 1, PageEnd = 1, NativeObservation = line.Source
            }, line.Text, null)
            {
                NativeObservation = new(line.Source, 2, 0, 2, null, ["canonical_native_audit_literal_not_primary"]),
                SourceOverview = overview, SourceQuality = new(null, null, ["canonical_native_audit_literal_not_primary"])
            }).ToArray();
            if (exposeInitialInventory)
                ExposeInventory();
        }
        public string RawSha { get; }
        public string[] NativeLines { get; }
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> NativeEvidence => _native;
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _read ? _primaries.Concat(_native).ToArray() : _primaries;
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken ct)
        {
            Searches.Add(request);
            if (request.NativeObservationView == "inventory")
            {
                var inventory = ExposeInventory();
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 1, Searches.Count, AlternativeNativeReadings: inventory));
            }
            if (request.Operation == "read_source")
            {
                _read = true;
                _primaries = _primaries.Select(item => item with { SourceOverview = item.SourceOverview! with { NativeObservations = _readings } }).ToArray();
            }
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, _read ? _native : _primaries, [], 1, Searches.Count,
                AlternativeNativeReadings: _read ? _readings : null));
        }
        private AdvancedAnalysisAlternativeNativeReadings ExposeInventory()
        {
            var inventory = _readings with { View = null, Inventory = new("observed", null, 1, 0, null,
                [new(_readings.DocId, _readings.RevisionId, _readings.SourceSha256, new string('d', 64), "native-audit",
                    RawSha, 1, "native_layout_recovery", "PdfPig", "fixture", "native", "fixture", 2, null, [])]) };
            _primaries = _primaries.Select(item => item with { SourceOverview = item.SourceOverview! with { NativeObservations = inventory } }).ToArray();
            return inventory;
        }
    }
}
