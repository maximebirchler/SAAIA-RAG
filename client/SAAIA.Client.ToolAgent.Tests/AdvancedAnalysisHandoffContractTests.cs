using System.Text.Json;
using System.Text;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class AdvancedAnalysisHandoffContractTests
{
    [Fact]
    public async Task RunAsync_materializes_the_pre_retrieval_handoff_on_the_real_boundary_branch()
    {
        const string request = "Provide the documented set requested for the audit.";
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                LoadedAtUtc = DateTimeOffset.UtcNow
            }
        };
        var llm = new QueueLlmClient(
            """
            {
              "mode": "strict",
              "language": "en",
              "intent": "rag.answer",
              "responseFormat": "list",
              "needClarification": false,
              "toolCalls": [],
              "sourceBackedMission": {
                "planKind": "multi_item",
                "deliverable": "documented set",
                "structuredLayout": false,
                "rowCount": 1,
                "columnCount": 1,
                "atomicEvidenceCount": 5,
                "atomicEvidenceType": "documented item",
                "atomicEvidenceMode": "distinct_items",
                "selectionPolicy": "distinct_items"
              }
            }
            """);
        var orchestrator = new ToolAgentOrchestrator(
            new ApiClient(),
            llm,
            memory,
            new AppSettings { ActiveMode = "strict" });
        var streamed = new StringBuilder();

        var result = await orchestrator.RunAsync(
            Array.Empty<(string role, string content)>(),
            request,
            CancellationToken.None,
            onDelta: delta => streamed.Append(delta));

        Assert.Null(result.sourcesPayload);
        Assert.Contains("Advanced analysis", result.finalAnswer);
        Assert.Equal(result.finalAnswer, streamed.ToString());
        var handoff = Assert.IsType<AdvancedAnalysisHandoffEnvelope>(
            orchestrator.LastAdvancedAnalysisHandoff);
        Assert.Equal(request, handoff.RequestText);
        Assert.Equal("before_retrieval", handoff.TransferStage);
        Assert.Equal("multi_item_answer_units_at_or_above_5", handoff.ReasonCode);
        Assert.Equal(5, handoff.Load.AnswerUnitCount);
        Assert.Empty(handoff.ResearchState.ExecutedTools);
        Assert.Empty(handoff.ResearchState.EvidenceReferences);
    }

    [Fact]
    public async Task Budget_terminal_captures_research_before_clearing_visible_sources()
    {
        const string request = "Give four documented procedures.";
        var memory = new ToolMemory
        {
            LastToolNames = ["rag.search"],
            LastRagQueries = ["documented procedures"],
            LastSourcesUsed =
            [
                new ToolMemory.SourceRef
                {
                    EvidenceId = "E4",
                    DocId = "11111111-1111-1111-1111-111111111111",
                    RevisionId = "22222222-2222-2222-2222-222222222222",
                    ChunkId = "chunk-4",
                    PageStart = 12,
                    PageEnd = 12,
                    Label = "evidence body must stay local"
                }
            ]
        };
        var orchestrator = new ToolAgentOrchestrator(
            new ApiClient(),
            new QueueLlmClient(),
            memory);
        var snapshot = new SourceBackedLlmBudgetSnapshot(
            MaximumTokens: 12000,
            MaximumElapsedMilliseconds: 540000,
            TerminalReserveTokens: 2000,
            TerminalReserveMilliseconds: 30000,
            ChargedTokens: 10000,
            ReservedTokens: 2000,
            RemainingTokens: 0,
            RemainingMilliseconds: 210000,
            NormalBudgetClosed: true,
            AdmittedCalls: 8,
            CompletedCalls: 7,
            FailedCalls: 1,
            LastUsageSource: "server_usage",
            LastAdmissionReason: "token_budget_exhausted",
            LastCallClass: "submit_semantic_review");

        var result = await orchestrator
            .CompleteAdvancedSourceBackedBudgetHandoffForTestsAsync(
                request,
                BuildPlan("multi_item", 4),
                new SourceBackedLlmBudgetExceededException(
                    "token_budget_exhausted",
                    snapshot));

        Assert.True(result.handled);
        Assert.Null(result.sourcesPayload);
        var handoff = Assert.IsType<AdvancedAnalysisHandoffEnvelope>(
            orchestrator.LastAdvancedAnalysisHandoff);
        Assert.Equal("after_local_budget", handoff.TransferStage);
        Assert.Equal(4, handoff.Load.AnswerUnitCount);
        Assert.Equal(12000, handoff.LocalBudget!.MaximumTokens);
        Assert.Equal("submit_semantic_review", handoff.LocalBudget.LastCallClass);
        Assert.Single(handoff.ResearchState.EvidenceReferences);
        Assert.Equal("chunk-4", handoff.ResearchState.EvidenceReferences[0].ChunkId);
        Assert.DoesNotContain("evidence body must stay local", JsonSerializer.Serialize(handoff));
        Assert.Empty(memory.LastSourcesUsed);
        Assert.Empty(memory.LastToolNames);
        Assert.Empty(memory.LastRagQueries);
    }

    [Fact]
    public void Early_boundary_handoff_preserves_the_measured_load_without_fabricating_evidence()
    {
        var plan = BuildPlan(
            planKind: "structured_layout",
            answerUnits: 20,
            rows: 5,
            columns: 4);

        var handoff = ToolAgentOrchestrator.BuildAdvancedAnalysisHandoffForTests(
            plan,
            new ToolMemory(),
            "Prépare cinq jours avec quatre repas par jour.",
            "structured_answer_units_at_or_above_6",
            "before_retrieval",
            answerUnitCount: 20);

        Assert.Equal(AdvancedAnalysisHandoffEnvelope.CurrentSchemaVersion, handoff.SchemaVersion);
        Assert.NotEqual(Guid.Empty, handoff.HandoffId);
        Assert.Equal("before_retrieval", handoff.TransferStage);
        Assert.Equal("rag.answer", handoff.OriginIntent);
        Assert.Equal("structured_answer_units_at_or_above_6", handoff.ReasonCode);
        Assert.Equal("structured_layout", handoff.Load.PlanKind);
        Assert.Equal(20, handoff.Load.AnswerUnitCount);
        Assert.Equal(5, handoff.Load.RowCount);
        Assert.Equal(4, handoff.Load.ColumnCount);
        Assert.Empty(handoff.ResearchState.EvidenceReferences);
        Assert.False(handoff.ResearchState.MemoryIsEvidence);
        Assert.True(handoff.ResearchState.EvidenceRevalidationRequired);
        Assert.False(handoff.DataPolicy.ExternalProviderContentAuthorized);
    }

    [Fact]
    public void Handoff_preserves_canonical_source_identities_but_not_evidence_text()
    {
        var memory = new ToolMemory
        {
            LastSourcesUsed =
            [
                new ToolMemory.SourceRef
                {
                    EvidenceId = "E7",
                    DocId = "11111111-1111-1111-1111-111111111111",
                    RevisionId = "22222222-2222-2222-2222-222222222222",
                    DocName = "manual.pdf",
                    DocPath = "Normes/manual.pdf",
                    SourceHash = "sha256-source",
                    PageStart = 7,
                    PageEnd = 8,
                    ChunkId = "chunk-7",
                    AnchorId = "anchor-7",
                    ContentCardId = "card-7",
                    Label = "SECRET EVIDENCE TEXT MUST NOT CROSS THE HANDOFF"
                }
            ]
        };

        var handoff = ToolAgentOrchestrator.BuildAdvancedAnalysisHandoffForTests(
            BuildPlan("comparison", 2),
            memory,
            "Compare les deux documents observés.",
            "explicit_documentary_comparison_outside_local_envelope",
            "before_retrieval",
            answerUnitCount: 2);

        var evidence = Assert.Single(handoff.ResearchState.EvidenceReferences);
        Assert.Equal("E7", evidence.EvidenceId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", evidence.DocId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", evidence.RevisionId);
        Assert.Equal("chunk-7", evidence.ChunkId);
        Assert.Equal("anchor-7", evidence.AnchorId);
        Assert.Equal("card-7", evidence.ContentCardId);

        var json = JsonSerializer.Serialize(handoff);
        Assert.DoesNotContain("SECRET EVIDENCE TEXT", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"excerpt\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"fullText\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"documentText\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Budget_handoff_preserves_research_state_and_budget_snapshot()
    {
        var memory = new ToolMemory
        {
            LastToolNames = ["rag.search", "documents.context"],
            LastRagQueries = ["vacuum dead tuples"],
            ResearchWorkingNotes =
            [
                new ToolMemory.ResearchWorkingNote
                {
                    Queries = ["vacuum dead tuples"],
                    CategoryScope = "Bases de données",
                    DocPath = "PostgreSQL_18_Manual.pdf",
                    PageStart = 2276,
                    PageEnd = 2276,
                    Outcome = "yielded_evidence",
                    Accepted = true,
                    CandidateCountBefore = 0,
                    CandidateCountAfter = 2,
                    UsableHitsBefore = 0,
                    UsableHitsAfter = 1
                }
            ]
        };
        var budget = new AdvancedAnalysisLocalBudgetSnapshot
        {
            StopReason = "token_budget_exhausted",
            MaximumTokens = 12000,
            ChargedTokens = 9100,
            ReservedTokens = 2900,
            RemainingTokens = 0,
            MaximumElapsedMilliseconds = 540000,
            RemainingMilliseconds = 420000,
            AdmittedCalls = 7,
            CompletedCalls = 6,
            FailedCalls = 1
        };

        var handoff = ToolAgentOrchestrator.BuildAdvancedAnalysisHandoffForTests(
            BuildPlan("multi_item", 4),
            memory,
            "Trouve quatre préparations.",
            "local_model_budget_exhausted",
            "after_local_budget",
            answerUnitCount: 4,
            budget);

        Assert.Equal("after_local_budget", handoff.TransferStage);
        Assert.Equal(new[] { "rag.search", "documents.context" }, handoff.ResearchState.ExecutedTools);
        Assert.Equal(new[] { "vacuum dead tuples" }, handoff.ResearchState.ExecutedQueries);
        var attempt = Assert.Single(handoff.ResearchState.Attempts);
        Assert.Equal("yielded_evidence", attempt.Outcome);
        Assert.Equal(2, attempt.CandidateCountAfter);
        Assert.Equal(12000, handoff.LocalBudget!.MaximumTokens);
        Assert.Equal(9100, handoff.LocalBudget.ChargedTokens);
        Assert.Equal(1, handoff.LocalBudget.FailedCalls);
    }

    [Fact]
    public void Shared_handoff_contract_round_trips_without_changing_security_flags()
    {
        var original = ToolAgentOrchestrator.BuildAdvancedAnalysisHandoffForTests(
            BuildPlan("multi_item", 5),
            new ToolMemory(),
            "Give five documented options.",
            "multi_item_answer_units_at_or_above_5",
            "before_retrieval",
            answerUnitCount: 5);

        var json = JsonSerializer.Serialize(original);
        var copy = JsonSerializer.Deserialize<AdvancedAnalysisHandoffEnvelope>(json);

        Assert.NotNull(copy);
        Assert.Equal(original.HandoffId, copy.HandoffId);
        Assert.Equal(original.RequestText, copy.RequestText);
        Assert.Equal(original.Load.AnswerUnitCount, copy.Load.AnswerUnitCount);
        Assert.False(copy.DataPolicy.ExternalProviderContentAuthorized);
        Assert.True(copy.ResearchState.EvidenceRevalidationRequired);
        Assert.False(copy.ResearchState.MemoryIsEvidence);
    }

    [Fact]
    public void New_turn_reset_removes_a_previous_advanced_handoff()
    {
        var orchestrator = new ToolAgentOrchestrator(
            new ApiClient(),
            new QueueLlmClient(),
            new ToolMemory());

        orchestrator.BuildAndRememberAdvancedAnalysisHandoffForTests(
            BuildPlan("multi_item", 5),
            "Give five documented options.",
            "multi_item_answer_units_at_or_above_5",
            "before_retrieval",
            answerUnitCount: 5);

        Assert.NotNull(orchestrator.LastAdvancedAnalysisHandoff);

        orchestrator.ResetLastTurnDiagnosticsForTests();

        Assert.Null(orchestrator.LastAdvancedAnalysisHandoff);
    }

    private static RouterPlan BuildPlan(
        string planKind,
        int answerUnits,
        int rows = 1,
        int columns = 1)
        => new()
        {
            Intent = "rag.answer",
            Language = "fr",
            SourceBackedMission = new RouterPlan.SourceBackedMissionPlan
            {
                PlanKind = planKind,
                Deliverable = "source-backed answer",
                StructuredLayout = planKind == "structured_layout",
                AtomicEvidenceCount = answerUnits,
                RowCount = rows,
                ColumnCount = columns,
                RequestedDocumentName = ""
            }
        };
}
