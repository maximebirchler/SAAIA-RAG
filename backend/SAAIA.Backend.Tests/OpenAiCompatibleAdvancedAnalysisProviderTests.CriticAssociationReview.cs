using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string AssociationPartial = """{"outcome":"insufficient_documentation","answerText":"Module Atlas reste à vérifier [C2].","claims":[{"claimId":"C2","selectedItem":"Module Atlas","text":"Module Atlas fonctionne à 17 bar et à froid.","evidenceIds":["E1","E2"]}]}""";

    [Theory]
    [InlineData("responses", "contract")]
    [InlineData("responses", "agent")]
    [InlineData("chat-completions", "contract")]
    [InlineData("chat-completions", "agent")]
    public async Task CriticAssociationReview_exposes_source_groups_without_transferring_identity_from_metadata(
        string protocol, string style)
    {
        var first = BuildEvidence("E1", "Module Atlas\nPression maximale : 17 bar.", "first.pdf", "doc1", "rev1");
        var second = BuildEvidence("E2", "Utilisation à froid possible.", "second.pdf", "doc2", "rev2",
            exactTitle: "Module Atlas") with
        {
            SourceStructure = new("Module Atlas", "Module Atlas", null, null)
            { ContainsUnassignedPhysicalContext = true }
        };
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner),
            EarlyReply(protocol, AssociationPartial), EarlyReply(protocol, AssociationPartial));
        var options = WorkspaceOptions(protocol);
        options.SemanticCriticEnabled = true; options.SynthesisPromptStyle = style;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildFixedSubjectRequest(false), new RecordingToolGateway(first, second), CancellationToken.None);

        var prompt = WorkspaceUser(factory.Requests[2].Body);
        var review = Assert.Single(prompt.GetProperty("citationReview").GetProperty("namedItemSourceAssociations").EnumerateArray());
        Assert.Equal("C2", review.GetProperty("claimId").GetString());
        Assert.True(review.GetProperty("multipleSourceVersions").GetBoolean());
        var groups = review.GetProperty("sourceGroups").EnumerateArray().ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal("E1", Assert.Single(groups[0].GetProperty("visibleNameMentionEvidenceIds").EnumerateArray()).GetString());
        Assert.Empty(groups[1].GetProperty("visibleNameMentionEvidenceIds").EnumerateArray());
        Assert.Equal("E2", Assert.Single(groups[1].GetProperty("unassignedPhysicalContextEvidenceIds").EnumerateArray()).GetString());
        Assert.DoesNotContain("17 bar", review.GetRawText(), StringComparison.Ordinal);
        Assert.False(review.TryGetProperty("requestedCoordinate", out _));
        // This is review context, not a lexical veto of legitimate multisource synthesis.
        Assert.Equal(new[] { "E1", "E2" }, Assert.Single(result.Claims).EvidenceIds);
        Assert.Equal("insufficient_documentation", result.Outcome);
    }

    [Theory]
    [InlineData("same", false, false)]
    [InlineData("same", true, false)]
    [InlineData("revision", false, true)]
    [InlineData("document", false, true)]
    public async Task CriticAssociationReview_groups_provenance_and_highlights_unassigned_same_source_context(
        string difference, bool unassigned, bool multiple)
    {
        var first = BuildEvidence("E1", "Module Atlas\nPression maximale : 17 bar.", "limits.pdf", "doc1", "rev1");
        var second = BuildEvidence("E2", "Module Atlas\nUtilisation à froid possible.", "limits.pdf",
            difference == "document" ? "doc2" : "doc1", difference == "revision" ? "rev2" : "rev1", pageStart: 2) with
        {
            SourceStructure = new(null, null, null, null) { ContainsUnassignedPhysicalContext = unassigned }
        };
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner),
            Completion(AssociationPartial), Completion(AssociationPartial));
        var options = WorkspaceOptions(); options.SemanticCriticEnabled = true;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildFixedSubjectRequest(false), new RecordingToolGateway(first, second), CancellationToken.None);

        var prompt = WorkspaceUser(factory.Requests[2].Body);
        var reviews = prompt.GetProperty("citationReview").GetProperty("namedItemSourceAssociations");
        if (!multiple && !unassigned) Assert.Empty(reviews.EnumerateArray());
        else
        {
            var review = Assert.Single(reviews.EnumerateArray());
            Assert.Equal(multiple, review.GetProperty("multipleSourceVersions").GetBoolean());
            var groups = review.GetProperty("sourceGroups").EnumerateArray().ToArray();
            Assert.Equal(multiple ? 2 : 1, groups.Length);
            Assert.All(groups, group => Assert.NotEmpty(group.GetProperty("visibleNameMentionEvidenceIds").EnumerateArray()));
            Assert.Equal(unassigned ? 1 : 0, groups.Sum(group => group.GetProperty("unassignedPhysicalContextEvidenceIds").GetArrayLength()));
        }
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task CriticAssociationReview_refreshes_name_observation_only_after_its_window_is_read(string protocol)
    {
        var first = BuildEvidence("E1", "Module Atlas\nPression maximale : 17 bar.", "first.pdf", "doc1", "rev1");
        var second = BuildEvidence("E2", new string('x', 4_000) + "\nModule Atlas\nUtilisation à froid possible.",
            "second.pdf", "doc2", "rev2");
        var answer = AssociationPartial.Replace("insufficient_documentation", "answered", StringComparison.Ordinal);
        using var factory = new QueuedHttpClientFactory(Completion(CandidatePlanner),
            EarlyReply(protocol, answer),
            PostResearchFunction(protocol, "read_evidence", """{"evidenceId":"E2","offset":4000,"length":256}"""),
            EarlyReply(protocol, answer));
        var options = WorkspaceOptions(protocol); options.SemanticCriticEnabled = true;
        options.MaximumEvidencePromptCharacters = 8_000;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(first, second), CancellationToken.None);

        var initial = WorkspaceUser(factory.Requests[2].Body);
        var refreshed = WorkspaceUser(factory.Requests[3].Body);
        static JsonElement SecondGroup(JsonElement prompt) => prompt.GetProperty("citationReview")
            .GetProperty("namedItemSourceAssociations")[0].GetProperty("sourceGroups")
            .EnumerateArray().Single(group => group.GetProperty("evidenceIds").EnumerateArray().Any(id => id.GetString() == "E2"));
        Assert.Empty(SecondGroup(initial).GetProperty("visibleNameMentionEvidenceIds").EnumerateArray());
        Assert.Equal("E2", Assert.Single(SecondGroup(refreshed).GetProperty("visibleNameMentionEvidenceIds").EnumerateArray()).GetString());
    }
}
