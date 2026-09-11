using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class Bug135NamedReferenceRouterContractTests
{
    [Fact]
    public void Bug135_source_route_schemas_require_an_explicit_named_reference_kind()
    {
        var sourceTools = ToolAgentOrchestrator.BuildNativeRouterToolsForTests()
            .Where(static tool => tool.Name is
                "submit_source_backed_route" or
                "submit_source_backed_grid_route")
            .ToArray();

        Assert.Equal(2, sourceTools.Length);
        foreach (var tool in sourceTools)
        {
            var schema = tool.Parameters;
            var required = schema.GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray();
            Assert.Contains("namedReferenceKind", required);

            var kind = schema.GetProperty("properties")
                .GetProperty("namedReferenceKind");
            Assert.Equal(
                new[] { "none", "subject", "document" },
                kind.GetProperty("enum")
                    .EnumerateArray()
                    .Select(static item => item.GetString())
                    .ToArray());
            var description = kind.GetProperty("description").GetString();
            Assert.Contains("product", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("model", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("entity", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("document", description, StringComparison.OrdinalIgnoreCase);
        }

        var prompts = new[]
        {
            ToolAgentOrchestrator.BuildNativeRouterSystemPromptForTests(),
            ToolAgentOrchestrator.BuildNativeRouterSpecializedSystemPromptForTests(
                "submit_source_backed_route"),
            ToolAgentOrchestrator.BuildNativeRouterSpecializedSystemPromptForTests(
                "submit_source_backed_grid_route")
        };
        Assert.All(prompts, static prompt =>
        {
            Assert.Contains("namedReferenceKind", prompt, StringComparison.Ordinal);
            Assert.Contains("product", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("model", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not a document", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("without an extension", prompt, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Bug135_named_subject_keeps_the_llm_query_without_creating_a_document_anchor()
    {
        const string request =
            "For the AX-17 pump model, what nominal flow rate is documented?";
        var result = BuildRoute(
            request,
            namedReferenceKind: "subject",
            document: null);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Empty(mission.RequestedDocumentName);
        Assert.Equal("subject", ReadNamedReferenceKind(mission));
        var action = Assert.Single(result.Plan.ToolCalls);
        Assert.Equal("rag.search", action.Name);
        Assert.Equal(
            "AX-17 nominal flow rate",
            action.Args.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("subject", "AX-17", "native_source_route_named_reference_inconsistent")]
    [InlineData("none", "AX-17", "native_source_route_named_reference_inconsistent")]
    [InlineData("document", null, "native_source_route_named_reference_inconsistent")]
    [InlineData("artifact", null, "native_source_route_named_reference_kind_invalid")]
    public void Bug135_named_reference_kind_and_document_must_be_mechanically_consistent(
        string namedReferenceKind,
        string? document,
        string expectedFailure)
    {
        const string request =
            "For the AX-17 document, what nominal flow rate is documented?";
        var result = BuildRoute(request, namedReferenceKind, document);

        Assert.False(result.Accepted);
        Assert.Equal(expectedFailure, result.FailureReason);
    }

    [Theory]
    [InlineData("HydraulicPumpManual.pdf")]
    [InlineData("Hydraulic Pump Service Manual")]
    public void Bug135_explicit_document_files_and_extensionless_titles_remain_exact(
        string document)
    {
        var request = "Summarize " + document + " with cited requirements.";
        var result = BuildRoute(
            request,
            namedReferenceKind: "document",
            document);

        Assert.True(result.Accepted, result.FailureReason);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            result.Plan.SourceBackedMission);
        Assert.Equal(document, mission.RequestedDocumentName);
        Assert.Equal("document", ReadNamedReferenceKind(mission));
    }

    [Fact]
    public void Bug135_router_repair_does_not_reinject_a_reference_classified_as_subject()
    {
        const string request =
            "For the AX-17 model, provide two documented operating limits.";
        var invalidCompletion = new SourceBackedAgentCompletion(
            string.Empty,
            new[]
            {
                new SourceBackedAgentToolCall(
                    "invalid-grid",
                    "submit_source_backed_grid_route",
                    JsonSerializer.SerializeToElement(new
                    {
                        tool = "cards",
                        sourceItemType = "operating limit",
                        intent = "answer",
                        useFocusedDocument = false,
                        questionFocus = "content",
                        namedReferenceKind = "subject",
                        document = "AX-17",
                        pool = 8,
                        count = 2,
                        rowHeader = "Limit",
                        rows = new[] { "1", "2" },
                        columns = new[] { "Value" }
                    }))
            },
            "tool_calls");
        var repaired = BuildRoute(
            request,
            namedReferenceKind: "subject",
            document: null);
        Assert.True(repaired.Accepted, repaired.FailureReason);

        var continuityMethod = typeof(ToolAgentOrchestrator).GetMethod(
            "PreserveExplicitDocumentAcrossNativeRouterRepair",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(continuityMethod);
        continuityMethod!.Invoke(
            null,
            new object[] { request, invalidCompletion, repaired.Plan });

        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            repaired.Plan.SourceBackedMission);
        Assert.Empty(mission.RequestedDocumentName);
        Assert.Equal("subject", ReadNamedReferenceKind(mission));
    }

    private static (
        bool Accepted,
        RouterPlan Plan,
        string FailureReason) BuildRoute(
            string request,
            string namedReferenceKind,
            string? document)
    {
        var arguments = JsonSerializer.Serialize(new
        {
            tool = "search",
            intent = "answer",
            query = "AX-17 nominal flow rate",
            answerUnitType = "documented nominal flow rate",
            answerUnitMode = "content_claim",
            selectionPolicy = "single_item",
            useFocusedDocument = false,
            questionFocus = "content",
            namedReferenceKind,
            document,
            pool = 6,
            count = 1
        });
        return ToolAgentOrchestrator.TryBuildNativeRouterPlanForTests(
            new ToolMemory(),
            request,
            "submit_source_backed_route",
            arguments);
    }

    private static string? ReadNamedReferenceKind(
        RouterPlan.SourceBackedMissionPlan mission)
    {
        var property = mission.GetType().GetProperty(
            "NamedReferenceKind",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return property!.GetValue(mission) as string;
    }
}

public sealed partial class SourceBackedAgentV2Tests
{
    [Fact]
    public async Task Bug135_immediate_named_document_insufficiency_is_reconsidered_then_subject_action_is_restored()
    {
        var firstInsufficiency = Completion(Call(
            "transition-first",
            "declare_named_document_insufficiency",
            new
            {
                reason =
                    "The current catalog has no exact identity for the requested reference."
            }));
        var reinterpretAsSubject = Completion(Call(
            "transition-reconsidered",
            "reinterpret_named_reference_as_subject",
            new { }));
        var llm = new ScriptedAgentLlm(
            firstInsufficiency,
            reinterpretAsSubject,
            FastReview(
                "research",
                "NONE",
                "NONE",
                "More evidence is required before answering."));
        var executor = new ScriptedToolExecutor(SearchResult());
        var resolver = new RecordingNamedDocumentResolver(
            Observation(
                SourceBackedDocumentResolutionStatus.NotFound,
                complete: true));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);
        var intake = TypedNamedReferenceIntake("document");

        var result = await runner.RunAsync(intake, CancellationToken.None);

        var transitionToolSets = llm.ToolSets
            .Where(static tools => tools.Any(tool => tool.Name ==
                "declare_named_document_insufficiency"))
            .ToArray();
        Assert.Equal(2, transitionToolSets.Length);
        Assert.All(transitionToolSets, static tools => Assert.Contains(
            tools,
            static tool => tool.Name ==
                "reinterpret_named_reference_as_subject"));
        Assert.Contains(
            "named_document_immediate_insufficiency_requires_reference_reconsideration",
            string.Join("\n", llm.Requests[1].Select(static message => message.Content)),
            StringComparison.Ordinal);
        Assert.Equal("rag.search", Assert.Single(executor.ToolNames));
        var executedArguments = Assert.Single(executor.Arguments);
        Assert.Equal(
            "AX-17 nominal flow rate",
            executedArguments.GetProperty("query").GetString());
        Assert.Equal(4, executedArguments.GetProperty("topK").GetInt32());
        Assert.Null(result.Intake.RequestedDocumentName);
        Assert.Null(result.Intake.RequestedDocumentResolution);
        Assert.Equal("subject", ReadIntakeNamedReferenceKind(result.Intake));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_named_document_observation.decision_completed"
            && trace.Fields["decision"]
                == "reinterpret_named_reference_as_subject"
            && trace.Fields["attempts"] == "2");
    }

    [Fact]
    public async Task Bug135_repeated_insufficiency_after_reconsideration_remains_terminal_for_a_missing_document()
    {
        SourceBackedAgentCompletion Insufficiency(string id)
            => Completion(Call(
                id,
                "declare_named_document_insufficiency",
                new
                {
                    reason =
                        "The user requested this exact document and the complete current catalog has no matching identity."
                }));
        var llm = new ScriptedAgentLlm(
            Insufficiency("transition-first"),
            Insufficiency("transition-confirmed"));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: new RecordingNamedDocumentResolver(
                Observation(
                    SourceBackedDocumentResolutionStatus.NotFound,
                    complete: true)));

        var result = await runner.RunAsync(
            TypedNamedReferenceIntake("document"),
            CancellationToken.None);

        Assert.Equal(2, llm.ToolSets.Count);
        Assert.Empty(executor.ToolNames);
        Assert.Empty(result.EvidenceBundle.Items);
        Assert.Null(result.Clarification);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_named_document_observation.decision_completed"
            && trace.Fields["decision"]
                == "declare_named_document_insufficiency"
            && trace.Fields["attempts"] == "2");
    }

    private static SourceBackedIntake TypedNamedReferenceIntake(string kind)
        => NamedDocumentIntake(
                "AX-17",
                JsonSerializer.SerializeToElement(new
                {
                    query = "AX-17 nominal flow rate",
                    topK = 4
                }))
            with
        {
            UserQuestion =
                    "For the AX-17 model, what nominal flow rate is documented?",
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                    JsonSerializer.SerializeToElement(new
                    {
                        planKind = "single_item",
                        deliverable = "one documented nominal flow rate",
                        structuredLayout = false,
                        rowCount = 1,
                        columnCount = 1,
                        atomicEvidenceCount = 1,
                        atomicEvidenceType = "documented nominal flow rate",
                        atomicEvidenceMode = "content_claim",
                        selectionPolicy = "single_item",
                        initialCapability = "rag_search",
                        namedReferenceKind = kind
                    }),
                    "llm_router")
        };

    private static string? ReadIntakeNamedReferenceKind(SourceBackedIntake intake)
    {
        var property = intake.GetType().GetProperty(
            "NamedReferenceKind",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return property!.GetValue(intake) as string;
    }
}
