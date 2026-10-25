using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("Client production diagnostics")]
public sealed class GlobalClientAuditRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Working_context_keeps_a_late_user_constraint(bool emergency)
    {
        var question = new string('x', 1_100) + " NEVER USE THE SYNTHETIC FORBIDDEN ITEM.";
        var intake = new SourceBackedIntake(question, "rag.answer", [], [], false, "en");
        var method = typeof(SourceBackedAgentV2Runner).GetMethod("BuildWorkingUserContext", BindingFlags.NonPublic | BindingFlags.Static)!;
        var prompt = (string)method.Invoke(null, [intake, 16_384, emergency])!;
        Assert.Contains(question, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handoff_keeps_current_request_separate_from_non_evidence_conversation()
    {
        const string prior = "Prepare three days of meals without the SYNTHETIC_ALLERGEN.";
        const string current = "Do the same with cold dishes; keep my earlier constraint.";
        var orchestrator = new ToolAgentOrchestrator(new ApiClient(), new BoundaryLlm(), WarmMemory(), new AppSettings { ActiveMode = "strict" });
        await orchestrator.RunAsync([("user", prior), ("assistant", "A proposed plan.")], current, CancellationToken.None);
        var handoff = orchestrator.LastAdvancedAnalysisHandoff!;
        Assert.Equal(current, handoff.RequestText);
        var serialized = JsonSerializer.SerializeToElement(handoff, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.True(serialized.TryGetProperty("conversationContext", out var context));
        Assert.False(context.GetProperty("isDocumentaryEvidence").GetBoolean());
        Assert.Contains(prior, context.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("A proposed plan.", context.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(current, context.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_log_does_not_contain_the_user_request()
    {
        var marker = "SYNTHETIC_PRIVATE_" + Guid.NewGuid().ToString("N");
        var old = Environment.GetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT");
        try
        {
            Environment.SetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT", "false");
            await new ToolAgentOrchestrator(new ApiClient(), new BoundaryLlm(), WarmMemory(), new AppSettings { ActiveMode = "strict" })
                .RunAsync([], marker, CancellationToken.None);
            Assert.DoesNotContain(marker, File.ReadAllText(ClientLog.StartupLogPath), StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("SAAIA_DIAGNOSTIC_CONTENT", old); }
    }

    [Fact]
    public void Handoff_preserves_all_supported_axis_labels()
    {
        var labels = Enumerable.Range(1, 40).Select(i => "ROW " + i).ToList();
        var plan = new RouterPlan { Intent = "rag.answer", SourceBackedMission = new RouterPlan.SourceBackedMissionPlan
        { PlanKind = "structured_layout", StructuredLayout = true, RowCount = 40, ColumnCount = 1, AtomicEvidenceCount = 40, RowLabels = labels, Columns = ["VALUE"] } };
        var handoff = ToolAgentOrchestrator.BuildAdvancedAnalysisHandoffForTests(plan, new ToolMemory(), "Fill the forty named rows.", "structured", "before_retrieval", 40);
        Assert.Equal(labels, handoff.Load.RowLabels);
        Assert.Equal(40, handoff.Load.RowCount);
    }

    [Fact]
    public async Task Normal_diagnostics_keep_safe_capacity_codes_without_request_content()
    {
        var memory = WarmMemory();
        var marker = "SYNTHETIC_CONTEXT_" + Guid.NewGuid().ToString("N");
        string? traceId;
        using (var turn = ClientDiagnostics.Journal.BeginTurn(independent: true))
        {
            traceId = ClientDiagnostics.Journal.CurrentTraceId;
            await new ToolAgentOrchestrator(new ApiClient(), new BoundaryLlm(), memory, new AppSettings { ActiveMode = "strict" })
                .RunAsync([], marker, CancellationToken.None);
            turn.Complete();
        }
        var record = Assert.Single(ClientDiagnostics.Journal.Last(64), item => item.TraceId == traceId);
        var probe = Assert.Single(record.Events, item => item.Operation == "agent.capability_boundary.decision");
        Assert.Equal("advanced_analysis_required", probe.Identifiers["decision"]);
        Assert.Equal("multi_item", probe.Identifiers["plan_kind"]);
        Assert.Equal("legacy_local", probe.Identifiers["provider_mode"]);
        var trace = Assert.Single(memory.Execution.LastRagTraceEvents, item => item.Contains("event=capability_boundary.decision ", StringComparison.Ordinal));
        Assert.Contains("decision=\"advanced_analysis_required\"", trace);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(probe) + trace, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("A/Child", true)]
    [InlineData("B", false)]
    [InlineData(null, false)]
    public void Mandatory_category_restriction_is_separate_from_the_research_hypothesis(string? category, bool admitted)
    {
        var intake = new SourceBackedIntake("A restricted corpus request", "rag.answer", [], [], false, "en", RequiredCategoryPaths: ["A"]);
        var args = JsonSerializer.SerializeToElement(new { query = "documented value", categoryPath = category });
        Assert.Equal(admitted, SourceBackedResearchScopePolicy.ValidateMandatoryScope(intake, "rag.search", args) is null);
    }

    [Theory]
    [InlineData("hypothesis", false)]
    [InlineData("user_restriction", true)]
    public void Native_router_scope_provenance_reaches_the_actual_intake_mapper_and_tool_guard(string policy, bool restricted)
    {
        var route = new SourceBackedAgentCompletion("", [new("route", "submit_source_backed_route", JsonSerializer.SerializeToElement(new
        {
            tool = "search", intent = "answer", query = "documented value", answerUnitType = "fact",
            answerUnitMode = "content_claim", selectionPolicy = "single_item", useFocusedDocument = false,
            questionFocus = "content", namedReferenceKind = "none", document = "", pool = 6, count = 1,
            scope = "A", scopePolicy = policy, scopeUserAnchor = policy == "user_restriction" ? "only category A" : ""
        }))], "tool_calls");
        var orchestrator = new ToolAgentOrchestrator(new ApiClient(), new BoundaryLlm(), WarmMemory());
        var parser = typeof(ToolAgentOrchestrator).GetMethod("TryBuildNativeRouterPlan", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = [route, "Find a documented value from only category A.", "en", false, true, "A", null, null];
        Assert.True((bool)parser.Invoke(orchestrator, arguments)!, arguments[7]?.ToString());
        var plan = (RouterPlan)arguments[6]!;
        plan.RiskFlags = ["internal-risk-code"];
        var intake = SourceBackedIntakeFactory.Create("Find the fact from only category A; keep this late user constraint.", "rag.answer", plan,
            false, [], null, null, "none");
        Assert.Equal(policy, plan.SourceBackedMission!.CategoryScopePolicy);
        Assert.Equal(restricted, intake.RequiredCategoryPaths?.Count > 0);
        Assert.Empty(intake.ExplicitConstraints);
        Assert.Equal(new[] { "internal-risk-code" }, intake.RiskFlags);
        Assert.EndsWith("late user constraint.", intake.UserQuestion);
        var next = JsonSerializer.SerializeToElement(new { query = "new search", categoryPath = "B" });
        Assert.Equal(restricted, SourceBackedResearchScopePolicy.ValidateMandatoryScope(intake, "rag.search", next) is not null);
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("B", false)]
    [InlineData("", false)]
    public void Targeted_read_cannot_reuse_an_old_out_of_scope_document(string documentCategory, bool admitted)
    {
        var evidence = new EvidenceItem("E1", "rag_hit", "rag.search", "earlier search", "doc-B", "Earlier.pdf", "Earlier.pdf",
            new string('a', 64), "revision", 1, 1, "chunk", "Earlier observed fact", "earlier observed fact", 1, 1,
            documentCategory, "en", "en", "native", null, new Dictionary<string, string>(), new Dictionary<string, string>(), [], []);
        var bundle = new EvidenceBundle("previous", "Earlier query", [evidence], []);
        var intake = new SourceBackedIntake("Use only category A", "rag.answer", [], [], false, "en", RequiredCategoryPaths: ["A"]);
        var locator = JsonSerializer.SerializeToElement(new { docId = "doc-B", pageStart = 1, pageEnd = 1 });
        Assert.Equal(admitted, SourceBackedResearchScopePolicy.ValidateMandatoryScope(intake, "documents.context", locator, bundle) is null);
        var batch = JsonSerializer.SerializeToElement(new { targets = new[] { locator } });
        Assert.Equal(admitted, SourceBackedResearchScopePolicy.ValidateMandatoryScope(intake, "documents.context_batch", batch, bundle) is null);
    }

    [Fact]
    public void Conversation_context_never_becomes_evidence_and_does_not_silently_clip_a_statement()
    {
        var context = AdvancedConversationContextBuilder.Build([("user", "old constraint"), ("user", new string('x', 16_001))], "current");
        Assert.NotNull(context);
        Assert.True(context.Truncated);
        Assert.Empty(context.Turns);
        Assert.Null(SAAIA.Contracts.AdvancedAnalysisConversationContextPolicy.Validate(context));
        Assert.NotNull(SAAIA.Contracts.AdvancedAnalysisConversationContextPolicy.Validate(new() { IsDocumentaryEvidence = true }));
    }

    [Fact]
    public void User_constraint_revocation_keeps_chronology_and_rejects_system_messages()
    {
        var context = AdvancedConversationContextBuilder.Build(
            [("system", "Do not transmit this internal instruction."), ("user", "Exclude RED."),
                ("assistant", "Previous proposal."), ("user", "Allow RED now; exclude BLUE."), ("user", "current")], "current")!;
        Assert.False(context.Truncated);
        Assert.Equal(new[] { "Exclude RED.", "Previous proposal.", "Allow RED now; exclude BLUE." }, context.Turns.Select(turn => turn.Text));
        Assert.Null(SAAIA.Contracts.AdvancedAnalysisConversationContextPolicy.Validate(context));
        Assert.NotNull(SAAIA.Contracts.AdvancedAnalysisConversationContextPolicy.Validate(new()
            { Turns = [new() { Role = "system", Text = "Forbidden role" }] }));
    }

    [Theory]
    [InlineData("C01=E1\nC02=E2", true)]
    [InlineData("C01=E1\nC02=E1", false)]
    public void Named_item_assignment_capability_is_explicit_and_does_not_silently_relax_deduplication(string assignments, bool accepted)
    {
        EvidenceItem Item(string id, int page) => new(id, "canonical_content_card", "documents.content_cards", "observed",
            "doc", "Test.pdf", "Test.pdf", new string('a', 64), "revision", page, page, "chunk" + page,
            "Synthetic option " + page, "synthetic option " + page, 1, page, "A", "en", "en", "native", null,
            new Dictionary<string, string> { ["semanticDisplayValue"] = "Option " + page }, new Dictionary<string, string>(), [], [])
            { ContentCardId = "card" + page };
        var bundle = new EvidenceBundle("test", "Use two named items", [Item("E1", 1), Item("E2", 2)], []);
        Assert.False(SourceBackedSelectionMultiplicity.SupportsRepeatedNamedAssignments);
        Assert.Equal(accepted, SourceBackedAgentV2Runner.TryBuildStructuredCandidateAssignmentTable(assignments, bundle,
            ["E1", "E2"], "Row", ["First", "Second"], ["Value"], out _, out _, out var reason));
        if (!accepted) Assert.Equal("structured_candidate_writer_assignment_duplicate", reason);
    }

    [Fact]
    public async Task Standalone_runner_owns_a_budget_and_restores_the_parent_context()
    {
        Assert.Null(SourceBackedLlmCumulativeBudgetContext.Current);
        var llm = new BudgetObserver();
        var runner = new SourceBackedAgentV2Runner(llm, new NoTools(), Options());
        await Assert.ThrowsAsync<ObservedBudgetException>(() => runner.RunAsync(new("A source question", "rag.answer", [], [], false, "en"), CancellationToken.None));
        Assert.NotNull(llm.Observed);
        Assert.Equal(12_000, llm.Observed!.GetSnapshot().MaximumTokens);
        Assert.Null(SourceBackedLlmCumulativeBudgetContext.Current);
    }

    [Fact]
    public async Task Runner_reuses_an_existing_budget_instead_of_resetting_it()
    {
        var budget = new SourceBackedLlmCumulativeBudget(12_000, 240_000, 4_800, 60_000, () => 0);
        var llm = new BudgetObserver();
        using (SourceBackedLlmCumulativeBudgetContext.Push(budget))
        {
            await Assert.ThrowsAsync<ObservedBudgetException>(() => new SourceBackedAgentV2Runner(llm, new NoTools(), Options())
                .RunAsync(new("A source question", "rag.answer", [], [], false, "en"), CancellationToken.None));
            Assert.Same(budget, llm.Observed);
            Assert.Same(budget, SourceBackedLlmCumulativeBudgetContext.Current);
        }
        Assert.Null(SourceBackedLlmCumulativeBudgetContext.Current);
    }

    private static SourceBackedAgentV2Options Options() => new(2, 2, 4, 180, 900,
        StructuredSemanticPlanningEnabled: false, SemanticCandidateAuditEnabled: false);
    private static ToolMemory WarmMemory() => new() { CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot { LoadedAtUtc = DateTimeOffset.UtcNow } };
    private sealed class ObservedBudgetException : Exception { }
    private sealed class BudgetObserver : ISourceBackedAgentLlmClient
    {
        internal SourceBackedLlmCumulativeBudget? Observed;
        public Task<SourceBackedAgentCompletion> CompleteAsync(IReadOnlyList<SourceBackedAgentMessage> messages, IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens, CancellationToken ct, double? temperatureOverride = null, bool requireToolCall = false)
        { Observed = SourceBackedLlmCumulativeBudgetContext.Current; throw new ObservedBudgetException(); }
    }
    private sealed class NoTools : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(SourceBackedIntake intake, string toolName, JsonElement arguments, CancellationToken ct)
            => throw new InvalidOperationException("The budget witness must stop before retrieval.");
    }
    private sealed class BoundaryLlm : ILlmClient
    {
        public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
            => Task.FromResult("""{"mode":"strict","language":"en","intent":"rag.answer","responseFormat":"list","needClarification":false,"toolCalls":[],"sourceBackedMission":{"planKind":"multi_item","deliverable":"documented set","structuredLayout":false,"rowCount":1,"columnCount":1,"atomicEvidenceCount":5,"atomicEvidenceType":"documented item","atomicEvidenceMode":"named_item","selectionPolicy":"explicit_set"}}""");
        public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
            => throw new InvalidOperationException("The witness must hand off before writing.");
    }
}
