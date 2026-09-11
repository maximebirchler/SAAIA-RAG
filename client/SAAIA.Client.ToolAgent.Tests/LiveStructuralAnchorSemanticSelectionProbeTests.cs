using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveStructuralAnchorSemanticSelectionProbeTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_structural_facet_anchor_selection_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_STRUCTURAL_FACET_SELECTION_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_STRUCTURAL_FACET_SELECTION_PROBE to map explicit overview facets to canonical navigation anchors.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_STRUCTURAL_ANCHOR_SELECTION_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_ANCHOR_SELECTION_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_STRUCTURAL_ANCHOR_SELECTION_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_ANCHOR_SELECTION_OUTPUT_ARTIFACT"));
        var userRequest = Require(
            "SAAIA_STRUCTURAL_ANCHOR_SELECTION_REQUEST",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_ANCHOR_SELECTION_REQUEST"));
        var facets = ReadRequiredFacetArray(
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_FACET_SELECTION_FACETS_JSON"));
        var timeoutSeconds = ReadPositiveInt(
            "SAAIA_STRUCTURAL_ANCHOR_SELECTION_TIMEOUT_SECONDS",
            180);
        Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);

        using var input = JsonDocument.Parse(
            await File.ReadAllTextAsync(inputArtifact));
        var root = input.RootElement;
        var docId = Require("input docId", ReadString(root, "docId"));
        var docPath = Require("input docPath", ReadString(root, "docPath"));
        var allAnchors = ReadAnchors(root);
        var excludedAnchors = allAnchors.Where(static anchor =>
            !string.Equals(
                anchor.MechanicalRisk,
                "none",
                StringComparison.Ordinal)).ToArray();
        var anchors = allAnchors.Where(static anchor => string.Equals(
            anchor.MechanicalRisk,
            "none",
            StringComparison.Ordinal)).ToArray();
        Assert.True(
            anchors.Length >= facets.Length,
            $"Only {anchors.Length} mechanically eligible anchors are available for {facets.Length} facets.");

        var prompt = BuildFacetSelectionPrompt(
            userRequest,
            facets,
            anchors);
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

            var completion = await CompleteAsync(
                liveSettings.LlmBaseUrl,
                liveSettings.ModelId,
                "You are the semantic evidence selector. Map every explicit user facet to canonical anchor IDs. Return only the required F-number lines, without claims or commentary.",
                prompt,
                maxTokens: 96,
                cts.Token);
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "documents.structural_anchor.facet_selection",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_facet_selection_captured_before_contract_validation",
                        inputArtifact,
                        userRequest,
                        facets,
                        docId,
                        docPath,
                        inventoryCandidateCount = allAnchors.Length,
                        candidateCount = anchors.Length,
                        excludedAnchors,
                        promptCharacters = prompt.Length,
                        modelId = liveSettings.ModelId,
                        selector = completion
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            Assert.Equal("stop", completion.FinishReason);
            var facetSelections = ParseFacetSelection(
                completion.Content,
                facets,
                anchors);
            var byId = anchors.ToDictionary(
                static anchor => anchor.EvidenceId,
                StringComparer.OrdinalIgnoreCase);
            var selectedIds = facetSelections
                .SelectMany(static selection => selection.EvidenceIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedAnchors = selectedIds
                .Select(id => byId[id])
                .ToArray();

            var appSettings = AppSettings.Load();
            var backendUrl = Require(
                "backend URL",
                FirstNonBlank(
                    Environment.GetEnvironmentVariable(
                        "SAAIA_VALIDATION_BACKEND_URL"),
                    appSettings.BackendUrl));
            var apiKey = Require(
                "configured backend API key",
                FirstNonBlank(
                    Environment.GetEnvironmentVariable("SAAIA_API_KEY"),
                    SecureLocalStore.GetServerApiKey()));
            var api = new ApiClient();
            api.Configure(
                backendUrl,
                apiKey,
                Guid.NewGuid().ToString("D"));

            var materializationWatch = Stopwatch.StartNew();
            var entries = await Task.WhenAll(selectedAnchors.Select(
                async anchor =>
                {
                    var context = await api.DocumentsContextAsync(
                        docId,
                        docPath,
                        anchor.ChunkId,
                        pageStart: null,
                        pageEnd: null,
                        before: 0,
                        after: 0,
                        limit: 3,
                        offset: 0,
                        cts.Token);
                    return Materialize(anchor, context);
                }));
            materializationWatch.Stop();
            Assert.All(entries, static entry => Assert.True(
                entry.HasCompleteCanonicalIdentity,
                $"Incomplete canonical identity for {entry.EvidenceId}."));

            var report = new
            {
                probe = "documents.structural_anchor.facet_selection",
                capturedAtUtc = DateTimeOffset.UtcNow,
                stage = "facet_selection_contract_validated_and_materialized",
                inputArtifact,
                userRequest,
                facets,
                backendUrl,
                docId,
                docPath,
                inventoryCandidateCount = allAnchors.Length,
                candidateCount = anchors.Length,
                excludedAnchors,
                promptCharacters = prompt.Length,
                modelId = liveSettings.ModelId,
                selector = new
                {
                    completion.ElapsedMs,
                    completion.PromptTokens,
                    completion.CompletionTokens,
                    completion.FinishReason,
                    raw = completion.Content,
                    facetSelections
                },
                materializationMs = materializationWatch.ElapsedMilliseconds,
                selectedAnchors,
                entries
            };
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("selector_ms=" + completion.ElapsedMs);
            output.WriteLine("prompt_tokens=" + completion.PromptTokens);
            output.WriteLine("completion_tokens=" + completion.CompletionTokens);
            output.WriteLine("finish_reason=" + completion.FinishReason);
            output.WriteLine("selection=" + completion.Content);
            output.WriteLine("selected_pages=" + string.Join(",", entries.Select(
                static entry => entry.PageStart)));
            output.WriteLine("materialization_ms=" + materializationWatch.ElapsedMilliseconds);
            output.WriteLine("artifact=" + outputArtifact);
        }
        finally
        {
            manager.Stop();
        }
    }

    [Fact]
    public async Task Live_structural_anchor_semantic_selection_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_STRUCTURAL_ANCHOR_SELECTION_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_STRUCTURAL_ANCHOR_SELECTION_PROBE to let Qwen select from canonical navigation anchors.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_STRUCTURAL_ANCHOR_SELECTION_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_ANCHOR_SELECTION_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_STRUCTURAL_ANCHOR_SELECTION_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_ANCHOR_SELECTION_OUTPUT_ARTIFACT"));
        var userRequest = Require(
            "SAAIA_STRUCTURAL_ANCHOR_SELECTION_REQUEST",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_ANCHOR_SELECTION_REQUEST"));
        var requestedCount = ReadPositiveInt(
            "SAAIA_STRUCTURAL_ANCHOR_SELECTION_COUNT",
            6);
        var timeoutSeconds = ReadPositiveInt(
            "SAAIA_STRUCTURAL_ANCHOR_SELECTION_TIMEOUT_SECONDS",
            180);
        Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);

        using var input = JsonDocument.Parse(
            await File.ReadAllTextAsync(inputArtifact));
        var root = input.RootElement;
        var docId = Require("input docId", ReadString(root, "docId"));
        var docPath = Require("input docPath", ReadString(root, "docPath"));
        var allAnchors = ReadAnchors(root);
        var excludeMechanicalRisks = string.Equals(
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_ANCHOR_SELECTION_EXCLUDE_MECHANICAL_RISKS"),
            "1",
            StringComparison.Ordinal);
        var excludedAnchors = excludeMechanicalRisks
            ? allAnchors.Where(static anchor => !string.Equals(
                anchor.MechanicalRisk,
                "none",
                StringComparison.Ordinal)).ToArray()
            : [];
        var anchors = excludeMechanicalRisks
            ? allAnchors.Where(static anchor => string.Equals(
                anchor.MechanicalRisk,
                "none",
                StringComparison.Ordinal)).ToArray()
            : allAnchors;
        Assert.True(
            anchors.Length >= requestedCount,
            $"Only {anchors.Length} resolvable anchors are available.");

        var prompt = BuildSelectionPrompt(
            userRequest,
            requestedCount,
            anchors);
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

            var completion = await CompleteAsync(
                liveSettings.LlmBaseUrl,
                liveSettings.ModelId,
                "You are the semantic evidence selector. Choose only canonical anchor IDs that collectively support the complete user request. Return IDs only, without claims or commentary.",
                prompt,
                maxTokens: 64,
                cts.Token);
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "documents.structural_anchor.semantic_selection",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_selection_captured_before_contract_validation",
                        inputArtifact,
                        userRequest,
                        docId,
                        docPath,
                        requestedCount,
                        inventoryCandidateCount = allAnchors.Length,
                        candidateCount = anchors.Length,
                        excludedAnchors,
                        promptCharacters = prompt.Length,
                        modelId = liveSettings.ModelId,
                        selector = completion
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            var selectedIds = ParseSelection(
                completion.Content,
                anchors,
                requestedCount);
            var byId = anchors.ToDictionary(
                static anchor => anchor.EvidenceId,
                StringComparer.OrdinalIgnoreCase);
            var selectedAnchors = selectedIds
                .Select(id => byId[id])
                .ToArray();
            Assert.DoesNotContain(
                selectedAnchors,
                static anchor => !string.Equals(
                    anchor.MechanicalRisk,
                    "none",
                    StringComparison.Ordinal));

            var appSettings = AppSettings.Load();
            var backendUrl = Require(
                "backend URL",
                FirstNonBlank(
                    Environment.GetEnvironmentVariable(
                        "SAAIA_VALIDATION_BACKEND_URL"),
                    appSettings.BackendUrl));
            var apiKey = Require(
                "configured backend API key",
                FirstNonBlank(
                    Environment.GetEnvironmentVariable("SAAIA_API_KEY"),
                    SecureLocalStore.GetServerApiKey()));
            var api = new ApiClient();
            api.Configure(
                backendUrl,
                apiKey,
                Guid.NewGuid().ToString("D"));

            var materializationWatch = Stopwatch.StartNew();
            var entries = await Task.WhenAll(selectedAnchors.Select(
                async anchor =>
                {
                    var context = await api.DocumentsContextAsync(
                        docId,
                        docPath,
                        anchor.ChunkId,
                        pageStart: null,
                        pageEnd: null,
                        before: 0,
                        after: 0,
                        limit: 3,
                        offset: 0,
                        cts.Token);
                    return Materialize(anchor, context);
                }));
            materializationWatch.Stop();
            Assert.All(entries, static entry => Assert.True(
                entry.HasCompleteCanonicalIdentity,
                $"Incomplete canonical identity for {entry.EvidenceId}."));

            var report = new
            {
                probe = "documents.structural_anchor.semantic_selection",
                capturedAtUtc = DateTimeOffset.UtcNow,
                stage = "selection_contract_validated_and_materialized",
                inputArtifact,
                userRequest,
                backendUrl,
                docId,
                docPath,
                requestedCount,
                inventoryCandidateCount = allAnchors.Length,
                candidateCount = anchors.Length,
                excludedAnchors,
                promptCharacters = prompt.Length,
                modelId = liveSettings.ModelId,
                selector = new
                {
                    completion.ElapsedMs,
                    completion.PromptTokens,
                    completion.CompletionTokens,
                    completion.FinishReason,
                    raw = completion.Content,
                    selectedIds
                },
                materializationMs = materializationWatch.ElapsedMilliseconds,
                selectedAnchors,
                entries
            };
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("selector_ms=" + completion.ElapsedMs);
            output.WriteLine("prompt_tokens=" + completion.PromptTokens);
            output.WriteLine("completion_tokens=" + completion.CompletionTokens);
            output.WriteLine("finish_reason=" + completion.FinishReason);
            output.WriteLine("selected_ids=" + string.Join(",", selectedIds));
            output.WriteLine("selected_pages=" + string.Join(",", entries.Select(
                static entry => entry.PageStart)));
            output.WriteLine("materialization_ms=" + materializationWatch.ElapsedMilliseconds);
            output.WriteLine("artifact=" + outputArtifact);
        }
        finally
        {
            manager.Stop();
        }
    }

    private static AnchorCandidate[] ReadAnchors(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("navigation", out var navigation)
            || navigation.ValueKind != JsonValueKind.Object
            || !navigation.TryGetProperty("anchors", out var anchors)
            || anchors.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return anchors.EnumerateArray()
            .Select((anchor, index) => new AnchorCandidate(
                "A" + (index + 1),
                ReadString(anchor, "chunkId"),
                ReadString(anchor, "anchorId"),
                ReadString(anchor, "label"),
                ReadString(anchor, "navigationKind"),
                ReadString(anchor, "sourceKind"),
                ReadInt(anchor, "pageStart"),
                ReadInt(anchor, "pageEnd"),
                ResolveMechanicalRisk(ReadString(anchor, "label"))))
            .Where(static anchor =>
                !string.IsNullOrWhiteSpace(anchor.ChunkId)
                && !string.IsNullOrWhiteSpace(anchor.Label)
                && anchor.PageStart > 0)
            .GroupBy(
                static anchor => anchor.ChunkId,
                StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
    }

    private static string BuildSelectionPrompt(
        string userRequest,
        int requestedCount,
        IReadOnlyList<AnchorCandidate> anchors)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_STRUCTURAL_ANCHOR_SELECTOR");
        prompt.AppendLine("USER_REQUEST:");
        prompt.AppendLine(userRequest);
        prompt.Append("Select exactly ").Append(requestedCount)
            .AppendLine(" distinct anchor IDs from the canonical inventory below.");
        prompt.AppendLine("Choose passages whose underlying text collectively supports every explicitly requested facet, not merely the general topic.");
        prompt.AppendLine("Prefer direct, self-contained, answer-bearing labels. Reject cover titles, tables of contents, bibliographies, page furniture and fragments with no usable claim.");
        prompt.AppendLine("Never select an anchor whose mechanical_risk is not none; the risk describes form only and does not decide semantic relevance among the remaining anchors.");
        prompt.AppendLine("When the request asks for a document overview, include evidence for its stated scope or purpose and representative evidence for its contents when available.");
        prompt.AppendLine("Do not invent missing evidence and do not write the answer.");
        prompt.AppendLine("Return only comma-separated IDs, for example A2,A5,A9. No words, JSON, brackets or claims.");
        prompt.AppendLine("CANONICAL_ANCHORS:");
        foreach (var anchor in anchors)
        {
            prompt.Append(anchor.EvidenceId)
                .Append(" page=").Append(anchor.PageStart)
                .Append(" kind=").Append(anchor.NavigationKind)
                .Append(" mechanical_risk=").Append(anchor.MechanicalRisk)
                .Append(" label=").AppendLine(Compact(anchor.Label, 240));
        }

        return prompt.ToString();
    }

    private static string BuildFacetSelectionPrompt(
        string userRequest,
        IReadOnlyList<string> facets,
        IReadOnlyList<AnchorCandidate> anchors)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_STRUCTURAL_FACET_ANCHOR_SELECTOR");
        prompt.AppendLine("USER_REQUEST:");
        prompt.AppendLine(userRequest);
        prompt.AppendLine("EXPLICIT_FACETS:");
        for (var index = 0; index < facets.Count; index++)
            prompt.Append('F').Append(index + 1).Append('=').AppendLine(facets[index]);
        prompt.AppendLine("For each facet, select one or two anchor IDs whose underlying canonical text best supports a faithful answer to that facet.");
        prompt.AppendLine("Use semantic judgment. Prefer direct, self-contained, answer-bearing labels; coverage of the exact facet matters more than document-wide variety.");
        prompt.AppendLine("For a practical-use facet, choose passages that justify the use without pretending the source states the recommendation verbatim.");
        prompt.AppendLine("Do not invent missing evidence and do not write any answer claim.");
        prompt.AppendLine("Return exactly one line per facet in order, for example F1=A2,A5. Use one or two distinct IDs per line. No other text, JSON or brackets.");
        prompt.AppendLine("MECHANICALLY_ELIGIBLE_CANONICAL_ANCHORS:");
        foreach (var anchor in anchors)
        {
            prompt.Append(anchor.EvidenceId)
                .Append(" page=").Append(anchor.PageStart)
                .Append(" kind=").Append(anchor.NavigationKind)
                .Append(" label=").AppendLine(Compact(anchor.Label, 240));
        }

        return prompt.ToString();
    }

    private static string ResolveMechanicalRisk(string? label)
    {
        var compact = Regex.Replace(label ?? string.Empty, @"\s+", " ").Trim();
        if (compact.Length == 0)
            return "empty";

        var wordCount = Regex.Matches(
            compact,
            @"[\p{L}\p{N}]+",
            RegexOptions.CultureInvariant).Count;
        return wordCount < 4
               && !Regex.IsMatch(
                   compact,
                   @"[.!?:;]",
                   RegexOptions.CultureInvariant)
            ? "short_non_sentence"
            : "none";
    }

    private static string[] ParseSelection(
        string raw,
        IReadOnlyList<AnchorCandidate> anchors,
        int requestedCount)
    {
        var normalized = (raw ?? string.Empty).Trim();
        Assert.True(
            Regex.Replace(
                normalized,
                @"A\d{1,3}|[\s,;|]",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Length == 0,
            "Selector returned non-contract text: " + normalized);
        var ids = Regex.Matches(
                normalized,
                @"A\d{1,3}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => match.Value.ToUpperInvariant())
            .ToArray();
        Assert.Equal(requestedCount, ids.Length);
        Assert.Equal(requestedCount, ids.Distinct(
            StringComparer.OrdinalIgnoreCase).Count());
        var allowed = anchors.Select(static anchor => anchor.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(ids, id => Assert.Contains(id, allowed));
        return ids;
    }

    private static FacetSelection[] ParseFacetSelection(
        string raw,
        IReadOnlyList<string> facets,
        IReadOnlyList<AnchorCandidate> anchors)
    {
        var lines = (raw ?? string.Empty).Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        Assert.Equal(facets.Count, lines.Length);
        var allowed = anchors.Select(static anchor => anchor.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selections = new List<FacetSelection>(facets.Count);
        for (var index = 0; index < facets.Count; index++)
        {
            var match = Regex.Match(
                lines[index],
                "^F(?<index>\\d{1,2})\\s*=\\s*(?<ids>A\\d{1,3}(?:\\s*,\\s*A\\d{1,3})?)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Assert.True(match.Success, "Selector returned non-contract line: " + lines[index]);
            Assert.Equal(
                index + 1,
                int.Parse(match.Groups["index"].Value));
            var ids = match.Groups["ids"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries)
                .Select(static id => id.ToUpperInvariant())
                .ToArray();
            Assert.InRange(ids.Length, 1, 2);
            Assert.Equal(
                ids.Length,
                ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(ids, id => Assert.Contains(id, allowed));
            selections.Add(new FacetSelection(
                "F" + (index + 1),
                facets[index],
                ids));
        }

        return selections.ToArray();
    }

    private static string[] ReadRequiredFacetArray(string? rawJson)
    {
        using var document = JsonDocument.Parse(Require(
            "SAAIA_STRUCTURAL_FACET_SELECTION_FACETS_JSON",
            rawJson));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        var facets = document.RootElement.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim() ?? string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.InRange(facets.Length, 2, 10);
        Assert.All(facets, static facet => Assert.InRange(facet.Length, 1, 180));
        return facets;
    }

    private static MaterializedEntry Materialize(
        AnchorCandidate anchor,
        JsonElement context)
    {
        if (context.ValueKind != JsonValueKind.Object
            || !context.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Context response did not contain items for "
                + anchor.EvidenceId);
        }
        var item = items.EnumerateArray().FirstOrDefault(candidate =>
            string.Equals(
                ReadString(candidate, "chunkId"),
                anchor.ChunkId,
                StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Object, item.ValueKind);
        var document = context.TryGetProperty("document", out var documentValue)
                       && documentValue.ValueKind == JsonValueKind.Object
            ? documentValue
            : default;
        return new MaterializedEntry(
            anchor.EvidenceId,
            ReadString(document, "docId"),
            ReadString(document, "revisionId"),
            ReadString(document, "sourceHash"),
            ReadString(document, "docPath"),
            ReadString(document, "docName"),
            ReadString(item, "chunkId"),
            anchor.AnchorId,
            ReadInt(item, "pageStart"),
            ReadInt(item, "pageEnd"),
            ReadInt(item, "tokenCount"),
            ReadString(item, "chunkType"),
            ReadString(item, "contentRole"),
            ReadString(item, "sectionTitle"),
            ReadString(item, "headingPath"),
            ReadString(item, "text"));
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
        var body = await response.Content.ReadAsStringAsync(ct);
        watch.Stop();
        response.EnsureSuccessStatusCode();
        using var completion = JsonDocument.Parse(body);
        var choice = completion.RootElement.GetProperty("choices")[0];
        return new CompletionResult(
            choice.GetProperty("message").GetProperty("content")
                .GetString()?.Trim() ?? string.Empty,
            watch.ElapsedMilliseconds,
            ReadNestedInt(completion.RootElement, "usage", "prompt_tokens"),
            ReadNestedInt(completion.RootElement, "usage", "completion_tokens"),
            ReadString(choice, "finish_reason"));
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

    private static int ReadPositiveInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed)
           && parsed > 0
            ? parsed
            : fallback;

    private static string Require(string name, string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("Missing " + name + ".")
            : value.Trim();

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value =>
            !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record AnchorCandidate(
        string EvidenceId,
        string ChunkId,
        string AnchorId,
        string Label,
        string NavigationKind,
        string SourceKind,
        int PageStart,
        int PageEnd,
        string MechanicalRisk);

    private sealed record FacetSelection(
        string FacetId,
        string Facet,
        string[] EvidenceIds);

    private sealed record MaterializedEntry(
        string EvidenceId,
        string DocId,
        string RevisionId,
        string SourceHash,
        string DocPath,
        string DocName,
        string ChunkId,
        string AnchorId,
        int PageStart,
        int PageEnd,
        int TokenCount,
        string ChunkType,
        string ContentRole,
        string SectionTitle,
        string HeadingPath,
        string Text)
    {
        public bool HasCompleteCanonicalIdentity =>
            !string.IsNullOrWhiteSpace(DocId)
            && !string.IsNullOrWhiteSpace(RevisionId)
            && SourceHash.Length == 64
            && !string.IsNullOrWhiteSpace(DocPath)
            && !string.IsNullOrWhiteSpace(ChunkId)
            && !string.IsNullOrWhiteSpace(AnchorId)
            && PageStart > 0
            && PageEnd >= PageStart
            && !string.IsNullOrWhiteSpace(Text);
    }

    private sealed record CompletionResult(
        string Content,
        long ElapsedMs,
        int PromptTokens,
        int CompletionTokens,
        string FinishReason);
}
