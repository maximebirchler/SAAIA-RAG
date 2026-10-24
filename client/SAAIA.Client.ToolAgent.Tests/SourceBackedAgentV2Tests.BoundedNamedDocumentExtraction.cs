using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Fact]
    public async Task BoundedNamedDocumentExtraction_CommitsAnExactStructuredRevisionWithoutSecondReview()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "ANSI B11.0-2023 exigences",
            topK = 5
        });
        var intake = Intake("Donne deux exigences documentées de ANSI B11.0-2023.") with
        {
            QuestionFocus = "content",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées de ANSI B11.0-2023",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    boundedNamedDocumentExtraction = true,
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Les deux exigences couvrent la demande."
                })),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator.","evidenceIds":["E3"]}]}
                """),
            SemanticReview(
                "revise",
                "Les preuves restent valides, mais les verbes doivent reprendre leur portée exacte."),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone", "evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator", "evidenceIds":["E3"]}]}
                """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Equal(new[] { "E2", "E3" }, result.CitedEvidence
            .Select(static evidence => evidence.EvidenceId));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.review_directed_revision.committed_after_source_verification"
            && trace.Fields["selected_evidence"] == "E2,E3");
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed");
    }

    [Fact]
    public async Task BoundedNamedDocumentExtraction_RepairsOneNonExtractiveRevision()
    {
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Les deux exigences couvrent la demande."
                })),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator.","evidenceIds":["E3"]}]}
                """),
            SemanticReview(
                "revise",
                "Rends uniquement les deux valeurs nommées."),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard improves safety by preventing access.","evidenceIds":["E2"]},{"text":"The stop control supports rapid incident response.","evidenceIds":["E3"]}]}
                """),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator.","evidenceIds":["E3"]}]}
                """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(
            BoundedNamedValueExtractionIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Equal(new[] { "E2", "E3" }, result.CitedEvidence
            .Select(static evidence => evidence.EvidenceId));
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.named_value_extraction.protocol_repair.requested");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.named_value_extraction.protocol_repair.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["claim_count"] == "2");
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed");
    }

    [Fact]
    public async Task BoundedNamedDocumentExtraction_StopsAfterOneFailedExtractiveRepair()
    {
        const string invalidRevision =
            """
            {"presentation":"bullets","claims":[{"text":"The guard improves safety by preventing access.","evidenceIds":["E2"]},{"text":"The stop control supports rapid incident response.","evidenceIds":["E3"]}]}
            """;
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Les deux exigences couvrent la demande."
                })),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator.","evidenceIds":["E3"]}]}
                """),
            SemanticReview("revise", "Rends uniquement les deux valeurs nommées."),
            Completion(invalidRevision),
            Completion(invalidRevision));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(
            BoundedNamedValueExtractionIntake(),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.Answer);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.named_value_extraction.protocol_repair.requested");
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.named_value_extraction.protocol_repair.completed"
            && trace.Fields["protocol_valid"] == "false");
    }

    [Fact]
    public async Task BoundedNamedDocumentExtraction_DoesNotRepairAnotherProtocolError()
    {
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Les deux exigences couvrent la demande."
                })),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall be readily accessible to the operator.","evidenceIds":["E3"]}]}
                """),
            SemanticReview("revise", "Rends uniquement les deux valeurs nommées."),
            Completion("{\"unexpected\":true}"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(
            BoundedNamedValueExtractionIntake(),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(4, llm.Requests.Count);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.named_value_extraction.protocol_repair.requested");
    }
}
