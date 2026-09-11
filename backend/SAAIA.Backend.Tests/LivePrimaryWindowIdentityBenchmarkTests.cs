using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Backend.Tests;

public sealed class LivePrimaryWindowIdentityBenchmarkTests(
    ITestOutputHelper output)
{
    private const int ContextSize = 4096;
    private const int MaxCompletionTokens = 180;
    private const long MaxTotalWallMilliseconds = 240_000;

    private const string SystemPrompt = """
You identify the single primary autonomous retrievable identity represented by
one source window. Return JSON only and obey the provided schema. Return
items=[] when the window does not support one primary identity. Otherwise
return exactly one item: the highest-level concrete subject that best identifies
the window as a whole and that a user could intentionally retrieve. Do not
decompose it into steps, ingredients, parameters, secondary parts, or local
actions. The title must identify the primary subject without surrounding
layout. Do not turn navigation, layout labels, quantities, durations, costs,
difficulty labels, dangling fragments, isolated instructions, or OCR noise into
the primary identity. Use only the source window. Include exactly one short
sourceText copied verbatim from that window and up to three short retrieval
signals. Do not output page numbers; provenance pages are attached mechanically
from the immutable source window. Use the source document language.
""";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly JsonElement ResponseSchema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "maxItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string", "maxLength": 120 },
                  "signals": {
                    "type": "array",
                    "maxItems": 3,
                    "items": { "type": "string", "maxLength": 80 }
                  },
                  "evidence": {
                    "type": "array",
                    "minItems": 1,
                    "maxItems": 1,
                    "items": {
                      "type": "object",
                      "properties": {
                        "sourceText": { "type": "string", "maxLength": 180 }
                      },
                      "required": ["sourceText"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["title", "signals", "evidence"],
                "additionalProperties": false
              }
            }
          },
          "required": ["items"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    [Fact]
    public async Task Live_qwen3_selects_one_primary_window_identity_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_PHASE2_PRIMARY_WINDOW_IDENTITY_BENCHMARK"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_PHASE2_PRIMARY_WINDOW_IDENTITY_BENCHMARK=1 to run EXP-034/N.2.");
            return;
        }

        var inputPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_WINDOWED_SOURCE_INPUT_PATH"));
        var artifactPath = Path.GetFullPath(RequireEnvironment(
            "SAAIA_PHASE2_PRIMARY_WINDOW_ARTIFACT"));
        var baseUrl = EnsureTrailingSlash(RequireEnvironment(
            "SAAIA_VALIDATION_LLM_BASE_URL"));
        var model = RequireEnvironment("SAAIA_VALIDATION_LLM_MODEL");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

        var inputBytes = await File.ReadAllBytesAsync(inputPath);
        var inputHash = Convert.ToHexString(SHA256.HashData(inputBytes))
            .ToLowerInvariant();
        var input = JsonSerializer.Deserialize<WindowedSourceInput>(
            inputBytes,
            JsonOptions)
            ?? throw new InvalidOperationException("EXP-034 input is invalid.");

        Assert.Equal(8, input.Windows.Count);
        Assert.Equal(
            "c79c57c72e24a8c2389584c4f091387480dc8218768fb43b2835ff1a661a9729",
            inputHash);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(8)
        };

        var totalWall = Stopwatch.StartNew();
        var results = new List<WindowResult>(input.Windows.Count);
        for (var index = 0; index < input.Windows.Count; index++)
        {
            var window = input.Windows[index];
            var userPrompt = BuildUserPrompt(input.Document, window);
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
                        name = "primary_window_identity",
                        strict = true,
                        schema = ResponseSchema
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
            var parseValid = TryReadItems(envelope.Completion, out var proposedItems);
            var groundedItems = proposedItems
                .Where(item => IsMechanicallyGrounded(item, window.Text))
                .Select(item => new GroundedWindowItem(
                    item.Title,
                    item.Signals,
                    item.Evidence,
                    input.Document.RevisionId,
                    input.Document.SourceHash,
                    window.SourceKind,
                    window.SourceId,
                    window.PageStart,
                    window.PageEnd))
                .ToArray();
            var protocolValid = response.StatusCode == HttpStatusCode.OK
                && parseValid
                && proposedItems.Count <= 1
                && proposedItems.All(item => IsMechanicallyGrounded(item, window.Text))
                && string.Equals(envelope.FinishReason, "stop", StringComparison.Ordinal)
                && envelope.Usage.PromptTokens is not null
                && envelope.Usage.PromptTokens.Value + MaxCompletionTokens <= ContextSize;

            results.Add(new WindowResult(
                index + 1,
                window,
                (int)response.StatusCode,
                response.StatusCode.ToString(),
                requestBody,
                responseBody,
                envelope.Completion,
                envelope.FinishReason,
                parseValid,
                protocolValid,
                proposedItems,
                groundedItems,
                envelope.Usage,
                callWall.ElapsedMilliseconds));

            output.WriteLine(
                $"N.2 window {index + 1}/8: HTTP {(int)response.StatusCode}; "
                + $"finish={envelope.FinishReason}; parse={parseValid}; "
                + $"items={proposedItems.Count}; grounded={groundedItems.Length}; "
                + $"prompt={envelope.Usage.PromptTokens}; "
                + $"completion={envelope.Usage.CompletionTokens}; "
                + $"wall={callWall.ElapsedMilliseconds} ms");
        }
        totalWall.Stop();

        var report = new
        {
            experiment = "EXP-034",
            variant = "N.2",
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
                contextSize = ContextSize,
                maxCompletionTokens = MaxCompletionTokens,
                maxTotalWallMilliseconds = MaxTotalWallMilliseconds,
                temperature = 0.0,
                systemPrompt = SystemPrompt,
                responseSchema = ResponseSchema
            },
            mechanical = new
            {
                windowCount = results.Count,
                httpOkCount = results.Count(result => result.StatusCode == 200),
                stopCount = results.Count(result => string.Equals(result.FinishReason, "stop", StringComparison.Ordinal)),
                parseValidCount = results.Count(result => result.ParseValid),
                protocolValidCount = results.Count(result => result.ProtocolValid),
                proposedItemCount = results.Sum(result => result.ProposedItems.Count),
                groundedItemCount = results.Sum(result => result.GroundedItems.Count),
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
                requiredClearTitles = new[]
                {
                    "Bœuf bourguignon",
                    "Couscous royal",
                    "Cassoulet toulousain",
                    "Gigot d'agneau de sept heures",
                    "Ile flottante",
                    "Millefeuille aux framboises",
                    "Tarte au citron meringuée"
                },
                instruction =
                    "Le passage exige 7/7 identités principales, zéro sous-étape/fausse unité et une fenêtre OCR vide ou défendable."
            }
        };
        await File.WriteAllTextAsync(
            artifactPath,
            JsonSerializer.Serialize(report, JsonOptions),
            CancellationToken.None);

        output.WriteLine("Artifact: " + artifactPath);
        output.WriteLine(
            $"N.2 total: protocol={report.mechanical.protocolValidCount}/8; "
            + $"items={report.mechanical.proposedItemCount}; "
            + $"wall={report.mechanical.wallMilliseconds} ms; "
            + $"withinBudget={report.mechanical.withinWallBudget}");

        Assert.Equal(8, report.mechanical.httpOkCount);
        Assert.Equal(8, report.mechanical.stopCount);
        Assert.Equal(8, report.mechanical.parseValidCount);
        Assert.Equal(8, report.mechanical.protocolValidCount);
        Assert.True(report.mechanical.withinWallBudget);
    }

    private static string BuildUserPrompt(
        WindowedDocument document,
        SourceWindow window)
        => $"""
Document: {document.DocName}
Path: {document.DocPath}
Source window id: {window.SourceId}
Source pages: {window.PageStart}-{window.PageEnd}
Source window:
<<<
{window.Text}
>>>
""";

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

    private static bool TryReadItems(
        string? completion,
        out IReadOnlyList<ProposedWindowItem> items)
    {
        items = Array.Empty<ProposedWindowItem>();
        if (string.IsNullOrWhiteSpace(completion))
            return false;

        try
        {
            using var json = JsonDocument.Parse(completion);
            if (!json.RootElement.TryGetProperty("items", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = JsonSerializer.Deserialize<ProposedWindowItem[]>(
                array.GetRawText(),
                JsonOptions);
            if (parsed is null)
                return false;

            items = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsMechanicallyGrounded(
        ProposedWindowItem item,
        string sourceText)
        => !string.IsNullOrWhiteSpace(item.Title)
           && item.Evidence.Count == 1
           && !string.IsNullOrWhiteSpace(item.Evidence[0].SourceText)
           && sourceText.Contains(
               item.Evidence[0].SourceText,
               StringComparison.Ordinal);

    private static int? ReadInt32(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private sealed record WindowedSourceInput(
        WindowedDocument Document,
        WindowSelection Selection,
        IReadOnlyList<SourceWindow> Windows);

    private sealed record WindowedDocument(
        Guid DocId,
        Guid RevisionId,
        string DocPath,
        string DocName,
        string? Category,
        int PageCount,
        int IndexedVersion,
        string SourceHash);

    private sealed record WindowSelection(string Algorithm, int WindowCount);

    private sealed record SourceWindow(
        string SourceKind,
        Guid SourceId,
        int Ordinal,
        int PageStart,
        int PageEnd,
        int TokenCount,
        string ContentRole,
        double NavigationScore,
        double ContentDensityScore,
        string Text);

    private sealed record ProposedWindowItem(
        string Title,
        IReadOnlyList<string> Signals,
        IReadOnlyList<ProposedEvidence> Evidence);

    private sealed record ProposedEvidence(string SourceText);

    private sealed record GroundedWindowItem(
        string Title,
        IReadOnlyList<string> Signals,
        IReadOnlyList<ProposedEvidence> Evidence,
        Guid RevisionId,
        string SourceHash,
        string SourceKind,
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
        SourceWindow Window,
        int StatusCode,
        string Status,
        string RequestBody,
        string ResponseBody,
        string? Completion,
        string? FinishReason,
        bool ParseValid,
        bool ProtocolValid,
        IReadOnlyList<ProposedWindowItem> ProposedItems,
        IReadOnlyList<GroundedWindowItem> GroundedItems,
        TokenUsage Usage,
        long WallMilliseconds);
}
