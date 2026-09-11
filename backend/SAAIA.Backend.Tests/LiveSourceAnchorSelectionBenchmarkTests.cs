using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Backend.Tests;

public sealed class LiveSourceAnchorSelectionBenchmarkTests(
    ITestOutputHelper output)
{
    private const int ContextSize = 4096;
    private const int MaxCompletionTokens = 50;
    private const long MaxTotalWallMilliseconds = 120_000;

    private const string SystemPrompt = """
Select the single existing source anchor that is the primary autonomous
retrievable identity represented by one source window. Return JSON only with
selectedAnchorIds containing zero or one anchorId from the provided options.
Choose the highest-level concrete subject that best identifies the window as a
whole and that a user could intentionally retrieve. Do not select navigation,
layout labels, quantities, durations, costs, difficulty labels, dangling
fragments, ingredients, parameters, secondary parts, local actions, isolated
instructions, or OCR noise. Select no anchor when none of the provided options
is a defensible primary identity. Never invent or rewrite an anchor. Use only
the source window and anchor options.
""";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Fact]
    public async Task Live_qwen3_selects_existing_primary_source_anchor_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_PHASE2_SOURCE_ANCHOR_SELECTION_BENCHMARK"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_PHASE2_SOURCE_ANCHOR_SELECTION_BENCHMARK=1 to run EXP-035/N.3.");
            return;
        }

        var inputPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_SOURCE_ANCHOR_INPUT_PATH"));
        var artifactPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_SOURCE_ANCHOR_ARTIFACT"));
        var baseUrl = EnsureTrailingSlash(RequireEnvironment(
            "SAAIA_VALIDATION_LLM_BASE_URL"));
        var model = RequireEnvironment("SAAIA_VALIDATION_LLM_MODEL");
        var optionOrder = ParseOptionOrder(
            Environment.GetEnvironmentVariable(
                "SAAIA_PHASE2_SOURCE_ANCHOR_OPTION_ORDER"));
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

        var inputBytes = await File.ReadAllBytesAsync(inputPath);
        var inputHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(inputBytes))
            .ToLowerInvariant();
        var input = JsonSerializer.Deserialize<SourceAnchorInput>(
            inputBytes,
            JsonOptions)
            ?? throw new InvalidOperationException("EXP-035 input is invalid.");

        Assert.Equal(8, input.Windows.Count);
        Assert.Equal(
            "c79c57c72e24a8c2389584c4f091387480dc8218768fb43b2835ff1a661a9729",
            input.SourceInput.Sha256);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(6)
        };

        var totalWall = Stopwatch.StartNew();
        var results = new List<WindowResult>(input.Windows.Count);
        foreach (var window in input.Windows.OrderBy(static item => item.Index))
        {
            Assert.NotEmpty(window.Anchors);
            Assert.Equal(
                window.Anchors.Count,
                window.Anchors.Select(static anchor => anchor.AnchorId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            var orderedAnchors = OrderAnchors(window.Anchors, optionOrder);
            var responseSchema = BuildResponseSchema(orderedAnchors);
            var userPrompt = BuildUserPrompt(input.Document, window, orderedAnchors);
            var request = new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
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
                        name = "source_anchor_selection",
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
                out var selectedAnchorIds);
            var allowedIds = window.Anchors
                .Select(static anchor => anchor.AnchorId)
                .ToHashSet(StringComparer.Ordinal);
            var selectedAnchors = selectedAnchorIds
                .Where(allowedIds.Contains)
                .Select(id => window.Anchors.Single(anchor =>
                    string.Equals(anchor.AnchorId, id, StringComparison.Ordinal)))
                .Select(anchor => new GroundedAnchorSelection(
                    anchor.AnchorId,
                    anchor.Kind,
                    anchor.SourceOrdinal,
                    anchor.Text,
                    input.Document.RevisionId,
                    input.Document.SourceHash,
                    window.SourceId,
                    window.PageStart,
                    window.PageEnd))
                .ToArray();
            var protocolValid = response.StatusCode == HttpStatusCode.OK
                && parseValid
                && selectedAnchorIds.Count <= 1
                && selectedAnchorIds.All(allowedIds.Contains)
                && string.Equals(envelope.FinishReason, "stop", StringComparison.Ordinal)
                && envelope.Usage.PromptTokens is not null
                && envelope.Usage.PromptTokens.Value + MaxCompletionTokens <= ContextSize;

            results.Add(new WindowResult(
                window.Index,
                window.SourceId,
                window.PageStart,
                window.PageEnd,
                window.HeadingPath,
                window.ChunkType,
                orderedAnchors,
                (int)response.StatusCode,
                response.StatusCode.ToString(),
                requestBody,
                responseBody,
                envelope.Completion,
                envelope.FinishReason,
                parseValid,
                protocolValid,
                selectedAnchorIds,
                selectedAnchors,
                envelope.Usage,
                callWall.ElapsedMilliseconds));

            output.WriteLine(
                $"N.3/{optionOrder} window {window.Index}/8: "
                + $"HTTP {(int)response.StatusCode}; finish={envelope.FinishReason}; "
                + $"parse={parseValid}; selected={selectedAnchors.Length}; "
                + $"text={selectedAnchors.FirstOrDefault()?.Text ?? "<none>"}; "
                + $"prompt={envelope.Usage.PromptTokens}; "
                + $"completion={envelope.Usage.CompletionTokens}; "
                + $"wall={callWall.ElapsedMilliseconds} ms");
        }
        totalWall.Stop();

        var report = new
        {
            experiment = "EXP-035",
            variant = "N.3",
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
                maxTotalWallMilliseconds = MaxTotalWallMilliseconds,
                temperature = 0.0,
                systemPrompt = SystemPrompt
            },
            mechanical = new
            {
                windowCount = results.Count,
                httpOkCount = results.Count(result => result.StatusCode == 200),
                stopCount = results.Count(result => string.Equals(result.FinishReason, "stop", StringComparison.Ordinal)),
                parseValidCount = results.Count(result => result.ParseValid),
                protocolValidCount = results.Count(result => result.ProtocolValid),
                selectedCount = results.Sum(result => result.SelectedAnchors.Count),
                promptTokens = results.Sum(result => result.Usage.PromptTokens ?? 0),
                completionTokens = results.Sum(result => result.Usage.CompletionTokens ?? 0),
                totalTokens = results.Sum(result => result.Usage.TotalTokens ?? 0),
                wallMilliseconds = totalWall.ElapsedMilliseconds,
                withinWallBudget = totalWall.ElapsedMilliseconds <= MaxTotalWallMilliseconds
            },
            results,
            humanInspection = new
            {
                status = "A_FAIRE",
                expected = input.Windows
                    .OrderBy(static window => window.Index)
                    .Select(window => new
                    {
                        window.Index,
                        expectedText = window.Index == 7
                            ? null
                            : window.Anchors.FirstOrDefault(anchor =>
                                string.Equals(
                                    anchor.Kind,
                                    "heading_path_level_0",
                                    StringComparison.Ordinal))?.Text
                    })
                    .ToArray(),
                instruction =
                    "Run initial: exiger les sept racines heading exactes et aucune sélection fenêtre 7 avant toute permutation."
            }
        };
        await File.WriteAllTextAsync(
            artifactPath,
            JsonSerializer.Serialize(report, JsonOptions),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifactPath);
        output.WriteLine(
            $"N.3/{optionOrder} total: protocol={report.mechanical.protocolValidCount}/8; "
            + $"selected={report.mechanical.selectedCount}; "
            + $"wall={report.mechanical.wallMilliseconds} ms; "
            + $"withinBudget={report.mechanical.withinWallBudget}");

        Assert.Equal(8, report.mechanical.httpOkCount);
        Assert.Equal(8, report.mechanical.stopCount);
        Assert.Equal(8, report.mechanical.parseValidCount);
        Assert.Equal(8, report.mechanical.protocolValidCount);
        Assert.True(report.mechanical.withinWallBudget);
    }

    private static string BuildUserPrompt(
        SourceAnchorDocument document,
        SourceAnchorWindow window,
        IReadOnlyList<SourceAnchor> anchors)
    {
        var options = new StringBuilder();
        foreach (var anchor in anchors)
        {
            options.Append("anchorId: ").AppendLine(anchor.AnchorId);
            options.Append("kind: ").AppendLine(anchor.Kind);
            options.Append("text: <<<").Append(anchor.Text).AppendLine(">>>");
            options.AppendLine();
        }

        return $"""
Document: {document.DocName}
Path: {document.DocPath}
Source window id: {window.SourceId}
Source pages: {window.PageStart}-{window.PageEnd}
Anchor options:
{options}
Source window:
<<<
{window.Text}
>>>
""";
    }

    private static JsonElement BuildResponseSchema(
        IReadOnlyList<SourceAnchor> anchors)
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                selectedAnchorIds = new
                {
                    type = "array",
                    maxItems = 1,
                    items = new
                    {
                        type = "string",
                        @enum = anchors.Select(static anchor => anchor.AnchorId).ToArray()
                    }
                }
            },
            required = new[] { "selectedAnchorIds" },
            additionalProperties = false
        };
        return JsonSerializer.SerializeToElement(schema, JsonOptions);
    }

    private static IReadOnlyList<SourceAnchor> OrderAnchors(
        IReadOnlyList<SourceAnchor> anchors,
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
                "SAAIA_PHASE2_SOURCE_ANCHOR_OPTION_ORDER must be canonical, reverse, or hash.")
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
        out IReadOnlyList<string> selectedAnchorIds)
    {
        selectedAnchorIds = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(completion))
            return false;

        try
        {
            using var json = JsonDocument.Parse(completion);
            if (!json.RootElement.TryGetProperty(
                    "selectedAnchorIds",
                    out var selected)
                || selected.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var values = selected.EnumerateArray().ToArray();
            if (values.Any(static value => value.ValueKind != JsonValueKind.String))
                return false;

            selectedAnchorIds = values
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

    private sealed record GroundedAnchorSelection(
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

    private sealed record WindowResult(
        int Index,
        Guid SourceId,
        int PageStart,
        int PageEnd,
        string? HeadingPath,
        string ChunkType,
        IReadOnlyList<SourceAnchor> OrderedAnchors,
        int StatusCode,
        string Status,
        string RequestBody,
        string ResponseBody,
        string? Completion,
        string? FinishReason,
        bool ParseValid,
        bool ProtocolValid,
        IReadOnlyList<string> SelectedAnchorIds,
        IReadOnlyList<GroundedAnchorSelection> SelectedAnchors,
        TokenUsage Usage,
        long WallMilliseconds);
}
