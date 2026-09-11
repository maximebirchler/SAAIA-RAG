using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedCategoryScopeYieldRecoveryTests
{
    private static readonly string[] VisibleCategoryPaths =
    [
        "Knowledge",
        "Certificates",
        "Technical documents"
    ];

    [Fact]
    public void ZeroYieldObservation_exposes_the_executed_category_scope()
    {
        using var observation = BuildObservation(hasEvidence: false);

        Assert.Equal(
            "Certificates",
            ScopeYield(observation).GetProperty("requestedCategoryPath").GetString());
    }

    [Fact]
    public void ZeroYieldObservation_marks_a_visible_catalog_scope_as_listed()
    {
        using var observation = BuildObservation(hasEvidence: false);

        Assert.True(ScopeYield(observation).GetProperty("requestedScopeListed").GetBoolean());
    }

    [Fact]
    public void ZeroYieldObservation_names_the_mechanical_zero_evidence_status()
    {
        using var observation = BuildObservation(hasEvidence: false);

        Assert.Equal(
            "zero_evidence_in_requested_scope",
            ScopeYield(observation).GetProperty("yieldStatus").GetString());
    }

    [Fact]
    public void ZeroYieldObservation_exposes_exact_visible_catalog_paths_without_ranking()
    {
        using var observation = BuildObservation(hasEvidence: false);

        Assert.Equal(
            VisibleCategoryPaths,
            ScopeYield(observation)
                .GetProperty("visibleCategoryPaths")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
    }

    [Fact]
    public void ZeroYieldObservation_offers_a_retry_without_category_scope()
    {
        using var observation = BuildObservation(hasEvidence: false);

        Assert.Contains(
            "retry_without_category_scope",
            RecoveryOptions(observation));
    }

    [Fact]
    public void ZeroYieldObservation_offers_another_exact_catalog_scope()
    {
        using var observation = BuildObservation(hasEvidence: false);

        Assert.Contains(
            "retry_with_another_exact_catalog_scope",
            RecoveryOptions(observation));
    }

    [Fact]
    public void ZeroYieldObservation_keeps_semantic_choice_with_the_llm()
    {
        using var observation = BuildObservation(hasEvidence: false);
        var scopeYield = ScopeYield(observation);

        Assert.Equal("llm", scopeYield.GetProperty("semanticDecisionOwner").GetString());
        Assert.False(scopeYield.TryGetProperty("recommendedCategoryPath", out _));
    }

    [Fact]
    public void EvidenceBearingObservation_does_not_emit_zero_yield_recovery()
    {
        using var observation = BuildObservation(hasEvidence: true);

        Assert.False(observation.RootElement.TryGetProperty("scopeYield", out _));
    }

    private static JsonDocument BuildObservation(bool hasEvidence)
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = JsonSerializer.SerializeToElement(new
            {
                hits = hasEvidence
                    ? new object[]
                    {
                        new
                        {
                            docId = "doc-1",
                            docName = "certificate.pdf",
                            docPath = "Technical documents/certificate.pdf",
                            categoryPath = "Technical documents",
                            pageStart = 1,
                            pageEnd = 1,
                            chunkId = "chunk-1",
                            excerpt = "The product meets the stated certificate requirement."
                        }
                    }
                    : Array.Empty<object>()
            })
        });
        var bundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "Find one certificate.");
        using var request = JsonDocument.Parse(
            """{"query":"certificate product","categoryPath":"Certificates","topK":5}""");
        var build = typeof(SourceBackedAgentObservationCompactor)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(static method =>
                method.Name == nameof(SourceBackedAgentObservationCompactor.Build)
                && method.GetParameters().Length == 7);
        Assert.NotNull(build);
        var json = Assert.IsType<string>(build!.Invoke(
            null,
            [
                "rag_search",
                results.Items,
                bundle,
                1,
                Options(),
                request.RootElement.Clone(),
                VisibleCategoryPaths
            ]));
        return JsonDocument.Parse(json);
    }

    private static JsonElement ScopeYield(JsonDocument observation)
        => observation.RootElement.GetProperty("scopeYield");

    private static string[] RecoveryOptions(JsonDocument observation)
        => ScopeYield(observation)
            .GetProperty("recoveryOptions")
            .EnumerateArray()
            .Select(static item => item.GetString() ?? string.Empty)
            .ToArray();

    private static SourceBackedAgentV2Options Options()
        => new(
            MaximumTurns: 4,
            MaximumToolCalls: 8,
            MaximumObservationItems: 4,
            MaximumObservationExcerptCharacters: 160,
            MaximumOutputTokens: 256);
}
