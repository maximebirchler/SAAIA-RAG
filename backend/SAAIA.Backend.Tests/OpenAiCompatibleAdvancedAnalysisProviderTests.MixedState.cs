using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", true)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", true)]
    [InlineData("responses", false)]
    public async Task Mixed_inventory_update_and_evidence_read_reopen_the_merged_candidate_and_save_only_the_final_state(
        string protocol, bool saveFirst)
    {
        var body = BuildEvidence("E1", "R1\n" + new string('x', 3_000)
            + "\nA documented continuation for the requested item.", exactTitle: "R1");
        var key = LifecycleCandidateKey("R1");
        string NeedsBody(string note) => JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    key, exactTitle = "R1", sourceKey = "internal-source-1",
                    targetRoles = Array.Empty<string>(), selectedRoles = Array.Empty<string>(),
                    status = "needs_body", note,
                    locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { "E1" }
                }
            }
        });
        const string read = """{"evidenceId":"E1","offset":3000,"length":512}""";
        var update = NeedsBody("The continuation must be read before reassessment.");
        var mixed = saveFirst
            ? new[] { ("save_candidate_inventory", update), ("read_evidence", read) }
            : new[] { ("read_evidence", read), ("save_candidate_inventory", update) };
        HttpResponseMessage Calls(params (string Name, string Arguments)[] calls)
            => protocol == "chat-completions" ? NativeCompletion(calls)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        status = "completed",
                        output = calls.Select((call, index) => new
                        {
                            id = $"fc_{index}", type = "function_call", call_id = $"call_{index}",
                            name = call.Name, arguments = call.Arguments, status = "completed"
                        }).ToArray(),
                        usage = new { input_tokens = 100, output_tokens = 50 }
                    })
                };
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            Calls(("save_candidate_inventory", NeedsBody("An incomplete item needs a continuation."))),
            Calls(mixed),
            protocol == "responses" ? NativeResponseAnswer(CandidateTerminal) : Completion(CandidateTerminal));
        var gateway = new MixedStateCheckpointGateway(body);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
            .ExecuteAsync(BuildFlatNamedItemRequest(), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Single(gateway.Searches);
        var next = WorkspaceUser(factory.Requests[3].Body);
        var candidate = Assert.Single(next.GetProperty("candidateInventory").GetProperty("items").EnumerateArray());
        Assert.Equal("awaiting_review", candidate.GetProperty("status").GetString());
        Assert.Equal("The continuation must be read before reassessment.", candidate.GetProperty("note").GetString());
        var excerpt = Assert.Single(next.GetProperty("evidence").EnumerateArray());
        Assert.Equal(3_000, excerpt.GetProperty("contentOffset").GetInt32());
        Assert.Contains("documented continuation", excerpt.GetProperty("content").GetString());
        Assert.Equal(3, gateway.Checkpoints.Count); // observed, needs_body, one atomic mixed batch
        var checkpoint = gateway.Checkpoints[^1];
        Assert.Equal("awaiting_review", Assert.Single(checkpoint.Candidates).Status);
        Assert.Equal(3_000, checkpoint.EvidenceReadWindows!["E1"].Offset);
    }

    private sealed class MixedStateCheckpointGateway(params AdvancedAnalysisResolvedEvidence[] evidence)
        : IAdvancedAnalysisToolGateway
    {
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => Searches.Count == 0 ? [] : evidence;
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public List<AdvancedAnalysisResearchCheckpoint> Checkpoints { get; } = [];

        public Task<AdvancedAnalysisResearchCheckpoint?> LoadResearchCheckpointAsync(CancellationToken cancellationToken)
            => Task.FromResult<AdvancedAnalysisResearchCheckpoint?>(null);

        public Task SaveResearchCheckpointAsync(AdvancedAnalysisResearchCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            Checkpoints.Add(checkpoint);
            return Task.CompletedTask;
        }

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
        {
            Searches.Add(request);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, evidence, [], 1, 1));
        }
    }
}
