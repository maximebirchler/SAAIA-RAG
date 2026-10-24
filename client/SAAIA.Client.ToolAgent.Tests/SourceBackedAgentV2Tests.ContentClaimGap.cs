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
    public async Task ContentClaimGap_LaterProtocolFailurePreservesTheLastValidLlmGap()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "safety requirements",
            topK = 7
        });
        var intake = Intake(
            "Summarize seven documented requirements of Safety Standard.") with
        {
            QuestionFocus = "content",
            RequestedDocumentName = "Safety Standard.pdf",
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
                    deliverable = "seven documented requirements",
                    atomicEvidenceCount = 7,
                    atomicEvidenceType = "documented requirement",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ToolAwareContentClaimGapLlm();
        var executor = new ScriptedToolExecutor(
            SevenContentClaimSearchResult(),
            SevenContentClaimSearchResult(startIndex: 8),
            SevenContentClaimSearchResult(startIndex: 8),
            SevenContentClaimSearchResult(startIndex: 8),
            SevenContentClaimSearchResult(startIndex: 8));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 8,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        var resolutionIndex = llm.ToolSets.FindIndex(static tools =>
            tools.Any(static tool => tool.Name == "declare_source_insufficiency"));
        Assert.True(
            resolutionIndex >= 0,
            "Tool history: " + string.Join(
                " => ",
                llm.ToolSets.Select(static tools =>
                    "[" + string.Join(",", tools.Select(static tool => tool.Name)) + "]"))
            + "; traces: "
            + string.Join(
                " => ",
                result.TraceEvents.Select(static trace =>
                    trace.EventName + "{" + string.Join(",", trace.Fields.Select(
                        static field => field.Key + "=" + field.Value)) + "}")));
        Assert.Equal(
            new[]
            {
                "submit_research_action",
                "request_user_clarification",
                "declare_source_insufficiency"
            },
            llm.ToolSets[resolutionIndex]
                .Select(static tool => tool.Name)
                .ToArray());
        Assert.DoesNotContain(
            llm.ToolSets[resolutionIndex],
            static tool => tool.Name == "submit_evidence_selection");
        var resolutionRequest = string.Join(
            Environment.NewLine,
            llm.Requests[resolutionIndex]
                .Select(static message => message.Content));
        Assert.Contains("E1", resolutionRequest);
        Assert.Contains("E2", resolutionRequest);
        Assert.Contains(
            "five requested points remain unsupported",
            resolutionRequest,
            StringComparison.OrdinalIgnoreCase);
        var terminalIndex = llm.ToolSets.FindIndex(static tools =>
            tools.Any(static tool => tool.Name == "resolve_source_yield"));
        Assert.True(terminalIndex > resolutionIndex);
        Assert.Equal(
            new[] { "resolve_source_yield" },
            llm.ToolSets[terminalIndex]
                .Select(static tool => tool.Name)
                .ToArray());
        Assert.False(result.IsSourceVerified);
        Assert.Equal(
            new[]
            {
                "rag.search",
                "rag.search",
                "rag.search"
            },
            executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.flat_evidence_adequacy.completed"
            && trace.Fields["protocol_valid"] == "false");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.documentary_no_progress.recovery_requested"
            && trace.Fields["semantic_gap_active"] == "true"
            && trace.Fields["lead_evidence_ids"] == "E1,E2");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.documentary_no_progress.terminal_decision_requested"
            && trace.Fields["consecutive_no_progress_turns"] == "3");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.source_insufficiency.declared"
            && trace.Fields["decision_source"] == "llm_orchestrator");
    }
}
