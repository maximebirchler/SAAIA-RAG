using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string ExplorerReady =
        """{"outcome":"candidate_dossier_ready","reason":"Verified coverage is ready for synthesis."}""";
    private const string ExplorerBounded =
        """{"outcome":"candidate_dossier_bounded","reason":"The reserved Writer call leaves no further research call."}""";

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Candidate_explorer_builds_a_complete_dossier_before_writer_and_critic(
        string protocol)
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var roles = new[] { "petit-déjeuner", "déjeuner", "collation", "souper" };
        var evidence = Enumerable.Range(1, 20).Select(index => BuildEvidence(
                $"E{index}",
                $"R{index}\nINGRÉDIENTS\nÉlément {index}. PRÉPARATION\nProcédure {index}.",
                docId: documentId,
                revisionId: revisionId,
                exactTitle: $"R{index}"))
            .ToArray();
        var update = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(1, 20).Select(index => new
            {
                key = $"candidate-{index}",
                exactTitle = $"R{index}",
                sourceKey = "internal-source-1",
                targetRoles = new[] { roles[(index - 1) / 5] },
                selectedRoles = Array.Empty<string>(),
                status = "body_verified",
                note = "Verified by the Explorer.",
                locatorEvidenceIds = Array.Empty<string>(),
                bodyEvidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            protocol == "responses"
                ? NativeResponseFunction("save_candidate_inventory", update)
                : NativeCompletion(("save_candidate_inventory", update)),
            protocol == "responses"
                ? NativeResponseAnswer(ExplorerReady)
                : Completion(ExplorerReady),
            protocol == "responses"
                ? NativeResponseAnswer(CandidateAnswered(20))
                : Completion(CandidateAnswered(20)),
            protocol == "responses"
                ? NativeResponseAnswer(CandidateAnswered(20))
                : Completion(CandidateAnswered(20)));
        var options = WorkspaceOptions(protocol);
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 7;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(
                factory,
                options,
                null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new RecordingToolGateway(evidence),
                CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal(5, factory.Requests.Count);
        var writer = WorkspaceUser(factory.Requests[3].Body);
        var dossier = writer.GetProperty("candidateDossier");
        Assert.Equal("ready", dossier.GetProperty("outcome").GetString());
        Assert.Equal(20, dossier.GetProperty("bodyVerifiedDistinctCount").GetInt32());
        Assert.All(
            dossier.GetProperty("roles").EnumerateArray(),
            role => Assert.Equal(5, role.GetProperty("bodyVerifiedCount").GetInt32()));
        Assert.Equal(
            20,
            writer.GetProperty("candidateInventory")
                .GetProperty("bodyVerifiedDistinctCount")
                .GetInt32());
    }

    [Fact]
    public async Task Candidate_explorer_rejects_premature_ready_then_hands_writer_a_bounded_gap()
    {
        var evidence = BuildEvidence(
            "E1",
            "R1\nINGRÉDIENTS\nÉlément. PRÉPARATION\nProcédure.",
            exactTitle: "R1");
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            Completion(ExplorerReady),
            Completion(ExplorerBounded),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 4;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(
                factory,
                options,
                null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new RecordingToolGateway(evidence),
                CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(4, factory.Requests.Count);
        var correction = WorkspaceUser(factory.Requests[2].Body);
        Assert.Equal(
            "candidate_dossier_coverage_incomplete",
            correction.GetProperty("explorerFeedback")
                .GetProperty("reasonCode")
                .GetString());
        Assert.False(correction.GetProperty("researchTools")
            .GetProperty("researchAllowed").GetBoolean());
        var writer = WorkspaceUser(factory.Requests[3].Body);
        var dossier = writer.GetProperty("candidateDossier");
        Assert.Equal("bounded_gap", dossier.GetProperty("outcome").GetString());
        Assert.Equal(1, dossier.GetProperty("bodyVerifiedDistinctCount").GetInt32());
        Assert.Equal(20, dossier.GetProperty("requiredDistinctCount").GetInt32());
    }

    [Fact]
    public async Task Candidate_explorer_uses_global_coverage_for_a_flat_named_collection()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var evidence = Enumerable.Range(1, 2).Select(index => BuildEvidence(
                $"E{index}",
                $"R{index}\nSubstantive documented body for candidate {index}.",
                docId: documentId,
                revisionId: revisionId,
                exactTitle: $"R{index}"))
            .ToArray();
        var update = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(1, 2).Select(index => new
            {
                key = $"flat-{index}",
                exactTitle = $"R{index}",
                sourceKey = "internal-source-1",
                targetRoles = Array.Empty<string>(),
                selectedRoles = Array.Empty<string>(),
                status = "body_verified",
                note = "Suitable for the request-wide flat collection.",
                locatorEvidenceIds = Array.Empty<string>(),
                bodyEvidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(("save_candidate_inventory", update)),
            Completion(ExplorerReady),
            Completion(CandidateAnswered(2)));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.ExternalMaximumCallsPerJob = 4;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(
                factory,
                options,
                null)
            .ExecuteAsync(
                BuildFlatNamedItemRequest(),
                new RecordingToolGateway(evidence),
                CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(2, result.Claims.Count);
        var dossier = WorkspaceUser(factory.Requests[3].Body)
            .GetProperty("candidateDossier");
        Assert.Equal("ready", dossier.GetProperty("outcome").GetString());
        Assert.Equal(2, dossier.GetProperty("bodyVerifiedDistinctCount").GetInt32());
        Assert.Empty(dossier.GetProperty("roles").EnumerateArray());
    }

    [Fact]
    public async Task Native_inventory_accepts_a_realistic_twenty_candidate_batch_above_the_old_envelope()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var candidates = Enumerable.Range(1, 20).Select(index => new
        {
            key = $"candidate-{index:D2}-" + new string('k', 48),
            exactTitle = $"Verified candidate number {index:D2} with a stable exact title",
            sourceKey = "internal-source-1",
            targetRoles = new[] { "petit-déjeuner" },
            selectedRoles = Array.Empty<string>(),
            status = "body_verified",
            note = "Bounded Explorer note preserving the documentary decision across focus changes. "
                   + new string('n', 100),
            locatorEvidenceIds = Array.Empty<string>(),
            bodyEvidenceIds = new[] { $"advanced-evidence-{index:x32}" }
        }).ToArray();
        var update = JsonSerializer.Serialize(new { items = candidates });
        Assert.True(update.Length > 8_192);
        Assert.True(update.Length < 16_384);
        var evidence = candidates.Select(candidate => BuildEvidence(
                candidate.bodyEvidenceIds[0],
                candidate.exactTitle + "\nSubstantive documented body.",
                docId: documentId,
                revisionId: revisionId))
            .ToArray();
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(("save_candidate_inventory", update)),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.ExternalMaximumCallsPerJob = 3;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(
                factory,
                options,
                null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new RecordingToolGateway(evidence),
                CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(
            20,
            WorkspaceUser(factory.Requests[2].Body)
                .GetProperty("candidateInventory")
                .GetProperty("items")
                .GetArrayLength());
    }

    [Fact]
    public async Task Candidate_explorer_requires_the_native_agent_workspace()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.NativeResearchWorkspaceEnabled = false;

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(
                    BuildRequest(
                        answerUnitCount: 20,
                        atomicEvidenceMode: "named_item",
                        selectionPolicy: "distinct_structured_layout"),
                    new RecordingToolGateway(),
                    CancellationToken.None));

        Assert.Equal("advanced_candidate_explorer_configuration_invalid", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public async Task Candidate_explorer_requires_history_capacity_for_a_bounded_inventory_batch()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 16_384;

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(
                    BuildRequest(
                        answerUnitCount: 20,
                        atomicEvidenceMode: "named_item",
                        selectionPolicy: "distinct_structured_layout"),
                    new RecordingToolGateway(),
                    CancellationToken.None));

        Assert.Equal("advanced_candidate_explorer_configuration_invalid", error.ErrorCode);
        Assert.Empty(factory.Requests);
    }
}
