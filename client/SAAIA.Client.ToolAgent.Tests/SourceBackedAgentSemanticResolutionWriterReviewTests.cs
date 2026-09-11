using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    [Fact]
    public void SemanticResolutionWriterReview_IsDisabledByDefault()
        => Assert.False(Options().SemanticResolutionWriterReviewEnabled);

    [Fact]
    public void SemanticResolutionWriterReview_RejectsConcurrentV0AndV2FlagsBeforeAnyCall()
    {
        var llm = new ScriptedAgentLlm();

        Assert.Throws<InvalidOperationException>(() => new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            V2Options() with { SemanticAnswerTransactionEnabled = true }));
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public void SemanticResolutionV6Contract_ContainsOnlyDecisionEvidenceLeadAndReason()
    {
        var contract = BuildResolutionContract("E1", "E2");

        Assert.Equal("source_backed_semantic_resolution_v6", contract.Name);
        Assert.Equal(
            new[]
            {
                "decision",
                "evidenceIds",
                "leadEvidenceIds",
                "reason",
            },
            contract.Schema.GetProperty("properties")
                .EnumerateObject()
                .Select(static property => property.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray());
        var serialized = contract.Schema.GetRawText();
        Assert.DoesNotContain("claims", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("presentation", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticResolutionV6Contract_RestrictsEvidenceIdsToPresentedPool()
    {
        var contract = BuildResolutionContract("E1", "E3");
        var properties = contract.Schema.GetProperty("properties");

        Assert.Equal(
            new[] { "E1", "E3" },
            properties.GetProperty("evidenceIds")
                .GetProperty("items")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task SemanticResolutionV6Parser_AnswerRequiresEvidence()
    {
        var result = await RunV2Async(ResolutionV6(
            "answer", Array.Empty<string>(), "Les faits sont présents."));

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.Fields.TryGetValue("protocol_error", out var error)
            && error.Contains("semantic_resolution", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SemanticResolutionV6Parser_NonAnswerForbidsEvidence()
    {
        var result = await RunV2Async(ResolutionV6(
            "research", new[] { "E1" }, "Le fait demandé manque."));

        Assert.False(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.Fields.TryGetValue("protocol_error", out var error)
            && error.Contains("semantic_resolution", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SemanticResolutionV6Parser_ContextRequiresExactlyOneAnchor()
    {
        var result = await RunV2Async(ResolutionV6(
            "context", Array.Empty<string>(), "Une ancre est requise."));

        Assert.False(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.Fields.TryGetValue("protocol_error", out var error)
            && error.Contains("resolution_decision_mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SemanticResolutionV6Parser_ContextRejectsMultipleAnchors()
    {
        var result = await RunV2Async(
            ResolutionV6(
                "context", new[] { "E1", "E2" },
                "Une seule annexe visible doit être ouverte."),
            evidence: TwoEvidenceResult());

        Assert.False(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.Fields.TryGetValue("protocol_error", out var error)
            && error.Contains("resolution_decision_mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SemanticResolutionV6Parser_RejectsEvidenceOutsidePresentedPool()
    {
        var result = await RunV2Async(ResolutionV6(
            "answer", new[] { "E9" }, "La preuve doit être visible."));

        Assert.False(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.Fields.TryGetValue("protocol_error", out var error)
            && error.Contains("evidence_id_invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SemanticResolutionV6Parser_RejectsDuplicateEvidenceIds()
    {
        var result = await RunV2Async(ResolutionV6(
            "answer", new[] { "E1", "E1" }, "Chaque preuve est unique."));

        Assert.False(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.Fields.TryGetValue("protocol_error", out var error)
            && error.Contains("evidence_id_invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SemanticResolutionPolicyPrompt_DefinesBoundedComparisonAndGlobalScope()
    {
        var prompt = await CaptureSemanticResolutionPolicyPromptAsync();

        Assert.Contains("PORTEE_COMPARATIVE:", prompt, StringComparison.Ordinal);
        Assert.Contains("PORTEE_GLOBALE:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticResolutionPolicyPrompt_DecouplesAnswerUnitFromSupportEvidenceCount()
    {
        var prompt = await CaptureSemanticResolutionPolicyPromptAsync();

        Assert.Contains("PREUVES_DE_SUPPORT:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticResolutionPolicyPrompt_PrioritizesContextAndBoundsReason()
    {
        var prompt = await CaptureSemanticResolutionPolicyPromptAsync();

        Assert.Contains("PRIORITE_CONTEXTUELLE:", prompt, StringComparison.Ordinal);
        Assert.Contains("RAISON_CONCISE:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_AnswerRunsResolutionWriterAndReviewer()
    {
        var (result, llm) = await RunV2WithLlmAsync(
            ResolutionV6Answer("E1"),
            Writer("La pompe exige un serrage en croix à 85 N.m", "E1"),
            SemanticReview("accept", "Le claim est directement soutenu."));

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        Assert.Equal(3, llm.Requests.Count);
        Assert.Equal(
            new[]
            {
                "source_backed_semantic_resolution_v6",
                "source_backed_flat_writer_v1"
            },
            llm.StructuredOutputContracts.Select(static contract => contract.Name).ToArray());
        var resolution = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_resolution.completed");
        Assert.Equal("requested_information_present", resolution.Fields["answer_adequacy"]);
        Assert.Equal("true", resolution.Fields["requested_deliverable_complete"]);
        Assert.Equal("false", resolution.Fields["missing_user_input_prevents_unique_result"]);
        Assert.Equal("NONE", resolution.Fields["visible_context_evidence_id"]);
        Assert.Equal("decision", resolution.Fields["derived_alias_source"]);
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_WriterReceivesJudgeAssessmentAndExactSelectionRoles()
    {
        const string assessment =
            "HANDOFF-CAUSAL-A528: Cygnus est le choix du juge après comparaison de Brio et Cygnus.";
        var (result, llm) = await RunV2WithLlmAsync(
            new[]
            {
                Completion(ResolutionV6("answer", new[] { "E2", "E3" }, assessment)),
                Writer(
                    "Brio recharge en 58 minutes et Cygnus en 36 minutes; Cygnus est le plus rapide.",
                    "E2",
                    "E3"),
                SemanticReview("accept", "La comparaison et le choix sont soutenus.")
            },
            ThreeSameWindowEvidenceSearchResult(),
            null,
            "Quel drone d'au moins 4 kg recharge le plus vite ?",
            "drone charge recharge");

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        Assert.Equal(3, llm.Requests.Count);
        var writerPrompt = string.Join(
            "\n",
            llm.Requests[1].Select(static message => message.Content));
        Assert.Contains("HANDOFF_SEMANTIQUE_DU_JUGE:", writerPrompt, StringComparison.Ordinal);
        Assert.Contains("DECISION: answer", writerPrompt, StringComparison.Ordinal);
        Assert.Contains(
            "EVIDENCEIDS_SELECTIONNES_PAR_LE_JUGE: E2,E3",
            writerPrompt,
            StringComparison.Ordinal);
        Assert.Contains("RAISON_DU_JUGE: " + assessment, writerPrompt, StringComparison.Ordinal);
        Assert.Contains(
            "FENETRE_CONTEXTUELLE_CITABLE:",
            writerPrompt,
            StringComparison.Ordinal);
        Assert.Contains("[E1]", writerPrompt, StringComparison.Ordinal);
        Assert.Contains("[E2]", writerPrompt, StringComparison.Ordinal);
        Assert.Contains("[E3]", writerPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_EmitsSemanticWriterHandoffTrace()
    {
        const string assessment =
            "HANDOFF-TRACE-A528: le juge transmet son focus sans décision métier du code.";
        var (result, _) = await RunV2WithLlmAsync(
            ResolutionV6("answer", new[] { "E1" }, assessment),
            Writer("La pompe exige un serrage en croix à 85 N.m", "E1"),
            SemanticReview("accept", "Le claim est directement soutenu."));

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        var handoff = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution.writer_handoff.completed");
        Assert.Equal("llm_evidence_judge", handoff.Fields["decision_source"]);
        Assert.Equal("answer", handoff.Fields["decision"]);
        Assert.Equal("E1", handoff.Fields["selected_evidence_ids"]);
        Assert.Equal("true", handoff.Fields["assessment_forwarded"]);
        Assert.Equal(assessment.Length.ToString(), handoff.Fields["assessment_characters"]);
        Assert.Equal("unchanged", handoff.Fields["source_window_expansion_policy"]);
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_NonAnswerNeverEmitsWriterHandoff()
    {
        var (result, llm) = await RunV2WithLlmAsync(
            Completion(ResolutionV6(
                "research",
                Array.Empty<string>(),
                "La preuve documentaire nécessaire manque encore.")),
            options: V2Options() with { MaximumToolCalls = 1 });

        Assert.False(result.IsSourceVerified);
        Assert.Single(llm.Requests);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution.writer_handoff.completed");
    }

    [Theory]
    [InlineData("research", "requested_information_missing", false, "NONE")]
    [InlineData("clarify", "user_clarification_required", true, "NONE")]
    [InlineData("context", "visible_context_required", false, "E1")]
    public async Task SemanticResolutionWriterReview_NonAnswerNeverRunsWriterOrReviewer(
        string decision,
        string adequacy,
        bool missingUserInput,
        string visibleContext)
    {
        var evidenceIds = decision == "context"
            ? new[] { visibleContext }
            : Array.Empty<string>();
        var (result, llm) = await RunV2WithLlmAsync(
            new[]
            {
                Completion(ResolutionV6(
                    decision, evidenceIds,
                    decision == "clarify"
                        ? "Quelle option souhaitez-vous ?"
                        : "Le livrable ne peut pas encore être rédigé."))
            },
            null,
            V2Options() with { MaximumSemanticCorrectionTurns = 0 },
            null,
            null);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.Single(llm.Requests);
        Assert.Single(llm.StructuredOutputContracts);
        var resolution = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_resolution.completed");
        Assert.Equal(adequacy, resolution.Fields["answer_adequacy"]);
        Assert.Equal("false", resolution.Fields["requested_deliverable_complete"]);
        Assert.Equal(missingUserInput ? "true" : "false",
            resolution.Fields["missing_user_input_prevents_unique_result"]);
        Assert.Equal(visibleContext, resolution.Fields["visible_context_evidence_id"]);
        Assert.Equal("decision", resolution.Fields["derived_alias_source"]);
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_ResearchStopsBeforeImpossibleContinuationWhenToolBudgetIsExhausted()
    {
        var llm = new ScriptedAgentLlm(
            Completion(ResolutionV6(
                "research",
                Array.Empty<string>(),
                "Une recherche documentaire complémentaire est nécessaire.")),
            ResearchContinuation("research-after-resolution"));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            V2Options() with
            {
                MaximumToolCalls = 1,
                MaximumSemanticCorrectionTurns = 1
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle procédure documentée s'applique à cet équipement ?",
                "procédure équipement"),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.Single(llm.Requests);
        Assert.Single(executor.ToolNames);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.rejected"
            && trace.Fields.GetValueOrDefault("error")
                == "tool_call_budget_exhausted");
        var blocked = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution.continuation_blocked");
        Assert.Equal("tool_call_budget_exhausted", blocked.Fields["reason"]);
        Assert.Equal("research", blocked.Fields["next_capability"]);
        Assert.Equal("1", blocked.Fields["executed_tool_calls"]);
        Assert.Equal("1", blocked.Fields["maximum_tool_calls"]);
        Assert.Equal(
            "llm_evidence_judge+mechanical_budget_contract",
            blocked.Fields["decision_source"]);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName.StartsWith(
                "source_backed_agent_v2.semantic_review.",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_ContextStopsBeforeImpossibleContinuationWhenToolBudgetIsExhausted()
    {
        var llm = new ScriptedAgentLlm(Completion(ResolutionV6(
            "context",
            new[] { "E1" },
            "Le passage complet autour de l'ancre visible est nécessaire.")));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            V2Options() with
            {
                MaximumToolCalls = 1,
                MaximumSemanticCorrectionTurns = 1
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle condition complète encadre cette consigne ?",
                "condition complète consigne"),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.Single(llm.Requests);
        Assert.Single(executor.ToolNames);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.rejected"
            && trace.Fields.GetValueOrDefault("error")
                == "tool_call_budget_exhausted");
        var resolution = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_resolution.completed");
        Assert.Equal("E1", resolution.Fields["visible_context_evidence_id"]);
        var blocked = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution.continuation_blocked");
        Assert.Equal("tool_call_budget_exhausted", blocked.Fields["reason"]);
        Assert.Equal("documents_context", blocked.Fields["next_capability"]);
        Assert.Equal("1", blocked.Fields["executed_tool_calls"]);
        Assert.Equal("1", blocked.Fields["maximum_tool_calls"]);
        Assert.Equal(
            "llm_evidence_judge+mechanical_budget_contract",
            blocked.Fields["decision_source"]);
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_ResearchContinuationRemainsAvailableWhenToolBudgetRemains()
    {
        var llm = new ScriptedAgentLlm(
            Completion(ResolutionV6(
                "research",
                Array.Empty<string>(),
                "Une recherche documentaire complémentaire est nécessaire.")),
            ResearchContinuation("research-with-budget"));
        var executor = new ScriptedToolExecutor(SearchResult(), SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            V2Options() with
            {
                MaximumToolCalls = 2,
                MaximumSemanticCorrectionTurns = 1
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle procédure documentée s'applique à cet équipement ?",
                "procédure équipement"),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(new[] { "rag.search", "rag.search" }, executor.ToolNames);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution.continuation_blocked");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.rejected"
            && trace.Fields.GetValueOrDefault("error")
                == "tool_call_budget_exhausted");
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_WriterProtocolFailureBlocksPublication()
    {
        var (result, llm) = await RunV2WithLlmAsync(
            ResolutionV6Answer("E1"),
            Completion("not-writer-json"));

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.Equal(2, llm.Requests.Count);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed");
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_ReviewerAcceptPublishesVerifiedDraft()
    {
        var (result, _) = await RunV2WithLlmAsync(
            ResolutionV6Answer("E1"),
            Writer("La consigne impose 85 N.m", "E1"),
            SemanticReview("accept", "La formulation est impliquée par E1."));

        Assert.True(result.IsSourceVerified);
        Assert.Equal("La consigne impose 85 N.m [E1].", result.Answer);
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_ReviewerReviseRunsOneRepairAndRereview()
    {
        var (result, llm) = await RunV2WithLlmAsync(
            ResolutionV6Answer("E1"),
            Writer("Le manuel donne une information", "E1"),
            SemanticReview("revise", "Formule précisément la consigne visible."),
            Writer("La consigne impose un serrage en croix à 85 N.m", "E1"),
            SemanticReview("accept", "La révision est exactement soutenue."));

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        Assert.True(result.WasRepaired);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Equal("La consigne impose un serrage en croix à 85 N.m [E1].", result.Answer);
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_SecondReviseBlocksWithoutThirdDraft()
    {
        var (result, llm) = await RunV2WithLlmAsync(
            ResolutionV6Answer("E1"),
            Writer("Information vague", "E1"),
            SemanticReview("revise", "Le premier claim extrapole."),
            Writer("Autre formulation vague", "E1"),
            SemanticReview("revise", "La seconde formulation reste non impliquée."));

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution_writer_review.repair_scheduled");
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_NeedMoreEvidenceBlocksPublication()
    {
        var (result, _) = await RunV2WithLlmAsync(
            ResolutionV6Answer("E1"),
            Writer("L'autonomie mondiale est illimitée", "E1"),
            SemanticReview("need_more_evidence", "L'autonomie demandée n'est pas prouvée."));

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution_writer_review.publication_blocked");
    }

    [Fact]
    public async Task SemanticResolutionV6_AnchorOnlyAnswerReachesReviewerButCannotPublishWithoutAccept()
    {
        var (result, llm) = await RunV2WithLlmAsync(
            new[]
            {
                Completion(ResolutionV6Answer("E1")),
                Writer("La limite exacte vaut 12 millimètres", "E1"),
                SemanticReview(
                    "need_more_evidence",
                    "E1 localise une annexe mais ne contient aucune valeur numérique.")
            },
            TwoEvidenceResult(),
            null,
            "Quelle est la limite exacte en millimètres ?",
            "limite exacte");

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution_writer_review.publication_blocked");
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_ReviewerProtocolFailureBlocksPublication()
    {
        var (result, _) = await RunV2WithLlmAsync(
            ResolutionV6Answer("E1"),
            Writer("La consigne impose 85 N.m", "E1"),
            Completion("review-without-required-tool"));

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution_writer_review.publication_blocked");
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_UnsafeGlobalClaimCannotPublishWithoutAccept()
    {
        var (result, _) = await RunV2WithLlmAsync(
            new[]
            {
                Completion(ResolutionV6Answer("E1", "E2")),
                Writer("Oriole n'est pas certifiée dans le monde entier", "E1", "E2"),
                SemanticReview(
                    "need_more_evidence",
                    "Le catalogue régional ne soutient aucune conclusion mondiale.")
            },
            GlobalScopeEvidenceResult(),
            null,
            null,
            null);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.FinalDraft);
        Assert.DoesNotContain("monde entier", result.Answer ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_UsesOneCanonicalEvidenceBundleAcrossRoles()
    {
        var (result, llm) = await RunV2WithLlmAsync(
            new[]
            {
                Completion(ResolutionV6Answer("E3")),
                Writer("Cygnus recharge en 36 minutes", "E3"),
                SemanticReview("accept", "E3 soutient le modèle et la durée.")
            },
            ThreeSameWindowEvidenceSearchResult(),
            null,
            "Quel drone d'au moins 4 kg recharge le plus vite ?",
            "drone charge recharge");

        Assert.True(result.IsSourceVerified);
        Assert.Equal(3, llm.Requests.Count);
        Assert.All(llm.Requests, request => Assert.Contains(
            "E3",
            string.Join("\n", request.Select(static message => message.Content)),
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_EmitsPerRoleTokensLatencyAndBlockReason()
    {
        var (result, _) = await RunV2WithLlmAsync(
            ResolutionV6Answer("E1"),
            Writer("Claim non prouvé", "E1"),
            SemanticReview("need_more_evidence", "La preuve ne soutient pas le claim."));

        var resolution = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_resolution.completed");
        Assert.True(resolution.Fields.ContainsKey("prompt_tokens"));
        Assert.True(resolution.Fields.ContainsKey("completion_tokens"));
        Assert.True(resolution.Fields.ContainsKey("ms"));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.writer.completed");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_resolution_writer_review.publication_blocked"
            && trace.Fields.ContainsKey("reason"));
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_AnswerNeverSkipsIndependentReview()
    {
        var (result, _) = await RunV2WithLlmAsync(
            ResolutionV6Answer("E1"),
            Writer("La consigne impose 85 N.m", "E1"),
            SemanticReview("accept", "Le texte est soutenu."));

        Assert.True(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task SemanticResolutionWriterReview_FlagOffPreservesLegacyV0OneCall()
    {
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"NONE","presentation":"paragraphs","claims":[{"text":"La pompe exige un serrage en croix à 85 N.m","evidenceIds":["E1"]}],"reason":"La preuve répond exactement à la demande."}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            V2Options() with
            {
                SemanticResolutionWriterReviewEnabled = false,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake("Quelle consigne s'applique ?", "pompe consigne"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Single(llm.Requests);
        Assert.DoesNotContain(
            "HANDOFF_SEMANTIQUE_DU_JUGE:",
            string.Join("\n", llm.Requests[0].Select(static message => message.Content)),
            StringComparison.Ordinal);
        Assert.Equal(
            "source_backed_semantic_answer_transaction_v3",
            Assert.Single(llm.StructuredOutputContracts).Name);
    }

    private static SourceBackedAgentV2Options V2Options()
        => Options() with
        {
            MaximumTurns = 1,
            MaximumSemanticCorrectionTurns = 1,
            SeparateActionAndWriter = true,
            RequireEvidenceSelectionBeforeWriter = true,
            StructuredFlatWriterEnabled = true,
            SemanticAnswerTransactionEnabled = false,
            SemanticResolutionWriterReviewEnabled = true
        };

    private static LlmStructuredOutputContract BuildResolutionContract(
        params string[] allowedEvidenceIds)
    {
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "BuildSemanticResolutionContract",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<LlmStructuredOutputContract>(method!.Invoke(
            null,
            new object?[] { allowedEvidenceIds }));
    }

    private static async Task<string> CaptureSemanticResolutionPolicyPromptAsync()
    {
        var (_, llm) = await RunV2WithLlmAsync(
            Completion(ResolutionV6(
                "research",
                Array.Empty<string>(),
                "La preuve documentaire requise manque du pool visible.")),
            options: V2Options() with { MaximumToolCalls = 1 });
        return string.Join(
            Environment.NewLine,
            Assert.Single(llm.Requests).Select(static message => message.Content));
    }

    private static string ResolutionV6Answer(params string[] evidenceIds)
        => ResolutionV6(
            "answer",
            evidenceIds,
            "La sélection contient les faits nécessaires au livrable.");

    private static string ResolutionV6(
        string decision,
        IReadOnlyList<string> evidenceIds,
        string reason)
    {
        var leadEvidenceIds = string.Equals(
                decision,
                "context",
                StringComparison.OrdinalIgnoreCase)
            ? evidenceIds
            : string.Equals(
                    decision,
                    "answer",
                    StringComparison.OrdinalIgnoreCase)
                ? evidenceIds.Take(1).ToArray()
                : Array.Empty<string>();
        return JsonSerializer.Serialize(new
        {
            decision,
            evidenceIds,
            leadEvidenceIds,
            reason
        });
    }

    private static string ResolutionAnswer(params string[] evidenceIds)
        => JsonSerializer.Serialize(new
        {
            decision = "answer",
            evidenceIds,
            reason = "La sélection contient les faits nécessaires au livrable."
        });

    private static SourceBackedAgentCompletion ResearchContinuation(string id)
        => Completion(Call(
            id,
            "submit_research_action",
            new
            {
                capability = "rag_search",
                query = "procédure équipement source officielle",
                scope = "",
                document = "",
                anchor = "",
                navigationKind = "",
                limit = 5,
                offset = 0
            }));

    private static SourceBackedAgentCompletion Writer(
        string text,
        params string[] evidenceIds)
        => Completion(JsonSerializer.Serialize(new
        {
            presentation = "paragraphs",
            claims = new[]
            {
                new { text, evidenceIds }
            }
        }));

    private static async Task<SourceBackedPipelineResult> RunV2Async(
        string resolution,
        ToolResults? evidence = null)
        => (await RunV2WithLlmAsync(
            Completion(resolution),
            evidence: evidence)).Result;

    private static async Task<(SourceBackedPipelineResult Result, ScriptedAgentLlm Llm)>
        RunV2WithLlmAsync(
            string resolution,
            params SourceBackedAgentCompletion[] following)
        => await RunV2WithLlmAsync(
            new[] { Completion(resolution) }.Concat(following).ToArray(),
            null,
            null,
            null,
            null);

    private static async Task<(SourceBackedPipelineResult Result, ScriptedAgentLlm Llm)>
        RunV2WithLlmAsync(
            SourceBackedAgentCompletion resolution,
            ToolResults? evidence = null,
            SourceBackedAgentV2Options? options = null,
            string? question = null,
            string? query = null)
        => await RunV2WithLlmAsync(
            new[] { resolution }, evidence, options, question, query);

    private static async Task<(SourceBackedPipelineResult Result, ScriptedAgentLlm Llm)>
        RunV2WithLlmAsync(
            SourceBackedAgentCompletion first,
            SourceBackedAgentCompletion second,
            params SourceBackedAgentCompletion[] following)
        => await RunV2WithLlmAsync(
            new[] { first, second }.Concat(following).ToArray(),
            null,
            null,
            null,
            null);

    private static async Task<(SourceBackedPipelineResult Result, ScriptedAgentLlm Llm)>
        RunV2WithLlmAsync(
            SourceBackedAgentCompletion first,
            SourceBackedAgentCompletion second,
            SourceBackedAgentCompletion third,
            ToolResults evidence)
        => await RunV2WithLlmAsync(
            new[] { first, second, third }, evidence, null, null, null);

    private static async Task<(SourceBackedPipelineResult Result, ScriptedAgentLlm Llm)>
        RunV2WithLlmAsync(
            IReadOnlyList<SourceBackedAgentCompletion> completions,
            ToolResults? evidence,
            SourceBackedAgentV2Options? options,
            string? question,
            string? query)
    {
        var llm = new ScriptedAgentLlm(completions.ToArray());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(evidence ?? SearchResult()),
            options ?? V2Options());
        var effectiveQuestion = question ?? "Quelle consigne documentée s'applique ?";
        var result = await runner.RunAsync(
            SemanticTransactionIntake(effectiveQuestion, query ?? "consigne documentée"),
            CancellationToken.None);
        return (result, llm);
    }

    private static ToolResults TwoEvidenceResult()
        => Results("rag.search", new
        {
            hits = new object[]
            {
                new
                {
                    docId = "manual-index",
                    docName = "Manual Index.pdf",
                    pageStart = 3,
                    pageEnd = 3,
                    chunkId = "index-h",
                    excerpt = "La valeur demandée figure dans l'annexe H, page 92.",
                    score = 0.96
                },
                new
                {
                    docId = "manual-index",
                    docName = "Manual Index.pdf",
                    pageStart = 4,
                    pageEnd = 4,
                    chunkId = "index-j",
                    excerpt = "Une autre limite figure dans l'annexe J.",
                    score = 0.82
                }
            }
        });

    private static ToolResults GlobalScopeEvidenceResult()
        => Results("rag.search", new
        {
            hits = new object[]
            {
                new
                {
                    docId = "north-catalog",
                    docName = "North Catalog.pdf",
                    pageStart = 8,
                    pageEnd = 8,
                    chunkId = "oriole-certification",
                    excerpt = "Oriole est certifiée S-9 dans le catalogue Nord.",
                    score = 0.97
                },
                new
                {
                    docId = "north-catalog",
                    docName = "North Catalog.pdf",
                    pageStart = 2,
                    pageEnd = 2,
                    chunkId = "catalog-scope",
                    excerpt = "Le catalogue couvre uniquement les fournisseurs de la région Nord.",
                    score = 0.95
                }
            }
        });
}
