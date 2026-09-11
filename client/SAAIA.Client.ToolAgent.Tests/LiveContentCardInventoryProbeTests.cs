using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveContentCardInventoryProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_cuisine_content_card_inventory_is_paged_citable_and_materialized()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_CONTENT_CARD_INVENTORY_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_CONTENT_CARD_INVENTORY_PROBE=1 to inspect the live inventory.");
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
            throw new InvalidOperationException("The live content-card probe requires the configured backend and API key.");

        var artifactDirectory = Path.Combine(
            FindRepoRoot(),
            "artifacts",
            "live-content-card-inventory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(artifactDirectory);

        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));
        var configuredQuery = Environment.GetEnvironmentVariable(
            "SAAIA_CONTENT_CARD_INVENTORY_QUERY");
        var query = string.IsNullOrWhiteSpace(configuredQuery)
            ? null
            : configuredQuery.Trim();
        var inventoryMode = Environment.GetEnvironmentVariable(
            "SAAIA_CONTENT_CARD_INVENTORY_MODE");
        var limit = ReadInt(
            "SAAIA_CONTENT_CARD_INVENTORY_LIMIT",
            fallback: 120,
            minimum: 1);
        var offset = ReadInt(
            "SAAIA_CONTENT_CARD_INVENTORY_OFFSET",
            fallback: 0,
            minimum: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var raw = await api.DocumentsContentCardsAsync(
            "Cuisine",
            null,
            null,
            null,
            query,
            inventoryMode,
            limit,
            offset,
            cts.Token);

        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.content_cards",
            Result = raw.Clone(),
            DurationMs = 0
        });
        var bundle = EvidenceBundleBuilder.FromToolResults(
            toolResults,
            "Construire un planning de repas source de vingt cases.");

        var report = new StringBuilder();
        report.AppendLine("LIVE CONTENT-CARD INVENTORY");
        report.AppendLine("Backend: " + backendUrl);
        report.AppendLine("Category: Cuisine");
        report.AppendLine("Query: " + (query ?? "(none)"));
        report.AppendLine("Inventory mode: " + (inventoryMode ?? "(default)"));
        report.AppendLine("Limit: " + limit);
        report.AppendLine("Offset: " + offset);
        report.AppendLine("Evidence count: " + bundle.Items.Count);
        report.AppendLine();
        report.AppendLine(JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
        report.AppendLine();
        report.AppendLine("NORMALIZED EVIDENCE");
        foreach (var item in bundle.Items)
        {
            report.Append(item.EvidenceId);
            report.Append(" | ").Append(item.Excerpt);
            report.Append(" | ").Append(item.DocPath);
            report.Append(" | p.").Append(item.PageStart);
            report.Append(" | ").Append(item.ChunkId);
            report.AppendLine();
        }

        var artifact = Path.Combine(artifactDirectory, "report.txt");
        await File.WriteAllTextAsync(artifact, report.ToString(), CancellationToken.None);
        output.WriteLine("Artifact: " + artifact);
        output.WriteLine("Evidence count: " + bundle.Items.Count);

        Assert.Equal(JsonValueKind.Object, raw.ValueKind);
        Assert.True(raw.GetProperty("citable").GetBoolean());
        Assert.True(raw.GetProperty("total").GetInt32() > 0);
        Assert.NotEmpty(bundle.Items);
        Assert.All(bundle.Items, item =>
        {
            Assert.Equal("canonical_content_card", item.SourceKind);
            Assert.False(string.IsNullOrWhiteSpace(item.DocPath));
            Assert.True(item.PageStart > 0);
            Assert.StartsWith("content-card:", item.ChunkId);
        });
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static int ReadInt(
        string environmentVariable,
        int fallback,
        int minimum)
        => int.TryParse(
               Environment.GetEnvironmentVariable(environmentVariable),
               out var parsed)
           && parsed >= minimum
            ? parsed
            : fallback;

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RAG.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
