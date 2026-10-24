using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    [Fact]
    public async Task Reused_runner_keeps_overlapping_requests_evidence_and_iteration_budgets_separate()
    {
        var executor = new OverlappingRunExecutor();
        var llm = new ScriptedAgentLlm(); // The router supplies the plan and first action; no model call is expected.
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options() with
        {
            MaximumTurns = 1,
            SeparateActionAndWriter = true,
            RequireEvidenceSelectionBeforeWriter = true,
            StructuredSemanticPlanningEnabled = true,
            SemanticCandidateStrategyEnabled = false,
            SemanticCandidateDefinitionEnabled = false,
            SemanticCandidateAuditEnabled = false
        });

        var results = await Task.WhenAll(
            runner.RunAsync(IsolatedRunIntake("requête alpha"), CancellationToken.None),
            runner.RunAsync(IsolatedRunIntake("requête beta"), CancellationToken.None));

        Assert.Empty(llm.Requests);
        Assert.Equal(2, executor.Entered);
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            var expected = index == 0 ? "requête alpha" : "requête beta";
            Assert.Equal(expected, result.Intake.UserQuestion);
            Assert.Equal(expected, result.EvidenceBundle.UserQuestion);
            var evidence = Assert.Single(result.EvidenceBundle.Items);
            Assert.Equal(expected, evidence.Excerpt);
            Assert.Equal(index == 0 ? 7 : 9, evidence.PageStart);
            Assert.Single(result.TraceEvents, trace => trace.EventName == "source_backed_agent_v2.started");
            Assert.False(result.IsSourceVerified);
        }
        Assert.NotEqual(results[0].EvidenceBundle.BundleId, results[1].EvidenceBundle.BundleId);
        Assert.NotSame(results[0].EvidenceBundle, results[1].EvidenceBundle);
        Assert.NotSame(results[0].TraceEvents, results[1].TraceEvents);
    }

    private static SourceBackedIntake IsolatedRunIntake(string question) => Intake(question) with
    {
        InitialToolCalls = new[]
        {
            new SourceBackedInitialToolCall("router-first", "rag_search",
                JsonSerializer.SerializeToElement(new { query = question, topK = 1 }), "llm_router")
        },
        InitialSemanticMission = new SourceBackedInitialSemanticMission(JsonSerializer.SerializeToElement(new
        {
            planKind = "structured_layout",
            deliverable = "grille sourcée",
            structuredLayout = true,
            rowCount = 2,
            columnCount = 2,
            atomicEvidenceCount = 4,
            atomicEvidenceType = "instances documentées",
            initialCapability = "rag_search",
            rowHeader = "Période",
            rowLabels = new[] { "Première", "Deuxième" },
            columns = new[] { "Option A", "Option B" }
        }), "llm_router")
    };

    private sealed class OverlappingRunExecutor : ISourceBackedAgentToolExecutor
    {
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        public int Entered => Volatile.Read(ref _entered);

        public async Task<ToolResults> ExecuteToolCallAsync(SourceBackedIntake intake,
            string toolName, JsonElement arguments, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _entered) == 2) _bothEntered.TrySetResult();
            await _bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            return SearchResultAtPage(intake.UserQuestion == "requête alpha" ? 7 : 9, intake.UserQuestion);
        }
    }
}
