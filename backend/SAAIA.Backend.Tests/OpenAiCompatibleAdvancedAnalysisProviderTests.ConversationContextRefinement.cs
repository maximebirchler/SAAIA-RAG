using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("one_per_cell", "distinct", "distinct_named_items", "named_item", "distinct_structured_layout")]
    [InlineData("named_item", "structured_layout", "content_claims", "content_claim", "structured_layout")]
    public async Task Grid_planner_refinement_preserves_non_evidence_conversation_for_the_actual_writer(
        string initialMode, string initialPolicy, string plannerMode, string refinedMode, string refinedPolicy)
    {
        const string earlier = "Prepare the documented grid without the synthetic RED item.";
        const string revised = "RED is allowed now; exclude the synthetic BLUE item instead.";
        const string current = "Redo that same grid with the latest constraint.";
        var original = BuildRequest(answerUnitCount: 20, atomicEvidenceMode: initialMode, selectionPolicy: initialPolicy);
        var request = original with
        {
            Handoff = new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = original.Handoff.HandoffId,
                RequestText = current,
                Language = "en",
                Load = original.Handoff.Load,
                ResearchState = original.Handoff.ResearchState,
                ConversationContext = new()
                {
                    Turns = [new() { Role = "user", Text = earlier },
                        new() { Role = "assistant", Text = "An earlier proposal, not documentary evidence." },
                        new() { Role = "user", Text = revised }]
                }
            }
        };
        using var factory = new QueuedHttpClientFactory(
            Completion(JsonSerializer.Serialize(new { selectionMode = plannerMode,
                queries = new[] { new { query = "documented grid", topK = 12 } } })),
            Completion("""{"outcome":"clarification_required","answerText":"Clarify the eligible documented items.","claims":[]}"""));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, CreateOptions(), null)
            .ExecuteAsync(request, new RecordingToolGateway(BuildEvidence("E1", "A synthetic documented value.")), CancellationToken.None);

        Assert.Equal("clarification_required", result.Outcome);
        Assert.Equal(2, factory.Requests.Count);
        var planner = WorkspaceUser(factory.Requests[0].Body);
        var writer = WorkspaceUser(factory.Requests[1].Body);
        Assert.Equal(initialMode, planner.GetProperty("load").GetProperty("atomicEvidenceMode").GetString());
        Assert.Equal(refinedMode, writer.GetProperty("load").GetProperty("atomicEvidenceMode").GetString());
        Assert.Equal(refinedPolicy, writer.GetProperty("load").GetProperty("selectionPolicy").GetString());
        foreach (var payload in new[] { planner, writer })
        {
            Assert.Equal(current, payload.GetProperty("request").GetString());
            var context = payload.GetProperty("conversationContext");
            Assert.Equal(JsonValueKind.Object, context.ValueKind);
            Assert.False(context.GetProperty("isDocumentaryEvidence").GetBoolean());
            Assert.False(context.GetProperty("truncated").GetBoolean());
            var turns = context.GetProperty("turns").EnumerateArray().ToArray();
            Assert.Equal(new[] { earlier, "An earlier proposal, not documentary evidence.", revised },
                turns.Select(turn => turn.GetProperty("text").GetString()));
            Assert.Equal(new[] { "user", "assistant", "user" },
                turns.Select(turn => turn.GetProperty("role").GetString()));
            Assert.Contains("current request may revise or revoke", context.GetProperty("policy").GetString());
        }
    }
}
