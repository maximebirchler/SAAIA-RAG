using System.Text.Json.Nodes;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public async Task Incorrect_model_bindings_are_not_silently_reassigned_to_uncited_proofs()
    {
        var evidence = Enumerable.Range(1, 4)
            .Select(i => BuildEvidence(
                "E" + i,
                "Option " + (char)('A' + i - 1) + "\nHold at 17 bar for 3 minutes.",
                exactTitle: "Option " + (char)('A' + i - 1)))
            .ToArray();
        var claims = new JsonArray(Enumerable.Range(1, 4).Select(i => (JsonNode)new JsonObject
        {
            ["claimId"] = "C" + i,
            ["selectedItem"] = "Option " + (char)('A' + i - 1),
            ["text"] = "Chosen option " + i + ".",
            ["evidenceIds"] = new JsonArray("E" + (i % 4 + 1))
        }).ToArray());
        var answer = BuildMultiEvidenceAnswer(claims);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"overview","topK":12}]}"""),
            Completion(answer),
            Completion(answer));
        var options = WorkspaceOptions();
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;
        var gateway = new RecordingToolGateway(evidence);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 4, atomicEvidenceMode: "named_item"), gateway, CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
        Assert.Single(gateway.Searches);
    }

    [Fact]
    public async Task Model_selected_title_and_substantive_body_both_survive_identity_normalization()
    {
        var evidence = new List<AdvancedAnalysisResolvedEvidence>();
        var claims = new JsonArray();
        for (var i = 1; i <= 4; i++)
        {
            var name = "Option " + (char)('A' + i - 1);
            var title = BuildEvidence("TITLE" + i, name, exactTitle: name, pageStart: i * 2);
            var body = BuildEvidence("BODY" + i, "Set pressure to 17 bar and hold for 3 minutes. Use the pressure regulator.",
                exactTitle: "Parameters", docId: title.Reference.DocId, revisionId: title.Reference.RevisionId, pageStart: i * 2 + 1);
            evidence.Add(title);
            evidence.Add(body);
            claims.Add(new JsonObject
            {
                ["claimId"] = "C" + i,
                ["selectedItem"] = name,
                ["text"] = name + " uses 17 bar.",
                ["evidenceIds"] = new JsonArray("TITLE" + i, "BODY" + i)
            });
        }
        var answer = BuildMultiEvidenceAnswer(claims);
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"overview","topK":12}]}"""),
            Completion(answer),
            Completion(answer));
        var options = WorkspaceOptions();
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;
        var gateway = new RecordingToolGateway(evidence.ToArray());
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 4, atomicEvidenceMode: "named_item"), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Single(gateway.Searches);
        for (var i = 1; i <= 4; i++)
            Assert.Equal(new[] { "TITLE" + i, "BODY" + i }, result.Claims[i - 1].EvidenceIds);
    }

    private static string BuildMultiEvidenceAnswer(JsonArray claims) => new JsonObject
    {
        ["outcome"] = "answered",
        ["answerText"] = "Option A [C1], Option B [C2], Option C [C3], Option D [C4].",
        ["claims"] = claims
    }.ToJsonString();
}
