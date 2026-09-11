using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveDocumentOverviewTwoPassWriterProbeTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_document_overview_two_pass_writer_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_DOCUMENT_OVERVIEW_TWO_PASS_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_DOCUMENT_OVERVIEW_TWO_PASS_PROBE to run source-language normalization followed by target-language translation.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_DOCUMENT_OVERVIEW_TWO_PASS_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_DOCUMENT_OVERVIEW_TWO_PASS_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_DOCUMENT_OVERVIEW_TWO_PASS_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_DOCUMENT_OVERVIEW_TWO_PASS_OUTPUT_ARTIFACT"));
        var timeoutSeconds = ReadPositiveInt(
            "SAAIA_DOCUMENT_OVERVIEW_TWO_PASS_TIMEOUT_SECONDS",
            180);
        var sourceMaxTokens = ReadPositiveInt(
            "SAAIA_DOCUMENT_OVERVIEW_TWO_PASS_SOURCE_MAX_TOKENS",
            480);
        var translationMaxTokens = ReadPositiveInt(
            "SAAIA_DOCUMENT_OVERVIEW_TWO_PASS_TRANSLATION_MAX_TOKENS",
            480);
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
            var root = input.RootElement;
            var entries = ReadArray(root, "entries");
            Assert.NotEmpty(entries);
            if (string.Equals(
                    Environment.GetEnvironmentVariable(
                        "SAAIA_DOCUMENT_OVERVIEW_TWO_PASS_READABLE_POOL"),
                    "1",
                    StringComparison.Ordinal))
            {
                var readable = entries.Where(IsMechanicallyReadable).ToArray();
                var count = ReadPositiveInt(
                    "SAAIA_DOCUMENT_OVERVIEW_TWO_PASS_COUNT",
                    7);
                Assert.True(readable.Length >= count);
                entries = SelectEvenly(readable, count);
            }

            var sourcePrompt = BuildSourceFactPrompt(entries);
            var sourceCall = await CompleteAsync(
                liveSettings.LlmBaseUrl,
                liveSettings.ModelId,
                "You normalize documentary evidence into atomic facts in the evidence language. Use only the canonical source. Never translate, invent, generalize or omit a technical qualifier.",
                sourcePrompt,
                maxTokens: sourceMaxTokens,
                cts.Token);
            Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "document_overview.two_pass_source_fact_translation",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_source_captured_before_contract_validation",
                        inputArtifact,
                        modelId = liveSettings.ModelId,
                        sourcePass = sourceCall
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);
            var sourceFacts = ParseOrderedLines(
                sourceCall.Content,
                entries.Length);

            var translationPrompt = BuildTranslationPrompt(sourceFacts);
            var translationCall = await CompleteAsync(
                liveSettings.LlmBaseUrl,
                liveSettings.ModelId,
                "Tu traduis des faits documentaires normalises vers un francais professionnel. Preserve exactement le sujet, l'action, l'objet, la modalite, les nombres, les unites, les negations, les qualificatifs et les conditions. Ne resume pas et n'ajoute rien.",
                translationPrompt,
                maxTokens: translationMaxTokens,
                cts.Token);
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "document_overview.two_pass_source_fact_translation",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_translation_captured_before_contract_validation",
                        inputArtifact,
                        modelId = liveSettings.ModelId,
                        sourcePass = sourceCall,
                        sourceFacts,
                        translationPass = translationCall
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);
            var translatedFacts = ParseOrderedLines(
                translationCall.Content,
                entries.Length);

            var report = new
            {
                probe = "document_overview.two_pass_source_fact_translation",
                capturedAtUtc = DateTimeOffset.UtcNow,
                inputArtifact,
                modelId = liveSettings.ModelId,
                sourcePass = new
                {
                    sourceCall.ElapsedMs,
                    sourceCall.PromptTokens,
                    sourceCall.CompletionTokens,
                    sourceCall.FinishReason,
                    promptCharacters = sourcePrompt.Length,
                    raw = sourceCall.Content,
                    facts = sourceFacts
                },
                translationPass = new
                {
                    translationCall.ElapsedMs,
                    translationCall.PromptTokens,
                    translationCall.CompletionTokens,
                    translationCall.FinishReason,
                    promptCharacters = translationPrompt.Length,
                    raw = translationCall.Content,
                    facts = translatedFacts
                }
            };
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("source_elapsed_ms=" + sourceCall.ElapsedMs);
            output.WriteLine("source_prompt_tokens=" + sourceCall.PromptTokens);
            output.WriteLine("source_completion_tokens=" + sourceCall.CompletionTokens);
            output.WriteLine("translation_elapsed_ms=" + translationCall.ElapsedMs);
            output.WriteLine("translation_prompt_tokens=" + translationCall.PromptTokens);
            output.WriteLine("translation_completion_tokens=" + translationCall.CompletionTokens);
            output.WriteLine("source_facts=" + string.Join(" | ", sourceFacts));
            output.WriteLine("translated_facts=" + string.Join(" | ", translatedFacts));
            output.WriteLine("artifact=" + outputArtifact);
        }
        finally
        {
            manager.Stop();
        }
    }

    [Fact]
    public async Task Live_document_overview_structurally_readable_pool_writer_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_DOCUMENT_OVERVIEW_READABLE_POOL_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_DOCUMENT_OVERVIEW_READABLE_POOL_PROBE to run the one-pass writer over mechanically readable evidence.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_DOCUMENT_OVERVIEW_READABLE_POOL_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_DOCUMENT_OVERVIEW_READABLE_POOL_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_DOCUMENT_OVERVIEW_READABLE_POOL_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_DOCUMENT_OVERVIEW_READABLE_POOL_OUTPUT_ARTIFACT"));
        var timeoutSeconds = ReadPositiveInt(
            "SAAIA_DOCUMENT_OVERVIEW_READABLE_POOL_TIMEOUT_SECONDS",
            180);
        var requestedCount = ReadPositiveInt(
            "SAAIA_DOCUMENT_OVERVIEW_READABLE_POOL_COUNT",
            8);
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
            var readable = entries
                .Where(IsMechanicallyReadable)
                .ToArray();
            Assert.True(
                readable.Length >= requestedCount,
                $"Only {readable.Length} mechanically readable entries remain.");
            var selected = SelectEvenly(readable, requestedCount);
            var prompt = BuildReadablePoolPrompt(selected);
            var call = await CompleteAsync(
                liveSettings.LlmBaseUrl,
                liveSettings.ModelId,
                "You write source-grounded technical document overviews. Use only each assigned canonical evidence item. Preserve its actors, actions, objects, modality, numbers, units, qualifiers, scope and conditions. Never invent, merge or generalize.",
                prompt,
                maxTokens: 320,
                cts.Token);

            Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "document_overview.mechanically_readable_pool_writer",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_writer_captured_before_contract_validation",
                        inputArtifact,
                        modelId = liveSettings.ModelId,
                        inputCount = entries.Length,
                        readableCount = readable.Length,
                        selectedPages = selected.Select(
                            static entry => ReadInt(entry, "PageStart")),
                        promptCharacters = prompt.Length,
                        writer = call
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);
            var facts = ParseOrderedLines(call.Content, selected.Length);
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "document_overview.mechanically_readable_pool_writer",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "contract_validated",
                        inputArtifact,
                        modelId = liveSettings.ModelId,
                        inputCount = entries.Length,
                        readableCount = readable.Length,
                        selected = selected.Select(
                            (entry, index) => new
                            {
                                evidenceId = "E" + (index + 1),
                                page = ReadInt(entry, "PageStart"),
                                chunkId = ReadString(entry, "ChunkId"),
                                text = ReadString(entry, "Text")
                            }),
                        writer = new
                        {
                            call.ElapsedMs,
                            call.PromptTokens,
                            call.CompletionTokens,
                            call.FinishReason,
                            raw = call.Content,
                            facts
                        }
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("elapsed_ms=" + call.ElapsedMs);
            output.WriteLine("prompt_tokens=" + call.PromptTokens);
            output.WriteLine("completion_tokens=" + call.CompletionTokens);
            output.WriteLine("selected_pages=" + string.Join(
                ",",
                selected.Select(static entry => ReadInt(entry, "PageStart"))));
            output.WriteLine("artifact=" + outputArtifact);
        }
        finally
        {
            manager.Stop();
        }
    }

    private static bool IsMechanicallyReadable(JsonElement entry)
    {
        var text = ReadString(entry, "Text");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var dotLeaderCount = Regex.Matches(text, @"\.{4,}").Count;
        var columnSeparatorCount = text.Count(static character => character == '|');
        var bulletCount = text.Count(static character => character == '•');
        var sentenceStopCount = Regex.Matches(text, @"[.!?](?:\s|$)").Count;
        return dotLeaderCount < 3
               && columnSeparatorCount < 3
               && bulletCount < 8
               && sentenceStopCount > 0;
    }

    private static JsonElement[] SelectEvenly(
        IReadOnlyList<JsonElement> entries,
        int count)
    {
        Assert.True(entries.Count >= count);
        if (entries.Count == count)
            return entries.Select(static entry => entry.Clone()).ToArray();

        return Enumerable.Range(0, count)
            .Select(index => (int)Math.Round(
                index * (entries.Count - 1d) / (count - 1d),
                MidpointRounding.AwayFromZero))
            .Distinct()
            .Select(index => entries[index].Clone())
            .ToArray();
    }

    private static string BuildReadablePoolPrompt(
        IReadOnlyList<JsonElement> entries)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_READABLE_POOL_WRITER");
        prompt.Append("Produis exactement ").Append(entries.Count)
            .AppendLine(" lignes en francais, dans l'ordre fourni.");
        prompt.AppendLine("La ligne i utilise uniquement la preuve [Ei] et se termine par le marqueur exact [Ei].");
        prompt.AppendLine("Ecris un fait autonome utile a un apercu, normalement 12 a 24 mots et jusqu'a 36 si la fidelite l'exige.");
        prompt.AppendLine("Conserve le sujet grammatical, l'action, l'objet, la modalite, les nombres, les unites, les negations, les qualificatifs techniques, la portee et les conditions utiles.");
        prompt.AppendLine("Pour shall ou must, le sujet grammatical de la source reste le porteur de l'obligation en francais.");
        prompt.AppendLine("N'ajoute aucun titre, label, commentaire, introduction ou conclusion.");
        prompt.AppendLine("CANONICAL_EVIDENCE:");
        for (var index = 0; index < entries.Count; index++)
        {
            prompt.Append("[E").Append(index + 1).Append("] page=")
                .Append(ReadInt(entries[index], "PageStart"))
                .Append(" source=")
                .AppendLine(Compact(
                    ReadString(entries[index], "Text"),
                    1400));
        }

        return prompt.ToString();
    }

    private static string BuildSourceFactPrompt(
        IReadOnlyList<JsonElement> entries)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_SOURCE_FACTS");
        prompt.Append("Produce exactly ").Append(entries.Count)
            .AppendLine(" lines in the same language as the canonical evidence.");
        prompt.AppendLine("Line i uses only evidence [Ei] and ends with the exact marker [Ei].");
        prompt.AppendLine("Write one self-contained atomic fact per line, normally 12 to 24 words and up to 36 only when fidelity requires it.");
        prompt.AppendLine("Preserve every actor, action, object, modality, number, unit, negation, technical qualifier, scope and condition.");
        prompt.AppendLine("Normalize OCR order only when the relation is explicit. If a table is interleaved, state only the relation directly visible without merging columns.");
        prompt.AppendLine("No title, translation, commentary or text outside the required lines.");
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

    private static string BuildTranslationPrompt(
        IReadOnlyList<string> sourceFacts)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_FACT_TRANSLATION");
        prompt.Append("Traduis exactement les ").Append(sourceFacts.Count)
            .AppendLine(" lignes ci-dessous en francais professionnel.");
        prompt.AppendLine("Conserve l'ordre et le marqueur [Ei] exact de chaque ligne.");
        prompt.AppendLine("Ne resume, ne generalise et n'omets aucun sujet, action, objet, modalite, nombre, unite, negation, qualificatif technique, portee ou condition.");
        prompt.AppendLine("Utilise un equivalent francais naturel et precis; n'utilise pas de mot anglais lorsqu'un equivalent francais courant existe.");
        prompt.AppendLine("Retourne uniquement les lignes traduites, sans titre ni commentaire.");
        prompt.AppendLine("SOURCE_FACTS:");
        foreach (var fact in sourceFacts)
            prompt.AppendLine(fact);
        return prompt.ToString();
    }

    private static string[] ParseOrderedLines(
        string raw,
        int expectedCount)
    {
        var lines = (raw ?? string.Empty).Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        Assert.Equal(expectedCount, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            Assert.Matches(
                new Regex(
                    $@"\[E{index + 1}\][.!?]?\s*$",
                    RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant),
                lines[index]);
        }

        return lines;
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
            ? property.EnumerateArray().Select(static item => item.Clone()).ToArray()
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

    private sealed record CompletionResult(
        string Content,
        int PromptTokens,
        int CompletionTokens,
        string FinishReason,
        long ElapsedMs);
}
