using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task InitialTableInventory_primary_summary_recovers_explicit_inventory_then_whole_raw_windows(string protocol)
    {
        const string inventory = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":20,"tableObservationView":"inventory","tableObservationId":null,"tableObservationBandOrdinal":null,"tableObservationLineOffset":null,"tableObservationLineLimit":null}""";
        const string raw0 = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":20,"tableObservationView":"raw","tableObservationId":"observation-1","tableObservationBandOrdinal":null,"tableObservationLineOffset":0,"tableObservationLineLimit":1}""";
        const string raw1 = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":1,"topK":20,"tableObservationView":"raw","tableObservationId":"observation-1","tableObservationBandOrdinal":null,"tableObservationLineOffset":1,"tableObservationLineLimit":1}""";
        var gateway = new InitialTableInventoryGateway();
        var original = JsonSerializer.Serialize(gateway.Catalogue);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Function(inventory), Function(raw0), Function(raw1),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var options = WorkspaceOptions(protocol); options.ExternalMaximumCallsPerJob = 6;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(5, result.ProviderCallCount);
        Assert.Equal(4, gateway.Searches.Count);
        var first = WorkspaceUser(factory.Requests[1].Body);
        var summary = Assert.Single(first.GetProperty("alternativeTableReadings").GetProperty("inventories").EnumerateArray());
        Assert.False(summary.TryGetProperty("observations", out _));
        Assert.Equal(1, summary.GetProperty("observationDescriptorsOmittedCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("nextObservationDescriptorOffset").GetInt32());
        Assert.Equal("read_source tableObservationView=inventory on one listed physical page; line offsets must be null",
            summary.GetProperty("observationDescriptorRecovery").GetString());
        Assert.Equal("observed", summary.GetProperty("availability").GetString());
        Assert.Equal(1, summary.GetProperty("observationCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("attemptCount").GetInt32());
        Assert.Equal(1, Assert.Single(summary.GetProperty("pages").EnumerateArray()).GetProperty("pageNumber").GetInt32());
        Assert.DoesNotContain("observation-1", factory.Requests[1].Body);

        using var inventoryOutput = InitialTableToolOutput(factory.Requests[2].Body, protocol);
        var full = inventoryOutput.RootElement.GetProperty("secondaryInventory");
        var expected = JsonSerializer.SerializeToElement(gateway.Catalogue, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expected.GetRawText(), full.GetRawText());
        var descriptor = Assert.Single(full.GetProperty("observations").EnumerateArray());
        Assert.Equal("observation-1", descriptor.GetProperty("observationId").GetString());
        Assert.Equal(new string('c', 64), descriptor.GetProperty("rawObservationSha256").GetString());
        Assert.Equal(2, descriptor.GetProperty("lineCount").GetInt32());
        Assert.Equal("unavailable", descriptor.GetProperty("physicalReadingAvailability").GetString());
        Assert.Null(gateway.Searches[1].TableObservationLineOffset);
        Assert.Null(gateway.Searches[1].TableObservationLineLimit);

        for (var offset = 0; offset < 2; offset++)
        {
            var current = WorkspaceUser(factory.Requests[offset + 3].Body);
            var raw = current.GetProperty("evidence").EnumerateArray()
                .Single(item => item.GetProperty("evidenceId").GetString() == $"E{offset + 2}");
            Assert.Equal(gateway.Lines[offset].Content, raw.GetProperty("content").GetString());
            Assert.False(raw.GetProperty("contentTruncated").GetBoolean());
            Assert.Equal(gateway.Lines[offset].Content.Length, raw.GetProperty("originalContentLength").GetInt32());
            Assert.Equal("not_verified", raw.GetProperty("sourceQuality").GetProperty("semanticAccuracy").GetString());
            Assert.Equal(gateway.Lines[offset].SourceQuality!.Signals, raw.GetProperty("sourceQuality").GetProperty("signals").EnumerateArray().Select(value => value.GetString()));
            var source = raw.GetProperty("sourceObservation");
            Assert.Equal(offset, source.GetProperty("lineOffset").GetInt32());
            Assert.Equal(gateway.Lines[offset].SourceObservation!.TextSha256, source.GetProperty("textSha256").GetString());
            var alternatives = current.GetProperty("alternativeTableReadings");
            var expanded = Assert.Single(alternatives.GetProperty("inventories").EnumerateArray());
            Assert.Equal("observation-1", Assert.Single(expanded.GetProperty("observations").EnumerateArray()).GetProperty("observationId").GetString());
            Assert.False(expanded.TryGetProperty("observationDescriptorsOmittedCount", out _));
            var view = alternatives.GetProperty("views").EnumerateArray().Single(value => value.GetProperty("lineOffset").GetInt32() == offset);
            Assert.Equal(1, view.GetProperty("returnedLineCount").GetInt32());
            Assert.False(view.GetProperty("pdfCoverageVerified").GetBoolean());
            Assert.False(view.GetProperty("canonicalGridRepaired").GetBoolean());
            if (offset == 0) Assert.Equal(1, view.GetProperty("nextLineOffset").GetInt32());
            else Assert.Equal(JsonValueKind.Null, view.GetProperty("nextLineOffset").ValueKind);
        }
        Assert.Equal(original, JsonSerializer.Serialize(gateway.Catalogue));

        HttpResponseMessage Function(string arguments)
            => protocol == "responses" ? NativeResponseFunction("read_source", arguments) : NativeCompletion(("read_source", arguments));
    }

    private static JsonDocument InitialTableToolOutput(string body, string protocol)
    {
        using var wire = JsonDocument.Parse(body);
        var tool = wire.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray()
            .Single(item => protocol == "responses"
                ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : item.TryGetProperty("role", out var role) && role.GetString() == "tool");
        return JsonDocument.Parse(tool.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
    }

    private sealed class InitialTableInventoryGateway : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public AdvancedAnalysisToolBudget? Budget => new(32, Searches.Count, 60_000, Searches.Count);
        public CanonicalTableObservationInventory Catalogue { get; }
        public AdvancedAnalysisResolvedEvidence[] Lines { get; }
        private readonly AdvancedAnalysisResolvedEvidence _primary;

        public InitialTableInventoryGateway()
        {
            var first = SecondaryLineFixture("Le seuil est 17 bar. Ligne entière -5,25 °C.", "E2");
            Catalogue = first.SourceOverview!.TableRegionObservations! with
            { Observations = [first.SourceOverview.TableRegionObservations!.Observations![0] with { LineCount = 2 }] };
            Lines = new[] { first, SecondaryLineFixture("Seconde ligne entière : ±0,125 mm et fin exacte.", "E3") }
                .Select((item, offset) => item with
                {
                    Reference = new AdvancedAnalysisResultEvidence
                    {
                        EvidenceId = "E" + (offset + 2), DocId = first.Reference.DocId,
                        RevisionId = first.Reference.RevisionId, SourceHash = first.Reference.SourceHash,
                        DocPath = first.Reference.DocPath, FileName = first.Reference.FileName,
                        PageStart = 1, PageEnd = 1, AnchorId = "table-observation-anchor-" + offset
                    },
                    SourceOverview = item.SourceOverview! with { TableRegionObservations = Catalogue },
                    SourceObservation = item.SourceObservation! with
                    {
                        LineId = "line-" + (offset + 1), RawOrdinal = offset, ViewOrdinal = offset,
                        ViewLineCount = 2, LineOffset = offset, NextLineOffset = offset == 0 ? 1 : null
                    }
                }).ToArray();
            _primary = first with
            {
                Reference = new AdvancedAnalysisResultEvidence
                {
                    EvidenceId = "E1", DocId = first.Reference.DocId, RevisionId = first.Reference.RevisionId,
                    SourceHash = first.Reference.SourceHash, DocPath = first.Reference.DocPath,
                    FileName = first.Reference.FileName, PageStart = 1, PageEnd = 1
                }, SourceObservation = null,
                SourceOverview = first.SourceOverview with { TableRegionObservations = Catalogue }
            };
        }

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Searches.Add(request);
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> added = Searches.Count == 1 ? [_primary]
                : request.TableObservationView == "raw" ? [Lines[request.TableObservationLineOffset!.Value]] : [];
            _evidence.AddRange(added);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, added, [], 1, Searches.Count,
                AlternativeTableReadings: request.TableObservationView == "inventory" ? new(Catalogue, null) : null));
        }
    }
}
