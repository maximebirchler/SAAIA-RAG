using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public void Partial_selection_counts_named_choices_instead_of_placeholder_claims()
    {
        var load = BuildRequest(20, "named_item", "distinct_structured_layout").Handoff.Load;
        var claims = Enumerable.Range(1, 20).Select(index => new AdvancedAnalysisResultClaim
        {
            ClaimId = $"C{index}", SelectedItem = index <= 13 ? $"Option {index}" : null,
            Text = index <= 13 ? "Documented option." : "A missing position.", EvidenceIds = ["E1"]
        }).ToArray();
        var counts = OpenAiCompatibleAdvancedAnalysisProvider.DescribeSelectionProgress(load, claims)!;
        Assert.Equal(20, counts.ClaimCount);
        Assert.Equal(13, counts.NamedClaimCount);
        Assert.Equal(13, counts.DistinctNamedItemCount);
        Assert.Equal(7, counts.ClaimsWithoutSelectedItem);
        Assert.Equal(7, counts.RemainingSelectionCount);
        Assert.False(counts.SuitabilityVerified);
    }

    [Fact]
    public void Selection_progress_respects_allowed_reuse_and_leaves_content_claims_unchanged()
    {
        var claims = Enumerable.Range(1, 2).Select(index => new AdvancedAnalysisResultClaim
        {
            ClaimId = $"C{index}", SelectedItem = "OptionA", Text = "Documented.", EvidenceIds = ["E1"]
        }).ToArray();
        var repeatable = BuildRequest(20, "named_item", "structured_layout").Handoff.Load;
        var distinct = BuildRequest(20, "named_item", "distinct_structured_layout").Handoff.Load;
        Assert.Equal(18, OpenAiCompatibleAdvancedAnalysisProvider.DescribeSelectionProgress(repeatable, claims)!.RemainingSelectionCount);
        Assert.Equal(19, OpenAiCompatibleAdvancedAnalysisProvider.DescribeSelectionProgress(distinct, claims)!.RemainingSelectionCount);
        var content = BuildRequest(20, "content_claims", "structured_layout", "property").Handoff.Load;
        var facts = new[] { new AdvancedAnalysisResultClaim { ClaimId = "fact", Text = "A documented fact.", EvidenceIds = ["E1"] } };
        Assert.Null(OpenAiCompatibleAdvancedAnalysisProvider.DescribeSelectionProgress(content, facts));
    }

    [Theory]
    [InlineData("insufficient_documentation", false)]
    [InlineData("insufficient_documentation", true)]
    [InlineData("clarification_required", false)]
    [InlineData("clarification_required", true)]
    public async Task Partial_selection_preserves_contextual_facts_and_reports_actual_choices_to_critic(string outcome, bool repair)
    {
        var partial = JsonSerializer.Serialize(new
        {
            outcome,
            answerText = "OptionA est documentée [A]. La limite documentée est 17 bar [B]. Les autres choix restent à établir.",
            claims = new[]
            {
                new { claimId = "A", selectedItem = (string?)"OptionA", text = "OptionA est documentée.", evidenceIds = new[] { "E1" } },
                new { claimId = "B", selectedItem = (string?)null, text = "La limite documentée est 17 bar.", evidenceIds = new[] { "E2" } }
            }
        });
        var responses = new List<System.Net.Http.HttpResponseMessage>
        {
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"options documentées","topK":12}]}"""),
            Completion(partial), Completion(repair ? "invalid json" : partial)
        };
        if (repair) responses.Add(Completion(partial));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var options = CreateOptions();
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = repair ? 4 : 3;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                new RecordingToolGateway(BuildEvidence("E1", "OptionA est documentée."),
                    BuildEvidence("E2", "La limite documentée est 17 bar.")), CancellationToken.None);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(2, result.Claims.Count);
        Assert.Equal("OptionA", result.Claims[0].SelectedItem);
        Assert.Null(result.Claims[1].SelectedItem);
        Assert.Contains("17 bar", result.AnswerText);
        Assert.Equal(options.ExternalMaximumCallsPerJob, factory.Requests.Count);
        foreach (var index in repair ? new[] { 2, 3 } : new[] { 2 })
        {
            var prompt = WorkspaceUser(factory.Requests[index].Body);
            if (index == 3) Assert.Equal("advanced_writer_protocol_invalid", prompt.GetProperty("validationErrorCode").GetString());
            var counts = prompt.GetProperty("selectionProgress");
            Assert.Equal(2, counts.GetProperty("claimCount").GetInt32());
            Assert.Equal(1, counts.GetProperty("namedClaimCount").GetInt32());
            Assert.Equal(1, counts.GetProperty("claimsWithoutSelectedItem").GetInt32());
            Assert.Equal(19, counts.GetProperty("remainingSelectionCount").GetInt32());
            Assert.False(counts.GetProperty("suitabilityVerified").GetBoolean());
        }
        var planner = SelectionPromptWords(PayloadAuditSystem(factory.Requests[0].Body));
        Assert.Contains("start in the user's request language", planner);
    }
}
