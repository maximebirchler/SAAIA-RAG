using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("distinct_structured_layout")]
    [InlineData("structured_layout")]
    public void Named_grid_keeps_a_late_target_and_source_inside_the_existing_eight_body_allowance(string selectionPolicy)
    {
        const int cap = 24_000;
        var request = BuildRequest(20, "named_item", selectionPolicy, "documented_option");
        var body = string.Concat(Enumerable.Repeat("Documented procedure: isolate the assembly before opening the housing. ", 18));
        var prior = Enumerable.Range(1, 12).Select(index => PreviousProjectionBody($"OLD-{index}",
            $"Procedure {index}\n{body}", source: index <= 9 ? 1 : 2, page: index)).ToArray();
        var current = Enumerable.Range(1, 15).Select(index => PreviousProjectionBody($"FRESH-{index}",
            $"Current observation {index}\n{body}", page: 20 + index)).ToArray();
        var active = prior.Concat(current).ToArray();
        var gateway = new AccumulatedReadGateway(active);
        var context = AccumulatedReadContext(gateway);
        var queries = PreviousProjectionQueries(context);
        for (var index = 0; index < prior.Length; index++)
            queries[prior[index].Reference.EvidenceId!] = [$"Initial intent {index}"];
        queries["OLD-10"] = [$"Documented option for {request.Handoff.Load.Columns[^1]}"];
        PreviousProjectionRemember(context, prior);
        RememberBalancedExplicitBatch(context, [current]);
        var focused = current.Concat(prior).ToArray();

        // This control uses the same old continuity path kept for non-grid
        // requests. Both sides project the same canonical input under the same cap.
        var old = PreviousProjectionPriority(context, active, focused);
        var improved = PreviousProjectionPriority(context, active, focused, request.Handoff.Load);
        Assert.Equal(prior.Take(8).Select(item => item.Reference.EvidenceId),
            old.Take(16).Where(item => item.Reference.EvidenceId!.StartsWith("OLD-", StringComparison.Ordinal))
                .Select(item => item.Reference.EvidenceId));
        Assert.Equal("FRESH-1", improved[0].Reference.EvidenceId);
        Assert.Equal("OLD-10", improved[1].Reference.EvidenceId);
        var kept = improved.Take(16).Where(item => item.Reference.EvidenceId!.StartsWith("OLD-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(8, kept.Length);
        Assert.Equal(2, kept.Select(item => item.Reference.DocId).Distinct().Count());
        Assert.DoesNotContain("OLD-10", AccumulatedReadIds(context));

        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.MaximumEvidencePromptCharacters = cap;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string Project(IReadOnlyList<AdvancedAnalysisResolvedEvidence> order) => JsonSerializer.Serialize(
            InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", request, active, queries, order,
                new Dictionary<string, string>(), null, null)!, jsonOptions);
        using var before = JsonDocument.Parse(Project(old));
        using var after = JsonDocument.Parse(Project(improved));
        Assert.DoesNotContain(before.RootElement.EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == "OLD-10");
        var restored = Assert.Single(after.RootElement.EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == "OLD-10");
        Assert.Equal(prior[9].Content, restored.GetProperty("content").GetString());
        Assert.False(restored.GetProperty("contentTruncated").GetBoolean());

        // A real explicit page remains first, even with the named-grid rule active.
        RememberBalancedExplicitBatch(context, [[current[^1]]], "read_source");
        Assert.Equal("FRESH-15", PreviousProjectionPriority(context, active, focused, request.Handoff.Load)[0].Reference.EvidenceId);
    }
}
