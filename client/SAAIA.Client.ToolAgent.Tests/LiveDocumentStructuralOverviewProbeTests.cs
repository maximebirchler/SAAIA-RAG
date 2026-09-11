using System.Diagnostics;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveDocumentStructuralOverviewProbeTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_document_structural_overview_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_DOCUMENT_STRUCTURAL_OVERVIEW_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_DOCUMENT_STRUCTURAL_OVERVIEW_PROBE to sample exact document anchors.");
            return;
        }

        var settings = AppSettings.Load();
        var backendUrl = Require(
            "backend URL",
            FirstNonBlank(
                Environment.GetEnvironmentVariable("SAAIA_VALIDATION_BACKEND_URL"),
                settings.BackendUrl));
        var apiKey = Require(
            "configured backend API key",
            FirstNonBlank(
                Environment.GetEnvironmentVariable("SAAIA_API_KEY"),
                SecureLocalStore.GetServerApiKey()));
        var docId = Require(
            "SAAIA_STRUCTURAL_OVERVIEW_DOC_ID",
            Environment.GetEnvironmentVariable("SAAIA_STRUCTURAL_OVERVIEW_DOC_ID"));
        var docPath = Require(
            "SAAIA_STRUCTURAL_OVERVIEW_DOC_PATH",
            Environment.GetEnvironmentVariable("SAAIA_STRUCTURAL_OVERVIEW_DOC_PATH"));
        var artifact = Require(
            "SAAIA_STRUCTURAL_OVERVIEW_ARTIFACT",
            Environment.GetEnvironmentVariable("SAAIA_STRUCTURAL_OVERVIEW_ARTIFACT"));
        var sampleCount = ReadPositiveInt(
            "SAAIA_STRUCTURAL_OVERVIEW_SAMPLE_COUNT",
            10);
        var navigationKind = FirstNonBlank(
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_OVERVIEW_NAVIGATION_KIND"));
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);

        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(ReadPositiveInt(
                "SAAIA_STRUCTURAL_OVERVIEW_TIMEOUT_SECONDS",
                120)));

        var total = Stopwatch.StartNew();
        var navigationWatch = Stopwatch.StartNew();
        const int navigationPageSize = 80;
        var firstNavigationPage = await api.DocumentsNavigationAsync(
            path: null,
            categoryRef: null,
            docId,
            docPath,
            q: null,
            kind: navigationKind,
            limit: navigationPageSize,
            offset: 0,
            cts.Token);
        var navigationTotal = ReadInt(firstNavigationPage, "total");
        var navigationOffsets = new List<int>();
        for (var offset = navigationPageSize;
             offset < navigationTotal;
             offset += navigationPageSize)
        {
            navigationOffsets.Add(offset);
        }

        var remainingNavigationPages = await Task.WhenAll(
            navigationOffsets.Select(offset => api.DocumentsNavigationAsync(
                path: null,
                categoryRef: null,
                docId,
                docPath,
                q: null,
                kind: navigationKind,
                limit: navigationPageSize,
                offset,
                cts.Token)));
        navigationWatch.Stop();

        var navigationPages = new[] { firstNavigationPage }
            .Concat(remainingNavigationPages)
            .ToArray();
        var navigationPageDiagnostics = navigationPages
            .Select(page => new
            {
                offset = ReadInt(page, "offset"),
                limit = ReadInt(page, "limit"),
                itemCount = ReadArrayLength(page, "items"),
                minTargetPage = ReadTargetPageBoundary(page, useMaximum: false),
                maxTargetPage = ReadTargetPageBoundary(page, useMaximum: true),
                resolvableCount = ReadResolvableAnchors(page).Count
            })
            .OrderBy(static page => page.offset)
            .ToArray();
        var anchors = navigationPages
            .SelectMany(ReadResolvableAnchors)
            .GroupBy(static anchor => anchor.ChunkId, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static anchor => anchor.PageStart)
            .ThenBy(static anchor => anchor.ChunkId, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(anchors);
        var pageCount = anchors.Max(static anchor =>
            Math.Max(anchor.PageCount, anchor.PageStart));
        var selected = SelectStratifiedAnchors(
            anchors,
            pageCount,
            sampleCount);
        Assert.NotEmpty(selected);

        var contextWatch = Stopwatch.StartNew();
        var contextTasks = selected.Select(async anchor =>
        {
            var watch = Stopwatch.StartNew();
            var context = await api.DocumentsContextAsync(
                docId,
                docPath,
                anchor.ChunkId,
                pageStart: null,
                pageEnd: null,
                before: 0,
                after: 0,
                limit: 3,
                offset: 0,
                cts.Token);
            watch.Stop();
            return BuildOverviewEntry(anchor, context, watch.ElapsedMilliseconds);
        });
        var entries = await Task.WhenAll(contextTasks);
        contextWatch.Stop();
        total.Stop();

        var canonicalEntries = entries
            .Where(static entry => entry is not null)
            .Cast<OverviewEntry>()
            .OrderBy(static entry => entry.PageStart)
            .ThenBy(static entry => entry.ChunkIndex)
            .ToArray();
        var serializedEntries = JsonSerializer.Serialize(canonicalEntries);
        var report = new
        {
            probe = "documents.structural_overview.read_only",
            capturedAtUtc = DateTimeOffset.UtcNow,
            backendUrl,
            docId,
            docPath,
            requestedSamples = sampleCount,
            navigation = new
            {
                kind = navigationKind,
                elapsedMs = navigationWatch.ElapsedMilliseconds,
                total = navigationTotal,
                calls = navigationPages.Length,
                pageSize = navigationPageSize,
                resolvableAnchorCount = anchors.Count,
                pageCount,
                pages = navigationPageDiagnostics,
                anchors = anchors.Select(static anchor => new
                {
                    anchor.ChunkId,
                    anchor.AnchorId,
                    anchor.Label,
                    navigationKind = anchor.NavigationKind,
                    anchor.SourceKind,
                    pageStart = anchor.PageStart,
                    pageEnd = anchor.PageEnd,
                    anchor.PageCount
                })
            },
            materialization = new
            {
                elapsedMs = contextWatch.ElapsedMilliseconds,
                totalElapsedMs = total.ElapsedMilliseconds,
                selectedAnchorCount = selected.Count,
                materializedChunkCount = canonicalEntries.Length,
                distinctPageCount = canonicalEntries
                    .Select(static entry => entry.PageStart)
                    .Distinct()
                    .Count(),
                totalChunkTokens = canonicalEntries.Sum(static entry => entry.TokenCount),
                serializedCharacters = serializedEntries.Length,
                completeCanonicalIdentityCount = canonicalEntries.Count(
                    static entry => entry.HasCompleteCanonicalIdentity)
            },
            entries = canonicalEntries
        };
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("navigation_ms=" + navigationWatch.ElapsedMilliseconds);
        output.WriteLine("context_ms=" + contextWatch.ElapsedMilliseconds);
        output.WriteLine("total_ms=" + total.ElapsedMilliseconds);
        output.WriteLine("resolvable_anchors=" + anchors.Count);
        output.WriteLine("materialized_chunks=" + canonicalEntries.Length);
        output.WriteLine("distinct_pages=" + canonicalEntries.Select(static entry => entry.PageStart).Distinct().Count());
        output.WriteLine("total_chunk_tokens=" + canonicalEntries.Sum(static entry => entry.TokenCount));
        output.WriteLine("serialized_characters=" + serializedEntries.Length);
        output.WriteLine("artifact=" + artifact);

        Assert.Equal(selected.Count, canonicalEntries.Length);
        Assert.True(canonicalEntries.Length >= Math.Min(sampleCount, anchors.Count));
        Assert.True(canonicalEntries.Select(static entry => entry.PageStart).Distinct().Count()
                    >= Math.Max(1, canonicalEntries.Length / 2));
        Assert.All(canonicalEntries, static entry =>
            Assert.True(entry.HasCompleteCanonicalIdentity,
                $"Incomplete canonical identity for {entry.ChunkId}."));
    }

    private static List<NavigationAnchor> ReadResolvableAnchors(
        JsonElement navigation)
    {
        if (navigation.ValueKind != JsonValueKind.Object
            || !navigation.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray()
            .Select(static item => new NavigationAnchor(
                ReadString(item, "targetChunkId"),
                ReadString(item, "targetAnchorId"),
                ReadString(item, "label"),
                ReadString(item, "kind"),
                ReadString(item, "sourceKind"),
                ReadInt(item, "targetPageStart"),
                ReadInt(item, "targetPageEnd"),
                ReadInt(item, "pageCount")))
            .Where(static anchor =>
                !string.IsNullOrWhiteSpace(anchor.ChunkId)
                && anchor.PageStart > 0)
            .GroupBy(static anchor => anchor.ChunkId, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static anchor => anchor.PageStart)
            .ThenBy(static anchor => anchor.ChunkId, StringComparer.Ordinal)
            .ToList();
    }

    private static List<NavigationAnchor> SelectStratifiedAnchors(
        IReadOnlyList<NavigationAnchor> anchors,
        int pageCount,
        int requestedCount)
    {
        var targetCount = Math.Min(
            Math.Max(1, requestedCount),
            anchors.Count);
        var selected = new List<NavigationAnchor>(targetCount);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var effectivePageCount = Math.Max(
            pageCount,
            anchors.Max(static anchor => anchor.PageStart));

        for (var bucket = 0; bucket < targetCount; bucket++)
        {
            var start = (int)Math.Floor(
                            bucket * effectivePageCount / (double)targetCount)
                        + 1;
            var end = (int)Math.Floor(
                (bucket + 1) * effectivePageCount / (double)targetCount);
            end = Math.Max(start, end);
            var midpoint = (start + end) / 2d;
            var candidate = anchors
                .Where(anchor => !used.Contains(anchor.ChunkId))
                .Where(anchor => anchor.PageStart >= start && anchor.PageStart <= end)
                .OrderBy(anchor => Math.Abs(anchor.PageStart - midpoint))
                .ThenBy(static anchor => anchor.PageStart)
                .FirstOrDefault()
                ?? anchors
                    .Where(anchor => !used.Contains(anchor.ChunkId))
                    .OrderBy(anchor => Math.Abs(anchor.PageStart - midpoint))
                    .ThenBy(static anchor => anchor.PageStart)
                    .First();

            selected.Add(candidate);
            used.Add(candidate.ChunkId);
        }

        return selected;
    }

    private static OverviewEntry? BuildOverviewEntry(
        NavigationAnchor anchor,
        JsonElement context,
        long elapsedMs)
    {
        if (context.ValueKind != JsonValueKind.Object
            || !context.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var item = items.EnumerateArray()
            .FirstOrDefault(candidate => string.Equals(
                ReadString(candidate, "chunkId"),
                anchor.ChunkId,
                StringComparison.Ordinal));
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var document = context.TryGetProperty("document", out var documentValue)
                       && documentValue.ValueKind == JsonValueKind.Object
            ? documentValue
            : default;
        return new OverviewEntry(
            ReadString(document, "docId"),
            ReadString(document, "revisionId"),
            ReadString(document, "sourceHash"),
            ReadString(document, "docPath"),
            ReadString(document, "docName"),
            ReadString(item, "chunkId"),
            anchor.AnchorId,
            ReadInt(item, "chunkIndex"),
            ReadInt(item, "pageStart"),
            ReadInt(item, "pageEnd"),
            ReadInt(item, "tokenCount"),
            ReadString(item, "chunkType"),
            ReadString(item, "contentRole"),
            ReadString(item, "sectionTitle"),
            ReadString(item, "headingPath"),
            ReadString(item, "text"),
            anchor.Label,
            anchor.NavigationKind,
            anchor.SourceKind,
            elapsedMs);
    }

    private static string ReadString(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static int ReadArrayLength(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.Array
            ? property.GetArrayLength()
            : 0;

    private static int ReadTargetPageBoundary(
        JsonElement navigation,
        bool useMaximum)
    {
        if (navigation.ValueKind != JsonValueKind.Object
            || !navigation.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var pages = items.EnumerateArray()
            .Select(static item => ReadInt(item, "targetPageStart"))
            .Where(static page => page > 0)
            .ToArray();
        if (pages.Length == 0)
            return 0;
        return useMaximum ? pages.Max() : pages.Min();
    }

    private static int ReadPositiveInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
           && parsed > 0
            ? parsed
            : fallback;

    private static string Require(string name, string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("Missing " + name + ".")
            : value.Trim();

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value =>
            !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record NavigationAnchor(
        string ChunkId,
        string AnchorId,
        string Label,
        string NavigationKind,
        string SourceKind,
        int PageStart,
        int PageEnd,
        int PageCount);

    private sealed record OverviewEntry(
        string DocId,
        string RevisionId,
        string SourceHash,
        string DocPath,
        string DocName,
        string ChunkId,
        string AnchorId,
        int ChunkIndex,
        int PageStart,
        int PageEnd,
        int TokenCount,
        string ChunkType,
        string ContentRole,
        string SectionTitle,
        string HeadingPath,
        string Text,
        string NavigationLabel,
        string NavigationKind,
        string NavigationSourceKind,
        long ContextElapsedMs)
    {
        public bool HasCompleteCanonicalIdentity =>
            !string.IsNullOrWhiteSpace(DocId)
            && !string.IsNullOrWhiteSpace(RevisionId)
            && SourceHash.Length == 64
            && !string.IsNullOrWhiteSpace(DocPath)
            && !string.IsNullOrWhiteSpace(ChunkId)
            && !string.IsNullOrWhiteSpace(AnchorId)
            && PageStart > 0
            && PageEnd >= PageStart;
    }
}
