using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CumulativeBudgetResearchCompactionTests
{
    private const string Question = "Read both recorded measurements and their validity conditions.";

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Research_admission_tries_the_existing_compact_context_once_before_terminal_resolution(bool compactFits, bool timeReserveReached)
    {
        var cards = LoadCards();
        var ids = EvidenceBundleBuilder.FromToolResults(cards, Question).Items.Select(item => item.EvidenceId).ToArray();
        long elapsed = 0;
        var llm = new BudgetLlm(ids, compactFits,
            () => { if (timeReserveReached) elapsed = 180_000; });
        var executor = new RecordingExecutor(cards);
        var options = new SourceBackedAgentV2Options(
            MaximumTurns: 3, MaximumToolCalls: 2, MaximumObservationItems: 8,
            MaximumObservationExcerptCharacters: 360, MaximumOutputTokens: 900,
            MaximumActionTokens: 256, MaximumWorkingEvidenceItems: 12,
            MaximumSemanticCorrectionTurns: 0, SemanticCandidateAuditEnabled: false,
            SemanticColumnRoleReviewEnabled: false, MaximumSelectionProtocolRepairTurns: 0,
            StructuredSemanticPlanningEnabled: false, RequireEvidenceSelectionBeforeWriter: true,
            SemanticCandidateStrategyEnabled: false);
        var budget = new SourceBackedLlmCumulativeBudget(
            12_000, 240_000, 4_800, 60_000, () => elapsed);
        var earlier = budget.TryReserve(4_987, 0, false);
        Assert.True(earlier.Admitted);
        budget.Complete(earlier.ReservationId, 4_987, 0);
        SourceBackedPipelineResult result;
        using (SourceBackedLlmCumulativeBudgetContext.Push(budget))
        {
            result = await new SourceBackedAgentV2Runner(llm, executor, options)
                .RunAsync(Intake(), CancellationToken.None);
        }

        var expectedForms = timeReserveReached ? new[] { false }
            : compactFits ? new[] { false, true, false, true } : new[] { false, true };
        Assert.Equal(expectedForms, llm.ResearchAttempts.Select(attempt => attempt.Compact));
        var full = llm.ResearchAttempts[0];
        Assert.False(full.Terminal);
        for (var index = 1; index < llm.ResearchAttempts.Count; index += 2)
        {
            var previous = llm.ResearchAttempts[index - 1];
            var compact = llm.ResearchAttempts[index];
            Assert.Equal(previous.ToolsJson, compact.ToolsJson);
            Assert.Equal(previous.MaxTokens, compact.MaxTokens);
            Assert.False(compact.Terminal);
            Assert.Contains(Question, compact.Prompt);
            Assert.Contains("AMBER CALIBRATION", compact.Prompt);
            Assert.Contains("COBALT CALIBRATION", compact.Prompt);
            Assert.True(compact.Prompt.Length < previous.Prompt.Length);
        }
        var canRead = compactFits && !timeReserveReached;
        Assert.Equal(canRead ? 1 : 0, llm.ExecutedResearchCalls);
        Assert.Equal(canRead ? 2 : 1, executor.Calls.Count);
        if (canRead)
        {
            var resolution = executor.Calls[1];
            Assert.Equal("documents.context_batch", resolution.Name);
            var targets = resolution.Arguments.GetProperty("targets").EnumerateArray().ToArray();
            Assert.Equal(ids, targets.Select(target => target.GetProperty("evidenceId").GetString()));
            Assert.Equal(new[] { 2, 1 }, targets.Select(target => target.GetProperty("pageStart").GetInt32()));
        }
        Assert.Equal(1, llm.ExecutedTerminalCalls);
        Assert.Contains(result.TraceEvents, trace => trace.EventName ==
            "source_backed_agent_v2.cumulative_budget.terminal_budget_only");
        Assert.False(result.IsSourceVerified); // An empty context response never becomes proof.
        var snapshot = budget.GetSnapshot();
        Assert.Equal(12_000, snapshot.MaximumTokens);
        Assert.Equal(4_800, snapshot.TerminalReserveTokens);
        Assert.Equal(0, snapshot.ReservedTokens);
        Assert.True(snapshot.ChargedTokens <= snapshot.MaximumTokens);
        Assert.Equal(timeReserveReached ? 0 : compactFits ? 2 : 1,
            result.TraceEvents.Count(trace => trace.EventName ==
                "source_backed_agent_v2.cumulative_budget.research_compaction_requested"));
    }

    private static SourceBackedIntake Intake() => new(Question, "rag.answer", [], [], false, "en")
    {
        InitialToolCalls = [new SourceBackedInitialToolCall("observed-cards", "documents_content_cards",
            JsonSerializer.SerializeToElement(new { query = "", inventoryMode = "representative", limit = 40 }), "llm_router")],
        InitialSemanticMission = new SourceBackedInitialSemanticMission(JsonSerializer.SerializeToElement(new
        {
            planKind = "multi_item", deliverable = "both recorded measurements and their validity conditions",
            structuredLayout = false, rowCount = 1, columnCount = 1, atomicEvidenceCount = 3,
            atomicEvidenceType = "documented facts", atomicEvidenceMode = "content_claim",
            selectionPolicy = "explicit_set", initialCapability = "documents_content_cards",
            rowHeader = "", rowLabels = Array.Empty<string>(), columns = Array.Empty<string>()
        }), "llm_router")
    };

    private static ToolResults LoadCards()
    {
        var result = new ToolResults();
        result.Items.Add(new ToolResults.Item { ToolName = "documents.content_cards", Result =
            JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "CanonicalContentCardLocatorContract.json"))) });
        return result;
    }

    private sealed class RecordingExecutor(ToolResults cards) : ISourceBackedAgentToolExecutor
    {
        public List<(string Name, JsonElement Arguments)> Calls { get; } = [];
        public Task<ToolResults> ExecuteToolCallAsync(SourceBackedIntake intake, string toolName,
            JsonElement arguments, CancellationToken ct)
        {
            Calls.Add((toolName, arguments.Clone()));
            if (Calls.Count == 1) return Task.FromResult(cards);
            Assert.Equal("documents.context_batch", toolName);
            return Task.FromResult(new ToolResults());
        }
    }

    private sealed record Attempt(bool Compact, string Prompt, string ToolsJson, int MaxTokens, bool Terminal);

    private sealed class BudgetLlm(string[] ids, bool compactFits, Action beforeResearchAdmission) :
        ISourceBackedAgentLlmClient, ISourceBackedAgentInputTokenCounter
    {
        public List<Attempt> ResearchAttempts { get; } = [];
        public int ExecutedResearchCalls { get; private set; }
        public int ExecutedTerminalCalls { get; private set; }
        private static bool IsResearch(IReadOnlyList<SourceBackedAgentToolDefinition> tools)
            => tools.Any(tool => tool.Name == "resolve_navigation_anchors");
        private static bool IsCompact(IReadOnlyList<SourceBackedAgentMessage> messages)
            => messages.Any(message => message.Content?.Contains("CONTEXTE COMPACT DE RECUPERATION", StringComparison.Ordinal) == true);
        private int InputTokens(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools)
            => IsResearch(tools) ? IsCompact(messages) && compactFits ? 1_200 : 2_849 : 100;

        public Task<int?> CountInputTokensAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, CancellationToken ct, bool requireToolCall = false)
            => Task.FromResult<int?>(InputTokens(messages, tools));

        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools, int maxTokens, CancellationToken ct,
            double? temperatureOverride = null, bool requireToolCall = false)
        {
            var research = IsResearch(tools);
            var terminal = SourceBackedLlmCumulativeBudgetContext.IsTerminalNativeCall(tools);
            if (research) ResearchAttempts.Add(new Attempt(IsCompact(messages),
                string.Join("\n", messages.Select(message => message.Content)), JsonSerializer.Serialize(tools), maxTokens, terminal));
            if (research) beforeResearchAdmission();
            return SourceBackedLlmCumulativeBudgetContext.ExecuteAsync(
                SourceBackedLlmCumulativeBudgetContext.ResolveNativeCallClass(tools), terminal,
                messages, tools, maxTokens, _ => Task.FromResult<int?>(InputTokens(messages, tools)), _ =>
                {
                    SourceBackedAgentToolCall call;
                    if (research)
                    {
                        ExecutedResearchCalls++;
                        call = new("model-selected-pages", "resolve_navigation_anchors",
                            JsonSerializer.SerializeToElement(new { evidenceIds = ids }));
                    }
                    else
                    {
                        Assert.Single(tools);
                        Assert.Equal("resolve_source_yield", tools[0].Name);
                        ExecutedTerminalCalls++;
                        call = new("model-terminal", "resolve_source_yield", JsonSerializer.SerializeToElement(new
                        { decision = "insufficiency", message = "The visible observations do not yet contain the requested source passages." }));
                    }
                    return Task.FromResult(new SourceBackedAgentCompletion("", [call], "tool_calls", InputTokens(messages, tools), 40));
                }, ct);
        }
    }
}
