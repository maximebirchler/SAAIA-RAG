using System.Collections;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_retry_history_uses_current_canonical_catalogue_and_never_revives_retired_scope(bool changedHash)
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = 8_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var active = SourceCatalogueEvidence();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var before = SourceCatalogueProjection(provider, active, aliases);
        var key = SourceCatalogueKeyFor(before, active[1].Reference.DocId);
        var read = SourceCatalogueParse(provider, before, key, review: false) with
        {
            ResourceLimit = new("canonical_read_window_result_limit_exceeded", null, null, null, 60, 61, StopsResearch: false)
        };
        JsonElement History(object projection)
        {
            var items = (IEnumerable)InvokeSourceCatalogue(null, "BuildPriorSearchesForPrompt",
                new[] { read }, projection)!;
            return JsonSerializer.SerializeToElement(items.Cast<object>().Single(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        var current = History(before);
        Assert.Equal(key, current.GetProperty("readRetry").GetProperty("suggestedRequest").GetProperty("sourceKey").GetString());
        Assert.Single(((IEnumerable)before).Cast<object>()); // The source's body did not fit, but its catalogue handle remains valid.
        var afterEvidence = changedHash
            ? new[] { active[0], active[1] with { Reference = SourceCatalogueReference(active[1].Reference, "NEW-HASH", new string('f', 64)) } }
            : new[] { active[0] };
        var after = History(SourceCatalogueProjection(provider, afterEvidence, aliases));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("sourceKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("readRetry").GetProperty("suggestedRequest").ValueKind);
        Assert.Contains("no longer in the current catalogue", after.GetProperty("readRetry").GetProperty("instruction").GetString());
        Assert.Empty(factory.Requests);
    }

    [Theory]
    [InlineData(40, 41, 60)]
    [InlineData(60, 61, null)]
    public void Single_page_retry_never_exceeds_the_existing_chunk_bound(int topK, int observed, int? expected)
    {
        var request = new AdvancedAnalysisSearchRequest("observed page", TopK: topK, PageStart: 7, PageEnd: 7, Operation: "read_source");
        var limit = new AdvancedAnalysisResearchResourceLimit("canonical_read_window_result_limit_exceeded",
            null, null, null, topK, observed, StopsResearch: false);
        var hint = InvokeSourceCatalogue(null, "BuildReadRetryHint", request, limit, "internal-source-1");
        var json = JsonSerializer.SerializeToElement(hint, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (expected is int count)
        {
            Assert.Equal(count, json.GetProperty("suggestedRequest").GetProperty("topK").GetInt32());
            Assert.Equal(1, json.GetProperty("suggestedRequest").GetProperty("pageCount").GetInt32());
        }
        else Assert.Equal(JsonValueKind.Null, json.GetProperty("suggestedRequest").ValueKind);
        Assert.Contains("does not establish absence", json.GetProperty("instruction").GetString());
    }
}
