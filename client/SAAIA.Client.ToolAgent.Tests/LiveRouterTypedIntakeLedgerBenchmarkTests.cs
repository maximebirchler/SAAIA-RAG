using System.Diagnostics;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

/// <summary>
/// EXP-042 shadow-only intake ablation. It never calls the product router or a
/// backend tool and never converts the model output into executable arguments.
/// </summary>
public sealed class LiveRouterTypedIntakeLedgerBenchmarkTests(
    ITestOutputHelper output)
{
    private const string EnableVariable =
        "SAAIA_LIVE_ROUTER_TYPED_INTAKE_LEDGER";
    private const string OutputVariable =
        "SAAIA_PHASE1_TYPED_INTAKE_LEDGER_OUTPUT";
    private const string ToolName = "submit_intake_ledger";

    private const string SystemPrompt = """
        You are SAAIA's semantic intake analyst. Never answer and never choose
        or fill a retrieval tool. Call submit_intake_ledger exactly once.

        knowledgeMode is corpus_backed when documents can improve the requested
        facts, instructions, comparison, recommendation or plan; otherwise it
        is operational for social chat, settings, inventory, export or
        diagnostics. layoutMode is grid only when the user specified repeated
        row-by-column positions, non_grid for another defined deliverable, and
        undetermined when its form is not yet specified.

        actionability is actionable_now when the request contains enough user
        decisions to start its route. Use observe_then_maybe_clarify when a safe
        corpus observation can narrow a remaining uncertainty. Use
        user_input_before_any_action only when no safe useful action can start
        before the user supplies a missing decision.

        explicitRequirementSpans copies every explicit requested outcome or
        constraint as the smallest exact, complete span from USER_MESSAGE.
        Never paraphrase a span and never invent an alternative. missingDecision
        is null when actionable_now; otherwise state only the unresolved user
        decision.
        """;

    [Fact]
    public async Task Typed_intake_ledger_is_measured_on_frozen_cross_domain_matrix_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                $"Skipped: set {EnableVariable}=1 to run EXP-042/S.0.");
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
                "EXP-042 requires the local validation LLM URL and model.");
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
        var tool = BuildTool();
        var llm = new OpenAiLlmClient();
        llm.Configure(llmBaseUrl, model);
        var observations = new List<Observation>();
        var executedOrders = new List<string>();

        foreach (var order in orders)
        {
            executedOrders.Add(order.Name);
            foreach (var scenario in order.Scenarios)
            {
                observations.Add(await RunScenarioAsync(
                    llm,
                    tool,
                    order.Name,
                    scenario));
            }

            var current = observations
                .Where(observation => string.Equals(
                    observation.Order,
                    order.Name,
                    StringComparison.Ordinal))
                .ToArray();
            if (current.Length != scenarios.Length
                || current.Any(static observation =>
                    !observation.ProtocolValid || !observation.SemanticPass))
            {
                break;
            }
        }

        var completeOrders = executedOrders
            .Where(orderName => observations.Count(observation =>
                string.Equals(
                    observation.Order,
                    orderName,
                    StringComparison.Ordinal)) == scenarios.Length)
            .ToArray();
        var allExecutedPass = observations.Count > 0
                              && observations.All(static observation =>
                                  observation.ProtocolValid
                                  && observation.SemanticPass);
        var allThreeOrdersPass = completeOrders.Length == orders.Length
                                 && allExecutedPass;
        var latencies = observations
            .Select(static observation => observation.ElapsedMilliseconds)
            .OrderBy(static value => value)
            .ToArray();
        var medianMilliseconds = latencies.Length == 0
            ? 0
            : latencies.Length % 2 == 1
                ? latencies[latencies.Length / 2]
                : (latencies[(latencies.Length / 2) - 1]
                   + latencies[latencies.Length / 2]) / 2.0;
        var maximumMilliseconds = latencies.DefaultIfEmpty(0).Max();
        var latencyPass = medianMilliseconds <= 10_000
                          && maximumMilliseconds <= 15_000;
        var verdict = allThreeOrdersPass && latencyPass
            ? "APPROUVE_COMME_BRIQUE_SHADOW"
            : "REJETE";

        var artifact = ResolveArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(
                new
                {
                    experiment = "EXP-042",
                    variant = "S0-typed-intake-ledger-shadow",
                    generatedAt = DateTimeOffset.Now,
                    machine = Environment.MachineName,
                    model,
                    llmBaseUrl,
                    maximumOutputTokens = 192,
                    temperature = 0,
                    systemPrompt = SystemPrompt,
                    promptCharacters = SystemPrompt.Length,
                    executedOrders,
                    completeOrders,
                    expectedOrders = orders.Select(static order => order.Name),
                    observationCount = observations.Count,
                    semanticPassCount = observations.Count(static observation =>
                        observation.SemanticPass),
                    protocolPassCount = observations.Count(static observation =>
                        observation.ProtocolValid),
                    medianMilliseconds,
                    maximumMilliseconds,
                    latencyPass,
                    allThreeOrdersPass,
                    verdict,
                    observations
                },
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifact);
        output.WriteLine(
            $"EXP-042: verdict={verdict}, orders={string.Join(",", executedOrders)}, "
            + $"semantic={observations.Count(static item => item.SemanticPass)}/{observations.Count}, "
            + $"protocol={observations.Count(static item => item.ProtocolValid)}/{observations.Count}, "
            + $"median={medianMilliseconds:0} ms, max={maximumMilliseconds} ms");

        Assert.True(
            allThreeOrdersPass,
            "EXP-042 semantic/protocol gate failed; inspect the artifact.");
        Assert.True(
            latencyPass,
            "EXP-042 latency gate failed; inspect the artifact.");
    }

    private static async Task<Observation> RunScenarioAsync(
        OpenAiLlmClient llm,
        SourceBackedAgentToolDefinition tool,
        string order,
        Scenario scenario)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var completion = await llm.ChatOnceNativeAsync(
                new[]
                {
                    SourceBackedAgentMessage.System(SystemPrompt),
                    SourceBackedAgentMessage.User(
                        "USER_MESSAGE:\n" + scenario.Question)
                },
                new[] { tool },
                temperature: 0,
                maxTokens: 192,
                ct: cts.Token,
                requireToolCall: true);
            stopwatch.Stop();
            return ParseObservation(
                order,
                scenario,
                completion,
                stopwatch.ElapsedMilliseconds);
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
                string.Empty,
                Array.Empty<string>(),
                null,
                false,
                false,
                "exception:" + exception.GetType().Name + ":"
                + Truncate(exception.Message, 300),
                string.Empty,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }
    }

    private static Observation ParseObservation(
        string order,
        Scenario scenario,
        SourceBackedAgentCompletion completion,
        long elapsedMilliseconds)
    {
        var failureReasons = new List<string>();
        JsonElement arguments = default;
        if (completion.ToolCalls.Count != 1)
        {
            failureReasons.Add("single_tool_call_required");
        }
        else
        {
            var call = completion.ToolCalls[0];
            if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal))
                failureReasons.Add("unexpected_tool_name");
            arguments = call.Arguments;
            if (arguments.ValueKind != JsonValueKind.Object)
                failureReasons.Add("arguments_object_required");
        }

        var knowledgeMode = ReadString(arguments, "knowledgeMode");
        var layoutMode = ReadString(arguments, "layoutMode");
        var actionability = ReadString(arguments, "actionability");
        var missingDecision = ReadNullableString(arguments, "missingDecision");
        var spans = ReadStringArray(
            arguments,
            "explicitRequirementSpans",
            out var spansArrayValid);
        if (knowledgeMode is not ("corpus_backed" or "operational"))
            failureReasons.Add("knowledge_mode_invalid");
        if (layoutMode is not ("grid" or "non_grid" or "undetermined"))
            failureReasons.Add("layout_mode_invalid");
        if (actionability is not (
                "actionable_now"
                or "observe_then_maybe_clarify"
                or "user_input_before_any_action"))
            failureReasons.Add("actionability_invalid");
        if (!spansArrayValid
            || spans.Count > 8
            || spans.Any(static span => span.Length is < 1 or > 180)
            || spans.Distinct(StringComparer.Ordinal).Count() != spans.Count)
        {
            failureReasons.Add("explicit_spans_invalid");
        }
        if (spans.Any(span => !scenario.Question.Contains(
                span,
                StringComparison.Ordinal)))
        {
            failureReasons.Add("explicit_span_not_exact_substring");
        }
        if (string.Equals(
                actionability,
                "actionable_now",
                StringComparison.Ordinal)
            ? !string.IsNullOrWhiteSpace(missingDecision)
            : string.IsNullOrWhiteSpace(missingDecision))
        {
            failureReasons.Add("missing_decision_inconsistent");
        }
        if (!string.Equals(
                completion.FinishReason,
                "tool_calls",
                StringComparison.OrdinalIgnoreCase))
        {
            failureReasons.Add("finish_reason_not_tool_calls");
        }

        var semanticReasons = new List<string>();
        if (!string.Equals(
                knowledgeMode,
                scenario.ExpectedKnowledgeMode,
                StringComparison.Ordinal))
        {
            semanticReasons.Add("knowledge_mode_mismatch");
        }
        if (!string.Equals(
                layoutMode,
                scenario.ExpectedLayoutMode,
                StringComparison.Ordinal))
        {
            semanticReasons.Add("layout_mode_mismatch");
        }
        if (!string.Equals(
                actionability,
                scenario.ExpectedActionability,
                StringComparison.Ordinal))
        {
            semanticReasons.Add("actionability_mismatch");
        }
        foreach (var anchor in scenario.RequiredSpanAnchors)
        {
            if (!spans.Any(span => span.Contains(
                    anchor,
                    StringComparison.OrdinalIgnoreCase)))
            {
                semanticReasons.Add("required_span_missing:" + anchor);
            }
        }

        var protocolValid = failureReasons.Count == 0;
        var semanticPass = protocolValid && semanticReasons.Count == 0;
        return new Observation(
            order,
            scenario.Id,
            scenario.Question,
            elapsedMilliseconds,
            knowledgeMode,
            layoutMode,
            actionability,
            spans,
            missingDecision,
            protocolValid,
            semanticPass,
            string.Join("|", failureReasons),
            string.Join("|", semanticReasons),
            completion.FinishReason,
            completion.PromptTokens,
            completion.CompletionTokens,
            completion.ServerCacheTokens,
            completion.ServerPromptTokensEvaluated,
            completion.ServerPromptMilliseconds,
            completion.ServerPredictedMilliseconds);
    }

    private static SourceBackedAgentToolDefinition BuildTool()
        => new(
            ToolName,
            "Record independent intake dimensions only. Do not answer and do not produce executable route or retrieval arguments.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    knowledgeMode = new
                    {
                        type = "string",
                        @enum = new[] { "corpus_backed", "operational" }
                    },
                    layoutMode = new
                    {
                        type = "string",
                        @enum = new[] { "grid", "non_grid", "undetermined" }
                    },
                    actionability = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "actionable_now",
                            "observe_then_maybe_clarify",
                            "user_input_before_any_action"
                        }
                    },
                    explicitRequirementSpans = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 180
                        },
                        minItems = 0,
                        maxItems = 8
                    },
                    missingDecision = new
                    {
                        type = new[] { "string", "null" },
                        minLength = 2,
                        maxLength = 180
                    }
                },
                required = new[]
                {
                    "knowledgeMode", "layoutMode", "actionability",
                    "explicitRequirementSpans", "missingDecision"
                },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private static Scenario[] BuildScenarios()
        =>
        [
            new(
                "compound-cuisine",
                "Pour les croquettes de poulet NEFF, quels sont les ingrédients et le réglage ?",
                "corpus_backed",
                "non_grid",
                "actionable_now",
                ["ingrédients", "réglage"]),
            new(
                "compound-noncuisine",
                "Dans ANSI B11.0-2023 - Safety of Machinery, retrouve les passages qui parlent de IEC 60204-1 et explique ce qu’ils imposent ou recommandent.",
                "corpus_backed",
                "non_grid",
                "actionable_now",
                ["retrouve", "explique"]),
            new(
                "ambiguous",
                "Prépare-moi le planning à partir des documents.",
                "corpus_backed",
                "undetermined",
                "user_input_before_any_action",
                []),
            new(
                "grid-cuisine",
                "J'ai besoin d'un planning de repas pour la semaine du lundi au vendredi incluant petit-déjeuner, déjeuner, collation et souper. Fais un format clair et professionnel, avec uniquement des sources utiles, non dupliquées inutilement. N'invente rien.",
                "corpus_backed",
                "grid",
                "actionable_now",
                ["lundi", "vendredi", "petit-déjeuner", "souper"]),
            new(
                "grid-maintenance",
                "Dans le corpus Maintenance, prépare une grille lundi à vendredi avec une opération de maintenance documentée et une preuve par jour, sans rien inventer.",
                "corpus_backed",
                "grid",
                "actionable_now",
                ["lundi", "vendredi", "opération de maintenance"]),
            new(
                "simple",
                "Dans Cuisine, trouve une recette documentée de ratatouille et cite sa source.",
                "corpus_backed",
                "non_grid",
                "actionable_now",
                ["ratatouille"]),
            new(
                "named-document",
                "Ouvre le document FIT-PTFE_TF_1620-EN.pdf et résume les informations techniques qu'il contient, avec ses pages sources.",
                "corpus_backed",
                "non_grid",
                "actionable_now",
                ["FIT-PTFE_TF_1620-EN.pdf"]),
            new(
                "operational-inventory",
                "Combien de documents sont indexés ?",
                "operational",
                "undetermined",
                "actionable_now",
                ["Combien de documents"])
        ];

    private static string ReadString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string? ReadNullableString(
        JsonElement root,
        string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement root,
        string propertyName,
        out bool valid)
    {
        JsonElement value = default;
        valid = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(propertyName, out value)
                && value.ValueKind == JsonValueKind.Array;
        if (!valid)
            return Array.Empty<string>();
        var values = value.EnumerateArray().ToArray();
        valid = values.All(static item => item.ValueKind == JsonValueKind.String);
        return valid
            ? values.Select(static item => item.GetString()?.Trim() ?? string.Empty)
                .ToArray()
            : Array.Empty<string>();
    }

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
            "exp042-s0-typed-intake-ledger.json");
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value =>
            !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string Truncate(string value, int maximumCharacters)
        => value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters];

    private sealed record Scenario(
        string Id,
        string Question,
        string ExpectedKnowledgeMode,
        string ExpectedLayoutMode,
        string ExpectedActionability,
        IReadOnlyList<string> RequiredSpanAnchors);

    private sealed record ScenarioOrder(
        string Name,
        IReadOnlyList<Scenario> Scenarios);

    private sealed record Observation(
        string Order,
        string ScenarioId,
        string Question,
        long ElapsedMilliseconds,
        string KnowledgeMode,
        string LayoutMode,
        string Actionability,
        IReadOnlyList<string> ExplicitRequirementSpans,
        string? MissingDecision,
        bool ProtocolValid,
        bool SemanticPass,
        string ProtocolFailure,
        string SemanticFailure,
        string? FinishReason,
        int? PromptTokens,
        int? CompletionTokens,
        int? ServerCacheTokens,
        int? ServerPromptTokensEvaluated,
        double? ServerPromptMilliseconds,
        double? ServerPredictedMilliseconds);
}
