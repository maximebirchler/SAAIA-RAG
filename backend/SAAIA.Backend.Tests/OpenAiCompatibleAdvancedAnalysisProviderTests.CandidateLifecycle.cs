using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static string LifecycleCandidateKey(string title, string? evidenceId = null)
    {
        var identity = "internal-source-1\n" + title.ToLowerInvariant()
                       + (evidenceId is null ? "" : " " + evidenceId.ToLowerInvariant());
        return "candidate-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..16];
    }

    private static string LifecycleJudgeItem(
        string key, string title, string status, string[] evidenceIds,
        string[]? roles = null)
        => JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    key, exactTitle = title, sourceKey = "internal-source-1",
                    targetRoles = roles ?? [], selectedRoles = Array.Empty<string>(),
                    status,
                    note = status == "needs_body"
                        ? "Read the preceding physical page to establish the named item's suitability."
                        : "Suitability assessed from supplied canonical evidence.",
                    locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = evidenceIds
                }
            }
        });

    [Fact]
    public async Task Candidate_lifecycle_keeps_repeated_disjoint_headings_provisional_and_separate()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var evidence = new[]
        {
            BuildEvidence("E18", "INGRÉDIENTS\nAsperges et huile.",
                docId: documentId, revisionId: revisionId,
                pageStart: 18, exactTitle: "INGRÉDIENTS"),
            BuildEvidence("E20", "INGRÉDIENTS\nPoulet et feta.",
                docId: documentId, revisionId: revisionId,
                pageStart: 20, exactTitle: "INGRÉDIENTS")
        };
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner), Completion(CandidateTerminal));

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildFlatNamedItemRequest(), new RecordingToolGateway(evidence),
                CancellationToken.None);

        var inventory = WorkspaceUser(factory.Requests[1].Body)
            .GetProperty("candidateInventory");
        var items = inventory.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal("awaiting_review",
            item.GetProperty("status").GetString()));
        Assert.Equal(2, items.Select(item => item.GetProperty("key").GetString()).Distinct().Count());
        Assert.All(items, item => Assert.Single(item.GetProperty("bodyEvidenceIds").EnumerateArray()));
        Assert.Equal(0, inventory.GetProperty("bodyVerifiedDistinctCount").GetInt32());
    }

    [Fact]
    public async Task Candidate_lifecycle_flat_collection_requires_one_semantic_judgment_and_does_not_loop()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var evidence = Enumerable.Range(1, 2).Select(index => BuildEvidence(
            $"E{index}", $"R{index}\nSubstantive body establishing the documented item's use.",
            docId: documentId, revisionId: revisionId, exactTitle: $"R{index}")).ToArray();
        var judge = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(1, 2).Select(index => new
            {
                key = LifecycleCandidateKey($"R{index}"), exactTitle = $"R{index}",
                sourceKey = "internal-source-1", targetRoles = Array.Empty<string>(),
                selectedRoles = Array.Empty<string>(), status = "body_verified", note = "Suitable.",
                locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { $"E{index}" }
            })
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner), Completion(judge), Completion(CandidateAnswered(2)));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.ExternalMaximumCallsPerJob = 4;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildFlatNamedItemRequest(), new RecordingToolGateway(evidence),
                CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(3, factory.Requests.Count);
        var judgePrompt = WorkspaceUser(factory.Requests[1].Body);
        Assert.All(judgePrompt.GetProperty("candidateInventory").GetProperty("items").EnumerateArray(),
            item => Assert.Equal("awaiting_review", item.GetProperty("status").GetString()));
        Assert.Equal(2, WorkspaceUser(factory.Requests[2].Body).GetProperty("candidateDossier")
            .GetProperty("bodyVerifiedDistinctCount").GetInt32());
    }

    [Theory]
    [InlineData("needs_body")]
    [InlineData("rejected")]
    public async Task Candidate_lifecycle_new_body_allows_review_after_an_incomplete_or_rejected_observation(
        string firstStatus)
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var fragment = BuildEvidence("E1", "R1\nStep 4: serve.",
            docId: documentId, revisionId: revisionId, pageStart: 20, exactTitle: "R1");
        var complement = BuildEvidence("E2", "R1\nA documented named preparation with the required suitability.",
            docId: documentId, revisionId: revisionId, pageStart: 19, exactTitle: "R1");
        var firstKey = LifecycleCandidateKey("R1");
        var nextKey = firstStatus == "needs_body" ? firstKey : LifecycleCandidateKey("R1", "E2");
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            Completion(LifecycleJudgeItem(firstKey, "R1", firstStatus, ["E1"])),
            NativeCompletion(("find_source_text",
                """{"sourceKey":"internal-source-1","query":"R1","offset":0,"topK":20}""")),
            Completion(LifecycleJudgeItem(nextKey, "R1", "body_verified",
                firstStatus == "needs_body" ? ["E1", "E2"] : ["E2"], ["petit-déjeuner"])),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.ExternalMaximumCallsPerJob = 5;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                new SequencedToolGateway([fragment], [complement]), CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(5, factory.Requests.Count);
        var secondJudge = WorkspaceUser(factory.Requests[3].Body)
            .GetProperty("candidateInventory").GetProperty("items");
        Assert.Single(secondJudge.EnumerateArray());
        Assert.Equal("awaiting_review", secondJudge[0].GetProperty("status").GetString());
        Assert.Contains(secondJudge[0].GetProperty("bodyEvidenceIds").EnumerateArray(),
            id => id.GetString() == "E2");
        Assert.Equal(1, WorkspaceUser(factory.Requests[4].Body).GetProperty("candidateDossier")
            .GetProperty("bodyVerifiedDistinctCount").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Candidate_lifecycle_judge_can_discover_multiple_named_items_but_cannot_invent_their_identity(
        bool inventedIdentity)
    {
        var evidence = BuildEvidence("E1", "ITEMS\nR1\nA documented named preparation.\nR2\nAnother documented named preparation.",
            exactTitle: "ITEMS");
        var seedKey = LifecycleCandidateKey("ITEMS");
        var judge = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    key = seedKey, exactTitle = "R1", sourceKey = "internal-source-1",
                    targetRoles = Array.Empty<string>(), selectedRoles = Array.Empty<string>(),
                    status = "body_verified", note = "Suitable documented item.",
                    locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { "E1" }
                },
                new
                {
                    key = "discovery-r2", exactTitle = inventedIdentity ? "Undocumented item" : "R2",
                    sourceKey = "internal-source-1", targetRoles = Array.Empty<string>(),
                    selectedRoles = Array.Empty<string>(), status = "body_verified", note = "A second item from the same body.",
                    locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { "E1" }
                }
            }
        });
        var answer = JsonSerializer.Serialize(new
        {
            outcome = "answered", answerText = "R1 [C1], R2 [C2].",
            claims = Enumerable.Range(1, 2).Select(index => new
            {
                claimId = $"C{index}", selectedItem = $"R{index}", text = $"R{index} est documenté.",
                evidenceIds = new[] { "E1" }
            })
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner), Completion(judge), Completion(answer));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.ExternalMaximumCallsPerJob = 4;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        if (inventedIdentity)
        {
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                provider.ExecuteAsync(BuildFlatNamedItemRequest(), new RecordingToolGateway(evidence),
                    CancellationToken.None));
            Assert.Equal("advanced_candidate_judge_protocol_invalid", error.ErrorCode);
            Assert.Equal(2, factory.Requests.Count);
            return;
        }

        var result = await provider.ExecuteAsync(BuildFlatNamedItemRequest(),
            new RecordingToolGateway(evidence), CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(2, result.Claims.Count);
        Assert.Equal(2, WorkspaceUser(factory.Requests[2].Body).GetProperty("candidateDossier")
            .GetProperty("bodyVerifiedDistinctCount").GetInt32());
    }

    [Theory]
    [InlineData("saaia.advanced-analysis-research-checkpoint.v1", "awaiting_review")]
    [InlineData("saaia.advanced-analysis-research-checkpoint.v2", "body_verified")]
    public async Task Candidate_lifecycle_migrates_legacy_unjudged_flat_checkpoint_but_preserves_new_judged_state(
        string schemaVersion, string expectedState)
    {
        var evidence = BuildEvidence("E1", "R1\nDocumented substantive named item.", exactTitle: "R1");
        var gateway = new CheckpointToolGateway(evidence);
        await SeedLifecycleCheckpoint(gateway, evidence, schemaVersion,
            [new("flat-r1", "R1", "internal-source-1", [], [], "body_verified",
                "Legacy unassigned or new semantically judged item.", [], ["E1"])]);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner), Completion(CandidateTerminal));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildFlatNamedItemRequest(), gateway, CancellationToken.None);
        var item = Assert.Single(WorkspaceUser(factory.Requests[1].Body)
            .GetProperty("candidateInventory").GetProperty("items").EnumerateArray());
        Assert.Equal(expectedState, item.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Candidate_lifecycle_accepts_legacy_history_above_the_new_rejected_bound_after_validating_every_proof(
        bool invalidHistoricalProof)
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var evidence = Enumerable.Range(1, 38).Select(index => BuildEvidence(
            $"E{index}", $"R{index}\nCanonical named body.",
            docId: documentId, revisionId: revisionId, exactTitle: $"R{index}")).ToArray();
        var candidates = Enumerable.Range(1, 38).Select(index =>
            new AdvancedAnalysisCandidateCheckpoint($"legacy-{index}", $"R{index}", "internal-source-1",
                index == 1 ? ["petit-déjeuner"] : [], [], index == 1 ? "body_verified" : "rejected",
                "Historical assessment.", [],
                [invalidHistoricalProof && index == 2 ? "UNKNOWN-HISTORICAL-PROOF" : $"E{index}"]))
            .ToArray();
        var gateway = new CheckpointToolGateway(evidence);
        await SeedLifecycleCheckpoint(gateway, evidence[0],
            "saaia.advanced-analysis-research-checkpoint.v1", candidates);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner), Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.MaximumEvidencePromptCharacters = 1_000_000;
        options.ExternalMaximumCallsPerJob = 2;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var request = BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
            selectionPolicy: "distinct_structured_layout");
        if (invalidHistoricalProof)
        {
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                provider.ExecuteAsync(request, gateway, CancellationToken.None));
            Assert.Equal("advanced_research_checkpoint_invalid", error.ErrorCode);
            Assert.Single(factory.Requests);
            return;
        }

        await provider.ExecuteAsync(request, gateway, CancellationToken.None);
        var items = WorkspaceUser(factory.Requests[1].Body).GetProperty("candidateInventory")
            .GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(33, items.Length);
        Assert.Equal(32, items.Count(item => item.GetProperty("status").GetString() == "rejected"));
        Assert.DoesNotContain(items, item => item.GetProperty("status").GetString() == "awaiting_review");
    }

    [Fact]
    public async Task Candidate_lifecycle_retains_new_complementary_proof_instead_of_the_oldest_four_fragments()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var evidence = Enumerable.Range(1, 5).Select(index => BuildEvidence(
            $"E{index}", $"R1\nDocumentary fragment number {index}.",
            docId: documentId, revisionId: revisionId, pageStart: index, exactTitle: "R1")).ToArray();
        var gateway = new CheckpointToolGateway(evidence);
        await SeedLifecycleCheckpoint(gateway, evidence[0],
            AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion,
            [new("r1", "R1", "internal-source-1", [], [], "needs_body",
                "Read complementary proof.", [], ["E1", "E2", "E3", "E4"])]);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner), NativeCompletion(("search_corpus", WorkspaceSearch)),
            Completion(CandidateTerminal));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildFlatNamedItemRequest(), gateway, CancellationToken.None);
        var item = Assert.Single(WorkspaceUser(factory.Requests[2].Body)
            .GetProperty("candidateInventory").GetProperty("items").EnumerateArray());
        Assert.Equal("awaiting_review", item.GetProperty("status").GetString());
        Assert.Equal(["E2", "E3", "E4", "E5"], item.GetProperty("bodyEvidenceIds")
            .EnumerateArray().Select(id => id.GetString()!).ToArray());
    }

    [Fact]
    public async Task Candidate_lifecycle_rejected_history_does_not_consume_the_active_admission_capacity()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var evidence = Enumerable.Range(1, 97).Select(index => BuildEvidence(
            $"E{index}", $"R{index}\nSubstantive named body number {index}.",
            docId: documentId, revisionId: revisionId, exactTitle: $"R{index}")).ToArray();
        var candidates = Enumerable.Range(1, 95).Select(index =>
            new AdvancedAnalysisCandidateCheckpoint($"seed-{index}", $"R{index}", "internal-source-1",
                index <= 63 ? ["petit-déjeuner"] : [], [],
                index <= 63 ? "body_verified" : "rejected", "Assessed canonical item.", [], [$"E{index}"]))
            .ToArray();
        var gateway = new CheckpointToolGateway(evidence);
        await SeedLifecycleCheckpoint(gateway, evidence[0],
            AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion, candidates);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner), Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.MaximumEvidencePromptCharacters = 1_000_000;
        options.ExternalMaximumCallsPerJob = 2;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"), gateway, CancellationToken.None);

        var inventory = WorkspaceUser(factory.Requests[1].Body).GetProperty("candidateInventory");
        Assert.Equal(64, inventory.GetProperty("capacity").GetProperty("activeCount").GetInt32());
        Assert.Equal(32, inventory.GetProperty("capacity").GetProperty("rejectedHistoryCount").GetInt32());
        Assert.True(inventory.GetProperty("capacity").GetProperty("activeCapacityReached").GetBoolean());
        Assert.Equal(96, gateway.Checkpoint!.Candidates.Count);
        Assert.Single(gateway.Checkpoint.Candidates, item => item.Status == "awaiting_review");
    }

    [Fact]
    public async Task Candidate_lifecycle_trimmed_rejection_is_not_automatically_readmitted_and_rejudged()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var evidence = Enumerable.Range(1, 33).Select(index => BuildEvidence(
            $"E{index}", $"R{index}\nSubstantive but unsuitable named body.",
            docId: documentId, revisionId: revisionId, exactTitle: $"R{index}")).ToArray();
        var candidates = Enumerable.Range(1, 33).Select(index =>
            new AdvancedAnalysisCandidateCheckpoint($"seed-{index}", $"R{index}", "internal-source-1",
                [], [], index == 33 ? "awaiting_review" : "rejected", "Assessed or pending item.", [], [$"E{index}"]))
            .ToArray();
        var gateway = new CheckpointToolGateway(evidence);
        await SeedLifecycleCheckpoint(gateway, evidence[0],
            AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion, candidates);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            Completion(LifecycleJudgeItem("seed-33", "R33", "rejected", ["E33"])),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.MaximumEvidencePromptCharacters = 1_000_000;
        options.ExternalMaximumCallsPerJob = 4;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildFlatNamedItemRequest(), gateway, CancellationToken.None);

        Assert.Equal(3, factory.Requests.Count);
        Assert.Equal(32, gateway.Checkpoint!.Candidates.Count);
        Assert.All(gateway.Checkpoint.Candidates, item => Assert.Equal("rejected", item.Status));
        Assert.Equal(33, gateway.Checkpoint.ObservedCandidateEvidenceIds!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Candidate_lifecycle_additional_discovery_at_capacity_preserves_seed_assessments_and_uses_slots_freed_by_rejections(
        bool rejectSeed)
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var evidence = Enumerable.Range(1, 64).Select(index => BuildEvidence(
            $"E{index}", $"R{index}\nSubstantive named body."
                         + (index == 64 ? "\nR65\nAnother documented named item." : ""),
            docId: documentId, revisionId: revisionId, exactTitle: $"R{index}")).ToArray();
        var candidates = Enumerable.Range(1, 64).Select(index =>
            new AdvancedAnalysisCandidateCheckpoint($"seed-{index}", $"R{index}", "internal-source-1",
                index < 64 ? ["petit-déjeuner"] : [], [],
                index < 64 ? "body_verified" : "awaiting_review", "Canonical prior assessment.", [], [$"E{index}"]))
            .ToArray();
        var judge = JsonSerializer.Serialize(new
        {
            // Extra discovery deliberately precedes the seed assessment in JSON.
            // Admission must still account for a slot freed by that assessment.
            items = new[]
            {
                new
                {
                    key = "new-r65", exactTitle = "R65", sourceKey = "internal-source-1",
                    targetRoles = new[] { "collation" }, selectedRoles = Array.Empty<string>(),
                    status = "body_verified", note = "Additional item visible in supplied body.",
                    locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { "E64" }
                },
                new
                {
                    key = "seed-64", exactTitle = "R64", sourceKey = "internal-source-1",
                    targetRoles = rejectSeed ? [] : new[] { "déjeuner" }, selectedRoles = Array.Empty<string>(),
                    status = rejectSeed ? "rejected" : "body_verified", note = "Seed assessment.",
                    locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { "E64" }
                }
            }
        });
        var gateway = new CheckpointToolGateway(evidence);
        await SeedLifecycleCheckpoint(gateway, evidence[0],
            AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion, candidates);
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner), Completion(judge), Completion(CandidateTerminal),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.MaximumEvidencePromptCharacters = 1_000_000;
        options.ExternalMaximumCallsPerJob = 4;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(64, gateway.Checkpoint!.Candidates.Count(item => item.Status != "rejected"));
        Assert.Equal(rejectSeed, gateway.Checkpoint.Candidates.Any(item => item.ExactTitle == "R65"));
        Assert.Equal(rejectSeed ? "rejected" : "body_verified",
            Assert.Single(gateway.Checkpoint.Candidates, item => item.Key == "seed-64").Status);
        if (!rejectSeed)
            Assert.Contains("capacity", WorkspaceUser(factory.Requests[2].Body)
                .GetProperty("candidateDossier").GetProperty("reason").GetString());
    }

    private static Task SeedLifecycleCheckpoint(
        CheckpointToolGateway gateway, AdvancedAnalysisResolvedEvidence source,
        string schemaVersion, IReadOnlyList<AdvancedAnalysisCandidateCheckpoint> candidates)
        => gateway.SaveResearchCheckpointAsync(new AdvancedAnalysisResearchCheckpoint(
            schemaVersion, candidates,
            new Dictionary<string, string>
            {
                [string.Join("|", source.Reference.DocId, source.Reference.RevisionId,
                    source.Reference.SourceHash)] = "internal-source-1"
            }), CancellationToken.None);
}
