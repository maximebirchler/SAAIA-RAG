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
/// A206 route-only probe. It never creates a RouterPlan and never calls the
/// backend. Qwen owns the document/facet decision; the test validates only the
/// transport contract and the preregistered Q016 semantic canary.
/// </summary>
public sealed class LiveDocumentOverviewSpecializedRouterProbeTests(
    ITestOutputHelper output)
{
    private const string EnableVariable =
        "SAAIA_LIVE_DOCUMENT_OVERVIEW_SPECIALIZED_ROUTER_PROBE";
    private const string ArtifactVariable =
        "SAAIA_DOCUMENT_OVERVIEW_SPECIALIZED_ROUTER_ARTIFACT";
    private const string ToolName = "submit_document_overview_route";
    private const int MaximumOutputTokens = 160;
    private const long MaximumAcceptedLatencyMs = 32_000;
    private const long MinimumObservedGeneralSecondStageMs = 49_039;

    private const string DocumentName =
        "ISO 19011 2011 Lignes directrices pour l'audit des systèmes de management.pdf";

    private const string Question =
        "Fais-moi une synthèse utile de `"
        + DocumentName
        + "` : à quoi sert ce document, quelles informations il contient, "
        + "et dans quels cas je devrais le citer ?";

    private const string SystemPrompt = """
        You are SAAIA's second-stage source-backed document-overview router.
        The first-stage classifier has already decided that the user requests
        one overview of one explicit document. Never answer the documentary
        question and never select evidence. Call exactly one available tool.

        Preserve the explicit document reference exactly. Identify every
        cumulative facet that the user asks the overview to address and put
        those facets in their original order. A facet is a requested semantic
        question, not a proposed answer, source, keyword expansion or topic you
        invented. Do not merge distinct requested facets. Do not emit several
        calls for one deliverable.

        requestedPointCount is the number of explicit facets. sampleCount is
        requestedPointCount plus one. questionFocus must be content.
        """;

    [Fact]
    public void Specialized_overview_route_contract_is_single_bounded_and_semantic()
    {
        var tool = BuildTool();

        Assert.Equal(ToolName, tool.Name);
        Assert.Equal(JsonValueKind.Object, tool.Parameters.ValueKind);
        Assert.Equal(
            "object",
            tool.Parameters.GetProperty("type").GetString());
        Assert.False(tool.Parameters.GetProperty(
            "additionalProperties").GetBoolean());
        var required = tool.Parameters.GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Equal(
            new[]
            {
                "document", "facets", "requestedPointCount",
                "sampleCount", "questionFocus"
            },
            required);
        var facets = tool.Parameters.GetProperty("properties")
            .GetProperty("facets");
        Assert.Equal(2, facets.GetProperty("minItems").GetInt32());
        Assert.Equal(5, facets.GetProperty("maxItems").GetInt32());
        Assert.Equal(
            180,
            facets.GetProperty("items").GetProperty("maxLength").GetInt32());
        Assert.Contains(
            "Never answer",
            SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "original order",
            SystemPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Specialized_overview_route_is_measured_once_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine($"Skipped: set {EnableVariable}=1 to run A206.");
            return;
        }

        var artifact = Environment.GetEnvironmentVariable(ArtifactVariable);
        if (string.IsNullOrWhiteSpace(artifact))
        {
            throw new InvalidOperationException(
                $"A206 requires {ArtifactVariable}.");
        }
        artifact = Path.GetFullPath(artifact);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);

        var settings = AppSettings.Load();
        var liveSettings = settings.Clone();
        liveSettings.UseLocalLlm = true;
        liveSettings.ManageLocalLlmProcess = true;
        var manager = new LlamaCppProcessManager();
        manager.SetIdleStopSuppressionProvider(static () => true);

        try
        {
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(180));
            var (ok, message) = await manager.EnsureRunningAsync(
                liveSettings,
                cts.Token);
            Assert.True(ok, message);

            var llm = new OpenAiLlmClient();
            llm.Configure(liveSettings.LlmBaseUrl, liveSettings.ModelId);
            var tool = BuildTool();
            var messages = BuildMessages();
            var inputTokens = await llm.CountNativeInputTokensAsync(
                messages,
                new[] { tool },
                cts.Token,
                requireToolCall: true);
            var watch = Stopwatch.StartNew();
            var completion = await llm.ChatOnceNativeAsync(
                messages,
                new[] { tool },
                temperature: 0,
                maxTokens: MaximumOutputTokens,
                ct: cts.Token,
                requireToolCall: true);
            watch.Stop();

            var failures = Validate(completion, watch.ElapsedMilliseconds);
            var call = completion.ToolCalls.Count == 1
                ? completion.ToolCalls[0]
                : null;
            var reduction = 1d - watch.ElapsedMilliseconds
                / (double)MinimumObservedGeneralSecondStageMs;
            var report = new
            {
                probe = "router.document_overview.specialized.route_only",
                capturedAtUtc = DateTimeOffset.UtcNow,
                stage = "test_only_no_router_plan_no_backend",
                model = liveSettings.ModelId,
                question = Question,
                systemPrompt = SystemPrompt,
                maximumOutputTokens = MaximumOutputTokens,
                preregisteredBaseline = new
                {
                    minimumObservedGeneralSecondStageMs =
                        MinimumObservedGeneralSecondStageMs,
                    maximumAcceptedLatencyMs = MaximumAcceptedLatencyMs,
                    requiredReductionPercent = 40
                },
                inputTokens,
                elapsedMs = watch.ElapsedMilliseconds,
                reductionPercent = Math.Round(reduction * 100d, 2),
                completion.FinishReason,
                completion.PromptTokens,
                completion.CompletionTokens,
                completion.ServerCacheTokens,
                completion.ServerPromptTokensEvaluated,
                completion.ServerPromptMilliseconds,
                completion.ServerPredictedTokens,
                completion.ServerPredictedMilliseconds,
                content = completion.Content,
                toolCallCount = completion.ToolCalls.Count,
                toolCall = call is null
                    ? null
                    : new
                    {
                        call.Name,
                        arguments = call.Arguments,
                        call.ArgumentError
                    },
                protocolAndSemanticFailures = failures,
                protocolValid = failures.Count == 0,
                latencyPass = watch.ElapsedMilliseconds
                              <= MaximumAcceptedLatencyMs,
                backendExecuted = false,
                routerPlanBuilt = false,
                productChanged = false,
                verdict = failures.Count == 0
                    ? "APPROUVE_POUR_INTEGRATION_CONDITIONNELLE_A207"
                    : "REJETE"
            };
            await File.WriteAllTextAsync(
                artifact,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("artifact=" + artifact);
            output.WriteLine("elapsed_ms=" + watch.ElapsedMilliseconds);
            output.WriteLine("completion_tokens=" + completion.CompletionTokens);
            output.WriteLine("reduction_percent=" + Math.Round(
                reduction * 100d,
                2));
            output.WriteLine("failures=" + string.Join(" | ", failures));

            Assert.Empty(failures);
        }
        finally
        {
            manager.Stop();
        }
    }

    private static IReadOnlyList<SourceBackedAgentMessage> BuildMessages()
        => new[]
        {
            SourceBackedAgentMessage.System(SystemPrompt),
            SourceBackedAgentMessage.User("USER_MESSAGE:\n" + Question)
        };

    private static SourceBackedAgentToolDefinition BuildTool()
        => new(
            ToolName,
            "Submit one semantic route for one explicit document overview with all cumulative user facets preserved in order.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    document = new
                    {
                        type = "string",
                        minLength = 1,
                        maxLength = 260
                    },
                    facets = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = 180
                        },
                        minItems = 2,
                        maxItems = 5
                    },
                    requestedPointCount = new
                    {
                        type = "integer",
                        minimum = 2,
                        maximum = 10
                    },
                    sampleCount = new
                    {
                        type = "integer",
                        minimum = 2,
                        maximum = 11
                    },
                    questionFocus = new
                    {
                        type = "string",
                        @enum = new[] { "content" }
                    }
                },
                required = new[]
                {
                    "document", "facets", "requestedPointCount",
                    "sampleCount", "questionFocus"
                },
                additionalProperties = false
            }));

    private static List<string> Validate(
        SourceBackedAgentCompletion completion,
        long elapsedMs)
    {
        var failures = new List<string>();
        if (!string.Equals(
                completion.FinishReason,
                "tool_calls",
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("finish_reason_not_tool_calls");
        }
        if (!string.IsNullOrWhiteSpace(completion.Content))
            failures.Add("documentary_or_commentary_content_present");
        if (completion.ToolCalls.Count != 1)
        {
            failures.Add("single_tool_call_required");
            return failures;
        }

        var call = completion.ToolCalls[0];
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal))
            failures.Add("unexpected_tool:" + call.Name);
        if (!string.IsNullOrWhiteSpace(call.ArgumentError))
            failures.Add("argument_error:" + call.ArgumentError);
        if (call.Arguments.ValueKind != JsonValueKind.Object)
        {
            failures.Add("arguments_object_required");
            return failures;
        }

        var document = ReadString(call.Arguments, "document");
        if (!string.Equals(document, DocumentName, StringComparison.Ordinal))
            failures.Add("explicit_document_not_preserved:" + document);
        var facets = ReadStringArray(call.Arguments, "facets");
        if (facets.Length != 3)
            failures.Add("three_facets_required:" + facets.Length);
        if (facets.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != facets.Length)
        {
            failures.Add("duplicate_facets");
        }
        if (facets.Length == 3)
        {
            RequireAny(facets[0], failures, "sert", "but", "objectif");
            RequireAny(
                facets[1],
                failures,
                "information", "contient", "contenu");
            RequireAny(facets[2], failures, "citer", "citation");
        }
        var requestedPointCount = ReadInt(
            call.Arguments,
            "requestedPointCount");
        if (requestedPointCount != 3)
            failures.Add("requested_point_count_not_three");
        if (ReadInt(call.Arguments, "sampleCount") != 4)
            failures.Add("sample_count_not_four");
        if (!string.Equals(
                ReadString(call.Arguments, "questionFocus"),
                "content",
                StringComparison.Ordinal))
        {
            failures.Add("question_focus_not_content");
        }
        if (completion.CompletionTokens is > MaximumOutputTokens)
            failures.Add("completion_token_budget_exceeded");
        var reductionPass = elapsedMs <= MaximumAcceptedLatencyMs
                            || elapsedMs
                            <= MinimumObservedGeneralSecondStageMs * 0.60d;
        if (!reductionPass)
            failures.Add("latency_reduction_gate_failed:" + elapsedMs);
        return failures;
    }

    private static void RequireAny(
        string value,
        ICollection<string> failures,
        params string[] alternatives)
    {
        var normalized = Normalize(value);
        if (!alternatives.Any(term => normalized.Contains(
                Normalize(term),
                StringComparison.Ordinal)))
        {
            failures.Add("facet_semantic_anchor_missing:" + value);
        }
    }

    private static string ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static string[] ReadStringArray(
        JsonElement root,
        string propertyName)
        => root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()?.Trim() ?? string.Empty)
                .Where(static item => item.Length > 0)
                .ToArray()
            : [];

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
}
