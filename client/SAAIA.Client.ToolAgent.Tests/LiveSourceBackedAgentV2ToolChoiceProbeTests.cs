using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveSourceBackedAgentV2ToolChoiceProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_qwen3_chooses_the_first_observation_from_the_interpreted_mission_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_SOURCE_BACKED_AGENT_V2_TOOL_CHOICE_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_AGENT_V2_TOOL_CHOICE_PROBE=1 to run the native navigation-materialization probe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl) || string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("The live V2 tool-choice probe requires the LLM URL and model.");

        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(llmBaseUrl, model);
        var executor = new BoundedToolRecordingExecutor();
        var llm = new RecordingLlm(innerLlm);
        var structuredPlanning = !string.Equals(
            Environment.GetEnvironmentVariable(
                "SAAIA_LIVE_SEMANTIC_PLANNER_STRUCTURED"),
            "0",
            StringComparison.Ordinal);
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            new SourceBackedAgentV2Options(
                MaximumTurns: 8,
                MaximumToolCalls: 8,
                MaximumObservationItems: 10,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 1200,
                PlanningTemperature: 0,
                StructuredSemanticPlanningEnabled: structuredPlanning,
                RequireEvidenceSelectionBeforeWriter: true,
                SemanticCandidateStrategyEnabled: false,
                SemanticCandidateDefinitionEnabled: false));
        var intake = new SourceBackedIntake(
            "J'ai besoin d'un planning de repas pour la semaine du lundi au vendredi incluant petit-dejeuner, dejeuner, collation et souper.",
            "rag.plan_repas",
            Array.Empty<string>(),
            new[]
            {
                "row:Lundi",
                "row:Mardi",
                "row:Mercredi",
                "row:Jeudi",
                "row:Vendredi",
                "column:Petit-dejeuner",
                "column:Dejeuner",
                "column:Collation",
                "column:Souper"
            },
            AllowsPartialAnswer: false,
            Language: "fr",
            CatalogHints: new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine",
                    "Cuisine",
                    TotalDocuments: null,
                    Aliases: Array.Empty<string>(),
                    IsDefaultScope: false),
                new SourceBackedCatalogHint(
                    "SOP - GMP - Quality",
                    "SOP - GMP - Quality",
                    TotalDocuments: null,
                    Aliases: Array.Empty<string>(),
                    IsDefaultScope: false),
                new SourceBackedCatalogHint(
                    "Catalogue commercial",
                    "Catalogue commercial",
                    TotalDocuments: null,
                    Aliases: Array.Empty<string>(),
                    IsDefaultScope: false),
                new SourceBackedCatalogHint(
                    "Support client - FAQ",
                    "Support client - FAQ",
                    TotalDocuments: null,
                    Aliases: Array.Empty<string>(),
                    IsDefaultScope: false),
                new SourceBackedCatalogHint(
                    "Documents avec annexe",
                    "Documents avec annexe",
                    TotalDocuments: null,
                    Aliases: Array.Empty<string>(),
                    IsDefaultScope: false)
            },
            InitialSemanticMission: new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable =
                        "planning de repas du lundi au vendredi avec quatre moments par jour",
                    structuredLayout = true,
                    rowCount = 5,
                    columnCount = 4,
                    atomicEvidenceCount = 20,
                    atomicEvidenceType = "repas quotidien",
                    initialCapability = "",
                    candidateScopePaths = new[] { "Cuisine" },
                    rowHeader = "Jour",
                    rowLabels = new[]
                    {
                        "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"
                    },
                    columns = new[]
                    {
                        "Petit-déjeuner", "Déjeuner", "Collation", "Souper"
                    }
                }),
                "llm_router"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        SourceBackedPipelineResult? completedResult = null;
        var executorBoundaryReached = false;
        try
        {
            completedResult = await runner.RunAsync(intake, cts.Token);
        }
        catch (SecondToolBoundaryReachedException)
        {
            // Expected: stop before materialization so the probe remains short and read-only.
            executorBoundaryReached = true;
        }

        var report = new StringBuilder();
        report.AppendLine("LIVE SOURCE-BACKED AGENT V2 NAVIGATION-MATERIALIZATION PROBE");
        report.AppendLine("Model: " + model);
        report.AppendLine("LLM URL: " + llmBaseUrl);
        report.AppendLine("Question: " + intake.UserQuestion);
        report.AppendLine(
            "Structured semantic planning: " + structuredPlanning);
        report.AppendLine("Executor boundary reached: " + executorBoundaryReached);
        report.AppendLine("Internal tools: " + string.Join(" -> ", executor.ToolNames));
        if (completedResult is not null)
        {
            report.AppendLine("Completed answer: " + completedResult.Answer);
            report.AppendLine(
                "Trace: "
                + string.Join(
                    " || ",
                    completedResult.TraceEvents.Select(static trace =>
                        trace.Sequence + ":" + trace.EventName + " "
                        + JsonSerializer.Serialize(trace.Fields))));
        }
        for (var index = 0; index < executor.Arguments.Count; index++)
            report.AppendLine($"Arguments {index + 1}: {executor.Arguments[index]}");
        report.AppendLine();
        report.AppendLine("LLM COMPLETIONS");
        foreach (var completion in llm.Completions)
        {
            report.AppendLine(JsonSerializer.Serialize(new
            {
                content = completion.Content,
                finishReason = completion.FinishReason,
                toolCalls = completion.ToolCalls.Select(static call => new
                {
                    call.Name,
                    arguments = call.Arguments
                })
            }));
        }
        await File.WriteAllTextAsync(artifact, report.ToString(), CancellationToken.None);

        output.WriteLine("Tools: " + string.Join(" -> ", executor.ToolNames));
        output.WriteLine("Artifact: " + artifact);
        Assert.True(
            executorBoundaryReached,
            "Qwen3 did not reach the materialization boundary.");
        Assert.NotEmpty(llm.Completions);
        Assert.Contains(
            llm.Completions.SelectMany(static completion => completion.ToolCalls),
            static call => string.Equals(
                call.Name,
                "submit_initial_observation_decision",
                StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(executor.ToolNames);
        Assert.Contains(
            executor.ToolNames[0],
            new[]
            {
                "documents.navigation",
                "documents.content_cards",
                "rag.search"
            });
        using var firstArguments = JsonDocument.Parse(executor.Arguments[0]);
        Assert.Equal(
            "Cuisine",
            firstArguments.RootElement
                .GetProperty("categoryPath")
                .GetString());
        if (string.Equals(
                executor.ToolNames[0],
                "documents.navigation",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(executor.ToolNames.Count >= 2);
            Assert.Contains(
                executor.ToolNames[1],
                new[] { "documents.context_batch", "documents.content_cards", "rag.search" });
            if (string.Equals(
                    executor.ToolNames[1],
                    "documents.context_batch",
                    StringComparison.OrdinalIgnoreCase))
            {
                using var secondArguments = JsonDocument.Parse(executor.Arguments[1]);
                Assert.Equal(
                    20,
                    secondArguments.RootElement
                        .GetProperty("targets")
                        .GetArrayLength());
            }
        }
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string CreateArtifactPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, ".git")))
            current = current.Parent;
        if (current is null)
            throw new InvalidOperationException("Could not locate the repository root for the live artifact.");

        return Path.Combine(
            current.FullName,
            "artifacts",
            "live-source-backed-agent-v2-tool-choice-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            "report.txt");
    }

    private sealed class RecordingLlm(OpenAiLlmClient inner) : ISourceBackedAgentLlmClient
    {
        public List<SourceBackedAgentCompletion> Completions { get; } = new();

        public async Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            var completion = await inner.ChatOnceNativeAsync(
                messages,
                tools,
                temperatureOverride ?? 0.2,
                Math.Clamp(maxTokens, 256, 4096),
                ct,
                requireToolCall);
            Completions.Add(completion);
            return completion;
        }
    }

    private sealed class BoundedToolRecordingExecutor : ISourceBackedAgentToolExecutor
    {
        private readonly object _gate = new();

        public List<string> ToolNames { get; } = new();
        public List<string> Arguments { get; } = new();

        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            int callIndex;
            lock (_gate)
            {
                callIndex = ToolNames.Count;
                ToolNames.Add(toolName);
                Arguments.Add(arguments.GetRawText());
            }

            if (callIndex == 0
                && string.Equals(
                    toolName,
                    "documents.navigation",
                    StringComparison.OrdinalIgnoreCase))
            {
                var recipeNames = new[]
                {
                    "Boeuf bourguignon",
                    "Tartiflette",
                    "Gratin dauphinois",
                    "Osso buco",
                    "Aligot",
                    "Garbure",
                    "Couscous royal",
                    "Cassoulet",
                    "Paella",
                    "Blanquette de veau",
                    "Quiche lorraine",
                    "Gigot d'agneau",
                    "Tomates farcies",
                    "Magret de canard",
                    "Moules marinieres",
                    "Poulet basquaise",
                    "Hachis parmentier",
                    "Pot-au-feu",
                    "Ratatouille",
                    "Sole meuniere"
                };
                var results = new ToolResults();
                results.Items.Add(new ToolResults.Item
                {
                    ToolName = "documents.navigation",
                    Result = JsonSerializer.SerializeToElement(new
                    {
                        navigationOnly = true,
                        total = recipeNames.Length,
                        limit = recipeNames.Length,
                        offset = 0,
                        items = recipeNames.Select((label, index) => new
                        {
                            label,
                            kind = "navigation_entry",
                            docId = "doc-recettes",
                            docName = "recettes.pdf",
                            docPath = "Cuisine/recettes.pdf",
                            categoryPath = "Cuisine",
                            revisionId = "revision-recettes",
                            targetChunkId = "recipe-chunk-" + (index + 1),
                            targetAnchorId = "recipe-anchor-" + (index + 1),
                            targetPageStart = index + 1,
                            targetPageEnd = index + 1
                        }).ToArray()
                    })
                });
                return Task.FromResult(results);
            }

            throw new SecondToolBoundaryReachedException();
        }
    }

    private sealed class SecondToolBoundaryReachedException : Exception
    {
    }
}
