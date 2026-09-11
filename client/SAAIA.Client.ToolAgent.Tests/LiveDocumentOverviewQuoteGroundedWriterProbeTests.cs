using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveDocumentOverviewQuoteGroundedWriterProbeTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_document_overview_quote_grounded_writer_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_DOCUMENT_OVERVIEW_QUOTE_GROUNDED_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_DOCUMENT_OVERVIEW_QUOTE_GROUNDED_PROBE to run the one-pass exact-quote writer probe.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_DOCUMENT_OVERVIEW_QUOTE_GROUNDED_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_DOCUMENT_OVERVIEW_QUOTE_GROUNDED_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_DOCUMENT_OVERVIEW_QUOTE_GROUNDED_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_DOCUMENT_OVERVIEW_QUOTE_GROUNDED_OUTPUT_ARTIFACT"));
        var timeoutSeconds = ReadPositiveInt(
            "SAAIA_DOCUMENT_OVERVIEW_QUOTE_GROUNDED_TIMEOUT_SECONDS",
            180);
        var settings = AppSettings.Load();
        var liveSettings = settings.Clone();
        liveSettings.UseLocalLlm = true;
        liveSettings.ManageLocalLlmProcess = true;
        var manager = new LlamaCppProcessManager();
        manager.SetIdleStopSuppressionProvider(static () => true);

        try
        {
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(timeoutSeconds));
            var (ok, message) = await manager.EnsureRunningAsync(
                liveSettings,
                cts.Token);
            Assert.True(ok, message);

            using var input = JsonDocument.Parse(
                await File.ReadAllTextAsync(inputArtifact, cts.Token));
            var entries = ReadArray(input.RootElement, "entries");
            Assert.NotEmpty(entries);

            var prompt = BuildPrompt(entries);
            var call = await CompleteAsync(
                liveSettings.LlmBaseUrl,
                liveSettings.ModelId,
                "You are a source-grounded multilingual technical writer. For every candidate, first select a short verbatim quote from its assigned canonical evidence, then write only the French fact supported by that quote. Never invent, generalize, merge evidence, or alter the quote.",
                prompt,
                maxTokens: 480,
                cts.Token);

            Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "document_overview.quote_grounded_one_pass",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_writer_captured_before_contract_validation",
                        inputArtifact,
                        modelId = liveSettings.ModelId,
                        promptCharacters = prompt.Length,
                        writer = call
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            var candidates = ParseAndValidate(call.Content, entries);
            var report = new
            {
                probe = "document_overview.quote_grounded_one_pass",
                capturedAtUtc = DateTimeOffset.UtcNow,
                stage = "contract_validated",
                inputArtifact,
                modelId = liveSettings.ModelId,
                promptCharacters = prompt.Length,
                writer = new
                {
                    call.ElapsedMs,
                    call.PromptTokens,
                    call.CompletionTokens,
                    call.FinishReason,
                    raw = call.Content
                },
                candidates
            };
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("elapsed_ms=" + call.ElapsedMs);
            output.WriteLine("prompt_tokens=" + call.PromptTokens);
            output.WriteLine("completion_tokens=" + call.CompletionTokens);
            output.WriteLine("finish_reason=" + call.FinishReason);
            output.WriteLine("artifact=" + outputArtifact);
        }
        finally
        {
            manager.Stop();
        }
    }

    private static string BuildPrompt(IReadOnlyList<JsonElement> entries)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_QUOTE_GROUNDED_WRITER");
        prompt.Append("Produce exactly ").Append(entries.Count)
            .AppendLine(" lines in the supplied order.");
        prompt.AppendLine("Required line syntax: [Ei] QUOTE=<verbatim source span> || FR=<French fact> [Ei]");
        prompt.AppendLine("The opening and closing EvidenceId must match the assigned line exactly.");
        prompt.AppendLine("QUOTE must be one short, consecutive, character-for-character span copied from that evidence only; do not add quotes, ellipses or corrections.");
        prompt.AppendLine("Choose the smallest span sufficient to support one useful overview fact, normally 8 to 30 words; never enumerate a whole multi-row table.");
        prompt.AppendLine("FR must express only the fact supported by QUOTE in natural professional French.");
        prompt.AppendLine("Preserve the grammatical subject, action, object, modality, numbers, units, negation, technical qualifiers, scope and conditions present in QUOTE.");
        prompt.AppendLine("Do not use English in FR when a normal French equivalent exists. Do not add a title, label, strength, introduction, conclusion or commentary.");
        prompt.AppendLine("CANONICAL_EVIDENCE:");
        for (var index = 0; index < entries.Count; index++)
        {
            prompt.Append("[E").Append(index + 1).Append("] page=")
                .Append(ReadInt(entries[index], "PageStart"))
                .Append(" source=")
                .AppendLine(Compact(
                    ReadString(entries[index], "Text"),
                    1800));
        }

        return prompt.ToString();
    }

    private static Candidate[] ParseAndValidate(
        string raw,
        IReadOnlyList<JsonElement> entries)
    {
        var lines = (raw ?? string.Empty).Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        Assert.Equal(entries.Count, lines.Length);
        var candidates = new List<Candidate>(entries.Count);

        for (var index = 0; index < entries.Count; index++)
        {
            var evidenceId = "E" + (index + 1);
            var prefix = "[" + evidenceId + "] QUOTE=";
            var suffix = " [" + evidenceId + "]";
            Assert.StartsWith(prefix, lines[index]);
            Assert.EndsWith(suffix, lines[index]);
            var body = lines[index][prefix.Length..^suffix.Length];
            const string separator = " || FR=";
            var separatorIndex = body.IndexOf(
                separator,
                StringComparison.Ordinal);
            Assert.True(
                separatorIndex > 0,
                "Missing quote/French separator for " + evidenceId + ".");
            var quote = body[..separatorIndex].Trim();
            var french = body[(separatorIndex + separator.Length)..].Trim();
            Assert.NotEmpty(quote);
            Assert.NotEmpty(french);

            var canonicalSource = Compact(
                ReadString(entries[index], "Text"),
                1800);
            Assert.Contains(quote, canonicalSource, StringComparison.Ordinal);
            candidates.Add(new Candidate(
                evidenceId,
                ReadInt(entries[index], "PageStart"),
                quote,
                french));
        }

        return candidates.ToArray();
    }

    private static async Task<CompletionResult> CompleteAsync(
        string baseUrl,
        string modelId,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken ct)
    {
        var payload = new
        {
            model = modelId,
            stream = false,
            temperature = 0.1,
            top_p = 0.9,
            max_tokens = maxTokens,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var watch = Stopwatch.StartNew();
        using var response = await http.PostAsJsonAsync(
            baseUrl.TrimEnd('/') + "/chat/completions",
            payload,
            ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        watch.Stop();
        response.EnsureSuccessStatusCode();
        using var completion = JsonDocument.Parse(responseBody);
        var choice = completion.RootElement.GetProperty("choices")[0];
        return new CompletionResult(
            choice.GetProperty("message").GetProperty("content")
                .GetString()?.Trim() ?? string.Empty,
            ReadNestedInt(completion.RootElement, "usage", "prompt_tokens"),
            ReadNestedInt(completion.RootElement, "usage", "completion_tokens"),
            ReadString(choice, "finish_reason"),
            watch.ElapsedMilliseconds);
    }

    private static JsonElement[] ReadArray(
        JsonElement value,
        string propertyName)
        => TryGetPropertyIgnoreCase(value, propertyName, out var property)
           && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Select(static item => item.Clone()).ToArray()
            : [];

    private static string ReadString(
        JsonElement value,
        string propertyName)
        => TryGetPropertyIgnoreCase(value, propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(
        JsonElement value,
        string propertyName)
        => TryGetPropertyIgnoreCase(value, propertyName, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static int ReadNestedInt(
        JsonElement value,
        string objectName,
        string propertyName)
        => value.TryGetProperty(objectName, out var nested)
           && nested.ValueKind == JsonValueKind.Object
            ? ReadInt(nested, propertyName)
            : 0;

    private static bool TryGetPropertyIgnoreCase(
        JsonElement value,
        string propertyName,
        out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in value.EnumerateObject())
            {
                if (string.Equals(
                        candidate.Name,
                        propertyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static string Compact(string value, int maxCharacters)
    {
        var compact = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        return compact.Length <= maxCharacters
            ? compact
            : compact[..maxCharacters].TrimEnd() + "...";
    }

    private static int ReadPositiveInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
           && parsed > 0
            ? parsed
            : fallback;

    private static string Require(string name, string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("Missing " + name + ".")
            : value.Trim();

    private sealed record Candidate(
        string EvidenceId,
        int Page,
        string Quote,
        string French);

    private sealed record CompletionResult(
        string Content,
        int PromptTokens,
        int CompletionTokens,
        string FinishReason,
        long ElapsedMs);
}
