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
    public async Task DirectWriterHandoff_UsesStructuredClaimsBeforeTheNormalVerifier()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "documented machinery safeguards",
            topK = 5
        });
        var intake = Intake("Summarize two documented machinery safeguards.") with
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
                    deliverable = "two documented machinery safeguards",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "documented safeguard",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "safeguard-selection",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Both excerpts directly document one requested safeguard."
                })),
            Completion(
                """
                {"presentation":"bullets","claims":[{"text":"The guard shall prevent access to the hazard zone.","evidenceIds":["E2"]},{"text":"The stop control shall remain readily accessible to the operator.","evidenceIds":["E3"]}]}
                """),
            SemanticReview(
                "accept",
                "Both claims preserve the source wording and cite their own evidence."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 3,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true,
                StructuredFlatWriterEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(
            result.IsSourceVerified,
            string.Join(
                Environment.NewLine,
                result.TraceEvents.Select(trace =>
                    trace.EventName + " | " + string.Join(
                        ";",
                        trace.Fields.Select(static field =>
                            field.Key + "=" + field.Value)))));
        Assert.Equal(new[] { "E2", "E3" }, result.CitedEvidence
            .Select(static evidence => evidence.EvidenceId));
        Assert.Contains(
            llm.StructuredOutputContracts,
            static contract => contract.Name == "source_backed_flat_writer_v1");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.writer.completed"
            && trace.Fields["finish_reason"] == "structured_flat_writer"
            && trace.Fields["cited_evidence_ids"] == "E2,E3");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.answer.verified"
            && trace.Fields["valid"] == "true"
            && trace.Fields["cited_evidence_ids"] == "E2,E3");
    }
}
