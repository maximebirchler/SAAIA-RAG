using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class Bug138TerminalTransitionAndRouterCostContractTests
{
    [Fact]
    public async Task Bug138_FastReviewBudgetClosureTransitionsToQwenSelectionAndWriter()
    {
        var llm = new BudgetAwareScriptedLlm(
            Completion(
                JsonSerializer.Serialize(new
                {
                    action = "writer",
                    evidenceId = "E1",
                    anchorId = "E1",
                    text = ""
                }),
                promptTokens: 1_900,
                completionTokens: 100),
            Completion(
                Call("terminal-selection", "submit_evidence_selection", new
                {
                    evidenceIds = new[] { "E1" }
                }),
                promptTokens: 120,
                completionTokens: 20),
            Completion(
                "La procédure Atlas coupe l'alimentation pendant dix secondes [E1].",
                promptTokens: 200,
                completionTokens: 80),
            Completion(Call("terminal-review", "submit_semantic_review", new
            {
                decision = "accept",
                reasons = new[] { "E1 décrit la coupure de dix secondes." },
                rejectedEvidenceIds = Array.Empty<string>(),
                preferredAlternativeEvidenceIds = Array.Empty<string>()
            }), 100, 30));
        var executor = new RecordingToolExecutor(TwoProcedureSearchResults());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            // This boundary fixture reserves exactly 1888 + 112 ordinary tokens.
            Options() with { MaximumActionTokens = 112 });
        var budget = new SourceBackedLlmCumulativeBudget(
            maximumTokens: 4_000,
            maximumElapsedMilliseconds: 60_000,
            terminalReserveTokens: 2_000,
            terminalReserveMilliseconds: 10_000,
            elapsedMilliseconds: static () => 0);

        SourceBackedPipelineResult result;
        using (SourceBackedLlmCumulativeBudgetContext.Push(budget))
        {
            result = await runner.RunAsync(
                SingleSelectionIntake(),
                CancellationToken.None);
        }

        Assert.True(result.IsSourceVerified);
        Assert.Contains("[E1]", result.Answer, StringComparison.Ordinal);
        Assert.Single(executor.Calls);
        Assert.Equal(
            new[]
            {
                "source_backed_fast_evidence_review_v1",
                "submit_evidence_selection",
                "terminal_writer_or_review",
                "submit_semantic_review"
            },
            llm.ExecutedCallClasses);
        Assert.Equal(
            new[]
            {
                "source_backed_fast_evidence_review_v1",
                "source_backed_single_selection_scope_review_v2",
                "submit_evidence_selection",
                "terminal_writer_or_review",
                "submit_semantic_review"
            },
            llm.AttemptedCallClasses);
        Assert.DoesNotContain(
            "source_backed_single_selection_scope_review_v2",
            llm.ExecutedCallClasses,
            StringComparer.Ordinal);
        Assert.True(llm.TerminalFlags["submit_evidence_selection"]);
        Assert.True(llm.TerminalFlags["terminal_writer_or_review"]);
        Assert.True(llm.TerminalFlags["submit_semantic_review"]);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.cumulative_budget.terminal_budget_only"
            && trace.Fields["terminal_budget_only"] == "true"
            && trace.Fields["has_finalizable_evidence"] == "true"
            && trace.Fields["terminal_contract"] == "semantic_selection_then_writer"
            && trace.Fields["decision_source"] == "mechanical_budget_contract");
        var snapshot = budget.GetSnapshot();
        Assert.Equal(4, snapshot.AdmittedCalls);
        Assert.Equal(4, snapshot.CompletedCalls);
        Assert.Equal("submit_semantic_review", snapshot.LastCallClass);
    }

    [Fact]
    public async Task Terminal_selection_is_available_for_one_item_even_when_optional_before_budget_closure()
    {
        var budget = new SourceBackedLlmCumulativeBudget(4000, 60000, 2000, 10000, static () => 0);
        var llm = new BudgetAwareScriptedLlm(
            Completion(Call("terminal-selection", "submit_evidence_selection", new { evidenceIds = new[] { "E1" } }), 120, 20),
            Completion("La procédure Atlas coupe l'alimentation pendant dix secondes [E1].", 200, 80),
            Completion(Call("terminal-review", "submit_semantic_review", new
            {
                decision = "accept",
                reasons = new[] { "La procédure et sa durée sont explicites dans la preuve sélectionnée." },
                rejectedEvidenceIds = Array.Empty<string>(),
                preferredAlternativeEvidenceIds = Array.Empty<string>()
            }), 100, 30));
        var executor = new RecordingToolExecutor(TwoProcedureSearchResults())
        {
            AfterExecution = () =>
            {
                var prior = budget.TryReserve(2000, 0, false);
                Assert.True(prior.Admitted);
                budget.Complete(prior.ReservationId, 2000, 0);
            }
        };
        SourceBackedPipelineResult result;
        using (SourceBackedLlmCumulativeBudgetContext.Push(budget))
            result = await new SourceBackedAgentV2Runner(llm, executor,
                Options() with { RequireEvidenceSelectionBeforeWriter = false })
                .RunAsync(SingleSelectionIntake(), CancellationToken.None);
        Assert.True(result.IsSourceVerified);
        Assert.Single(executor.Calls);
        Assert.Equal(new[] { "submit_evidence_selection", "terminal_writer_or_review", "submit_semantic_review" }, llm.ExecutedCallClasses);
        Assert.Equal(llm.ExecutedCallClasses, llm.AttemptedCallClasses);
        Assert.All(llm.TerminalFlags.Values, Assert.True);
    }

    [Fact]
    public async Task Terminal_decision_replaces_pending_compact_research_after_truncated_scope_review()
    {
        var capture = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "TruncatedScopeReviewCompletionContract.json")));
        var choice = capture.GetProperty("choices")[0];
        Assert.Equal("length", choice.GetProperty("finish_reason").GetString());
        var scopeContent = choice.GetProperty("message").GetProperty("content").GetString()!;
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(scopeContent));
        var llm = new BudgetAwareScriptedLlm(
            Completion(JsonSerializer.Serialize(new { action = "writer", evidenceId = "E1", anchorId = "E1", text = "" }), 1174, 91),
            new SourceBackedAgentCompletion(scopeContent, [], "length", 971, 128),
            Completion(Call("terminal-selection", "submit_evidence_selection", new { evidenceIds = new[] { "E1" } }), 120, 20),
            Completion("La procédure Atlas coupe l'alimentation pendant dix secondes [E1].", 200, 80),
            Completion(Call("terminal-review", "submit_semantic_review", new
            {
                decision = "accept",
                reasons = new[] { "La procédure est explicite dans E1." },
                rejectedEvidenceIds = Array.Empty<string>(),
                preferredAlternativeEvidenceIds = Array.Empty<string>()
            }), 100, 30))
        {
            InputTokenOverrides = new Dictionary<string, int>
            {
                ["source_backed_fast_evidence_review_v1"] = 1174,
                ["source_backed_single_selection_scope_review_v2"] = 971,
                ["submit_research_action"] = 1069
            }
        };
        var executor = new RecordingToolExecutor(TwoProcedureSearchResults());
        var budget = new SourceBackedLlmCumulativeBudget(12000, 240000, 4800, 60000, static () => 0);
        var prior = budget.TryReserve(3865, 0, false);
        Assert.True(prior.Admitted);
        budget.Complete(prior.ReservationId, 3865, 0);
        SourceBackedPipelineResult result;
        using (SourceBackedLlmCumulativeBudgetContext.Push(budget))
            result = await new SourceBackedAgentV2Runner(llm, executor, Options())
                .RunAsync(SingleSelectionIntake(), CancellationToken.None);
        Assert.True(result.IsSourceVerified);
        Assert.Single(executor.Calls);
        Assert.Contains("submit_research_action", llm.AttemptedCallClasses);
        Assert.DoesNotContain("submit_research_action", llm.ExecutedCallClasses);
        Assert.Contains("submit_evidence_selection", llm.ExecutedCallClasses);
        Assert.True(llm.TerminalFlags["submit_evidence_selection"]);
        Assert.Contains(result.TraceEvents, trace => trace.EventName == "source_backed_agent_v2.cumulative_budget.terminal_budget_only");
        Assert.True(budget.GetSnapshot().ChargedTokens <= 12000);
    }

    [Fact]
    public void Bug138_NamedReferenceRepairContractIsFocusedAndBounded()
    {
        var buildTools = RequireStaticMethod(
            "BuildNativeRouterNamedReferenceRepairToolsForTests",
            parameterCount: 0);
        var tools = Assert.IsAssignableFrom<
            IReadOnlyList<SourceBackedAgentToolDefinition>>(
            buildTools.Invoke(null, null));
        var tool = Assert.Single(tools);
        Assert.Equal("repair_named_reference_pair", tool.Name);

        var properties = tool.Parameters.GetProperty("properties");
        Assert.Equal(
            new[] { "document", "namedReferenceKind" },
            properties.EnumerateObject()
                .Select(static property => property.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            new[] { "document", "namedReferenceKind" },
            tool.Parameters.GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.False(tool.Parameters.GetProperty("additionalProperties").GetBoolean());

        var readLimit = RequireStaticMethod(
            "GetNativeRouterNamedReferenceRepairMaximumOutputTokensForTests",
            parameterCount: 0);
        Assert.Equal(96, Assert.IsType<int>(readLimit.Invoke(null, null)));
    }

    [Fact]
    public void Bug138_NamedReferenceRepairActivationIsRestrictedToTheExactFailure()
    {
        var shouldUse = RequireStaticMethod(
            "ShouldUseNativeRouterNamedReferencePairRepairForTests",
            parameterCount: 1);

        Assert.True(InvokeBool(
            shouldUse,
            "native_source_route_named_reference_inconsistent"));
        foreach (var other in new[]
                 {
                     "native_source_route_document_not_explicit",
                     "native_source_route_axes_not_grounded",
                     "native_clarification_route_contract_invalid",
                     "native_router_protocol_invalid"
                 })
        {
            Assert.False(InvokeBool(shouldUse, other), other);
        }
    }

    [Fact]
    public void Bug138_QwenPairRepairChangesOnlyTheContradictoryTransportFields()
    {
        const string question =
            "Selon Rapport Atlas 2024, quelle procédure annuelle est documentée ?";
        var invalid = Completion(Call(
            "initial-route",
            "submit_source_backed_route",
            new
            {
                tool = "search",
                intent = "answer",
                query = "procédure annuelle Atlas 2024",
                answerUnitType = "procédure annuelle documentée",
                answerUnitMode = "content_claim",
                selectionPolicy = "single_item",
                scope = "Knowledge",
                useFocusedDocument = false,
                questionFocus = "content",
                namedReferenceKind = "none",
                document = "Rapport Atlas 2024",
                pool = 10,
                count = 1
            }));
        var repair = Completion(Call(
            "pair-repair",
            "repair_named_reference_pair",
            new
            {
                namedReferenceKind = "document",
                document = "Rapport Atlas 2024"
            }));
        var apply = RequireStaticMethod(
            "ApplyNativeRouterNamedReferencePairRepairForTests",
            parameterCount: 2);

        var repaired = Assert.IsType<SourceBackedAgentCompletion>(
            apply.Invoke(null, new object[] { invalid, repair }));
        var originalArguments = Assert.Single(invalid.ToolCalls).Arguments;
        var repairedCall = Assert.Single(repaired.ToolCalls);
        var repairedArguments = repairedCall.Arguments;

        Assert.Equal("submit_source_backed_route", repairedCall.Name);
        foreach (var property in originalArguments.EnumerateObject())
        {
            if (property.Name is "namedReferenceKind" or "document")
                continue;
            Assert.True(repairedArguments.TryGetProperty(property.Name, out var value));
            Assert.Equal(property.Value.GetRawText(), value.GetRawText());
        }
        Assert.Equal(
            "document",
            repairedArguments.GetProperty("namedReferenceKind").GetString());
        Assert.Equal(
            "Rapport Atlas 2024",
            repairedArguments.GetProperty("document").GetString());

        var parsed = ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            question,
            repairedCall.Name,
            repairedArguments.GetRawText());
        Assert.True(parsed.Accepted, parsed.FailureReason);
        Assert.Equal(
            "Rapport Atlas 2024",
            parsed.Plan.SourceBackedMission?.RequestedDocumentName);

        var subjectRepair = Completion(Call(
            "subject-pair-repair",
            "repair_named_reference_pair",
            new
            {
                namedReferenceKind = "subject",
                document = (string?)null
            }));
        var subjectResult = Assert.IsType<SourceBackedAgentCompletion>(
            apply.Invoke(null, new object[] { invalid, subjectRepair }));
        var subjectArguments = Assert.Single(subjectResult.ToolCalls).Arguments;
        Assert.Equal(
            "subject",
            subjectArguments.GetProperty("namedReferenceKind").GetString());
        Assert.Equal(JsonValueKind.Null, subjectArguments.GetProperty("document").ValueKind);
    }

    [Fact]
    public void Bug138_ProductionBudgetsAndDomainNeutralityRemainFrozen()
    {
        var options = ReadProductSource(
            "ToolAgent", "SourceBackedRag", "SourceBackedAgentV2Options.cs");
        Assert.Contains("MaximumCumulativeLlmTokens = 12_000", options, StringComparison.Ordinal);
        Assert.Contains("MaximumCumulativeLlmElapsedMilliseconds = 240_000", options, StringComparison.Ordinal);
        Assert.Contains("CumulativeLlmTerminalReserveTokens = 4_800", options, StringComparison.Ordinal);
        Assert.Contains("CumulativeLlmTerminalReserveMilliseconds = 60_000", options, StringComparison.Ordinal);

        var product = string.Join(
            "\n",
            ReadProductSource("ToolAgent", "SourceBackedRag", "SourceBackedAgentV2Runner.cs"),
            ReadProductSource("ToolAgent", "ToolAgentOrchestrator.RouterCore.cs"),
            ReadOptionalProductSource(
                "ToolAgent", "ToolAgentOrchestrator.NativeRouterNamedReferenceRepair.cs"));
        foreach (var forbidden in new[]
                 {
                     "C005_ONE_MICROSOFT",
                     "Microsoft_Annual_Report_2024",
                     "245 billion",
                     "16 percent"
                 })
        {
            Assert.DoesNotContain(forbidden, product, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(
            ReadProductSource(
                    "ToolAgent", "SourceBackedRag", "SourceBackedAgentV2Runner.cs")
                .TrimEnd('\r', '\n')
                .Split('\n').Length <= 2_500);
    }

    private static SourceBackedAgentV2Options Options()
        => new(
            MaximumTurns: 4,
            MaximumToolCalls: 4,
            MaximumObservationItems: 8,
            MaximumObservationExcerptCharacters: 360,
            MaximumOutputTokens: 900,
            MaximumActionTokens: 480,
            MaximumWorkingEvidenceItems: 12,
            SeparateActionAndWriter: true,
            MaximumSemanticCorrectionTurns: 0,
            SemanticCandidateAuditEnabled: false,
            SemanticColumnRoleReviewEnabled: false,
            MaximumSelectionProtocolRepairTurns: 0,
            StructuredSemanticPlanningEnabled: false,
            RequireEvidenceSelectionBeforeWriter: true,
            StructuredFlatWriterEnabled: false);

    private static SourceBackedIntake SingleSelectionIntake()
    {
        var initialCall = Call("initial-search", "rag_search", new
        {
            query = "procédure documentée Atlas",
            categoryPath = "Knowledge"
        });
        return new SourceBackedIntake(
            "Donne une procédure documentée pour relancer le capteur Atlas.",
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "fr")
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialCall.Id,
                    initialCall.Name,
                    initialCall.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    deliverable = "une procédure source répondant à la demande",
                    structuredLayout = false,
                    rowCount = 1,
                    columnCount = 1,
                    atomicEvidenceCount = 1,
                    atomicEvidenceType = "procédure technique exploitable",
                    initialCapability = "rag_search",
                    rowHeader = "",
                    rowLabels = Array.Empty<string>(),
                    columns = Array.Empty<string>()
                }),
                "llm_router")
        };
    }

    private static ToolResults TwoProcedureSearchResults()
    {
        var payload = JsonSerializer.SerializeToNode(new
        {
            hits = new[]
            {
                new
                {
                    docId = "doc-atlas-a",
                    docName = "procedure-atlas-a.pdf",
                    docPath = "Knowledge/procedure-atlas-a.pdf",
                    revisionId = "revision-atlas-a",
                    sourceHash = new string('a', 64),
                    pageStart = 4,
                    pageEnd = 4,
                    chunkId = "atlas-a-4",
                    excerpt =
                        "Relance du capteur Atlas : coupez l'alimentation pendant dix secondes.",
                    score = 0.94
                },
                new
                {
                    docId = "doc-atlas-b",
                    docName = "procedure-atlas-b.pdf",
                    docPath = "Knowledge/procedure-atlas-b.pdf",
                    revisionId = "revision-atlas-b",
                    sourceHash = new string('b', 64),
                    pageStart = 9,
                    pageEnd = 9,
                    chunkId = "atlas-b-9",
                    excerpt =
                        "Relance alternative : maintenez les deux commandes pendant cinq secondes.",
                    score = 0.91
                }
            }
        }) ?? throw new InvalidOperationException("Unable to build test payload.");
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = JsonSerializer.SerializeToElement(payload),
            DurationMs = 12
        });
        return results;
    }

    private static SourceBackedAgentToolCall Call(
        string id,
        string name,
        object arguments)
        => new(id, name, JsonSerializer.SerializeToElement(arguments));

    private static SourceBackedAgentCompletion Completion(
        SourceBackedAgentToolCall call,
        int promptTokens = 100,
        int completionTokens = 20)
        => new(
            string.Empty,
            new[] { call },
            "tool_calls",
            promptTokens,
            completionTokens);

    private static SourceBackedAgentCompletion Completion(
        string content,
        int promptTokens,
        int completionTokens)
        => new(
            content,
            Array.Empty<SourceBackedAgentToolCall>(),
            "stop",
            promptTokens,
            completionTokens);

    private static MethodInfo RequireStaticMethod(
        string name,
        int parameterCount)
    {
        var method = typeof(ToolAgentOrchestrator)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
                candidate.Name == name
                && candidate.GetParameters().Length == parameterCount);
        Assert.NotNull(method);
        return method!;
    }

    private static bool InvokeBool(MethodInfo method, string argument)
        => Assert.IsType<bool>(method.Invoke(null, new object[] { argument }));

    private static string ReadProductSource(params string[] relativeParts)
    {
        var path = Path.Combine(
            new[] { FindRepoRoot(), "client", "SAAIA.Client.WinUI" }
                .Concat(relativeParts)
                .ToArray());
        Assert.True(File.Exists(path), $"Expected product source missing: {path}");
        return File.ReadAllText(path);
    }

    private static string ReadOptionalProductSource(params string[] relativeParts)
    {
        var path = Path.Combine(
            new[] { FindRepoRoot(), "client", "SAAIA.Client.WinUI" }
                .Concat(relativeParts)
                .ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "client",
                    "SAAIA.Client.WinUI",
                    "SAAIA.Client.WinUI.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class RecordingToolExecutor : ISourceBackedAgentToolExecutor
    {
        private readonly Queue<ToolResults> _results;

        public RecordingToolExecutor(params ToolResults[] results)
            => _results = new Queue<ToolResults>(results);

        public List<string> Calls { get; } = new();
        public Action? AfterExecution { get; init; }

        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            Calls.Add(toolName);
            AfterExecution?.Invoke();
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class BudgetAwareScriptedLlm :
        ISourceBackedAgentLlmClient,
        ISourceBackedAgentStructuredLlmClient,
        ISourceBackedAgentInputTokenCounter
    {
        private readonly Queue<SourceBackedAgentCompletion> _completions;

        public BudgetAwareScriptedLlm(
            params SourceBackedAgentCompletion[] completions)
            => _completions = new Queue<SourceBackedAgentCompletion>(completions);

        public List<string> AttemptedCallClasses { get; } = new();
        public List<string> ExecutedCallClasses { get; } = new();
        public Dictionary<string, bool> TerminalFlags { get; } = new(
            StringComparer.Ordinal);
        public IReadOnlyDictionary<string, int> InputTokenOverrides { get; init; } = new Dictionary<string, int>();

        public Task<int?> CountInputTokensAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            CancellationToken ct,
            bool requireToolCall = false)
            => Task.FromResult<int?>(100);

        public Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null)
            => ExecuteAsync(
                contract.Name,
                SourceBackedLlmCumulativeBudgetContext.IsTerminalStructuredCall(
                    contract.Name),
                messages,
                Array.Empty<SourceBackedAgentToolDefinition>(),
                maxTokens,
                ct);

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            var callClass = SourceBackedLlmCumulativeBudgetContext
                .ResolveNativeCallClass(tools);
            return ExecuteAsync(
                callClass,
                SourceBackedLlmCumulativeBudgetContext.IsTerminalNativeCall(tools),
                messages,
                tools,
                maxTokens,
                ct);
        }

        private Task<SourceBackedAgentCompletion> ExecuteAsync(
            string callClass,
            bool terminal,
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct)
        {
            AttemptedCallClasses.Add(callClass);
            if (AttemptedCallClasses.Count > 12)
                throw new InvalidOperationException("Terminal transition did not converge: " + string.Join(",", AttemptedCallClasses));
            TerminalFlags[callClass] = terminal;
            var inputTokens = InputTokenOverrides.TryGetValue(callClass, out var overrideTokens) ? overrideTokens
                : callClass == "source_backed_fast_evidence_review_v1"
                ? 1_888
                : 100;
            return SourceBackedLlmCumulativeBudgetContext.ExecuteAsync(
                callClass,
                terminal,
                messages,
                tools,
                maxTokens,
                _ => Task.FromResult<int?>(inputTokens),
                _ =>
                {
                    ExecutedCallClasses.Add(callClass);
                    return Task.FromResult(_completions.Dequeue());
                },
                ct);
        }
    }
}
