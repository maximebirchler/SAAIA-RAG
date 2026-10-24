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
    public async Task StructuredWriterProtocolFailure_CannotPublishRawControlText()
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
        const string invalidWriterOutput =
            "{\"presentation\":\"paragraphs\",\"claims\":[{\"text\":\"UNITE_SELECTIONNEE 1 - The guard prevents access.\",\"evidenceIds\":[\"E2\"]}]}";
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "safeguard-selection",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Both excerpts directly document one requested safeguard."
                })),
            Completion(invalidWriterOutput));
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

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.Answer);
        Assert.Empty(result.CitedEvidence);
        var writerTrace = Assert.Single(
            result.TraceEvents,
            trace => trace.EventName == "source_backed_agent_v2.writer.completed");
        Assert.Equal("protocol_error", writerTrace.Fields["finish_reason"]);
        Assert.Equal(
            "structured_flat_writer_claim_control_marker",
            writerTrace.Fields["protocol_error"]);
        Assert.Contains(
            "UNITE_SELECTIONNEE 1",
            writerTrace.Fields["protocol_raw_output"],
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.answer.verified"
            && trace.Fields["valid"] == "false");
        Assert.DoesNotContain(
            result.TraceEvents,
            trace => trace.EventName == "source_backed_agent_v2.answer.verified"
                     && trace.Fields["valid"] == "true");
    }
}
