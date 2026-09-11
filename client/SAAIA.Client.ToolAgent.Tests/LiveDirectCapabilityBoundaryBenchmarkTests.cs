using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

/// <summary>
/// EXP-046/V.0 shadow measure. The model selects one real read-only capability
/// directly. Nothing is converted to RouterPlan and no backend tool is run.
/// </summary>
public sealed class LiveDirectCapabilityBoundaryBenchmarkTests(
    ITestOutputHelper output)
{
    private const string EnableVariable =
        "SAAIA_LIVE_DIRECT_CAPABILITY_BOUNDARY";
    private const string OutputVariable =
        "SAAIA_PHASE1_DIRECT_CAPABILITY_OUTPUT";
    private const string ExperimentVariable =
        "SAAIA_PHASE1_DIRECT_CAPABILITY_EXPERIMENT";
    private const string VariantVariable =
        "SAAIA_PHASE1_DIRECT_CAPABILITY_VARIANT";
    private const int MaximumOutputTokens = 256;
    private const int MaximumLatencyMilliseconds = 30_000;
    internal const string CountToolName = "documents_count";
    internal const string ClarificationToolName =
        "request_user_clarification";

    internal const string SystemPrompt = """
        You are SAAIA's direct semantic action selector. Never answer. Call
        exactly one available function. Every function is a real read-only
        capability and its arguments must be executable exactly as submitted.

        Preserve every explicit requested facet, constraint, document and
        alternative. Never invent a query term, category, document, axis or
        option. Omit an uncertain scope. Choose the cheapest safe first action.
        Search for source terms that may occur in evidence; navigate or inspect
        context for a named document; enumerate cards or navigation without q
        when unknown values for a structured grid must first be discovered.

        Request clarification only when the user must decide before any safe
        action. Respect an explicit instruction to ask before acting. Do not
        clarify uncertainty that a read-only source observation can resolve.
        """;

    internal static readonly string[] CategoryPaths =
    [
        "Cuisine", "Documents techniques", "Achats - devis - fournisseurs",
        "Assurance - CG - Police", "Audit",
        "Documents scannés  - OCR imparfait", "Catalogue commercial", "CDC",
        "Certifications", "Construction", "Documents avec annexe",
        "Documents avec versions multiples", "Documents contradictoires",
        "Emails - Réunion", "Environnement - Energie",
        "Formations - Education", "Finance - Compta", "Immobilier",
        "Juridique", "Manuels logiciel", "MultiLingues",
        "Support client - FAQ", "Médical", "Normes", "Normes automation",
        "Programmation", "RCA", "RH", "SOP - GMP - Quality",
        "Tableaux complexes", "Voyages"
    ];

    [Fact]
    public void Frozen_boundary_contains_only_the_six_registered_shadow_capabilities()
    {
        var tools = BuildTools();
        Assert.Equal(6, tools.Count);
        Assert.Equal(
            new[]
            {
                "rag_search", "documents_navigation",
                "documents_content_cards", "documents_context",
                CountToolName, ClarificationToolName
            }.OrderBy(static value => value, StringComparer.Ordinal),
            tools.Select(static tool => tool.Name)
                .OrderBy(static value => value, StringComparer.Ordinal));
        Assert.Equal(
            tools.Count,
            tools.Select(static tool => tool.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.All(tools, static tool =>
        {
            Assert.Equal(JsonValueKind.Object, tool.Parameters.ValueKind);
            Assert.True(tool.Parameters.TryGetProperty("type", out var type));
            Assert.Equal("object", type.GetString());
        });
    }

    [Fact]
    public async Task Direct_capability_is_measured_on_the_frozen_matrix_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                $"Skipped: set {EnableVariable}=1 to run EXP-046/V.0.");
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
                "EXP-046 requires the local validation LLM URL and model.");
        }

        var scenarios = BuildScenarios();
        var orders = new[]
        {
            new ScenarioOrder("canonical", scenarios),
            new ScenarioOrder("reverse", scenarios.Reverse().ToArray()),
            new ScenarioOrder(
                "rotate-3",
                scenarios.Skip(3).Concat(scenarios.Take(3)).ToArray())
        };
        var llm = new OpenAiLlmClient();
        llm.Configure(llmBaseUrl, model);
        var tools = BuildTools();
        var observations = new List<Observation>();
        var executedOrders = new List<string>();

        int? inputTokens;
        using (var countCts = new CancellationTokenSource(
                   TimeSpan.FromSeconds(30)))
        {
            inputTokens = await llm.CountNativeInputTokensAsync(
                BuildMessages(scenarios[0]),
                tools,
                countCts.Token,
                requireToolCall: true);
        }

        foreach (var order in orders)
        {
            executedOrders.Add(order.Name);
            foreach (var scenario in order.Scenarios)
            {
                observations.Add(await RunScenarioAsync(
                    llm,
                    tools,
                    order.Name,
                    scenario));
            }

            var current = observations.Where(observation => string.Equals(
                    observation.Order,
                    order.Name,
                    StringComparison.Ordinal))
                .ToArray();
            if (current.Length != scenarios.Length
                || current.Any(static observation =>
                    !observation.ProtocolValid
                    || !observation.SemanticPass
                    || !observation.LatencyPass))
            {
                break;
            }
        }

        var allThreeOrdersPass = executedOrders.Count == orders.Length
                                 && observations.Count == scenarios.Length
                                 * orders.Length
                                 && observations.All(static observation =>
                                     observation.ProtocolValid
                                     && observation.SemanticPass
                                     && observation.LatencyPass);
        var stableByScenario = scenarios.All(scenario =>
        {
            var values = observations
                .Where(observation => string.Equals(
                    observation.ScenarioId,
                    scenario.Id,
                    StringComparison.Ordinal))
                .Select(static observation =>
                    observation.ToolName + "|" + observation.ArgumentsRaw)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return values.Length == 1;
        });
        var latencies = observations
            .Select(static observation => observation.ElapsedMilliseconds)
            .OrderBy(static value => value)
            .ToArray();
        var medianMilliseconds = Median(latencies);
        var maximumMilliseconds = latencies.DefaultIfEmpty(0).Max();
        var totalPromptTokens = observations.Sum(static observation =>
            observation.PromptTokens ?? 0);
        var totalCompletionTokens = observations.Sum(static observation =>
            observation.CompletionTokens ?? 0);
        var verdict = allThreeOrdersPass && stableByScenario
            ? "APPROUVE_POUR_INTEGRATION_EXPERIMENTALE"
            : "REJETE";
        var experiment = FirstNonBlank(
            Environment.GetEnvironmentVariable(ExperimentVariable),
            "EXP-046");
        var variant = FirstNonBlank(
            Environment.GetEnvironmentVariable(VariantVariable),
            "V0-direct-capability-boundary");

        var artifact = ResolveArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(
                new
                {
                    experiment,
                    variant,
                    generatedAt = DateTimeOffset.Now,
                    machine = Environment.MachineName,
                    model,
                    llmBaseUrl,
                    maximumOutputTokens = MaximumOutputTokens,
                    maximumLatencyMilliseconds = MaximumLatencyMilliseconds,
                    samplingEnvironment = new
                    {
                        temperature = Environment.GetEnvironmentVariable(
                            "SAAIA_LLM_TEMPERATURE"),
                        topP = Environment.GetEnvironmentVariable(
                            "SAAIA_LLM_TOP_P"),
                        frequencyPenalty = Environment.GetEnvironmentVariable(
                            "SAAIA_LLM_FREQUENCY_PENALTY"),
                        presencePenalty = Environment.GetEnvironmentVariable(
                            "SAAIA_LLM_PRESENCE_PENALTY"),
                        structuredSampling = Environment.GetEnvironmentVariable(
                            "SAAIA_LLM_STRUCTURED_SAMPLING")
                    },
                    systemPrompt = SystemPrompt,
                    promptCharacters = SystemPrompt.Length,
                    firstScenarioInputTokens = inputTokens,
                    toolNames = tools.Select(static tool => tool.Name),
                    executedOrders,
                    expectedOrders = orders.Select(static order => order.Name),
                    observationCount = observations.Count,
                    semanticPassCount = observations.Count(static observation =>
                        observation.SemanticPass),
                    protocolPassCount = observations.Count(static observation =>
                        observation.ProtocolValid),
                    latencyPassCount = observations.Count(static observation =>
                        observation.LatencyPass),
                    stableByScenario,
                    medianMilliseconds,
                    maximumMilliseconds,
                    totalPromptTokens,
                    totalCompletionTokens,
                    allThreeOrdersPass,
                    backendExecuted = false,
                    routerPlanBuilt = false,
                    semanticMissionBuilt = false,
                    verdict,
                    observations
                },
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifact);
        output.WriteLine(
            $"{experiment}: verdict={verdict}, orders={string.Join(",", executedOrders)}, "
            + $"semantic={observations.Count(static item => item.SemanticPass)}/{observations.Count}, "
            + $"protocol={observations.Count(static item => item.ProtocolValid)}/{observations.Count}, "
            + $"latency={observations.Count(static item => item.LatencyPass)}/{observations.Count}, "
            + $"median={medianMilliseconds:0} ms, max={maximumMilliseconds} ms");

        Assert.True(
            allThreeOrdersPass,
            "EXP-046 semantic/protocol/latency gate failed; inspect artifact.");
        Assert.True(
            stableByScenario,
            "EXP-046 order-stability gate failed; inspect artifact.");
    }

    private static async Task<Observation> RunScenarioAsync(
        OpenAiLlmClient llm,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        string order,
        Scenario scenario)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var completion = await llm.ChatOnceNativeAsync(
                BuildMessages(scenario),
                tools,
                temperature: 0,
                maxTokens: MaximumOutputTokens,
                ct: cts.Token,
                requireToolCall: true);
            stopwatch.Stop();

            var protocolFailures = new List<string>();
            SourceBackedAgentToolCall? call = null;
            if (completion.ToolCalls.Count != 1)
            {
                protocolFailures.Add("single_tool_call_required");
            }
            else
            {
                call = completion.ToolCalls[0];
                if (!tools.Any(tool => string.Equals(
                        tool.Name,
                        call.Name,
                        StringComparison.Ordinal)))
                {
                    protocolFailures.Add("unknown_tool_name");
                }
                if (call.Arguments.ValueKind != JsonValueKind.Object)
                    protocolFailures.Add("arguments_object_required");
                if (!string.IsNullOrWhiteSpace(call.ArgumentError))
                    protocolFailures.Add("argument_error:" + call.ArgumentError);
            }
            if (!string.Equals(
                    completion.FinishReason,
                    "tool_calls",
                    StringComparison.OrdinalIgnoreCase))
            {
                protocolFailures.Add("finish_reason_not_tool_calls");
            }
            if (completion.CompletionTokens is > MaximumOutputTokens)
                protocolFailures.Add("completion_token_budget_exceeded");

            var arguments = call?.Arguments;
            if (call is not null
                && string.Equals(
                    call.Name,
                    ClarificationToolName,
                    StringComparison.Ordinal))
            {
                ValidateClarification(
                    arguments,
                    scenario.Question,
                    protocolFailures);
            }
            if (call is not null
                && string.Equals(
                    call.Name,
                    CountToolName,
                    StringComparison.Ordinal))
            {
                ValidateCountArguments(arguments, protocolFailures);
            }
            if (call is not null)
                ValidateRegisteredCategoryArguments(
                    arguments,
                    protocolFailures);

            var protocolValid = protocolFailures.Count == 0;
            var semanticFailures = protocolValid && call is not null
                ? EvaluateSemanticGate(scenario, call.Name, call.Arguments)
                : new List<string> { "not_evaluated" };
            var latencyPass = stopwatch.ElapsedMilliseconds
                              <= MaximumLatencyMilliseconds;
            return new Observation(
                order,
                scenario.Id,
                scenario.Question,
                stopwatch.ElapsedMilliseconds,
                call?.Name ?? string.Empty,
                arguments?.GetRawText() ?? string.Empty,
                protocolValid,
                semanticFailures.Count == 0,
                latencyPass,
                protocolFailures,
                semanticFailures,
                completion.FinishReason,
                completion.PromptTokens,
                completion.CompletionTokens,
                completion.ServerCacheTokens,
                completion.ServerPromptTokensEvaluated,
                completion.ServerPromptMilliseconds,
                completion.ServerPredictedTokens,
                completion.ServerPredictedMilliseconds);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new Observation(
                order,
                scenario.Id,
                scenario.Question,
                stopwatch.ElapsedMilliseconds,
                string.Empty,
                string.Empty,
                false,
                false,
                stopwatch.ElapsedMilliseconds <= MaximumLatencyMilliseconds,
                new[]
                {
                    "exception:" + exception.GetType().Name + ":"
                    + Truncate(exception.Message, 300)
                },
                new[] { "not_evaluated" },
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }
    }

    private static IReadOnlyList<SourceBackedAgentMessage> BuildMessages(
        Scenario scenario)
        => new[]
        {
            SourceBackedAgentMessage.System(SystemPrompt),
            SourceBackedAgentMessage.User(
                "CATEGORY_PATHS (optional exact values; omit when uncertain):\n"
                + string.Join(" | ", CategoryPaths)
                + "\n\nUSER_MESSAGE:\n"
                + scenario.Question)
        };

    internal static IReadOnlyList<SourceBackedAgentToolDefinition> BuildTools()
    {
        var tools = SourceBackedAgentToolCatalog.Build(
                useConstrainedContextDescriptions: true,
                allowedCategoryPaths: CategoryPaths,
                includeCategoryPathEnums: false)
            .ToList();
        tools.Add(BuildCountTool());
        tools.Add(BuildClarificationTool());
        return tools;
    }

    private static SourceBackedAgentToolDefinition BuildCountTool()
        => new(
            CountToolName,
            "Count indexed documents globally or with an explicit user-provided filter.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    categoryPath = NullableStringSchema(),
                    categoryRef = NullableStringSchema(),
                    q = NullableStringSchema()
                },
                additionalProperties = false
            }));

    private static SourceBackedAgentToolDefinition BuildClarificationTool()
        => new(
            ClarificationToolName,
            "Pause before tools when only the user can resolve a material choice, including when the user explicitly asks to be asked first. This must be the only action.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    understanding = BoundedString(2, 300),
                    options = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                label = BoundedString(1, 180),
                                userTextAnchor = new
                                {
                                    type = new[] { "string", "null" },
                                    minLength = 2,
                                    maxLength = 180
                                }
                            },
                            required = new[] { "label", "userTextAnchor" },
                            additionalProperties = false
                        },
                        minItems = 2,
                        maxItems = 4
                    },
                    executionImpact = BoundedString(2, 240),
                    ambiguityKind = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "goal", "scope", "constraints", "deliverable",
                            "route", "other"
                        }
                    }
                },
                required = new[]
                {
                    "understanding", "options", "executionImpact",
                    "ambiguityKind"
                },
                additionalProperties = false
            }));

    private static object NullableStringSchema()
        => new { type = new[] { "string", "null" } };

    private static object BoundedString(int minimum, int maximum)
        => new
        {
            type = "string",
            minLength = minimum,
            maxLength = maximum
        };

    private static void ValidateClarification(
        JsonElement? arguments,
        string userMessage,
        ICollection<string> failures)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value
            || !TryGetProperty(value, "options", out var options)
            || options.ValueKind != JsonValueKind.Array
            || options.GetArrayLength() is < 2 or > 4)
        {
            failures.Add("clarification_options_invalid");
            return;
        }
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object
                || ReadString(option, "label").Length == 0)
            {
                failures.Add("clarification_option_invalid");
                break;
            }
            var anchor = ReadString(option, "userTextAnchor");
            if (anchor.Length > 0
                && userMessage.IndexOf(
                    anchor,
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                failures.Add(
                    "clarification_anchor_not_in_user_message:" + anchor);
            }
        }
    }

    private static void ValidateRegisteredCategoryArguments(
        JsonElement? arguments,
        ICollection<string> failures)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
            return;
        foreach (var propertyName in new[] { "categoryPath", "path" })
        {
            var submitted = ReadString(value, propertyName);
            if (submitted.Length > 0
                && !CategoryPaths.Contains(
                    submitted,
                    StringComparer.OrdinalIgnoreCase))
            {
                failures.Add(
                    "unregistered_category_scope:"
                    + propertyName
                    + "="
                    + submitted);
            }
        }
    }

    private static void ValidateCountArguments(
        JsonElement? arguments,
        ICollection<string> failures)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
            return;
        var allowed = new HashSet<string>(
            new[] { "categoryPath", "categoryRef", "q" },
            StringComparer.OrdinalIgnoreCase);
        if (value.EnumerateObject().Any(property =>
                !allowed.Contains(property.Name)))
        {
            failures.Add("count_arguments_unknown_property");
        }
    }

    private static List<string> EvaluateSemanticGate(
        Scenario scenario,
        string toolName,
        JsonElement arguments)
    {
        var failures = new List<string>();
        var raw = arguments.GetRawText();
        switch (scenario.Id)
        {
            case "compound-cuisine":
                RequireTool(toolName, "rag_search", failures);
                RequireTerms(raw, failures, "neff", "ingredient", "reglage");
                RequireScope(arguments, failures, "Cuisine");
                break;
            case "compound-noncuisine":
                RequireOneTool(
                    toolName,
                    failures,
                    "documents_context",
                    "documents_navigation",
                    "rag_search");
                RequireTerms(raw, failures, "ansi", "iec 60204-1");
                RequireScope(arguments, failures, "Normes");
                break;
            case "ambiguous":
                RequireTool(toolName, ClarificationToolName, failures);
                break;
            case "ambiguous-nongrid":
                RequireTool(toolName, ClarificationToolName, failures);
                RequireTerms(raw, failures, "obligatoire", "recommandation");
                break;
            case "operational-inventory":
                RequireTool(toolName, CountToolName, failures);
                break;
            case "simple":
                RequireTool(toolName, "rag_search", failures);
                RequireTerms(raw, failures, "ratatouille");
                RequireScope(arguments, failures, "Cuisine");
                break;
            case "named-document":
                RequireOneTool(
                    toolName,
                    failures,
                    "documents_navigation",
                    "documents_context");
                RequireTerms(raw, failures, "fit-ptfe_tf_1620-en.pdf");
                break;
            case "p1":
                RequireOneTool(
                    toolName,
                    failures,
                    "documents_content_cards",
                    "documents_navigation");
                RequireScope(arguments, failures, "Cuisine");
                var query = FirstNonBlank(
                    ReadString(arguments, "q"),
                    ReadString(arguments, "query"));
                if (!string.IsNullOrWhiteSpace(query))
                    failures.Add("p1_query_must_be_empty");
                break;
            default:
                failures.Add("unknown_scenario");
                break;
        }
        return failures;
    }

    private static void RequireTool(
        string actual,
        string expected,
        ICollection<string> failures)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            failures.Add($"tool_mismatch:{actual}!={expected}");
    }

    private static void RequireOneTool(
        string actual,
        ICollection<string> failures,
        params string[] expected)
    {
        if (!expected.Contains(actual, StringComparer.Ordinal))
            failures.Add("tool_mismatch:" + actual + "!="
                         + string.Join("|", expected));
    }

    private static void RequireTerms(
        string value,
        ICollection<string> failures,
        params string[] requiredTerms)
    {
        var normalized = Normalize(value);
        foreach (var term in requiredTerms)
        {
            if (!normalized.Contains(Normalize(term), StringComparison.Ordinal))
                failures.Add("missing_term:" + term);
        }
    }

    private static void RequireScope(
        JsonElement arguments,
        ICollection<string> failures,
        string allowedScope)
    {
        var scope = ReadString(arguments, "categoryPath");
        if (scope.Length > 0
            && !string.Equals(
                scope,
                allowedScope,
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("scope_mismatch:" + scope + "!=" + allowedScope);
        }
    }

    private static bool TryGetProperty(
        JsonElement root,
        string propertyName,
        out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string ReadString(JsonElement root, string propertyName)
        => TryGetProperty(root, propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string Normalize(string value)
    {
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
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static Scenario[] BuildScenarios()
        =>
        [
            new(
                "compound-cuisine",
                "Pour les croquettes de poulet NEFF, quels sont les ingrédients et le réglage ?"),
            new(
                "compound-noncuisine",
                "Dans ANSI B11.0-2023 - Safety of Machinery, retrouve les passages qui parlent de IEC 60204-1 et explique ce qu’ils imposent ou recommandent."),
            new(
                "ambiguous",
                "Prépare-moi le planning à partir des documents."),
            new(
                "ambiguous-nongrid",
                "Dans les documents, compare soit uniquement les exigences obligatoires, soit aussi les recommandations, mais demande-moi d'abord laquelle des deux portées je veux."),
            new(
                "operational-inventory",
                "Combien de documents sont indexés ?"),
            new(
                "simple",
                "Dans Cuisine, trouve une recette documentée de ratatouille et cite sa source."),
            new(
                "named-document",
                "Ouvre le document FIT-PTFE_TF_1620-EN.pdf et résume les informations techniques qu'il contient, avec ses pages sources."),
            new(
                "p1",
                "J'ai besoin d'un planning de repas pour la semaine du lundi au vendredi incluant petit-déjeuner, déjeuner, collation et souper. Fais un format clair et professionnel, avec uniquement des sources utiles, non dupliquées inutilement. N'invente rien.")
        ];

    private static double Median(IReadOnlyList<long> sortedValues)
        => sortedValues.Count == 0
            ? 0
            : sortedValues.Count % 2 == 1
                ? sortedValues[sortedValues.Count / 2]
                : (sortedValues[(sortedValues.Count / 2) - 1]
                   + sortedValues[sortedValues.Count / 2]) / 2.0;

    private static string ResolveArtifactPath()
    {
        var configured = Environment.GetEnvironmentVariable(OutputVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
               && !Directory.Exists(Path.Combine(current.FullName, ".git")))
        {
            current = current.Parent;
        }
        if (current is null)
            throw new InvalidOperationException("Could not locate repository root.");
        return Path.Combine(
            current.FullName,
            "artifacts",
            "goal-rag-end-to-end-20260826-233646",
            "phase1",
            "exp046-v0-direct-capability-boundary.json");
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value =>
            !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string Truncate(string value, int maximumCharacters)
        => value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters];

    internal sealed record Scenario(string Id, string Question);

    private sealed record ScenarioOrder(
        string Name,
        IReadOnlyList<Scenario> Scenarios);

    private sealed record Observation(
        string Order,
        string ScenarioId,
        string Question,
        long ElapsedMilliseconds,
        string ToolName,
        string ArgumentsRaw,
        bool ProtocolValid,
        bool SemanticPass,
        bool LatencyPass,
        IReadOnlyList<string> ProtocolFailures,
        IReadOnlyList<string> SemanticFailures,
        string? FinishReason,
        int? PromptTokens,
        int? CompletionTokens,
        int? ServerCacheTokens,
        int? ServerPromptTokensEvaluated,
        double? ServerPromptMilliseconds,
        int? ServerPredictedTokens,
        double? ServerPredictedMilliseconds);
}
