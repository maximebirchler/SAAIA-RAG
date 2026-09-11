using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveDocumentNavigationProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_cuisine_navigation_exposes_named_recipe_anchors_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_DOCUMENT_NAVIGATION_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_DOCUMENT_NAVIGATION_PROBE=1 to inspect real document navigation.");
            return;
        }

        var settings = AppSettings.Load();
        var backendUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_BACKEND_URL"),
            settings.BackendUrl);
        var apiKey = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_API_KEY"),
            SecureLocalStore.GetServerApiKey());
        if (string.IsNullOrWhiteSpace(backendUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "The live navigation probe requires the configured backend and API key.");
        }

        var artifact = Environment.GetEnvironmentVariable(
            "SAAIA_DOCUMENT_NAVIGATION_ARTIFACT");
        if (string.IsNullOrWhiteSpace(artifact))
            artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);

        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));
        var query = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_DOCUMENT_NAVIGATION_QUERY"),
            "recette")!;
        var limit = int.TryParse(
            Environment.GetEnvironmentVariable("SAAIA_DOCUMENT_NAVIGATION_LIMIT"),
            out var configuredLimit)
            ? Math.Clamp(configuredLimit, 1, 200)
            : 200;
        var kind = Environment.GetEnvironmentVariable(
            "SAAIA_DOCUMENT_NAVIGATION_KIND");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var raw = await api.DocumentsNavigationAsync(
            path: "Cuisine",
            categoryRef: null,
            docId: null,
            docPath: null,
            q: query,
            kind,
            limit,
            offset: 0,
            cts.Token);

        var entries = ReadEntries(raw);
        var anchored = entries
            .Where(static entry =>
                !string.IsNullOrWhiteSpace(entry.Label)
                && !string.IsNullOrWhiteSpace(entry.Document)
                && entry.PageStart.HasValue)
            .ToArray();
        var resolvedChunkEntries = anchored
            .Where(static entry =>
                !string.IsNullOrWhiteSpace(entry.TargetChunkId)
                && !string.IsNullOrWhiteSpace(entry.RevisionId)
                && !string.IsNullOrWhiteSpace(entry.SourceHash))
            .ToArray();
        var resolvedEntry = Assert.Single(resolvedChunkEntries.Take(1));
        var resolvedContext = await api.DocumentsContextAsync(
            resolvedEntry.DocId,
            resolvedEntry.Document,
            resolvedEntry.TargetChunkId,
            resolvedEntry.PageStart,
            resolvedEntry.PageEnd,
            before: 0,
            after: 1,
            limit: 3,
            offset: 0,
            cts.Token);
        var report = new StringBuilder();
        report.AppendLine("LIVE DOCUMENT NAVIGATION PROBE");
        report.AppendLine("Backend: " + backendUrl);
        report.AppendLine("Path: Cuisine");
        report.AppendLine("Query: " + query);
        report.AppendLine("Kind: " + (kind ?? "all"));
        report.AppendLine("Entry count: " + entries.Count);
        report.AppendLine("Anchored entry count: " + anchored.Length);
        report.AppendLine("Resolved chunk entry count: " + resolvedChunkEntries.Length);
        report.AppendLine();
        foreach (var entry in entries)
        {
            report.Append(entry.Label)
                .Append(" | docId=").Append(entry.DocId)
                .Append(" | document=").Append(entry.Document)
                .Append(" | page=").Append(entry.PageStart)
                .Append('-').Append(entry.PageEnd)
                .Append(" | revisionId=").Append(entry.RevisionId)
                .Append(" | targetChunkId=").Append(entry.TargetChunkId)
                .Append(" | targetAnchorId=").Append(entry.TargetAnchorId)
                .Append(" | kind=").Append(entry.Kind)
                .AppendLine();
        }
        report.AppendLine();
        report.AppendLine("RAW:");
        report.AppendLine(JsonSerializer.Serialize(
            raw,
            new JsonSerializerOptions { WriteIndented = true }));
        report.AppendLine();
        report.AppendLine("RESOLVED CONTEXT:");
        report.AppendLine(JsonSerializer.Serialize(
            resolvedContext,
            new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(artifact, report.ToString(), CancellationToken.None);

        output.WriteLine(
            $"entries={entries.Count}; anchored={anchored.Length}; resolvedChunks={resolvedChunkEntries.Length}");
        output.WriteLine("Artifact: " + artifact);
        Assert.NotEmpty(entries);
        Assert.NotEmpty(anchored);
        Assert.NotEmpty(resolvedChunkEntries);
        Assert.True(resolvedContext.GetProperty("found").GetBoolean());
        Assert.True(resolvedContext.GetProperty("anchorFound").GetBoolean());
        Assert.NotEmpty(resolvedContext.GetProperty("items").EnumerateArray());
    }

    private static IReadOnlyList<NavigationEntry> ReadEntries(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object
            || !TryGetProperty(raw, "items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<NavigationEntry>();
        }

        return items.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new NavigationEntry(
                GetString(item, "label", "title", "name"),
                GetString(item, "docId", "doc_id", "documentId"),
                FirstNonBlank(
                    GetString(item, "docPath", "doc_path", "path", "documentPath"),
                    GetString(item, "docName", "doc_name", "documentName", "fileName")),
                GetInt(item, "targetPageStart", "target_page_start", "sourcePage", "source_page"),
                GetInt(item, "targetPageEnd", "target_page_end"),
                GetString(item, "kind", "type"),
                GetString(item, "revisionId", "revision_id"),
                GetString(item, "sourceHash", "source_hash"),
                GetString(item, "targetChunkId", "target_chunk_id"),
                GetString(item, "targetAnchorId", "target_anchor_id")))
            .ToArray();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim();
            }
        }

        return null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), out number))
            {
                return number;
            }
        }

        return null;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string CreateArtifactPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, ".git")))
            current = current.Parent;
        if (current is null)
            throw new InvalidOperationException("Could not locate the repository root.");

        return Path.Combine(
            current.FullName,
            "artifacts",
            "live-document-navigation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
    }

    private sealed record NavigationEntry(
        string? Label,
        string? DocId,
        string? Document,
        int? PageStart,
        int? PageEnd,
        string? Kind,
        string? RevisionId,
        string? SourceHash,
        string? TargetChunkId,
        string? TargetAnchorId);
}
