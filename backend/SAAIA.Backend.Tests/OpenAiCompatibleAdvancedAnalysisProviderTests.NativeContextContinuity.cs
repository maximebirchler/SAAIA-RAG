using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public void Native_context_inventory_then_raw_keeps_the_other_observed_locator_in_the_actual_writer_payload()
    {
        var fixture = NativeContextFixture();
        var inventory = AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 0, 16, 32_000);
        var initial = fixture.Primary with
        {
            SourceOverview = fixture.Primary.SourceOverview! with
            {
                NativeObservations = NativeContextReadings(fixture, inventory: inventory)
            }
        };
        var raw = AdvancedAnalysisNativeObservationWindows.Raw(fixture.Document, 1, "native-a", fixture.HashA, 0, 16, 32_000);
        var readings = NativeContextReadings(fixture, view: raw);
        var updated = ApplyNativeContextTransition(initial, readings);
        using var payload = NativeContextWriterPayload(updated);
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", pageStart = 1, pageEnd = 1,
            nativeObservationView = "raw", nativeObservationBlockId = "native-b",
            nativeObservationRawSha256 = fixture.HashB, nativeObservationOffset = 0, nativeObservationLimit = 16
        }));
        var guard = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("RequireObservedNativeRawLocator",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var error = Record.Exception(() => guard.Invoke(null, [arguments.RootElement, payload.RootElement]));
        Assert.True(error is null, "Previously rendered block B vanished after reading A: " + (error?.InnerException ?? error)?.Message);
        var descriptors = payload.RootElement.GetProperty("alternativeNativeReadings").GetProperty("inventories")
            .EnumerateArray().SelectMany(item => item.GetProperty("inventory").GetProperty("observations").EnumerateArray()).ToArray();
        Assert.Contains(descriptors, item => item.GetProperty("blockId").GetString() == "native-b");
    }

    [Fact]
    public void Native_context_raw_line_and_writer_view_keep_the_actual_observed_block_polygon()
    {
        var fixture = NativeContextFixture();
        var context = NativeObservationPageReadContext.FromInline(Guid.Parse("33333333-3333-4333-8333-333333333333"), fixture.Document, 1);
        var window = AdvancedAnalysisNativeObservationWindows.Raw(context, "native-a", fixture.HashA, 0, 16, 32_000);
        var stored = new AdvancedAnalysisNativeObservationReader.StoredPage(context, "fixture.pdf", "fixture.pdf");
        var line = stored.Resolve(window.Lines[0].Source, CancellationToken.None);
        var source = stored.Source(line);
        Assert.NotNull(source.ObservedBlockPolygon);
        Assert.Equal(.1, source.ObservedBlockPolygon.Points[0].X);
        Assert.NotSame(fixture.Document.Pages[0].Blocks[0].Polygon, source.ObservedBlockPolygon);
        var polygon = JsonSerializer.SerializeToElement(source.ObservedBlockPolygon, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var native = new AdvancedAnalysisResolvedEvidence(new()
        {
            EvidenceId = "N1", DocId = fixture.Primary.Reference.DocId, RevisionId = fixture.Primary.Reference.RevisionId,
            SourceHash = fixture.Primary.Reference.SourceHash, PageStart = 1, PageEnd = 1,
            NativeObservation = line.Source
        }, line.Text) { NativeObservation = source };
        using var payload = NativeContextWriterPayload(fixture.Primary, native);
        var view = Assert.Single(payload.RootElement.GetProperty("alternativeNativeReadings").GetProperty("views").EnumerateArray());
        Assert.True(view.TryGetProperty("observedBlockPolygon", out var visible));
        Assert.Equal(polygon.GetRawText(), visible.GetRawText());
        Assert.Equal(CanonicalSchema.NormalizedTopLeftCoordinateSpace, view.GetProperty("coordinateSpace").GetString());
        Assert.Contains("whole_observed_block", view.GetProperty("polygonScope").GetString());
        var renderedNative = payload.RootElement.GetProperty("evidence").EnumerateArray()
            .Single(item => item.GetProperty("evidenceId").GetString() == "N1");
        Assert.False(renderedNative.GetProperty("nativeObservation").TryGetProperty("observedBlockPolygon", out _));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("tenant")]
    [InlineData("manifest")]
    public void Native_context_foreign_previous_identity_never_grants_a_raw_locator(string changed)
    {
        var fixture = NativeContextFixture();
        var prior = NativeContextReadings(fixture,
            inventory: AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 0, 16, 32_000));
        prior = changed switch
        {
            "document" => prior with { DocId = Guid.NewGuid().ToString("D") },
            "revision" => prior with { RevisionId = Guid.NewGuid().ToString("D") },
            "source" => prior with { SourceSha256 = new string('c', 64) },
            "tenant" => prior with { TenantId = Guid.NewGuid() },
            _ => prior with { ManifestSha256 = new string('c', 64) }
        };
        var evidence = fixture.Primary with { SourceOverview = fixture.Primary.SourceOverview! with { NativeObservations = prior } };
        var raw = AdvancedAnalysisNativeObservationWindows.Raw(fixture.Document, 1, "native-a", fixture.HashA, 0, 16, 32_000);
        var updated = ApplyNativeContextTransition(evidence, NativeContextReadings(fixture, view: raw));
        Assert.Empty(AdvancedAnalysisNativeInventoryContext.Windows(updated.SourceOverview!.NativeObservations!));
        using var payload = NativeContextWriterPayload(updated);
        Assert.Equal("advanced_native_raw_locator_not_observed", NativeContextLocatorError(payload, fixture.HashB, "native-b", 1));
    }

    [Theory]
    [InlineData("page")]
    [InlineData("manifest")]
    [InlineData("document")]
    [InlineData("revision")]
    [InlineData("source")]
    public void Native_context_inconsistent_descriptor_identity_is_refused(string changed)
    {
        var fixture = NativeContextFixture();
        var inventory = AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 0, 16, 32_000);
        var descriptor = inventory.Observations![0];
        descriptor = changed switch
        {
            "page" => descriptor with { PhysicalPage = 2 },
            "manifest" => descriptor with { ManifestSha256 = new string('c', 64) },
            "revision" => descriptor with { RevisionId = Guid.NewGuid().ToString("D") },
            "source" => descriptor with { SourceSha256 = new string('c', 64) },
            _ => descriptor with { DocId = Guid.NewGuid().ToString("D") }
        };
        var readings = NativeContextReadings(fixture, inventory: inventory with { Observations = [descriptor] });
        Assert.Equal("native_inventory_context_identity_invalid", Assert.Throws<AdvancedAnalysisToolException>(() =>
            AdvancedAnalysisNativeInventoryContext.Preserve(readings, [], NativeContextTenant, 14_000)).Message);
    }

    [Fact]
    public void Native_context_overlapping_windows_are_kept_with_distinct_returned_counts()
    {
        var fixture = NativeContextFixture();
        var full = NativeContextReadings(fixture, inventory:
            AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 0, 16, 14_000));
        var evidence = ApplyNativeContextTransition(fixture.Primary, full);
        var prefix = full with { Inventory = AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 0, 1, 14_000) };
        evidence = ApplyNativeContextTransition(evidence, prefix);
        using var payload = NativeContextWriterPayload(14_000, evidence);
        var inventories = payload.RootElement.GetProperty("alternativeNativeReadings").GetProperty("inventories").EnumerateArray().ToArray();
        Assert.Equal(new[] { 2, 1 }, inventories.Select(item => item.GetProperty("inventory").GetProperty("observations").GetArrayLength()));
        Assert.Null(NativeContextLocatorError(payload, fixture.HashB, "native-b", 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_context_current_missing_or_foreign_tenant_is_refused(bool missing)
    {
        var fixture = NativeContextFixture();
        var current = NativeContextReadings(fixture) with { TenantId = missing ? Guid.Empty : Guid.NewGuid() };
        Assert.Equal("native_inventory_context_identity_invalid", Assert.Throws<AdvancedAnalysisToolException>(() =>
            AdvancedAnalysisNativeInventoryContext.Preserve(current, [], NativeContextTenant, 14_000)).Message);
    }

    [Fact]
    public void Native_context_pagination_and_pages_remain_distinct_without_inventing_locators()
    {
        var fixture = NativeContextFixture();
        fixture.Document.PageCount = 2;
        fixture.Document.Pages.Add(new() { PageNumber = 2, Blocks = [] });
        var first = NativeContextReadings(fixture, inventory:
            AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 0, 1, 14_000));
        var evidence = ApplyNativeContextTransition(fixture.Primary, first);
        var second = NativeContextReadings(fixture, inventory:
            AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 1, 1, 14_000));
        evidence = ApplyNativeContextTransition(evidence, second);
        var pageTwo = NativeContextReadings(fixture, inventory:
            AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 2, 0, 1, 14_000)) with { PhysicalPage = 2 };
        evidence = ApplyNativeContextTransition(evidence, pageTwo);
        using var payload = NativeContextWriterPayload(14_000, evidence);
        var inventories = payload.RootElement.GetProperty("alternativeNativeReadings").GetProperty("inventories").EnumerateArray().ToArray();
        Assert.Equal(3, inventories.Length);
        Assert.Equal(new[] { 0, 1 }, inventories.Where(item => item.GetProperty("physicalPage").GetInt32() == 1)
            .Select(item => item.GetProperty("inventory").GetProperty("offset").GetInt32()));
        Assert.Null(NativeContextLocatorError(payload, fixture.HashB, "native-b", 1));
        Assert.Equal("advanced_native_raw_locator_not_observed", NativeContextLocatorError(payload, fixture.HashB, "native-b", 2));
    }

    [Fact]
    public void Native_context_budget_evicts_whole_old_windows_and_announces_their_omission()
    {
        var fixture = NativeContextFixture();
        var first = NativeContextReadings(fixture, inventory:
            AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 0, 1, 14_000));
        var prior = ApplyNativeContextTransition(fixture.Primary, first);
        var second = NativeContextReadings(fixture, inventory:
            AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 1, 1, 14_000));
        var oneWindowCharacters = JsonSerializer.Serialize(second, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
        var bounded = AdvancedAnalysisNativeInventoryContext.Preserve(second, [prior], NativeContextTenant, oneWindowCharacters + 150);
        Assert.Equal(1, bounded.InventoryWindowsOmitted);
        Assert.Null(bounded.RetainedInventories);
        var updated = AttachNativeContext(prior, bounded);
        using var payload = NativeContextWriterPayload(14_000, updated);
        var alternative = payload.RootElement.GetProperty("alternativeNativeReadings");
        Assert.Equal(1, Assert.Single(alternative.GetProperty("inventoryOmissions").EnumerateArray()).GetProperty("omittedWindowCount").GetInt32());
        Assert.Single(alternative.GetProperty("inventories").EnumerateArray());
        Assert.Equal("advanced_native_raw_locator_not_observed", NativeContextLocatorError(payload, fixture.HashA, "native-a", 1));
        Assert.Null(NativeContextLocatorError(payload, fixture.HashB, "native-b", 1));
    }

    [Fact]
    public void Native_context_repeated_excerpts_do_not_evict_exact_raw_lines_at_fourteen_thousand_characters()
    {
        var fixture = NativeContextFixture();
        var inventory = NativeContextReadings(fixture, inventory:
            AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 0, 16, 14_000));
        var primary = ApplyNativeContextTransition(fixture.Primary, inventory);
        var evidence = new List<AdvancedAnalysisResolvedEvidence>();
        var stored = new AdvancedAnalysisNativeObservationReader.StoredDocument(fixture.Document, "fixture.pdf", "fixture.pdf");
        foreach (var block in fixture.Document.Pages[0].Blocks)
        {
            var window = AdvancedAnalysisNativeObservationWindows.Raw(fixture.Document, 1, block.BlockId, block.Text.RawSha256, 0, 16, 14_000);
            foreach (var line in window.Lines)
            {
                var native = NativeContextEvidence(fixture, stored.Source(stored.Resolve(line.Source, CancellationToken.None)), line.Text);
                evidence.Add(AttachNativeContext(native, inventory));
            }
        }
        evidence.AddRange(Enumerable.Range(0, 30).Select(index => primary with
        {
            Reference = new() { EvidenceId = "E" + index, ChunkId = Guid.NewGuid().ToString("D"),
                DocId = primary.Reference.DocId, RevisionId = primary.Reference.RevisionId,
                SourceHash = primary.Reference.SourceHash, PageStart = 1, PageEnd = 1,
                DocPath = primary.Reference.DocPath, FileName = primary.Reference.FileName }
        }));
        using var payload = NativeContextWriterPayload(14_000, evidence.ToArray());
        var visible = payload.RootElement.GetProperty("evidence").EnumerateArray().ToArray();
        var natives = visible.Where(item => item.TryGetProperty("nativeObservation", out var n) && n.ValueKind == JsonValueKind.Object).ToArray();
        Assert.Equal(2, natives.Length);
        Assert.All(natives, item => Assert.False(item.GetProperty("contentTruncated").GetBoolean()));
        foreach (var block in fixture.Document.Pages[0].Blocks)
            Assert.Equal(1, CountLiteralInJsonStrings(payload.RootElement.GetRawText(), block.Text.Raw));
        Assert.Single(payload.RootElement.GetProperty("alternativeNativeReadings").GetProperty("inventories").EnumerateArray());
        Assert.Equal(2, payload.RootElement.GetProperty("alternativeNativeReadings").GetProperty("views").GetArrayLength());
        Assert.Null(NativeContextLocatorError(payload, fixture.HashB, "native-b", 1));
        Assert.InRange(NativeContextWireCharacters(payload), 1, 14_000);
    }

    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task Native_context_inventory_raw_a_raw_b_remain_visible_on_the_actual_fourteen_thousand_wire(string protocol, bool sourceCatalogue)
    {
        var gateway = new NativeContextGateway(sourceCatalogue);
        string Arguments(string view, string? block = null, string? hash = null) => JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", pageStart = 1, pageEnd = 1, topK = 20,
            nativeObservationView = view, nativeObservationBlockId = block, nativeObservationRawSha256 = hash,
            nativeObservationOffset = 0, nativeObservationLimit = 16
        });
        HttpResponseMessage Tool(string args) => protocol == "responses"
            ? NativeResponseFunction("read_source", args) : NativeCompletion(("read_source", args));
        var terminal = sourceCatalogue ? CandidateTerminal : NativeAnswered;
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Tool(Arguments("inventory")), Tool(Arguments("raw", "native-a", gateway.HashA)),
            Tool(Arguments("raw", "native-b", gateway.HashB)),
            protocol == "responses" ? NativeResponseAnswer(terminal) : Completion(terminal));
        var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 14_000;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal(sourceCatalogue ? "insufficient_documentation" : "answered", result.Outcome);
        Assert.Equal(new[] { "inventory", "raw", "raw" }, gateway.Searches.Where(request => request.NativeObservationView is not null)
            .Select(request => request.NativeObservationView));
        Assert.Equal(5, factory.Requests.Count);
        var afterA = WorkspaceUser(factory.Requests[3].Body);
        using var afterADocument = JsonDocument.Parse(afterA.GetRawText());
        Assert.Null(NativeContextLocatorError(afterADocument, gateway.HashB, "native-b", 1));
        var final = WorkspaceUser(factory.Requests[^1].Body);
        Assert.Single(final.GetProperty("alternativeNativeReadings").GetProperty("inventories").EnumerateArray());
        var views = final.GetProperty("alternativeNativeReadings").GetProperty("views").EnumerateArray().ToArray();
        Assert.Equal(2, views.Length);
        Assert.All(views, view => Assert.Equal(CanonicalSchema.NormalizedTopLeftCoordinateSpace, view.GetProperty("coordinateSpace").GetString()));
        Assert.All(gateway.Literals, literal => Assert.Equal(1, CountLiteralInJsonStrings(factory.Requests[^1].Body, literal)));
        Assert.All(factory.Requests, request => Assert.InRange(
            OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(request.Body), 0, 14_000));
    }

    private static int NativeContextWireCharacters(JsonDocument payload)
        => OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(JsonSerializer.Serialize(new
        { messages = new[] { new { role = "user", content = payload.RootElement.GetRawText() } } }));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Native_context_probe_reports_retention_eviction_and_actual_resolved_polygon_without_content(bool evict, bool hasPolygon)
    {
        var fixture = NativeContextFixture();
        var first = NativeContextReadings(fixture, inventory:
            AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 0, 1, 14_000));
        var prior = ApplyNativeContextTransition(fixture.Primary, first);
        var second = NativeContextReadings(fixture, inventory:
            AdvancedAnalysisNativeObservationWindows.Inventory(fixture.Document, 1, 1, 1, 14_000));
        var ownCharacters = JsonSerializer.Serialize(second, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
        var admitted = AdvancedAnalysisNativeInventoryContext.Preserve(second, [prior], NativeContextTenant,
            evict ? ownCharacters + 150 : 14_000);
        var raw = AdvancedAnalysisNativeObservationWindows.Raw(fixture.Document, 1, "native-a", fixture.HashA, 0, 16, 14_000);
        var stored = new AdvancedAnalysisNativeObservationReader.StoredDocument(fixture.Document, "fixture.pdf", "fixture.pdf");
        var source = stored.Source(stored.Resolve(raw.Lines[0].Source, CancellationToken.None));
        if (!hasPolygon) source = source with { ObservedBlockPolygon = null };
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProviderTests>();
        using (var probe = BackendDiagnostics.Start("advanced.research_tool", logger))
        {
            AdvancedAnalysisNativeInventoryContext.RecordDiagnostics(probe, admitted, [NativeContextEvidence(fixture, source, raw.Lines[0].Text)]);
            probe.Complete();
        }
        var entry = Assert.Single(logger.Events);
        using var measurements = JsonDocument.Parse((string)entry.Values["Measurements"]!);
        Assert.Equal(1, measurements.RootElement.GetProperty("native_inventory_windows_current").GetInt32());
        Assert.Equal(evict ? 0 : 1, measurements.RootElement.GetProperty("native_inventory_windows_retained").GetInt32());
        Assert.Equal(evict ? 1 : 0, measurements.RootElement.GetProperty("native_inventory_windows_omitted").GetInt32());
        Assert.Equal(hasPolygon, measurements.RootElement.GetProperty("native_block_polygon_present").GetBoolean());
        Assert.DoesNotContain(fixture.Primary.Reference.DocId, entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(raw.Lines[0].Text.Trim(), entry.Text, StringComparison.Ordinal);
    }

    private sealed class NativeContextGateway : IAdvancedAnalysisToolGateway
    {
        private readonly (CanonicalDocument Document, AdvancedAnalysisResolvedEvidence Primary, string HashA, string HashB) fixture = NativeContextFixture();
        private readonly List<AdvancedAnalysisResolvedEvidence> evidence = [];
        public NativeContextGateway(bool sourceCatalogue = false) => evidence.Add(fixture.Primary with
        {
            Content = "Le seuil est 17 bar.",
            SourceOverview = fixture.Primary.SourceOverview! with
            {
                TableRegionObservations = sourceCatalogue ? LargeNativeBudgetCatalog() : null
            }
        });
        public string HashA => fixture.HashA;
        public string HashB => fixture.HashB;
        public IEnumerable<string> Literals => fixture.Document.Pages[0].Blocks.Select(block => block.Text.Raw);
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => evidence;
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken ct)
        {
            Searches.Add(request);
            if (request.NativeObservationView is null)
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, evidence.ToArray(), [], 1, Searches.Count));
            AdvancedAnalysisAlternativeNativeReadings readings;
            var resolved = new List<AdvancedAnalysisResolvedEvidence>();
            if (request.NativeObservationView == "inventory")
                readings = NativeContextReadings(fixture, inventory: AdvancedAnalysisNativeObservationWindows.Inventory(
                    fixture.Document, 1, request.NativeObservationOffset ?? 0, request.NativeObservationLimit ?? 16, 14_000));
            else
            {
                var window = AdvancedAnalysisNativeObservationWindows.Raw(fixture.Document, 1, request.NativeObservationBlockId!,
                    request.NativeObservationRawSha256!, request.NativeObservationOffset ?? 0, request.NativeObservationLimit ?? 16, 14_000);
                readings = NativeContextReadings(fixture, view: window);
                var stored = new AdvancedAnalysisNativeObservationReader.StoredDocument(fixture.Document, "fixture.pdf", "fixture.pdf");
                foreach (var line in window.Lines)
                    resolved.Add(NativeContextEvidence(fixture, stored.Source(stored.Resolve(line.Source, ct)), line.Text));
            }
            readings = AdvancedAnalysisNativeInventoryContext.Preserve(readings, evidence, NativeContextTenant, 14_000);
            for (var index = 0; index < evidence.Count; index++)
                evidence[index] = AttachNativeContext(evidence[index], readings);
            var admitted = resolved.Select(item => AttachNativeContext(item, readings)).ToArray();
            evidence.AddRange(admitted);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, admitted, [], 1, Searches.Count,
                AlternativeNativeReadings: readings));
        }
    }

    private static readonly Guid NativeContextTenant = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static AdvancedAnalysisAlternativeNativeReadings NativeContextReadings(
        (CanonicalDocument Document, AdvancedAnalysisResolvedEvidence Primary, string HashA, string HashB) fixture,
        AdvancedAnalysisNativeObservationInventory? inventory = null, AdvancedAnalysisNativeObservationWindow? view = null)
        => new(fixture.Primary.Reference.DocId, fixture.Primary.Reference.RevisionId!, fixture.Primary.Reference.SourceHash, 1, inventory, view)
        { TenantId = NativeContextTenant, ManifestSha256 = fixture.Document.ManifestSha256 };

    private static AdvancedAnalysisResolvedEvidence NativeContextEvidence(
        (CanonicalDocument Document, AdvancedAnalysisResolvedEvidence Primary, string HashA, string HashB) fixture,
        AdvancedAnalysisNativeObservationLineSource source, string text)
        => new(new() { EvidenceId = "N-" + source.Reference.BlockId,
            DocId = fixture.Primary.Reference.DocId, RevisionId = fixture.Primary.Reference.RevisionId,
            SourceHash = fixture.Primary.Reference.SourceHash, PageStart = source.Reference.PhysicalPage,
            PageEnd = source.Reference.PhysicalPage, NativeObservation = source.Reference,
            DocPath = fixture.Primary.Reference.DocPath, FileName = fixture.Primary.Reference.FileName }, text)
        { NativeObservation = source };

    private static string? NativeContextLocatorError(JsonDocument payload, string hash, string block, int page)
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { sourceKey = "internal-source-1",
            pageStart = page, pageEnd = page, nativeObservationView = "raw", nativeObservationBlockId = block,
            nativeObservationRawSha256 = hash, nativeObservationOffset = 0, nativeObservationLimit = 16 }));
        try
        {
            typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("RequireObservedNativeRawLocator", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [arguments.RootElement, payload.RootElement]);
            return null;
        }
        catch (TargetInvocationException error) { return error.InnerException!.Message; }
    }

    private static AdvancedAnalysisResolvedEvidence ApplyNativeContextTransition(
        AdvancedAnalysisResolvedEvidence evidence, AdvancedAnalysisAlternativeNativeReadings readings)
        => AttachNativeContext(evidence, AdvancedAnalysisNativeInventoryContext.Preserve(readings, [evidence], NativeContextTenant, 14_000));

    private static AdvancedAnalysisResolvedEvidence AttachNativeContext(
        AdvancedAnalysisResolvedEvidence evidence, AdvancedAnalysisAlternativeNativeReadings readings)
        => (AdvancedAnalysisResolvedEvidence)typeof(AdvancedAnalysisToolGateway)
            .GetMethod("AttachNativeRead", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [evidence, readings])!;

    private static JsonDocument NativeContextWriterPayload(params AdvancedAnalysisResolvedEvidence[] evidence)
        => NativeContextWriterPayload(32_000, evidence);

    private static JsonDocument NativeContextWriterPayload(int maximumCharacters, params AdvancedAnalysisResolvedEvidence[] evidence)
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = maximumCharacters;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var request = BuildDirectRequest();
        var projected = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildPromptEvidence", flags)!
            .Invoke(provider, [request, evidence, new Dictionary<string, HashSet<string>>(), null]);
        var prompt = (string)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("BuildWriterUserPrompt", flags)!
            .Invoke(provider, [request, projected])!;
        return JsonDocument.Parse((string)typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetMethod("AddNativeObservationsToPrompt", flags)!
            .Invoke(provider, [prompt])!);
    }

    private static (CanonicalDocument Document, AdvancedAnalysisResolvedEvidence Primary, string HashA, string HashB) NativeContextFixture()
    {
        var source = new string('a', 64); var manifest = new string('b', 64);
        string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        CanonicalPolygon Polygon(double x) => new() { Points = [new() { X = x, Y = .1 }, new() { X = x + .2, Y = .1 }, new() { X = x + .2, Y = .3 }] };
        CanonicalBlock Block(string id, string text, int ordinal, double x) => new()
        {
            BlockId = id, BlockType = "native_layout_recovery", Ordinal = ordinal, ReadingOrder = ordinal,
            Text = new() { Raw = text, RawSha256 = Hash(text), Canonical = text }, Polygon = Polygon(x),
            Provenance = new() { Engine = "fixture", EngineVersion = "1", Method = "native", StageId = "native-fixture" }
        };
        var document = new CanonicalDocument
        {
            DocumentId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            RevisionId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Source = new() { Sha256 = source }, ManifestSha256 = manifest, PageCount = 1,
            Pages = [new() { PageNumber = 1, Blocks = [Block("native-a", "First native line.\n", 0, .1), Block("native-b", "Second native line.\n", 1, .6)] }]
        };
        var primary = new AdvancedAnalysisResolvedEvidence(new()
        {
            EvidenceId = "E1", DocId = document.DocumentId.ToString("D"), RevisionId = document.RevisionId.ToString("D"),
            DocPath = "fixture.pdf", FileName = "fixture.pdf", SourceHash = source,
            ChunkId = "44444444-4444-4444-8444-444444444444", PageStart = 1, PageEnd = 1
        }, "Primary documented context.", SourceOverview: new(1, 1, 1));
        return (document, primary, Hash(document.Pages[0].Blocks[0].Text.Raw), Hash(document.Pages[0].Blocks[1].Text.Raw));
    }
}
