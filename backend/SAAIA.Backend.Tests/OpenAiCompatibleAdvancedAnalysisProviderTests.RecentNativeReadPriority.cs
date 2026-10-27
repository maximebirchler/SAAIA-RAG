using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task RecentNativeReadPriority_requested_whole_line_survives_old_windows_at_the_same_budget(string protocol)
    {
        var gateway = new RecentNativeReadGateway();
        string NativeRead(string view) => JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", pageStart = 1, pageEnd = 1, topK = 20,
            nativeObservationView = view, nativeObservationBlockId = view == "raw" ? "priority-native" : null,
            nativeObservationRawSha256 = view == "raw" ? gateway.RawSha : null,
            nativeObservationOffset = 0, nativeObservationLimit = 1
        });
        HttpResponseMessage Call(string name, string arguments) => protocol == "responses"
            ? NativeResponseFunction(name, arguments) : NativeCompletion((name, arguments));
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Call("read_evidence", """{"evidenceId":"E1","offset":3000,"length":256}"""),
            Call("read_source", NativeRead("inventory")), Call("read_source", NativeRead("raw")),
            protocol == "responses" ? NativeResponseAnswer(CandidateTerminal) : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 14_000;
        var run = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        var error = await Record.ExceptionAsync(async () => await run);
        Assert.Equal(1, gateway.RawReadCount);
        Assert.Null(error);
        Assert.Equal("insufficient_documentation", (await run).Outcome);
        Assert.Equal(5, factory.Requests.Count);
        var prompt = WorkspaceUser(factory.Requests[^1].Body);
        var items = prompt.GetProperty("evidence").EnumerateArray().ToArray();
        var native = Assert.Single(items, item => item.GetProperty("evidenceId").GetString() == "N0");
        Assert.Equal(gateway.RawText, native.GetProperty("content").GetString());
        Assert.False(native.GetProperty("contentTruncated").GetBoolean());
        Assert.Equal("N0", items[0].GetProperty("evidenceId").GetString());
        Assert.True(prompt.GetProperty("evidenceProjection").GetProperty("canonicalProjection")
            .GetProperty("omittedByPromptBudgetCount").GetInt32() > 0);
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(factory.Requests[^1].Body), 1, 14_000);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task RecentNativeReadPriority_control_the_same_whole_line_fits_alone_in_the_unchanged_wire_budget(string protocol)
    {
        var source = new RecentNativeReadGateway().NativeEvidence;
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer(CandidateTerminal) : Completion(CandidateTerminal));
        var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 14_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var projection = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildPromptEvidence", flags)!.Invoke(provider,
            new object?[] { BuildDirectRequest(), new[] { source }, new Dictionary<string, HashSet<string>>(), null });
        var prompt = JsonSerializer.Serialize(new { evidence = projection },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await (Task)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("CompleteJsonAsync", flags)!.Invoke(provider,
            new object?[] { Guid.NewGuid(), "writer", "Return JSON.", prompt, 128, CancellationToken.None, false, null, projection })!;
        var body = Assert.Single(factory.Requests).Body;
        var native = Assert.Single(WorkspaceUser(body).GetProperty("evidence").EnumerateArray());
        Assert.Equal(source.Content, native.GetProperty("content").GetString());
        Assert.False(native.GetProperty("contentTruncated").GetBoolean());
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, 14_000);
    }

    [Theory]
    [InlineData("chat-completions", "new_primary_window")]
    [InlineData("responses", "new_primary_window")]
    [InlineData("chat-completions", "same_ids_read_again")]
    [InlineData("responses", "same_ids_read_again")]
    [InlineData("chat-completions", "rejected_raw_batch")]
    [InlineData("responses", "rejected_raw_batch")]
    public async Task RecentNativeReadPriority_follows_actual_read_chronology_and_ignores_rejected_batches(string protocol, string action)
    {
        // Both old and recent excerpts must remain visible for this chronology
        // control; the separate large-metadata case above tests real eviction.
        var gateway = new RecentNativeReadGateway(80, compactStructure: true);
        HttpResponseMessage Call(string name, string arguments) => protocol == "responses"
            ? NativeResponseFunction(name, arguments) : NativeCompletion((name, arguments));
        string NativeRead(string view, string? sha = null, int limit = 1) => JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", pageStart = 1, pageEnd = 1, topK = 20,
            nativeObservationView = view, nativeObservationBlockId = view == "raw" ? "priority-native" : null,
            nativeObservationRawSha256 = view == "raw" ? sha ?? gateway.RawSha : null,
            nativeObservationOffset = 0, nativeObservationLimit = limit
        });
        var responses = new List<HttpResponseMessage>
        {
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Call("read_evidence", """{"evidenceId":"E1","offset":3000,"length":256}"""),
            Call("read_source", NativeRead("inventory")), Call("read_source", NativeRead("raw"))
        };
        if (action == "rejected_raw_batch")
            responses.Add(Call("read_source", NativeRead("raw", new string('0', 64))));
        else
        {
            responses.Add(Call("read_evidence", """{"evidenceId":"E1","offset":6000,"length":256}"""));
            if (action == "same_ids_read_again")
                responses.Add(Call("read_source", NativeRead("raw", limit: 2)));
        }
        responses.Add(protocol == "responses" ? NativeResponseAnswer(CandidateTerminal) : Completion(CandidateTerminal));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 14_000; options.ExternalMaximumCallsPerJob = 9;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        var current = WorkspaceUser(factory.Requests[^1].Body).GetProperty("evidence")[0];
        Assert.Equal(action == "new_primary_window" ? "E1" : "N0", current.GetProperty("evidenceId").GetString());
        if (action == "new_primary_window") Assert.Equal(6_000, current.GetProperty("contentOffset").GetInt32());
        Assert.Equal(action == "same_ids_read_again" ? 2 : 1, gateway.RawReadCount);
        if (action == "rejected_raw_batch")
            Assert.Contains("batch_rejected_before_execution", factory.Requests[^1].Body, StringComparison.Ordinal);
    }

    private sealed class RecentNativeReadGateway : IAdvancedAnalysisToolGateway
    {
        private AdvancedAnalysisResolvedEvidence[] _primary;
        private readonly AdvancedAnalysisResolvedEvidence _native;
        private readonly AdvancedAnalysisAlternativeNativeReadings _raw;
        public RecentNativeReadGateway(int rawCharacters = 2_500, bool compactStructure = false)
        {
            RawText = "Native literal: " + new string('r', rawCharacters) + "\r\n";
            var document = new CanonicalDocument
            {
                DocumentId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
                RevisionId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
                ManifestSha256 = new string('d', 64), Source = new() { Sha256 = new string('a', 64) },
                Pages = [new() { PageNumber = 1, Blocks = [new()
                {
                    BlockId = "priority-native", BlockType = "native_layout_recovery",
                    Text = new() { Raw = RawText, RawSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(RawText))).ToLowerInvariant() }
                }] }]
            };
            var view = AdvancedAnalysisNativeObservationWindows.Raw(document, 1, "priority-native",
                document.Pages[0].Blocks[0].Text.RawSha256, 0, 1, 100_000);
            RawSha = view.RawTextSha256;
            _raw = new(document.DocumentId.ToString("D"), document.RevisionId.ToString("D"), document.Source.Sha256, 1, null, view);
            var structure = new AdvancedAnalysisEvidenceStructure(new string('s', 240), new string('h', 500), "prior", "next");
            _primary = new[] { BuildEvidence("E1", WireTextPrefix(3_000) + new string('x', 6_000), docId: _raw.DocId, revisionId: _raw.RevisionId),
                BuildEvidence("E2", WireTextFact + new string('y', 400), docId: _raw.DocId, revisionId: _raw.RevisionId) }.Select(item => item with
                {
                    SourceStructure = item.Reference.EvidenceId == "E1" && !compactStructure ? structure : null, SourceOverview = new(1, 1, 2)
                }).ToArray();
            var line = view.Lines[0];
            _native = new(new() { EvidenceId = "N0", DocId = _raw.DocId, RevisionId = _raw.RevisionId,
                SourceHash = document.Source.Sha256, FileName = _primary[0].Reference.FileName,
                DocPath = _primary[0].Reference.DocPath, PageStart = 1, PageEnd = 1, NativeObservation = line.Source }, line.Text, null)
            {
                NativeObservation = new(line.Source, 1, 0, 1, null, ["canonical_native_audit_literal_not_primary"]),
                SourceQuality = new(null, null, ["canonical_native_audit_literal_not_primary"])
            };
        }
        public string RawText { get; }
        public string RawSha { get; }
        public AdvancedAnalysisResolvedEvidence NativeEvidence => _native;
        public int RawReadCount { get; private set; }
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => RawReadCount == 0 ? _primary
            : _primary.Append(_native).ToArray();
        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken ct)
        {
            if (request.NativeObservationView == "inventory")
            {
                var inventory = _raw with { View = null, Inventory = new("observed", null, 1, 0, null,
                    [new(_raw.DocId, _raw.RevisionId, _raw.SourceSha256, new string('d', 64), "priority-native", RawSha,
                        1, "native_layout_recovery", "PdfPig", "fixture", "native", "fixture", 1, null, [])]) };
                _primary = _primary.Select(item => item with { SourceOverview = item.SourceOverview! with { NativeObservations = inventory } }).ToArray();
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 1, 1, AlternativeNativeReadings: inventory));
            }
            if (request.NativeObservationView == "raw")
            {
                RawReadCount++;
                _primary = _primary.Select(item => item with { SourceOverview = item.SourceOverview! with { NativeObservations = _raw } }).ToArray();
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [_native], [], 1, 1, AlternativeNativeReadings: _raw));
            }
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, _primary, [], 1, 1));
        }
    }
}
