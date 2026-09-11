using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Backend.Tests;

public sealed class LiveStructuralAnchorSelectionBenchmarkTests(
    ITestOutputHelper output)
{
    private const int ContextSize = 4096;
    private const int MaxCompletionTokens = 50;
    private const long MaxTotalWallMilliseconds = 120_000;
    private const long MaxNonCuisineTotalWallMilliseconds = 150_000;

    private const string SystemPrompt = """
Select the single existing structural source anchor that is the primary
autonomous retrievable identity represented by one source window. Return JSON
only with selectedAnchorKeys containing exactly one anchorKey from the provided
options. Choose the highest-level concrete subject that best identifies the
window as a whole and that a user could intentionally retrieve. Do not select a
layout label, generic section function, secondary part, or local action. Never
invent, rewrite, or combine anchors. Use only the source window and structural
anchor options.
""";

    private const string SemanticAbstentionSystemPrompt = """
Select zero or one existing structural source anchor that is the primary
autonomous retrievable identity represented by one source window. Return JSON
only with selectedAnchorKeys containing either zero keys when no provided option
is a legitimate primary identity, or exactly one anchorKey from the provided
options. Choose the concrete subject best supported by the source window as a
whole and that a user could intentionally retrieve. Heading depth is evidence,
not a priority: never prefer an option only because it is higher or lower in the
hierarchy. Abstain if every option is extraction noise, a layout label, citation,
truncated fragment, generic section function, secondary part, or local action.
Never invent, rewrite, or combine anchors. Use only the source window and
structural anchor options.
""";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Fact]
    public async Task Live_qwen3_selects_primary_structural_anchor_when_enabled()
    {
        var cuisineEnabled = string.Equals(
            Environment.GetEnvironmentVariable(
                "SAAIA_LIVE_PHASE2_STRUCTURAL_ANCHOR_SELECTION_BENCHMARK"),
            "1",
            StringComparison.Ordinal);
        var nonCuisineEnabled = string.Equals(
            Environment.GetEnvironmentVariable(
                "SAAIA_LIVE_PHASE2_NON_CUISINE_STRUCTURAL_ANCHOR_BENCHMARK"),
            "1",
            StringComparison.Ordinal);
        var semanticAbstentionEnabled = string.Equals(
            Environment.GetEnvironmentVariable(
                "SAAIA_LIVE_PHASE2_SEMANTIC_ABSTENTION_BENCHMARK"),
            "1",
            StringComparison.Ordinal);
        if (!cuisineEnabled && !nonCuisineEnabled && !semanticAbstentionEnabled)
        {
            output.WriteLine(
                "Skipped: enable the EXP-036/N.4, EXP-037/O.0, or EXP-038/O.1 live structural-anchor benchmark.");
            return;
        }
        Assert.Equal(
            1,
            new[] { cuisineEnabled, nonCuisineEnabled, semanticAbstentionEnabled }
                .Count(static enabled => enabled));

        var inputPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_SOURCE_ANCHOR_INPUT_PATH"));
        var artifactPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_STRUCTURAL_ANCHOR_ARTIFACT"));
        var baseUrl = EnsureTrailingSlash(RequireEnvironment(
            "SAAIA_VALIDATION_LLM_BASE_URL"));
        var model = RequireEnvironment("SAAIA_VALIDATION_LLM_MODEL");
        var optionOrder = ParseOptionOrder(
            Environment.GetEnvironmentVariable(
                "SAAIA_PHASE2_STRUCTURAL_ANCHOR_OPTION_ORDER"));
        var experiment = semanticAbstentionEnabled
            ? "EXP-038"
            : nonCuisineEnabled ? "EXP-037" : "EXP-036";
        var variant = semanticAbstentionEnabled
            ? "O.1"
            : nonCuisineEnabled ? "O.0" : "N.4";
        var maxWallMilliseconds = nonCuisineEnabled || semanticAbstentionEnabled
            ? MaxNonCuisineTotalWallMilliseconds
            : MaxTotalWallMilliseconds;
        var systemPrompt = semanticAbstentionEnabled
            ? SemanticAbstentionSystemPrompt
            : SystemPrompt;
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

        var inputBytes = await File.ReadAllBytesAsync(inputPath);
        var inputHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(inputBytes))
            .ToLowerInvariant();
        var input = JsonSerializer.Deserialize<SourceAnchorInput>(
            inputBytes,
            JsonOptions)
            ?? throw new InvalidOperationException($"{experiment} input is invalid.");

        NonCuisineTruth? truth = null;
        string? truthPath = null;
        string? truthHash = null;
        if (nonCuisineEnabled || semanticAbstentionEnabled)
        {
            truthPath = Path.GetFullPath(RequireEnvironment(
                "SAAIA_PHASE2_STRUCTURAL_ANCHOR_TRUTH_PATH"));
            var truthBytes = await File.ReadAllBytesAsync(truthPath);
            truthHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(truthBytes))
                .ToLowerInvariant();
            truth = JsonSerializer.Deserialize<NonCuisineTruth>(
                truthBytes,
                JsonOptions)
                ?? throw new InvalidOperationException("EXP-037 truth is invalid.");
            Assert.Contains(
                truth.Status,
                new[]
                {
                    "FROZEN_BEFORE_ANY_O0_LLM_CALL",
                    "FROZEN_BEFORE_ANY_O1_LLM_CALL",
                    "FROZEN_FROM_APPROVED_N4_BEFORE_O1_REGRESSION"
                });
            Assert.Equal(inputHash, truth.InputSha256);
        }
        else
        {
            Assert.Equal(
                "3a03f1edc7e1fa429dee94b7cd63a579fc0b6a92b500938c91207483e9596da7",
                inputHash);
        }

        var prepared = input.Windows
            .OrderBy(static window => window.Index)
            .Select(window => new PreparedWindow(
                window,
                window.Anchors
                    .Where(static anchor =>
                        anchor.Kind.StartsWith(
                            "heading_path_level_",
                            StringComparison.Ordinal)
                        || string.Equals(
                            anchor.Kind,
                            "section_title",
                            StringComparison.Ordinal))
                    .Select(anchor => new StructuralAnchor(
                        BuildAnchorKey(anchor.AnchorId),
                        anchor.AnchorId,
                        anchor.Kind,
                        anchor.SourceOrdinal,
                        anchor.Text))
                    .ToArray()))
            .ToArray();

        if (nonCuisineEnabled || semanticAbstentionEnabled)
        {
            Assert.NotNull(truth);
            Assert.Equal(truth.Windows.Count, prepared.Length);
            Assert.Equal(
                truth.Windows.Select(static item => item.Index).OrderBy(static index => index),
                prepared.Select(static item => item.Window.Index));
            Assert.Equal(
                truth.Windows
                    .Where(static item => string.Equals(
                        item.ExpectedAction,
                        "no_call",
                        StringComparison.Ordinal))
                    .Select(static item => item.Index)
                    .OrderBy(static index => index),
                prepared
                    .Where(static item => item.Anchors.Count == 0)
                    .Select(static item => item.Window.Index)
                    .OrderBy(static index => index));
        }
        else
        {
            Assert.Equal(8, prepared.Length);
            Assert.Equal(7, prepared.Count(static item => item.Anchors.Count > 0));
            Assert.Single(prepared, static item => item.Anchors.Count == 0);
            Assert.Equal(7, prepared.Single(static item => item.Anchors.Count == 0).Window.Index);
            Assert.All(
                prepared.Where(static item => item.Anchors.Count > 0),
                static item => Assert.Equal(2, item.Anchors.Count));
        }
        var allKeys = prepared.SelectMany(static item => item.Anchors)
            .Select(static anchor => anchor.AnchorKey)
            .ToArray();
        Assert.Equal(
            allKeys.Length,
            allKeys.Distinct(StringComparer.Ordinal).Count());

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(5)
        };

        var totalWall = Stopwatch.StartNew();
        var results = new List<WindowResult>(prepared.Length);
        foreach (var item in prepared)
        {
            if (item.Anchors.Count == 0)
            {
                results.Add(WindowResult.NotApplicable(item.Window));
                output.WriteLine(
                    $"{variant}/{optionOrder} window {item.Window.Index}/{prepared.Length}: "
                    + "not_applicable_missing_structural_anchor; no LLM call");
                continue;
            }

            var orderedAnchors = OrderAnchors(item.Anchors, optionOrder);
            var responseSchema = BuildResponseSchema(
                orderedAnchors,
                semanticAbstentionEnabled);
            var userPrompt = BuildUserPrompt(
                input.Document,
                item.Window,
                orderedAnchors);
            var request = new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.0,
                max_tokens = MaxCompletionTokens,
                stream = false,
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "structural_anchor_selection",
                        strict = true,
                        schema = responseSchema
                    }
                }
            };
            var requestBody = JsonSerializer.Serialize(request, JsonOptions);
            var callWall = Stopwatch.StartNew();
            using var response = await client.PostAsJsonAsync(
                "v1/chat/completions",
                request,
                JsonOptions,
                cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);
            callWall.Stop();

            var envelope = ReadResponseEnvelope(responseBody);
            var parseValid = TryReadSelection(
                envelope.Completion,
                out var selectedAnchorKeys);
            var allowedKeys = item.Anchors
                .Select(static anchor => anchor.AnchorKey)
                .ToHashSet(StringComparer.Ordinal);
            var selectedAnchors = selectedAnchorKeys
                .Where(allowedKeys.Contains)
                .Select(key => item.Anchors.Single(anchor =>
                    string.Equals(
                        anchor.AnchorKey,
                        key,
                        StringComparison.Ordinal)))
                .Select(anchor => new GroundedStructuralSelection(
                    anchor.AnchorKey,
                    anchor.AnchorId,
                    anchor.Kind,
                    anchor.SourceOrdinal,
                    anchor.Text,
                    input.Document.RevisionId,
                    input.Document.SourceHash,
                    item.Window.SourceId,
                    item.Window.PageStart,
                    item.Window.PageEnd))
                .ToArray();
            var protocolValid = response.StatusCode == HttpStatusCode.OK
                && parseValid
                && (semanticAbstentionEnabled
                    ? selectedAnchorKeys.Count <= 1
                    : selectedAnchorKeys.Count == 1)
                && selectedAnchorKeys.All(allowedKeys.Contains)
                && string.Equals(
                    envelope.FinishReason,
                    "stop",
                    StringComparison.Ordinal)
                && envelope.Usage.PromptTokens is not null
                && envelope.Usage.PromptTokens.Value + MaxCompletionTokens
                <= ContextSize;

            results.Add(new WindowResult(
                item.Window.Index,
                true,
                null,
                item.Window.SourceId,
                item.Window.PageStart,
                item.Window.PageEnd,
                item.Window.HeadingPath,
                item.Window.ChunkType,
                orderedAnchors,
                (int)response.StatusCode,
                response.StatusCode.ToString(),
                requestBody,
                responseBody,
                envelope.Completion,
                envelope.FinishReason,
                parseValid,
                protocolValid,
                selectedAnchorKeys,
                selectedAnchors,
                envelope.Usage,
                callWall.ElapsedMilliseconds));

            output.WriteLine(
                $"{variant}/{optionOrder} window {item.Window.Index}/{prepared.Length}: "
                + $"HTTP {(int)response.StatusCode}; finish={envelope.FinishReason}; "
                + $"parse={parseValid}; "
                + $"text={selectedAnchors.SingleOrDefault()?.Text ?? "<none>"}; "
                + $"prompt={envelope.Usage.PromptTokens}; "
                + $"completion={envelope.Usage.CompletionTokens}; "
                + $"wall={callWall.ElapsedMilliseconds} ms");
        }
        totalWall.Stop();

        var applicableResults = results.Where(static result => result.Applicable)
            .ToArray();
        var semanticEvaluations = truth?.Windows
            .OrderBy(static expected => expected.Index)
            .Select(expected =>
            {
                var actual = results.Single(result => result.Index == expected.Index);
                var correct = expected.ExpectedAction switch
                {
                    "no_call" => !actual.Applicable
                        && actual.SelectedAnchorKeys.Count == 0,
                    "abstain" => actual.Applicable
                        && actual.SelectedAnchorKeys.Count == 0,
                    "select" => actual.Applicable
                        && actual.SelectedAnchorKeys.Count == 1
                        && string.Equals(
                            expected.ExpectedAnchorKey,
                            actual.SelectedAnchorKeys[0],
                            StringComparison.Ordinal),
                    _ => throw new InvalidOperationException(
                        $"Unknown truth action '{expected.ExpectedAction}'.")
                };
                return new SemanticEvaluation(
                    expected.Index,
                    expected.ExpectedAction,
                    expected.ExpectedAnchorKey,
                    actual.Applicable,
                    actual.SelectedAnchorKeys,
                    correct,
                    expected.Justification);
            })
            .ToArray()
            ?? Array.Empty<SemanticEvaluation>();
        var semanticCorrectCount = semanticEvaluations.Count(
            static evaluation => evaluation.Correct);
        var report = new
        {
            experiment,
            variant,
            generatedAt = DateTimeOffset.Now,
            approval = "TESTE_NON_APPROUVE",
            input = new
            {
                path = inputPath,
                sha256 = inputHash,
                exact = input
            },
            configuration = new
            {
                baseUrl,
                model,
                optionOrder = optionOrder.ToString().ToLowerInvariant(),
                contextSize = ContextSize,
                maxCompletionTokens = MaxCompletionTokens,
                maxTotalWallMilliseconds = maxWallMilliseconds,
                temperature = 0.0,
                eligibility = "heading_path_level_* or section_title",
                anchorKey = "a_ + first 16 hex of full anchorId SHA-256",
                cardinality = semanticAbstentionEnabled ? "zero_or_one" : "exactly_one",
                systemPrompt
            },
            preflight = new
            {
                windowCount = prepared.Length,
                applicableCount = prepared.Count(static item => item.Anchors.Count > 0),
                nonApplicableCount = prepared.Count(static item => item.Anchors.Count == 0),
                structuralAnchorCount = prepared.Sum(static item => item.Anchors.Count),
                uniqueKeyCount = allKeys.Distinct(StringComparer.Ordinal).Count()
            },
            mechanical = new
            {
                llmCallCount = applicableResults.Length,
                httpOkCount = applicableResults.Count(result => result.StatusCode == 200),
                stopCount = applicableResults.Count(result => string.Equals(result.FinishReason, "stop", StringComparison.Ordinal)),
                parseValidCount = applicableResults.Count(result => result.ParseValid),
                protocolValidCount = applicableResults.Count(result => result.ProtocolValid),
                selectedCount = applicableResults.Sum(result => result.SelectedAnchors.Count),
                promptTokens = applicableResults.Sum(result => result.Usage.PromptTokens ?? 0),
                completionTokens = applicableResults.Sum(result => result.Usage.CompletionTokens ?? 0),
                totalTokens = applicableResults.Sum(result => result.Usage.TotalTokens ?? 0),
                wallMilliseconds = totalWall.ElapsedMilliseconds,
                withinWallBudget = totalWall.ElapsedMilliseconds <= maxWallMilliseconds
            },
            results,
            frozenTruth = nonCuisineEnabled || semanticAbstentionEnabled
                ? new
                {
                    status = truth!.Status,
                    path = truthPath,
                    sha256 = truthHash,
                    exact = truth
                }
                : null,
            semantic = nonCuisineEnabled || semanticAbstentionEnabled
                ? new
                {
                    expectedCount = truth!.Windows.Count,
                    correctCount = semanticCorrectCount,
                    allCorrect = semanticCorrectCount == truth.Windows.Count,
                    evaluations = semanticEvaluations
                }
                : null,
            humanInspection = nonCuisineEnabled || semanticAbstentionEnabled
                ? null
                : new
                {
                    status = "A_FAIRE",
                    expected = prepared.Select(item => new
                    {
                        item.Window.Index,
                        applicable = item.Anchors.Count > 0,
                        expectedText = item.Anchors.FirstOrDefault(anchor =>
                            string.Equals(
                                anchor.Kind,
                                "heading_path_level_0",
                                StringComparison.Ordinal))?.Text
                    })
                        .ToArray(),
                    instruction =
                        "Exiger 7/7 racines et fenêtre 7 non applicable avant permutations."
                }
        };
        await File.WriteAllTextAsync(
            artifactPath,
            JsonSerializer.Serialize(report, JsonOptions),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifactPath);
        output.WriteLine(
            $"{variant}/{optionOrder} total: protocol={report.mechanical.protocolValidCount}/{applicableResults.Length}; "
            + $"selected={report.mechanical.selectedCount}; "
            + $"wall={report.mechanical.wallMilliseconds} ms; "
            + $"withinBudget={report.mechanical.withinWallBudget}");

        var expectedLlmCalls = prepared.Count(static item => item.Anchors.Count > 0);
        Assert.Equal(expectedLlmCalls, report.mechanical.llmCallCount);
        Assert.Equal(expectedLlmCalls, report.mechanical.httpOkCount);
        Assert.Equal(expectedLlmCalls, report.mechanical.stopCount);
        Assert.Equal(expectedLlmCalls, report.mechanical.parseValidCount);
        Assert.Equal(expectedLlmCalls, report.mechanical.protocolValidCount);
        var expectedSelectedCount = semanticAbstentionEnabled
            ? truth!.Windows.Count(static item =>
                string.Equals(item.ExpectedAction, "select", StringComparison.Ordinal))
            : expectedLlmCalls;
        Assert.Equal(expectedSelectedCount, report.mechanical.selectedCount);
        Assert.True(report.mechanical.withinWallBudget);
        if (nonCuisineEnabled || semanticAbstentionEnabled)
        {
            output.WriteLine(
                $"{variant}/{optionOrder} semantic: {semanticCorrectCount}/{truth!.Windows.Count}");
            Assert.Equal(truth.Windows.Count, semanticCorrectCount);
        }
    }

    private static string BuildAnchorKey(string anchorId)
    {
        const string prefix = "sha256:";
        if (!anchorId.StartsWith(prefix, StringComparison.Ordinal)
            || anchorId.Length < prefix.Length + 16)
        {
            throw new InvalidOperationException("Invalid full anchorId.");
        }

        return "a_" + anchorId.Substring(prefix.Length, 16);
    }

    private static string BuildUserPrompt(
        SourceAnchorDocument document,
        SourceAnchorWindow window,
        IReadOnlyList<StructuralAnchor> anchors)
    {
        var options = new StringBuilder();
        foreach (var anchor in anchors)
        {
            options.Append("anchorKey: ").AppendLine(anchor.AnchorKey);
            options.Append("kind: ").AppendLine(anchor.Kind);
            options.Append("text: <<<").Append(anchor.Text).AppendLine(">>>");
            options.AppendLine();
        }

        return $"""
Document: {document.DocName}
Path: {document.DocPath}
Source window id: {window.SourceId}
Source pages: {window.PageStart}-{window.PageEnd}
Structural anchor options:
{options}
Source window:
<<<
{window.Text}
>>>
""";
    }

    private static JsonElement BuildResponseSchema(
        IReadOnlyList<StructuralAnchor> anchors,
        bool allowEmptySelection)
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                selectedAnchorKeys = new
                {
                    type = "array",
                    minItems = allowEmptySelection ? 0 : 1,
                    maxItems = 1,
                    items = new
                    {
                        type = "string",
                        @enum = anchors.Select(static anchor => anchor.AnchorKey).ToArray()
                    }
                }
            },
            required = new[] { "selectedAnchorKeys" },
            additionalProperties = false
        };
        return JsonSerializer.SerializeToElement(schema, JsonOptions);
    }

    private static IReadOnlyList<StructuralAnchor> OrderAnchors(
        IReadOnlyList<StructuralAnchor> anchors,
        AnchorOptionOrder order)
        => order switch
        {
            AnchorOptionOrder.Reverse => anchors.Reverse().ToArray(),
            AnchorOptionOrder.Hash => anchors
                .OrderBy(static anchor => anchor.AnchorId, StringComparer.Ordinal)
                .ToArray(),
            _ => anchors.ToArray()
        };

    private static AnchorOptionOrder ParseOptionOrder(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "reverse" => AnchorOptionOrder.Reverse,
            "hash" => AnchorOptionOrder.Hash,
            null or "" or "canonical" => AnchorOptionOrder.Canonical,
            _ => throw new InvalidOperationException(
                "SAAIA_PHASE2_STRUCTURAL_ANCHOR_OPTION_ORDER must be canonical, reverse, or hash.")
        };

    private static string RequireEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(name + " is required.");

    private static string EnsureTrailingSlash(string value)
        => value.Trim().TrimEnd('/') + "/";

    private static ResponseEnvelope ReadResponseEnvelope(string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            string? completion = null;
            string? finishReason = null;
            if (json.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("finish_reason", out var finish)
                    && finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = finish.GetString();
                }
                if (first.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    completion = content.GetString();
                }
            }

            var usage = json.RootElement.TryGetProperty("usage", out var usageJson)
                        && usageJson.ValueKind == JsonValueKind.Object
                ? new TokenUsage(
                    ReadInt32(usageJson, "prompt_tokens"),
                    ReadInt32(usageJson, "completion_tokens"),
                    ReadInt32(usageJson, "total_tokens"))
                : new TokenUsage();
            return new ResponseEnvelope(completion, finishReason, usage);
        }
        catch (JsonException)
        {
            return new ResponseEnvelope(null, null, new TokenUsage());
        }
    }

    private static bool TryReadSelection(
        string? completion,
        out IReadOnlyList<string> selectedAnchorKeys)
    {
        selectedAnchorKeys = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(completion))
            return false;

        try
        {
            using var json = JsonDocument.Parse(completion);
            if (!json.RootElement.TryGetProperty(
                    "selectedAnchorKeys",
                    out var selected)
                || selected.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var values = selected.EnumerateArray().ToArray();
            if (values.Any(static value => value.ValueKind != JsonValueKind.String))
                return false;

            selectedAnchorKeys = values
                .Select(static value => value.GetString()!)
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? ReadInt32(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private enum AnchorOptionOrder
    {
        Canonical,
        Reverse,
        Hash
    }

    private sealed record PreparedWindow(
        SourceAnchorWindow Window,
        IReadOnlyList<StructuralAnchor> Anchors);

    private sealed record SourceAnchorInput(
        SourceAnchorDocument Document,
        SourceInputIdentity SourceInput,
        AnchorScheme AnchorScheme,
        IReadOnlyList<SourceAnchorWindow> Windows);

    private sealed record SourceAnchorDocument(
        Guid DocId,
        Guid RevisionId,
        string DocPath,
        string DocName,
        string? Category,
        int PageCount,
        int IndexedVersion,
        string SourceHash);

    private sealed record SourceInputIdentity(string Path, string Sha256);

    private sealed record AnchorScheme(
        string Version,
        string Identity,
        IReadOnlyList<string> CanonicalOrder);

    private sealed record SourceAnchorWindow(
        int Index,
        string SourceKind,
        Guid SourceId,
        int Ordinal,
        int PageStart,
        int PageEnd,
        int TokenCount,
        string ContentRole,
        string? HeadingPath,
        string? SectionTitle,
        Guid? SectionId,
        string ChunkType,
        string ChunkComposition,
        IReadOnlyList<string> ExtractionQualitySignals,
        string Text,
        IReadOnlyList<SourceAnchor> Anchors);

    private sealed record SourceAnchor(
        string AnchorId,
        string Kind,
        int SourceOrdinal,
        string Text);

    private sealed record StructuralAnchor(
        string AnchorKey,
        string AnchorId,
        string Kind,
        int SourceOrdinal,
        string Text);

    private sealed record GroundedStructuralSelection(
        string AnchorKey,
        string AnchorId,
        string Kind,
        int SourceOrdinal,
        string Text,
        Guid RevisionId,
        string SourceHash,
        Guid SourceId,
        int PageStart,
        int PageEnd);

    private sealed record TokenUsage(
        int? PromptTokens = null,
        int? CompletionTokens = null,
        int? TotalTokens = null);

    private sealed record ResponseEnvelope(
        string? Completion,
        string? FinishReason,
        TokenUsage Usage);

    private sealed record NonCuisineTruth(
        string Status,
        string InputSha256,
        IReadOnlyList<NonCuisineTruthWindow> Windows);

    private sealed record NonCuisineTruthWindow(
        int Index,
        string ExpectedAction,
        string? ExpectedAnchorKey,
        string? Justification);

    private sealed record SemanticEvaluation(
        int Index,
        string ExpectedAction,
        string? ExpectedAnchorKey,
        bool ActualApplicable,
        IReadOnlyList<string> ActualSelectedAnchorKeys,
        bool Correct,
        string? Justification);

    private sealed record WindowResult(
        int Index,
        bool Applicable,
        string? NonApplicableReason,
        Guid SourceId,
        int PageStart,
        int PageEnd,
        string? HeadingPath,
        string ChunkType,
        IReadOnlyList<StructuralAnchor> OrderedAnchors,
        int? StatusCode,
        string? Status,
        string? RequestBody,
        string? ResponseBody,
        string? Completion,
        string? FinishReason,
        bool ParseValid,
        bool ProtocolValid,
        IReadOnlyList<string> SelectedAnchorKeys,
        IReadOnlyList<GroundedStructuralSelection> SelectedAnchors,
        TokenUsage Usage,
        long WallMilliseconds)
    {
        internal static WindowResult NotApplicable(SourceAnchorWindow window)
            => new(
                window.Index,
                false,
                "not_applicable_missing_structural_anchor",
                window.SourceId,
                window.PageStart,
                window.PageEnd,
                window.HeadingPath,
                window.ChunkType,
                Array.Empty<StructuralAnchor>(),
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                true,
                Array.Empty<string>(),
                Array.Empty<GroundedStructuralSelection>(),
                new TokenUsage(),
                0);
    }
}
