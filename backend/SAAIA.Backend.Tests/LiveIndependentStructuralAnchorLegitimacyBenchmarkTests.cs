using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Backend.Tests;

public sealed class LiveIndependentStructuralAnchorLegitimacyBenchmarkTests(
    ITestOutputHelper output)
{
    private const int ContextSize = 4096;
    private const int MaxCompletionTokens = 30;
    private const long DefaultMaxWallMilliseconds = 150_000;

    private const string SystemPrompt = """
Judge whether the one existing structural source anchor is a legitimate primary
autonomous retrievable identity for the provided source window as a whole.
Return JSON only with supported set to true or false. Return true only when the
anchor text itself is a concrete identity and the source window as a whole
substantively represents that identity. Return false otherwise. Heading metadata,
hierarchy depth, and option position are not semantic validity by themselves.
Never invent, rewrite, broaden, or narrow the anchor. Judge only the supplied
anchor against the supplied source window.
""";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Fact]
    public async Task Live_qwen3_judges_each_structural_anchor_independently_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_PHASE2_INDEPENDENT_ANCHOR_LEGITIMACY_BENCHMARK"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable EXP-039/P.0 independent structural-anchor legitimacy benchmark.");
            return;
        }

        var inputPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_SOURCE_ANCHOR_INPUT_PATH"));
        var truthPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_INDEPENDENT_ANCHOR_TRUTH_PATH"));
        var artifactPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_INDEPENDENT_ANCHOR_ARTIFACT"));
        var baseUrl = EnsureTrailingSlash(RequireEnvironment(
            "SAAIA_VALIDATION_LLM_BASE_URL"));
        var model = RequireEnvironment("SAAIA_VALIDATION_LLM_MODEL");
        var callOrder = ParseCallOrder(Environment.GetEnvironmentVariable(
            "SAAIA_PHASE2_INDEPENDENT_ANCHOR_CALL_ORDER"));
        var maxWallMilliseconds = ParseMaxWall(Environment.GetEnvironmentVariable(
            "SAAIA_PHASE2_INDEPENDENT_ANCHOR_MAX_WALL_MS"));
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

        var inputBytes = await File.ReadAllBytesAsync(inputPath);
        var truthBytes = await File.ReadAllBytesAsync(truthPath);
        var inputHash = Sha256(inputBytes);
        var truthHash = Sha256(truthBytes);
        var input = JsonSerializer.Deserialize<SourceAnchorInput>(
            inputBytes,
            JsonOptions)
            ?? throw new InvalidOperationException("P.0 source-anchor input is invalid.");
        var truth = JsonSerializer.Deserialize<AnchorTruth>(
            truthBytes,
            JsonOptions)
            ?? throw new InvalidOperationException("P.0 anchor truth is invalid.");
        Assert.Equal(inputHash, truth.InputSha256);
        Assert.Contains("FROZEN", truth.Status, StringComparison.Ordinal);

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
        Assert.Equal(
            truth.Windows.Select(static window => window.Index).OrderBy(static index => index),
            prepared.Select(static window => window.Window.Index));

        var allKeys = prepared.SelectMany(static item => item.Anchors)
            .Select(static anchor => anchor.AnchorKey)
            .ToArray();
        Assert.Equal(
            allKeys.Length,
            allKeys.Distinct(StringComparer.Ordinal).Count());

        var canonicalCases = new List<AnchorCase>();
        foreach (var item in prepared)
        {
            var expectedWindow = truth.Windows.Single(expected =>
                expected.Index == item.Window.Index);
            Assert.Equal(
                expectedWindow.Anchors
                    .Select(static anchor => anchor.AnchorKey)
                    .OrderBy(static key => key, StringComparer.Ordinal),
                item.Anchors
                    .Select(static anchor => anchor.AnchorKey)
                    .OrderBy(static key => key, StringComparer.Ordinal));
            Assert.Equal(
                string.Equals(
                    expectedWindow.ExpectedResolution,
                    "no_call",
                    StringComparison.Ordinal),
                item.Anchors.Count == 0);

            foreach (var anchor in item.Anchors)
            {
                var expectedAnchor = expectedWindow.Anchors.Single(expected =>
                    string.Equals(
                        expected.AnchorKey,
                        anchor.AnchorKey,
                        StringComparison.Ordinal));
                Assert.Equal(anchor.AnchorId, expectedAnchor.AnchorId);
                canonicalCases.Add(new AnchorCase(
                    item.Window,
                    anchor,
                    expectedAnchor));
            }
        }

        var orderedCases = OrderCases(canonicalCases, callOrder);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(5)
        };

        var totalWall = Stopwatch.StartNew();
        var results = new List<AnchorResult>(orderedCases.Count);
        for (var position = 0; position < orderedCases.Count; position++)
        {
            var item = orderedCases[position];
            var userPrompt = BuildUserPrompt(
                input.Document,
                item.Window,
                item.Anchor);
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
                        name = "independent_structural_anchor_legitimacy",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                supported = new { type = "boolean" }
                            },
                            required = new[] { "supported" },
                            additionalProperties = false
                        }
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
            var parseValid = TryReadSupported(
                envelope.Completion,
                out var supported);
            var protocolValid = response.StatusCode == HttpStatusCode.OK
                && parseValid
                && string.Equals(
                    envelope.FinishReason,
                    "stop",
                    StringComparison.Ordinal)
                && envelope.Usage.PromptTokens is not null
                && envelope.Usage.PromptTokens.Value + MaxCompletionTokens
                <= ContextSize;
            var correct = parseValid
                && supported == item.Expected.ExpectedSupported;

            results.Add(new AnchorResult(
                position,
                item.Window.Index,
                item.Window.SourceId,
                item.Window.PageStart,
                item.Window.PageEnd,
                item.Anchor,
                item.Expected.ExpectedSupported,
                supported,
                correct,
                item.Expected.Justification,
                (int)response.StatusCode,
                response.StatusCode.ToString(),
                requestBody,
                responseBody,
                envelope.Completion,
                envelope.FinishReason,
                parseValid,
                protocolValid,
                envelope.Usage,
                callWall.ElapsedMilliseconds));

            output.WriteLine(
                $"P.0/{callOrder} anchor {position + 1}/{orderedCases.Count}: "
                + $"window={item.Window.Index}; key={item.Anchor.AnchorKey}; "
                + $"HTTP={(int)response.StatusCode}; finish={envelope.FinishReason}; "
                + $"supported={supported?.ToString() ?? "<invalid>"}; "
                + $"expected={item.Expected.ExpectedSupported}; correct={correct}; "
                + $"wall={callWall.ElapsedMilliseconds} ms");
        }
        totalWall.Stop();

        var resolutions = prepared.Select(item =>
        {
            var expected = truth.Windows.Single(window =>
                window.Index == item.Window.Index);
            var supportedKeys = results
                .Where(result => result.WindowIndex == item.Window.Index
                    && result.Supported == true)
                .Select(static result => result.Anchor.AnchorKey)
                .OrderBy(static key => key, StringComparer.Ordinal)
                .ToArray();
            var actualResolution = item.Anchors.Count == 0
                ? "no_call"
                : supportedKeys.Length switch
                {
                    0 => "abstain",
                    1 => "select",
                    _ => "ambiguous_multiple_supported_anchors"
                };
            var actualAnchorKey = supportedKeys.Length == 1
                ? supportedKeys[0]
                : null;
            var correct = string.Equals(
                    expected.ExpectedResolution,
                    actualResolution,
                    StringComparison.Ordinal)
                && string.Equals(
                    expected.ExpectedAnchorKey,
                    actualAnchorKey,
                    StringComparison.Ordinal);
            return new WindowResolution(
                item.Window.Index,
                expected.ExpectedResolution,
                expected.ExpectedAnchorKey,
                actualResolution,
                actualAnchorKey,
                supportedKeys,
                correct);
        }).ToArray();

        var report = new
        {
            experiment = "EXP-039",
            variant = "P.0",
            generatedAt = DateTimeOffset.Now,
            approval = "TESTE_NON_APPROUVE",
            input = new
            {
                path = inputPath,
                sha256 = inputHash,
                exact = input
            },
            frozenTruth = new
            {
                path = truthPath,
                sha256 = truthHash,
                exact = truth
            },
            configuration = new
            {
                baseUrl,
                model,
                callOrder = callOrder.ToString().ToLowerInvariant(),
                contextSize = ContextSize,
                maxCompletionTokens = MaxCompletionTokens,
                maxWallMilliseconds,
                temperature = 0.0,
                eligibility = "heading_path_level_* or section_title",
                resolution = "0 true => abstain; 1 true => select; >1 true => ambiguous",
                systemPrompt = SystemPrompt
            },
            preflight = new
            {
                windowCount = prepared.Length,
                anchorCount = canonicalCases.Count,
                noCallWindowCount = prepared.Count(static item => item.Anchors.Count == 0),
                uniqueKeyCount = allKeys.Distinct(StringComparer.Ordinal).Count()
            },
            mechanical = new
            {
                llmCallCount = results.Count,
                httpOkCount = results.Count(result => result.StatusCode == 200),
                stopCount = results.Count(result => string.Equals(result.FinishReason, "stop", StringComparison.Ordinal)),
                parseValidCount = results.Count(static result => result.ParseValid),
                protocolValidCount = results.Count(static result => result.ProtocolValid),
                promptTokens = results.Sum(result => result.Usage.PromptTokens ?? 0),
                completionTokens = results.Sum(result => result.Usage.CompletionTokens ?? 0),
                totalTokens = results.Sum(result => result.Usage.TotalTokens ?? 0),
                wallMilliseconds = totalWall.ElapsedMilliseconds,
                withinWallBudget = totalWall.ElapsedMilliseconds <= maxWallMilliseconds
            },
            semantic = new
            {
                anchorExpectedCount = canonicalCases.Count,
                anchorCorrectCount = results.Count(static result => result.Correct),
                resolutionExpectedCount = resolutions.Length,
                resolutionCorrectCount = resolutions.Count(static result => result.Correct),
                ambiguityCount = resolutions.Count(static result => string.Equals(
                    result.ActualResolution,
                    "ambiguous_multiple_supported_anchors",
                    StringComparison.Ordinal)),
                resolutions
            },
            results
        };
        await File.WriteAllTextAsync(
            artifactPath,
            JsonSerializer.Serialize(report, JsonOptions),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifactPath);
        output.WriteLine(
            $"P.0/{callOrder} total: anchors={report.semantic.anchorCorrectCount}/{report.semantic.anchorExpectedCount}; "
            + $"resolutions={report.semantic.resolutionCorrectCount}/{report.semantic.resolutionExpectedCount}; "
            + $"protocol={report.mechanical.protocolValidCount}/{canonicalCases.Count}; "
            + $"wall={report.mechanical.wallMilliseconds} ms");

        Assert.Equal(canonicalCases.Count, report.mechanical.llmCallCount);
        Assert.Equal(canonicalCases.Count, report.mechanical.httpOkCount);
        Assert.Equal(canonicalCases.Count, report.mechanical.stopCount);
        Assert.Equal(canonicalCases.Count, report.mechanical.parseValidCount);
        Assert.Equal(canonicalCases.Count, report.mechanical.protocolValidCount);
        Assert.True(report.mechanical.withinWallBudget);
        Assert.Equal(canonicalCases.Count, report.semantic.anchorCorrectCount);
        Assert.Equal(resolutions.Length, report.semantic.resolutionCorrectCount);
        Assert.Equal(0, report.semantic.ambiguityCount);
    }

    private static IReadOnlyList<AnchorCase> OrderCases(
        IReadOnlyList<AnchorCase> cases,
        AnchorCallOrder order)
        => order switch
        {
            AnchorCallOrder.Reverse => cases.Reverse().ToArray(),
            AnchorCallOrder.Hash => cases
                .OrderBy(static item => item.Anchor.AnchorId, StringComparer.Ordinal)
                .ToArray(),
            _ => cases.ToArray()
        };

    private static string BuildUserPrompt(
        SourceAnchorDocument document,
        SourceAnchorWindow window,
        StructuralAnchor anchor)
        => $"""
Document: {document.DocName}
Path: {document.DocPath}
Source window id: {window.SourceId}
Source pages: {window.PageStart}-{window.PageEnd}
Structural source anchor:
anchorKey: {anchor.AnchorKey}
kind: {anchor.Kind}
text: <<<{anchor.Text}>>>
Source window:
<<<
{window.Text}
>>>
""";

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

            var usage = new TokenUsage();
            if (json.RootElement.TryGetProperty("usage", out var usageElement))
            {
                usage = new TokenUsage(
                    ReadInt32(usageElement, "prompt_tokens"),
                    ReadInt32(usageElement, "completion_tokens"),
                    ReadInt32(usageElement, "total_tokens"));
            }
            return new ResponseEnvelope(completion, finishReason, usage);
        }
        catch (JsonException)
        {
            return new ResponseEnvelope(null, null, new TokenUsage());
        }
    }

    private static bool TryReadSupported(string? completion, out bool? supported)
    {
        supported = null;
        if (string.IsNullOrWhiteSpace(completion))
            return false;
        try
        {
            using var json = JsonDocument.Parse(completion);
            if (!json.RootElement.TryGetProperty("supported", out var value)
                || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }
            supported = value.GetBoolean();
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

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

    private static string RequireEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(name + " is required.");

    private static string EnsureTrailingSlash(string value)
        => value.Trim().TrimEnd('/') + "/";

    private static long ParseMaxWall(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? DefaultMaxWallMilliseconds
            : long.TryParse(value, out var parsed) && parsed > 0
                ? parsed
                : throw new InvalidOperationException(
                    "SAAIA_PHASE2_INDEPENDENT_ANCHOR_MAX_WALL_MS must be a positive integer.");

    private static AnchorCallOrder ParseCallOrder(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "reverse" => AnchorCallOrder.Reverse,
            "hash" => AnchorCallOrder.Hash,
            null or "" or "canonical" => AnchorCallOrder.Canonical,
            _ => throw new InvalidOperationException(
                "SAAIA_PHASE2_INDEPENDENT_ANCHOR_CALL_ORDER must be canonical, reverse, or hash.")
        };

    private enum AnchorCallOrder
    {
        Canonical,
        Reverse,
        Hash
    }

    private sealed record SourceAnchorInput(
        SourceAnchorDocument Document,
        IReadOnlyList<SourceAnchorWindow> Windows);

    private sealed record SourceAnchorDocument(
        Guid DocId,
        Guid RevisionId,
        string DocPath,
        string DocName,
        string SourceHash);

    private sealed record SourceAnchorWindow(
        int Index,
        Guid SourceId,
        int PageStart,
        int PageEnd,
        string? HeadingPath,
        string ChunkType,
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

    private sealed record PreparedWindow(
        SourceAnchorWindow Window,
        IReadOnlyList<StructuralAnchor> Anchors);

    private sealed record AnchorTruth(
        string Status,
        string InputSha256,
        IReadOnlyList<AnchorTruthWindow> Windows);

    private sealed record AnchorTruthWindow(
        int Index,
        string ExpectedResolution,
        string? ExpectedAnchorKey,
        IReadOnlyList<AnchorTruthItem> Anchors);

    private sealed record AnchorTruthItem(
        string AnchorKey,
        string AnchorId,
        bool ExpectedSupported,
        string? Justification);

    private sealed record AnchorCase(
        SourceAnchorWindow Window,
        StructuralAnchor Anchor,
        AnchorTruthItem Expected);

    private sealed record TokenUsage(
        int? PromptTokens = null,
        int? CompletionTokens = null,
        int? TotalTokens = null);

    private sealed record ResponseEnvelope(
        string? Completion,
        string? FinishReason,
        TokenUsage Usage);

    private sealed record AnchorResult(
        int CallPosition,
        int WindowIndex,
        Guid SourceId,
        int PageStart,
        int PageEnd,
        StructuralAnchor Anchor,
        bool ExpectedSupported,
        bool? Supported,
        bool Correct,
        string? Justification,
        int StatusCode,
        string Status,
        string RequestBody,
        string ResponseBody,
        string? Completion,
        string? FinishReason,
        bool ParseValid,
        bool ProtocolValid,
        TokenUsage Usage,
        long WallMilliseconds);

    private sealed record WindowResolution(
        int Index,
        string ExpectedResolution,
        string? ExpectedAnchorKey,
        string ActualResolution,
        string? ActualAnchorKey,
        IReadOnlyList<string> SupportedAnchorKeys,
        bool Correct);
}
