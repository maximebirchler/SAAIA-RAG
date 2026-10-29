using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
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
    public async Task NativeCataloguePriority_current_four_descriptors_survive_table_catalogue_pressure_at_14000(string protocol)
    {
        var fixture = new NativeCataloguePriorityFixture();
        var canonicalCatalogue = JsonSerializer.Serialize(fixture.Catalogue);
        var originalInventory = JsonSerializer.Serialize(fixture.Inventory, NativeCataloguePriorityJson);
        var previous = new AdvancedAnalysisObservedNativeInventory(7,
            new("unavailable", "no_captured_native_observation", null, 0, null, null));
        var unavailable = fixture.Readings with { PhysicalPage = 7, Inventory = previous.Inventory };
        var current = fixture.Readings with { RetainedInventories = [previous] };

        var first = await CaptureNativeCataloguePriorityAsync(protocol, fixture, unavailable, "inventory");
        Assert.NotEmpty(WorkspaceUser(first).GetProperty("evidence").EnumerateArray());
        var inventoryBody = await CaptureNativeCataloguePriorityAsync(protocol, fixture, current, "inventory");
        var user = WorkspaceUser(inventoryBody);
        Assert.NotEmpty(user.GetProperty("evidence").EnumerateArray());
        AssertNativeCataloguePriorityInventory(user, originalInventory);
        AssertNativeCataloguePriorityTools(inventoryBody, protocol);

        var table = Assert.Single(user.GetProperty("alternativeTableReadings").GetProperty("inventories").EnumerateArray());
        Assert.Equal(8, table.GetProperty("observationCount").GetInt32());
        Assert.Equal(8, table.GetProperty("attemptCount").GetInt32());
        Assert.Equal("partial", table.GetProperty("availability").GetString());
        Assert.Equal(new[] { 17, 18, 19, 20, 177 }, table.GetProperty("pages").EnumerateArray()
            .Select(page => page.GetProperty("pageNumber").GetInt32()));
        Assert.Equal(new[] { 1, 3, 2, 1, 1 }, table.GetProperty("pages").EnumerateArray()
            .Select(page => page.GetProperty("observationCount").GetInt32()));
        Assert.False(table.TryGetProperty("observations", out _)); // Omitted is never known empty.
        Assert.Equal(8, table.GetProperty("observationDescriptorsOmittedCount").GetInt32());
        Assert.Equal(0, table.GetProperty("nextObservationDescriptorOffset").GetInt32());
        Assert.Equal("read_source tableObservationView=inventory on one listed physical page; line offsets must be null",
            table.GetProperty("observationDescriptorRecovery").GetString());
        using (var output = NativeBudgetToolOutput(inventoryBody, protocol))
        {
            var reference = output.RootElement.GetProperty("nativeInventory");
            Assert.True(reference.TryGetProperty("inventoryReference", out _));
            Assert.Equal(18, reference.GetProperty("physicalPage").GetInt32());
            Assert.Equal(4, reference.GetProperty("returnedObservationCount").GetInt32());
        }

        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(inventoryBody), 1, 14_000);
        Assert.Equal(canonicalCatalogue, JsonSerializer.Serialize(fixture.Catalogue));
        Assert.Equal(8, fixture.Catalogue.Observations!.Count);
        Assert.All(fixture.Primary, item => Assert.Equal(new string('x', 880), item.Content));
        Assert.DoesNotContain("secondaryCatalogueDescriptorsOmitted", inventoryBody, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("chat-completions", 14_000, false)]
    [InlineData("responses", 14_000, false)]
    [InlineData("chat-completions", 20_000, true)]
    [InlineData("responses", 20_000, true)]
    [InlineData("chat-completions", 24_000, true)]
    [InlineData("responses", 24_000, true)]
    public async Task NativeCataloguePriority_two_independent_raw_reconstructions_preserve_full_metadata_when_profile_fits(
        string protocol, int maximumDocumentaryCharacters, bool fits)
    {
        var fixture = new NativeCataloguePriorityFixture();
        var originalInventory = JsonSerializer.Serialize(fixture.Inventory, NativeCataloguePriorityJson);
        var canonicalCatalogue = JsonSerializer.Serialize(fixture.Catalogue);
        var current = fixture.Readings with { RetainedInventories = [new(7,
            new("unavailable", "no_captured_native_observation", null, 0, null, null))] };
        // These are two independently reconstructed native turns with the same
        // complete current inventory. Linux qualification tests session continuity.
        foreach (var block in fixture.Document.Pages.Single().Blocks.Take(2))
        {
            var window = AdvancedAnalysisNativeObservationWindows.Raw(fixture.Document, 18,
                block.BlockId, block.Text.RawSha256, 0, 1, 100_000);
            var rawReadings = current with { View = window };
            if (!fits)
            {
                using var factory = new QueuedHttpClientFactory(protocol == "responses"
                    ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
                var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                    CaptureNativeCataloguePriorityAsync(protocol, fixture, rawReadings, "raw", factory,
                        maximumDocumentaryCharacters: maximumDocumentaryCharacters));
                Assert.Equal("advanced_native_documentary_metadata_budget_exceeded", error.ErrorCode);
                Assert.Empty(factory.Requests);
                continue;
            }
            var body = await CaptureNativeCataloguePriorityAsync(protocol, fixture, rawReadings, "raw",
                maximumDocumentaryCharacters: maximumDocumentaryCharacters);
            var rawUser = WorkspaceUser(body);
            AssertNativeCataloguePriorityInventory(rawUser, originalInventory);
            AssertNativeCataloguePriorityTools(body, protocol);
            var line = Assert.Single(rawUser.GetProperty("evidence").EnumerateArray(), item =>
                item.TryGetProperty("nativeObservation", out var native)
                && native.GetProperty("reference").GetProperty("blockId").GetString() == block.BlockId);
            Assert.Equal(window.Lines[0].Text, line.GetProperty("content").GetString());
            Assert.False(line.GetProperty("contentTruncated").GetBoolean());
            Assert.Equal(1, CountLiteralInJsonStrings(body, window.Lines[0].Text));
            var view = Assert.Single(rawUser.GetProperty("alternativeNativeReadings").GetProperty("views").EnumerateArray());
            Assert.Equal(block.BlockId, view.GetProperty("blockId").GetString());
            Assert.Equal(block.Text.RawSha256, view.GetProperty("rawTextSha256").GetString());
            Assert.Equal(JsonSerializer.SerializeToElement(block.Polygon, NativeCataloguePriorityJson).GetRawText(),
                view.GetProperty("observedBlockPolygon").GetRawText());
            Assert.Equal(JsonValueKind.Null, view.GetProperty("nextOffset").ValueKind);
            Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(body), 1, maximumDocumentaryCharacters);
        }
        Assert.Equal(canonicalCatalogue, JsonSerializer.Serialize(fixture.Catalogue));
        Assert.Equal(originalInventory, JsonSerializer.Serialize(fixture.Inventory, NativeCataloguePriorityJson));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task NativeCataloguePriority_requested_raw_precedes_an_older_primary_with_persistent_second_source_key(string protocol)
    {
        var fixture = new NativeCataloguePriorityFixture();
        var block = fixture.Document.Pages.Single().Blocks[0];
        var window = AdvancedAnalysisNativeObservationWindows.Raw(fixture.Document, 18, block.BlockId, block.Text.RawSha256, 0, 1, 100_000);
        var body = await CaptureNativeCataloguePriorityAsync(protocol, fixture, fixture.Readings with { View = window }, "raw",
            maximumDocumentaryCharacters: 64_000, olderPrimaryFirst: true, sourceKey: "internal-source-2");
        var first = WorkspaceUser(body).GetProperty("evidence")[0];
        Assert.Equal(window.Lines[0].Text, first.GetProperty("content").GetString());
        Assert.Equal(block.BlockId, first.GetProperty("nativeObservation").GetProperty("reference").GetProperty("blockId").GetString());
        Assert.Equal("internal-source-2", first.GetProperty("sourceKey").GetString());
        AssertNativeCataloguePriorityTools(body, protocol, "internal-source-2");
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task NativeCataloguePriority_incompressible_current_metadata_refuses_before_any_http(string protocol)
    {
        var fixture = new NativeCataloguePriorityFixture();
        var huge = fixture.Readings with { Inventory = fixture.Inventory with
        {
            Observations = fixture.Inventory.Observations!.Select(item => item with
                { QualitySignals = [new string('q', 20_000)] }).ToArray()
        } };
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            CaptureNativeCataloguePriorityAsync(protocol, fixture, huge, "inventory", factory, maximumHistoryCharacters: 256_000));
        Assert.Equal("advanced_native_documentary_metadata_budget_exceeded", error.ErrorCode);
        Assert.Empty(factory.Requests);
        Assert.Equal(8, fixture.Catalogue.Observations!.Count);
        Assert.All(huge.Inventory!.Observations!, item => Assert.Equal(new string('q', 20_000), Assert.Single(item.QualitySignals)));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task NativeCataloguePriority_a_second_current_source_cannot_silently_lose_its_metadata_after_first_admission(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}"));
        var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 40_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var json = (JsonSerializerOptions)typeof(OpenAiCompatibleAdvancedAnalysisProvider)
            .GetField("JsonOptions", NativeCataloguePriorityFlags)!.GetValue(null)!;
        var signals = Enumerable.Range(0, 64).Select(index => "signal_" + index.ToString("D2") + "_" + new string('q', 150)).ToArray();
        AdvancedAnalysisResolvedEvidence Evidence(string id, string doc, string revision, char hash)
        {
            var readings = new AdvancedAnalysisAlternativeNativeReadings(doc, revision, new string(hash, 64), 18,
                new("unavailable", "no_captured_native_observation", null, 0, null, null), null);
            return new(new() { EvidenceId = id, DocId = doc, RevisionId = revision, SourceHash = readings.SourceSha256,
                FileName = id + ".pdf", DocPath = "Documents/" + id + ".pdf", PageStart = 18, PageEnd = 18, ChunkId = id },
                new string('x', 2400), SourceOverview: new(1, 18, 1) { NativeObservations = readings })
            { SourceQuality = new("ok", false, signals) };
        }
        var evidence = new[]
        {
            Evidence("first", "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222", 'a'),
            Evidence("second", "44444444-4444-4444-8444-444444444444", "55555555-5555-4555-8555-555555555555", 'b')
        };
        var reads = evidence.Select(item => new AdvancedAnalysisSearchRequest("Read " + item.Reference.EvidenceId,
            Operation: "read_source", DocId: item.Reference.DocId, RevisionId: item.Reference.RevisionId,
            PageStart: 18, PageEnd: 18, NativeObservationView: "inventory", NativeObservationOffset: 0, NativeObservationLimit: 4)).ToArray();
        var results = reads.Select((read, index) => new
        {
            key = (string)InvokeNativeCataloguePriority(null, "BuildSearchIdentity", [read]),
            value = new AdvancedAnalysisSearchObservation(read.Query, [], [], 0, 1,
                AlternativeNativeReadings: evidence[index].SourceOverview!.NativeObservations)
        }).ToDictionary(item => item.key, item => item.value);
        var calls = reads.Select((_, index) => new { id = "source-" + index, type = "function", function = new
        {
            name = "read_source", arguments = JsonSerializer.Serialize(new { sourceKey = "internal-source-" + (index + 1),
                pageStart = 18, pageEnd = 18, topK = 1, nativeObservationView = "inventory", nativeObservationOffset = 0, nativeObservationLimit = 4 })
        } }).ToArray();
        var responseOutput = protocol == "responses" ? JsonSerializer.Serialize(calls.Select(call => new
            { type = "function_call", id = "fc-" + call.id, call_id = call.id, call.function.name, call.function.arguments })) : null;
        var turn = Activator.CreateInstance(typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("NativeResearchTurn", NativeCataloguePriorityFlags)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object?[] { JsonSerializer.Serialize(calls), reads, results, null, responseOutput, false, false, false }, null)!;
        var first = InvokeNativeCataloguePriority(provider, "BuildPromptEvidenceWithPersistentSourceKeys",
            [BuildDirectRequest(), evidence.Take(1).ToArray(), new Dictionary<string, HashSet<string>>(), null, null, null, turn]);
        var firstItem = Assert.Single(((System.Collections.IEnumerable)first).Cast<object>());
        var firstCost = (int)InvokeNativeCataloguePriority(provider, "MeasurePromptEvidenceCharacters", [firstItem]);
        var available = 20_000 - (int)InvokeNativeCataloguePriority(provider, "MeasureDocumentaryProjectionWireOverhead", []);
        Assert.InRange(firstCost, available / 2 + 1, available);
        Assert.Equal(2400, JsonSerializer.SerializeToElement(first, json)[0].GetProperty("content").GetString()!.Length);

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(async () =>
        {
            var projection = InvokeNativeCataloguePriority(provider, "BuildPromptEvidenceWithPersistentSourceKeys",
                [BuildDirectRequest(), evidence, new Dictionary<string, HashSet<string>>(), null, null, null, turn]);
            var history = (IReadOnlyList<object>)InvokeNativeCataloguePriority(provider, "BuildNativeToolTurnMessages", [turn, projection, protocol == "responses"]);
            var prompt = JsonSerializer.Serialize(new { evidence = projection }, json);
            await (Task)InvokeNativeCataloguePriority(provider, "CompleteJsonAsync",
                [Guid.NewGuid(), "writer", "Return JSON.", prompt, 16, CancellationToken.None, false, history, projection]);
        });
        Assert.Equal("advanced_native_documentary_metadata_budget_exceeded", error.ErrorCode);
        Assert.Empty(factory.Requests);
        Assert.All(evidence, item => Assert.Equal(signals, item.SourceQuality!.Signals));
    }

    private static readonly JsonSerializerOptions NativeCataloguePriorityJson = new(JsonSerializerDefaults.Web);
    private const BindingFlags NativeCataloguePriorityFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    private static void AssertNativeCataloguePriorityInventory(JsonElement user, string originalInventory)
    {
        var inventories = user.GetProperty("alternativeNativeReadings").GetProperty("inventories").EnumerateArray().ToArray();
        var current = Assert.Single(inventories, item => item.GetProperty("physicalPage").GetInt32() == 18);
        Assert.Equal("internal-source-1", current.GetProperty("sourceKey").GetString());
        Assert.Equal(originalInventory, current.GetProperty("inventory").GetRawText());
        Assert.Equal(4, current.GetProperty("inventory").GetProperty("observations").GetArrayLength());
        var unavailable = Assert.Single(inventories, item => item.GetProperty("physicalPage").GetInt32() == 7).GetProperty("inventory");
        Assert.Equal("unavailable", unavailable.GetProperty("availability").GetString());
        Assert.Equal("no_captured_native_observation", unavailable.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("observationCount").ValueKind);
    }

    private static void AssertNativeCataloguePriorityTools(string body, string protocol, string sourceKey = "internal-source-1")
    {
        using var document = JsonDocument.Parse(body);
        var tools = document.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => protocol == "responses" ? tool : tool.GetProperty("function")).ToArray();
        foreach (var name in new[] { "read_source", "find_source_text" })
        {
            var tool = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == name);
            Assert.Equal(sourceKey, Assert.Single(tool.GetProperty("parameters").GetProperty("properties")
                .GetProperty("sourceKey").GetProperty("enum").EnumerateArray()).GetString());
        }
        var properties = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == "read_source")
            .GetProperty("parameters").GetProperty("properties");
        Assert.True(properties.TryGetProperty("nativeObservationView", out _));
        Assert.True(properties.TryGetProperty("tableObservationView", out _));
    }

    private static object InvokeNativeCataloguePriority(object? target, string method, object?[] arguments)
    {
        try { return typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod(method, NativeCataloguePriorityFlags)!.Invoke(target, arguments)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private async Task<string> CaptureNativeCataloguePriorityAsync(string protocol, NativeCataloguePriorityFixture fixture,
        AdvancedAnalysisAlternativeNativeReadings readings, string view, QueuedHttpClientFactory? suppliedFactory = null,
        int maximumHistoryCharacters = 16_384, int maximumDocumentaryCharacters = 14_000,
        bool olderPrimaryFirst = false, string sourceKey = "internal-source-1")
    {
        using var ownedFactory = suppliedFactory is null ? new QueuedHttpClientFactory(protocol == "responses"
            ? NativeResponseAnswer("{\"ok\":true}") : Completion("{\"ok\":true}")) : null;
        var factory = suppliedFactory ?? ownedFactory!;
        var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
        options.VisualSourceCitationsEnabled = true; options.MaximumEvidencePromptCharacters = maximumDocumentaryCharacters;
        options.NativeResearchMaximumHistoryCharacters = maximumHistoryCharacters;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var json = (JsonSerializerOptions)typeof(OpenAiCompatibleAdvancedAnalysisProvider)
            .GetField("JsonOptions", NativeCataloguePriorityFlags)!.GetValue(null)!;
        var evidence = fixture.Primary.Select(item => item with
            { SourceOverview = item.SourceOverview! with { NativeObservations = readings } }).ToList();
        var returned = Array.Empty<AdvancedAnalysisResolvedEvidence>();
        if (readings.View is { } window)
        {
            returned = window.Lines.Select(line => new AdvancedAnalysisResolvedEvidence(new()
            {
                EvidenceId = "N-" + window.BlockId, DocId = readings.DocId, RevisionId = readings.RevisionId,
                SourceHash = readings.SourceSha256, FileName = "diagnostic.pdf", DocPath = "Documents/diagnostic.pdf",
                PageStart = 18, PageEnd = 18, NativeObservation = line.Source
            }, line.Text)
            {
                SourceOverview = fixture.Primary[0].SourceOverview! with { NativeObservations = readings },
                NativeObservation = new(line.Source, window.RawLineCount, window.Offset, window.Limit, window.NextOffset,
                    ["canonical_native_audit_literal_not_primary"])
                { ObservedBlockPolygon = fixture.Document.Pages.Single().Blocks.Single(block => block.BlockId == window.BlockId).Polygon },
                SourceQuality = new(null, null, ["canonical_native_audit_literal_not_primary"])
            }).ToArray();
            if (olderPrimaryFirst) evidence.AddRange(returned);
            else evidence.InsertRange(0, returned);
        }
        var read = new AdvancedAnalysisSearchRequest("Read current native observations", Operation: "read_source",
            DocId: readings.DocId, RevisionId: readings.RevisionId, DocPath: "Documents/diagnostic.pdf",
            PageStart: readings.PhysicalPage, PageEnd: readings.PhysicalPage, TopK: 1, NativeObservationView: view,
            NativeObservationBlockId: readings.View?.BlockId, NativeObservationRawSha256: readings.View?.RawTextSha256,
            NativeObservationOffset: 0, NativeObservationLimit: view == "inventory" ? 4 : 1);
        var observation = new AdvancedAnalysisSearchObservation(read.Query, returned, [], 0, 1,
            ReadDiagnostic: new("native_observation_" + (view == "raw" ? "observed" : readings.Inventory!.Availability), 1, 177, returned.Length),
            AlternativeNativeReadings: readings);
        var searchIdentity = (string)InvokeNativeCataloguePriority(null, "BuildSearchIdentity", [read]);
        var arguments = JsonSerializer.Serialize(new { sourceKey, pageStart = readings.PhysicalPage,
            pageEnd = readings.PhysicalPage, topK = 1, nativeObservationView = view,
            nativeObservationBlockId = readings.View?.BlockId, nativeObservationRawSha256 = readings.View?.RawTextSha256,
            nativeObservationOffset = 0, nativeObservationLimit = view == "inventory" ? 4 : 1 });
        var calls = JsonSerializer.Serialize(new[] { new { id = "native-priority", type = "function",
            function = new { name = "read_source", arguments } } });
        var responseOutput = protocol == "responses" ? JsonSerializer.Serialize(new[] { new
            { type = "function_call", id = "fc-native-priority", call_id = "native-priority", name = "read_source", arguments } }) : null;
        var turn = Activator.CreateInstance(typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("NativeResearchTurn", NativeCataloguePriorityFlags)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object?[] { calls, new[] { read }, new Dictionary<string, AdvancedAnalysisSearchObservation> { [searchIdentity] = observation },
                null, responseOutput, false, false, false }, null)!;
        Dictionary<string, string>? persistentKeys = null;
        if (sourceKey == "internal-source-2")
        {
            var sourceIdentity = (string)InvokeNativeCataloguePriority(null, "BuildCanonicalSourceIdentity",
                [fixture.Primary[0], fixture.Primary[0].Reference.EvidenceId!]);
            persistentKeys = new() { ["previously observed other source"] = "internal-source-1", [sourceIdentity] = sourceKey };
        }
        var projection = InvokeNativeCataloguePriority(provider, "BuildPromptEvidenceWithPersistentSourceKeys",
            [BuildDirectRequest(), evidence, new Dictionary<string, HashSet<string>>(), null, persistentKeys, null, turn]);
        var history = (IReadOnlyList<object>)InvokeNativeCataloguePriority(provider, "BuildNativeToolTurnMessages", [turn, projection, protocol == "responses"]);
        var prompt = JsonSerializer.Serialize(new { evidence = projection, researchTools = new
            { tools = Array.Empty<object>(), researchAllowed = true, evidenceReadingAllowed = true, maximumQueries = 6, availableCategories = Array.Empty<string>() } }, json);
        await (Task)InvokeNativeCataloguePriority(provider, "CompleteJsonAsync",
            [Guid.NewGuid(), "writer", "Return JSON.", prompt, 16, CancellationToken.None, true, history, projection]);
        return Assert.Single(factory.Requests).Body;
    }

    private sealed class NativeCataloguePriorityFixture
    {
        internal readonly CanonicalDocument Document;
        internal readonly CanonicalTableObservationInventory Catalogue;
        internal readonly AdvancedAnalysisNativeObservationInventory Inventory;
        internal readonly AdvancedAnalysisAlternativeNativeReadings Readings;
        internal readonly AdvancedAnalysisResolvedEvidence[] Primary;

        internal NativeCataloguePriorityFixture()
        {
            var warnings = new[] { "document_intelligence_docling", "canonical_table_sparse_grid", "figure_semantics_not_extracted",
                "canonical_table_native_region_coverage_unavailable", "canonical_native_text_gap_recovered",
                "canonical_native_literal_coverage_unresolved", "canonical_native_pdf_image_inventory", "spatial_anchor_page_only" };
            Document = new() { DocumentId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
                RevisionId = Guid.Parse("22222222-2222-4222-8222-222222222222"), Source = new() { Sha256 = new string('a', 64) },
                ManifestSha256 = new string('d', 64), PageCount = 177,
                Pages = [new() { PageNumber = 18, QualityFlags = warnings.ToList(), Blocks = Enumerable.Range(1, 4).Select(index =>
                {
                    var text = "Native diagnostic " + index + " : exact \"<value>\" \\ 😀.\r\n";
                    return new CanonicalBlock { BlockId = "block_" + index.ToString("x32"), BlockType = "native_text_recovery",
                        Ordinal = index, ReadingOrder = index, Text = new() { Raw = text, RawSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() },
                        Polygon = index <= 2 ? new() { Points = [new() { X = .1, Y = .2 }, new() { X = .3, Y = .2 }, new() { X = .3, Y = .4 }] } : null,
                        Provenance = new() { Engine = "PdfPig", EngineVersion = "0.1.13.0", Method = "native_text_coverage_reconciliation", StageId = "native_text_coverage_reconciliation" } };
                }).ToList() }] };
            Inventory = AdvancedAnalysisNativeObservationWindows.Inventory(Document, 18, 0, 4, 100_000);
            Readings = new(Document.DocumentId.ToString("D"), Document.RevisionId.ToString("D"), Document.Source.Sha256, 18, Inventory, null);
            var pageCounts = new Dictionary<int, int> { [17] = 1, [18] = 3, [19] = 2, [20] = 1, [177] = 1 };
            Catalogue = new(Document.Source.Sha256, "partial", 8, 8,
                Enumerable.Range(1, 177).Select(page => new CanonicalTableObservationPageInventory(page,
                    pageCounts.ContainsKey(page) ? "partial" : "unavailable", pageCounts.GetValueOrDefault(page), pageCounts.GetValueOrDefault(page))).ToArray(),
                Enumerable.Range(1, 8).Select(index => new CanonicalTableObservationDescriptor("table_observation_" + index.ToString("x32"),
                    new[] { 17, 18, 18, 18, 19, 19, 20, 177 }[index - 1], "table_" + index.ToString("x32"), "#/tables/" + index,
                    50, new string('c', 64), "unavailable", null, 50)).ToArray());
            Primary = Enumerable.Range(1, 5).Select(index => new AdvancedAnalysisResolvedEvidence(new()
            {
                EvidenceId = "E" + index, DocId = Readings.DocId, RevisionId = Readings.RevisionId, SourceHash = Readings.SourceSha256,
                FileName = "diagnostic.pdf", DocPath = "Documents/diagnostic.pdf", PageStart = 7, PageEnd = 7,
                ChunkId = Guid.Parse("33333333-3333-4333-8333-" + index.ToString("D12")).ToString("D")
            }, new string('x', 880), SourceOverview: new(1, 177, 753) { TableRegionObservations = Catalogue,
                VisualAssets = new("pixels_available", 13, [1], false, "not_performed", false) })
                { SourceQuality = new("ok", false, warnings) }).ToArray();
        }
    }
}
