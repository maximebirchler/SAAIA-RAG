using System.Diagnostics;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveDocumentPageStratifiedOverviewProbeTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_document_page_stratified_overview_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_DOCUMENT_PAGE_STRATIFIED_OVERVIEW_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_DOCUMENT_PAGE_STRATIFIED_OVERVIEW_PROBE to sample canonical chunks across the complete page range.");
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
        var docId = Environment.GetEnvironmentVariable(
            "SAAIA_PAGE_OVERVIEW_DOC_ID")?.Trim() ?? string.Empty;
        var docPath = Require(
            "SAAIA_PAGE_OVERVIEW_DOC_PATH",
            Environment.GetEnvironmentVariable("SAAIA_PAGE_OVERVIEW_DOC_PATH"));
        var artifact = Require(
            "SAAIA_PAGE_OVERVIEW_ARTIFACT",
            Environment.GetEnvironmentVariable("SAAIA_PAGE_OVERVIEW_ARTIFACT"));
        var sampleCount = ReadPositiveInt(
            "SAAIA_PAGE_OVERVIEW_SAMPLE_COUNT",
            12);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);

        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(ReadPositiveInt(
                "SAAIA_PAGE_OVERVIEW_TIMEOUT_SECONDS",
                120)));

        var pageCount = 0;

        if (docId.Length == 0)
        {
            var search = await api.DocumentsSearchAsync(
                docPath,
                categoryPath: null,
                categoryRef: null,
                limit: 20,
                offset: 0,
                cts.Token);
            var resolved = ResolveExactDocument(search, docPath);
            docId = resolved.DocId;
            pageCount = resolved.PageCount;
            if (!string.IsNullOrWhiteSpace(resolved.DocPath))
                docPath = resolved.DocPath;
        }

        var total = Stopwatch.StartNew();
        var metadataWatch = Stopwatch.StartNew();
        if (pageCount <= 0)
        {
            var navigation = await api.DocumentsNavigationAsync(
                path: null,
                categoryRef: null,
                docId,
                docPath,
                q: null,
                kind: null,
                limit: 1,
                offset: 0,
                cts.Token);
            pageCount = ReadPageCount(navigation);
        }
        metadataWatch.Stop();
        Assert.True(pageCount > 0, "The document page count was not available.");

        var targetPages = SelectBucketMidpointPages(pageCount, sampleCount);
        var contextWatch = Stopwatch.StartNew();
        var samples = await Task.WhenAll(targetPages.Select(async targetPage =>
        {
            var watch = Stopwatch.StartNew();
            var context = await api.DocumentsContextAsync(
                docId,
                docPath,
                chunkId: null,
                pageStart: targetPage,
                pageEnd: targetPage,
                before: 0,
                after: 0,
                limit: 12,
                offset: 0,
                cts.Token);
            watch.Stop();
            return BuildPageSample(targetPage, context, watch.ElapsedMilliseconds);
        }));
        contextWatch.Stop();
        total.Stop();

        var entries = samples
            .Where(static sample => sample.Entry is not null)
            .Select(static sample => sample.Entry!)
            .OrderBy(static entry => entry.TargetPage)
            .ToArray();
        var serializedEntries = JsonSerializer.Serialize(entries);
        var report = new
        {
            probe = "documents.page_stratified_overview.read_only",
            capturedAtUtc = DateTimeOffset.UtcNow,
            backendUrl,
            docId,
            docPath,
            pageCount,
            requestedSamples = sampleCount,
            targetPages,
            metadataElapsedMs = metadataWatch.ElapsedMilliseconds,
            materialization = new
            {
                elapsedMs = contextWatch.ElapsedMilliseconds,
                totalElapsedMs = total.ElapsedMilliseconds,
                materializedChunkCount = entries.Length,
                missingTargetPages = samples
                    .Where(static sample => sample.Entry is null)
                    .Select(static sample => sample.TargetPage)
                    .ToArray(),
                distinctChunkCount = entries
                    .Select(static entry => entry.ChunkId)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                totalChunkTokens = entries.Sum(static entry => entry.TokenCount),
                serializedCharacters = serializedEntries.Length,
                completeCanonicalIdentityCount = entries.Count(
                    static entry => entry.HasCompleteCanonicalIdentity)
            },
            entries
        };
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("metadata_ms=" + metadataWatch.ElapsedMilliseconds);
        output.WriteLine("context_ms=" + contextWatch.ElapsedMilliseconds);
        output.WriteLine("total_ms=" + total.ElapsedMilliseconds);
        output.WriteLine("page_count=" + pageCount);
        output.WriteLine("target_pages=" + string.Join(",", targetPages));
        output.WriteLine("materialized_chunks=" + entries.Length);
        output.WriteLine("total_chunk_tokens=" + entries.Sum(static entry => entry.TokenCount));
        output.WriteLine("serialized_characters=" + serializedEntries.Length);
        output.WriteLine("artifact=" + artifact);

        Assert.Equal(targetPages.Length, entries.Length);
        Assert.Equal(entries.Length, entries.Select(static entry => entry.ChunkId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, static entry =>
        {
            Assert.True(entry.CoversTargetPage,
                $"Chunk {entry.ChunkId} does not cover target page {entry.TargetPage}.");
            Assert.True(entry.HasCompleteCanonicalIdentity,
                $"Incomplete canonical identity for {entry.ChunkId}.");
        });
    }

    private static int ReadPageCount(JsonElement navigation)
    {
        if (navigation.ValueKind != JsonValueKind.Object
            || !navigation.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        foreach (var item in items.EnumerateArray())
        {
            var pageCount = ReadInt(item, "pageCount");
            if (pageCount > 0)
                return pageCount;
        }

        return 0;
    }

    private static ExactDocumentMatch ResolveExactDocument(
        JsonElement search,
        string requestedDocPath)
    {
        if (search.ValueKind != JsonValueKind.Object
            || !search.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "The document search response did not contain an items array.");
        }

        var normalizedRequest = NormalizeDocumentReference(requestedDocPath);
        var requestedName = Path.GetFileName(normalizedRequest);
        var matches = items.EnumerateArray()
            .Where(item =>
                string.Equals(
                    NormalizeDocumentReference(ReadString(item, "docPath")),
                    normalizedRequest,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    NormalizeDocumentReference(ReadString(item, "docName")),
                    requestedName,
                    StringComparison.OrdinalIgnoreCase))
            .Select(item => new ExactDocumentMatch(
                ReadString(item, "docId"),
                ReadInt(item, "pages"),
                ReadString(item, "docPath")))
            .Where(static match =>
                !string.IsNullOrWhiteSpace(match.DocId))
            .GroupBy(
                static match => match.DocId,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static match => match.PageCount)
                .ThenBy(static match => match.DocPath, StringComparer.Ordinal)
                .First())
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                "No exact indexed document matched '" + requestedDocPath + "'."),
            _ => throw new InvalidOperationException(
                "Multiple indexed documents matched '" + requestedDocPath + "'.")
        };
    }

    private static string NormalizeDocumentReference(string? value)
        => (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim()
            .Trim('/');

    private static int[] SelectBucketMidpointPages(
        int pageCount,
        int requestedCount)
    {
        var count = Math.Min(Math.Max(1, requestedCount), pageCount);
        return Enumerable.Range(0, count)
            .Select(bucket => Math.Clamp(
                (int)Math.Floor((bucket + 0.5d) * pageCount / count) + 1,
                1,
                pageCount))
            .Distinct()
            .ToArray();
    }

    private static PageSample BuildPageSample(
        int targetPage,
        JsonElement context,
        long elapsedMs)
    {
        if (context.ValueKind != JsonValueKind.Object
            || !context.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return new PageSample(targetPage, null);
        }

        var candidates = items.EnumerateArray()
            .Where(item => ReadInt(item, "pageStart") <= targetPage
                           && ReadInt(item, "pageEnd") >= targetPage)
            .Where(item => !string.IsNullOrWhiteSpace(ReadString(item, "text")))
            .Where(item => !string.Equals(
                ReadString(item, "contentRole"),
                "navigation",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(ChunkPreference)
            .ThenByDescending(item => ReadInt(item, "tokenCount"))
            .ThenBy(item => ReadInt(item, "chunkIndex"))
            .ToArray();
        if (candidates.Length == 0)
            return new PageSample(targetPage, null);

        var item = candidates[0];
        var document = context.TryGetProperty("document", out var documentValue)
                       && documentValue.ValueKind == JsonValueKind.Object
            ? documentValue
            : default;
        return new PageSample(
            targetPage,
            new PageOverviewEntry(
                ReadString(document, "docId"),
                ReadString(document, "revisionId"),
                ReadString(document, "sourceHash"),
                ReadString(document, "docPath"),
                ReadString(document, "docName"),
                ReadString(item, "chunkId"),
                ReadInt(item, "chunkIndex"),
                ReadInt(item, "pageStart"),
                ReadInt(item, "pageEnd"),
                targetPage,
                ReadInt(item, "tokenCount"),
                ReadString(item, "chunkType"),
                ReadString(item, "contentRole"),
                ReadString(item, "sectionTitle"),
                ReadString(item, "headingPath"),
                ReadString(item, "text"),
                elapsedMs));
    }

    private static int ChunkPreference(JsonElement item)
        => ReadInt(item, "tokenCount") < 60
            ? 3
            : ReadString(item, "chunkType") switch
            {
                "unit_exact_v1" => 0,
                "section_window_v1" => 1,
                _ => 2
            };

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

    private sealed record PageSample(
        int TargetPage,
        PageOverviewEntry? Entry);

    private sealed record ExactDocumentMatch(
        string DocId,
        int PageCount,
        string DocPath);

    private sealed record PageOverviewEntry(
        string DocId,
        string RevisionId,
        string SourceHash,
        string DocPath,
        string DocName,
        string ChunkId,
        int ChunkIndex,
        int PageStart,
        int PageEnd,
        int TargetPage,
        int TokenCount,
        string ChunkType,
        string ContentRole,
        string SectionTitle,
        string HeadingPath,
        string Text,
        long ContextElapsedMs)
    {
        public bool CoversTargetPage =>
            PageStart <= TargetPage && PageEnd >= TargetPage;

        public bool HasCompleteCanonicalIdentity =>
            !string.IsNullOrWhiteSpace(DocId)
            && !string.IsNullOrWhiteSpace(RevisionId)
            && SourceHash.Length == 64
            && !string.IsNullOrWhiteSpace(DocPath)
            && !string.IsNullOrWhiteSpace(ChunkId)
            && PageStart > 0
            && PageEnd >= PageStart;
    }
}
