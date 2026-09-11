using System.Diagnostics;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

/// <summary>
/// EXP-043 shadow ablation of the only S.0 dimension that was correct 8/8.
/// The output is never converted to a route or executable argument.
/// </summary>
public sealed class LiveRouterKnowledgeModeBenchmarkTests(
    ITestOutputHelper output)
{
    private const string EnableVariable =
        "SAAIA_LIVE_ROUTER_KNOWLEDGE_MODE";
    private const string OutputVariable =
        "SAAIA_PHASE1_KNOWLEDGE_MODE_OUTPUT";
    private const string ToolName = "submit_knowledge_mode";
    private const string SystemPrompt = """
        You are SAAIA's semantic knowledge-mode classifier. Never answer and
        call submit_knowledge_mode exactly once. Choose corpus_backed when
        documents can improve the requested facts, instructions, comparison,
        recommendation or plan. Choose operational for social chat, settings,
        inventory, export or diagnostics.
        """;

    [Fact]
    public async Task Knowledge_mode_is_measured_across_three_frozen_orders_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                $"Skipped: set {EnableVariable}=1 to run EXP-043/S.1.");
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
                "EXP-043 requires the local validation LLM URL and model.");
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
        var tool = BuildTool();
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

            var current = observations.Where(observation => string.Equals(
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

        var allThreeOrdersPass = executedOrders.Count == orders.Length
                                 && observations.Count == scenarios.Length
                                 * orders.Length
                                 && observations.All(static observation =>
                                     observation.ProtocolValid
                                     && observation.SemanticPass);
        var stableByScenario = scenarios.All(scenario =>
        {
            var values = observations
                .Where(observation => string.Equals(
                    observation.ScenarioId,
                    scenario.Id,
                    StringComparison.Ordinal))
                .Select(static observation => observation.KnowledgeMode)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return values.Length == 1;
        });
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
        var latencyPass = medianMilliseconds <= 5_000
                          && maximumMilliseconds <= 8_000;
        var verdict = allThreeOrdersPass && stableByScenario && latencyPass
            ? "APPROUVE_COMME_SIGNAL_SHADOW"
            : "REJETE";

        var artifact = ResolveArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(
                new
                {
                    experiment = "EXP-043",
                    variant = "S1-knowledge-mode-isolation",
                    generatedAt = DateTimeOffset.Now,
                    machine = Environment.MachineName,
                    model,
                    llmBaseUrl,
                    maximumOutputTokens = 32,
                    temperature = 0,
                    systemPrompt = SystemPrompt,
                    promptCharacters = SystemPrompt.Length,
                    executedOrders,
                    expectedOrders = orders.Select(static order => order.Name),
                    observationCount = observations.Count,
                    semanticPassCount = observations.Count(static observation =>
                        observation.SemanticPass),
                    protocolPassCount = observations.Count(static observation =>
                        observation.ProtocolValid),
                    stableByScenario,
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
            $"EXP-043: verdict={verdict}, orders={string.Join(",", executedOrders)}, "
            + $"semantic={observations.Count(static item => item.SemanticPass)}/{observations.Count}, "
            + $"protocol={observations.Count(static item => item.ProtocolValid)}/{observations.Count}, "
            + $"median={medianMilliseconds:0} ms, max={maximumMilliseconds} ms");

        Assert.True(
            allThreeOrdersPass,
            "EXP-043 semantic/protocol gate failed; inspect the artifact.");
        Assert.True(
            stableByScenario,
            "EXP-043 order-stability gate failed; inspect the artifact.");
        Assert.True(
            latencyPass,
            "EXP-043 latency gate failed; inspect the artifact.");
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var completion = await llm.ChatOnceNativeAsync(
                new[]
                {
                    SourceBackedAgentMessage.System(SystemPrompt),
                    SourceBackedAgentMessage.User(
                        "USER_MESSAGE:\n" + scenario.Question)
                },
                new[] { tool },
                temperature: 0,
                maxTokens: 32,
                ct: cts.Token,
                requireToolCall: true);
            stopwatch.Stop();

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
            if (knowledgeMode is not ("corpus_backed" or "operational"))
                failureReasons.Add("knowledge_mode_invalid");
            if (!string.Equals(
                    completion.FinishReason,
                    "tool_calls",
                    StringComparison.OrdinalIgnoreCase))
            {
                failureReasons.Add("finish_reason_not_tool_calls");
            }
            if (completion.CompletionTokens is > 32)
                failureReasons.Add("completion_token_budget_exceeded");

            var protocolValid = failureReasons.Count == 0;
            var semanticPass = protocolValid && string.Equals(
                knowledgeMode,
                scenario.ExpectedKnowledgeMode,
                StringComparison.Ordinal);
            return new Observation(
                order,
                scenario.Id,
                scenario.Question,
                stopwatch.ElapsedMilliseconds,
                knowledgeMode,
                scenario.ExpectedKnowledgeMode,
                protocolValid,
                semanticPass,
                string.Join("|", failureReasons),
                semanticPass ? string.Empty : "knowledge_mode_mismatch",
                completion.FinishReason,
                completion.PromptTokens,
                completion.CompletionTokens,
                completion.ServerCacheTokens,
                completion.ServerPromptTokensEvaluated,
                completion.ServerPromptMilliseconds,
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
                scenario.ExpectedKnowledgeMode,
                false,
                false,
                "exception:" + exception.GetType().Name + ":"
                + Truncate(exception.Message, 300),
                "not_evaluated",
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }
    }

    private static SourceBackedAgentToolDefinition BuildTool()
        => new(
            ToolName,
            "Classify only whether the request needs corpus knowledge or an operational path.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    knowledgeMode = new
                    {
                        type = "string",
                        @enum = new[] { "corpus_backed", "operational" }
                    }
                },
                required = new[] { "knowledgeMode" },
                additionalProperties = false
            }, ClientJson.CamelCase));

    private static Scenario[] BuildScenarios()
        =>
        [
            new(
                "compound-cuisine",
                "Pour les croquettes de poulet NEFF, quels sont les ingrédients et le réglage ?",
                "corpus_backed"),
            new(
                "compound-noncuisine",
                "Dans ANSI B11.0-2023 - Safety of Machinery, retrouve les passages qui parlent de IEC 60204-1 et explique ce qu’ils imposent ou recommandent.",
                "corpus_backed"),
            new(
                "ambiguous",
                "Prépare-moi le planning à partir des documents.",
                "corpus_backed"),
            new(
                "grid-cuisine",
                "J'ai besoin d'un planning de repas pour la semaine du lundi au vendredi incluant petit-déjeuner, déjeuner, collation et souper. Fais un format clair et professionnel, avec uniquement des sources utiles, non dupliquées inutilement. N'invente rien.",
                "corpus_backed"),
            new(
                "grid-maintenance",
                "Dans le corpus Maintenance, prépare une grille lundi à vendredi avec une opération de maintenance documentée et une preuve par jour, sans rien inventer.",
                "corpus_backed"),
            new(
                "simple",
                "Dans Cuisine, trouve une recette documentée de ratatouille et cite sa source.",
                "corpus_backed"),
            new(
                "named-document",
                "Ouvre le document FIT-PTFE_TF_1620-EN.pdf et résume les informations techniques qu'il contient, avec ses pages sources.",
                "corpus_backed"),
            new(
                "operational-inventory",
                "Combien de documents sont indexés ?",
                "operational")
        ];

    private static string ReadString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

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
            "exp043-s1-knowledge-mode-isolation.json");
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
        string ExpectedKnowledgeMode);

    private sealed record ScenarioOrder(
        string Name,
        IReadOnlyList<Scenario> Scenarios);

    private sealed record Observation(
        string Order,
        string ScenarioId,
        string Question,
        long ElapsedMilliseconds,
        string KnowledgeMode,
        string ExpectedKnowledgeMode,
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
