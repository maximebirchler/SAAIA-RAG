using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;
using Xunit.Abstractions;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveSourceBackedSemanticGranularityMicroprobeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Live_qwen3_independent_candidate_audit_is_invariant_to_candidate_order_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_INDEPENDENT_AUDIT_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_INDEPENDENT_AUDIT_PROBE=1 "
                + "to run the independent candidate-audit microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The independent candidate-audit microprobe requires the local LLM URL and model.");
        }
        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 24,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900,
                MaximumContextTokens: 8192,
                MaximumSemanticCandidatesPerAuditTurn: 24,
                MaximumSemanticCandidateAuditConcurrency: 2));
        var original = BuildTypedAuditEvidence();
        var expectedIds = Enumerable.Range(17, 8)
            .Select(static index => "E" + index)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedDisplayValues = original
            .Where(item => expectedIds.Contains(
                item.EvidenceId,
                StringComparer.OrdinalIgnoreCase))
            .ToDictionary(
                static item => item.EvidenceId,
                static item => item.MatchedContentCards!.Value[0]
                    .GetProperty("title")
                    .GetString()!,
                StringComparer.OrdinalIgnoreCase);
        var report = new StringBuilder()
            .AppendLine("LIVE INDEPENDENT CANDIDATE AUDIT ORDER-INVARIANCE MICROPROBE")
            .AppendLine("MODEL: " + model);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        foreach (var rotation in new[] { 0, 8, 16 })
        {
            var candidates = original
                .Skip(rotation)
                .Concat(original.Take(rotation))
                .ToArray();
            var execution = await InvokePrivateAsync(
                runner,
                "CompleteIndependentCandidateAuditAsync",
                "recette",
                "Le libelle exact nomme une recette particuliere et autonome, pas un type de repas, une rubrique, une collection ni une position.",
                candidates,
                2,
                cts.Token);
            var completion = ReadProperty<SourceBackedAgentCompletion>(
                execution,
                "Completion");
            var llmCallCount = ReadProperty<int>(execution, "LlmCallCount");
            var labelDecisionCount = ReadProperty<int>(
                execution,
                "LabelDecisionCount");
            var labelVerificationCount = ReadProperty<int>(
                execution,
                "LabelVerificationCount");
            var labelArbitrationCount = ReadProperty<int>(
                execution,
                "LabelArbitrationCount");
            var decision = ReadProperty<object>(execution, "Decision");
            var valid = ReadProperty<bool>(decision, "ProtocolValid");
            var failureReason = decision.GetType()
                .GetProperty("FailureReason")!
                .GetValue(decision) as string;
            var approvedDisplayValues = ParseApprovedDisplayValues(
                completion.Content);
            var approvedIds = approvedDisplayValues.Keys.ToArray();
            report.AppendLine("ROTATION: " + rotation)
                .AppendLine("PROMPT_TOKENS: " + completion.PromptTokens)
                .AppendLine("COMPLETION_TOKENS: " + completion.CompletionTokens)
                .AppendLine("LLM_CALLS: " + llmCallCount)
                .AppendLine("LABEL_DECISIONS: " + labelDecisionCount)
                .AppendLine("LABEL_VERIFICATIONS: " + labelVerificationCount)
                .AppendLine("LABEL_ARBITRATIONS: " + labelArbitrationCount)
                .AppendLine("PROTOCOL_VALID: " + valid)
                .AppendLine("FAILURE_REASON: " + failureReason)
                .AppendLine("APPROVED_IDS: " + string.Join(",", approvedIds))
                .AppendLine(
                    "APPROVED_DISPLAY_VALUES: "
                    + string.Join(
                        " | ",
                        approvedDisplayValues.Select(static pair =>
                            pair.Key + "=" + pair.Value)))
                .AppendLine("RAW:")
                .AppendLine(completion.Content)
                .AppendLine();
            output.WriteLine(
                "ROTATION " + rotation
                + " CALLS=" + llmCallCount
                + " LABEL_VERIFICATIONS=" + labelVerificationCount
                + " LABEL_ARBITRATIONS=" + labelArbitrationCount
                + " RAW: " + completion.Content);

            Assert.True(valid, failureReason + ": " + completion.Content);
            Assert.Equal(
                expectedIds,
                approvedIds
                    .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            Assert.Equal(
                expectedDisplayValues
                    .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(static pair => pair.Value),
                approvedDisplayValues
                    .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(static pair => pair.Value));
        }

        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine("Artifact: " + artifact);
    }

    [Fact]
    public async Task Live_qwen3_independent_candidate_audit_selects_the_standalone_parent_label_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_INDEPENDENT_AUDIT_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_INDEPENDENT_AUDIT_PROBE=1 "
                + "to run the difficult-label candidate-audit microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The difficult-label audit microprobe requires the local LLM URL and model.");
        }

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 12,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900,
                MaximumContextTokens: 8192,
                MaximumSemanticCandidatesPerAuditTurn: 12,
                MaximumSemanticCandidateAuditConcurrency: 2));
        var original = BuildDifficultLabelAuditEvidence();
        var required = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["E1"] = "Tartiflette",
            ["E2"] = "Wok de nouilles aux crevettes",
            ["E3"] = "Saumon avec sauce yaourt-menthe",
            ["E7"] = "Ail confit",
            ["E8"] = "Boulettes de viande suedoises"
        };
        var report = new StringBuilder()
            .AppendLine("LIVE INDEPENDENT CANDIDATE AUDIT DIFFICULT-LABEL MICROPROBE")
            .AppendLine("MODEL: " + model);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        foreach (var rotation in new[] { 0, 3, 6 })
        {
            var candidates = original
                .Skip(rotation)
                .Concat(original.Take(rotation))
                .ToArray();
            var execution = await InvokePrivateAsync(
                runner,
                "CompleteIndependentCandidateAuditAsync",
                "recette",
                "Le libelle choisi nomme une recette particuliere et autonome, pas un document, une rubrique, une quantite ni une etape.",
                candidates,
                1,
                cts.Token);
            var completion = ReadProperty<SourceBackedAgentCompletion>(
                execution,
                "Completion");
            var decision = ReadProperty<object>(execution, "Decision");
            var valid = ReadProperty<bool>(decision, "ProtocolValid");
            var failureReason = decision.GetType()
                .GetProperty("FailureReason")!
                .GetValue(decision) as string;
            var displayValues = ParseApprovedDisplayValues(completion.Content);
            report.AppendLine("ROTATION: " + rotation)
                .AppendLine("PROTOCOL_VALID: " + valid)
                .AppendLine("FAILURE_REASON: " + failureReason)
                .AppendLine("RAW:")
                .AppendLine(completion.Content)
                .AppendLine();

            Assert.True(valid, failureReason + ": " + completion.Content);
            foreach (var pair in required)
            {
                Assert.True(
                    displayValues.TryGetValue(pair.Key, out var actual),
                    "Missing required semantic selection " + pair.Key
                    + ": " + completion.Content);
                Assert.Equal(pair.Value, actual);
            }
            Assert.DoesNotContain("E5", displayValues.Keys);
            Assert.DoesNotContain("E6", displayValues.Keys);
            Assert.DoesNotContain("E9", displayValues.Keys);
            if (displayValues.TryGetValue("E4", out var optionalParent))
                Assert.Equal("Veloute de courge", optionalParent);
            Assert.InRange(displayValues.Count, required.Count, required.Count + 1);
        }

        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine("Artifact: " + artifact);
    }

    [Fact]
    public async Task Live_qwen3_candidate_label_resolution_recovers_named_parents_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_PARENT_REVIEW_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_PARENT_REVIEW_PROBE=1 "
                + "to run the candidate label-resolution microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The candidate label-resolution microprobe requires the local LLM URL and model.");
        }

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 12,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900,
                MaximumContextTokens: 8192,
                MaximumSemanticCandidatesPerAuditTurn: 12,
                MaximumSemanticCandidateAuditConcurrency: 1));
        var candidates = BuildDifficultLabelAuditEvidence();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var execution = await InvokePrivateAsync(
            runner,
            "CompleteCandidateLabelResolutionAsync",
            candidates,
            cts.Token);
        var completions = ReadProperty<IReadOnlyList<SourceBackedAgentCompletion>>(
            execution,
            "Completions");
        var decision = ReadProperty<object>(execution, "Decision");
        var valid = ReadProperty<bool>(decision, "ProtocolValid");
        var failureReason = decision.GetType()
            .GetProperty("FailureReason")!
            .GetValue(decision) as string;
        var approved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var approvals = (System.Collections.IEnumerable)decision.GetType()
            .GetProperty("ApprovedCandidates")!
            .GetValue(decision)!;
        foreach (var approval in approvals)
        {
            var type = approval!.GetType();
            var id = (string)type.GetProperty("EvidenceId")!.GetValue(approval)!;
            var label = (string)type.GetProperty("DisplayValue")!.GetValue(approval)!;
            approved[id] = label;
        }

        var report = new StringBuilder()
            .AppendLine("LIVE CANDIDATE LABEL-RESOLUTION MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("PROTOCOL_VALID: " + valid)
            .AppendLine("FAILURE_REASON: " + failureReason)
            .AppendLine("APPROVED: " + string.Join(
                " | ",
                approved.Select(static pair => pair.Key + "=" + pair.Value)))
            .AppendLine("RAW:");
        foreach (var completion in completions)
            report.AppendLine(completion.Content);
        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine(report.ToString());
        output.WriteLine("Artifact: " + artifact);

        Assert.True(valid, failureReason + ": " + string.Join(
            Environment.NewLine,
            completions.Select(static completion => completion.Content)));
        Assert.Equal("Tartiflette", approved["E1"]);
        Assert.Equal("Wok de nouilles aux crevettes", approved["E2"]);
        Assert.Equal("Saumon avec sauce yaourt-menthe", approved["E3"]);
        Assert.Equal("Veloute de courge", approved["E4"]);
        Assert.Equal("Ail confit", approved["E7"]);
        Assert.Equal("Boulettes de viande suedoises", approved["E8"]);
        Assert.Equal(candidates.Count, approved.Count);
    }

    [Fact]
    public async Task Live_qwen3_role_audit_separates_placement_labels_from_source_content_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_ROLE_AUDIT_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_ROLE_AUDIT_PROBE=1 "
                + "to run the placement-role audit microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The placement-role audit microprobe requires the local LLM URL and model.");
        }

        var resolvedLabels = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["E1"] = "Tartiflette",
            ["E2"] = "Wok de nouilles aux crevettes",
            ["E3"] = "Saumon avec sauce yaourt-menthe",
            ["E4"] = "Veloute de courge"
        };
        var candidates = BuildDifficultLabelAuditEvidence()
            .Select(candidate =>
            {
                if (!resolvedLabels.TryGetValue(
                        candidate.EvidenceId,
                        out var resolvedLabel))
                {
                    return candidate;
                }

                var hints = new Dictionary<string, string>(
                    candidate.SelectionHints,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["sourceAnchorLabel"] = resolvedLabel
                };
                return candidate with { SelectionHints = hints };
            })
            .ToList();
        candidates.Add(BuildEvidence(
            10,
            "Collation (vers 16h)",
            "Cuisine/audit-libelles-difficiles.pdf",
            10,
            "Rubrique de placement horaire dans un planning."));
        candidates.Add(BuildEvidence(
            11,
            "Nettoyer les surfaces puis serrer les quatre vis avant de remettre le capot.",
            "Technique/audit-libelles-difficiles.pdf",
            11,
            "Phrase de procedure extraite au milieu d'une section."));
        candidates.Add(BuildEvidence(
            12,
            "PIECES SOUDEES AUX SUPPORTS 60min 4",
            "Technique/audit-libelles-difficiles.pdf",
            12,
            "Titre autonome en majuscules avec duree et nombre."));
        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 12,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900,
                MaximumContextTokens: 8192,
                MaximumSemanticCandidateAuditConcurrency: 2));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var execution = await InvokePrivateAsync(
            runner,
            "CompleteResolvedCandidateRoleAuditAsync",
            "Jour",
            new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
            new[] { "Petit-dejeuner", "Dejeuner", "Collation", "Souper" },
            candidates,
            cts.Token);
        var decision = ReadProperty<object>(execution, "Decision");
        var valid = ReadProperty<bool>(decision, "ProtocolValid");
        var rejectedIds = ReadProperty<IReadOnlyList<string>>(
            decision,
            "RejectedEvidenceIds");
        var approvals = (System.Collections.IEnumerable)decision.GetType()
            .GetProperty("ApprovedCandidates")!
            .GetValue(decision)!;
        var approvedIds = approvals.Cast<object>()
            .Select(approval => (string)approval.GetType()
                .GetProperty("EvidenceId")!
                .GetValue(approval)!)
            .ToArray();
        var completions = ReadProperty<IReadOnlyList<SourceBackedAgentCompletion>>(
            execution,
            "Completions");
        var report = new StringBuilder()
            .AppendLine("LIVE PLACEMENT-ROLE AUDIT MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("PROTOCOL_VALID: " + valid)
            .AppendLine("APPROVED_IDS: " + string.Join(" | ", approvedIds))
            .AppendLine("REJECTED_IDS: " + string.Join(" | ", rejectedIds))
            .AppendLine("RAW:")
            .AppendLine(string.Join(
                Environment.NewLine,
                completions.Select(static completion => completion.Content)));
        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine(report.ToString());
        output.WriteLine("Artifact: " + artifact);

        Assert.True(valid);
        Assert.All(
            new[] { "E1", "E2", "E3", "E4", "E7", "E8", "E12" },
            id => Assert.Contains(id, approvedIds));
        Assert.All(
            new[] { "E10", "E11" },
            id => Assert.Contains(id, rejectedIds));
    }

    [Fact]
    public async Task Live_qwen3_resolved_candidate_audit_accepts_named_items_and_rejects_layout_labels_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_RESOLVED_AUDIT_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_RESOLVED_AUDIT_PROBE=1 "
                + "to run the resolved-label candidate-audit microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The resolved-label audit microprobe requires the local LLM URL and model.");
        }

        var resolvedLabels = new[]
        {
            "Tartiflette",
            "Wok de nouilles aux crevettes",
            "Saumon avec sauce yaourt-menthe",
            "Veloute de courge",
            "JE CUISINE simplement",
            "MES RECETTES INTERNATIONALES",
            "Ail confit",
            "Boulettes de viande suedoises",
            "PETITS DEJEUNERS"
        };
        var candidates = BuildDifficultLabelAuditEvidence()
            .Select((candidate, index) => candidate with
            {
                SelectionHints = new Dictionary<string, string>(
                    candidate.SelectionHints,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["sourceAnchorLabel"] = resolvedLabels[index]
                }
            })
            .ToList();
        var qualifiedPlacementRole = BuildEvidence(
            10,
            "Collation (vers 16h)",
            "Cuisine/audit-libelles-difficiles.pdf",
            10,
            "Rubrique de placement horaire dans un planning.");
        candidates.Add(qualifiedPlacementRole with
        {
            SelectionHints = new Dictionary<string, string>(
                qualifiedPlacementRole.SelectionHints,
                StringComparer.OrdinalIgnoreCase)
            {
                ["sourceAnchorLabel"] = "Collation (vers 16h)"
            }
        });
        var intake = BuildWideMealPlanIntake();
        var messagesMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildResolvedCandidateAuditMessages",
            BindingFlags.Static | BindingFlags.NonPublic);
        var contractMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildResolvedCandidateAuditContract",
            BindingFlags.Static | BindingFlags.NonPublic);
        var parserMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "ReadResolvedCandidateAuditDecision",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(messagesMethod);
        Assert.NotNull(contractMethod);
        Assert.NotNull(parserMethod);

        var messages = Assert.IsAssignableFrom<IReadOnlyList<SourceBackedAgentMessage>>(
            messagesMethod!.Invoke(
                null,
                new object[]
                {
                    intake,
                    BuildAbstractMealSlotSemanticPlan(),
                    "repas_journalier",
                    "Le libelle exact doit nommer une instance de repas_journalier telle "
                    + "qu'elle est decrite dans la source originale, sans reference a un "
                    + "jour, une periode, un type de repas, une categorie, une sous-partie "
                    + "ou une etape finale.",
                    "Jour",
                    new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
                    new[] { "Petit-dejeuner", "Dejeuner", "Collation", "Souper" },
                    candidates
                }));
        var contract = Assert.IsType<LlmStructuredOutputContract>(
            contractMethod!.Invoke(null, new object[] { candidates }));
        var llm = new OpenAiLlmClient();
        llm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var adapter = new NativeLlmAdapter(llm);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var completion = await adapter.CompleteStructuredAsync(
            messages,
            contract,
            256,
            cts.Token,
            temperatureOverride: 0);
        var decision = parserMethod!.Invoke(
            null,
            new object[] { completion, candidates });
        Assert.NotNull(decision);
        var valid = ReadProperty<bool>(decision!, "ProtocolValid");
        var failureReason = decision!.GetType()
            .GetProperty("FailureReason")!
            .GetValue(decision) as string;
        var approvals = (System.Collections.IEnumerable)decision.GetType()
            .GetProperty("ApprovedCandidates")!
            .GetValue(decision)!;
        var approvedIds = approvals.Cast<object>()
            .Select(approval => (string)approval.GetType()
                .GetProperty("EvidenceId")!
                .GetValue(approval)!)
            .ToArray();
        var rejectedIds = ReadProperty<IReadOnlyList<string>>(
            decision,
            "RejectedEvidenceIds");

        var report = new StringBuilder()
            .AppendLine("LIVE RESOLVED-LABEL CANDIDATE AUDIT MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("PROTOCOL_VALID: " + valid)
            .AppendLine("FAILURE_REASON: " + failureReason)
            .AppendLine("APPROVED_IDS: " + string.Join(" | ", approvedIds))
            .AppendLine("REJECTED_IDS: " + string.Join(" | ", rejectedIds))
            .AppendLine("RAW:")
            .AppendLine(completion.Content);
        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine(report.ToString());
        output.WriteLine("Artifact: " + artifact);

        Assert.True(valid, failureReason + ": " + completion.Content);
        Assert.Equal(new[] { "E1", "E2", "E3", "E4", "E8" }, approvedIds);
        Assert.Equal(new[] { "E5", "E6", "E7", "E9", "E10" }, rejectedIds);
    }

    private static IReadOnlyDictionary<string, string> ParseApprovedDisplayValues(
        string? content)
        => Regex.Matches(
                content ?? string.Empty,
                @"(?m)^(E\d+)=(.+)$")
            .ToDictionary(
                static match => match.Groups[1].Value,
                static match => match.Groups[2].Value.Trim(),
                StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Live_qwen3_grouped_resolved_candidate_audit_separates_recipes_from_non_recipes_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_GROUPED_RESOLVED_AUDIT_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_GROUPED_RESOLVED_AUDIT_PROBE=1 "
                + "to run the grouped resolved-label audit microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The grouped resolved-label audit probe requires the local LLM URL and model.");
        }

        var candidates = BuildTypedAuditEvidence()
            .Take(20)
            .Select(candidate => candidate with
            {
                SelectionHints = new Dictionary<string, string>(
                    candidate.SelectionHints,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["sourceAnchorLabel"] = candidate.MatchedContentCards!
                        .Value[0]
                        .GetProperty("title")
                        .GetString()!
                }
            })
            .ToArray();
        var expectedApprovedIds = Enumerable.Range(17, 4)
            .Select(static index => "E" + index)
            .ToArray();
        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 20,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900,
                MaximumContextTokens: 4096,
                MaximumSemanticCandidatesPerAuditTurn: 20,
                SemanticCandidateLabelResolutionEnabled: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var execution = await InvokePrivateAsync(
            runner,
            "CompleteBatchedCandidateAuditAsync",
            BuildWideMealPlanIntake(),
            BuildWideMealPlanPlan(),
            "repas_journalier",
            "Le libelle exact doit nommer une instance de repas_journalier telle "
            + "qu'elle est decrite dans une source documentaire, sans reference "
            + "a un jour, un moment, une categorie ou un role; une recette ou une "
            + "composition alimentaire particuliere est une instance valide.",
            "Jour",
            new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
            new[] { "Petit-dejeuner", "Dejeuner", "Collation", "Souper" },
            candidates,
            cts.Token);
        var completion = ReadProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        var decision = ReadProperty<object>(execution, "Decision");
        var llmCallCount = ReadProperty<int>(execution, "LlmCallCount");
        var elapsedMilliseconds = ReadProperty<long>(
            execution,
            "ElapsedMilliseconds");
        var valid = ReadProperty<bool>(decision, "ProtocolValid");
        var approvals = (System.Collections.IEnumerable)decision.GetType()
            .GetProperty("ApprovedCandidates")!
            .GetValue(decision)!;
        var approvedIds = approvals.Cast<object>()
            .Select(approval => (string)approval.GetType()
                .GetProperty("EvidenceId")!
                .GetValue(approval)!)
            .ToArray();
        var rejectedIds = ReadProperty<IReadOnlyList<string>>(
            decision,
            "RejectedEvidenceIds");

        var report = new StringBuilder()
            .AppendLine("LIVE GROUPED RESOLVED-LABEL CANDIDATE AUDIT MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("CANDIDATE_COUNT: " + candidates.Length)
            .AppendLine("LLM_CALLS: " + llmCallCount)
            .AppendLine("ELAPSED_MS: " + elapsedMilliseconds)
            .AppendLine("PROTOCOL_VALID: " + valid)
            .AppendLine("EXPECTED_APPROVED_IDS: " + string.Join(",", expectedApprovedIds))
            .AppendLine("APPROVED_IDS: " + string.Join(",", approvedIds))
            .AppendLine("REJECTED_IDS: " + string.Join(",", rejectedIds))
            .AppendLine("RAW:")
            .AppendLine(completion.Content);
        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine("Artifact: " + artifact);

        Assert.True(valid, completion.Content);
        Assert.Equal(1, llmCallCount);
        Assert.Equal(expectedApprovedIds, approvedIds);
    }

    [Fact]
    public async Task Live_qwen3_typed_candidate_audit_separates_target_items_from_layout_labels_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_TYPED_AUDIT_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_TYPED_AUDIT_PROBE=1 "
                + "to run the typed candidate-audit microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The typed candidate-audit microprobe requires the local LLM URL and model.");
        }

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 20,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 1200,
                MaximumContextTokens: 8192));
        var candidates = BuildTypedAuditEvidence();
        var maximumConcurrency = int.TryParse(
            Environment.GetEnvironmentVariable(
                "SAAIA_LIVE_TYPED_AUDIT_CONCURRENCY"),
            out var configuredConcurrency)
            ? Math.Clamp(configuredConcurrency, 1, 2)
            : 1;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var execution = await InvokePrivateAsync(
            runner,
            "CompleteIndependentCandidateAuditAsync",
            "recette",
            "Le libelle exact nomme une recette particuliere et autonome, pas un type de repas, une rubrique, une collection ni une position.",
            candidates,
            maximumConcurrency,
            cts.Token);
        var completion = ReadProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        var observedConcurrency = ReadProperty<int>(
            execution,
            "ObservedConcurrency");
        var elapsedMilliseconds = ReadProperty<long>(
            execution,
            "ElapsedMilliseconds");
        var decision = ReadProperty<object>(execution, "Decision");
        var protocolValid = ReadProperty<bool>(decision, "ProtocolValid");
        var llmCallCount = ReadProperty<int>(execution, "LlmCallCount");
        var labelDecisionCount = ReadProperty<int>(
            execution,
            "LabelDecisionCount");
        var labelVerificationCount = ReadProperty<int>(
            execution,
            "LabelVerificationCount");
        var labelArbitrationCount = ReadProperty<int>(
            execution,
            "LabelArbitrationCount");
        var approvedIds = Regex.Matches(
                completion.Content ?? string.Empty,
                @"(?m)^(E\d+)=")
            .Select(static match => match.Groups[1].Value)
            .ToArray();
        var report = new StringBuilder()
            .AppendLine("LIVE TYPED CANDIDATE AUDIT MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("TYPE_CIBLE: recette")
            .AppendLine("MAXIMUM_CONCURRENCY: " + maximumConcurrency)
            .AppendLine("OBSERVED_CONCURRENCY: " + observedConcurrency)
            .AppendLine("ELAPSED_MS: " + elapsedMilliseconds)
            .AppendLine("LLM_CALLS: " + llmCallCount)
            .AppendLine("LABEL_DECISIONS: " + labelDecisionCount)
            .AppendLine("LABEL_VERIFICATIONS: " + labelVerificationCount)
            .AppendLine("LABEL_ARBITRATIONS: " + labelArbitrationCount)
            .AppendLine("PROTOCOL_VALID: " + protocolValid)
            .AppendLine("FINISH_REASON: " + completion.FinishReason)
            .AppendLine("CONTENT: " + completion.Content)
            .AppendLine("CANDIDATES: " + string.Join(
                " | ",
                candidates.Select(static item =>
                    item.EvidenceId + "=" + item.MatchedContentCards!.Value[0]
                        .GetProperty("title").GetString())))
            .AppendLine("APPROVED_IDS: " + string.Join(" | ", approvedIds));
        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine("Artifact: " + artifact);

        Assert.True(protocolValid, "Audit protocol error: " + completion.Content);
        Assert.Equal(
            Math.Min(maximumConcurrency, candidates.Count),
            observedConcurrency);
        Assert.Equal(candidates.Count, labelDecisionCount);
        Assert.InRange(
            llmCallCount,
            labelDecisionCount + labelVerificationCount + labelArbitrationCount,
            2 * (labelDecisionCount + labelVerificationCount + candidates.Count));
        Assert.True(labelVerificationCount > 0);
        Assert.Equal(
            Enumerable.Range(17, 8).Select(static index => "E" + index),
            approvedIds);
    }

    [Fact]
    public async Task Live_qwen3_batched_candidate_audit_compares_wide_and_small_batches_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_BATCHED_AUDIT_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_BATCHED_AUDIT_PROBE=1 "
                + "to compare wide and small candidate-audit batches.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The batched candidate-audit probe requires the local LLM URL and model.");
        }

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var configuredContextTokens = int.TryParse(
            Environment.GetEnvironmentVariable(
                "SAAIA_LIVE_BATCHED_AUDIT_CONTEXT_TOKENS"),
            out var parsedContextTokens)
                ? Math.Clamp(parsedContextTokens, 2048, 32768)
                : 4096;
        var configuredCandidateCount = int.TryParse(
            Environment.GetEnvironmentVariable(
                "SAAIA_LIVE_BATCHED_AUDIT_CANDIDATES"),
            out var parsedCandidateCount)
                ? Math.Clamp(parsedCandidateCount, 1, 80)
                : 20;
        var labelResolutionEnabled = string.Equals(
            Environment.GetEnvironmentVariable(
                "SAAIA_LIVE_BATCHED_AUDIT_LABEL_RESOLUTION"),
            "1",
            StringComparison.Ordinal);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: configuredCandidateCount,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900,
                MaximumContextTokens: configuredContextTokens,
                MaximumSemanticCandidatesPerAuditTurn: configuredCandidateCount,
                MaximumSemanticCandidateAuditConcurrency: 1,
                SemanticCandidateLabelResolutionEnabled: labelResolutionEnabled,
                MaximumSemanticCandidatesPerAuditBatch: configuredCandidateCount));
        var intake = BuildWideMealPlanIntake();
        var typedAuditEvidence = BuildTypedAuditEvidence();
        var candidates = Enumerable.Range(0, configuredCandidateCount)
            .Select(index =>
            {
                var source = typedAuditEvidence[index % typedAuditEvidence.Count];
                return source with
                {
                    EvidenceId = "E" + (index + 1),
                    DocId = "typed-audit-doc-" + (index + 1),
                    PageStart = index + 1,
                    PageEnd = index + 1,
                    ChunkId = "typed-audit-chunk-" + (index + 1),
                    Rank = index + 1
                };
            })
            .ToArray();
        var expectedIds = Enumerable.Range(0, candidates.Length)
            .Where(index => index % typedAuditEvidence.Count >= 16)
            .Select(static index => "E" + (index + 1))
            .ToArray();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var report = new StringBuilder()
            .AppendLine("LIVE BATCHED CANDIDATE AUDIT GRANULARITY MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("CONTEXT_TOKENS: " + configuredContextTokens)
            .AppendLine("CANDIDATE_COUNT: " + candidates.Length)
            .AppendLine("LABEL_RESOLUTION_ENABLED: " + labelResolutionEnabled)
            .AppendLine("SEMANTIC_MISSION_INCLUDED: true")
            .AppendLine("EXPECTED_APPROVED_IDS: " + string.Join(",", expectedIds));
        const string candidateObjectType = "repas_journalier";
        const string candidateEligibilityRule =
            "Le libelle exact doit nommer une instance de repas_journalier telle "
            + "qu'elle est decrite dans une source documentaire, sans reference "
            + "a un jour, un moment, une categorie ou un role; une recette ou une "
            + "composition alimentaire particuliere est une instance valide.";

        var wideExecution = await InvokePrivateAsync(
            runner,
            "CompleteBatchedCandidateAuditAsync",
            intake,
            BuildWideMealPlanPlan(),
            candidateObjectType,
            candidateEligibilityRule,
            "Jour",
            new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
            new[] { "Petit-dejeuner", "Dejeuner", "Collation", "Souper" },
            candidates,
            cts.Token);
        var wideCompletion = ReadProperty<SourceBackedAgentCompletion>(
            wideExecution,
            "Completion");
        var wideDecision = ReadProperty<object>(wideExecution, "Decision");
        var wideProtocolValid = ReadProperty<bool>(wideDecision, "ProtocolValid");
        var wideApprovedIds = ParseApprovedDisplayValues(wideCompletion.Content)
            .Keys
            .ToArray();
        report.AppendLine()
            .AppendLine("WIDE_BATCH_SIZE: " + candidates.Length)
            .AppendLine("WIDE_PROTOCOL_VALID: " + wideProtocolValid)
             .AppendLine("WIDE_LLM_CALLS: " + ReadProperty<int>(wideExecution, "LlmCallCount"))
             .AppendLine("WIDE_EXECUTED_BATCHES: " + ReadProperty<int>(wideExecution, "ExecutedBatchCount"))
             .AppendLine("WIDE_INPUT_BUDGET_SPLITS: " + ReadProperty<int>(wideExecution, "InputBudgetSplitCount"))
             .AppendLine("WIDE_LARGEST_EXECUTED_BATCH: " + ReadProperty<int>(wideExecution, "LargestExecutedBatchCandidateCount"))
             .AppendLine("WIDE_MAXIMUM_MEASURED_INPUT_TOKENS: "
                 + (wideExecution.GetType()
                         .GetProperty("MaximumMeasuredInputTokens")!
                         .GetValue(wideExecution)?.ToString()
                    ?? "unavailable"))
             .AppendLine("WIDE_ELAPSED_MS: " + ReadProperty<long>(wideExecution, "ElapsedMilliseconds"))
            .AppendLine("WIDE_PROMPT_TOKENS: " + wideCompletion.PromptTokens)
            .AppendLine("WIDE_COMPLETION_TOKENS: " + wideCompletion.CompletionTokens)
            .AppendLine("WIDE_APPROVED_IDS: " + string.Join(",", wideApprovedIds))
            .AppendLine("WIDE_RAW:")
            .AppendLine(wideCompletion.Content);

        var skipSmallBatches = string.Equals(
            Environment.GetEnvironmentVariable(
                "SAAIA_LIVE_BATCHED_AUDIT_SKIP_SMALL"),
            "1",
            StringComparison.Ordinal);
        var smallApprovedIds = new List<string>();
        var smallProtocolValid = true;
        var smallLlmCalls = 0;
        long smallElapsedMilliseconds = 0;
        var smallBatchIndex = 0;
        foreach (var batch in skipSmallBatches
                     ? Array.Empty<EvidenceItem[]>()
                     : candidates.Chunk(10))
        {
            smallBatchIndex++;
            var execution = await InvokePrivateAsync(
                runner,
                "CompleteBatchedCandidateAuditAsync",
                intake,
                BuildWideMealPlanPlan(),
                candidateObjectType,
                candidateEligibilityRule,
                "Jour",
                new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
                new[] { "Petit-dejeuner", "Dejeuner", "Collation", "Souper" },
                batch,
                cts.Token);
            var completion = ReadProperty<SourceBackedAgentCompletion>(
                execution,
                "Completion");
            var decision = ReadProperty<object>(execution, "Decision");
            var protocolValid = ReadProperty<bool>(decision, "ProtocolValid");
            var approvedIds = ParseApprovedDisplayValues(completion.Content)
                .Keys
                .ToArray();
            smallProtocolValid &= protocolValid;
            smallApprovedIds.AddRange(approvedIds);
            smallLlmCalls += ReadProperty<int>(execution, "LlmCallCount");
            smallElapsedMilliseconds += ReadProperty<long>(
                execution,
                "ElapsedMilliseconds");
            report.AppendLine()
                .AppendLine("SMALL_BATCH_" + smallBatchIndex + "_SIZE: " + batch.Length)
                .AppendLine("SMALL_BATCH_" + smallBatchIndex + "_PROTOCOL_VALID: " + protocolValid)
                .AppendLine("SMALL_BATCH_" + smallBatchIndex + "_APPROVED_IDS: " + string.Join(",", approvedIds))
                .AppendLine("SMALL_BATCH_" + smallBatchIndex + "_RAW:")
                .AppendLine(completion.Content);
        }
        report.AppendLine()
            .AppendLine("SMALL_BATCH_SIZE: 10")
            .AppendLine("SMALL_BATCHES_SKIPPED: " + skipSmallBatches)
            .AppendLine("SMALL_PROTOCOL_VALID: " + smallProtocolValid)
            .AppendLine("SMALL_LLM_CALLS: " + smallLlmCalls)
            .AppendLine("SMALL_ELAPSED_MS: " + smallElapsedMilliseconds)
            .AppendLine("SMALL_APPROVED_IDS: " + string.Join(",", smallApprovedIds));

        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine("Artifact: " + artifact);

        Assert.True(skipSmallBatches || smallProtocolValid,
            "Every small audit protocol must remain valid.");
        Assert.Equal(candidates.Length, ReadProperty<int>(wideExecution, "CandidateDecisionCount"));
        Assert.Equal(expectedIds, wideApprovedIds);
    }

    [Fact]
    public async Task Live_qwen3_reviews_visible_navigation_anchors_before_pivoting_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_NAVIGATION_TRANSITION_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_NAVIGATION_TRANSITION_PROBE=1 "
                + "to run the navigation-transition microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl) || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The navigation-transition microprobe requires the local LLM URL and model.");
        }

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var executor = new NavigationTransitionProbeExecutor();
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            executor,
            new SourceBackedAgentV2Options(
                MaximumTurns: 2,
                MaximumToolCalls: 2,
                MaximumObservationItems: 20,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900,
                MaximumWorkingEvidenceItems: 40,
                SeparateActionAndWriter: false,
                SemanticCandidateAuditEnabled: true,
                MaximumContextTokens: 8192,
                SemanticColumnRoleReviewEnabled: false,
                StructuredSemanticPlanningEnabled: true,
                RequireEvidenceSelectionBeforeWriter: false));
        var intake = BuildWideMealPlanIntake() with
        {
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "structured_layout",
                    deliverable = "planning de repas du lundi au vendredi",
                    structuredLayout = true,
                    rowCount = 5,
                    columnCount = 4,
                    atomicEvidenceCount = 20,
                    atomicEvidenceType = "repas",
                    initialCapability = "",
                    rowHeader = "Jour",
                    rowLabels = new[]
                    {
                        "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"
                    },
                    columns = new[]
                    {
                        "Petit-déjeuner", "Déjeuner", "Collation", "Souper"
                    }
                }),
                "llm_router")
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var result = await runner.RunAsync(intake, cts.Token);

        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        var report = new StringBuilder()
            .AppendLine("LIVE NAVIGATION TRANSITION MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("EXECUTED_TOOLS: " + string.Join(" | ", executor.ToolNames))
            .AppendLine("SECOND_ARGUMENTS: " + (executor.Arguments.Count > 1
                ? executor.Arguments[1].GetRawText()
                : "(none)"))
            .AppendLine("EVIDENCE_ITEMS: " + result.EvidenceBundle.Items.Count);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine("Artifact: " + artifact);

        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Equal("documents.navigation", executor.ToolNames[0]);
        Assert.Equal("documents.context_batch", executor.ToolNames[1]);
        Assert.Equal(
            20,
            executor.Arguments[1].GetProperty("targets").GetArrayLength());
    }

    [Fact]
    public async Task Live_qwen3_defines_the_atomic_candidate_without_choosing_retrieval_tools()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_CANDIDATE_DEFINITION_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_CANDIDATE_DEFINITION_PROBE=1 "
                + "to run the candidate-definition microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl) || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The candidate-definition microprobe requires the local LLM URL and model.");
        }

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 20,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900));
        var intake = BuildWideMealPlanIntake();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var outcome = await InvokePrivateAsync(
            runner,
            "ReviewSemanticCandidateDefinitionAsync",
            intake,
            BuildAbstractMealSlotSemanticPlan(),
            "Jour",
            new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Petit-dejeuner"] = "Valeur adaptee au premier repas de la journee.",
                ["Dejeuner"] = "Valeur adaptee au repas du milieu de journee.",
                ["Collation"] = "Valeur adaptee a une prise alimentaire legere.",
                ["Souper"] = "Valeur adaptee au repas du soir."
            },
            cts.Token);

        var candidateObjectType = ReadProperty<string>(
            outcome,
            "CandidateObjectType");
        var hypotheticalSinglePositionValue = ReadProperty<string>(
            outcome,
            "HypotheticalSinglePositionValue");
        var candidateEligibilityRule = ReadProperty<string>(
            outcome,
            "CandidateEligibilityRule");
        var protocolValid = ReadProperty<bool>(outcome, "ProtocolValid");
        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        var report = new StringBuilder()
            .AppendLine("LIVE ATOMIC CANDIDATE DEFINITION MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("PROTOCOL_VALID: " + protocolValid)
            .AppendLine(
                "HYPOTHETICAL_SINGLE_POSITION_VALUE: "
                + hypotheticalSinglePositionValue)
            .AppendLine("CANDIDATE_OBJECT_TYPE: " + candidateObjectType)
            .AppendLine("CANDIDATE_ELIGIBILITY_RULE: " + candidateEligibilityRule)
            .AppendLine("LLM_CALLS: " + ReadProperty<int>(outcome, "LlmCallCount"))
            .AppendLine("FAILURE_REASON: " + ReadProperty<string>(outcome, "FailureReason"));
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine(report.ToString());
        output.WriteLine("Artifact: " + artifact);

        Assert.True(protocolValid);
        Assert.False(string.IsNullOrWhiteSpace(hypotheticalSinglePositionValue));
        foreach (var forbidden in new[]
                 {
                     "planning", "semaine", "lundi", "jour", "petit-dejeuner",
                     "dejeuner", "collation", "souper"
                 })
        {
            Assert.DoesNotContain(
                forbidden,
                candidateObjectType,
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(candidateEligibilityRule.Length >= 8);
    }

    [Fact]
    public async Task Live_qwen3_refines_the_concrete_source_type_and_chooses_the_first_observation_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_EVIDENCE_STRATEGY_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_EVIDENCE_STRATEGY_PROBE=1 "
                + "to run the evidence-strategy microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl) || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The evidence-strategy microprobe requires the local LLM URL and model.");
        }

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 20,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 1200));
        var intake = BuildWideMealPlanIntake();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var plan = BuildAbstractMealSlotSemanticPlan();
        var strategy = await InvokePrivateAsync(
            runner,
            "ReviewSemanticCandidateStrategyAsync",
            intake,
            plan,
            "Jour",
            new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" },
            new[] { "Petit-déjeuner", "Déjeuner", "Collation", "Souper" },
            new[] { "Cuisine", "Audit" },
            20,
            cts.Token);

        var initialAction = strategy.GetType()
            .GetProperty("InitialAction", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(strategy);
        var initialTool = initialAction is null
            ? string.Empty
            : ReadProperty<string>(initialAction, "ToolName");
        var initialArguments = initialAction is null
            ? default
            : ReadProperty<JsonElement>(initialAction, "Arguments");
        var report = new StringBuilder()
            .AppendLine("LIVE SOURCE OBJECT STRATEGY MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("PLAN: " + plan)
            .AppendLine(
                "ROUTER_CANDIDATE_OBJECT_TYPE: "
                + ReadProperty<string>(strategy, "RouterCandidateObjectType"))
            .AppendLine(
                "CANDIDATE_OBJECT_TYPE: "
                + ReadProperty<string>(strategy, "CandidateObjectType"))
            .AppendLine(
                "PROTOCOL_VALID: "
                + ReadProperty<bool>(strategy, "ProtocolValid"))
            .AppendLine(
                "FIRST_OBSERVATION_TOOL: " + initialTool)
            .AppendLine(
                "FIRST_OBSERVATION_ARGUMENTS: "
                + (initialArguments.ValueKind == JsonValueKind.Undefined
                    ? "(none)"
                    : initialArguments.GetRawText()))
            .AppendLine(
                "SOURCE_DISCOVERY_QUERY: "
                + ReadProperty<string>(strategy, "SourceDiscoveryQuery"))
            .AppendLine(
                "NAVIGATION_KIND: "
                + ReadProperty<string>(strategy, "NavigationKind"))
            .AppendLine(
                "CANDIDATE_POOL_RELATION: "
                + ReadProperty<string>(strategy, "CandidatePoolRelation"))
            .AppendLine("ATTEMPTS: " + ReadProperty<int>(strategy, "Attempts"))
            .AppendLine(
                "FAILURE_REASON: "
                + ReadProperty<string>(strategy, "FailureReason"))
            .AppendLine("SOURCE_OBJECT_DECISION: INDEPENDENT_LLM_STRATEGY_MICROCALL")
            .AppendLine("AXIS_PRECLASSIFICATION: REMOVED")
            .AppendLine("SUITABILITY_DECISION: DEFERRED_UNTIL_REAL_EVIDENCE_EXISTS")
            .AppendLine(
                "CANDIDATE_RAW: "
                + ReadProperty<string>(strategy, "LastToolArguments"));
        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine("Artifact: " + artifact);

        Assert.True(ReadProperty<bool>(strategy, "ProtocolValid"));
        Assert.True(ReadProperty<bool>(strategy, "Attempted"));
        Assert.Equal(1, ReadProperty<int>(strategy, "Attempts"));
        var candidateObjectType = ReadProperty<string>(
            strategy,
            "CandidateObjectType");
        Assert.DoesNotContain(
            "planning",
            candidateObjectType,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "jour",
            candidateObjectType,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "quotidien",
            candidateObjectType,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            candidateObjectType.Contains(
                "recette",
                StringComparison.OrdinalIgnoreCase)
            || candidateObjectType.Contains(
                "plat",
                StringComparison.OrdinalIgnoreCase),
            "Le type affine doit nommer l'objet documentaire concret, pas une case du planning: "
            + candidateObjectType);
        var sourceDiscoveryQuery = ReadProperty<string>(
            strategy,
            "SourceDiscoveryQuery");
        var candidatePoolRelation = ReadProperty<string>(
            strategy,
            "CandidatePoolRelation");
        Assert.DoesNotContain(
            "planning",
            sourceDiscoveryQuery,
            StringComparison.OrdinalIgnoreCase);
        if (string.Equals(
                candidatePoolRelation,
                "shared_pool",
                StringComparison.Ordinal))
        {
            Assert.Contains(
                initialTool,
                new[]
                {
                    "documents_content_cards",
                    "documents_navigation",
                    "rag_search"
                });
            Assert.Equal(
                "Cuisine",
                initialArguments.GetProperty("categoryPath").GetString());
            if (string.Equals(
                    initialTool,
                    "documents_navigation",
                    StringComparison.Ordinal))
            {
                Assert.True(
                    !initialArguments.TryGetProperty("kind", out var kind)
                    || kind.ValueKind == JsonValueKind.Null
                    || kind.GetString() is "navigation_entry" or "title_anchor");
            }
            Assert.InRange(
                initialArguments.GetProperty("limit").GetInt32(),
                20,
                40);
        }
        else
        {
            Assert.Equal("partitioned_pool", candidatePoolRelation);
            Assert.Null(initialAction);
        }
        var layoutCoordinates = new[]
        {
            "Jour", "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi",
            "Petit-déjeuner", "Déjeuner", "Collation", "Souper"
        };
        foreach (var coordinate in layoutCoordinates)
        {
            Assert.DoesNotContain(
                coordinate,
                candidateObjectType,
                StringComparison.OrdinalIgnoreCase);
        }
        foreach (var coordinate in new[]
                 {
                     "Jour", "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"
                 })
        {
            Assert.DoesNotContain(
                coordinate,
                sourceDiscoveryQuery,
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(
            new[] { "Cuisine" },
            ReadStringList(strategy, "CandidateScopePaths"));
    }

    [Fact]
    public async Task Live_qwen3_selects_atomic_objects_during_global_assignment_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_GLOBAL_ASSIGNMENT_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_GLOBAL_ASSIGNMENT_PROBE=1 "
                + "to run the mixed-candidate global-assignment probe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The global-assignment probe requires the local LLM URL and model.");
        }

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 24,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 1200,
                MaximumContextTokens: 4096));
        var rows = new[] { "Ligne 1", "Ligne 2" };
        var columns = new[] { "Choix A", "Choix B", "Choix C", "Choix D" };
        var roles = columns.ToDictionary(
            static column => column,
            static _ => "Une recette ou un plat nomme et autonome pouvant servir de "
                        + "valeur finale; exclut axe, horaire, rubrique, collection, "
                        + "instruction, fragment et metadonnee.",
            StringComparer.OrdinalIgnoreCase);
        var intake = new SourceBackedIntake(
            "Compose une grille de huit recettes distinctes et documentees a partir "
            + "des candidats. N'utilise ni rubrique, ni horaire, ni fragment.",
            "rag.structured_selection_probe",
            new[] { "2 lignes", "4 colonnes", "8 recettes distinctes" },
            Enumerable.Range(1, 8).Select(static index => "case:" + index).ToArray(),
            AllowsPartialAnswer: false,
            Language: "fr");
        var evidence = BuildTypedAuditEvidence();
        var bundle = new EvidenceBundle(
            "global-assignment-mixed-candidates",
            intake.UserQuestion,
            evidence,
            Array.Empty<SourceBackedTraceEvent>());
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var execution = await InvokePrivateAsync(
            runner,
            "CompleteStructuredCandidateWriterAsync",
            intake,
            bundle,
            evidence.Select(static item => item.EvidenceId).ToArray(),
            "Ligne",
            rows,
            columns,
            roles,
            null!,
            cts.Token);
        var protocolValid = ReadProperty<bool>(execution, "ProtocolValid");
        var failureReason = execution.GetType()
            .GetProperty("FailureReason")?
            .GetValue(execution) as string;
        var completion = ReadProperty<SourceBackedAgentCompletion>(
            execution,
            "Completion");
        var selectedIds = Regex.Matches(
                completion.Content ?? string.Empty,
                @"\[(E\d+)\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => match.Groups[1].Value.ToUpperInvariant())
            .ToArray();
        var expectedIds = Enumerable.Range(17, 8)
            .Select(static index => "E" + index)
            .ToArray();
        var report = new StringBuilder()
            .AppendLine("LIVE GLOBAL ASSIGNMENT MIXED-CANDIDATE MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("CANDIDATE_COUNT: " + evidence.Count)
            .AppendLine("EXPECTED_SELECTED_IDS: " + string.Join(",", expectedIds))
            .AppendLine("PROTOCOL_VALID: " + protocolValid)
            .AppendLine("FAILURE_REASON: " + failureReason)
            .AppendLine("ATTEMPTS: " + ReadProperty<int>(execution, "Attempts"))
            .AppendLine("LLM_CALLS: " + ReadProperty<int>(execution, "LlmCallCount"))
            .AppendLine("PROMPT_TOKENS: " + completion.PromptTokens)
            .AppendLine("COMPLETION_TOKENS: " + completion.CompletionTokens)
            .AppendLine("SELECTED_IDS: " + string.Join(",", selectedIds))
            .AppendLine("CONTENT:")
            .AppendLine(completion.Content)
            .AppendLine("RAW:")
            .AppendLine(ReadProperty<string>(execution, "RawOutput"));
        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine("Artifact: " + artifact);

        Assert.True(protocolValid, failureReason);
        Assert.Equal(expectedIds, selectedIds.OrderBy(static id => id).ToArray());
    }

    [Fact]
    public async Task Live_qwen3_writes_the_structured_plan_directly_from_candidates_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_STRUCTURED_WRITER_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_STRUCTURED_WRITER_PROBE=1 "
                + "to run the direct structured-writer microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl) || string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("The structured-writer probe requires the local LLM.");

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 40,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 1200,
                MaximumSemanticCandidateAuditConcurrency: 2));
        var rows = new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" };
        var columns = new[] { "Petit-dejeuner", "Dejeuner", "Collation", "Souper" };
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Petit-dejeuner"] =
                "Recette conventionnellement servie au petit-dejeuner dans le contexte "
                + "francophone; exclut les plats principaux de midi ou du soir.",
            ["Dejeuner"] =
                "Plat principal complet conventionnellement servi au milieu de journee; "
                + "exclut desserts, collations et recettes du matin.",
            ["Collation"] =
                "Encas leger conventionnellement pris entre les repas, sucre ou fruite; "
                + "exclut tout plat principal.",
            ["Souper"] =
                "Plat principal complet conventionnellement servi en fin de journee; "
                + "exclut desserts, collations et recettes du matin."
        };
        var intake = BuildWideMealPlanIntake() with
        {
            RequestedAxes = rows.Select(static row => "row:" + row)
                .Concat(columns.Select(static column => "column:" + column))
                .ToArray(),
            RowHeaderLabel = "Jour",
            StructuredCellValueMode = "canonical_content_card",
            CanonicalColumnSemanticRoles = roles
        };
        var evidence = BuildWideMealPlanEvidence();
        var bundle = new EvidenceBundle(
            "direct-structured-writer-meal-plan",
            intake.UserQuestion,
            evidence,
            Array.Empty<SourceBackedTraceEvent>());
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var completionObject = await InvokePrivateAsync(
            runner,
            "CompleteStructuredCandidateWriterAsync",
            intake,
            bundle,
            evidence.Select(static item => item.EvidenceId).ToArray(),
            "Jour",
            rows,
            columns,
            roles,
            null!,
            cts.Token);
        var protocolValid = ReadProperty<bool>(completionObject, "ProtocolValid");
        var protocolFailure = completionObject.GetType()
            .GetProperty("FailureReason")?
            .GetValue(completionObject) as string;
        var rawOutput = ReadProperty<string>(completionObject, "RawOutput");
        var attempts = ReadProperty<int>(completionObject, "Attempts");
        var completion = ReadProperty<SourceBackedAgentCompletion>(
            completionObject,
            "Completion");
        var finalCompletion = completion;
        var finalReviewDecision = "not_run";
        var reviewLog = new StringBuilder();
        var rejectedEvidenceIdsByCell =
            new Dictionary<int, HashSet<string>>();
        IReadOnlySet<int>? reviewOnlyCellIndexes = null;
        if (protocolValid)
        {
            for (var reviewRound = 0; reviewRound < 4; reviewRound++)
            {
                var currentIds = Regex.Matches(
                        finalCompletion.Content,
                        @"\[(E\d+)\]",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    .Select(static match => match.Groups[1].Value)
                    .ToArray();
                var currentDraft = new WriterDraft(
                    finalCompletion.Content,
                    currentIds);
                var review = await InvokePrivateAsync(
                    runner,
                    "ReviewStructuredAssignmentsAsync",
                    intake,
                    "Composer une grille professionnelle en respectant le role semantique de chaque colonne.",
                    currentDraft,
                    bundle,
                    evidence.Select(static item => item.EvidenceId).ToArray(),
                    reviewOnlyCellIndexes,
                    cts.Token);
                finalReviewDecision = ReadProperty<string>(review, "Decision");
                var reviewProtocolFailed =
                    ReadProperty<bool>(review, "TruncationRetryExhausted");
                var currentClaims = SourceContractVerifier
                    .ExtractRequestedStructuredCellClaims(
                        currentDraft.Answer,
                        intake)
                    .ToArray();
                foreach (Match rejectedMatch in Regex.Matches(
                             ReadProperty<string>(review, "RawOutput"),
                             @"(?m)^C(\d{2,})=REJECT:",
                             RegexOptions.IgnoreCase
                             | RegexOptions.CultureInvariant))
                {
                    var rejectedIndex =
                        int.Parse(rejectedMatch.Groups[1].Value) - 1;
                    if (rejectedIndex < 0 || rejectedIndex >= currentClaims.Length)
                        continue;
                    if (!rejectedEvidenceIdsByCell.TryGetValue(
                            rejectedIndex,
                            out var rejectedIds))
                    {
                        rejectedIds = new HashSet<string>(
                            StringComparer.OrdinalIgnoreCase);
                        rejectedEvidenceIdsByCell[rejectedIndex] = rejectedIds;
                    }
                    foreach (var evidenceId in currentClaims[rejectedIndex].EvidenceIds)
                        rejectedIds.Add(evidenceId);
                }
                reviewOnlyCellIndexes = Regex.Matches(
                        ReadProperty<string>(review, "RawOutput"),
                        @"(?m)^C(\d{2,})=REJECT:",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    .Select(static match => int.Parse(match.Groups[1].Value) - 1)
                    .Where(static index => index >= 0)
                    .ToHashSet();
                reviewLog.AppendLine(
                        "REVIEW_ROUND_" + (reviewRound + 1) + ": "
                        + finalReviewDecision)
                    .AppendLine(ReadProperty<string>(review, "RawOutput"));
                if (reviewProtocolFailed)
                {
                    reviewLog.AppendLine(
                        "REVIEW_PROTOCOL_FAILED_" + (reviewRound + 1) + ": true");
                    break;
                }
                if (string.Equals(
                        finalReviewDecision,
                        "accept",
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                if (reviewRound == 3)
                    break;
                var revision = await InvokePrivateAsync(
                    runner,
                    "CompleteStructuredAssignmentRevisionAsync",
                    intake,
                    bundle,
                    review,
                    currentDraft,
                    evidence.Select(static item => item.EvidenceId).ToArray(),
                    rejectedEvidenceIdsByCell,
                    cts.Token);
                var revisionValid = ReadProperty<bool>(revision, "ProtocolValid");
                reviewLog.AppendLine(
                        "REVISION_PROTOCOL_VALID_" + (reviewRound + 1) + ": "
                        + revisionValid)
                    .AppendLine(
                        "REVISION_RAW_" + (reviewRound + 1) + ": "
                        + ReadProperty<string>(revision, "RawOutput"));
                if (!revisionValid)
                {
                    reviewLog.AppendLine(
                        "REVISION_FAILURE: "
                        + (revision.GetType().GetProperty("FailureReason")?
                            .GetValue(revision) as string));
                    break;
                }
                finalCompletion = ReadProperty<SourceBackedAgentCompletion>(
                    revision,
                    "Completion");
            }
        }
        var citedIds = Regex.Matches(
                finalCompletion.Content,
                @"\[(E\d+)\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => match.Groups[1].Value)
            .ToArray();
        var report = new StringBuilder()
            .AppendLine("LIVE DIRECT STRUCTURED WRITER MICROPROBE")
            .AppendLine("MODEL: " + model)
            .AppendLine("PROTOCOL_VALID: " + protocolValid)
            .AppendLine("PROTOCOL_FAILURE: " + protocolFailure)
            .AppendLine("ATTEMPTS: " + attempts)
            .AppendLine("PROMPT_TOKENS: " + completion.PromptTokens)
            .AppendLine("COMPLETION_TOKENS: " + completion.CompletionTokens)
            .AppendLine("FINAL_REVIEW_DECISION: " + finalReviewDecision)
            .AppendLine("CITED_COUNT: " + citedIds.Length)
            .AppendLine("DISTINCT_CITED_COUNT: " + citedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .AppendLine("INITIAL_ANSWER:")
            .AppendLine(completion.Content)
            .AppendLine("FINAL_ANSWER:")
            .AppendLine(finalCompletion.Content)
            .AppendLine("RAW_OUTPUT:")
            .AppendLine(rawOutput)
            .AppendLine("REVIEW_LOG:")
            .AppendLine(reviewLog.ToString());
        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(artifact, report.ToString(), cts.Token);
        output.WriteLine("Artifact: " + artifact);

        Assert.True(protocolValid, protocolFailure);
        Assert.Equal("accept", finalReviewDecision);
        Assert.Empty(finalCompletion.ToolCalls);
        Assert.Equal(20, citedIds.Length);
        Assert.Equal(20, citedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(citedIds, id => Assert.Contains(
            evidence,
            item => string.Equals(item.EvidenceId, id, StringComparison.OrdinalIgnoreCase)));
        var breakfastIds = new HashSet<string>(
            new[] { "E2", "E3", "E4", "E10", "E12", "E13", "E14", "E15", "E20", "E23", "E24", "E27" },
            StringComparer.OrdinalIgnoreCase);
        var snackIds = new HashSet<string>(
            new[] { "E3", "E4", "E5", "E10", "E12", "E13", "E20", "E23", "E27" },
            StringComparer.OrdinalIgnoreCase);
        var mainMealIds = new HashSet<string>(
            new[] { "E1", "E6", "E7", "E8", "E9", "E11", "E14", "E16", "E17", "E18", "E19", "E21", "E22", "E24", "E25", "E26", "E28" },
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(5, Regex.Matches(finalCompletion.Content, @"(?m)^\|\s*(Lundi|Mardi|Mercredi|Jeudi|Vendredi)\s*\|").Count);
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            Assert.Contains(citedIds[rowIndex * 4], breakfastIds);
            Assert.Contains(citedIds[(rowIndex * 4) + 1], mainMealIds);
            Assert.Contains(citedIds[(rowIndex * 4) + 2], snackIds);
            Assert.Contains(citedIds[(rowIndex * 4) + 3], mainMealIds);
        }
    }

    [Fact]
    public async Task Live_qwen3_reviews_structured_assignments_globally_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_STRUCTURED_ASSIGNMENT_REVIEW_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_STRUCTURED_ASSIGNMENT_REVIEW_PROBE=1 "
                + "to run the structured-assignment review microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl)
            || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The structured-assignment review microprobe requires the local LLM.");
        }

        var innerLlm = new OpenAiLlmClient();
        innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
        var runner = new SourceBackedAgentV2Runner(
            new NativeLlmAdapter(innerLlm),
            new UnusedToolExecutor(),
            new SourceBackedAgentV2Options(
                MaximumTurns: 1,
                MaximumToolCalls: 1,
                MaximumObservationItems: 12,
                MaximumObservationExcerptCharacters: 180,
                MaximumOutputTokens: 900,
                MaximumContextTokens: 8192,
                MaximumSemanticCandidateAuditConcurrency: 2));
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Petit-dejeuner"] =
                "Recette conventionnellement servie au petit-dejeuner; exclut les plats principaux.",
            ["Dejeuner"] =
                "Plat principal complet servi au milieu de journee; exclut desserts et recettes du matin.",
            ["Collation"] =
                "Encas leger entre les repas; exclut les plats principaux.",
            ["Souper"] =
                "Plat principal complet servi en fin de journee; exclut desserts et recettes du matin."
        };
        var intake = new SourceBackedIntake(
            "Propose une journee avec petit-dejeuner, dejeuner, collation et souper.",
            "rag.answer",
            Array.Empty<string>(),
            new[]
            {
                "row:Jour test A",
                "row:Jour test B",
                "column:Petit-dejeuner",
                "column:Dejeuner",
                "column:Collation",
                "column:Souper"
            },
            AllowsPartialAnswer: false,
            Language: "fr",
            RowHeaderLabel: "Jour",
            CanonicalColumnSemanticRoles: roles);
        var evidence = new[]
        {
            BuildEvidence(1, "Porridge aux flocons d'avoine", "Cuisine/test.pdf", 1,
                "Recette de porridge avec avoine, lait et cuisson."),
            BuildEvidence(2, "Paella mixte", "Cuisine/test.pdf", 2,
                "Plat principal de paella avec riz, legumes et cuisson."),
            BuildEvidence(3, "Compote de pommes", "Cuisine/test.pdf", 3,
                "Encas leger de compote de pommes."),
            BuildEvidence(4, "Curry de pois chiches", "Cuisine/test.pdf", 4,
                "Plat principal de curry avec pois chiches et sauce."),
            BuildEvidence(5, "Oeufs brouilles", "Cuisine/test.pdf", 5,
                "Recette d'oeufs brouilles servie au petit-dejeuner."),
            BuildEvidence(6, "Lasagnes aux legumes", "Cuisine/test.pdf", 6,
                "Plat principal de lasagnes aux legumes."),
            BuildEvidence(7, "Yaourt aux fruits", "Cuisine/test.pdf", 7,
                "Encas leger de yaourt aux fruits."),
            BuildEvidence(8, "Ragout de lentilles", "Cuisine/test.pdf", 8,
                "Plat principal de ragout de lentilles servi au souper.")
        };
        var bundle = new EvidenceBundle(
            "structured-assignment-review-probe",
            intake.UserQuestion,
            evidence,
            Array.Empty<SourceBackedTraceEvent>());
        WriterDraft Draft(IReadOnlyList<int> order)
        {
            var columns = roles.Keys.ToArray();
            var cells = order.Select((evidenceIndex, columnIndex) =>
                evidence[evidenceIndex].MatchedContentCards!.Value[0]
                    .GetProperty("title").GetString()
                + " ["
                + evidence[evidenceIndex].EvidenceId
                + "]").ToArray();
            var answer = "| Jour | " + string.Join(" | ", columns) + " |\n"
                         + "| --- | --- | --- | --- | --- |\n"
                         + "| Jour test A | " + string.Join(" | ", cells.Take(4)) + " |\n"
                         + "| Jour test B | " + string.Join(" | ", cells.Skip(4).Take(4)) + " |";
            return new WriterDraft(
                answer,
                order.Select(index => evidence[index].EvidenceId)
                    .ToArray());
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var validDraft = Draft(new[] { 0, 1, 2, 3, 4, 5, 6, 7 });
        var validReview = await InvokePrivateAsync(
            runner,
            "ReviewStructuredAssignmentsAsync",
            intake,
            "LIVRABLE: grille 2 x 4\nPREUVES_ATOMIQUES: 8 recettes distinctes",
            validDraft,
            bundle,
            evidence.Select(static item => item.EvidenceId).ToArray(),
            cts.Token);
        var invalidDraft = Draft(new[] { 1, 0, 2, 3, 4, 5, 6, 7 });
        var invalidReview = await InvokePrivateAsync(
            runner,
            "ReviewStructuredAssignmentsAsync",
            intake,
            "LIVRABLE: grille 2 x 4\nPREUVES_ATOMIQUES: 8 recettes distinctes",
            invalidDraft,
            bundle,
            evidence.Select(static item => item.EvidenceId).ToArray(),
            cts.Token);
        var categoryEvidence = BuildEvidence(
            9,
            "Desserts et collations",
            "Cuisine/test.pdf",
            9,
            "Rubrique qui regroupe plusieurs recettes et exemples distincts.");
        var categoryBundle = new EvidenceBundle(
            "structured-assignment-category-review-probe",
            intake.UserQuestion,
            evidence.Concat(new[] { categoryEvidence }).ToArray(),
            Array.Empty<SourceBackedTraceEvent>());
        var categoryAnswer = validDraft.Answer.Replace(
            "Compote de pommes [E3]",
            "Desserts et collations [E9]",
            StringComparison.Ordinal);
        var categoryDraft = new WriterDraft(
            categoryAnswer,
            SourceContractVerifier.ExtractEvidenceIds(categoryAnswer));
        var categoryReview = await InvokePrivateAsync(
            runner,
            "ReviewStructuredAssignmentsAsync",
            intake,
            "LIVRABLE: grille 2 x 4\nPREUVES_ATOMIQUES: 8 recettes distinctes",
            categoryDraft,
            categoryBundle,
            categoryBundle.Items.Select(static item => item.EvidenceId).ToArray(),
            cts.Token);

        output.WriteLine(
            "VALID REVIEW: decision={0}; finish={1}; raw={2}",
            ReadProperty<string>(validReview, "Decision"),
            ReadProperty<string>(validReview, "FinishReason"),
            ReadProperty<string>(validReview, "RawOutput"));
        output.WriteLine(
            "INVALID REVIEW: decision={0}; finish={1}; raw={2}",
            ReadProperty<string>(invalidReview, "Decision"),
            ReadProperty<string>(invalidReview, "FinishReason"),
            ReadProperty<string>(invalidReview, "RawOutput"));
        output.WriteLine(
            "CATEGORY REVIEW: decision={0}; finish={1}; raw={2}",
            ReadProperty<string>(categoryReview, "Decision"),
            ReadProperty<string>(categoryReview, "FinishReason"),
            ReadProperty<string>(categoryReview, "RawOutput"));
        Assert.Equal("accept", ReadProperty<string>(validReview, "Decision"));
        Assert.Equal("revise", ReadProperty<string>(invalidReview, "Decision"));
        Assert.Equal("revise", ReadProperty<string>(categoryReview, "Decision"));
        Assert.Contains(
            "C03=REJECT:",
            ReadProperty<string>(categoryReview, "RawOutput"),
            StringComparison.OrdinalIgnoreCase);
        var invalidRaw = ReadProperty<string>(invalidReview, "RawOutput");
        var acceptedCellIndexes = Regex.Matches(
                invalidRaw,
                @"(?m)^C(\d{2})=ACCEPT:",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => int.Parse(match.Groups[1].Value) - 1)
            .ToArray();
        Assert.NotEmpty(acceptedCellIndexes);
        Assert.Contains("REJECT", invalidRaw, StringComparison.OrdinalIgnoreCase);

        var feedbackMethod = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildStructuredAssignmentRevisionFeedback",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(feedbackMethod);
        var revisionFeedback = Assert.IsType<string>(feedbackMethod.Invoke(
            null,
            new object[]
            {
                intake,
                invalidReview,
                invalidDraft,
                evidence.Select(static item => item.EvidenceId).ToArray()
            }));
        Assert.Contains(invalidDraft.Answer, revisionFeedback, StringComparison.Ordinal);
        Assert.Contains(
            invalidRaw.Replace("\r\n", "\n", StringComparison.Ordinal),
            revisionFeedback,
            StringComparison.Ordinal);
        Assert.Contains(
            "conserve exactement la valeur et l'EvidenceId de chaque cellule ACCEPT",
            revisionFeedback,
            StringComparison.Ordinal);
        Assert.Contains(
            "EVIDENCEIDS DISPONIBLES POUR LES CELLULES REJECT: E1, E2",
            revisionFeedback,
            StringComparison.Ordinal);

        var revisionExecution = await InvokePrivateAsync(
            runner,
            "CompleteStructuredAssignmentRevisionAsync",
            intake,
            bundle,
            invalidReview,
            invalidDraft,
            evidence.Select(static item => item.EvidenceId).ToArray(),
            new Dictionary<int, HashSet<string>>(),
            cts.Token);
        output.WriteLine(
            "REVISION: valid={0}; attempts={1}; failure={2}; raw={3}",
            ReadProperty<bool>(revisionExecution, "ProtocolValid"),
            ReadProperty<int>(revisionExecution, "Attempts"),
            revisionExecution.GetType().GetProperty("FailureReason")?
                .GetValue(revisionExecution) as string ?? "(aucune)",
            ReadProperty<string>(revisionExecution, "RawOutput"));
        Assert.True(ReadProperty<bool>(revisionExecution, "ProtocolValid"));
        Assert.Equal(2, ReadProperty<int>(revisionExecution, "RevisedCellCount"));
        output.WriteLine(
            "REVISION PATCH: " + ReadProperty<string>(revisionExecution, "RawOutput"));
        var revisedCompletion = ReadProperty<SourceBackedAgentCompletion>(
            revisionExecution,
            "Completion");
        output.WriteLine("REVISED ANSWER: " + revisedCompletion.Content);
        var revisedCitedIds = Regex.Matches(
                revisedCompletion.Content,
                @"\[(E\d+)\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => match.Groups[1].Value)
            .ToArray();
        var revisedDraft = new WriterDraft(
            revisedCompletion.Content,
            revisedCitedIds);
        var revisedReview = await InvokePrivateAsync(
            runner,
            "ReviewStructuredAssignmentsAsync",
            intake,
            "LIVRABLE: grille 2 x 4\nPREUVES_ATOMIQUES: 8 recettes distinctes",
            revisedDraft,
            bundle,
            evidence.Select(static item => item.EvidenceId).ToArray(),
            cts.Token);

        Assert.Equal(8, revisedCitedIds.Length);
        Assert.Equal(8, revisedCitedIds.Distinct(
            StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("accept", ReadProperty<string>(revisedReview, "Decision"));
        var invalidClaims = SourceContractVerifier
            .ExtractRequestedStructuredCellClaims(invalidDraft.Answer, intake);
        var revisedClaims = SourceContractVerifier
            .ExtractRequestedStructuredCellClaims(revisedDraft.Answer, intake);
        Assert.Equal(8, invalidClaims.Count);
        Assert.Equal(8, revisedClaims.Count);
        foreach (var acceptedCellIndex in acceptedCellIndexes)
        {
            Assert.Equal(
                invalidClaims[acceptedCellIndex].ClaimText,
                revisedClaims[acceptedCellIndex].ClaimText);
            Assert.Equal(
                invalidClaims[acceptedCellIndex].EvidenceIds,
                revisedClaims[acceptedCellIndex].EvidenceIds);
        }
        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllTextAsync(
            artifact,
            "VALID_DECISION: " + ReadProperty<string>(validReview, "Decision")
            + Environment.NewLine
            + "INVALID_DECISION: " + ReadProperty<string>(invalidReview, "Decision")
            + Environment.NewLine
            + "INVALID_RAW: " + invalidRaw
            + Environment.NewLine
            + "CATEGORY_DECISION: " + ReadProperty<string>(categoryReview, "Decision")
            + Environment.NewLine
            + "CATEGORY_RAW: " + ReadProperty<string>(categoryReview, "RawOutput")
            + Environment.NewLine
            + "REVISION_FEEDBACK: " + revisionFeedback
            + Environment.NewLine
            + "REVISED_ANSWER: " + revisedCompletion.Content
            + Environment.NewLine
            + "REVISED_DECISION: " + ReadProperty<string>(revisedReview, "Decision"),
            cts.Token);
        output.WriteLine("Artifact: " + artifact);
    }

    [Fact]
    public async Task Live_qwen3_distinguishes_named_items_from_requested_details_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_LIVE_SOURCE_BACKED_SEMANTIC_GRANULARITY_PROBE"),
                "1",
                StringComparison.Ordinal))
        {
            output.WriteLine(
                "Skipped: set SAAIA_LIVE_SOURCE_BACKED_SEMANTIC_GRANULARITY_PROBE=1 "
                + "to run the semantic-granularity microprobe.");
            return;
        }

        var settings = AppSettings.Load();
        var liveSettings = settings.Clone();
        liveSettings.UseLocalLlm = true;
        liveSettings.LlmTemperature = 0.1;
        liveSettings.LlmMaxOutputTokens = 1200;
        if (string.Equals(
                Environment.GetEnvironmentVariable(
                    "SAAIA_VALIDATION_MANAGE_LOCAL_LLM_PROCESS"),
                "0",
                StringComparison.Ordinal))
        {
            liveSettings.ManageLocalLlmProcess = false;
        }

        var llmBaseUrl = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_BASE_URL"),
            settings.LlmBaseUrl);
        var model = FirstNonBlank(
            Environment.GetEnvironmentVariable("SAAIA_VALIDATION_LLM_MODEL"),
            settings.ModelId);
        if (string.IsNullOrWhiteSpace(llmBaseUrl) || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "The semantic-granularity microprobe requires the local LLM URL and model.");
        }

        var artifact = CreateArtifactPath();
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        var report = new StringBuilder();
        report.AppendLine("LIVE SOURCE-BACKED SEMANTIC GRANULARITY MICROPROBE");
        report.AppendLine("MODEL: " + model);
        report.AppendLine("LLM_URL: " + NormalizeLlmBaseUrl(llmBaseUrl));
        report.AppendLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        LlamaCppProcessManager? manager = null;
        try
        {
            if (liveSettings.ManageLocalLlmProcess)
            {
                manager = new LlamaCppProcessManager();
                manager.SetIdleStopSuppressionProvider(static () => true);
                var (ok, message) = await manager.EnsureRunningAsync(
                    liveSettings,
                    cts.Token);
                report.AppendLine("LLM_START: ok=" + ok + "; " + message);
                if (!ok)
                {
                    throw new InvalidOperationException(
                        "Managed LLM runtime did not start: " + message);
                }
            }

            var innerLlm = new OpenAiLlmClient();
            innerLlm.Configure(NormalizeLlmBaseUrl(llmBaseUrl), model);
            var runner = new SourceBackedAgentV2Runner(
                new NativeLlmAdapter(innerLlm),
                new UnusedToolExecutor(),
                new SourceBackedAgentV2Options(
                    MaximumTurns: 4,
                    MaximumToolCalls: 4,
                    MaximumObservationItems: 20,
                    MaximumObservationExcerptCharacters: 180,
                    MaximumOutputTokens: 1200));

            var titleIntake = BuildTitleOnlyIntake();
            var titlePlan = BuildTitleOnlyPlan();
            var validEvidence = BuildNamedEvidence();

            var titleBundle = new EvidenceBundle(
                "semantic-granularity-titles",
                titleIntake.UserQuestion,
                validEvidence,
                Array.Empty<SourceBackedTraceEvent>());
            var titleDraft = BuildTitleDraft(validEvidence);
            var titleReview = await InvokePrivateAsync(
                runner,
                "ReviewSemanticsAsync",
                titleIntake,
                titlePlan,
                titleDraft,
                titleBundle,
                validEvidence.Select(static item => item.EvidenceId).ToArray(),
                cts.Token);
            var titleDecision = ReadProperty<string>(titleReview, "Decision");
            report.AppendLine("TITLE_ONLY_DECISION: " + titleDecision);
            report.AppendLine(
                "TITLE_ONLY_REASONS: "
                + string.Join(" | ", ReadStringList(titleReview, "Reasons")));
            report.AppendLine(
                "TITLE_ONLY_RAW: " + ReadProperty<string>(titleReview, "RawOutput"));
            report.AppendLine();

            var detailedIntake = titleIntake with
            {
                UserQuestion =
                    "Donne la recette détaillée de la Paëlla mixte avec ingrédients, "
                    + "quantités et étapes de préparation."
            };
            const string detailedPlan = """
                                        LIVRABLE: fiche de recette detaillee
                                        DIMENSIONS: une recette
                                        PREUVES_ATOMIQUES: ingredients quantites et etapes de preparation
                                        INTENTIONS_RECHERCHE: Paella mixte ingredients quantites preparation
                                        APPROCHE_OUTILS: rag_search puis documents_context si necessaire
                                        PREMIERE_ACTION: rag_search {"query":"Paella mixte ingredients quantites preparation","categoryPath":"Cuisine","topK":8}
                                        ACCEPTER_SI: ingredients quantites et etapes sont directement prouves
                                        INSUFFISANT_SEULEMENT_SI: un detail explicitement demande reste introuvable
                                        """;
            var detailedBundle = titleBundle with
            {
                BundleId = "semantic-granularity-details",
                UserQuestion = detailedIntake.UserQuestion,
                Items = new[] { validEvidence[0] }
            };
            var detailedDraft = new WriterDraft(
                "Paëlla mixte [E1].",
                new[] { "E1" });
            var detailedReview = await InvokePrivateAsync(
                runner,
                "ReviewSemanticsAsync",
                detailedIntake,
                detailedPlan,
                detailedDraft,
                detailedBundle,
                new[] { "E1" },
                cts.Token);
            var detailedDecision = ReadProperty<string>(detailedReview, "Decision");
            report.AppendLine("DETAIL_REQUEST_DECISION: " + detailedDecision);
            report.AppendLine(
                "DETAIL_REQUEST_REASONS: "
                + string.Join(" | ", ReadStringList(detailedReview, "Reasons")));
            report.AppendLine(
                "DETAIL_REQUEST_RAW: "
                + ReadProperty<string>(detailedReview, "RawOutput"));
            report.AppendLine();

            var wideEvidence = BuildWideMealPlanEvidence();
            var wideIntake = BuildWideMealPlanIntake();
            var wideBundle = new EvidenceBundle(
                "semantic-granularity-wide-meal-plan",
                wideIntake.UserQuestion,
                wideEvidence,
                Array.Empty<SourceBackedTraceEvent>());
            var wideReview = await InvokePrivateAsync(
                runner,
                "ReviewSemanticsAsync",
                wideIntake,
                BuildWideMealPlanPlan(),
                BuildWideMealPlanDraft(wideEvidence.Take(20).ToArray()),
                wideBundle,
                wideEvidence.Select(static item => item.EvidenceId).ToArray(),
                cts.Token);
            var wideDecision = ReadProperty<string>(wideReview, "Decision");
            var widePromptTokens = ReadProperty<int?>(wideReview, "PromptTokens");
            report.AppendLine("WIDE_MEAL_PLAN_DECISION: " + wideDecision);
            report.AppendLine("WIDE_MEAL_PLAN_PROMPT_TOKENS: " + widePromptTokens);
            report.AppendLine(
                "WIDE_MEAL_PLAN_REASONS: "
                + string.Join(" | ", ReadStringList(wideReview, "Reasons")));
            report.AppendLine(
                "WIDE_MEAL_PLAN_RAW: "
                + ReadProperty<string>(wideReview, "RawOutput"));
            report.AppendLine();

            Assert.Equal("accept", titleDecision, ignoreCase: true);
            Assert.False(string.Equals(
                "accept",
                detailedDecision,
                StringComparison.OrdinalIgnoreCase));
            Assert.Equal("accept", wideDecision, ignoreCase: true);
            Assert.NotNull(widePromptTokens);
            Assert.InRange(widePromptTokens!.Value, 1, 4095);
            report.AppendLine(
                "RESULT: PASS - named source items are sufficient for a names-only "
                + "deliverable, while the same title-only proof is insufficient for "
                + "explicitly requested recipe details.");
        }
        finally
        {
            if (manager is not null)
            {
                manager.SetIdleStopSuppressionProvider(null);
                manager.Stop();
            }

            await File.WriteAllTextAsync(
                artifact,
                report.ToString(),
                CancellationToken.None);
            output.WriteLine("Artifact: " + artifact);
        }
    }

    private static SourceBackedIntake BuildTitleOnlyIntake()
        => new(
            "Donne huit noms de plats documentés. Je ne demande ni ingrédients, "
            + "ni quantités, ni préparation.",
            "source_backed_list",
            new[] { "Huit noms distincts", "Ne rien inventer" },
            new[] { "item:1", "item:2", "item:3", "item:4", "item:5", "item:6", "item:7", "item:8" },
            AllowsPartialAnswer: false,
            Language: "fr");

    private static string BuildTitleOnlyPlan()
        => """
           LIVRABLE: liste de huit noms de plats documentes
           DIMENSIONS: huit elements
           PREUVES_ATOMIQUES: huit noms de plats distincts
           INTENTIONS_RECHERCHE: plats recettes titres nommes
           APPROCHE_OUTILS: documents_content_cards
           PREMIERE_ACTION: documents_content_cards {"categoryPath":"Cuisine","limit":40,"offset":0}
           ACCEPTER_SI: huit titres de plats distincts sont prouves par fichier et page
           INSUFFISANT_SEULEMENT_SI: moins de huit noms de plats distincts sont prouves
           """;

    private static IReadOnlyList<EvidenceItem> BuildNamedEvidence()
    {
        var rows = new[]
        {
            ("Paëlla mixte", "Cuisine/30-recettes-preferees-des-francais.pdf", 13),
            ("Porridge aux flocons d’avoine", "Cuisine/chefbot_livre_de_recettes_fr.pdf", 124),
            ("Compote de pommes", "Cuisine/chefbot_livre_de_recettes_fr.pdf", 119),
            ("Mousse yaourt", "Cuisine/si-on-cuisinait.pdf", 74),
            ("Mousse au chocolat ultra rapide", "Cuisine/chefbot_livre_de_recettes_fr.pdf", 123),
            ("Burger végétarien", "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf", 83),
            ("Bœuf bourguignon", "Cuisine/30-recettes-preferees-des-francais.pdf", 5),
            ("Fisherman’s Pie", "Cuisine/nobilia-recettes-internationales-FR.pdf", 25)
        };
        return rows
            .Select(static (row, index) => BuildEvidence(
                index + 1,
                row.Item1,
                row.Item2,
                row.Item3,
                "La source nomme exactement le plat « " + row.Item1 + " »."))
            .ToArray();
    }

    private static SourceBackedIntake BuildWideMealPlanIntake()
        => new SourceBackedIntake(
            "J'ai besoin d'un planning de repas pour la semaine du lundi au vendredi "
            + "incluant petit-dejeuner, dejeuner, collation et souper. Fais un format "
            + "clair et professionnel, avec uniquement des sources utiles, non "
            + "dupliquees inutilement. N'invente rien.",
            "rag.plan_repas",
            new[] { "5 jours", "4 moments", "20 noms distincts" },
            Enumerable.Range(1, 20).Select(static index => "case:" + index).ToArray(),
            AllowsPartialAnswer: false,
            Language: "fr")
        {
            CatalogHints = new[]
            {
                new SourceBackedCatalogHint(
                    "Cuisine",
                    "Cuisine",
                    10,
                    Array.Empty<string>()),
                new SourceBackedCatalogHint(
                    "Audit",
                    "Audit",
                    10,
                    Array.Empty<string>())
            }
        };

    private static string BuildWideMealPlanPlan()
        => """
           LIVRABLE: planning de repas avec vingt noms documentes
           DIMENSIONS: cinq jours x quatre moments
           PREUVES_ATOMIQUES: vingt noms de plats distincts
           INTENTIONS_RECHERCHE: plats recettes titres nommes
           APPROCHE_OUTILS: documents_content_cards
           PREMIERE_ACTION: documents_content_cards {"categoryPath":"Cuisine","limit":40,"offset":0}
           ACCEPTER_SI: vingt titres distincts sont prouves par fichier et page
           INSUFFISANT_SEULEMENT_SI: moins de vingt noms distincts sont prouves
           """;

    private static string BuildAbstractMealSlotSemanticPlan()
        => """
           LIVRABLE: Planning de repas pour la semaine du lundi au vendredi
           DIMENSIONS: 5 jours x 4 moments de repas
           PREUVES_ATOMIQUES: 20 repas
           INTENTIONS_RECHERCHE: planning de repas semaine lundi vendredi
           APPROCHE_OUTILS: documents_content_cards puis documents_navigation si necessaire
           PREMIERE_ACTION: documents_content_cards {"categoryPath":"Cuisine","q":"planning de repas semaine lundi vendredi","limit":30,"offset":0}
           ACCEPTER_SI: les 20 positions visibles sont remplies par des instances distinctes directement prouvees
           INSUFFISANT_SEULEMENT_SI: moins de 20 instances distinctes adequates sont prouvees
           """;

    private static IReadOnlyList<EvidenceItem> BuildWideMealPlanEvidence()
    {
        var titles = new[]
        {
            "Paella mixte",
            "Porridge aux flocons d'avoine",
            "Compote de pommes",
            "Mousse au yaourt",
            "Mousse au chocolat",
            "Burger vegetarien",
            "Boeuf bourguignon",
            "Fisherman's Pie",
            "Saute vegetarien",
            "Kebab de fruits",
            "Ribollita",
            "Brioches fourrees",
            "Tarte de Linz",
            "Omelette aux herbes",
            "Pancakes a la banane",
            "Salade de lentilles",
            "Quiche aux legumes",
            "Gratin dauphinois",
            "Soupe de courge",
            "Tarte aux pommes",
            "Risotto aux champignons",
            "Curry de pois chiches",
            "Clafoutis aux cerises",
            "Galettes de courgettes",
            "Poulet basquaise",
            "Salade de quinoa",
            "Creme de marrons",
            "Rosti aux legumes"
        };
        return titles
            .Select(static (title, index) => BuildEvidence(
                index + 1,
                title,
                "Cuisine/recettes-" + ((index % 4) + 1) + ".pdf",
                index + 2,
                "La source nomme exactement le plat " + title + "."))
            .ToArray();
    }

    private static IReadOnlyList<EvidenceItem> BuildTypedAuditEvidence()
    {
        var candidates = new[]
        {
            (
                Title: "Petit-dejeuner",
                Kind: "section_title",
                HeadingPath: "Exemple de menu > Petit-dejeuner",
                Evidence: "La rubrique contient notamment un porridge et des tartines sourcees."),
            (
                Title: "Collation (9h-10h)",
                Kind: "schedule_label",
                HeadingPath: "Exemple de menu > Collation (9h-10h)",
                Evidence: "Le passage conseille un fruit, un yaourt et une barre de cereales."),
            (
                Title: "DEJEUNER",
                Kind: "section_title",
                HeadingPath: "Exemple de menu > DEJEUNER",
                Evidence: "Titre de rubrique suivi de plusieurs plats et recettes concretes."),
            (
                Title: "PETITS DEJEUNERS",
                Kind: "section_title",
                HeadingPath: "PETITS DEJEUNERS",
                Evidence: "Cette section contient la recette sourcee des pancakes a la banane."),
            (
                Title: "LEGUMES ET POELEES",
                Kind: "section_title",
                HeadingPath: "LEGUMES ET POELEES",
                Evidence: "Le passage decrit des brochettes de volaille et plusieurs accompagnements."),
            (
                Title: "LES RECETTES",
                Kind: "section_title",
                HeadingPath: "LES RECETTES",
                Evidence: "Sommaire qui annonce notamment une soupe de legumes et un roti source."),
            (
                Title: "Une collation!",
                Kind: "section_title",
                HeadingPath: "Conseils > Une collation!",
                Evidence: "Conseils suivis d'exemples concrets comme une compote et un laitage."),
            (
                Title: "Exemple de menu",
                Kind: "section_title",
                HeadingPath: "Exemple de menu",
                Evidence: "Le menu cite plusieurs plats concrets et des heures de service."),
            (
                Title: "DESSERTS ET PETITS DEJEUNERS",
                Kind: "section_title",
                HeadingPath: "DESSERTS ET PETITS DEJEUNERS",
                Evidence: "Rubrique qui regroupe des crepes, mousses et autres recettes."),
            (
                Title: "Dejeuner (vers 11h)",
                Kind: "schedule_label",
                HeadingPath: "Exemple de menu > Dejeuner (vers 11h)",
                Evidence: "Horaire suivi d'une proposition de repas complete."),
            (
                Title: "Dejeuner (au reveil)",
                Kind: "schedule_label",
                HeadingPath: "Exemple de menu > Dejeuner (au reveil)",
                Evidence: "Position temporelle contenant plusieurs aliments suggeres."),
            (
                Title: "Diner",
                Kind: "schedule_label",
                HeadingPath: "Exemple de menu > Diner",
                Evidence: "Champ d'un planning suivi d'un exemple de plat."),
            (
                Title: "Collaboration",
                Kind: "ocr_heading",
                HeadingPath: "Collaboration",
                Evidence: "Passage OCR voisin de titres culinaires, sans recette nommee."),
            (
                Title: "LA CONGELATION",
                Kind: "section_title",
                HeadingPath: "LA CONGELATION",
                Evidence: "Conseils de conservation qui mentionnent plusieurs aliments."),
            (
                Title: "Correction",
                Kind: "ocr_heading",
                HeadingPath: "Correction",
                Evidence: "Fragment OCR situe pres d'une liste de recettes."),
            (
                Title: "La conservation",
                Kind: "section_title",
                HeadingPath: "La conservation",
                Evidence: "Rubrique de conseils qui cite des soupes et des plats."),
            (
                Title: "Fondant au chocolat",
                Kind: "recipe",
                HeadingPath: "Desserts > Fondant au chocolat",
                Evidence: "Recette sourcee avec chocolat, oeufs, beurre et instructions."),
            (
                Title: "Crepes a la vanille",
                Kind: "recipe",
                HeadingPath: "Desserts > Crepes a la vanille",
                Evidence: "Recette sourcee avec farine, lait, oeufs et vanille."),
            (
                Title: "Mousse au chocolat express",
                Kind: "recipe",
                HeadingPath: "Desserts > Mousse au chocolat express",
                Evidence: "Recette sourcee avec chocolat et oeufs, suivie des etapes."),
            (
                Title: "Ile flottante",
                Kind: "recipe",
                HeadingPath: "Desserts > Ile flottante",
                Evidence: "Recette sourcee avec oeufs, lait, sucre et instructions."),
            (
                Title: "Tarte aux pommes",
                Kind: "recipe",
                HeadingPath: "Desserts > Tarte aux pommes",
                Evidence: "Recette sourcee avec pommes, pate et etapes de cuisson."),
            (
                Title: "Soupe russe",
                Kind: "recipe",
                HeadingPath: "Soupes > Soupe russe",
                Evidence: "Recette sourcee avec legumes, quantites et methode."),
            (
                Title: "Soupe aux choux",
                Kind: "recipe",
                HeadingPath: "Soupes > Soupe aux choux",
                Evidence: "Recette sourcee avec choux, ingredients et cuisson."),
            (
                Title: "Roti de palette a la soupe a l'oignon",
                Kind: "recipe",
                HeadingPath: "Plats > Roti de palette a la soupe a l'oignon",
                Evidence: "Recette sourcee avec roti, soupe a l'oignon et instructions.")
        };
        return candidates
            .Select(static (candidate, index) =>
            {
                var evidence = BuildEvidence(
                    index + 1,
                    candidate.Title,
                    "Cuisine/audit-types.pdf",
                    index + 1,
                    candidate.Evidence);
                return evidence with
                {
                    MatchedContentCards = JsonSerializer.SerializeToElement(
                        new[]
                        {
                            new
                            {
                                contentCardId = "typed-audit-" + (index + 1),
                                title = candidate.Title,
                                kind = candidate.Kind
                            }
                        }),
                    SelectionHints = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["kind"] = candidate.Kind,
                        ["headingPath"] = candidate.HeadingPath,
                        ["hasGroundedEvidence"] = "true"
                    }
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<EvidenceItem> BuildDifficultLabelAuditEvidence()
    {
        var candidates = new[]
        {
            (
                CurrentLabel: "Pour 4 personnes",
                HeadingPath: "Tartiflette > Pour 4 personnes",
                Kind: "recipe_detail",
                Evidence: "La page contient la recette Tartiflette et indique les quantites pour quatre personnes."),
            (
                CurrentLabel: "Preparation",
                HeadingPath: "Wok de nouilles aux crevettes > Preparation",
                Kind: "recipe_detail",
                Evidence: "Etapes de preparation du wok de nouilles aux crevettes."),
            (
                CurrentLabel: "INGREDIENTS",
                HeadingPath: "Saumon avec sauce yaourt-menthe > INGREDIENTS",
                Kind: "recipe_detail",
                Evidence: "Ingredients puis methode de la recette de saumon avec sauce yaourt-menthe."),
            (
                CurrentLabel: "3. Cuire le veloute",
                HeadingPath: "Veloute de courge > 3. Cuire le veloute",
                Kind: "recipe_step",
                Evidence: "Troisieme etape de la recette complete de veloute de courge."),
            (
                CurrentLabel: "JE CUISINE simplement",
                HeadingPath: "JE CUISINE simplement",
                Kind: "document_title",
                Evidence: "Couverture generale d'un livre contenant de nombreuses recettes."),
            (
                CurrentLabel: "MES RECETTES INTERNATIONALES",
                HeadingPath: "MES RECETTES INTERNATIONALES",
                Kind: "document_title",
                Evidence: "Titre d'une collection de recettes internationales."),
            (
                CurrentLabel: "Ail confit",
                HeadingPath: "Condiments > Ail confit",
                Kind: "recipe",
                Evidence: "Recette autonome d'ail confit avec ingredients et cuisson."),
            (
                CurrentLabel: "Boulettes de viande suedoises",
                HeadingPath: "Plats > Boulettes de viande suedoises",
                Kind: "recipe",
                Evidence: "Recette autonome de boulettes de viande suedoises avec sauce."),
            (
                CurrentLabel: "PETITS DEJEUNERS",
                HeadingPath: "PETITS DEJEUNERS",
                Kind: "section_title",
                Evidence: "Rubrique generale contenant plusieurs recettes du matin."),
        };
        return candidates
            .Select(static (candidate, index) =>
            {
                var evidence = BuildEvidence(
                    index + 1,
                    candidate.CurrentLabel,
                    "Cuisine/audit-libelles-difficiles.pdf",
                    index + 1,
                    candidate.Evidence);
                return evidence with
                {
                    MatchedContentCards = JsonSerializer.SerializeToElement(
                        new[]
                        {
                            new
                            {
                                contentCardId = "difficult-label-" + (index + 1),
                                title = candidate.CurrentLabel,
                                kind = candidate.Kind
                            }
                        }),
                    SelectionHints = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["kind"] = candidate.Kind,
                        ["headingPath"] = candidate.HeadingPath,
                        ["hasGroundedEvidence"] = "true"
                    }
                };
            })
            .ToArray();
    }

    private static WriterDraft BuildWideMealPlanDraft(
        IReadOnlyList<EvidenceItem> selectedEvidence)
    {
        var columns = new[]
        {
            "Petit-dejeuner",
            "Dejeuner",
            "Collation",
            "Souper"
        };
        var days = new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi" };
        var lines = new List<string>
        {
            "| Jour | " + string.Join(" | ", columns) + " |",
            "|---|---|---|---|---|"
        };
        for (var row = 0; row < days.Length; row++)
        {
            var cells = Enumerable.Range(0, columns.Length)
                .Select(column =>
                {
                    var item = selectedEvidence[(row * columns.Length) + column];
                    var title = item.MatchedContentCards!.Value[0]
                        .GetProperty("title")
                        .GetString();
                    return title + " [" + item.EvidenceId + "]";
                });
            lines.Add("| " + days[row] + " | " + string.Join(" | ", cells) + " |");
        }

        var answer = string.Join(Environment.NewLine, lines);
        return new WriterDraft(
            answer,
            selectedEvidence.Select(static item => item.EvidenceId).ToArray());
    }

    private static EvidenceItem BuildEvidence(
        int index,
        string title,
        string docPath,
        int page,
        string excerpt)
        => new(
            "E" + index,
            "canonical_content_card",
            "documents.content_cards",
            "inventaire de plats",
            "doc-" + index,
            Path.GetFileName(docPath),
            docPath,
            "source-hash-" + index,
            "revision-live-probe",
            page,
            page,
            "content-card:probe-" + index,
            excerpt,
            excerpt,
            1,
            index,
            "Cuisine",
            "fr",
            "fr",
            "native",
            JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    contentCardId = "probe-" + index,
                    title,
                    kind = "recipe_title"
                }
            }),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["kind"] = "recipe_title",
                ["hasGroundedEvidence"] = "true"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>(),
            new[] { "live-semantic-granularity-probe" });

    private static WriterDraft BuildTitleDraft(IReadOnlyList<EvidenceItem> evidence)
        => new(
            string.Join(
                Environment.NewLine,
                evidence.Select(static item =>
                    "- "
                    + item.MatchedContentCards!.Value[0].GetProperty("title").GetString()
                    + " ["
                    + item.EvidenceId
                    + "]")),
            evidence.Select(static item => item.EvidenceId).ToArray());

    private static async Task<object> InvokePrivateAsync(
        SourceBackedAgentV2Runner runner,
        string methodName,
        params object?[] arguments)
    {
        var method = typeof(SourceBackedAgentV2Runner)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(candidate => candidate.Name == methodName)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == arguments.Length
                       && parameters.Zip(arguments).All(static pair =>
                           pair.Second is null
                           || pair.First.ParameterType.IsInstanceOfType(pair.Second));
            });
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(runner, arguments));
        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        Assert.NotNull(result);
        return result!;
    }

    private static T ReadProperty<T>(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property!.GetValue(value));
    }

    private static IReadOnlyList<string> ReadStringList(
        object value,
        string propertyName)
        => ReadProperty<IReadOnlyList<string>>(value, propertyName);

    private static string NormalizeLlmBaseUrl(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        return normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + "/v1";
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string CreateArtifactPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, ".git")))
            current = current.Parent;
        if (current is null)
            throw new InvalidOperationException("Could not locate the repository root.");

        return Path.Combine(
            current.FullName,
            "artifacts",
            "live-source-backed-semantic-granularity-"
            + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            "report.txt");
    }

    private sealed class NativeLlmAdapter(OpenAiLlmClient inner)
        : ISourceBackedAgentLlmClient,
          ISourceBackedAgentStructuredLlmClient,
          ISourceBackedAgentInputTokenCounter
    {
        public Task<SourceBackedAgentCompletion> CompleteAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null,
            bool requireToolCall = false)
            => inner.ChatOnceNativeAsync(
                messages,
                tools,
                temperatureOverride ?? 0.1,
                Math.Clamp(maxTokens, 128, 1200),
                ct,
                requireToolCall);

        public async Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            LlmStructuredOutputContract contract,
            int maxTokens,
            CancellationToken ct,
            double? temperatureOverride = null)
        {
            var content = await inner.ChatOnceStructuredAsync(
                messages.Select(static message =>
                    (message.Role, message.Content ?? string.Empty)).ToArray(),
                temperatureOverride ?? 0,
                Math.Clamp(maxTokens, 128, 1200),
                contract,
                ct);
            return new SourceBackedAgentCompletion(
                content,
                Array.Empty<SourceBackedAgentToolCall>(),
                "stop");
        }

        public Task<int?> CountInputTokensAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            IReadOnlyList<SourceBackedAgentToolDefinition> tools,
            CancellationToken ct,
            bool requireToolCall = false)
            => inner.CountNativeInputTokensAsync(
                messages,
                tools,
                ct,
                requireToolCall);
    }

    private sealed class UnusedToolExecutor : ISourceBackedAgentToolExecutor
    {
        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "The semantic-granularity probe does not execute retrieval tools.");
    }

    private sealed class NavigationTransitionProbeExecutor
        : ISourceBackedAgentToolExecutor
    {
        private static readonly string[] Titles =
        {
            "Bœuf bourguignon", "Tartiflette", "Gratin dauphinois",
            "Osso buco", "Paëlla mixte", "Porridge aux flocons d’avoine",
            "Compote de pommes", "Mousse au yaourt", "Burger végétarien",
            "Fisherman’s Pie", "Ribollita", "Omelette aux herbes",
            "Pancakes à la banane", "Salade de lentilles", "Quiche aux légumes",
            "Soupe de courge", "Tarte aux pommes", "Risotto aux champignons",
            "Curry de pois chiches", "Poulet basquaise"
        };

        public List<string> ToolNames { get; } = new();
        public List<JsonElement> Arguments { get; } = new();

        public Task<ToolResults> ExecuteToolCallAsync(
            SourceBackedIntake intake,
            string toolName,
            JsonElement arguments,
            CancellationToken ct)
        {
            ToolNames.Add(toolName);
            Arguments.Add(arguments.Clone());
            if (string.Equals(
                    toolName,
                    "documents.navigation",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(BuildNavigationResults());
            }
            if (string.Equals(
                    toolName,
                    "documents.context_batch",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(BuildContextResults(arguments));
            }
            return Task.FromResult(new ToolResults());
        }

        private static ToolResults BuildNavigationResults()
        {
            var results = new ToolResults();
            results.Items.Add(new ToolResults.Item
            {
                ToolName = "documents.navigation",
                DurationMs = 5,
                Result = JsonSerializer.SerializeToElement(new
                {
                    navigationOnly = true,
                    total = Titles.Length,
                    limit = Titles.Length,
                    offset = 0,
                    items = Titles.Select((title, index) => new
                    {
                        label = title,
                        kind = "navigation_entry",
                        docId = "doc-recettes",
                        docName = "recettes.pdf",
                        docPath = "Cuisine/recettes.pdf",
                        categoryPath = "Cuisine",
                        revisionId = "revision-recettes",
                        targetChunkId = "recipe-" + (index + 1),
                        targetAnchorId = "anchor-" + (index + 1),
                        targetPageStart = index + 1,
                        targetPageEnd = index + 1
                    }).ToArray()
                })
            });
            return results;
        }

        private static ToolResults BuildContextResults(JsonElement arguments)
        {
            var results = new ToolResults();
            foreach (var target in arguments.GetProperty("targets").EnumerateArray())
            {
                var chunkId = target.GetProperty("chunkId").GetString()!;
                var page = target.GetProperty("pageStart").GetInt32();
                results.Items.Add(new ToolResults.Item
                {
                    ToolName = "documents.context",
                    DurationMs = 5,
                    Result = JsonSerializer.SerializeToElement(new
                    {
                        document = new
                        {
                            docId = "doc-recettes",
                            docName = "recettes.pdf",
                            docPath = "Cuisine/recettes.pdf"
                        },
                        items = new[]
                        {
                            new
                            {
                                docId = "doc-recettes",
                                docName = "recettes.pdf",
                                docPath = "Cuisine/recettes.pdf",
                                categoryPath = "Cuisine",
                                pageStart = page,
                                pageEnd = page,
                                chunkId,
                                text = "Recette documentée et directement citable."
                            }
                        }
                    })
                });
            }
            return results;
        }
    }
}
