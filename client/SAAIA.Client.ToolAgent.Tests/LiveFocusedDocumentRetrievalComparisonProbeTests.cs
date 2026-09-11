using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveFocusedDocumentRetrievalComparisonProbeTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_focused_document_routes_are_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_FOCUSED_DOCUMENT_RETRIEVAL_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_FOCUSED_DOCUMENT_RETRIEVAL_PROBE to compare focused routes.");
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
            "SAAIA_FOCUSED_DOCUMENT_DOC_ID",
            Environment.GetEnvironmentVariable("SAAIA_FOCUSED_DOCUMENT_DOC_ID"));
        var docPath = Require(
            "SAAIA_FOCUSED_DOCUMENT_DOC_PATH",
            Environment.GetEnvironmentVariable("SAAIA_FOCUSED_DOCUMENT_DOC_PATH"));
        var query = Require(
            "SAAIA_FOCUSED_DOCUMENT_QUERY",
            Environment.GetEnvironmentVariable("SAAIA_FOCUSED_DOCUMENT_QUERY"));
        var page = ReadPositiveInt("SAAIA_FOCUSED_DOCUMENT_PAGE", 1);
        var limit = ReadPositiveInt("SAAIA_FOCUSED_DOCUMENT_LIMIT", 10);
        var artifact = Require(
            "SAAIA_FOCUSED_DOCUMENT_ARTIFACT",
            Environment.GetEnvironmentVariable("SAAIA_FOCUSED_DOCUMENT_ARTIFACT"));
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);

        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var navigation = await api.DocumentsNavigationAsync(
            path: null,
            categoryRef: null,
            docId,
            docPath,
            q: query,
            kind: null,
            limit,
            offset: 0,
            cts.Token);
        var context = await api.DocumentsContextAsync(
            docId,
            docPath,
            chunkId: null,
            pageStart: page,
            pageEnd: page,
            before: 2,
            after: 8,
            limit,
            offset: 0,
            cts.Token);
        var search = await api.RagSearchToolAsync(
            query,
            limit,
            category: null,
            mode: "balanced",
            cts.Token,
            docId,
            docPath,
            maxPerDoc: limit,
            maxPerPage: 3,
            researchMode: "source_exploration",
            includeResearchSurfaces: true,
            sourceBackedCanonical: true);

        var report = new StringBuilder();
        report.AppendLine("LIVE FOCUSED-DOCUMENT RETRIEVAL COMPARISON");
        report.AppendLine("Backend: " + backendUrl);
        report.AppendLine("DocId: " + docId);
        report.AppendLine("DocPath: " + docPath);
        report.AppendLine("Query: " + query);
        report.AppendLine("Context page: " + page);
        report.AppendLine("Limit: " + limit);
        report.AppendLine();
        AppendRoute(report, "DOCUMENTS.NAVIGATION", navigation);
        AppendRoute(report, "DOCUMENTS.CONTEXT", context);
        AppendRoute(report, "RAG.SEARCH SCOPED TO DOCUMENT", search);
        await File.WriteAllTextAsync(
            artifact,
            report.ToString(),
            CancellationToken.None);

        output.WriteLine("navigation_items=" + CountItems(navigation));
        output.WriteLine("context_items=" + CountItems(context));
        output.WriteLine("search_items=" + CountItems(search));
        output.WriteLine("Artifact: " + artifact);
        Assert.True(CountItems(navigation) > 0);
        Assert.True(CountItems(context) > 0);
        Assert.True(CountItems(search) > 0);
    }

    private static void AppendRoute(
        StringBuilder report,
        string title,
        JsonElement payload)
    {
        report.AppendLine(title);
        report.AppendLine("Item count: " + CountItems(payload));
        report.AppendLine(JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions { WriteIndented = true }));
        report.AppendLine();
    }

    private static int CountItems(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty("items", out var items)
           && items.ValueKind == JsonValueKind.Array
            ? items.GetArrayLength()
            : payload.ValueKind == JsonValueKind.Object
              && payload.TryGetProperty("hits", out var hits)
              && hits.ValueKind == JsonValueKind.Array
                ? hits.GetArrayLength()
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
}
