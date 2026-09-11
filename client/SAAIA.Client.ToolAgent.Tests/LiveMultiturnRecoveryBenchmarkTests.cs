using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

/// <summary>
/// EXP-047/W.0 shadow recovery measure. It replays the five failed EXP-046
/// first actions, returns only mechanical errors or real read-only observations,
/// and lets the same Q5 choose at most two additional actions.
/// </summary>
public sealed class LiveMultiturnRecoveryBenchmarkTests(
    ITestOutputHelper output)
{
    private const string EnableVariable =
        "SAAIA_LIVE_MULTITURN_RECOVERY";
    private const string OutputVariable =
        "SAAIA_PHASE1_MULTITURN_RECOVERY_OUTPUT";
    private const string ExpectedReplaySha256 =
        "254D9CD1DC15E522E10DEB42F953DDC2FC5137148757C6F6BF2C9345FC866708";
    private const int MaximumOutputTokens = 256;
    private const int MaximumLatencyMilliseconds = 30_000;
    private const int MaximumRecoveryDecisions = 2;
    private const int MaximumObservationItems = 10;
    private const int MaximumObservationExcerptCharacters = 180;
    private const int SufficientInventoryEvidenceCount = 20;
    private const int SufficientInventoryDocumentCount = 10;

    private static readonly string[] RecoveryScenarioIds =
    [
        "compound-noncuisine",
        "ambiguous",
        "ambiguous-nongrid",
        "simple",
        "p1"
    ];

    private static readonly SourceBackedAgentV2Options ObservationOptions =
        new(
            MaximumTurns: 4,
            MaximumToolCalls: 3,
            MaximumObservationItems: MaximumObservationItems,
            MaximumObservationExcerptCharacters:
                MaximumObservationExcerptCharacters,
            MaximumOutputTokens: 900,
            MaximumActionTokens: MaximumOutputTokens,
            MaximumWorkingEvidenceItems: MaximumObservationItems,
            MaximumWorkingExcerptCharacters:
                MaximumObservationExcerptCharacters);

    [Fact]
    public void Frozen_replay_and_feedback_controller_are_mechanically_valid()
    {
        var replay = LoadReplay();
        var tools = LiveDirectCapabilityBoundaryBenchmarkTests.BuildTools();

        Assert.Equal(ExpectedReplaySha256, replay.Sha256);
        Assert.Equal(
            LiveDirectCapabilityBoundaryBenchmarkTests.SystemPrompt,
            replay.SystemPrompt);
        Assert.Equal(
            tools.Select(static tool => tool.Name)
                .OrderBy(static name => name, StringComparer.Ordinal),
            replay.ToolNames.OrderBy(
                static name => name,
                StringComparer.Ordinal));
        Assert.Equal(RecoveryScenarioIds, replay.Scenarios.Select(
            static scenario => scenario.Id));
        Assert.Equal(5, replay.Scenarios.Count);

        var classifications = replay.Scenarios.ToDictionary(
            static scenario => scenario.Id,
            scenario => ValidateCall(
                scenario.InitialToolName,
                scenario.InitialArguments,
                scenario.Question,
                tools),
            StringComparer.Ordinal);
        Assert.Empty(classifications["compound-noncuisine"]);
        Assert.Contains(
            classifications["ambiguous"],
            static failure => failure.StartsWith(
                "unregistered_category_scope:",
                StringComparison.Ordinal));
        Assert.Contains(
            classifications["ambiguous-nongrid"],
            static failure => failure.StartsWith(
                "clarification_anchor_not_in_user_message:",
                StringComparison.Ordinal));
        Assert.Empty(classifications["simple"]);
        Assert.Empty(classifications["p1"]);
    }

    [Fact]
    public async Task Q5_recovers_the_five_frozen_first_actions_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                $"Skipped: set {EnableVariable}=1 to run EXP-047/W.0.");
            return;
        }

        var replay = LoadReplay();
        var tools = LiveDirectCapabilityBoundaryBenchmarkTests.BuildTools();
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
                "EXP-047 requires the validation LLM URL/model and backend URL/API key.");
        }

        var llm = new OpenAiLlmClient();
        llm.Configure(llmBaseUrl, model);
        var api = new ApiClient();
        api.Configure(
            backendUrl,
            apiKey,
            Guid.NewGuid().ToString("D"));
        var orchestrator = new ToolAgentOrchestrator(
            api,
            llm: null!,
            new ToolMemory());
        var results = new List<ScenarioResult>();

        foreach (var scenario in replay.Scenarios)
        {
            results.Add(await RunScenarioAsync(
                llm,
                orchestrator,
                tools,
                scenario));
        }

        var passed = results.Count(static result => result.Passed);
        var protocolValidDecisions = results
            .SelectMany(static result => result.Decisions)
            .Count(static decision => decision.ProtocolValid);
        var decisionCount = results.Sum(static result =>
            result.Decisions.Count);
        var latencyPassCount = results
            .SelectMany(static result => result.Decisions)
            .Count(static decision => decision.LatencyPass);
        var backendExecutions = results.Sum(static result =>
            result.BackendExecutionCount);
        var allPass = passed == RecoveryScenarioIds.Length
                      && protocolValidDecisions == decisionCount
                      && latencyPassCount == decisionCount
                      && results.All(static result =>
                          result.Decisions.Count <= MaximumRecoveryDecisions);
        var verdict = allPass
            ? "APPROUVE_COMME_CAPACITE_DE_RECUPERATION_SHADOW"
            : "REJETE";

        var artifact = ResolveOutputPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(
            artifact,
            JsonSerializer.Serialize(
                new
                {
                    experiment = "EXP-047",
                    variant = "W0-multiturn-recovery",
                    generatedAt = DateTimeOffset.Now,
                    machine = Environment.MachineName,
                    model,
                    llmBaseUrl,
                    backendUrl,
                    replayPath = replay.Path,
                    replaySha256 = replay.Sha256,
                    expectedReplaySha256 = ExpectedReplaySha256,
                    sameSystemPrompt = string.Equals(
                        replay.SystemPrompt,
                        LiveDirectCapabilityBoundaryBenchmarkTests.SystemPrompt,
                        StringComparison.Ordinal),
                    toolNames = tools.Select(static tool => tool.Name),
                    maximumRecoveryDecisions = MaximumRecoveryDecisions,
                    maximumOutputTokens = MaximumOutputTokens,
                    maximumLatencyMilliseconds = MaximumLatencyMilliseconds,
                    maximumObservationItems = MaximumObservationItems,
                    maximumObservationExcerptCharacters =
                        MaximumObservationExcerptCharacters,
                    sufficientInventoryEvidenceCount =
                        SufficientInventoryEvidenceCount,
                    sufficientInventoryDocumentCount =
                        SufficientInventoryDocumentCount,
                    scenarioCount = results.Count,
                    passedScenarioCount = passed,
                    decisionCount,
                    protocolValidDecisionCount = protocolValidDecisions,
                    latencyPassDecisionCount = latencyPassCount,
                    backendExecutions,
                    backendMutationCount = 0,
                    semanticOracleFeedbackCount = 0,
                    productModifiedByHarness = false,
                    verdict,
                    results
                },
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifact);
        output.WriteLine(
            $"EXP-047: verdict={verdict}, scenarios={passed}/{results.Count}, "
            + $"protocol={protocolValidDecisions}/{decisionCount}, "
            + $"latency={latencyPassCount}/{decisionCount}, "
            + $"backend_reads={backendExecutions}");

        Assert.True(
            allPass,
            "EXP-047 recovery gate failed; inspect the JSON artifact.");
    }

    private static async Task<ScenarioResult> RunScenarioAsync(
        OpenAiLlmClient llm,
        ToolAgentOrchestrator orchestrator,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        ReplayScenario scenario)
    {
        var messages = BuildInitialMessages(scenario);
        var decisions = new List<DecisionResult>();
        var backendExecutionCount = 0;
        var firstCall = new SourceBackedAgentToolCall(
            "exp046-replay-" + scenario.Id,
            scenario.InitialToolName,
            scenario.InitialArguments);
        var firstValidation = ValidateCall(
            firstCall.Name,
            firstCall.Arguments,
            scenario.Question,
            tools);
        var feedback = firstValidation.Count > 0
            ? BuildMechanicalErrorFeedback(
                firstCall.Name,
                firstValidation)
            : await ExecuteReadOnlyAsync(
                orchestrator,
                scenario,
                firstCall);
        var initialFeedback = feedback;
        if (feedback.BackendExecuted)
            backendExecutionCount++;

        messages.Add(SourceBackedAgentMessage.Assistant(
            null,
            new[] { firstCall }));
        messages.Add(SourceBackedAgentMessage.Tool(
            firstCall.Id,
            firstCall.Name,
            feedback.CompactJson));

        var passed = false;
        int? passedOnDecision = null;
        var passReason = string.Empty;
        var terminalReason = string.Empty;

        for (var turn = 1; turn <= MaximumRecoveryDecisions; turn++)
        {
            var stopwatch = Stopwatch.StartNew();
            SourceBackedAgentCompletion? completion = null;
            Exception? completionException = null;
            try
            {
                using var cts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(45));
                completion = await llm.ChatOnceNativeAsync(
                    messages,
                    tools,
                    temperature: 0,
                    maxTokens: MaximumOutputTokens,
                    ct: cts.Token,
                    requireToolCall: true);
            }
            catch (Exception exception)
            {
                completionException = exception;
            }
            stopwatch.Stop();

            var protocolFailures = new List<string>();
            SourceBackedAgentToolCall? call = null;
            if (completionException is not null)
            {
                protocolFailures.Add(
                    "exception:"
                    + completionException.GetType().Name
                    + ":"
                    + Truncate(completionException.Message, 300));
            }
            else if (completion is null)
            {
                protocolFailures.Add("completion_missing");
            }
            else
            {
                if (completion.ToolCalls.Count != 1)
                    protocolFailures.Add("single_tool_call_required");
                else
                    call = completion.ToolCalls[0];
                if (!string.Equals(
                        completion.FinishReason,
                        "tool_calls",
                        StringComparison.OrdinalIgnoreCase))
                {
                    protocolFailures.Add("finish_reason_not_tool_calls");
                }
                if (completion.CompletionTokens is > MaximumOutputTokens)
                    protocolFailures.Add("completion_token_budget_exceeded");
            }

            if (call is not null)
            {
                if (!string.IsNullOrWhiteSpace(call.ArgumentError))
                {
                    protocolFailures.Add(
                        "argument_error:" + call.ArgumentError);
                }
                protocolFailures.AddRange(ValidateCall(
                    call.Name,
                    call.Arguments,
                    scenario.Question,
                    tools));
            }

            var latencyPass = stopwatch.ElapsedMilliseconds
                              <= MaximumLatencyMilliseconds;
            var targetFailures = call is not null
                                 && protocolFailures.Count == 0
                ? EvaluateTarget(scenario, call, feedback)
                : new List<string> { "not_evaluated" };
            var targetPass = targetFailures.Count == 0;
            var decision = new DecisionResult(
                turn,
                stopwatch.ElapsedMilliseconds,
                call?.Name ?? string.Empty,
                call?.Arguments.GetRawText() ?? string.Empty,
                protocolFailures.Count == 0,
                latencyPass,
                targetPass,
                protocolFailures,
                targetFailures,
                completion?.FinishReason,
                completion?.PromptTokens,
                completion?.CompletionTokens,
                completion?.ServerCacheTokens,
                completion?.ServerPromptTokensEvaluated,
                completion?.ServerPromptMilliseconds,
                completion?.ServerPredictedTokens,
                completion?.ServerPredictedMilliseconds,
                null);
            decisions.Add(decision);

            if (targetPass)
            {
                passed = true;
                passedOnDecision = turn;
                passReason = "target_action_selected";
                break;
            }
            if (turn == MaximumRecoveryDecisions)
            {
                terminalReason = "decision_budget_exhausted";
                break;
            }
            if (call is null)
            {
                terminalReason = "no_repairable_tool_call";
                break;
            }
            if (string.Equals(
                    call.Name,
                    LiveDirectCapabilityBoundaryBenchmarkTests
                        .ClarificationToolName,
                    StringComparison.Ordinal)
                && protocolFailures.Count == 0)
            {
                terminalReason = "valid_clarification_did_not_meet_target";
                break;
            }

            feedback = protocolFailures.Count > 0
                ? BuildMechanicalErrorFeedback(call.Name, protocolFailures)
                : await ExecuteReadOnlyAsync(
                    orchestrator,
                    scenario,
                    call);
            if (feedback.BackendExecuted)
                backendExecutionCount++;
            decisions[^1] = decisions[^1] with { Feedback = feedback };

            if (protocolFailures.Count == 0
                && ObservationSatisfiesTarget(
                    scenario,
                    feedback,
                    out var observationPassReason))
            {
                passed = true;
                passedOnDecision = turn;
                passReason = observationPassReason;
                break;
            }

            messages.Add(SourceBackedAgentMessage.Assistant(
                completion?.Content,
                new[] { call }));
            messages.Add(SourceBackedAgentMessage.Tool(
                call.Id,
                call.Name,
                feedback.CompactJson));
        }

        if (!passed && string.IsNullOrWhiteSpace(terminalReason))
            terminalReason = "target_not_reached";

        return new ScenarioResult(
            scenario.Id,
            scenario.Question,
            scenario.InitialToolName,
            scenario.InitialArguments.GetRawText(),
            firstValidation,
            InitialFeedback: initialFeedback,
            Decisions: decisions,
            Passed: passed,
            PassedOnDecision: passedOnDecision,
            PassReason: passReason,
            TerminalReason: terminalReason,
            BackendExecutionCount: backendExecutionCount);
    }

    private static List<SourceBackedAgentMessage> BuildInitialMessages(
        ReplayScenario scenario)
        =>
        [
            SourceBackedAgentMessage.System(
                LiveDirectCapabilityBoundaryBenchmarkTests.SystemPrompt),
            SourceBackedAgentMessage.User(
                "CATEGORY_PATHS (optional exact values; omit when uncertain):\n"
                + string.Join(
                    " | ",
                    LiveDirectCapabilityBoundaryBenchmarkTests.CategoryPaths)
                + "\n\nUSER_MESSAGE:\n"
                + scenario.Question)
        ];

    private static async Task<FeedbackResult> ExecuteReadOnlyAsync(
        ToolAgentOrchestrator orchestrator,
        ReplayScenario scenario,
        SourceBackedAgentToolCall call)
    {
        if (!TryResolveExecutionMethod(
                call.Name,
                call.Arguments,
                out var internalName,
                out var methodName))
        {
            return BuildMechanicalErrorFeedback(
                call.Name,
                new[] { "tool_not_read_only_or_unregistered" });
        }

        var executedArguments = PrepareExecutionArguments(
            internalName,
            call.Arguments);
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null)
        {
            return BuildMechanicalErrorFeedback(
                call.Name,
                new[] { "read_only_executor_not_found:" + methodName });
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var task = (Task<JsonElement>)method.Invoke(
                orchestrator,
                new object[] { executedArguments, cts.Token })!;
            var raw = await task.ConfigureAwait(false);
            stopwatch.Stop();

            var toolResults = new ToolResults();
            toolResults.Items.Add(new ToolResults.Item
            {
                ToolName = internalName,
                Result = raw.Clone(),
                DurationMs = stopwatch.ElapsedMilliseconds
            });
            var bundle = EvidenceBundleBuilder.FromToolResults(
                toolResults,
                scenario.Question);
            var compact = SourceBackedAgentObservationCompactor.Build(
                call.Name,
                toolResults.Items,
                bundle,
                firstToolSequence: 1,
                ObservationOptions);
            var distinctDocuments = bundle.Items
                .Select(static item => FirstNonBlank(
                    item.DocPath,
                    item.DocId,
                    item.DocName))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            return new FeedbackResult(
                Kind: "read_only_observation",
                CompactJson: compact,
                RawJson: raw.GetRawText(),
                BackendExecuted: true,
                BackendElapsedMilliseconds: stopwatch.ElapsedMilliseconds,
                InternalToolName: internalName,
                ExecutedArgumentsRaw: executedArguments.GetRawText(),
                EvidenceCount: bundle.Items.Count,
                DistinctDocumentCount: distinctDocuments,
                MechanicalFailures: Array.Empty<string>());
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var effective = exception is TargetInvocationException
                            && exception.InnerException is not null
                ? exception.InnerException
                : exception;
            var payload = JsonSerializer.Serialize(new
            {
                ok = false,
                tool = call.Name,
                error = "read_only_execution_failed",
                exception = effective.GetType().Name,
                message = Truncate(effective.Message, 300),
                evidence = Array.Empty<object>()
            });
            return new FeedbackResult(
                Kind: "read_only_execution_error",
                CompactJson: payload,
                RawJson: payload,
                BackendExecuted: true,
                BackendElapsedMilliseconds: stopwatch.ElapsedMilliseconds,
                InternalToolName: internalName,
                ExecutedArgumentsRaw: executedArguments.GetRawText(),
                EvidenceCount: 0,
                DistinctDocumentCount: 0,
                MechanicalFailures: new[] { "read_only_execution_failed" });
        }
    }

    private static bool TryResolveExecutionMethod(
        string externalName,
        JsonElement arguments,
        out string internalName,
        out string methodName)
    {
        if (string.Equals(
                externalName,
                LiveDirectCapabilityBoundaryBenchmarkTests.CountToolName,
                StringComparison.Ordinal))
        {
            internalName = "documents.count";
            methodName = "ExecDocumentsCountAsync";
            return true;
        }
        if (!SourceBackedAgentToolCatalog.TryResolveInternalName(
                externalName,
                arguments,
                out internalName))
        {
            methodName = string.Empty;
            return false;
        }

        methodName = internalName switch
        {
            "rag.search" => "ExecRagSearchAsync",
            "rag.multi_search" => "ExecRagMultiSearchAsync",
            "documents.navigation" => "ExecDocumentsNavigationAsync",
            "documents.content_cards" => "ExecDocumentsContentCardsAsync",
            "documents.context" => "ExecDocumentsContextAsync",
            _ => string.Empty
        };
        return methodName.Length > 0;
    }

    private static JsonElement PrepareExecutionArguments(
        string internalName,
        JsonElement arguments)
    {
        var normalized = SourceBackedAgentToolCatalog.NormalizeRouterArguments(
            internalName,
            arguments);
        if (internalName is not ("rag.search" or "rag.multi_search"))
            return normalized;

        var values = normalized.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        values["sourceBackedCanonical"] =
            JsonSerializer.SerializeToElement(true);
        values["disableAutomaticCategoryScoping"] =
            JsonSerializer.SerializeToElement(true);
        values["includeResearchSurfaces"] =
            JsonSerializer.SerializeToElement(true);
        values["researchMode"] =
            JsonSerializer.SerializeToElement("source_exploration");
        return JsonSerializer.SerializeToElement(values, ClientJson.CamelCase);
    }

    private static FeedbackResult BuildMechanicalErrorFeedback(
        string toolName,
        IReadOnlyList<string> failures)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["tool"] = toolName,
            ["errors"] = failures,
            ["evidence"] = Array.Empty<object>()
        };
        if (failures.Any(static failure => failure.StartsWith(
                "unregistered_category_scope:",
                StringComparison.Ordinal)))
        {
            payload["allowedCategoryPaths"] =
                LiveDirectCapabilityBoundaryBenchmarkTests.CategoryPaths;
        }
        if (failures.Any(static failure => failure.StartsWith(
                "clarification_anchor_not_in_user_message:",
                StringComparison.Ordinal)))
        {
            payload["userTextAnchorContract"] =
                "Each non-null userTextAnchor must be an exact substring of USER_MESSAGE; otherwise use null.";
        }
        var json = JsonSerializer.Serialize(payload, ClientJson.CamelCase);
        return new FeedbackResult(
            Kind: "mechanical_protocol_error",
            CompactJson: json,
            RawJson: json,
            BackendExecuted: false,
            BackendElapsedMilliseconds: 0,
            InternalToolName: string.Empty,
            ExecutedArgumentsRaw: string.Empty,
            EvidenceCount: 0,
            DistinctDocumentCount: 0,
            MechanicalFailures: failures);
    }

    private static List<string> ValidateCall(
        string toolName,
        JsonElement arguments,
        string userMessage,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools)
    {
        var failures = new List<string>();
        var tool = tools.FirstOrDefault(item => string.Equals(
            item.Name,
            toolName,
            StringComparison.Ordinal));
        if (tool is null)
        {
            failures.Add("unknown_tool_name:" + toolName);
            return failures;
        }
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            failures.Add("arguments_object_required");
            return failures;
        }

        ValidateRootSchema(arguments, tool.Parameters, failures);
        ValidateRegisteredCategoryArguments(arguments, failures);
        if (string.Equals(
                toolName,
                LiveDirectCapabilityBoundaryBenchmarkTests
                    .ClarificationToolName,
                StringComparison.Ordinal))
        {
            ValidateClarification(arguments, userMessage, failures);
        }
        return failures;
    }

    private static void ValidateRootSchema(
        JsonElement arguments,
        JsonElement schema,
        ICollection<string> failures)
    {
        if (!TryGetProperty(schema, "properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            failures.Add("tool_schema_properties_missing");
            return;
        }
        var allowed = properties.EnumerateObject()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                failures.Add("unknown_argument_property:" + property.Name);
                continue;
            }
            var propertySchema = properties.GetProperty(property.Name);
            if (!MatchesDeclaredType(property.Value, propertySchema))
                failures.Add("argument_type_mismatch:" + property.Name);
            if (TryGetProperty(propertySchema, "enum", out var enumValues)
                && enumValues.ValueKind == JsonValueKind.Array
                && property.Value.ValueKind == JsonValueKind.String
                && !enumValues.EnumerateArray().Any(item =>
                    item.ValueKind == JsonValueKind.String
                    && string.Equals(
                        item.GetString(),
                        property.Value.GetString(),
                        StringComparison.Ordinal)))
            {
                failures.Add("argument_enum_mismatch:" + property.Name);
            }
        }

        if (TryGetProperty(schema, "required", out var required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray()
                         .Where(static item =>
                             item.ValueKind == JsonValueKind.String)
                         .Select(static item => item.GetString()!))
            {
                if (!TryGetProperty(arguments, name, out _))
                    failures.Add("required_argument_missing:" + name);
            }
        }
        if (TryGetProperty(schema, "anyOf", out var anyOf)
            && anyOf.ValueKind == JsonValueKind.Array
            && !anyOf.EnumerateArray().Any(option =>
                RequiredSetIsPresent(arguments, option)))
        {
            failures.Add("any_of_required_arguments_missing");
        }
    }

    private static bool RequiredSetIsPresent(
        JsonElement arguments,
        JsonElement schemaOption)
    {
        if (!TryGetProperty(schemaOption, "required", out var required)
            || required.ValueKind != JsonValueKind.Array)
        {
            return true;
        }
        return required.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .All(item => TryGetProperty(
                arguments,
                item.GetString()!,
                out var value)
                && value.ValueKind is not (
                    JsonValueKind.Null or JsonValueKind.Undefined));
    }

    private static bool MatchesDeclaredType(
        JsonElement value,
        JsonElement schema)
    {
        if (!TryGetProperty(schema, "type", out var type))
            return true;
        if (type.ValueKind == JsonValueKind.String)
            return MatchesTypeName(value, type.GetString());
        return type.ValueKind == JsonValueKind.Array
               && type.EnumerateArray().Any(item =>
                   item.ValueKind == JsonValueKind.String
                   && MatchesTypeName(value, item.GetString()));
    }

    private static bool MatchesTypeName(JsonElement value, string? type)
        => type switch
        {
            "null" => value.ValueKind == JsonValueKind.Null,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number
                         && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True
                or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => true
        };

    private static void ValidateRegisteredCategoryArguments(
        JsonElement arguments,
        ICollection<string> failures)
    {
        foreach (var propertyName in new[] { "categoryPath", "path" })
        {
            var submitted = ReadString(arguments, propertyName);
            if (submitted.Length > 0
                && !LiveDirectCapabilityBoundaryBenchmarkTests.CategoryPaths
                    .Contains(
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

    private static void ValidateClarification(
        JsonElement arguments,
        string userMessage,
        ICollection<string> failures)
    {
        if (!TryGetProperty(arguments, "options", out var options)
            || options.ValueKind != JsonValueKind.Array
            || options.GetArrayLength() is < 2 or > 4)
        {
            failures.Add("clarification_options_invalid");
            return;
        }
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object
                || ReadString(option, "label").Length == 0
                || !TryGetProperty(option, "userTextAnchor", out var anchor))
            {
                failures.Add("clarification_option_invalid");
                continue;
            }
            if (anchor.ValueKind == JsonValueKind.Null)
                continue;
            if (anchor.ValueKind != JsonValueKind.String)
            {
                failures.Add("clarification_anchor_type_invalid");
                continue;
            }
            var submitted = anchor.GetString()?.Trim() ?? string.Empty;
            if (submitted.Length == 0
                || userMessage.IndexOf(
                    submitted,
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                failures.Add(
                    "clarification_anchor_not_in_user_message:"
                    + submitted);
            }
        }
    }

    private static List<string> EvaluateTarget(
        ReplayScenario scenario,
        SourceBackedAgentToolCall call,
        FeedbackResult previousFeedback)
    {
        var failures = new List<string>();
        var raw = call.Arguments.GetRawText();
        switch (scenario.Id)
        {
            case "compound-noncuisine":
                RequireOneTool(
                    call.Name,
                    failures,
                    "documents_context",
                    "documents_navigation",
                    "rag_search");
                if (string.Equals(
                        call.Name,
                        "documents_context",
                        StringComparison.Ordinal))
                {
                    if (!LocatorIsGrounded(
                            call.Arguments,
                            previousFeedback,
                            requiredText: "iec 60204-1"))
                    {
                        failures.Add("context_locator_not_grounded_in_iec_observation");
                    }
                    RequireTerms(
                        raw + " " + previousFeedback.CompactJson,
                        failures,
                        "ansi",
                        "iec 60204-1");
                }
                else
                {
                    RequireTerms(raw, failures, "ansi", "iec 60204-1");
                }
                RequireScope(call.Arguments, failures, "Normes");
                break;
            case "ambiguous":
                RequireTool(
                    call.Name,
                    LiveDirectCapabilityBoundaryBenchmarkTests
                        .ClarificationToolName,
                    failures);
                break;
            case "ambiguous-nongrid":
                RequireTool(
                    call.Name,
                    LiveDirectCapabilityBoundaryBenchmarkTests
                        .ClarificationToolName,
                    failures);
                RequireTerms(raw, failures, "obligatoire", "recommandation");
                break;
            case "simple":
                if (string.Equals(
                        call.Name,
                        "rag_search",
                        StringComparison.Ordinal))
                {
                    RequireTerms(raw, failures, "ratatouille");
                    RequireScope(call.Arguments, failures, "Cuisine");
                }
                else if (string.Equals(
                             call.Name,
                             "documents_context",
                             StringComparison.Ordinal))
                {
                    if (!LocatorIsGrounded(
                            call.Arguments,
                            previousFeedback,
                            requiredText: "ratatouille"))
                    {
                        failures.Add("context_locator_not_grounded_in_ratatouille_observation");
                    }
                }
                else
                {
                    failures.Add(
                        "tool_mismatch:"
                        + call.Name
                        + "!=rag_search|documents_context");
                }
                break;
            case "p1":
                RequireOneTool(
                    call.Name,
                    failures,
                    "documents_content_cards",
                    "documents_navigation");
                RequireScope(call.Arguments, failures, "Cuisine");
                if (!string.IsNullOrWhiteSpace(ReadString(
                        call.Arguments,
                        "q"))
                    || !string.IsNullOrWhiteSpace(ReadString(
                        call.Arguments,
                        "query")))
                {
                    failures.Add("p1_query_must_be_empty");
                }
                break;
            default:
                failures.Add("unknown_scenario:" + scenario.Id);
                break;
        }
        return failures;
    }

    private static bool ObservationSatisfiesTarget(
        ReplayScenario scenario,
        FeedbackResult feedback,
        out string reason)
    {
        reason = string.Empty;
        if (!feedback.BackendExecuted
            || !string.Equals(
                feedback.Kind,
                "read_only_observation",
                StringComparison.Ordinal))
        {
            return false;
        }
        if (string.Equals(
                scenario.Id,
                "compound-noncuisine",
                StringComparison.Ordinal)
            && ContainsTerms(
                feedback.RawJson + " " + feedback.CompactJson,
                "ansi",
                "iec 60204-1"))
        {
            reason = "read_only_observation_preserved_ansi_and_iec";
            return true;
        }
        if (string.Equals(scenario.Id, "p1", StringComparison.Ordinal)
            && feedback.EvidenceCount >= SufficientInventoryEvidenceCount
            && feedback.DistinctDocumentCount
            >= SufficientInventoryDocumentCount)
        {
            reason = "read_only_observation_reached_frozen_inventory_threshold";
            return true;
        }
        return false;
    }

    private static bool LocatorIsGrounded(
        JsonElement arguments,
        FeedbackResult feedback,
        string requiredText)
    {
        var observation = Normalize(
            feedback.RawJson + " " + feedback.CompactJson);
        if (!observation.Contains(Normalize(requiredText), StringComparison.Ordinal))
            return false;
        foreach (var propertyName in new[]
                 {
                     "docId", "docPath", "docRef", "chunkId"
                 })
        {
            var locator = ReadString(arguments, propertyName);
            if (locator.Length > 0
                && observation.Contains(
                    Normalize(locator),
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
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
        {
            failures.Add(
                "tool_mismatch:"
                + actual
                + "!="
                + string.Join("|", expected));
        }
    }

    private static void RequireTerms(
        string value,
        ICollection<string> failures,
        params string[] terms)
    {
        foreach (var term in terms)
        {
            if (!ContainsTerms(value, term))
                failures.Add("missing_term:" + term);
        }
    }

    private static bool ContainsTerms(string value, params string[] terms)
    {
        var normalized = Normalize(value);
        return terms.All(term => normalized.Contains(
            Normalize(term),
            StringComparison.Ordinal));
    }

    private static void RequireScope(
        JsonElement arguments,
        ICollection<string> failures,
        string allowedScope)
    {
        var scope = FirstNonBlank(
            ReadString(arguments, "categoryPath"),
            ReadString(arguments, "path"));
        if (!string.IsNullOrWhiteSpace(scope)
            && !string.Equals(
                scope,
                allowedScope,
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("scope_mismatch:" + scope + "!=" + allowedScope);
        }
    }

    private static ReplayArtifact LoadReplay()
    {
        var path = ResolveReplayPath();
        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var observations = root.GetProperty("observations")
            .EnumerateArray()
            .ToDictionary(
                static item => item.GetProperty("ScenarioId").GetString()!,
                static item => item.Clone(),
                StringComparer.Ordinal);
        var scenarios = RecoveryScenarioIds.Select(id =>
        {
            var observation = observations[id];
            var argumentsRaw = observation.GetProperty("ArgumentsRaw")
                .GetString()
                ?? throw new InvalidOperationException(
                    "EXP-046 replay arguments are missing for " + id + ".");
            using var argumentsDocument = JsonDocument.Parse(argumentsRaw);
            return new ReplayScenario(
                id,
                observation.GetProperty("Question").GetString() ?? string.Empty,
                observation.GetProperty("ToolName").GetString() ?? string.Empty,
                argumentsDocument.RootElement.Clone());
        }).ToArray();
        return new ReplayArtifact(
            path,
            sha,
            root.GetProperty("systemPrompt").GetString() ?? string.Empty,
            root.GetProperty("toolNames")
                .EnumerateArray()
                .Select(static item => item.GetString() ?? string.Empty)
                .ToArray(),
            scenarios);
    }

    private static string ResolveReplayPath()
        => Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "goal-rag-end-to-end-20260826-233646",
            "phase1",
            "exp046-v0-direct-capability-boundary.json");

    private static string ResolveOutputPath()
    {
        var configured = Environment.GetEnvironmentVariable(OutputVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        return Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "goal-rag-end-to-end-20260826-233646",
            "phase1",
            "exp047-w0-multiturn-recovery.json");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
               && !Directory.Exists(Path.Combine(current.FullName, ".git")))
        {
            current = current.Parent;
        }
        return current?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate repository root.");
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

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value =>
            !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string Truncate(string value, int maximumCharacters)
        => value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters];

    private sealed record ReplayArtifact(
        string Path,
        string Sha256,
        string SystemPrompt,
        IReadOnlyList<string> ToolNames,
        IReadOnlyList<ReplayScenario> Scenarios);

    private sealed record ReplayScenario(
        string Id,
        string Question,
        string InitialToolName,
        JsonElement InitialArguments);

    private sealed record FeedbackResult(
        string Kind,
        string CompactJson,
        string RawJson,
        bool BackendExecuted,
        long BackendElapsedMilliseconds,
        string InternalToolName,
        string ExecutedArgumentsRaw,
        int EvidenceCount,
        int DistinctDocumentCount,
        IReadOnlyList<string> MechanicalFailures);

    private sealed record DecisionResult(
        int Turn,
        long ElapsedMilliseconds,
        string ToolName,
        string ArgumentsRaw,
        bool ProtocolValid,
        bool LatencyPass,
        bool TargetPass,
        IReadOnlyList<string> ProtocolFailures,
        IReadOnlyList<string> TargetFailures,
        string? FinishReason,
        int? PromptTokens,
        int? CompletionTokens,
        int? ServerCacheTokens,
        int? ServerPromptTokensEvaluated,
        double? ServerPromptMilliseconds,
        int? ServerPredictedTokens,
        double? ServerPredictedMilliseconds,
        FeedbackResult? Feedback);

    private sealed record ScenarioResult(
        string ScenarioId,
        string Question,
        string InitialToolName,
        string InitialArgumentsRaw,
        IReadOnlyList<string> InitialProtocolFailures,
        FeedbackResult InitialFeedback,
        IReadOnlyList<DecisionResult> Decisions,
        bool Passed,
        int? PassedOnDecision,
        string PassReason,
        string TerminalReason,
        int BackendExecutionCount);
}
