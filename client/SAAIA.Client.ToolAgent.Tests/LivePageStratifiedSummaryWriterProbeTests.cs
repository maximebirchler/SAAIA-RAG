using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LivePageStratifiedSummaryWriterProbeTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_page_stratified_summary_writer_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_PAGE_STRATIFIED_SUMMARY_WRITER_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_PAGE_STRATIFIED_SUMMARY_WRITER_PROBE to run one compact semantic writer call.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = NormalizeLlmHost(Require(
            "LLM base URL",
            FirstNonBlank(
                Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
                settings.LlmBaseUrl)));
        var llmModel = Require(
            "LLM model",
            FirstNonBlank(
                Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
                settings.ModelId));
        var overviewArtifact = Require(
            "SAAIA_PAGE_STRATIFIED_OVERVIEW_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_PAGE_STRATIFIED_OVERVIEW_ARTIFACT"));
        var writerArtifact = Require(
            "SAAIA_PAGE_STRATIFIED_WRITER_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_PAGE_STRATIFIED_WRITER_ARTIFACT"));
        var maxTokens = ReadPositiveInt(
            "SAAIA_PAGE_STRATIFIED_WRITER_MAX_TOKENS",
            256);
        Directory.CreateDirectory(Path.GetDirectoryName(writerArtifact)!);

        using var overview = JsonDocument.Parse(
            await File.ReadAllTextAsync(overviewArtifact));
        var root = overview.RootElement;
        var docPath = ReadString(root, "docPath");
        var entries = root.TryGetProperty("entries", out var entryArray)
                      && entryArray.ValueKind == JsonValueKind.Array
            ? entryArray.EnumerateArray().Select(static item => item.Clone()).ToArray()
            : [];
        Assert.NotEmpty(entries);

        var prompt = BuildPrompt(docPath, entries);
        var payload = new
        {
            model = llmModel,
            stream = false,
            temperature = 0.1,
            top_p = 0.9,
            max_tokens = maxTokens,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "Tu es le redacteur semantique final. Reponds uniquement en francais, meme lorsque les extraits sont en anglais. Utilise uniquement les extraits canoniques fournis. N'invente rien et ne transforme jamais un exemple informatif en obligation."
                },
                new { role = "user", content = prompt }
            }
        };

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(ReadPositiveInt(
                "SAAIA_PAGE_STRATIFIED_WRITER_TIMEOUT_SECONDS",
                180)));
        var watch = Stopwatch.StartNew();
        using var response = await http.PostAsJsonAsync(
            llmBaseUrl.TrimEnd('/') + "/v1/chat/completions",
            payload,
            cts.Token);
        var responseBody = await response.Content.ReadAsStringAsync(cts.Token);
        watch.Stop();
        response.EnsureSuccessStatusCode();

        using var completion = JsonDocument.Parse(responseBody);
        var rawAnswer = completion.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?.Trim() ?? string.Empty;
        var promptTokens = ReadNestedInt(completion.RootElement, "usage", "prompt_tokens");
        var completionTokens = ReadNestedInt(completion.RootElement, "usage", "completion_tokens");
        var validation = ValidateAndRenderCandidatePoints(
            rawAnswer,
            entries,
            requiredPointCount: 7);
        var detectedAnswerLanguage = LocalizedStrings.DetectLanguage(
            rawAnswer,
            "fr");

        var report = new
        {
            probe = "documents.page_stratified_overview.single_writer",
            capturedAtUtc = DateTimeOffset.UtcNow,
            llmBaseUrl,
            llmModel,
            overviewArtifact,
            elapsedMs = watch.ElapsedMilliseconds,
            maxTokens,
            promptCharacters = prompt.Length,
            promptTokens,
            completionTokens,
            candidatePointCount = validation.CandidatePointCount,
            validCandidateCount = validation.ValidCandidateCount,
            rejectedCandidateCount = validation.Rejections.Count,
            rejectedCandidates = validation.Rejections,
            detectedAnswerLanguage,
            citedDistinctSourceCount = validation.CitedSourceNumbers.Length,
            citedSourceNumbers = validation.CitedSourceNumbers,
            rawAnswer,
            answer = validation.RenderedAnswer
        };
        await File.WriteAllTextAsync(
            writerArtifact,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        output.WriteLine("elapsed_ms=" + watch.ElapsedMilliseconds);
        output.WriteLine("prompt_characters=" + prompt.Length);
        output.WriteLine("prompt_tokens=" + promptTokens);
        output.WriteLine("completion_tokens=" + completionTokens);
        output.WriteLine("candidate_points=" + validation.CandidatePointCount);
        output.WriteLine("valid_candidates=" + validation.ValidCandidateCount);
        output.WriteLine("rejected_candidates=" + validation.Rejections.Count);
        output.WriteLine("detected_answer_language=" + detectedAnswerLanguage);
        output.WriteLine("distinct_cited_sources=" + validation.CitedSourceNumbers.Length);
        output.WriteLine("raw_answer=" + rawAnswer);
        output.WriteLine("answer=" + validation.RenderedAnswer);
        output.WriteLine("artifact=" + writerArtifact);

        Assert.False(string.IsNullOrWhiteSpace(rawAnswer));
        Assert.Equal("fr", detectedAnswerLanguage);
        Assert.True(validation.ValidCandidateCount >= 7);
        Assert.Equal(7, Regex.Matches(
            validation.RenderedAnswer,
            @"(?m)^\s*[1-7][\.)]\s+\[(?:Exigence|Recommandation|Contexte)\]").Count);
        Assert.Equal(7, validation.CitedSourceNumbers.Length);
    }

    private static string BuildPrompt(
        string docPath,
        IReadOnlyList<JsonElement> entries)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("DEMANDE:");
        prompt.AppendLine("Je dois justifier une exigence : resume ce document en exactement 7 points utiles pour quelqu'un qui doit prendre une decision.");
        prompt.AppendLine();
        prompt.AppendLine("CONTRAT DE SORTIE:");
        prompt.AppendLine("- TACHE TECHNIQUE PRIORITAIRE: produis exactement 8 lignes candidates, une pour chacun des extraits S1 a S8, dans cet ordre.");
        prompt.AppendLine("- La ligne 1 utilise uniquement [S1], la ligne 2 uniquement [S2], et ainsi de suite jusqu'a la ligne 8 avec [S8].");
        prompt.AppendLine("- Ce brouillon prive contient 8 candidats; le verificateur en retirera un pour livrer les 7 points demandes.");
        prompt.AppendLine("- Toutes les phrases doivent etre en francais, meme si les extraits sources sont en anglais.");
        prompt.AppendLine("- Une seule phrase factuelle et decisionnelle par ligne, 14 mots maximum.");
        prompt.AppendLine("- Utilise exactement un extrait distinct par ligne et termine par son unique marqueur, par exemple [S2].");
        prompt.AppendLine("- Chaque ligne doit etre directement soutenue par son extrait. Ecarte les extraits faibles plutot que de generaliser.");
        prompt.AppendLine("- Ne cite jamais le sommaire seul comme preuve d'une exigence.");
        prompt.AppendLine("- Ecris 'doit', 'obligatoire', 'exige' ou 'requis' uniquement si l'extrait contient shall ou must.");
        prompt.AppendLine("- Traduis may, should, recommended, encouraged et usually sans les renforcer en obligation.");
        prompt.AppendLine("- Les exemples et annexes informatives restent des recommandations ou du contexte.");
        prompt.AppendLine("- N'ecris aucun tag de modalite: le verificateur l'ajoute depuis source_strength.");
        prompt.AppendLine("- Aucun titre, introduction ou commentaire hors des 8 lignes.");
        prompt.AppendLine();
        prompt.AppendLine("DOCUMENT: " + docPath);
        prompt.AppendLine("EXTRAITS CANONIQUES:");
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var sourceText = ReadString(entry, "text");
            prompt.Append("[S").Append(index + 1).Append("] ")
                .Append("source_strength=").Append(ResolveSourceStrength(sourceText))
                .Append("; ")
                .Append("page=").Append(ReadInt(entry, "pageStart"))
                .Append("; section=").Append(Compact(ReadString(entry, "sectionTitle"), 100))
                .Append("; texte=").AppendLine(Compact(sourceText, 1800));
        }

        return prompt.ToString();
    }

    private static CandidateValidation ValidateAndRenderCandidatePoints(
        string rawAnswer,
        IReadOnlyList<JsonElement> entries,
        int requiredPointCount)
    {
        var rejections = new List<string>();
        var valid = new List<ValidatedCandidate>();
        var usedSources = new HashSet<int>();
        var lines = rawAnswer.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var linePattern = new Regex(
            @"^\s*(?<point>[1-9])[\.)]\s+(?<text>.*?)\s+\[S(?<source>\d{1,2})\][\.!?]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var match = linePattern.Match(line);
            int sourceNumber;
            string text;
            if (match.Success)
            {
                sourceNumber = int.Parse(match.Groups["source"].Value);
                text = match.Groups["text"].Value.Trim();
            }
            else if (lines.Length == entries.Count)
            {
                sourceNumber = lineIndex + 1;
                var explicitSource = Regex.Match(
                    line,
                    @"\[S(?<source>\d{1,2})\][\.!?]?\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (explicitSource.Success
                    && int.Parse(explicitSource.Groups["source"].Value)
                    != sourceNumber)
                {
                    rejections.Add(
                        $"source_order:line={lineIndex + 1}:marker={explicitSource.Groups["source"].Value}");
                    continue;
                }

                text = Regex.Replace(
                    line,
                    @"\s*\[S\d{1,2}\][\.!?]?\s*$",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                text = Regex.Replace(
                    text,
                    @"^\s*\d{1,2}[\.)]\s*",
                    string.Empty,
                    RegexOptions.CultureInvariant).Trim();
                if (text.Length == 0)
                {
                    rejections.Add("empty_candidate:line=" + (lineIndex + 1));
                    continue;
                }
            }
            else
            {
                rejections.Add("format:" + line);
                continue;
            }

            if (sourceNumber < 1 || sourceNumber > entries.Count)
            {
                rejections.Add("unknown_source:S" + sourceNumber);
                continue;
            }

            if (!usedSources.Add(sourceNumber))
            {
                rejections.Add("duplicate_source:S" + sourceNumber);
                continue;
            }

            var strength = ResolveSourceStrength(
                ReadString(entries[sourceNumber - 1], "text"));
            if (!string.Equals(strength, "Exigence", StringComparison.Ordinal)
                && Regex.IsMatch(
                    text,
                    @"\b(?:doit|doivent|obligatoire|exig[ée]e?|requis(?:e|es|s)?|shall|must|required|mandatory)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                rejections.Add(
                    $"unsupported_obligation:S{sourceNumber}:{text}");
                continue;
            }

            valid.Add(new ValidatedCandidate(sourceNumber, strength, text));
        }

        var selected = valid.Take(requiredPointCount).ToArray();
        var rendered = string.Join(
            Environment.NewLine,
            selected.Select((candidate, index) =>
                $"{index + 1}. [{candidate.Strength}] {candidate.Text} [S{candidate.SourceNumber}]"));
        return new CandidateValidation(
            lines.Length,
            valid.Count,
            selected.Select(static candidate => candidate.SourceNumber).ToArray(),
            rejections,
            rendered);
    }

    private static string ResolveSourceStrength(string sourceText)
    {
        if (Regex.IsMatch(
                sourceText,
                @"\b(?:shall|must)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Exigence";
        }

        if (Regex.IsMatch(
                sourceText,
                @"\b(?:should|recommended|recommendation|encouraged)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Recommandation";
        }

        return "Contexte";
    }

    private static string Compact(string value, int maxCharacters)
    {
        var compact = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        return compact.Length <= maxCharacters
            ? compact
            : compact[..maxCharacters].TrimEnd() + "...";
    }

    private static string ReadString(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && TryGetPropertyIgnoreCase(value, propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && TryGetPropertyIgnoreCase(value, propertyName, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt32(out var parsed)
            ? parsed
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

    private static int ReadNestedInt(
        JsonElement value,
        string objectProperty,
        string numberProperty)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(objectProperty, out var nested)
           && nested.ValueKind == JsonValueKind.Object
            ? ReadInt(nested, numberProperty)
            : 0;

    private sealed record ValidatedCandidate(
        int SourceNumber,
        string Strength,
        string Text);

    private sealed record CandidateValidation(
        int CandidatePointCount,
        int ValidCandidateCount,
        int[] CitedSourceNumbers,
        IReadOnlyList<string> Rejections,
        string RenderedAnswer);

    private static int ReadPositiveInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
           && parsed > 0
            ? parsed
            : fallback;

    private static string NormalizeLlmHost(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3].TrimEnd('/');
        return normalized;
    }

    private static string Require(string name, string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("Missing " + name + ".")
            : value.Trim();

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value =>
            !string.IsNullOrWhiteSpace(value))?.Trim();
}
