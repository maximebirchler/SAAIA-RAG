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

    [Fact]
    public void Candidate_judge_projects_historical_evidence_ids_to_the_current_prompt()
    {
        var projected = OpenAiCompatibleAdvancedAnalysisProvider
            .ProjectCandidateJudgeEvidenceIds(
                ["E-visible", "E-hidden", "E-visible"],
                new HashSet<string>(["E-visible"], StringComparer.Ordinal));

        Assert.Equal(["E-visible"], projected);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Candidate_explorer_builds_a_complete_dossier_before_writer_and_critic(
        string protocol)
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var roles = new[] { "petit-déjeuner", "déjeuner", "collation", "souper" };
        var evidence = Enumerable.Range(1, 21).Select(index => BuildEvidence(
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
                targetRoles = new[] { roles[(index - 1) % roles.Length] },
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
        options.CandidateExplorerMaxTokens = 2_345;
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
        using (var explorerRequest = JsonDocument.Parse(factory.Requests[1].Body))
        {
            Assert.Equal(
                2_345,
                explorerRequest.RootElement.GetProperty(
                    protocol == "responses" ? "max_output_tokens" : "max_tokens")
                    .GetInt32());
        }
        string explorerSystemPrompt;
        if (protocol == "responses")
        {
            using var terminalRequest = JsonDocument.Parse(factory.Requests[2].Body);
            var systemMessage = terminalRequest.RootElement.GetProperty("input")
                .EnumerateArray()
                .Single(item => item.TryGetProperty("role", out var role)
                    && role.GetString() == "system");
            explorerSystemPrompt = systemMessage.GetProperty("content").GetString()!;
            Assert.Contains("json", explorerSystemPrompt, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            using var explorerRequest = JsonDocument.Parse(factory.Requests[1].Body);
            explorerSystemPrompt = explorerRequest.RootElement.GetProperty("messages")
                .EnumerateArray()
                .Single(message => message.GetProperty("role").GetString() == "system")
                .GetProperty("content")
                .GetString()!;
        }
        Assert.Contains("find_source_text with exact", explorerSystemPrompt);
        Assert.Contains("observed entry names", explorerSystemPrompt);
        Assert.Contains("read_source from observed physical coordinates", explorerSystemPrompt);
        Assert.Contains("another broad search_corpus query", explorerSystemPrompt);
        var writer = WorkspaceUser(factory.Requests[3].Body);
        var dossier = writer.GetProperty("candidateDossier");
        Assert.Equal("ready", dossier.GetProperty("outcome").GetString());
        Assert.Equal(20, dossier.GetProperty("bodyVerifiedDistinctCount").GetInt32());
        Assert.Equal(20, dossier.GetProperty("maximumAssignableCount").GetInt32());
        Assert.Equal(20, dossier.GetProperty("proposedAssignments").GetArrayLength());
        Assert.Empty(dossier.GetProperty("missingByRole").EnumerateObject());
        Assert.Equal(20, dossier.GetProperty("bodyEvidenceIds").GetArrayLength());
        Assert.Equal(20, dossier.GetProperty("eligibleCandidateKeys").GetArrayLength());
        Assert.DoesNotContain(
            dossier.GetProperty("bodyEvidenceIds").EnumerateArray(),
            id => id.GetString() == "E21");
        Assert.All(
            dossier.GetProperty("roles").EnumerateArray(),
            role => Assert.Equal(5, role.GetProperty("bodyVerifiedCount").GetInt32()));
        Assert.Equal(
            20,
            writer.GetProperty("candidateInventory")
                .GetProperty("bodyVerifiedDistinctCount")
                .GetInt32());
        Assert.Equal(
            20,
            writer.GetProperty("candidateInventory")
                .GetProperty("items")
                .GetArrayLength());
    }

    [Fact]
    public async Task Candidate_explorer_requests_correction_when_writer_ignores_verified_roles()
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
                targetRoles = new[] { roles[(index - 1) % roles.Length] },
                selectedRoles = Array.Empty<string>(),
                status = "body_verified",
                note = "Verified by the Explorer.",
                locatorEvidenceIds = Array.Empty<string>(),
                bodyEvidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        var wrongRoles = JsonSerializer.Serialize(new
        {
            outcome = "answered",
            answerText = string.Join(", ", Enumerable.Range(1, 20)
                .Select(index => $"R{index} [C{index}]")) + ".",
            claims = Enumerable.Range(1, 20).Select(index =>
            {
                var selected = index switch { 1 => 2, 2 => 1, _ => index };
                return new
                {
                    claimId = $"C{index}",
                    selectedItem = $"R{selected}",
                    text = $"R{selected} est documenté.",
                    evidenceIds = new[] { $"E{selected}" }
                };
            }).ToArray()
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(("save_candidate_inventory", update)),
            Completion(ExplorerReady),
            Completion(wrongRoles),
            Completion(CandidateAnswered(20)));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.CandidateBindingFeedbackEnabled = true;
        options.ExternalMaximumCallsPerJob = 5;

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
        Assert.Equal(5, factory.Requests.Count);
        var correction = WorkspaceUser(factory.Requests[4].Body)
            .GetProperty("candidateSupportCorrections")
            .GetProperty("corrections")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, correction.Length);
        Assert.All(correction, item => Assert.Equal(
            "candidate_role_not_verified",
            item.GetProperty("reasonCode").GetString()));
    }

    [Fact]
    public async Task Candidate_explorer_keeps_a_valid_writer_when_the_final_critic_substitutes_an_unverified_item()
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
                targetRoles = new[] { roles[(index - 1) % roles.Length] },
                selectedRoles = Array.Empty<string>(),
                status = "body_verified",
                note = "Verified by the Explorer.",
                locatorEvidenceIds = Array.Empty<string>(),
                bodyEvidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        var invalidCritic = JsonSerializer.Serialize(new
        {
            outcome = "answered",
            answerText = string.Join(", ", Enumerable.Range(1, 20)
                .Select(index => $"{(index == 1 ? "Unsupported item" : $"R{index}")} [C{index}]")) + ".",
            claims = Enumerable.Range(1, 20).Select(index => new
            {
                claimId = $"C{index}",
                selectedItem = index == 1 ? "Unsupported item" : $"R{index}",
                text = index == 1 ? "Unsupported item." : $"R{index} est documenté.",
                evidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(("save_candidate_inventory", update)),
            Completion(ExplorerReady),
            Completion(CandidateAnswered(20)),
            Completion(invalidCritic));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.CandidateBindingFeedbackEnabled = true;
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 5;

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
        Assert.Equal("R1", result.Claims[0].SelectedItem);
        Assert.Equal(5, result.ProviderCallCount);
        Assert.Equal(5, factory.Requests.Count);
    }

    [Fact]
    public async Task Candidate_explorer_allows_writer_to_refine_a_subordinate_card_title_from_the_same_qualified_body()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var roles = new[] { "petit-déjeuner", "déjeuner", "collation", "souper" };
        var evidence = Enumerable.Range(1, 20).Select(index => BuildEvidence(
                $"E{index}",
                index == 2
                    ? "Timbale de pâtes\nLes pâtes\nINGRÉDIENTS\nÉlément. PRÉPARATION\nProcédure."
                    : $"R{index}\nINGRÉDIENTS\nÉlément {index}. PRÉPARATION\nProcédure {index}.",
                docId: documentId,
                revisionId: revisionId,
                exactTitle: index == 2 ? "Les pâtes" : $"R{index}"))
            .ToArray();
        var update = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(1, 20).Select(index => new
            {
                key = $"candidate-{index}",
                exactTitle = index == 2 ? "Les pâtes" : $"R{index}",
                sourceKey = "internal-source-1",
                targetRoles = new[] { roles[(index - 1) % roles.Length] },
                selectedRoles = Array.Empty<string>(),
                status = "body_verified",
                note = "Verified by the Explorer.",
                locatorEvidenceIds = Array.Empty<string>(),
                bodyEvidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        var answered = JsonSerializer.Serialize(new
        {
            outcome = "answered",
            answerText = string.Join(", ", Enumerable.Range(1, 20)
                .Select(index => $"{(index == 2 ? "Timbale de pâtes" : $"R{index}")} [C{index}]")) + ".",
            claims = Enumerable.Range(1, 20).Select(index => new
            {
                claimId = $"C{index}",
                selectedItem = index == 2 ? "Timbale de pâtes" : $"R{index}",
                text = index == 2 ? "Timbale de pâtes" : $"R{index}",
                evidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(("save_candidate_inventory", update)),
            Completion(ExplorerReady),
            Completion(answered),
            Completion(answered));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.CandidateBindingFeedbackEnabled = true;
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 5;

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
        Assert.Equal("Timbale de pâtes", result.Claims[1].SelectedItem);
        Assert.Equal(5, factory.Requests.Count);
        Assert.DoesNotContain(
            "candidateSupportCorrections",
            WorkspaceUser(factory.Requests[4].Body).ToString());
    }

    [Fact]
    public async Task Candidate_explorer_can_replace_an_automatic_subordinate_title_with_a_full_identity_from_the_same_body()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var roles = new[] { "petit-déjeuner", "déjeuner", "collation", "souper" };
        var evidence = Enumerable.Range(1, 20).Select(index => BuildEvidence(
                $"E{index}",
                index == 2
                    ? "Timbale de pâtes\nLes pâtes\nINGRÉDIENTS\nÉlément. PRÉPARATION\nProcédure."
                    : $"R{index}\nINGRÉDIENTS\nÉlément {index}. PRÉPARATION\nProcédure {index}.",
                docId: documentId,
                revisionId: revisionId,
                exactTitle: index == 2 ? "Les pâtes" : $"R{index}"))
            .ToArray();
        static string AutomaticKey(string sourceKey, string normalizedTitle)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(sourceKey + "\n" + normalizedTitle);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant()[..16];
            return "candidate-" + hash;
        }
        var update = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(1, 20).Select(index => new
            {
                key = index == 2
                    ? AutomaticKey("internal-source-1", "les pates")
                    : $"candidate-refined-{index}",
                exactTitle = index == 2 ? "Timbale de pâtes" : $"R{index}",
                sourceKey = "internal-source-1",
                targetRoles = new[] { roles[(index - 1) % roles.Length] },
                selectedRoles = Array.Empty<string>(),
                status = "body_verified",
                note = "Verified by the Explorer.",
                locatorEvidenceIds = Array.Empty<string>(),
                bodyEvidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        var answered = JsonSerializer.Serialize(new
        {
            outcome = "answered",
            answerText = string.Join(", ", Enumerable.Range(1, 20)
                .Select(index => $"{(index == 2 ? "Timbale de pâtes" : $"R{index}")} [C{index}]")) + ".",
            claims = Enumerable.Range(1, 20).Select(index => new
            {
                claimId = $"C{index}",
                selectedItem = index == 2 ? "Timbale de pâtes" : $"R{index}",
                text = index == 2 ? "Timbale de pâtes" : $"R{index}",
                evidenceIds = new[] { $"E{index}" }
            }).ToArray()
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(("save_candidate_inventory", update)),
            Completion(ExplorerReady),
            Completion(answered),
            Completion(answered));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.CandidateBindingFeedbackEnabled = true;
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 5;

        var gateway = new CheckpointToolGateway(evidence);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(
                factory,
                options,
                null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                gateway,
                CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal("Timbale de pâtes", result.Claims[1].SelectedItem);
        Assert.Contains(gateway.Checkpoint!.Candidates, candidate =>
            candidate.ExactTitle == "Timbale de pâtes"
            && candidate.BodyEvidenceIds.Contains("E2", StringComparer.Ordinal));
        Assert.Equal(5, factory.Requests.Count);
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
        Assert.Equal(0, dossier.GetProperty("bodyVerifiedDistinctCount").GetInt32());
        Assert.Equal(20, dossier.GetProperty("requiredDistinctCount").GetInt32());
    }

    [Fact]
    public async Task Candidate_explorer_rejects_raw_role_counts_without_a_distinct_full_assignment()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var roles = new[] { "petit-déjeuner", "déjeuner", "collation", "souper" };
        var candidateDefinitions = Enumerable.Range(1, 15)
            .Select(index => new
            {
                Key = $"breakfast-{index:D2}",
                Roles = new[] { roles[0] }
            })
            .Concat(Enumerable.Range(1, 5).Select(index => new
            {
                Key = $"shared-{index:D2}",
                Roles = roles
            }))
            .ToArray();
        var evidence = candidateDefinitions.Select((candidate, index) => BuildEvidence(
                $"E{index + 1}",
                $"R{index + 1}\nINGRÉDIENTS\nÉlément. PRÉPARATION\nProcédure.",
                docId: documentId,
                revisionId: revisionId,
                exactTitle: $"R{index + 1}"))
            .ToArray();
        var update = JsonSerializer.Serialize(new
        {
            items = candidateDefinitions.Select((candidate, index) => new
            {
                key = candidate.Key,
                exactTitle = $"R{index + 1}",
                sourceKey = "internal-source-1",
                targetRoles = candidate.Roles,
                selectedRoles = Array.Empty<string>(),
                status = "body_verified",
                note = "Verified by the Explorer.",
                locatorEvidenceIds = Array.Empty<string>(),
                bodyEvidenceIds = new[] { $"E{index + 1}" }
            }).ToArray()
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(("save_candidate_inventory", update)),
            Completion(ExplorerReady),
            Completion(ExplorerBounded),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 5;

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
        Assert.Equal(5, factory.Requests.Count);
        var coverage = WorkspaceUser(factory.Requests[3].Body)
            .GetProperty("explorerFeedback")
            .GetProperty("coverage");
        Assert.Equal(20, coverage.GetProperty("bodyVerifiedDistinctCount").GetInt32());
        Assert.Equal(10, coverage.GetProperty("maximumAssignableCount").GetInt32());
        Assert.Equal(
            10,
            coverage.GetProperty("missingByRole").EnumerateObject()
                .Sum(property => property.Value.GetInt32()));
        Assert.All(
            coverage.GetProperty("roles").EnumerateArray(),
            role => Assert.True(role.GetProperty("bodyVerifiedCount").GetInt32() >= 5));
        var assignmentGap = WorkspaceUser(factory.Requests[3].Body)
            .GetProperty("candidateAssignmentGap");
        Assert.Equal(10, assignmentGap.GetProperty("maximumAssignableCount").GetInt32());
        Assert.NotEmpty(assignmentGap.GetProperty("focusRoles").EnumerateArray());
        Assert.Equal(
            10,
            assignmentGap.GetProperty("missingByRole").EnumerateObject()
                .Sum(property => property.Value.GetInt32()));
        var dossier = WorkspaceUser(factory.Requests[4].Body)
            .GetProperty("candidateDossier");
        Assert.Equal("bounded_gap", dossier.GetProperty("outcome").GetString());
        Assert.Equal(10, dossier.GetProperty("maximumAssignableCount").GetInt32());
        Assert.Equal(10, dossier.GetProperty("proposedAssignments").GetArrayLength());
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
    public async Task Staged_candidate_explorer_judges_body_batches_before_writer()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var roles = new[] { "petit-déjeuner", "déjeuner", "collation", "souper" };
        static string AutomaticKey(string normalizedTitle)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                "internal-source-1\n" + normalizedTitle);
            var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant()[..16];
            return "candidate-" + hash;
        }
        var definitions = Enumerable.Range(1, 20).Select(index => (
                Index: index,
                Key: AutomaticKey($"r{index}"),
                Role: roles[(index - 1) % roles.Length]))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        var evidence = Enumerable.Range(1, 20).Select(index => BuildEvidence(
                $"E{index}",
                $"R{index}\nINGRÉDIENTS\nÉlément {index}. PRÉPARATION\nProcédure {index}.",
                docId: documentId,
                revisionId: revisionId,
                exactTitle: $"R{index}"))
            .ToArray();
        string JudgeUpdate(IEnumerable<(int Index, string Key, string Role)> candidates)
            => JsonSerializer.Serialize(new
            {
                items = candidates.Select(candidate => new
                {
                    key = candidate.Key,
                    exactTitle = $"R{candidate.Index}",
                    sourceKey = "internal-source-1",
                    targetRoles = new[] { candidate.Role },
                    selectedRoles = Array.Empty<string>(),
                    status = "body_verified",
                    locatorEvidenceIds = Array.Empty<string>(),
                    bodyEvidenceIds = new[] { $"E{candidate.Index}" }
                }).ToArray()
            });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            Completion(JudgeUpdate(definitions.Take(12))),
            Completion(JudgeUpdate(definitions.Skip(12))),
            Completion(CandidateAnswered(20)));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
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

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, factory.Requests.Count);
        foreach (var requestIndex in new[] { 1, 2 })
        {
            using var request = JsonDocument.Parse(factory.Requests[requestIndex].Body);
            var root = request.RootElement;
            Assert.False(root.TryGetProperty("tools", out _));
            var system = root.GetProperty("messages").EnumerateArray()
                .Single(message => message.GetProperty("role").GetString() == "system")
                .GetProperty("content").GetString();
            Assert.Contains("Candidate Judge", system);
            Assert.Contains("note is always a JSON string", system);
        }
        Assert.Equal(12, WorkspaceUser(factory.Requests[1].Body)
            .GetProperty("evidence").GetArrayLength());
        Assert.Equal(8, WorkspaceUser(factory.Requests[2].Body)
            .GetProperty("evidence").GetArrayLength());
        var dossier = WorkspaceUser(factory.Requests[3].Body)
            .GetProperty("candidateDossier");
        Assert.Equal("ready", dossier.GetProperty("outcome").GetString());
        Assert.Equal(20, dossier.GetProperty("maximumAssignableCount").GetInt32());
        Assert.Equal(20, dossier.GetProperty("proposedAssignments").GetArrayLength());
    }

    [Fact]
    public async Task Staged_candidate_explorer_separates_navigation_execution_and_judgment()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var navigation = BuildEvidence(
            "E1",
            "TABLE DES MATIÈRES\nR1 ........ 12",
            docId: documentId,
            revisionId: revisionId,
            exactTitle: "TABLE DES MATIÈRES");
        var body = BuildEvidence(
            "E2",
            "R1\nINGRÉDIENTS\nÉlément. PRÉPARATION\nProcédure.",
            docId: documentId,
            revisionId: revisionId,
            exactTitle: "R1");
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(
            "internal-source-1\nr1");
        var candidateKey = "candidate-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(keyBytes))
            .ToLowerInvariant()[..16];
        var judgeUpdate = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    key = candidateKey,
                    exactTitle = "R1",
                    sourceKey = "internal-source-1",
                    targetRoles = new[] { "petit-déjeuner" },
                    selectedRoles = Array.Empty<string>(),
                    status = "body_verified",
                    note = "Standalone item judged from its canonical body.",
                    locatorEvidenceIds = Array.Empty<string>(),
                    bodyEvidenceIds = new[] { "E2" }
                }
            }
        });
        var find = """{"sourceKey":"internal-source-1","query":"R1","offset":0,"topK":20}""";
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            NativeCompletion(("find_source_text", find)),
            Completion(judgeUpdate),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 4;
        var gateway = new SequencedToolGateway([navigation], [body]);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(
                factory,
                options,
                null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                gateway,
                CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(4, factory.Requests.Count);
        Assert.Contains(gateway.Searches, search =>
            search.Operation == "find_source_text" && search.Query == "R1");
        using (var navigatorRequest = JsonDocument.Parse(factory.Requests[1].Body))
        {
            var root = navigatorRequest.RootElement;
            var functionNames = root.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("function")
                    .GetProperty("name").GetString())
                .ToArray();
            Assert.Contains("find_source_text", functionNames);
            Assert.DoesNotContain("save_candidate_inventory", functionNames);
            var system = root.GetProperty("messages").EnumerateArray()
                .Single(message => message.GetProperty("role").GetString() == "system")
                .GetProperty("content").GetString();
            Assert.Contains("Candidate Navigator", system);
        }
        var navigatorUser = WorkspaceUser(factory.Requests[1].Body);
        Assert.NotEmpty(navigatorUser.GetProperty("assignmentGap")
            .GetProperty("focusRoles").EnumerateArray());
        Assert.True(navigatorUser.GetProperty("evidence").GetArrayLength() <= 20);
        using (var judgeRequest = JsonDocument.Parse(factory.Requests[2].Body))
        {
            var root = judgeRequest.RootElement;
            Assert.False(root.TryGetProperty("tools", out _));
            var system = root.GetProperty("messages").EnumerateArray()
                .Single(message => message.GetProperty("role").GetString() == "system")
                .GetProperty("content").GetString();
            Assert.Contains("Candidate Judge", system);
        }
        var dossier = WorkspaceUser(factory.Requests[3].Body)
            .GetProperty("candidateDossier");
        Assert.Equal("bounded_gap", dossier.GetProperty("outcome").GetString());
        Assert.Equal(1, dossier.GetProperty("maximumAssignableCount").GetInt32());
    }

    [Fact]
    public async Task Staged_candidate_explorer_reobserves_a_judge_refined_identity_without_duplicate_key_failure()
    {
        var evidence = BuildEvidence(
            "E1",
            "Timbale de pâtes\nLes pâtes\nINGRÉDIENTS\nÉlément. PRÉPARATION\nProcédure.",
            exactTitle: "Les pâtes");
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(
            "internal-source-1\nles pâtes");
        var candidateKey = "candidate-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(keyBytes))
            .ToLowerInvariant()[..16];
        var judged = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    key = candidateKey,
                    exactTitle = "Timbale de pâtes",
                    sourceKey = "internal-source-1",
                    targetRoles = new[] { "souper" },
                    selectedRoles = Array.Empty<string>(),
                    status = "body_verified",
                    locatorEvidenceIds = Array.Empty<string>(),
                    bodyEvidenceIds = new[] { "E1" }
                }
            }
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            Completion(judged),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = false;
        options.ExternalMaximumCallsPerJob = 4;

        var gateway = new CheckpointToolGateway([evidence]);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(
                factory,
                options,
                null)
            .ExecuteAsync(
                BuildRequest(
                    answerUnitCount: 20,
                    atomicEvidenceMode: "named_item",
                    selectionPolicy: "distinct_structured_layout"),
                gateway,
                CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        var candidate = Assert.Single(gateway.Checkpoint!.Candidates);
        Assert.Equal(candidateKey, candidate.Key);
        Assert.Equal("Timbale de pâtes", candidate.ExactTitle);
        Assert.Contains("E1", candidate.BodyEvidenceIds);
        Assert.Equal(3, factory.Requests.Count);
    }

    [Fact]
    public async Task Staged_candidate_judge_can_reject_a_body_without_assigning_a_false_role()
    {
        var evidence = BuildEvidence(
            "E1",
            "Composant isolé\nINGRÉDIENTS\n500 g de composant. PRÉPARATION\nMélanger le composant.",
            exactTitle: "Composant isolé");
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(
            "internal-source-1\ncomposant isolé");
        var candidateKey = "candidate-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(keyBytes))
            .ToLowerInvariant()[..16];
        var rejected = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    key = candidateKey,
                    exactTitle = "Composant isolé",
                    sourceKey = "internal-source-1",
                    targetRoles = Array.Empty<string>(),
                    selectedRoles = Array.Empty<string>(),
                    status = "rejected",
                    note = "Component heading, not a standalone candidate.",
                    locatorEvidenceIds = Array.Empty<string>(),
                    bodyEvidenceIds = new[] { "E1" }
                }
            }
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(CandidatePlanner),
            Completion(rejected),
            Completion(CandidateTerminal));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
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
        Assert.Equal(3, factory.Requests.Count);
        var writer = WorkspaceUser(factory.Requests[2].Body);
        Assert.Empty(writer.GetProperty("candidateInventory")
            .GetProperty("items").EnumerateArray());
        Assert.Equal(0, writer.GetProperty("candidateDossier")
            .GetProperty("maximumAssignableCount").GetInt32());
    }

    [Fact]
    public async Task Staged_candidate_explorer_requires_the_candidate_explorer_and_a_full_cycle_budget()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions();
        options.StagedCandidateExplorerEnabled = true;
        options.NativeCandidateExplorerEnabled = false;
        options.ExternalMaximumCallsPerJob = 3;

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(
                    BuildRequest(
                        answerUnitCount: 20,
                        atomicEvidenceMode: "named_item",
                        selectionPolicy: "distinct_structured_layout"),
                    new RecordingToolGateway(),
                    CancellationToken.None));

        Assert.Equal(
            "advanced_staged_candidate_explorer_configuration_invalid",
            error.ErrorCode);
        Assert.Empty(factory.Requests);
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

    [Fact]
    public async Task Candidate_explorer_rejects_an_output_budget_too_small_for_its_contract()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.CandidateExplorerMaxTokens = 511;

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(
                    BuildRequest(
                        answerUnitCount: 20,
                        atomicEvidenceMode: "named_item",
                        selectionPolicy: "distinct_structured_layout"),
                    new RecordingToolGateway(),
                    CancellationToken.None));

        Assert.Equal(
            "advanced_candidate_explorer_token_budget_invalid",
            error.ErrorCode);
        Assert.Empty(factory.Requests);
    }
}
