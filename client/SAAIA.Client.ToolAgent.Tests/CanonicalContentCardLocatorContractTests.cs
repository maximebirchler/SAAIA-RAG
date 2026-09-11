using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CanonicalContentCardLocatorContractTests
{
    private const string Question = "Read the recorded measurements and their validity conditions.";

    [Fact]
    public void Real_title_cards_keep_exact_identity_but_cannot_be_cited_without_source_text()
    {
        var bundle = LoadBundle();
        Assert.Equal(2, bundle.Items.Count);
        Assert.All(bundle.Items, item =>
        {
            Assert.Equal("canonical_content_card", item.SourceKind);
            Assert.Equal("Knowledge/Calibration-record.pdf", item.DocPath);
            Assert.True(Guid.TryParse(item.DocId, out _));
            Assert.True(Guid.TryParse(item.RevisionId, out _));
            Assert.True(Guid.TryParse(item.ContentCardId, out _));
            Assert.Equal(64, item.SourceHash!.Length);
            Assert.Null(item.ChunkId);
            Assert.Contains(SourceBackedContentCardEvidenceContract.MissingGroundedEvidenceRisk, item.RiskFlags);
            Assert.False(Call<bool>("IsMechanicallyCitableCandidate", item));
            var verification = SourceContractVerifier.Verify(
                new WriterDraft($"A recorded measurement. [{item.EvidenceId}]", [item.EvidenceId]),
                bundle, new SourceBackedIntake(Question, "answer", [], [], false, "en"), [item.EvidenceId]);
            Assert.False(verification.IsValid);
        });
    }

    [Fact]
    public void Real_title_cards_remain_available_as_document_focus()
    {
        var bundle = LoadBundle();
        var inventory = Call<string>("BuildDocumentFocusInventory", bundle, 20);
        Assert.Contains("Knowledge/Calibration-record.pdf", inventory);
        Assert.Contains(bundle.Items[0].DocId!, inventory);
    }

    [Fact]
    public void Both_unread_card_pages_are_offered_as_resolvable_locators()
    {
        var bundle = LoadBundle();
        var ids = Call<IReadOnlyList<string>>("SelectPendingNavigationEvidenceIds", bundle, new HashSet<string>(), 20, true);
        Assert.Equal(bundle.Items.Select(item => item.EvidenceId), ids);
        var remaining = Call<IReadOnlyList<string>>("SelectPendingNavigationEvidenceIds", bundle,
            new HashSet<string> { ids[0] }, 20, true);
        Assert.Equal([ids[1]], remaining);
    }

    [Fact]
    public void Research_transition_exposes_the_two_observed_titles_and_the_resolution_action()
    {
        var bundle = LoadBundle();
        var messages = Call<IReadOnlyList<SourceBackedAgentMessage>>("BuildResearchTransitionDecisionMessages",
            new SourceBackedIntake(Question, "answer", [], [], false, "en"), "Read the requested source facts.",
            "content_claim", "", bundle, new HashSet<string>(), Array.Empty<RetrievalRequest>(),
            Array.Empty<string>(), 0, 3, null, 20, false);
        var prompt = JsonSerializer.Serialize(messages);
        Assert.Contains("AMBER CALIBRATION", prompt);
        Assert.Contains("COBALT CALIBRATION", prompt);
        var tools = Call<IReadOnlyList<SourceBackedAgentToolDefinition>>("BuildResearchTransitionTools",
            bundle, new HashSet<string>(), Array.Empty<RetrievalRequest>(), 20);
        var resolve = Assert.Single(tools, tool => tool.Name == "resolve_navigation_anchors");
        var ids = resolve.Parameters.GetProperty("properties").GetProperty("evidenceIds")
            .GetProperty("items").GetProperty("enum").EnumerateArray().Select(value => value.GetString());
        Assert.Equal(bundle.Items.Select(item => item.EvidenceId), ids);
    }

    [Fact]
    public void Qwen_selected_title_cards_expand_to_the_exact_observed_document_and_pages()
    {
        var bundle = LoadBundle();
        object?[] args = [JsonSerializer.SerializeToElement(new { evidenceIds = bundle.Items.Select(item => item.EvidenceId) }),
            bundle, null, null];
        var accepted = (bool)Method("TryExpandNavigationContextBatchArguments").Invoke(null, args)!;
        Assert.True(accepted, args[3]?.ToString());
        var expanded = Assert.IsType<JsonElement>(args[2]);
        var targets = expanded.GetProperty("targets").EnumerateArray().ToArray();
        Assert.Equal(2, targets.Length);
        foreach (var target in targets)
        {
            var item = bundle.ById[target.GetProperty("evidenceId").GetString()!];
            Assert.Equal(item.DocId, target.GetProperty("docId").GetString());
            Assert.Equal(item.DocPath, target.GetProperty("docPath").GetString());
            Assert.Equal(item.PageStart, target.GetProperty("pageStart").GetInt32());
            Assert.Equal(item.PageEnd, target.GetProperty("pageEnd").GetInt32());
            Assert.Equal(JsonValueKind.Null, target.GetProperty("chunkId").ValueKind);
        }
        Assert.All(bundle.Items, item => Assert.False(Call<bool>("IsMechanicallyCitableCandidate", item)));
    }

    [Theory]
    [InlineData("docId")]
    [InlineData("revisionId")]
    [InlineData("sourceHash")]
    [InlineData("contentCardId")]
    [InlineData("page")]
    public void Incomplete_card_identity_does_not_become_an_exact_locator(string missing)
    {
        var item = LoadBundle().Items[0];
        item = missing switch
        {
            "docId" => item with { DocId = null },
            "revisionId" => item with { RevisionId = null },
            "sourceHash" => item with { SourceHash = null },
            "contentCardId" => item with { ContentCardId = null },
            _ => item with { PageStart = null, PageEnd = null }
        };
        Assert.False(Call<bool>("IsMechanicallyResolvableNavigationLocator", item));
    }

    private static EvidenceBundle LoadBundle()
    {
        var response = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "CanonicalContentCardLocatorContract.json")));
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item { ToolName = "documents.content_cards", Result = response });
        return EvidenceBundleBuilder.FromToolResults(results, Question);
    }

    private static MethodInfo Method(string name)
        => typeof(SourceBackedAgentV2Runner).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException("Missing production method: " + name);

    private static T Call<T>(string name, params object?[] arguments)
        => (T)Method(name).Invoke(null, arguments)!;
}
