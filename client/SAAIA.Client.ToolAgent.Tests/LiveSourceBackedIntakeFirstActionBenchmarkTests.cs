using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

/// <summary>
/// Read-only phase-1 benchmark. It joins the real router and the production
/// SourceBackedAgentV2 options, then stops at the first documentary tool. An
/// opt-in mode executes that single read-only observation before stopping. A
/// mechanically green probe is not an architectural approval: thresholds and
/// semantic findings are written in the report.
/// </summary>
public sealed class LiveSourceBackedIntakeFirstActionBenchmarkTests(
    ITestOutputHelper output)
{
    private const string EnableVariable =
        "SAAIA_LIVE_SOURCE_BACKED_INTAKE_FIRST_ACTION_BENCHMARK";
    private const string ExperimentVariable =
        "SAAIA_PHASE1_FIRST_ACTION_EXPERIMENT";
    private const string VariantVariable =
        "SAAIA_PHASE1_FIRST_ACTION_VARIANT";
    private const string ExecuteFirstObservationVariable =
        "SAAIA_PHASE1_EXECUTE_FIRST_OBSERVATION";

    [Fact]
    public async Task Live_router_to_first_tool_boundary_is_fully_instrumented_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine($"Skipped: set {EnableVariable}=1 to run the phase-1 benchmark.");
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
                "The phase-1 benchmark requires the local LLM URL/model and backend URL/API key.");
        }

        var selectedScenarios = SelectScenarios(BuildScenarios());
        var executeFirstObservation = string.Equals(
            Environment.GetEnvironmentVariable(ExecuteFirstObservationVariable),
            "1",
            StringComparison.Ordinal);
        var results = new List<ScenarioResult>();
        foreach (var scenario in selectedScenarios)
        {
            results.Add(await RunScenarioAsync(
                scenario,
                settings,
                llmBaseUrl,
                model,
                backendUrl,
                apiKey,
                executeFirstObservation));
        }

        var artifact = ResolveArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        var experiment = FirstNonBlank(
            Environment.GetEnvironmentVariable(ExperimentVariable),
            "EXP-005");
        var variant = FirstNonBlank(
            Environment.GetEnvironmentVariable(VariantVariable),
            "C-router-first-observation-without-pre-observation-reviews");
        var report = new
        {
            experiment,
            variant,
            approval = "TESTE_NON_APPROUVE",
            generatedAt = DateTimeOffset.Now,
            machine = Environment.MachineName,
            model,
            llmBaseUrl,
            backendUrl,
            executeFirstObservation,
            options = SourceBackedAgentV2Options.ResolveFromEnvironment(),
            tokenMeasurement = new
            {
                nativeCalls = "server-reported when available",
                structuredCalls =
                    "OpenAiLlmClient returns text only; characters and explicit null token counts are retained",
                routerCalls =
                    "OpenAiLlmClient returns text only; characters and explicit null token counts are retained"
            },
            thresholds = new
            {
                maximumQuestionToFirstToolMilliseconds = 30_000,
                maximumQuestionToFirstObservationMilliseconds = 30_000,
                mealPermutationPassCount = "5/5",
                noHypotheticalLeakage = true,
                exactScopeContinuity = true,
                noToolAfterClarification = true
            },
            results
        };
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifact);
        foreach (var result in results)
        {
            output.WriteLine(
                $"{result.ScenarioId}: route={result.RouterMilliseconds} ms, "
                + $"firstTool={result.FirstTool ?? "<none>"}, "
                + $"boundary={result.QuestionToFirstToolMilliseconds?.ToString() ?? "n/a"} ms, "
                + $"observation={result.QuestionToFirstObservationMilliseconds?.ToString() ?? "n/a"} ms, "
                + $"verdict={result.Verdict}");
        }

        Assert.Equal(selectedScenarios.Count, results.Count);
        Assert.All(results, static result => Assert.True(result.ReportCompleted));
    }

    private static async Task<ScenarioResult> RunScenarioAsync(
        Scenario scenario,
        AppSettings settings,
        string llmBaseUrl,
        string model,
        string backendUrl,
        string apiKey,
        bool executeFirstObservation)
    {
        var nativeLlm = new OpenAiLlmClient();
        nativeLlm.Configure(llmBaseUrl, model);
        var api = new ApiClient();
        api.Configure(backendUrl, apiKey, Guid.NewGuid().ToString("D"));
        var memory = new ToolMemory();
        if (!string.IsNullOrWhiteSpace(scenario.SyntheticScope))
        {
            memory.CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                SnapshotId = "phase1-synthetic-scope",
                TotalCategories = 1,
                TotalDocuments = 10,
                Categories =
                [
                    new ToolMemory.CategorySnapshot
                    {
                        CategoryRef = scenario.SyntheticScope,
                        CategoryPath = scenario.SyntheticScope,
                        DisplayName = scenario.SyntheticScope,
                        Ordinal = 1,
                        TotalDocuments = 10
                    }
                ]
            };
        }

        var total = Stopwatch.StartNew();
        var llm = new TimedLlmAdapter(
            nativeLlm,
            settings.LlmTemperature,
            settings.LlmMaxOutputTokens,
            total);
        var orchestrator = new ToolAgentOrchestrator(api, llm, memory, settings);
        RouterPlan? plan = null;
        long routerMilliseconds = 0;
        long runtimeContextMilliseconds = 0;
        int? runtimeContextTokens = null;
        var firstTool = (string?)null;
        var firstArguments = (JsonElement?)null;
        long? boundaryMilliseconds = null;
        long? backendMilliseconds = null;
        long? observationMilliseconds = null;
        int? observationEvidenceCount = null;
        int? observationDistinctDocumentCount = null;
        IReadOnlyList<FirstObservationSample> observationSamples =
            Array.Empty<FirstObservationSample>();
        var executionError = (string?)null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try
        {
            llm.SetPhase("router");
            var routerTimer = Stopwatch.StartNew();
            plan = string.IsNullOrWhiteSpace(scenario.SyntheticScope)
                ? await orchestrator.RouteOnlyWithCatalogForTests(
                    scenario.Question,
                    cts.Token)
                : await orchestrator.RouteOnlyForTests(
                    scenario.Question,
                    cts.Token);
            routerMilliseconds = routerTimer.ElapsedMilliseconds;

            if (!plan.NeedClarification && plan.SourceBackedMission is not null)
            {
                llm.SetPhase("runner");
                var runtimeTimer = Stopwatch.StartNew();
                runtimeContextTokens = await llm.GetRuntimeContextTokensAsync(cts.Token);
                runtimeContextMilliseconds = runtimeTimer.ElapsedMilliseconds;
                var resolvedOptions =
                    SourceBackedAgentV2Options.ResolveFromEnvironment();
                var options = resolvedOptions with
                {
                    MaximumContextTokens =
                        runtimeContextTokens
                        ?? resolvedOptions.MaximumContextTokens
                };
                var executor = new FirstToolBoundaryExecutor(
                    total,
                    executeFirstObservation ? api : null);
                var runner = new SourceBackedAgentV2Runner(llm, executor, options);
                try
                {
                    await runner.RunAsync(
                        BuildIntake(scenario.Question, plan, memory),
                        cts.Token);
                }
                catch (FirstToolBoundaryReachedException)
                {
                    firstTool = executor.ToolName;
                    firstArguments = executor.Arguments;
                    boundaryMilliseconds = executor.ElapsedMilliseconds;
                    backendMilliseconds = executor.BackendElapsedMilliseconds;
                    observationMilliseconds =
                        executor.ObservationElapsedMilliseconds;
                    observationEvidenceCount = executor.ObservationEvidenceCount;
                    observationDistinctDocumentCount =
                        executor.ObservationDistinctDocumentCount;
                    observationSamples = executor.ObservationSamples;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            executionError = ex.GetType().Name + ": " + ex.Message;
        }

        var mission = plan?.SourceBackedMission;
        var candidateDefinition = llm.Calls.LastOrDefault(static call =>
            string.Equals(
                call.Contract,
                "source_backed_atomic_candidate_definition_v7",
                StringComparison.Ordinal));
        var hypothetical = ReadJsonString(
            candidateDefinition?.Content,
            "hypotheticalSinglePositionValue");
        var protectedArguments = EnumerateProtectedArguments(firstArguments).ToArray();
        var missionText = mission is null
            ? string.Empty
            : JsonSerializer.Serialize(mission, ClientJson.CamelCase);
        var columnRoleText = string.Join(
            '\n',
            llm.Calls
                .Where(static call => call.Step == "semantic-column-roles")
                .SelectMany(static call => call.ToolCalls)
                .Select(static call => call.Arguments.GetRawText()));
        var candidateText = candidateDefinition?.Content ?? string.Empty;
        var argumentTermProvenance = BuildArgumentTermProvenance(
            protectedArguments,
            scenario.Question,
            missionText,
            columnRoleText,
            candidateText);
        var unrequestedArgumentTerms = argumentTermProvenance
            .Where(static pair => !pair.Value.Contains(
                "question",
                StringComparer.Ordinal))
            .Select(static pair => pair.Key)
            .Where(static term => !ArgumentStopWords.Contains(term))
            .ToArray();
        var forbiddenHits = scenario.ForbiddenArgumentTerms
            .Where(term => protectedArguments.Any(value =>
                ContainsTerm(value, term)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hypotheticalLeak = !string.IsNullOrWhiteSpace(hypothetical)
            && protectedArguments.Any(value => ContainsTerm(value, hypothetical));
        var scopeContinuity = EvaluateScopeContinuity(mission, firstArguments);
        var toolAfterClarification = plan?.NeedClarification == true
                                     && firstTool is not null;
        var measuredLatencyMilliseconds =
            observationMilliseconds ?? boundaryMilliseconds;
        var underLatencyThreshold = measuredLatencyMilliseconds is < 30_000;
        var verdict = executionError is not null
            ? "ERREUR_EXECUTION_NON_APPROUVE"
            : plan?.NeedClarification == true
                ? toolAfterClarification
                    ? "ECHEC_OUTIL_APRES_CLARIFICATION"
                    : "CLARIFICATION_SANS_OUTIL_A_EVALUER"
                : firstTool is null
                    ? "AUCUNE_FRONTIERE_OUTIL_NON_APPROUVE"
                    : forbiddenHits.Length > 0 || hypotheticalLeak || !scopeContinuity
                        ? "ECHEC_INVARIANT_NON_APPROUVE"
                        : underLatencyThreshold
                            ? "SEUIL_LOCAL_ATTEINT_NON_APPROUVE"
                            : "SEUIL_LATENCE_DEPASSE_NON_APPROUVE";

        return new ScenarioResult(
            ScenarioId: scenario.Id,
            ScenarioKind: scenario.Kind,
            Question: scenario.Question,
            ReportCompleted: true,
            RouterMilliseconds: routerMilliseconds,
            RuntimeContextMilliseconds: runtimeContextMilliseconds,
            RuntimeContextTokens: runtimeContextTokens,
            QuestionToFirstToolMilliseconds: boundaryMilliseconds,
            FirstObservationBackendMilliseconds: backendMilliseconds,
            QuestionToFirstObservationMilliseconds: observationMilliseconds,
            FirstObservationEvidenceCount: observationEvidenceCount,
            FirstObservationDistinctDocumentCount:
                observationDistinctDocumentCount,
            FirstObservationSamples: observationSamples,
            RouterPlan: plan,
            Mission: mission,
            CatalogCategories: memory.CatalogSnapshotCache?.Categories
                .Select(static category => category.CategoryPath)
                .ToArray() ?? Array.Empty<string>(),
            LlmCalls: llm.Calls.ToArray(),
            FirstTool: firstTool,
            FirstArguments: firstArguments,
            ProtectedArgumentValues: protectedArguments,
            ArgumentTermProvenance: argumentTermProvenance,
            UnrequestedArgumentTerms: unrequestedArgumentTerms,
            HypotheticalSinglePositionValue: hypothetical,
            ForbiddenArgumentHits: forbiddenHits,
            HypotheticalLeak: hypotheticalLeak,
            ScopeContinuity: scopeContinuity,
            ToolAfterClarification: toolAfterClarification,
            UnderLatencyThreshold: underLatencyThreshold,
            ExecutionError: executionError,
            Verdict: verdict);
    }

    private static SourceBackedIntake BuildIntake(
        string question,
        RouterPlan plan,
        ToolMemory memory)
    {
        var initialCalls = plan.ToolCalls
            .Select((call, index) =>
            {
                if (call.Args.ValueKind != JsonValueKind.Object
                    || !SourceBackedAgentToolCatalog.TryResolveExternalName(
                        call.Name,
                        out var externalName))
                {
                    return null;
                }

                return new SourceBackedInitialToolCall(
                    $"router-plan-{index + 1}",
                    externalName,
                    SourceBackedAgentToolCatalog.NormalizeRouterArguments(
                        call.Name,
                        call.Args),
                    "llm_router");
            })
            .Where(static call => call is not null)
            .Cast<SourceBackedInitialToolCall>()
            .ToArray();
        var mission = plan.SourceBackedMission!;
        return new SourceBackedIntake(
            question,
            plan.Intent,
            plan.RiskFlags?.ToArray() ?? Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: plan.Language,
            CatalogHints: memory.CatalogSnapshotCache?.Categories
                .Select(static category => new SourceBackedCatalogHint(
                    category.CategoryPath,
                    category.DisplayName,
                    category.TotalDocuments,
                    category.Aliases,
                    IsDefaultScope: false))
                .ToArray(),
            QuestionFocus: NullIfBlank(mission.QuestionFocus),
            RequestedDocumentName: NullIfBlank(mission.RequestedDocumentName),
            InitialToolCalls: initialCalls,
            InitialSemanticMission: new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(mission, ClientJson.CamelCase),
                "llm_router"));
    }

    private static IReadOnlyList<Scenario> SelectScenarios(
        IReadOnlyList<Scenario> scenarios)
    {
        var raw = Environment.GetEnvironmentVariable(
            "SAAIA_PHASE1_FIRST_ACTION_SCENARIOS");
        if (string.IsNullOrWhiteSpace(raw))
            raw = "p1";
        var selectedIds = raw.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedIds.Contains("all"))
            return scenarios;
        var selected = scenarios.Where(scenario => selectedIds.Contains(scenario.Id))
            .ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("No EXP-003 scenario matched: " + raw);
        return selected;
    }

    private static IReadOnlyList<Scenario> BuildScenarios()
    {
        var forbiddenGridTerms = new[]
        {
            "planning", "semaine", "lundi", "mardi", "mercredi", "jeudi",
            "vendredi", "petit-déjeuner", "déjeuner", "collation", "souper"
        };
        return
        [
            new("p1", "meal-grid-permutation", "J'ai besoin d'un planning de repas pour la semaine du lundi au vendredi incluant petit-déjeuner, déjeuner, collation et souper. Fais un format clair et professionnel, avec uniquement des sources utiles, non dupliquées inutilement. N'invente rien.", forbiddenGridTerms),
            new("p2", "meal-grid-permutation", "Du lundi au vendredi, j'ai besoin d'un planning incluant petit-déjeuner, déjeuner, collation et souper. Utilise uniquement des sources utiles, sans doublon inutile, n'invente rien et présente-le dans un format clair et professionnel.", forbiddenGridTerms),
            new("p3", "meal-grid-permutation", "Incluant petit-déjeuner, déjeuner, collation et souper, compose mon planning de repas du lundi au vendredi. Le format doit être clair et professionnel, les sources uniquement utiles et sans doublon inutile, sans rien inventer.", forbiddenGridTerms),
            new("p4", "meal-grid-permutation", "N'invente rien : compose un planning de repas clair et professionnel du lundi au vendredi, avec petit-déjeuner, déjeuner, collation et souper, uniquement à partir de sources utiles et sans duplication inutile.", forbiddenGridTerms),
            new("p5", "meal-grid-permutation", "Avec uniquement des sources utiles et sans duplication inutile, fais un planning de repas clair et professionnel pour lundi, mardi, mercredi, jeudi et vendredi, comprenant petit-déjeuner, déjeuner, collation et souper. N'invente rien.", forbiddenGridTerms),
            new("non-cuisine", "synthetic-scope-grid", "Dans le corpus Maintenance, prépare une grille lundi à vendredi avec une opération de maintenance documentée et une preuve par jour, sans rien inventer.", new[] { "lundi", "mardi", "mercredi", "jeudi", "vendredi" }, "Maintenance"),
            new("simple", "single-instance", "Dans Cuisine, trouve une recette documentée de ratatouille et cite sa source.", Array.Empty<string>()),
            new("named-document", "named-document", "Ouvre le document FIT-PTFE_TF_1620-EN.pdf et résume les informations techniques qu'il contient, avec ses pages sources.", Array.Empty<string>()),
            new("compound-cuisine", "cumulative-facets", "Pour les croquettes de poulet NEFF, quels sont les ingrédients et le réglage ?", Array.Empty<string>()),
            new("compound-noncuisine", "cumulative-facets-heldout", "Dans ANSI B11.0-2023 - Safety of Machinery, retrouve les passages qui parlent de IEC 60204-1 et explique ce qu’ils imposent ou recommandent.", Array.Empty<string>()),
            new("ambiguous", "clarification", "Prépare-moi le planning à partir des documents.", Array.Empty<string>()),
            new("ambiguous-nongrid", "explicit-source-ambiguity", "Dans les documents, compare soit uniquement les exigences obligatoires, soit aussi les recommandations, mais demande-moi d'abord laquelle des deux portées je veux.", Array.Empty<string>()),
            new("operational-inventory", "operational-control", "Combien de documents sont indexés ?", Array.Empty<string>())
        ];
    }

    private static bool EvaluateScopeContinuity(
        RouterPlan.SourceBackedMissionPlan? mission,
        JsonElement? arguments)
    {
        if (mission?.CandidateScopePaths is not { Count: > 0 })
            return true;
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
            return false;
        var categoryPath = ReadPropertyString(value, "categoryPath")
                           ?? ReadPropertyString(value, "path");
        return !string.IsNullOrWhiteSpace(categoryPath)
               && mission.CandidateScopePaths.Contains(
                   categoryPath,
                   StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateProtectedArguments(
        JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
            yield break;
        foreach (var propertyName in new[] { "query", "q", "docRef", "docPath" })
        {
            if (!value.TryGetProperty(propertyName, out var property))
                continue;
            if (property.ValueKind == JsonValueKind.String)
                yield return property.GetString() ?? string.Empty;
            else if (property.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        yield return item.GetString() ?? string.Empty;
                }
            }
        }
    }

    private static bool ContainsTerm(string value, string term)
        => !string.IsNullOrWhiteSpace(value)
           && !string.IsNullOrWhiteSpace(term)
           && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>
        BuildArgumentTermProvenance(
            IReadOnlyList<string> argumentValues,
            string question,
            string mission,
            string columnRoles,
            string candidateDefinition)
    {
        var sourceTokens = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal)
        {
            ["question"] = Tokenize(question),
            ["router_mission"] = Tokenize(mission),
            ["column_roles"] = Tokenize(columnRoles),
            ["candidate_definition"] = Tokenize(candidateDefinition)
        };
        var result = new SortedDictionary<string, IReadOnlyList<string>>(
            StringComparer.Ordinal);
        foreach (var token in argumentValues
                     .SelectMany(Tokenize)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(static token => token, StringComparer.Ordinal))
        {
            var sources = sourceTokens
                .Where(pair => pair.Value.Contains(token))
                .Select(static pair => pair.Key)
                .ToArray();
            result[token] = sources.Length == 0 ? new[] { "unknown" } : sources;
        }
        return result;
    }

    private static HashSet<string> Tokenize(string? value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
            return tokens;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character)
                != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }
        foreach (Match match in Regex.Matches(
                     builder.ToString().Normalize(NormalizationForm.FormC),
                     @"[\p{L}\p{N}]+",
                     RegexOptions.CultureInvariant))
        {
            if (match.Value.Length >= 2)
                tokens.Add(match.Value);
        }
        return tokens;
    }

    private static readonly IReadOnlySet<string> ArgumentStopWords =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "au", "aux", "avec", "dans", "de", "des", "du", "en",
            "et", "la", "le", "les", "ou", "par", "pour", "sur", "un", "une"
        };

    private static string? ReadJsonString(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadPropertyString(document.RootElement, propertyName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadPropertyString(
        JsonElement value,
        string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();

    private static string ResolveArtifactPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(
            "SAAIA_PHASE1_FIRST_ACTION_ARTIFACT");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
               && !Directory.Exists(Path.Combine(current.FullName, ".git")))
        {
            current = current.Parent;
        }
        if (current is null)
            throw new InvalidOperationException("Could not locate the repository root.");
        return Path.Combine(
            current.FullName,
            "artifacts",
            "live-source-backed-intake-first-action-"
            + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            "report.json");
    }

    private sealed class TimedLlmAdapter(
        OpenAiLlmClient inner,
        double temperature,
        int configuredMaxTokens,
        Stopwatch total) :
        ILlmClient,
        ISourceBackedAgentLlmClient,
        ISourceBackedAgentStructuredLlmClient,
        ISourceBackedAgentInputTokenCounter,
        ISourceBackedAgentRuntimeContextProvider
    {
        private readonly object _gate = new();
        private string _phase = "setup";
        public bool SupportsStructuredOutput => true;
        public List<LlmCallObservation> Calls { get; } = new();

        public void SetPhase(string phase)
        {
            lock (_gate)
                _phase = phase;
        }

        public async Task<string> CompleteAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            CancellationToken ct)
        {
            var list = messages?.ToList()
                       ?? new List<(string role, string content)>();
            if (forceJson)
                list.Insert(0, ("system", "Return ONLY valid JSON. No markdown. No extra text."));
            var joined = string.Join('\n', list.Select(static message => message.content ?? string.Empty));
            var maxTokens = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
                configuredMaxTokens,
                forceJson,
                joined);
            var visible = SourceBackedLlmPromptSanitizer.RemoveControlMetadata(list);
            var started = total.ElapsedMilliseconds;
            var timer = Stopwatch.StartNew();
            var content = await inner.ChatOnceAsync(
                visible,
                temperature,
                maxTokens,
                ct,
                forceJson);
            Add(new LlmCallObservation(
                NextIndex(), "router-chat", CurrentPhase(), null, started,
                timer.ElapsedMilliseconds, maxTokens, joined.Length,
                content.Length, null, null, "unknown", content,
                Array.Empty<ToolCallObservation>()));
            return content;
        }

        public async Task<string> CompleteStructuredAsync(
            IReadOnlyList<(string role, string content)> messages,
            LlmStructuredOutputContract contract,
            CancellationToken ct)
        {
            var list = messages?.ToList()
                       ?? new List<(string role, string content)>();
            list.Insert(0, ("system", "Return ONLY JSON matching the supplied schema. No markdown or extra text."));
            var joined = string.Join('\n', list.Select(static message => message.content ?? string.Empty));
            var maxTokens = RagChatAgent.ResolveLlmAdapterMaxTokensForTests(
                configuredMaxTokens,
                forceJson: true,
                joined);
            var visible = SourceBackedLlmPromptSanitizer.RemoveControlMetadata(list);
            var started = total.ElapsedMilliseconds;
            var timer = Stopwatch.StartNew();
            var content = await inner.ChatOnceStructuredAsync(
                visible,
                temperature,
                maxTokens,
                contract,
                ct);
            Add(new LlmCallObservation(
                NextIndex(), "router-structured", CurrentPhase(), contract.Name,
                started, timer.ElapsedMilliseconds, maxTokens, joined.Length,
                content.Length, null, null, "unknown", content,
                Array.Empty<ToolCallObservation>()));
            return content;
        }

        public async Task StreamAsync(
            IReadOnlyList<(string role, string content)> messages,
            bool forceJson,
            Action<string> onDelta,
            CancellationToken ct)
        {
            var content = await CompleteAsync(messages, forceJson, ct);
            onDelta(content);
        }

        public async Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
        {
            var started = total.ElapsedMilliseconds;
            var timer = Stopwatch.StartNew();
            var completion = await inner.ChatOnceNativeAsync(
                messages,
                tools,
                temperatureOverride ?? temperature,
                Math.Clamp(maxTokens, 64, 4096),
                ct,
                requireToolCall);
            Add(new LlmCallObservation(
                NextIndex(),
                CurrentPhase() == "router" ? "router-native" : "runner-native",
                CurrentPhase() == "router" ? "router" : InferNativeStep(tools),
                null,
                started, timer.ElapsedMilliseconds, maxTokens,
                messages.Sum(static message => message.Content?.Length ?? 0),
                completion.Content?.Length ?? 0,
                completion.PromptTokens,
                completion.CompletionTokens,
                completion.FinishReason,
                completion.Content ?? string.Empty,
                completion.ToolCalls.Select(static call =>
                    new ToolCallObservation(
                        call.Name,
                        call.Arguments,
                        call.ArgumentError)).ToArray()));
            return completion;
        }

        async Task<SourceBackedAgentCompletion>
            ISourceBackedAgentStructuredLlmClient.CompleteStructuredAsync(
                IReadOnlyList<SourceBackedAgentMessage> messages,
                LlmStructuredOutputContract contract,
                int maxTokens,
                CancellationToken ct,
                double? temperatureOverride)
        {
            var visible = SourceBackedLlmPromptSanitizer.RemoveControlMetadata(
                messages.Select(static message =>
                    (message.Role, message.Content ?? string.Empty)).ToArray());
            var started = total.ElapsedMilliseconds;
            var timer = Stopwatch.StartNew();
            var content = await inner.ChatOnceStructuredAsync(
                visible,
                temperatureOverride ?? temperature,
                Math.Clamp(maxTokens, 64, 4096),
                contract,
                ct);
            Add(new LlmCallObservation(
                NextIndex(), "runner-structured", contract.Name, contract.Name,
                started, timer.ElapsedMilliseconds, maxTokens,
                visible.Sum(static message => message.content?.Length ?? 0),
                content.Length, null, null, "stop", content,
                Array.Empty<ToolCallObservation>()));
            return new SourceBackedAgentCompletion(
                content,
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop");
        }

        public Task<int?> CountInputTokensAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            CancellationToken ct,
            bool requireToolCall = false)
            => inner.CountNativeInputTokensAsync(
                messages,
                tools,
                ct,
                requireToolCall);

        public Task<int?> GetRuntimeContextTokensAsync(CancellationToken ct)
            => inner.GetNativeRuntimeContextTokensAsync(ct);

        private int NextIndex()
        {
            lock (_gate)
                return Calls.Count + 1;
        }

        private string CurrentPhase()
        {
            lock (_gate)
                return _phase;
        }

        private void Add(LlmCallObservation observation)
        {
            lock (_gate)
                Calls.Add(observation);
        }

        private static string InferNativeStep(
            IReadOnlyList<SourceBackedAgentToolDefinition> tools)
        {
            var names = tools.Select(static tool => tool.Name).ToArray();
            if (names.Contains(
                    "submit_column_semantics",
                    StringComparer.OrdinalIgnoreCase))
                return "semantic-column-roles";
            if (names.Contains(
                    "submit_initial_observation_decision",
                    StringComparer.OrdinalIgnoreCase))
                return "initial-observation";
            return "runner-native-decision";
        }
    }

    private sealed class FirstToolBoundaryExecutor(
        Stopwatch total,
        ApiClient? observationApi) :
        ISourceBackedAgentToolExecutor
    {
        public string? ToolName { get; private set; }
        public JsonElement? Arguments { get; private set; }
        public long? ElapsedMilliseconds { get; private set; }
        public long? BackendElapsedMilliseconds { get; private set; }
        public long? ObservationElapsedMilliseconds { get; private set; }
        public int? ObservationEvidenceCount { get; private set; }
        public int? ObservationDistinctDocumentCount { get; private set; }
        public IReadOnlyList<FirstObservationSample> ObservationSamples
        {
            get;
            private set;
        } = Array.Empty<FirstObservationSample>();

        public async Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            ToolName = toolName;
            Arguments = arguments.Clone();
            ElapsedMilliseconds = total.ElapsedMilliseconds;
            if (observationApi is null)
                throw new FirstToolBoundaryReachedException();
            if (!string.Equals(
                    toolName,
                    "documents.content_cards",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Integrated first-observation mode only supports documents.content_cards; received "
                    + toolName
                    + ".");
            }

            var backendTimer = Stopwatch.StartNew();
            var raw = await observationApi.DocumentsContentCardsAsync(
                ReadPropertyString(arguments, "categoryPath"),
                ReadPropertyString(arguments, "categoryRef"),
                ReadPropertyString(arguments, "docId"),
                ReadPropertyString(arguments, "docPath"),
                ReadPropertyString(arguments, "q"),
                ReadPropertyString(arguments, "inventoryMode"),
                ReadInteger(arguments, "limit", 60),
                ReadInteger(arguments, "offset", 0),
                ct);
            backendTimer.Stop();
            BackendElapsedMilliseconds = backendTimer.ElapsedMilliseconds;

            var toolResults = new ToolResults();
            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = "documents.content_cards",
                Result = raw.Clone(),
                DurationMs = backendTimer.ElapsedMilliseconds
            });
            var bundle = EvidenceBundleBuilder.FromToolResults(
                toolResults,
                intake.UserQuestion);
            ObservationEvidenceCount = bundle.Items.Count;
            ObservationDistinctDocumentCount = bundle.Items
                .Select(static item => item.DocPath)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            ObservationSamples = ReadObservationSamples(raw);
            ObservationElapsedMilliseconds = total.ElapsedMilliseconds;
            throw new FirstToolBoundaryReachedException();
        }

        private static int ReadInteger(
            JsonElement arguments,
            string propertyName,
            int fallback)
            => arguments.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var parsed)
                ? parsed
                : fallback;

        private static IReadOnlyList<FirstObservationSample>
            ReadObservationSamples(JsonElement raw)
        {
            if (raw.ValueKind != JsonValueKind.Object
                || !raw.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<FirstObservationSample>();
            }

            return items.EnumerateArray()
                .Take(20)
                .Select(static item => new FirstObservationSample(
                    ReadPropertyString(item, "title") ?? string.Empty,
                    ReadPropertyString(item, "docPath") ?? string.Empty,
                    item.TryGetProperty("pageStart", out var pageStart)
                    && pageStart.ValueKind == JsonValueKind.Number
                    && pageStart.TryGetInt32(out var parsedPage)
                        ? parsedPage
                        : null,
                    ReadPropertyString(item, "kind") ?? string.Empty))
                .ToArray();
        }
    }

    private sealed class FirstToolBoundaryReachedException : Exception;

    private sealed record Scenario(
        string Id,
        string Kind,
        string Question,
        IReadOnlyList<string> ForbiddenArgumentTerms,
        string? SyntheticScope = null);

    private sealed record ToolCallObservation(
        string Name,
        JsonElement Arguments,
        string? ArgumentError);

    private sealed record FirstObservationSample(
        string Title,
        string DocPath,
        int? PageStart,
        string Kind);

    private sealed record LlmCallObservation(
        int Index,
        string Channel,
        string Step,
        string? Contract,
        long StartedMilliseconds,
        long ElapsedMilliseconds,
        int MaximumTokens,
        int PromptCharacters,
        int OutputCharacters,
        int? PromptTokens,
        int? CompletionTokens,
        string FinishReason,
        string Content,
        IReadOnlyList<ToolCallObservation> ToolCalls);

    private sealed record ScenarioResult(
        string ScenarioId,
        string ScenarioKind,
        string Question,
        bool ReportCompleted,
        long RouterMilliseconds,
        long RuntimeContextMilliseconds,
        int? RuntimeContextTokens,
        long? QuestionToFirstToolMilliseconds,
        long? FirstObservationBackendMilliseconds,
        long? QuestionToFirstObservationMilliseconds,
        int? FirstObservationEvidenceCount,
        int? FirstObservationDistinctDocumentCount,
        IReadOnlyList<FirstObservationSample> FirstObservationSamples,
        RouterPlan? RouterPlan,
        RouterPlan.SourceBackedMissionPlan? Mission,
        IReadOnlyList<string> CatalogCategories,
        IReadOnlyList<LlmCallObservation> LlmCalls,
        string? FirstTool,
        JsonElement? FirstArguments,
        IReadOnlyList<string> ProtectedArgumentValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ArgumentTermProvenance,
        IReadOnlyList<string> UnrequestedArgumentTerms,
        string? HypotheticalSinglePositionValue,
        IReadOnlyList<string> ForbiddenArgumentHits,
        bool HypotheticalLeak,
        bool ScopeContinuity,
        bool ToolAfterClarification,
        bool UnderLatencyThreshold,
        string? ExecutionError,
        string Verdict);
}
