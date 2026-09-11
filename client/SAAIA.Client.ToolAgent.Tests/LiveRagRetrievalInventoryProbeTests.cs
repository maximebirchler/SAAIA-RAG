using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveRagRetrievalInventoryProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_meal_plan_queries_capture_raw_hits_and_content_cards_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_RETRIEVAL_INVENTORY_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_RETRIEVAL_INVENTORY_PROBE=1 to capture the live retrieval inventory.");
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
            throw new InvalidOperationException("The live retrieval probe requires the configured backend and API key.");

        var artifact = Environment.GetEnvironmentVariable("SAAIA_RETRIEVAL_INVENTORY_ARTIFACT");
        if (string.IsNullOrWhiteSpace(artifact))
            artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);

        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));
        var queries = LoadQueries();
        var category = ResolveCategory();
        var topK = ReadPositiveInt("SAAIA_RETRIEVAL_INVENTORY_TOP_K", 12);
        var toolResults = new ToolResults();
        var report = new StringBuilder();
        report.AppendLine("LIVE MEAL-PLAN RETRIEVAL INVENTORY");
        report.AppendLine("Backend: " + backendUrl);
        report.AppendLine("Category: " + (category ?? "<none>"));
        report.AppendLine("topK: " + topK);
        report.AppendLine("mode: balanced");
        report.AppendLine("researchMode: source_exploration");
        report.AppendLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        foreach (var query in queries)
        {
            var raw = await api.RagSearchToolAsync(
                query,
                topK,
                category,
                mode: "balanced",
                cts.Token,
                researchMode: "source_exploration",
                includeResearchSurfaces: true,
                sourceBackedCanonical: true);
            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.search",
                Result = NormalizeRagSearchResponse(raw, query)
            });
            report.AppendLine("QUERY: " + query);
            report.AppendLine(JsonSerializer.Serialize(
                raw,
                new JsonSerializerOptions { WriteIndented = true }));
            report.AppendLine();
        }

        var bundle = EvidenceBundleBuilder.FromToolResults(
            toolResults,
            string.Join(" | ", queries));
        report.AppendLine("NORMALIZED EVIDENCE INVENTORY");
        report.AppendLine("Evidence count: " + bundle.Items.Count);
        foreach (var item in bundle.Items)
        {
            report.Append(item.EvidenceId);
            report.Append(" | doc=").Append(item.DocPath ?? item.DocName);
            report.Append(" | page=").Append(item.PageStart);
            report.Append(" | score=").Append(item.Score);
            report.Append(" | query=").Append(item.QueryUsed);
            report.AppendLine();
            report.AppendLine("EXCERPT: " + item.Excerpt);
            report.AppendLine(
                "MATCHED_CONTENT_CARDS: "
                + (item.MatchedContentCards?.GetRawText() ?? "null"));
            report.AppendLine();
        }

        await File.WriteAllTextAsync(artifact, report.ToString(), CancellationToken.None);
        output.WriteLine("Evidence count: " + bundle.Items.Count);
        output.WriteLine("Artifact: " + artifact);
        Assert.NotEmpty(bundle.Items);
    }

    private static JsonElement NormalizeRagSearchResponse(JsonElement raw, string query)
    {
        if (raw.ValueKind != JsonValueKind.Object
            || !raw.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return raw.Clone();
        }

        JsonElement? meta = raw.TryGetProperty("meta", out var rawMeta)
            ? rawMeta.Clone()
            : null;
        return JsonSerializer.SerializeToElement(new
        {
            query,
            hits = items.Clone(),
            meta
        });
    }

    private static IReadOnlyList<string> LoadQueries()
    {
        var configured = Environment.GetEnvironmentVariable(
            "SAAIA_RETRIEVAL_INVENTORY_QUERIES_JSON");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var parsed = JsonSerializer.Deserialize<string[]>(configured);
            if (parsed is { Length: > 0 }
                && parsed.All(static query => !string.IsNullOrWhiteSpace(query)))
            {
                return parsed.Select(static query => query.Trim()).ToArray();
            }
        }

        return new[]
        {
            "recettes nom de la recette ingrédients temps de cuisson Petit-déjeuner",
            "recettes nom de la recette ingrédients temps de cuisson Dîner",
            "recettes nom de la recette ingrédients temps de cuisson Souper",
            "recettes nom de la recette ingrédients temps de cuisson Collation"
        };
    }

    private static string? ResolveCategory()
    {
        var configured = Environment.GetEnvironmentVariable(
            "SAAIA_RETRIEVAL_INVENTORY_CATEGORY");
        if (string.Equals(configured, "<none>", StringComparison.OrdinalIgnoreCase))
            return null;
        return string.IsNullOrWhiteSpace(configured) ? "Cuisine" : configured.Trim();
    }

    private static int ReadPositiveInt(string name, int fallback)
        => int.TryParse(
               Environment.GetEnvironmentVariable(name),
               out var parsed)
           && parsed > 0
            ? parsed
            : fallback;

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string CreateArtifactPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, ".git")))
            current = current.Parent;
        if (current is null)
            throw new InvalidOperationException("Could not locate the repository root for the retrieval artifact.");

        return Path.Combine(
            current.FullName,
            "artifacts",
            "live-rag-retrieval-inventory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            "report.txt");
    }
}
