using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public async Task Follow_up_context_is_separate_non_evidence_data_in_actual_planner_and_writer_payloads()
    {
        const string current = "Repeat the documented result with my latest constraints.";
        const string earlier = "Initially exclude the synthetic RED item.";
        const string revised = "I now allow RED; exclude the synthetic BLUE item instead.";
        var original = BuildDirectRequest();
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
                        new() { Role = "assistant", Text = "An earlier suggestion, never a source." },
                        new() { Role = "user", Text = revised }]
                }
            }
        };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(request, new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);

        Assert.Equal(2, factory.Requests.Count);
        foreach (var sent in factory.Requests)
        {
            var body = WorkspaceUser(sent.Body);
            Assert.Equal(current, body.GetProperty("request").GetString());
            var context = body.GetProperty("conversationContext");
            Assert.False(context.GetProperty("isDocumentaryEvidence").GetBoolean());
            Assert.Contains("never documentary evidence", context.GetProperty("policy").GetString());
            var turns = context.GetProperty("turns").EnumerateArray().ToArray();
            Assert.Equal(earlier, turns[0].GetProperty("text").GetString());
            Assert.Equal("assistant", turns[1].GetProperty("role").GetString());
            Assert.Equal(revised, turns[2].GetProperty("text").GetString());
        }
    }
}
