using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveNativeRouterSemanticContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_native_router_scope_hints_are_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_NATIVE_ROUTER_SCOPE_DIAGNOSTIC"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_NATIVE_ROUTER_SCOPE_DIAGNOSTIC=1 to run the scope-hint diagnostic.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        var backendUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_BACKEND_URL"),
            settings.BackendUrl);
        var apiKey = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_API_KEY"),
            SecureLocalStore.GetServerApiKey());
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model)
            || string.IsNullOrWhiteSpace(backendUrl)
            || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "The live scope-hint diagnostic requires backend, API key, LLM URL and model.");
        }

        var api = new ApiClient();
        api.Configure(
            backendUrl,
            apiKey,
            Guid.NewGuid().ToString("D"));
        var llm = new OpenAiLlmClient();
        llm.Configure(llmBaseUrl, model);
        var memory = new ToolMemory();
        var orchestrator = new ToolAgentOrchestrator(
            api,
            new NativeLlmAdapter(llm),
            memory,
            settings);
        const string question =
            "Je dois justifier une exigence : résume ANSI B11.0-2023 - "
            + "Safety of Machinery en 7 points utiles pour quelqu’un qui doit "
            + "prendre une décision.";

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var plan = await orchestrator.RouteOnlyWithCatalogForTests(
            question,
            cts.Token);
        var hintMethod = typeof(ToolAgentOrchestrator).GetMethod(
            "BuildSourceBackedLlmCategoryHintsForPrompt",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic);
        var hints = Assert.IsType<string>(hintMethod?.Invoke(
            orchestrator,
            new object[] { question, 32, true }));
        var artifact = Environment.GetEnvironmentVariable(
                           "SAAIA_NATIVE_ROUTER_SCOPE_DIAGNOSTIC_OUTPUT")
                       ?? CreateArtifactPath("live-native-router-scope-hints");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(new
            {
                Model = model,
                Question = question,
                Hints = hints.Split(
                    Environment.NewLine,
                    StringSplitOptions.RemoveEmptyEntries),
                Catalog = memory.CatalogSnapshotCache?.Categories.Select(
                    static category => new
                    {
                        category.CategoryRef,
                        category.CategoryPath,
                        category.DisplayName,
                        category.Ordinal,
                        category.TotalDocuments,
                        category.Aliases
                    }),
                Plan = plan
            }, new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifact);
        Assert.NotEmpty(memory.CatalogSnapshotCache?.Categories ?? []);
        Assert.NotNull(plan.SourceBackedMission);
        Assert.DoesNotContain(
            plan.SourceBackedMission.CandidateScopePaths,
            static scope => string.Equals(
                scope,
                "Cuisine",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Live_qwen3_clarifies_only_the_materially_ambiguous_request_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_NATIVE_ROUTER_CLARIFICATION"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_NATIVE_ROUTER_CLARIFICATION=1 to run the clarification probe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The live clarification probe requires the LLM URL and model.");
        }

        var llm = new OpenAiLlmClient();
        llm.Configure(llmBaseUrl, model);
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories = new()
                {
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = "cat_001",
                        CategoryPath = "Cuisine",
                        DisplayName = "Cuisine",
                        Ordinal = 1,
                        TotalDocuments = 10
                    }
                }
            }
        };
        var orchestrator = new ToolAgentOrchestrator(
            api: null!,
            new NativeLlmAdapter(llm),
            memory,
            settings);
        const string ambiguous =
            "Je veux utiliser la base documentaire pour un planning hebdomadaire, "
            + "mais je n'ai pas choisi entre deux livrables differents : soit composer "
            + "chaque creneau avec une recette distincte, soit retrouver un planning "
            + "hebdomadaire deja constitue. Avant la recherche, demande-moi de choisir.";
        const string precise =
            "Compose un planning du lundi au vendredi avec petit-dejeuner, dejeuner, "
            + "collation et souper. Chaque cellule doit contenir une recette distincte "
            + "trouvee et sourcee dans la base Cuisine.";

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var streamed = new StringBuilder();
        var ambiguousRun = await orchestrator.RunAsync(
            Array.Empty<(string role, string content)>(),
            ambiguous,
            cts.Token,
            onDelta: delta => streamed.Append(delta));
        var pendingClarification = memory.PendingClarification;
        var precisePlan = await orchestrator.RouteOnlyForTests(
            precise,
            cts.Token);
        var artifact = CreateArtifactPath("live-native-router-clarification");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(new
            {
                Model = model,
                LlmUrl = llmBaseUrl,
                AmbiguousQuestion = ambiguous,
                AmbiguousAnswer = ambiguousRun.finalAnswer,
                AmbiguousStream = streamed.ToString(),
                PendingClarification = pendingClarification,
                ToolNamesAfterAmbiguousTurn = memory.LastToolNames,
                RagQueriesAfterAmbiguousTurn = memory.LastRagQueries,
                PreciseQuestion = precise,
                PrecisePlan = precisePlan
            }, new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifact);
        Assert.Null(ambiguousRun.sourcesPayload);
        Assert.Equal(ambiguousRun.finalAnswer, streamed.ToString());
        Assert.Empty(memory.LastToolNames);
        Assert.Empty(memory.LastRagQueries);
        var clarification = Assert.IsType<ToolMemory.PendingClarificationState>(
            pendingClarification);
        Assert.Equal("llm_router", clarification.Kind);
        Assert.InRange(clarification.Options.Count, 2, 4);
        Assert.False(string.IsNullOrWhiteSpace(clarification.Question));
        Assert.Contains(
            "composer chaque creneau avec une recette distincte",
            ambiguousRun.finalAnswer,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "retrouver un planning hebdomadaire deja constitue",
            ambiguousRun.finalAnswer,
            StringComparison.OrdinalIgnoreCase);
        Assert.All(
            clarification.Options,
            option => Assert.DoesNotContain(
                option,
                clarification.Question,
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RouterPlanOrigin.Llm, precisePlan.Origin);
        Assert.False(precisePlan.NeedClarification);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            precisePlan.SourceBackedMission);
        Assert.Equal("structured_layout", mission.PlanKind);
        Assert.Equal(20, mission.AtomicEvidenceCount);
        Assert.Equal(precise, mission.Deliverable);
    }

    [Fact]
    public async Task Live_qwen3_chooses_an_atomic_initial_action_after_grid_axes()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_INITIAL_GRID_ACTION"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_INITIAL_GRID_ACTION=1 to run the compact initial-action probe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The live initial-action probe requires the LLM URL and model.");
        }

        const string question =
            "J'ai besoin d'un planning de repas pour la semaine du lundi au "
            + "vendredi incluant petit-déjeuner, déjeuner, collation et souper. "
            + "Fais un format clair et professionnel, avec uniquement des sources "
            + "utiles, non dupliquées inutilement. N'invente rien.";
        var intake = new SourceBackedIntake(
            question,
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "fr",
            CatalogHints: new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine",
                    "Cuisine",
                    10,
                    Array.Empty<string>(),
                    IsDefaultScope: true)
            },
            InitialSemanticMission: new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable =
                        "planning professionnel du lundi au vendredi avec quatre repas",
                    structuredLayout = true,
                    rowCount = 5,
                    columnCount = 4,
                    atomicEvidenceCount = 20,
                    atomicEvidenceType =
                        "une recette ou un exemple concret de repas",
                    initialCapability = "",
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
        var nativeLlm = new OpenAiLlmClient();
        nativeLlm.Configure(llmBaseUrl, model);
        var executor = new CapturingToolExecutor();
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(nativeLlm),
            executor,
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 4,
                MaximumObservationExcerptCharacters: 120,
                MaximumOutputTokens: 256,
                MaximumActionTokens: 192,
                MaximumWorkingEvidenceItems: 8,
                MaximumWorkingExcerptCharacters: 120,
                MaximumPlanningTokens: 128,
                PlanningTemperature: 0,
                MaximumSemanticReviewTokens: 128,
                SemanticReviewTemperature: 0,
                SeparateActionAndWriter: false,
                MaximumSemanticCorrectionTurns: 0,
                SemanticCandidateAuditEnabled: false,
                MaximumContextTokens: 8192,
                SemanticColumnRoleReviewEnabled: true,
                MaximumSemanticColumnRoleTokens: 180,
                MaximumSelectionProtocolRepairTurns: 0,
                StructuredSemanticPlanningEnabled: true,
                RequireEvidenceSelectionBeforeWriter: false));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var result = await runner.RunAsync(intake, cts.Token);
        var actionTrace = result.TraceEvents.FirstOrDefault(static trace =>
            trace.EventName
                == "source_backed_agent_v2.initial_research_action.completed");
        var toolName = actionTrace?.Fields.GetValueOrDefault("tool")
                       ?? string.Empty;
        var argumentJson =
            actionTrace?.Fields.GetValueOrDefault("arguments")
            ?? string.Empty;
        var arguments = TryParseObject(argumentJson);
        var query = ReadOptionalString(arguments, "query")
                    ?? ReadOptionalString(arguments, "q")
                    ?? string.Empty;
        var artifact = CreateArtifactPath("live-initial-grid-action");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(
            artifact,
            new StringBuilder()
                .AppendLine("LIVE COMPACT INITIAL GRID ACTION")
                .AppendLine("Model: " + model)
                .AppendLine("LLM URL: " + llmBaseUrl)
                .AppendLine("Tool: " + toolName)
                .AppendLine("Arguments: " + argumentJson)
                .AppendLine("Query: " + query)
                .AppendLine(
                    "Executor calls: "
                    + string.Join(" | ", executor.ToolNames))
                .AppendLine("Trace:")
                .AppendLine(JsonSerializer.Serialize(
                    result.TraceEvents,
                    new JsonSerializerOptions { WriteIndented = true }))
                .ToString(),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifact);
        Assert.NotNull(actionTrace);
        Assert.Equal("true", actionTrace.Fields["protocol_valid"]);
        Assert.False(string.IsNullOrWhiteSpace(toolName));
        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
        Assert.Equal(
            "Cuisine",
            ReadOptionalString(arguments, "categoryPath"));
        Assert.DoesNotContain(
            "planning",
            query,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "semaine",
            query,
            StringComparison.OrdinalIgnoreCase);
        foreach (var placementLabel in new[]
                 {
                     "Jour", "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"
                 })
        {
            Assert.DoesNotContain(
                placementLabel,
                query,
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.initial_research_action.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["decision_source"]
                == "llm_initial_research_action");
    }

    [Fact]
    public async Task Live_qwen3_chooses_clean_initial_actions_for_generic_grids()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_INITIAL_GRID_MATRIX"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_INITIAL_GRID_MATRIX=1 to run the generic initial-action matrix.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The live generic-grid matrix requires the LLM URL and model.");
        }

        var scenarios = new[]
        {
            new InitialGridProbeScenario(
                "maintenance",
                "Prépare un planning de maintenance pour le premier et le "
                + "deuxième trimestre, avec inspection et remplacement.",
                "planning de maintenance documente",
                "une operation de maintenance documentee",
                "Trimestre",
                new[] { "Premier", "Deuxieme" },
                new[] { "Inspection", "Remplacement" },
                "Maintenance",
                new[] { "planning", "trimestre", "premier", "deuxieme" }),
            new InitialGridProbeScenario(
                "engineering",
                "Build a comparison matrix for alloys A36 and 304L across "
                + "yield strength and tensile strength.",
                "documented engineering comparison",
                "one documented technical value for an alloy and property",
                "Alloy",
                new[] { "A36", "304L" },
                new[] { "Yield strength", "Tensile strength" },
                "Engineering",
                new[] { "matrix", "comparison matrix" }),
            new InitialGridProbeScenario(
                "training",
                "Create a Monday to Friday training plan with cardio and "
                + "mobility activities.",
                "weekly training plan",
                "one named documented training activity",
                "Day",
                new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" },
                new[] { "Cardio", "Mobility" },
                "Training",
                new[] { "plan", "monday", "tuesday", "wednesday", "thursday", "friday" })
        };

        var nativeLlm = new OpenAiLlmClient();
        nativeLlm.Configure(llmBaseUrl, model);
        var observations = new List<InitialGridProbeObservation>();
        foreach (var scenario in scenarios)
        {
            var intake = new SourceBackedIntake(
                scenario.Question,
                "rag.answer",
                Array.Empty<string>(),
                Array.Empty<string>(),
                AllowsPartialAnswer: false,
                Language: scenario.Name == "maintenance" ? "fr" : "en",
                CatalogHints: new[]
                {
                    new SourceBackedCatalogHint(
                        scenario.CategoryPath,
                        scenario.CategoryPath,
                        10,
                        Array.Empty<string>(),
                        IsDefaultScope: true)
                },
                InitialSemanticMission: new SourceBackedInitialSemanticMission(
                    JsonSerializer.SerializeToElement(new
                    {
                        planKind = "structured_layout",
                        deliverable = scenario.Deliverable,
                        structuredLayout = true,
                        rowCount = scenario.Rows.Count,
                        columnCount = scenario.Columns.Count,
                        atomicEvidenceCount =
                            scenario.Rows.Count * scenario.Columns.Count,
                        atomicEvidenceType = scenario.AtomicEvidenceType,
                        initialCapability = "",
                        rowHeader = scenario.RowHeader,
                        rowLabels = scenario.Rows,
                        columns = scenario.Columns
                    }),
                    "llm_router"));
            var executor = new CapturingToolExecutor();
            var runner = new SourceBackedAgentV2Runner(
                new NativeLlmAdapter(nativeLlm),
                executor,
                new SourceBackedAgentV2Options(
                    MaximumTurns: 1,
                    MaximumToolCalls: 1,
                    MaximumObservationItems: 4,
                    MaximumObservationExcerptCharacters: 120,
                    MaximumOutputTokens: 256,
                    MaximumActionTokens: 192,
                    MaximumWorkingEvidenceItems: 8,
                    MaximumWorkingExcerptCharacters: 120,
                    MaximumPlanningTokens: 128,
                    PlanningTemperature: 0,
                    MaximumSemanticReviewTokens: 128,
                    SemanticReviewTemperature: 0,
                    SeparateActionAndWriter: false,
                    MaximumSemanticCorrectionTurns: 0,
                    SemanticCandidateAuditEnabled: false,
                    MaximumContextTokens: 8192,
                    SemanticColumnRoleReviewEnabled: true,
                    MaximumSemanticColumnRoleTokens: 180,
                    MaximumSelectionProtocolRepairTurns: 0,
                    StructuredSemanticPlanningEnabled: true,
                    RequireEvidenceSelectionBeforeWriter: false));
            using var cts =
                new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var result = await runner.RunAsync(intake, cts.Token);
            var actionTrace = result.TraceEvents.FirstOrDefault(static trace =>
                trace.EventName
                    == "source_backed_agent_v2.initial_research_action.completed");
            var argumentJson =
                actionTrace?.Fields.GetValueOrDefault("arguments")
                ?? string.Empty;
            var arguments = TryParseObject(argumentJson);
            var query = ReadOptionalString(arguments, "query")
                        ?? ReadOptionalString(arguments, "q")
                        ?? string.Empty;
            observations.Add(new InitialGridProbeObservation(
                scenario,
                actionTrace?.Fields.GetValueOrDefault("tool")
                ?? string.Empty,
                arguments,
                query,
                executor.ToolNames.ToArray(),
                result.TraceEvents));
        }

        var artifact = CreateArtifactPath("live-initial-grid-matrix");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(
                observations.Select(static observation => new
                {
                    observation.Scenario.Name,
                    observation.Scenario.Question,
                    observation.Scenario.AtomicEvidenceType,
                    observation.Scenario.CategoryPath,
                    observation.Tool,
                    Arguments = observation.Arguments,
                    observation.Query,
                    ExecutorCalls = observation.ExecutorCalls,
                    Trace = observation.TraceEvents
                }),
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);
        output.WriteLine("Artifact: " + artifact);

        foreach (var observation in observations)
        {
            Assert.False(string.IsNullOrWhiteSpace(observation.Tool));
            Assert.Equal(
                JsonValueKind.Object,
                observation.Arguments.ValueKind);
            Assert.Equal(
                observation.Scenario.CategoryPath,
                ReadOptionalString(
                    observation.Arguments,
                    "categoryPath"));
            foreach (var forbidden in observation.Scenario.ForbiddenQueryTerms)
            {
                Assert.DoesNotContain(
                    forbidden,
                    observation.Query,
                    StringComparison.OrdinalIgnoreCase);
            }
            Assert.Single(observation.ExecutorCalls);
            Assert.Contains(observation.TraceEvents, static trace =>
                trace.EventName
                    == "source_backed_agent_v2.semantic_candidate_strategy.completed"
                && trace.Fields["attempted"] == "false");
            Assert.Contains(observation.TraceEvents, trace =>
                trace.EventName
                    == "source_backed_agent_v2.semantic_plan.completed"
                && trace.Fields["plan"].Contains(
                    observation.Scenario.AtomicEvidenceType,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Live_qwen3_preserves_a_two_axis_request_in_the_native_route_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_NATIVE_ROUTER_GRID"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_NATIVE_ROUTER_GRID=1 to run the native semantic-route probe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The live native-router probe requires the LLM URL and model.");
        }

        var llm = new OpenAiLlmClient();
        llm.Configure(llmBaseUrl, model);
        var backendUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_BACKEND_URL"),
            settings.BackendUrl);
        var apiKey = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_API_KEY"),
            SecureLocalStore.GetServerApiKey());
        if (string.IsNullOrWhiteSpace(backendUrl)
            || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "The live native-router catalog probe requires the backend URL and API key.");
        }

        var api = new ApiClient();
        api.Configure(
            backendUrl,
            apiKey,
            Guid.NewGuid().ToString("D"));
        var memory = new ToolMemory();
        var orchestrator = new ToolAgentOrchestrator(
            api,
            new NativeLlmAdapter(llm),
            memory,
            settings);
        const string question =
            "J'ai besoin d'un planning de repas pour la semaine du lundi au "
            + "vendredi incluant petit-déjeuner, déjeuner, collation et souper. "
            + "Fais un format clair et professionnel, avec uniquement des sources "
            + "utiles, non dupliquées inutilement. N'invente rien.";

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var plan = await orchestrator.RouteOnlyWithCatalogForTests(
            question,
            cts.Token);
        var artifact = CreateArtifactPath("live-native-router-grid");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        var report = new StringBuilder()
            .AppendLine("LIVE NATIVE ROUTER SEMANTIC CONTRACT")
            .AppendLine("Model: " + model)
            .AppendLine("LLM URL: " + llmBaseUrl)
            .AppendLine("Question: " + question)
            .AppendLine(
                "Catalog categories: "
                + (memory.CatalogSnapshotCache?.Categories.Count ?? 0))
            .AppendLine("Catalog identities:")
            .AppendLine(JsonSerializer.Serialize(
                memory.CatalogSnapshotCache?.Categories.Select(static category => new
                {
                    category.CategoryRef,
                    category.CategoryPath,
                    category.DisplayName,
                    category.Ordinal
                }),
                new JsonSerializerOptions { WriteIndented = true }))
            .AppendLine("Plan:")
            .AppendLine(JsonSerializer.Serialize(
                plan,
                new JsonSerializerOptions { WriteIndented = true }))
            .ToString();
        await File.WriteAllTextAsync(artifact, report, CancellationToken.None);

        output.WriteLine("Artifact: " + artifact);
        Assert.Equal(RouterPlanOrigin.Llm, plan.Origin);
        Assert.Equal("rag.answer", plan.Intent);
        var mission = Assert.IsType<RouterPlan.SourceBackedMissionPlan>(
            plan.SourceBackedMission);
        Assert.Equal("structured_layout", mission.PlanKind);
        Assert.True(mission.StructuredLayout);
        Assert.Equal(5, mission.RowCount);
        Assert.Equal(4, mission.ColumnCount);
        Assert.Equal(20, mission.AtomicEvidenceCount);
        var initialAction = Assert.Single(plan.ToolCalls);
        Assert.Contains(
            initialAction.Name,
            new[]
            {
                "documents.navigation",
                "documents.content_cards",
                "rag.search"
            });
        Assert.Contains(
            mission.InitialCapability,
            new[]
            {
                "documents_navigation",
                "documents_content_cards",
                "rag_search"
            });
        Assert.Equal(
            "Cuisine",
            ReadOptionalString(initialAction.Args, "categoryPath"));
        var initialQuery = ReadOptionalString(initialAction.Args, "query")
                           ?? ReadOptionalString(initialAction.Args, "q")
                           ?? string.Empty;
        foreach (var coordinate in new[] { mission.RowHeader }
                     .Concat(mission.RowLabels)
                     .Concat(mission.Columns))
        {
            Assert.DoesNotContain(
                coordinate,
                initialQuery,
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(mission.RequestedDocumentName);
        Assert.Equal(new[] { "Cuisine" }, mission.CandidateScopePaths);
        Assert.DoesNotContain(
            ".pdf",
            mission.AtomicEvidenceType,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "planning",
            mission.AtomicEvidenceType,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "semaine",
            mission.AtomicEvidenceType,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ingrédient",
            mission.AtomicEvidenceType,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ingredient",
            mission.AtomicEvidenceType,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "méthode",
            mission.AtomicEvidenceType,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "methode",
            mission.AtomicEvidenceType,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(mission.AtomicEvidenceType));
        Assert.True(
            mission.AtomicEvidenceType.Contains(
                "recette",
                StringComparison.OrdinalIgnoreCase)
            || mission.AtomicEvidenceType.Contains(
                "plat",
                StringComparison.OrdinalIgnoreCase)
            || mission.AtomicEvidenceType.Contains(
                "repas",
                StringComparison.OrdinalIgnoreCase)
            || mission.AtomicEvidenceType.Contains(
                "preparation culinaire",
                StringComparison.OrdinalIgnoreCase),
            "The meal-plan source item must be a named answer-bearing item "
            + "found inside culinary documents, not a schedule or file.");
        Assert.Equal(
            new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
            mission.RowLabels,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(4, mission.Columns.Count);
        Assert.Contains(
            mission.Columns,
            static column =>
                column.Contains("Petit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            mission.Columns,
            static column =>
                column.Contains("jeuner", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            mission.Columns,
            static column =>
                column.Contains("Collation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            mission.Columns,
            static column =>
                column.Contains("Souper", StringComparison.OrdinalIgnoreCase));
        foreach (var coordinate in new[] { mission.RowHeader }
                     .Concat(mission.RowLabels)
                     .Concat(mission.Columns))
        {
            Assert.False(
                Regex.IsMatch(
                    mission.AtomicEvidenceType,
                    $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(coordinate)}(?![\p{{L}}\p{{N}}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                $"The source item type embeds the exact layout coordinate '{coordinate}'.");
        }
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(
            static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string CreateArtifactPath(string prefix)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
               && !Directory.Exists(Path.Combine(current.FullName, ".git")))
        {
            current = current.Parent;
        }
        if (current is null)
        {
            throw new InvalidOperationException(
                "Could not locate the repository root for the live artifact.");
        }

        return Path.Combine(
            current.FullName,
            "artifacts",
            prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            "report.txt");
    }

    private static string? ReadOptionalString(
        JsonElement arguments,
        string propertyName)
        => arguments.ValueKind == JsonValueKind.Object
           && arguments.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement TryParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private sealed class NativeLlmAdapter(OpenAiLlmClient inner) :
        ILlmClient,
        ISourceBackedAgentLlmClient
    {
        public bool SupportsStructuredOutput => true;

        public Task<string> CompleteAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            CancellationToken ct)
            => inner.ChatOnceAsync(
                messages,
                temperature: 0,
                maxTokens: 900,
                ct: ct,
                forceJson: forceJson);

        public Task<string> CompleteStructuredAsync(
            IReadOnlyList<(string role, string content)> messages,
            LlmStructuredOutputContract contract,
            CancellationToken ct)
            => inner.ChatOnceStructuredAsync(
                messages,
                temperature: 0,
                maxTokens: 900,
                contract: contract,
                ct: ct);

        public Task StreamAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            Action<string> onDelta,
            CancellationToken ct)
            => inner.ChatStreamAsync(
                messages,
                temperature: 0,
                maxTokens: 900,
                onDelta: onDelta,
                ct: ct);

        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
            => inner.ChatOnceNativeAsync(
                messages,
                tools,
                temperatureOverride ?? 0,
                maxTokens,
                ct,
                requireToolCall);
    }

    private sealed class CapturingToolExecutor :
        ISourceBackedAgentToolExecutor
    {
        public List<string> ToolNames { get; } = new();
        public List<JsonElement> Arguments { get; } = new();

        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            ToolNames.Add(toolName);
            Arguments.Add(arguments.Clone());
            return Task.FromResult(new ToolResults());
        }
    }

    private static ToolMemory.CategorySnapshot CatalogCategory(
        string path,
        int ordinal)
        => new()
        {
            CategoryRef = path,
            CategoryPath = path,
            DisplayName = path,
            Ordinal = ordinal,
            TotalDocuments = 10
        };

    private sealed record InitialGridProbeScenario(
        string Name,
        string Question,
        string Deliverable,
        string AtomicEvidenceType,
        string RowHeader,
        IReadOnlyList<string> Rows,
        IReadOnlyList<string> Columns,
        string CategoryPath,
        IReadOnlyList<string> ForbiddenQueryTerms);

    private sealed record InitialGridProbeObservation(
        InitialGridProbeScenario Scenario,
        string Tool,
        JsonElement Arguments,
        string Query,
        IReadOnlyList<string> ExecutorCalls,
        IReadOnlyList<SourceBackedTraceEvent> TraceEvents);
}
