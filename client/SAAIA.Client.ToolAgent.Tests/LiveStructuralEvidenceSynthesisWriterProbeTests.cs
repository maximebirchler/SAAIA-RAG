using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveStructuralEvidenceSynthesisWriterProbeTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_structural_facet_evidence_judges_are_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_STRUCTURAL_FACET_JUDGE_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_STRUCTURAL_FACET_JUDGE_PROBE to review each structured facet answer against its isolated canonical evidence.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_OUTPUT_ARTIFACT"));
        var userRequest = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_REQUEST",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_REQUEST"));
        var maxTokensPerFacet = ReadPositiveInt(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_MAX_TOKENS_PER_FACET",
            128);
        var timeoutSeconds = ReadPositiveInt(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_TIMEOUT_SECONDS",
            180);
        Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);

        using var input = JsonDocument.Parse(
            await File.ReadAllTextAsync(inputArtifact));
        var entries = ReadEntries(input.RootElement);
        var assignments = ReadFacetAssignments(
            input.RootElement,
            entries);
        var initialDecisions = ReadStructuredFacetDecisions(
            input.RootElement,
            assignments);
        Assert.Equal(assignments.Length, initialDecisions.Length);

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
            var llm = new OpenAiLlmClient();
            llm.Configure(liveSettings.LlmBaseUrl, liveSettings.ModelId);

            var wallWatch = Stopwatch.StartNew();
            var judgeRuns = await Task.WhenAll(assignments.Select(
                async (assignment, index) =>
                {
                    var prompt = BuildFacetJudgePrompt(
                        userRequest,
                        assignment,
                        initialDecisions[index],
                        entries);
                    var completion = await llm.ChatOnceNativeAsync(
                        new[]
                        {
                            SourceBackedAgentMessage.System(
                                "Tu es l'Evidence Judge final. Compare chaque relation, portée et modalité de la réponse aux seules preuves canoniques fournies. Appelle l'outil requis une seule fois et n'écris aucun texte hors de l'appel."),
                            SourceBackedAgentMessage.User(prompt)
                        },
                        new[] { BuildFacetJudgeTool(assignment) },
                        temperature: 0,
                        maxTokens: maxTokensPerFacet,
                        ct: cts.Token,
                        requireToolCall: true);
                    return new FacetJudgeRun(
                        assignment,
                        initialDecisions[index],
                        prompt.Length,
                        completion);
                }));
            wallWatch.Stop();

            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "documents.structural_evidence.facet_judges",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_facet_judge_calls_captured_before_contract_validation",
                        inputArtifact,
                        userRequest,
                        assignments,
                        initialDecisions,
                        modelId = liveSettings.ModelId,
                        evidenceCount = entries.Length,
                        maxTokensPerFacet,
                        maximumAggregateTokens = maxTokensPerFacet * assignments.Length,
                        wallMs = wallWatch.ElapsedMilliseconds,
                        judgeRuns
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            var judgments = judgeRuns.Select(run =>
                    ValidateFacetJudge(
                        run.Completion,
                        run.Assignment,
                        run.InitialDecision,
                        entries))
                .ToArray();
            Assert.DoesNotContain(
                judgments,
                static judgment => string.Equals(
                    judgment.Verdict,
                    "insufficient",
                    StringComparison.Ordinal));
            var aggregateCompletionTokens = judgeRuns.Sum(static run =>
                run.Completion.CompletionTokens ?? 0);
            Assert.InRange(
                aggregateCompletionTokens,
                1,
                maxTokensPerFacet * assignments.Length);
            var assembledAnswer = string.Join(
                Environment.NewLine,
                judgments.Select(static judgment =>
                    judgment.FacetId + ": "
                    + (judgment.IsPracticalInference
                        ? "[Déduction pratique] "
                        : string.Empty)
                    + judgment.FinalAnswer + " "
                    + string.Concat(judgment.EvidenceIds.Select(
                        static id => "[" + id + "]"))));
            var report = new
            {
                probe = "documents.structural_evidence.facet_judges",
                capturedAtUtc = DateTimeOffset.UtcNow,
                stage = "facet_judge_contracts_validated",
                inputArtifact,
                userRequest,
                assignments,
                initialDecisions,
                modelId = liveSettings.ModelId,
                evidenceCount = entries.Length,
                maxTokensPerFacet,
                maximumAggregateTokens = maxTokensPerFacet * assignments.Length,
                aggregateCompletionTokens,
                wallMs = wallWatch.ElapsedMilliseconds,
                judgeRuns,
                judgments,
                assembledAnswer,
                evidence = entries
            };
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("wall_ms=" + wallWatch.ElapsedMilliseconds);
            output.WriteLine("aggregate_completion_tokens=" + aggregateCompletionTokens);
            output.WriteLine("answer=" + assembledAnswer);
            output.WriteLine("artifact=" + outputArtifact);
        }
        finally
        {
            manager.Stop();
        }
    }

    [Fact]
    public async Task Live_structural_isolated_facet_tool_writers_are_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_STRUCTURAL_FACET_TOOL_WRITER_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_STRUCTURAL_FACET_TOOL_WRITER_PROBE to submit each isolated facet answer through a native tool contract.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_OUTPUT_ARTIFACT"));
        var userRequest = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_REQUEST",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_REQUEST"));
        var maxTokensPerFacet = ReadPositiveInt(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_MAX_TOKENS_PER_FACET",
            96);
        var timeoutSeconds = ReadPositiveInt(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_TIMEOUT_SECONDS",
            180);
        Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);

        using var input = JsonDocument.Parse(
            await File.ReadAllTextAsync(inputArtifact));
        var entries = ReadEntries(input.RootElement);
        var assignments = ReadFacetAssignments(
            input.RootElement,
            entries);
        Assert.NotEmpty(assignments);

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
            var llm = new OpenAiLlmClient();
            llm.Configure(liveSettings.LlmBaseUrl, liveSettings.ModelId);

            var wallWatch = Stopwatch.StartNew();
            var facetRuns = await Task.WhenAll(assignments.Select(
                async assignment =>
                {
                    var prompt = BuildStructuredFacetPrompt(
                        userRequest,
                        assignment,
                        entries);
                    var completion = await llm.ChatOnceNativeAsync(
                        new[]
                        {
                            SourceBackedAgentMessage.System(
                                "Tu es le redacteur semantique final. Utilise exclusivement les preuves canoniques de la facette courante. Appelle l'outil requis une seule fois; n'ecris aucun texte hors de l'appel."),
                            SourceBackedAgentMessage.User(prompt)
                        },
                        new[] { BuildFacetAnswerTool(assignment) },
                        temperature: 0,
                        maxTokens: maxTokensPerFacet,
                        ct: cts.Token,
                        requireToolCall: true);
                    return new StructuredFacetRun(
                        assignment,
                        prompt.Length,
                        completion);
                }));
            wallWatch.Stop();

            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "documents.structural_evidence.isolated_facet_tool_writers",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_isolated_facet_tool_calls_captured_before_contract_validation",
                        inputArtifact,
                        userRequest,
                        assignments,
                        modelId = liveSettings.ModelId,
                        evidenceCount = entries.Length,
                        maxTokensPerFacet,
                        maximumAggregateTokens = maxTokensPerFacet * assignments.Length,
                        wallMs = wallWatch.ElapsedMilliseconds,
                        facetRuns
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            var decisions = facetRuns.Select(run =>
                    ValidateStructuredFacetWriter(
                        run.Completion,
                        run.Assignment,
                        entries))
                .ToArray();
            var aggregateCompletionTokens = facetRuns.Sum(static run =>
                run.Completion.CompletionTokens ?? 0);
            Assert.InRange(
                aggregateCompletionTokens,
                1,
                maxTokensPerFacet * assignments.Length);
            var assembledAnswer = string.Join(
                Environment.NewLine,
                decisions.Select(static decision =>
                    decision.FacetId + ": "
                    + (decision.IsPracticalInference
                        ? "[Déduction pratique] "
                        : string.Empty)
                    + decision.Answer + " "
                    + string.Concat(decision.EvidenceIds.Select(
                        static id => "[" + id + "]"))));
            var report = new
            {
                probe = "documents.structural_evidence.isolated_facet_tool_writers",
                capturedAtUtc = DateTimeOffset.UtcNow,
                stage = "isolated_facet_tool_contracts_validated",
                inputArtifact,
                userRequest,
                assignments,
                modelId = liveSettings.ModelId,
                evidenceCount = entries.Length,
                maxTokensPerFacet,
                maximumAggregateTokens = maxTokensPerFacet * assignments.Length,
                aggregateCompletionTokens,
                wallMs = wallWatch.ElapsedMilliseconds,
                facetRuns,
                decisions,
                assembledAnswer,
                evidence = entries
            };
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("wall_ms=" + wallWatch.ElapsedMilliseconds);
            output.WriteLine("aggregate_completion_tokens=" + aggregateCompletionTokens);
            output.WriteLine("answer=" + assembledAnswer);
            output.WriteLine("artifact=" + outputArtifact);
        }
        finally
        {
            manager.Stop();
        }
    }

    [Fact]
    public async Task Live_structural_isolated_facet_writers_are_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_STRUCTURAL_ISOLATED_FACET_WRITER_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_STRUCTURAL_ISOLATED_FACET_WRITER_PROBE to write each overview facet from its isolated canonical evidence.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_OUTPUT_ARTIFACT"));
        var userRequest = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_REQUEST",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_REQUEST"));
        var maxTokensPerFacet = ReadPositiveInt(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_MAX_TOKENS_PER_FACET",
            80);
        var timeoutSeconds = ReadPositiveInt(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_TIMEOUT_SECONDS",
            180);
        Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);

        using var input = JsonDocument.Parse(
            await File.ReadAllTextAsync(inputArtifact));
        var entries = ReadEntries(input.RootElement);
        var assignments = ReadFacetAssignments(
            input.RootElement,
            entries);
        Assert.NotEmpty(assignments);

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

            var wallWatch = Stopwatch.StartNew();
            var facetRuns = await Task.WhenAll(assignments.Select(
                async assignment =>
                {
                    var prompt = BuildIsolatedFacetPrompt(
                        userRequest,
                        assignment,
                        entries);
                    var completion = await CompleteAsync(
                        liveSettings.LlmBaseUrl,
                        liveSettings.ModelId,
                        "Tu es le redacteur semantique final. Reponds uniquement en francais avec une seule phrase. Utilise exclusivement les preuves canoniques de la facette courante. N'invente aucun fait et formule tout usage pratique comme une deduction conseillee, jamais comme une obligation du document.",
                        prompt,
                        maxTokensPerFacet,
                        cts.Token);
                    return new IsolatedFacetRun(
                        assignment,
                        prompt.Length,
                        completion);
                }));
            wallWatch.Stop();

            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "documents.structural_evidence.isolated_facet_writers",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_isolated_facet_writers_captured_before_contract_validation",
                        inputArtifact,
                        userRequest,
                        assignments,
                        modelId = liveSettings.ModelId,
                        evidenceCount = entries.Length,
                        maxTokensPerFacet,
                        maximumAggregateTokens = maxTokensPerFacet * assignments.Length,
                        wallMs = wallWatch.ElapsedMilliseconds,
                        facetRuns
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            var validations = facetRuns.Select(run =>
                    ValidateIsolatedFacetWriter(
                        run.Completion,
                        run.Assignment,
                        entries))
                .ToArray();
            var aggregateCompletionTokens = facetRuns.Sum(static run =>
                run.Completion.CompletionTokens);
            Assert.InRange(
                aggregateCompletionTokens,
                1,
                maxTokensPerFacet * assignments.Length);
            var assembledAnswer = string.Join(
                Environment.NewLine,
                facetRuns.Select(static run =>
                    run.Assignment.FacetId + ": " + run.Completion.Content));
            var report = new
            {
                probe = "documents.structural_evidence.isolated_facet_writers",
                capturedAtUtc = DateTimeOffset.UtcNow,
                stage = "isolated_facet_writer_contracts_validated",
                inputArtifact,
                userRequest,
                assignments,
                modelId = liveSettings.ModelId,
                evidenceCount = entries.Length,
                maxTokensPerFacet,
                maximumAggregateTokens = maxTokensPerFacet * assignments.Length,
                aggregateCompletionTokens,
                wallMs = wallWatch.ElapsedMilliseconds,
                facetRuns,
                validations,
                assembledAnswer,
                evidence = entries
            };
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("wall_ms=" + wallWatch.ElapsedMilliseconds);
            output.WriteLine("aggregate_completion_tokens=" + aggregateCompletionTokens);
            output.WriteLine("answer=" + assembledAnswer);
            output.WriteLine("artifact=" + outputArtifact);
        }
        finally
        {
            manager.Stop();
        }
    }

    [Fact]
    public async Task Live_structural_facet_evidence_writer_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_STRUCTURAL_FACET_EVIDENCE_WRITER_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_STRUCTURAL_FACET_EVIDENCE_WRITER_PROBE to write one cited answer per explicit overview facet.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_OUTPUT_ARTIFACT"));
        var userRequest = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_REQUEST",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_REQUEST"));
        var maxTokens = ReadPositiveInt(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_MAX_TOKENS",
            256);
        var timeoutSeconds = ReadPositiveInt(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_TIMEOUT_SECONDS",
            180);
        Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);

        using var input = JsonDocument.Parse(
            await File.ReadAllTextAsync(inputArtifact));
        var entries = ReadEntries(input.RootElement);
        var assignments = ReadFacetAssignments(
            input.RootElement,
            entries);
        Assert.NotEmpty(assignments);
        var prompt = BuildFacetPrompt(
            userRequest,
            assignments,
            entries);

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
                "Tu es le redacteur semantique final. Reponds uniquement en francais. Reponds a chaque facette avec ses seules preuves canoniques autorisees. N'invente aucun fait et signale clairement toute recommandation pratique comme une deduction d'usage.",
                prompt,
                maxTokens,
                cts.Token);
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "documents.structural_evidence.facet_writer",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_facet_writer_captured_before_contract_validation",
                        inputArtifact,
                        userRequest,
                        assignments,
                        modelId = liveSettings.ModelId,
                        evidenceCount = entries.Length,
                        promptCharacters = prompt.Length,
                        maxTokens,
                        writer = completion
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            var validation = ValidateFacetWriter(
                completion.Content,
                completion.FinishReason,
                assignments,
                entries);
            var report = new
            {
                probe = "documents.structural_evidence.facet_writer",
                capturedAtUtc = DateTimeOffset.UtcNow,
                stage = "facet_writer_contract_validated",
                inputArtifact,
                userRequest,
                assignments,
                modelId = liveSettings.ModelId,
                evidenceCount = entries.Length,
                promptCharacters = prompt.Length,
                maxTokens,
                writer = new
                {
                    completion.ElapsedMs,
                    completion.PromptTokens,
                    completion.CompletionTokens,
                    completion.FinishReason,
                    raw = completion.Content
                },
                validation,
                evidence = entries
            };
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("writer_ms=" + completion.ElapsedMs);
            output.WriteLine("prompt_tokens=" + completion.PromptTokens);
            output.WriteLine("completion_tokens=" + completion.CompletionTokens);
            output.WriteLine("finish_reason=" + completion.FinishReason);
            output.WriteLine("line_count=" + validation.LineCount);
            output.WriteLine("detected_language=" + validation.DetectedLanguage);
            output.WriteLine("answer=" + completion.Content);
            output.WriteLine("artifact=" + outputArtifact);
        }
        finally
        {
            manager.Stop();
        }
    }

    [Fact]
    public async Task Live_structural_evidence_synthesis_writer_is_captured_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_STRUCTURAL_EVIDENCE_SYNTHESIS_WRITER_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: enable SAAIA_LIVE_STRUCTURAL_EVIDENCE_SYNTHESIS_WRITER_PROBE to write one cited synthesis from selected canonical anchors.");
            return;
        }

        var inputArtifact = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_INPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_INPUT_ARTIFACT"));
        var outputArtifact = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_OUTPUT_ARTIFACT",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_OUTPUT_ARTIFACT"));
        var userRequest = Require(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_REQUEST",
            Environment.GetEnvironmentVariable(
                "SAAIA_STRUCTURAL_EVIDENCE_WRITER_REQUEST"));
        var maxTokens = ReadPositiveInt(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_MAX_TOKENS",
            320);
        var timeoutSeconds = ReadPositiveInt(
            "SAAIA_STRUCTURAL_EVIDENCE_WRITER_TIMEOUT_SECONDS",
            180);
        Directory.CreateDirectory(Path.GetDirectoryName(outputArtifact)!);

        using var input = JsonDocument.Parse(
            await File.ReadAllTextAsync(inputArtifact));
        var entries = ReadEntries(input.RootElement);
        Assert.NotEmpty(entries);
        var prompt = BuildPrompt(userRequest, entries);

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
                "Tu es le redacteur semantique final. Reponds uniquement en francais. Utilise exclusivement les preuves canoniques fournies. N'invente aucun fait et distingue clairement ce que la source dit de l'usage pratique que tu en deduis.",
                prompt,
                maxTokens,
                cts.Token);
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    new
                    {
                        probe = "documents.structural_evidence.cited_synthesis_writer",
                        capturedAtUtc = DateTimeOffset.UtcNow,
                        stage = "raw_writer_captured_before_contract_validation",
                        inputArtifact,
                        userRequest,
                        modelId = liveSettings.ModelId,
                        evidenceCount = entries.Length,
                        promptCharacters = prompt.Length,
                        maxTokens,
                        writer = completion
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            var validation = Validate(
                completion.Content,
                completion.FinishReason,
                entries);
            var report = new
            {
                probe = "documents.structural_evidence.cited_synthesis_writer",
                capturedAtUtc = DateTimeOffset.UtcNow,
                stage = "writer_contract_validated",
                inputArtifact,
                userRequest,
                modelId = liveSettings.ModelId,
                evidenceCount = entries.Length,
                promptCharacters = prompt.Length,
                maxTokens,
                writer = new
                {
                    completion.ElapsedMs,
                    completion.PromptTokens,
                    completion.CompletionTokens,
                    completion.FinishReason,
                    raw = completion.Content
                },
                validation,
                evidence = entries
            };
            await File.WriteAllTextAsync(
                outputArtifact,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None);

            output.WriteLine("writer_ms=" + completion.ElapsedMs);
            output.WriteLine("prompt_tokens=" + completion.PromptTokens);
            output.WriteLine("completion_tokens=" + completion.CompletionTokens);
            output.WriteLine("finish_reason=" + completion.FinishReason);
            output.WriteLine("line_count=" + validation.LineCount);
            output.WriteLine("cited_ids=" + string.Join(",", validation.CitedIds));
            output.WriteLine("detected_language=" + validation.DetectedLanguage);
            output.WriteLine("answer=" + completion.Content);
            output.WriteLine("artifact=" + outputArtifact);
        }
        finally
        {
            manager.Stop();
        }
    }

    private static EvidenceEntry[] ReadEntries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(root, "entries", out var entries)
            && !TryGetPropertyIgnoreCase(root, "evidence", out entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return entries.EnumerateArray()
            .Select(static entry => new EvidenceEntry(
                ReadString(entry, "EvidenceId"),
                ReadString(entry, "DocId"),
                ReadString(entry, "RevisionId"),
                ReadString(entry, "SourceHash"),
                ReadString(entry, "DocPath"),
                ReadString(entry, "DocName"),
                ReadString(entry, "ChunkId"),
                ReadString(entry, "AnchorId"),
                ReadInt(entry, "PageStart"),
                ReadInt(entry, "PageEnd"),
                ReadString(entry, "SectionTitle"),
                ReadString(entry, "Text")))
            .Where(static entry =>
                !string.IsNullOrWhiteSpace(entry.EvidenceId)
                && !string.IsNullOrWhiteSpace(entry.Text))
            .ToArray();
    }

    private static string BuildPrompt(
        string userRequest,
        IReadOnlyList<EvidenceEntry> entries)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_SYNTHESIS_WRITER");
        prompt.AppendLine("DEMANDE UTILISATEUR:");
        prompt.AppendLine(userRequest);
        prompt.AppendLine();
        prompt.AppendLine("CONTRAT DE REPONSE:");
        prompt.AppendLine("- Reponds a chaque facette explicite de la demande, dans son ordre, en une ligne ou un court paragraphe distinct.");
        prompt.AppendLine("- Chaque ligne contient uniquement des affirmations soutenues par les preuves ci-dessous et se termine par un ou plusieurs marqueurs exacts tels que [A5][A6].");
        prompt.AppendLine("- Tu peux ignorer une preuve faible; ne cite jamais une preuve que tu n'utilises pas.");
        prompt.AppendLine("- Si tu proposes un cas d'usage pratique a partir de la portee ou du contenu, presente-le explicitement comme une recommandation d'usage, pas comme une phrase textuelle de la norme.");
        prompt.AppendLine("- Ne dis pas qu'un document impose, certifie ou remplace une exigence si la preuve ne le dit pas.");
        prompt.AppendLine("- Reste concis, professionnel et directement utile. Aucun commentaire sur le protocole.");
        prompt.AppendLine();
        prompt.AppendLine("PREUVES CANONIQUES:");
        foreach (var entry in entries)
        {
            prompt.Append('[').Append(entry.EvidenceId).Append("] page=")
                .Append(entry.PageStart)
                .Append(" section=").Append(Compact(entry.SectionTitle, 100))
                .Append(" texte=").AppendLine(Compact(entry.Text, 1200));
        }

        return prompt.ToString();
    }

    private static FacetAssignment[] ReadFacetAssignments(
        JsonElement root,
        IReadOnlyList<EvidenceEntry> entries)
    {
        JsonElement selections;
        if (TryGetPropertyIgnoreCase(root, "assignments", out var rootAssignments)
            && rootAssignments.ValueKind == JsonValueKind.Array)
        {
            selections = rootAssignments;
        }
        else if (!TryGetPropertyIgnoreCase(root, "selector", out var selector)
            || !TryGetPropertyIgnoreCase(
                selector,
                "facetSelections",
                out selections)
            || selections.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var allowedEvidence = entries.Select(static entry => entry.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parsedAssignments = selections.EnumerateArray()
            .Select(static selection => new FacetAssignment(
                ReadString(selection, "FacetId"),
                ReadString(selection, "Facet"),
                ReadStringArray(selection, "EvidenceIds")))
            .ToArray();
        Assert.InRange(parsedAssignments.Length, 2, 10);
        for (var index = 0; index < parsedAssignments.Length; index++)
        {
            Assert.Equal("F" + (index + 1), parsedAssignments[index].FacetId);
            Assert.False(string.IsNullOrWhiteSpace(parsedAssignments[index].Facet));
            Assert.InRange(parsedAssignments[index].EvidenceIds.Length, 1, 2);
            Assert.All(
                parsedAssignments[index].EvidenceIds,
                id => Assert.Contains(id, allowedEvidence));
        }

        return parsedAssignments;
    }

    private static StructuredFacetDecision[] ReadStructuredFacetDecisions(
        JsonElement root,
        IReadOnlyList<FacetAssignment> assignments)
    {
        if (!TryGetPropertyIgnoreCase(root, "decisions", out var decisions)
            || decisions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = decisions.EnumerateArray()
            .Select(static decision => new StructuredFacetDecision(
                ReadString(decision, "FacetId"),
                ReadString(decision, "Answer"),
                ReadStringArray(decision, "EvidenceIds"),
                TryGetPropertyIgnoreCase(
                    decision,
                    "IsPracticalInference",
                    out var inference)
                && inference.ValueKind is
                    JsonValueKind.True or JsonValueKind.False
                && inference.GetBoolean(),
                ReadString(decision, "DetectedLanguage")))
            .ToArray();
        Assert.Equal(assignments.Count, parsed.Length);
        for (var index = 0; index < parsed.Length; index++)
        {
            Assert.Equal(assignments[index].FacetId, parsed[index].FacetId);
            Assert.False(string.IsNullOrWhiteSpace(parsed[index].Answer));
        }

        return parsed;
    }

    private static string BuildFacetPrompt(
        string userRequest,
        IReadOnlyList<FacetAssignment> assignments,
        IReadOnlyList<EvidenceEntry> entries)
    {
        var byId = entries.ToDictionary(
            static entry => entry.EvidenceId,
            StringComparer.OrdinalIgnoreCase);
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_FACET_WRITER");
        prompt.AppendLine("DEMANDE UTILISATEUR:");
        prompt.AppendLine(userRequest);
        prompt.AppendLine();
        prompt.AppendLine("FACETTES ET PREUVES AUTORISEES:");
        foreach (var assignment in assignments)
        {
            prompt.Append(assignment.FacetId).Append('=').AppendLine(assignment.Facet);
            prompt.Append("allowed_evidence=").AppendLine(string.Join(",", assignment.EvidenceIds));
            foreach (var evidenceId in assignment.EvidenceIds)
            {
                var entry = byId[evidenceId];
                prompt.Append('[').Append(entry.EvidenceId).Append("] page=")
                    .Append(entry.PageStart)
                    .Append(" section=").Append(Compact(entry.SectionTitle, 100))
                    .Append(" texte=").AppendLine(Compact(entry.Text, 1200));
            }
        }
        prompt.AppendLine();
        prompt.AppendLine("CONTRAT DE REPONSE:");
        prompt.Append("- Produis exactement ").Append(assignments.Count)
            .AppendLine(" lignes, une par facette, dans l'ordre F1..Fn.");
        prompt.AppendLine("- Chaque ligne commence exactement par son identifiant suivi de deux-points, par exemple F1:.");
        prompt.AppendLine("- Réponds directement à la facette, pas à chaque extrait séparément; tu peux ignorer une preuve faible.");
        prompt.AppendLine("- OBLIGATOIRE: chaque ligne se termine par au moins un marqueur exact entre crochets parmi allowed_evidence, par exemple [A5] ou [A5][A6]. Toute ligne sans marqueur [A...] est invalide.");
        prompt.AppendLine("- Chaque affirmation de la ligne utilise seulement les preuves autorisées et ne cite que les marqueurs effectivement utilisés.");
        prompt.AppendLine("- Si la facette demande quand utiliser ou citer le document, commence cette ligne par une formulation de déduction pratique telle que 'En pratique, vous pouvez citer ce document lorsque...'. La source ne dit pas que le document doit ou devrait être cité: n'écris jamais cette obligation.");
        prompt.AppendLine("- Une seule phrase concise par ligne. Aucun titre, introduction, conclusion ou commentaire hors des lignes.");
        return prompt.ToString();
    }

    private static string BuildIsolatedFacetPrompt(
        string userRequest,
        FacetAssignment assignment,
        IReadOnlyList<EvidenceEntry> entries)
    {
        var byId = entries.ToDictionary(
            static entry => entry.EvidenceId,
            StringComparer.OrdinalIgnoreCase);
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_ISOLATED_FACET_WRITER");
        prompt.AppendLine("DEMANDE COMPLETE:");
        prompt.AppendLine(userRequest);
        prompt.AppendLine("FACETTE COURANTE:");
        prompt.AppendLine(assignment.Facet);
        prompt.AppendLine("PREUVES CANONIQUES AUTORISEES:");
        foreach (var evidenceId in assignment.EvidenceIds)
        {
            var entry = byId[evidenceId];
            prompt.Append('[').Append(entry.EvidenceId).Append("] page=")
                .Append(entry.PageStart)
                .Append(" section=").Append(Compact(entry.SectionTitle, 100))
                .Append(" texte=").AppendLine(Compact(entry.Text, 1200));
        }
        prompt.AppendLine("CONTRAT:");
        prompt.AppendLine("- Réponds uniquement à la facette courante en une seule phrase concise, sans titre ni préfixe F.");
        prompt.AppendLine("- Utilise exclusivement ces preuves; ne complète rien avec ta connaissance générale ou une autre facette.");
        prompt.AppendLine("- Termine obligatoirement par au moins un marqueur exact autorisé entre crochets, par exemple [A5] ou [A5][A6].");
        prompt.AppendLine("- Ne cite que les preuves réellement utilisées.");
        prompt.AppendLine("- Si la facette porte sur un cas d'usage, écris explicitement 'En pratique, vous pouvez...' ou une formulation équivalente de conseil; n'invente aucune obligation de citer.");
        return prompt.ToString();
    }

    private static string BuildStructuredFacetPrompt(
        string userRequest,
        FacetAssignment assignment,
        IReadOnlyList<EvidenceEntry> entries)
    {
        var byId = entries.ToDictionary(
            static entry => entry.EvidenceId,
            StringComparer.OrdinalIgnoreCase);
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_STRUCTURED_FACET_WRITER");
        prompt.AppendLine("DEMANDE COMPLETE:");
        prompt.AppendLine(userRequest);
        prompt.AppendLine("FACETTE COURANTE:");
        prompt.AppendLine(assignment.Facet);
        prompt.AppendLine("PREUVES CANONIQUES AUTORISEES:");
        foreach (var evidenceId in assignment.EvidenceIds)
        {
            var entry = byId[evidenceId];
            prompt.Append('[').Append(entry.EvidenceId).Append("] page=")
                .Append(entry.PageStart)
                .Append(" section=").Append(Compact(entry.SectionTitle, 100))
                .Append(" texte=").AppendLine(Compact(entry.Text, 1200));
        }
        prompt.AppendLine("Appelle submit_facet_answer exactement une fois.");
        prompt.AppendLine("answer: une seule phrase française concise répondant uniquement à la facette, sans marqueur ni préfixe F et sans connaissance extérieure.");
        prompt.AppendLine("evidenceIds: une ou deux preuves autorisées réellement utilisées.");
        prompt.AppendLine("isPracticalInference: true si answer formule un conseil d'usage déduit des preuves; false si answer rapporte directement leur contenu.");
        prompt.AppendLine("Une déduction pratique doit dire 'vous pouvez' ou l'équivalent et ne doit jamais inventer une obligation de citer.");
        return prompt.ToString();
    }

    private static SourceBackedAgentToolDefinition BuildFacetAnswerTool(
        FacetAssignment assignment)
        => new(
            "submit_facet_answer",
            "Submit one concise source-grounded answer for the current facet and the canonical evidence IDs actually used.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["answer"] = new
                    {
                        type = "string",
                        minLength = 12,
                        maxLength = 420
                    },
                    ["evidenceIds"] = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 2,
                        uniqueItems = true,
                        items = new
                        {
                            type = "string",
                            @enum = assignment.EvidenceIds
                        }
                    },
                    ["isPracticalInference"] = new
                    {
                        type = "boolean"
                    }
                },
                required = new[]
                {
                    "answer",
                    "evidenceIds",
                    "isPracticalInference"
                },
                additionalProperties = false
            }));

    private static string BuildFacetJudgePrompt(
        string userRequest,
        FacetAssignment assignment,
        StructuredFacetDecision initialDecision,
        IReadOnlyList<EvidenceEntry> entries)
    {
        var byId = entries.ToDictionary(
            static entry => entry.EvidenceId,
            StringComparer.OrdinalIgnoreCase);
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_FACET_EVIDENCE_JUDGE");
        prompt.AppendLine("DEMANDE COMPLETE:");
        prompt.AppendLine(userRequest);
        prompt.AppendLine("FACETTE COURANTE:");
        prompt.AppendLine(assignment.Facet);
        prompt.AppendLine("REPONSE A AUDITER:");
        prompt.AppendLine(initialDecision.Answer);
        prompt.Append("declared_evidence=").AppendLine(string.Join(",", initialDecision.EvidenceIds));
        prompt.Append("declared_practical_inference=").AppendLine(initialDecision.IsPracticalInference.ToString());
        prompt.AppendLine("PREUVES CANONIQUES AUTORISEES:");
        foreach (var evidenceId in assignment.EvidenceIds)
        {
            var entry = byId[evidenceId];
            prompt.Append('[').Append(entry.EvidenceId).Append("] page=")
                .Append(entry.PageStart)
                .Append(" section=").Append(Compact(entry.SectionTitle, 100))
                .Append(" texte=").AppendLine(Compact(entry.Text, 1200));
        }
        prompt.AppendLine("Appelle submit_facet_evidence_judgment exactement une fois.");
        prompt.AppendLine("- approved: la réponse est entièrement fidèle; recopie-la exactement dans finalAnswer.");
        prompt.AppendLine("- revised: corrige minimalement toute relation, portée ou modalité non soutenue, sans ajouter de connaissance extérieure.");
        prompt.AppendLine("- insufficient: les preuves ne permettent pas de répondre; finalAnswer et evidenceIds doivent alors être vides.");
        prompt.AppendLine("Une recommandation d'usage doit rester explicitement une possibilité ou un conseil déduit. Elle ne devient une obligation que si les preuves imposent explicitement cet usage ou cette citation.");
        prompt.AppendLine("evidenceIds contient seulement les preuves réellement utilisées par finalAnswer; isPracticalInference qualifie la réponse finale.");
        return prompt.ToString();
    }

    private static SourceBackedAgentToolDefinition BuildFacetJudgeTool(
        FacetAssignment assignment)
        => new(
            "submit_facet_evidence_judgment",
            "Approve, minimally revise, or reject one facet answer after comparing it with the allowed canonical evidence.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["verdict"] = new
                    {
                        type = "string",
                        @enum = new[] { "approved", "revised", "insufficient" }
                    },
                    ["finalAnswer"] = new
                    {
                        type = "string",
                        minLength = 0,
                        maxLength = 420
                    },
                    ["evidenceIds"] = new
                    {
                        type = "array",
                        minItems = 0,
                        maxItems = 2,
                        uniqueItems = true,
                        items = new
                        {
                            type = "string",
                            @enum = assignment.EvidenceIds
                        }
                    },
                    ["isPracticalInference"] = new
                    {
                        type = "boolean"
                    },
                    ["reasonCode"] = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "supported_direct",
                            "supported_inference",
                            "unsupported_modality_revised",
                            "unsupported_content_revised",
                            "insufficient_evidence"
                        }
                    }
                },
                required = new[]
                {
                    "verdict",
                    "finalAnswer",
                    "evidenceIds",
                    "isPracticalInference",
                    "reasonCode"
                },
                additionalProperties = false
            }));

    private static FacetWriterValidation ValidateFacetWriter(
        string raw,
        string finishReason,
        IReadOnlyList<FacetAssignment> assignments,
        IReadOnlyList<EvidenceEntry> entries)
    {
        Assert.Equal("stop", finishReason);
        var answer = (raw ?? string.Empty).Trim();
        Assert.False(string.IsNullOrWhiteSpace(answer));
        var lines = answer.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        Assert.Equal(assignments.Count, lines.Length);
        var allEvidenceIds = entries.Select(static entry => entry.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lineCitations = new List<string[]>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            Assert.Matches(
                new Regex(
                    "^" + Regex.Escape(assignments[index].FacetId) + @"\s*:\s*\S",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                lines[index]);
            var ids = Regex.Matches(
                    lines[index],
                    @"\[(?<id>A\d{1,3})\]",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(static match => match.Groups["id"].Value.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.NotEmpty(ids);
            Assert.All(ids, id => Assert.Contains(id, allEvidenceIds));
            var allowedForFacet = assignments[index].EvidenceIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            Assert.All(ids, id => Assert.Contains(id, allowedForFacet));
            lineCitations.Add(ids);
        }

        var detectedLanguage = LocalizedStrings.DetectLanguage(answer, "fr");
        Assert.Equal("fr", detectedLanguage);
        return new FacetWriterValidation(
            lines.Length,
            lineCitations.ToArray(),
            detectedLanguage);
    }

    private static IsolatedFacetValidation ValidateIsolatedFacetWriter(
        CompletionResult completion,
        FacetAssignment assignment,
        IReadOnlyList<EvidenceEntry> entries)
    {
        Assert.Equal("stop", completion.FinishReason);
        var answer = completion.Content.Trim();
        Assert.False(string.IsNullOrWhiteSpace(answer));
        var lines = answer.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        Assert.Single(lines);
        var ids = Regex.Matches(
                answer,
                @"\[(?<id>A\d{1,3})\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => match.Groups["id"].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.NotEmpty(ids);
        var allEvidenceIds = entries.Select(static entry => entry.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(ids, id => Assert.Contains(id, allEvidenceIds));
        var allowedForFacet = assignment.EvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        Assert.All(ids, id => Assert.Contains(id, allowedForFacet));
        var detectedLanguage = LocalizedStrings.DetectLanguage(answer, "fr");
        Assert.Equal("fr", detectedLanguage);
        return new IsolatedFacetValidation(
            assignment.FacetId,
            ids,
            detectedLanguage);
    }

    private static StructuredFacetDecision ValidateStructuredFacetWriter(
        SourceBackedAgentCompletion completion,
        FacetAssignment assignment,
        IReadOnlyList<EvidenceEntry> entries)
    {
        Assert.Equal("tool_calls", completion.FinishReason);
        Assert.True(string.IsNullOrWhiteSpace(completion.Content));
        var call = Assert.Single(completion.ToolCalls);
        Assert.Equal("submit_facet_answer", call.Name);
        var arguments = call.Arguments;
        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
        var answer = ReadString(arguments, "answer").Trim();
        Assert.InRange(answer.Length, 12, 420);
        Assert.Single(answer.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries));
        var evidenceIds = ReadStringArray(arguments, "evidenceIds");
        Assert.InRange(evidenceIds.Length, 1, 2);
        var allEvidenceIds = entries.Select(static entry => entry.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(evidenceIds, id => Assert.Contains(id, allEvidenceIds));
        var allowedForFacet = assignment.EvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        Assert.All(evidenceIds, id => Assert.Contains(id, allowedForFacet));
        Assert.True(TryGetPropertyIgnoreCase(
            arguments,
            "isPracticalInference",
            out var inferenceValue));
        Assert.True(inferenceValue.ValueKind is
            JsonValueKind.True or JsonValueKind.False);
        var isPracticalInference = inferenceValue.GetBoolean();
        var detectedLanguage = LocalizedStrings.DetectLanguage(answer, "fr");
        Assert.Equal("fr", detectedLanguage);
        return new StructuredFacetDecision(
            assignment.FacetId,
            answer,
            evidenceIds,
            isPracticalInference,
            detectedLanguage);
    }

    private static FacetJudgment ValidateFacetJudge(
        SourceBackedAgentCompletion completion,
        FacetAssignment assignment,
        StructuredFacetDecision initialDecision,
        IReadOnlyList<EvidenceEntry> entries)
    {
        Assert.Equal("tool_calls", completion.FinishReason);
        Assert.True(string.IsNullOrWhiteSpace(completion.Content));
        var call = Assert.Single(completion.ToolCalls);
        Assert.Equal("submit_facet_evidence_judgment", call.Name);
        var arguments = call.Arguments;
        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
        var verdict = ReadString(arguments, "verdict");
        Assert.Contains(verdict, new[]
        {
            "approved",
            "revised",
            "insufficient"
        });
        var finalAnswer = ReadString(arguments, "finalAnswer").Trim();
        var evidenceIds = ReadStringArray(arguments, "evidenceIds");
        var reasonCode = ReadString(arguments, "reasonCode");
        Assert.Contains(reasonCode, new[]
        {
            "supported_direct",
            "supported_inference",
            "unsupported_modality_revised",
            "unsupported_content_revised",
            "insufficient_evidence"
        });
        Assert.True(TryGetPropertyIgnoreCase(
            arguments,
            "isPracticalInference",
            out var inferenceValue));
        Assert.True(inferenceValue.ValueKind is
            JsonValueKind.True or JsonValueKind.False);
        var isPracticalInference = inferenceValue.GetBoolean();

        if (string.Equals(verdict, "insufficient", StringComparison.Ordinal))
        {
            Assert.Empty(finalAnswer);
            Assert.Empty(evidenceIds);
            Assert.Equal("insufficient_evidence", reasonCode);
            return new FacetJudgment(
                assignment.FacetId,
                verdict,
                finalAnswer,
                evidenceIds,
                isPracticalInference,
                reasonCode,
                string.Empty);
        }

        Assert.InRange(finalAnswer.Length, 12, 420);
        Assert.Single(finalAnswer.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries));
        if (string.Equals(verdict, "approved", StringComparison.Ordinal))
            Assert.Equal(initialDecision.Answer, finalAnswer);
        Assert.InRange(evidenceIds.Length, 1, 2);
        var allEvidenceIds = entries.Select(static entry => entry.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(evidenceIds, id => Assert.Contains(id, allEvidenceIds));
        var allowedForFacet = assignment.EvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        Assert.All(evidenceIds, id => Assert.Contains(id, allowedForFacet));
        var detectedLanguage = LocalizedStrings.DetectLanguage(finalAnswer, "fr");
        Assert.Equal("fr", detectedLanguage);
        return new FacetJudgment(
            assignment.FacetId,
            verdict,
            finalAnswer,
            evidenceIds,
            isPracticalInference,
            reasonCode,
            detectedLanguage);
    }

    private static WriterValidation Validate(
        string raw,
        string finishReason,
        IReadOnlyList<EvidenceEntry> entries)
    {
        Assert.Equal("stop", finishReason);
        var answer = (raw ?? string.Empty).Trim();
        Assert.False(string.IsNullOrWhiteSpace(answer));
        var lines = answer.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        Assert.InRange(lines.Length, 2, 6);
        Assert.All(lines, static line => Assert.Matches(
            new Regex(
                @"\[A\d{1,3}\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            line));

        var citedIds = Regex.Matches(
                answer,
                @"\[(?<id>A\d{1,3})\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => match.Groups["id"].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allowed = entries.Select(static entry => entry.EvidenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(citedIds, id => Assert.Contains(id, allowed));
        Assert.True(
            citedIds.Length >= 3,
            "The synthesis cited fewer than three distinct evidence items.");
        var detectedLanguage = LocalizedStrings.DetectLanguage(answer, "fr");
        Assert.Equal("fr", detectedLanguage);
        return new WriterValidation(
            lines.Length,
            citedIds,
            detectedLanguage);
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

    private static string[] ReadStringArray(
        JsonElement value,
        string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(value, propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim() ?? string.Empty)
            .Where(static item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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

    private sealed record EvidenceEntry(
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
        string SectionTitle,
        string Text);

    private sealed record FacetAssignment(
        string FacetId,
        string Facet,
        string[] EvidenceIds);

    private sealed record IsolatedFacetRun(
        FacetAssignment Assignment,
        int PromptCharacters,
        CompletionResult Completion);

    private sealed record StructuredFacetRun(
        FacetAssignment Assignment,
        int PromptCharacters,
        SourceBackedAgentCompletion Completion);

    private sealed record FacetJudgeRun(
        FacetAssignment Assignment,
        StructuredFacetDecision InitialDecision,
        int PromptCharacters,
        SourceBackedAgentCompletion Completion);

    private sealed record CompletionResult(
        string Content,
        long ElapsedMs,
        int PromptTokens,
        int CompletionTokens,
        string FinishReason);

    private sealed record WriterValidation(
        int LineCount,
        string[] CitedIds,
        string DetectedLanguage);

    private sealed record FacetWriterValidation(
        int LineCount,
        string[][] CitedIdsByFacet,
        string DetectedLanguage);

    private sealed record IsolatedFacetValidation(
        string FacetId,
        string[] CitedIds,
        string DetectedLanguage);

    private sealed record StructuredFacetDecision(
        string FacetId,
        string Answer,
        string[] EvidenceIds,
        bool IsPracticalInference,
        string DetectedLanguage);

    private sealed record FacetJudgment(
        string FacetId,
        string Verdict,
        string FinalAnswer,
        string[] EvidenceIds,
        bool IsPracticalInference,
        string ReasonCode,
        string DetectedLanguage);
}
