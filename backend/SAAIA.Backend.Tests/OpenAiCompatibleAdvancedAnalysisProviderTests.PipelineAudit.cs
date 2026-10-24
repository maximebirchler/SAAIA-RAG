using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string AuditControlPlanner = """{"selectionMode":"distinct_named_items","queries":[{"query":"procédures de contrôle conditions enveloppe documentée","topK":12}]}""";

    private static AdvancedAnalysisProviderRequest BuildAuditControlRequest()
    {
        var basis = BuildFlatNamedItemRequest();
        return basis with
        {
            Handoff = new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = basis.Handoff.HandoffId, CreatedAtUtc = basis.Handoff.CreatedAtUtc,
                RequestText = "Propose exactement deux procédures de contrôle distinctes issues des documents.",
                Language = "fr", OriginIntent = basis.Handoff.OriginIntent,
                ReasonCode = basis.Handoff.ReasonCode, TransferStage = basis.Handoff.TransferStage,
                Load = new AdvancedAnalysisLoadDescriptor
                {
                    PlanKind = "multi_item", Deliverable = "liste de procédures documentées",
                    AnswerUnitCount = 2, AtomicEvidenceCount = 2, AtomicEvidenceType = "documented_procedure",
                    AtomicEvidenceMode = "named_item", SelectionPolicy = "explicit_set"
                },
                ResearchState = basis.Handoff.ResearchState
            }
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_long_selected_identity_requires_its_exact_visible_whole_line_without_a_fuzzy_word_limit(bool altered)
    {
        var title = string.Join(" ", Enumerable.Range(1, 27).Select(index => $"term{index}"));
        var selected = altered ? title.Replace("term27", "changed", StringComparison.Ordinal) : title;
        var answer = JsonSerializer.Serialize(new
        {
            outcome = "answered", answerText = selected + " [C1].",
            claims = new[] { new { claimId = "C1", selectedItem = selected, text = selected, evidenceIds = new[] { "E1" } } }
        });
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(answer));
        var options = CreateOptions();
        options.ExternalMaximumCallsPerJob = 2;
        var operation = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(atomicEvidenceMode: "named_item"),
                new RecordingToolGateway(BuildEvidence("E1", title + "\nSet the control pressure to 17 bar and inspect the seal.")),
                CancellationToken.None);
        if (altered)
        {
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => operation);
            Assert.Equal("advanced_synthesis_candidate_identity_not_supported", error.ErrorCode);
        }
        else
        {
            var result = await operation;
            Assert.Equal("answered", result.Outcome);
            Assert.Equal(title, Assert.Single(result.Claims).SelectedItem);
        }
    }

    [Fact]
    public async Task Staged_navigation_can_read_an_unseen_middle_gap_after_the_final_window_and_documentary_budget_are_exhausted()
    {
        const string prefix = "R1\nFirst documented option.\nR2\nSecond complete documented option.\n";
        var content = prefix + new string('x', 4_000 - prefix.Length)
                      + "R1\nDocumented ending.\n" + new string('y', 490);
        var canonical = BuildEvidence("E1", content, exactTitle: "R1");
        var initialJudge = JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new { key = LifecycleCandidateKey("R1"), exactTitle = "R1", sourceKey = "internal-source-1",
                    targetRoles = Array.Empty<string>(), selectedRoles = Array.Empty<string>(), status = "needs_body",
                    note = "Read the ending at character 4000.", locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { "E1" } },
                new { key = "additional-r2", exactTitle = "R2", sourceKey = "internal-source-1",
                    targetRoles = Array.Empty<string>(), selectedRoles = Array.Empty<string>(), status = "body_verified",
                    note = "Documented second option.", locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { "E1" } }
            }
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(AuditControlPlanner), Completion(initialJudge),
            NativeCompletion(("read_evidence", """{"evidenceId":"E1","offset":4000,"length":512}""")),
            Completion(LifecycleJudgeItem(LifecycleCandidateKey("R1"), "R1", "needs_body", ["E1"])),
            NativeCompletion(("read_evidence", """{"evidenceId":"E1","offset":2400,"length":800}""")),
            Completion(LifecycleJudgeItem(LifecycleCandidateKey("R1"), "R1", "body_verified", ["E1"])),
            Completion(CandidateAnswered(2).Replace("\"E2\"", "\"E1\"", StringComparison.Ordinal)));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.ExternalMaximumCallsPerJob = 7;
        var gateway = new BudgetedToolGateway(canonical, 1, 1_000);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildAuditControlRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Single(gateway.Searches);
        var middleRequest = WorkspaceUser(factory.Requests[4].Body);
        var current = Assert.Single(middleRequest.GetProperty("evidence").EnumerateArray());
        Assert.Equal(4_000, current.GetProperty("contentOffset").GetInt32());
        Assert.True(!current.TryGetProperty("nextContentOffset", out var next) || next.ValueKind == JsonValueKind.Null);
        Assert.False(middleRequest.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        using var wire = JsonDocument.Parse(factory.Requests[4].Body);
        Assert.Equal(new[] { "read_evidence" }, wire.RootElement.GetProperty("tools").EnumerateArray()
            .Select(item => item.GetProperty("function").GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Complete_staged_payload_audit_preserves_question_proof_windows_and_linked_argument_recovery(string protocol)
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        const string prefix = "R1\nMandatory constraint: use only within the documented envelope.\n";
        var body = prefix + new string('x', 4_000 - prefix.Length)
                   + "R1\nThe continuation establishes the remaining documented conditions.\n" + new string('y', 900);
        var evidence = new[]
        {
            BuildEvidence("E1", body, docId: documentId, revisionId: revisionId, exactTitle: "R1"),
            BuildEvidence("E2", "R2\nA second complete documented procedure.", docId: documentId,
                revisionId: revisionId, exactTitle: "R2")
        };
        var judge = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(1, 2).Select(index => new
            {
                key = LifecycleCandidateKey($"R{index}"), exactTitle = $"R{index}",
                sourceKey = "internal-source-1", targetRoles = Array.Empty<string>(),
                selectedRoles = Array.Empty<string>(), status = index == 1 ? "needs_body" : "body_verified",
                note = index == 1 ? "Read the continuation at character 4000." : "Suitable.",
                locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { $"E{index}" }
            })
        });
        const string validRead = """{"evidenceId":"E1","offset":4000,"length":512}""";
        HttpResponseMessage Terminal(string value)
            => protocol == "responses" ? NativeResponseAnswer(value) : Completion(value);
        using var factory = new QueuedHttpClientFactory(
            Terminal(AuditControlPlanner), Terminal(judge),
            protocol == "responses" ? NativeResponseFunction("read_evidence", "{broken")
                : NativeCompletion(("read_evidence", "{broken")),
            protocol == "responses" ? NativeResponseFunction("read_evidence", validRead)
                : NativeCompletion(("read_evidence", validRead)),
            Terminal(LifecycleJudgeItem(LifecycleCandidateKey("R1"), "R1", "body_verified", ["E1"])),
            protocol == "responses" ? NativeResponseAnswer(CandidateAnswered(2)) : Completion(CandidateAnswered(2)),
            protocol == "responses" ? NativeResponseAnswer(CandidateAnswered(2)) : Completion(CandidateAnswered(2)));
        var options = WorkspaceOptions(protocol);
        options.Provider = "openai-dev";
        options.LlmLocation = "external-service";
        options.LlmBaseUrl = "https://api.openai.com/v1";
        options.LlmModel = "gpt-5.6-terra";
        options.ReasoningEffort = "low";
        options.SynthesisReasoningEffort = "low";
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 7;
        var gateway = new RecordingToolGateway(evidence);
        var request = BuildAuditControlRequest();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-unit-test-key")
            .ExecuteAsync(request, gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(7, factory.Requests.Count);
        Assert.Single(gateway.Searches);
        Assert.All(factory.Requests, call =>
        {
            Assert.Equal(request.Handoff.RequestText, WorkspaceUser(call.Body).GetProperty("request").GetString());
            Assert.DoesNotContain(documentId, call.Body, StringComparison.Ordinal);
            using var wire = JsonDocument.Parse(call.Body);
            Assert.Equal("gpt-5.6-terra", wire.RootElement.GetProperty("model").GetString());
            if (protocol == "responses")
            {
                Assert.False(wire.RootElement.GetProperty("store").GetBoolean());
                Assert.Equal("low", wire.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
                Assert.Contains(wire.RootElement.GetProperty("include").EnumerateArray(), item =>
                    item.GetString() == "reasoning.encrypted_content");
            }
        });
        foreach (var index in new[] { 4, 5, 6 })
        {
            var current = WorkspaceUser(factory.Requests[index].Body).GetProperty("evidence")
                .EnumerateArray().Single(item => item.GetProperty("evidenceId").GetString() == "E1");
            Assert.Equal(4_000, current.GetProperty("contentOffset").GetInt32());
            Assert.Equal(body.Substring(4_000, 512), current.GetProperty("content").GetString());
            Assert.Contains(current.GetProperty("supportingContentWindows").EnumerateArray(), window =>
                window.GetProperty("contentOffset").GetInt32() == 0
                && window.GetProperty("content").GetString()!.Contains("Mandatory constraint", StringComparison.Ordinal));
        }
        foreach (var index in new[] { 5, 6 })
        {
            var payload = WorkspaceUser(factory.Requests[index].Body);
            Assert.Equal(2, payload.GetProperty("candidateInventory").GetProperty("items").GetArrayLength());
            Assert.Equal(2, payload.GetProperty("candidateInventory").GetProperty("bodyVerifiedDistinctCount").GetInt32());
            Assert.Equal(2, payload.GetProperty("candidateDossier").GetProperty("bodyVerifiedDistinctCount").GetInt32());
        }
        Assert.Contains("batch_rejected_before_execution", factory.Requests[3].Body, StringComparison.Ordinal);
        var exportRoot = Environment.GetEnvironmentVariable("SAAIA_PAYLOAD_AUDIT_EXPORT_DIR");
        if (!string.IsNullOrWhiteSpace(exportRoot))
        {
            var directory = Path.Combine(exportRoot, protocol);
            Directory.CreateDirectory(directory);
            var roles = new[] { "planner", "judge-initial", "navigator-invalid", "navigator-correction",
                "judge-completed", "writer", "critic" };
            for (var index = 0; index < roles.Length; index++)
                await File.WriteAllTextAsync(Path.Combine(directory, $"{index + 1:00}-{roles[index]}.json"),
                    factory.Requests[index].Body);
        }
    }

    [Fact]
    public async Task Explicitly_repeatable_grid_does_not_launch_the_distinct_solver_and_accepts_actual_reuse()
    {
        var answer = JsonSerializer.Serialize(new
        {
            outcome = "answered", answerText = string.Join(", ", Enumerable.Range(1, 20).Select(index => $"R1 [C{index}]")),
            claims = Enumerable.Range(1, 20).Select(index => new
            {
                claimId = $"C{index}", selectedItem = "R1", text = "R1 est documenté.", evidenceIds = new[] { "E1" }
            })
        });
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"repeatable_named_items","queries":[{"query":"documented item","topK":12}]}"""),
            Completion(answer), Completion(answer));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 6;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                    selectionPolicy: "structured_layout", atomicEvidenceType: "meal_plan_entry"),
                new RecordingToolGateway(BuildEvidence("E1", "R1\nA documented substantive body.", exactTitle: "R1")),
                CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal("repeatable_named_items", result.SelectionMode);
        Assert.Equal(20, result.Claims.Count);
        Assert.Single(result.Claims.Select(claim => claim.SelectedItem).Distinct());
        Assert.Equal(3, factory.Requests.Count);
        Assert.False(WorkspaceUser(factory.Requests[1].Body).TryGetProperty("candidateDossier", out _));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("nested")]
    [InlineData("out_of_range")]
    [InlineData("too_many")]
    public async Task Invalid_checkpoint_supporting_windows_are_rejected_before_synthesis(string defect)
    {
        var history = defect switch
        {
            "duplicate" => new[] { new AdvancedAnalysisEvidenceReadCheckpoint(4_000, 512) },
            "nested" => [new(0, 512, [new(100, 512)])],
            "out_of_range" => [new(20_000, 512)],
            _ => Enumerable.Range(0, 4).Select(index => new AdvancedAnalysisEvidenceReadCheckpoint(index * 512, 512)).ToArray()
        };
        var gateway = new CheckpointToolGateway(BuildEvidence("E1", new string('x', 10_000)));
        await gateway.SaveResearchCheckpointAsync(new AdvancedAnalysisResearchCheckpoint(
            AdvancedAnalysisResearchCheckpoint.CurrentSchemaVersion, [], new Dictionary<string, string>(),
            EvidenceReadWindows: new Dictionary<string, AdvancedAnalysisEvidenceReadCheckpoint>
            {
                ["E1"] = new(4_000, 512, history)
            }), CancellationToken.None);
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_research_checkpoint_invalid", error.ErrorCode);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task Staged_judge_receives_untitled_canonical_bodies_and_establishes_their_actual_identities()
    {
        var documentId = Guid.NewGuid().ToString("D");
        var revisionId = Guid.NewGuid().ToString("D");
        var evidence = Enumerable.Range(1, 2).Select(index => BuildEvidence(
            $"E{index}", $"Observed page {index}\nR{index}\nSet the control pressure to 17 bar. Hold steady for three minutes, then inspect the seal before proceeding to the next documented stage.",
            docId: documentId, revisionId: revisionId, pageStart: index)).ToArray();
        var judge = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(1, 2).Select(index => new
            {
                key = LifecycleCandidateKey($"Observed page {index}"), exactTitle = $"R{index}",
                sourceKey = "internal-source-1", targetRoles = Array.Empty<string>(),
                selectedRoles = Array.Empty<string>(), status = "body_verified", note = "Identity established from canonical body.",
                locatorEvidenceIds = Array.Empty<string>(), bodyEvidenceIds = new[] { $"E{index}" }
            })
        });
        using var factory = new QueuedHttpClientFactory(
            Completion(AuditControlPlanner), Completion(judge), Completion(CandidateAnswered(2)));
        var options = WorkspaceOptions();
        options.NativeCandidateExplorerEnabled = true;
        options.StagedCandidateExplorerEnabled = true;
        options.NativeResearchMaximumHistoryCharacters = 32_768;
        options.ExternalMaximumCallsPerJob = 4;
        var gateway = new RecordingToolGateway(evidence);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildAuditControlRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(3, factory.Requests.Count);
        Assert.Single(gateway.Searches);
        Assert.All(gateway.Evidence, item => Assert.Null(item.ExactTitle));
        var judgePayload = WorkspaceUser(factory.Requests[1].Body);
        Assert.All(judgePayload.GetProperty("candidateInventory").GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal("awaiting_review", item.GetProperty("status").GetString());
            Assert.Contains("Provisional visible text prefix", item.GetProperty("note").GetString());
        });
        var writerPayload = WorkspaceUser(factory.Requests[2].Body);
        Assert.Equal(2, writerPayload.GetProperty("candidateDossier").GetProperty("bodyVerifiedDistinctCount").GetInt32());
    }
}
