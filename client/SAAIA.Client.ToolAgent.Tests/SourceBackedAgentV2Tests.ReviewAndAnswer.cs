using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Fact]
    public void TerminalAnswer_DoesNotExposeInternalProtocolNotes()
    {
        const string internalDecision =
            "Décision finale de l'orchestrateur LLM: submit_evidence_selection "
            + "evidenceIds: [\"9fb65de2-8e5f-df87-e960-bc54d5908308\"]";
        const string internalContract =
            "Le contrat mécanique exige 1 sources visibles distinctes, mais 0 seulement ont été observées.";
        var result = new SourceBackedPipelineResult(
            "terminal-public-contract",
            Intake("Retrouve le document demandé."),
            null,
            EvidenceBundle.Empty("Retrouve le document demandé."),
            new EvidenceJudgeDecision(
                "insufficient_evidence",
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<RetrievalRequest>(),
                new[] { internalDecision, internalContract }),
            null,
            null,
            null,
            false,
            Array.Empty<SourceBackedTraceEvent>());

        var answer = SourceBackedTerminalAnswer.Build(result);

        Assert.Contains("sources", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "submit_evidence_selection",
            answer,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "9fb65de2-8e5f-df87-e960-bc54d5908308",
            answer,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "contrat mécanique",
            answer,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EvidenceId", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(internalDecision, result.JudgeDecision.MissingEvidenceNotes[0]);
    }

    [Fact]
    public async Task SplitActionWriter_StopsAfterTwoIdenticalNoCitableDecisions()
    {
        const string question = "Retrouve le passage localise dans le manuel.";
        const string repeatedDecision =
            "submit_evidence_selection evidenceIds: [\"doc-hx42\"]";
        var scriptedCompletions = new List<SourceBackedAgentCompletion>
        {
            Completion(
                "LIVRABLE: une valeur sourcee\nDIMENSIONS: aucune matrice\n"
                + "PREUVES_ATOMIQUES: 1 valeur\n"
                + "MODE_PREUVES_ATOMIQUES: content_claim\n"
                + "POLITIQUE_SELECTION: single_item\n"
                + "INTENTIONS_RECHERCHE: couple serrage\n"
                + "APPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: une valeur directement soutenue\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune preuve exploitable"),
            Completion(Call(
                "navigation-only",
                "documents_navigation",
                new
                {
                    docPath = "Manuals/HydraulicPumpManual.pdf",
                    limit = 1,
                    offset = 0
                })),
            Completion(Call("zero-yield-1", "documents_content_cards", new
            {
                q = "target one",
                limit = 1,
                offset = 0
            })),
            Completion(Call("zero-yield-2", "documents_content_cards", new
            {
                q = "target two",
                limit = 1,
                offset = 0
            })),
            Completion(repeatedDecision),
            Completion(Call("zero-yield-3", "documents_content_cards", new
            {
                q = "target three",
                limit = 1,
                offset = 0
            })),
            Completion(repeatedDecision),
            Completion("Une troisième décision ne doit pas être consommée.")
        };
        scriptedCompletions.AddRange(Enumerable.Repeat(
            Completion(repeatedDecision),
            20));
        var llm = new ScriptedAgentLlm(scriptedCompletions.ToArray());
        var executor = new ScriptedToolExecutor(
            PageOnlyNavigationResult(),
            new ToolResults(),
            new ToolResults(),
            new ToolResults());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 10,
                MaximumToolCalls = 8,
                SeparateActionAndWriter = true,
                StructuredSemanticPlanningEnabled = true,
                SemanticCandidateDefinitionEnabled = false,
                SemanticCandidateStrategyEnabled = false,
                SemanticCandidateAuditEnabled = false
            });

        var result = await runner.RunAsync(
            Intake(question),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(7, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.action.no_citable_evidence_repeat.stopped"
            && trace.Fields["identical_decisions"] == "2");
    }

    [Fact]
    public async Task AdaptiveFastReview_NewWriterTextCannotInheritTheEvidenceSelectionApproval()
    {
        const string reviewReached = "Independent final-text review was reached.";
        var llm = new ContextOverflowOnceLlm(
            FastReview("writer", "E1", "E1"),
            Completion("La pression du module Atlas est de 999 unités fictives [E1]."),
            new InvalidOperationException(reviewReached));
        var runner = new SourceBackedAgentV2Runner(llm,
            new ScriptedToolExecutor(SearchResultAtPage(7,
                "La pression du module Atlas est de 73 unités fictives.")),
            Options() with { SeparateActionAndWriter = true, RequireEvidenceSelectionBeforeWriter = true });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            FastReviewIntake() with { UserQuestion = "Quelle pression le document indique-t-il pour Atlas ?" },
            CancellationToken.None));

        Assert.Equal(reviewReached, error.Message);
        Assert.Equal(3, llm.Requests.Count);
        var reviewContext = string.Join(Environment.NewLine, llm.Requests[2].Select(message => message.Content));
        Assert.Contains("999 unités fictives", reviewContext, StringComparison.Ordinal);
        Assert.Contains("73 unités fictives", reviewContext, StringComparison.Ordinal);
        Assert.Contains("BROUILLON A JUGER", reviewContext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdaptiveFastReview_HandsOneSufficientEvidenceDirectlyToWriter()
    {
        var intake = FastReviewIntake();
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            Completion("Je te propose le dessert au chocolat documente [E1]."),
            SemanticReview("accept", "E1 documente directement le dessert proposé."));
        var executor = new ScriptedToolExecutor(SearchResultAtPage(7,
            "Dessert au chocolat facile : une mousse au chocolat documentée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.search" }, executor.ToolNames);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Empty(llm.ToolSets[0]);
        Assert.Empty(llm.ToolSets[1]);
        Assert.Equal(2, llm.Requests[1].Count);
        Assert.Contains(
            "PREUVES EXACTEMENT AUTORISEES",
            RequestText(llm, 1));
        Assert.DoesNotContain(
            "ETAT DE TRAVAIL COMPACT",
            RequestText(llm, 1));
        Assert.Contains(
            "une phrase de 30 mots",
            RequestText(llm, 1));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["selected_evidence_ids"] == "E1"
            && trace.Fields["decision"] == "ready"
            && trace.Fields["anchor_verified"] == "true"
            && trace.Fields["anchor_excerpt"].Contains(
                "Dessert au chocolat facile",
                StringComparison.Ordinal));
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.writer.inline_from_evidence_review");
    }

    [Fact]
    public async Task AdaptiveFastReview_CanSelectAndDraftInlineBeforeIndependentReview()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Suivez la méthode documentée pour le couvercle."),
            SemanticReview(
                "accept",
                "La réponse couvre la demande et reste fidèle à la preuve citée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(
            "Suivez la méthode documentée pour le couvercle [E1].",
            result.Answer);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.writer.inline_from_evidence_review"
            && trace.Fields["shared_llm_call"] == "true"
            && trace.Fields["selected_evidence_ids"] == "E1");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_InlineAnswerRequiresIndependentSemanticReview()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Le manuel indique une information technique."),
            SemanticReview(
                "accept",
                "Le brouillon répond à la demande et reprend fidèlement la preuve citée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(
                4,
                "Le manuel exige de verrouiller le capot avant toute maintenance.")),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "Quelle information de sécurité ce manuel documente-t-il ?",
                QuestionFocus = "document_family_or_type"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_InlineRevisionCommitsAfterSourceVerificationWithoutSecondReview()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Manuel technique."),
            SemanticReview(
                "revise",
                "Le libellé est trop générique ; formule l'information de sécurité visible."),
            Completion(
                "Le manuel exige de verrouiller le capot avant toute maintenance [E1]."));
        var executor = new ScriptedToolExecutor(SearchResultAtPage(
            4,
            "Le manuel exige de verrouiller le capot avant toute maintenance."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "Quelle information de sécurité ce manuel documente-t-il ?",
                QuestionFocus = "document_family_or_type"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            "Le manuel exige de verrouiller le capot avant toute maintenance [E1].",
            result.Answer);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Single(executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.inline_answer_revision.returned_to_writer"
            && trace.Fields["selected_evidence"] == "E1");
        Assert.Equal(
            new[] { "revise" },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName
                        == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.review_directed_revision.committed_after_source_verification"
            && trace.Fields["selected_evidence"] == "E1");
    }

    [Fact]
    public async Task AdaptiveFastReview_InvalidReviewDirectedRevisionStillRequiresRepairAndReview()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Manuel technique."),
            SemanticReview(
                "revise",
                "Formule l'information de sécurité visible."),
            Completion(
                "Le manuel exige de verrouiller le capot avant toute maintenance."),
            Completion(
                "Le manuel exige de verrouiller le capot avant toute maintenance [E1]."),
            SemanticReview(
                "accept",
                "La réponse réparée est exacte et correctement citée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(
                4,
                "Le manuel exige de verrouiller le capot avant toute maintenance.")),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "Quelle information de sécurité ce manuel documente-t-il ?",
                QuestionFocus = "document_family_or_type"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(5, llm.Requests.Count);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.review_directed_revision.committed_after_source_verification");
        Assert.Equal(
            new[] { "revise", "accept" },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName
                        == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
    }

    [Fact]
    public async Task SemanticJudge_At4096ReceivesCompleteCitedEvidenceWhenMeasuredPromptFits()
    {
        const string criticalTail =
            "CRITICAL TAIL FACT: calibrated pressure equals 73 fictional units.";
        var longEvidence =
            "Technical section opening. "
            + new string('x', 260)
            + " "
            + criticalTail;
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "The calibrated pressure equals 73 fictional units."),
            SemanticReview(
                "accept",
                "E1 explicitly supports the calibrated pressure stated in the draft."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7, longEvidence)),
            Options() with
            {
                MaximumContextTokens = 4096,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "What calibrated pressure is explicitly documented?"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(
            criticalTail,
            RequestText(llm, 1),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticJudge_CompactsScaffoldingButPreservesCompleteCitedEvidence()
    {
        const string criticalTail =
            "CRITICAL TAIL FACT: calibrated pressure equals 73 fictional units.";
        var longEvidence =
            "Technical section opening. "
            + new string('x', 260)
            + " "
            + criticalTail;
        var llm = new TokenCountingScriptedAgentLlm(
            new[] { 3600, 2000 },
            FastReview(
                "answer",
                "E1",
                "E1",
                "The calibrated pressure equals 73 fictional units."),
            SemanticReview(
                "accept",
                "The compact request still contains sufficient cited evidence."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7, longEvidence)),
            Options() with
            {
                MaximumContextTokens = 4096,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "What calibrated pressure is explicitly documented?"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { 3600, 2000 }, llm.InputTokenCountsReturned);
        Assert.Equal(2, llm.CountedRequests.Count);
        Assert.Contains(
            criticalTail,
            string.Join(
                Environment.NewLine,
                llm.CountedRequests[0].Select(static message => message.Content)),
            StringComparison.Ordinal);
        Assert.Contains(
            criticalTail,
            string.Join(
                Environment.NewLine,
                llm.CountedRequests[1].Select(static message => message.Content)),
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["context_recovery_used"] == "true"
            && trace.Fields["evidence_context_mode"] == "measured_compact_fallback"
            && trace.Fields["exact_input_tokens"] == "2000");
    }

    [Fact]
    public async Task SemanticJudge_RefusesWhenCompleteEvidenceStillExceedsMeasuredContext()
    {
        var llm = new TokenCountingScriptedAgentLlm(
            new[] { 4300, 4200 },
            FastReview("answer", "E1", "E1", "The calibrated pressure equals 73 fictional units."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7,
                "Technical section. " + new string('x', 260) + " Calibrated pressure equals 73 fictional units.")),
            Options() with
            {
                MaximumContextTokens = 4096,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            FastReviewIntake() with { UserQuestion = "What calibrated pressure is explicitly documented?" },
            CancellationToken.None));

        Assert.Contains("outside the measured", error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { 4300, 4200 }, llm.InputTokenCountsReturned);
        Assert.Equal(1, llm.CompleteCallCount);
    }

    [Fact]
    public async Task SemanticJudge_HttpContextRecoveryPreservesCompleteCitedEvidence()
    {
        const string criticalTail = "CRITICAL TAIL FACT: calibrated pressure equals 73 fictional units.";
        var llm = new ContextOverflowOnceLlm(
            FastReview("answer", "E1", "E1", "The calibrated pressure equals 73 fictional units."),
            new HttpRequestException("request exceeds the available context size", null,
                System.Net.HttpStatusCode.BadRequest),
            SemanticReview("accept", "The full evidence supports the pressure."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7, "Technical section. " + new string('x', 260) + criticalTail)),
            Options() with
            {
                MaximumContextTokens = 4096,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with { UserQuestion = "What calibrated pressure is explicitly documented?" },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(criticalTail, string.Join(Environment.NewLine,
            llm.Requests[2].Select(static message => message.Content)), StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["evidence_context_mode"] == "http_context_emergency_fallback");
    }

    [Fact]
    public async Task AdaptiveFastReview_NamedItemWindowRevisionReturnsSameSelectionDirectlyOnce()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReview(
                "accept",
                "E1",
                "",
                ("E1", "match"),
                ("E3", "nonmatch")),
            Completion(
                "The selected manual documents the sibling safety detail [E2]."),
            SemanticReview(
                "revise",
                "Keep the same cited proof and state the safety detail more precisely."),
            Completion(
                "The selected manual requires locking the cover before service [E2]."),
            SemanticReview(
                "accept",
                "E2 directly supports the revised safety statement."));
        var executor = new ScriptedToolExecutor(
            FastReviewDuplicateSourceResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(6, llm.Requests.Count);
        Assert.Single(executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.flat_selection_revision.returned_to_writer"
            && trace.Fields["selected_evidence"] == "E1");
        Assert.Equal(
            new[] { "revise", "accept" },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName
                        == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
    }

    [Fact]
    public async Task AdaptiveFastReview_InlineRevisionUsesPreferredObservedAlternativeAndIsReviewedAgain()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Une option provisoire."),
            SemanticReview(
                "revise",
                "E1 est trop partiel ; E3 est l'alternative complete deja visible.",
                rejectedEvidenceIds: new[] { "E1" },
                preferredAlternativeEvidenceIds: new[] { "E3" }),
            Completion(
                "L'option complete est documentee avec ses elements utiles [E3]."),
            SemanticReview(
                "accept",
                "La revision utilise l'alternative choisie et repond a la demande."));
        var executor = new ScriptedToolExecutor(FastReviewSingleSourceAlternativesResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion = "Donne l'option complete deja documentee."
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            "L'option complete est documentee avec ses elements utiles [E3].",
            result.Answer);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Single(executor.ToolNames);
        Assert.Empty(llm.ToolSets[2]);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.inline_answer_alternative_revision.returned_to_writer"
            && trace.Fields["rejected_evidence"] == "E1"
            && trace.Fields["selected_evidence"] == "E3");
        Assert.Equal(
            new[] { "revise", "accept" },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName
                        == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_InlineAlternativeRevisionChainsDistinctPreferredEvidenceAndReviewsEachDraft()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Une option initiale."),
            SemanticReview(
                "revise",
                "E1 est trop partiel ; E3 est une alternative observee.",
                rejectedEvidenceIds: new[] { "E1" },
                preferredAlternativeEvidenceIds: new[] { "E3" }),
            Completion("La premiere revision documentee utilise l'alternative choisie [E3]."),
            SemanticReview(
                "revise",
                "E3 reste insuffisant ; E5 est une autre alternative observee.",
                rejectedEvidenceIds: new[] { "E3" },
                preferredAlternativeEvidenceIds: new[] { "E5" }),
            Completion("La revision finale documentee utilise l'alternative choisie [E5]."),
            SemanticReview(
                "accept",
                "La derniere revision repond a la demande avec la preuve choisie."));
        var executor = new ScriptedToolExecutor(
            FastReviewSingleSourceAlternativesResult(5));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                MaximumSemanticCorrectionTurns = 2
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion = "Donne l'option finale deja documentee."
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            "La revision finale documentee utilise l'alternative choisie [E5].",
            result.Answer);
        Assert.Equal(6, llm.Requests.Count);
        Assert.Single(executor.ToolNames);
        Assert.Empty(llm.ToolSets[2]);
        Assert.Empty(llm.ToolSets[4]);
        var transitions = result.TraceEvents
            .Where(static trace =>
                trace.EventName
                    == "source_backed_agent_v2.inline_answer_alternative_revision.returned_to_writer")
            .ToArray();
        Assert.Collection(
            transitions,
            first =>
            {
                Assert.Equal("E1", first.Fields["rejected_evidence"]);
                Assert.Equal("E3", first.Fields["selected_evidence"]);
                Assert.Equal("1", first.Fields["revision_attempt"]);
                Assert.Equal("2", first.Fields["maximum_revision_attempts"]);
            },
            second =>
            {
                Assert.Equal("E3", second.Fields["rejected_evidence"]);
                Assert.Equal("E5", second.Fields["selected_evidence"]);
                Assert.Equal("2", second.Fields["revision_attempt"]);
                Assert.Equal("2", second.Fields["maximum_revision_attempts"]);
            });
        Assert.Equal(
            new[] { "revise", "revise", "accept" },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName
                        == "source_backed_agent_v2.semantic_review.completed")
                .Select(static trace => trace.Fields["decision"])
                .ToArray());
        Assert.Equal(
            3,
            result.TraceEvents.Count(static trace =>
                trace.EventName == "source_backed_agent_v2.answer.verified"
                && trace.Fields["valid"] == "true"));
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_InlineAlternativeRevisionStopsAtConfiguredSemanticCorrectionBudget()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                "Une option initiale."),
            SemanticReview(
                "revise",
                "E1 est trop partiel ; E3 est une alternative observee.",
                rejectedEvidenceIds: new[] { "E1" },
                preferredAlternativeEvidenceIds: new[] { "E3" }),
            Completion("La revision documentee utilise l'alternative choisie [E3]."),
            SemanticReview(
                "revise",
                "E3 reste insuffisant ; E5 est une autre alternative observee.",
                rejectedEvidenceIds: new[] { "E3" },
                preferredAlternativeEvidenceIds: new[] { "E5" }));
        var executor = new ScriptedToolExecutor(
            FastReviewSingleSourceAlternativesResult(5));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                MaximumTurns = 1,
                MaximumSemanticCorrectionTurns = 1
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion = "Donne l'option finale deja documentee."
            },
            CancellationToken.None);

        Assert.Equal(4, llm.Requests.Count);
        Assert.Single(executor.ToolNames);
        var transition = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.inline_answer_alternative_revision.returned_to_writer");
        Assert.Equal("E3", transition.Fields["selected_evidence"]);
        Assert.Equal("1", transition.Fields["revision_attempt"]);
        Assert.Equal("1", transition.Fields["maximum_revision_attempts"]);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.inline_answer_alternative_revision.returned_to_writer"
            && trace.Fields["selected_evidence"] == "E5");
    }

    [Fact]
    public async Task AdaptiveFastReview_CanClarifyWhenSeveralObservedCandidatesFit()
    {
        const string question =
            "Souhaitez-vous la variante classique ou la variante au chocolat ?";
        var llm = new ScriptedAgentLlm(
            FastReview(
                "clarify",
                "NONE",
                "NONE",
                question));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(FastReviewDuplicateSourceResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion = "Donne-moi les variantes documentées."
            },
            CancellationToken.None);

        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Equal(question, clarification.Message);
        Assert.Equal("clarify", result.JudgeDecision.Decision);
        Assert.Equal(
            "clarification_requested",
            result.ConversationMemory!.Outcome);
        Assert.Single(llm.Requests);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.clarification.requested"
            && trace.Fields["decision_source"] == "llm_evidence_judge"
            && trace.Fields["observed_candidates"] == "2");
    }

    [Fact]
    public async Task AdaptiveFastReview_AnchorsInlineCitationToTheExplicitSourceWindowItem()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E2",
                "Ouvrez la section Typical Properties Powder."),
            SemanticReview(
                "accept",
                "Le passage choisi et son ancrage remappe repondent a la demande."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SamePageSourceWindowResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake() with
            {
                UserQuestion =
                    "Quel passage dois-je ouvrir pour verifier la reponse ?"
            },
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            "Ouvrez la section Typical Properties Powder [E2].",
            result.Answer);
        Assert.Equal(
            "properties-2",
            Assert.Single(result.CitedEvidence).ChunkId);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["selected_evidence_ids"] == "E2"
            && trace.Fields["anchor_verified"] == "true");
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_GivesMechanicalFailureToSelectedEvidenceWriter()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            Completion("Je te propose le dessert au chocolat documente."),
            Completion("Je te propose le dessert au chocolat documente [E1]."),
            SemanticReview("accept", "E1 documente le dessert proposé, désormais cité."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7,
                "Dessert au chocolat facile : mousse. Faites fondre le chocolat, "
                + "incorporez la crème puis laissez prendre deux heures au frais.")),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Contains(
            "CORRECTION MECANIQUE OBLIGATOIRE",
            RequestText(llm, 2));
        Assert.Contains("missing_citations", RequestText(llm, 2));
        Assert.Contains("[E#]", RequestText(llm, 2));
        Assert.Equal(
            new[] { false, true },
            result.TraceEvents
                .Where(static trace =>
                    trace.EventName == "source_backed_agent_v2.answer.verified")
                .Select(static trace => trace.Fields["valid"] == "true")
                .ToArray());
    }

    [Fact]
    public async Task AdaptiveFastReview_StopsMechanicalRepairWhenFailureSignatureDoesNotChange()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            Completion("Premiere formulation sans citation."),
            Completion("Deuxieme formulation toujours sans citation."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                MaximumTurns = 12,
                MaximumSemanticCorrectionTurns = 6
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.mechanical_repair.no_progress_stopped"
            && trace.Fields["failure_signature"].Contains(
                "missing_citations",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdaptiveFastReview_OffersOneCandidatePerVisibleSource()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E2"),
            SingleSelectionScopeReview(
                "accept",
                "E1",
                "",
                ("E1", "match"),
                ("E3", "nonmatch")),
            Completion("Je te propose les eclairs documentes [E2]."),
            SemanticReview("accept", "E2 appartient à la recette des éclairs sélectionnée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(FastReviewDuplicateSourceResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("[E1]", RequestText(llm, 0));
        Assert.DoesNotContain("CANDIDAT [E2]", RequestText(llm, 0));
        Assert.Contains("EXTRAIT 2 [E2]", RequestText(llm, 0));
        Assert.Contains(
            "Creme patissiere et poche a douille.",
            RequestText(llm, 0));
        Assert.Contains("[E3]", RequestText(llm, 0));
        Assert.Contains(
            "Creme patissiere et poche a douille.",
            RequestText(llm, 2));
        var normalizedWriterPrompt = RequestText(llm, 2)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "[E1]\nTITRE:",
            normalizedWriterPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "[E2]\nTITRE:",
            normalizedWriterPrompt,
            StringComparison.Ordinal);
        Assert.Equal("E2", Assert.Single(result.CitedEvidence).EvidenceId);
        Assert.Empty(llm.ToolSets[0]);
        Assert.Contains("action,", RequestText(llm, 0));
        Assert.Contains("evidenceId, anchorId, text", RequestText(llm, 0));
        Assert.Contains("180", RequestText(llm, 0));
        Assert.Equal(2, llm.StructuredOutputContracts.Count);
        var contract = llm.StructuredOutputContracts[0];
        Assert.Equal(
            "source_backed_fast_evidence_review_v1",
            contract.Name);
        Assert.Equal(
            new[] { "E1", "E3", "NONE" },
            contract.Schema
                .GetProperty("properties")
                .GetProperty("evidenceId")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        Assert.Equal(
            new[] { "E1", "E2", "E3", "NONE" },
            contract.Schema
                .GetProperty("properties")
                .GetProperty("anchorId")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
    }

    [Fact]
    public async Task AdaptiveFastReview_AllowsAnExactSiblingCitationAndReviewsItSemantically()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReview(
                "accept",
                "E1",
                "",
                ("E1", "match"),
                ("E3", "nonmatch")),
            Completion(
                "Les eclairs documentes utilisent une creme patissiere "
                + "et une poche a douille [E2]."),
            SemanticReview(
                "accept",
                "E2 soutient exactement le fait redige pour l'element choisi."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(FastReviewDuplicateSourceResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(
            result.IsSourceVerified,
            string.Join(", ", result.Verification?.Errors.Select(static error =>
                error.Code) ?? Array.Empty<string>()));
        Assert.Equal("E2", Assert.Single(result.CitedEvidence).EvidenceId);
        Assert.Equal("eclairs-2", result.CitedEvidence[0].ChunkId);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.answer.verified"
            && trace.Fields["writer_allowed_evidence_ids"] == "E1,E2"
            && trace.Fields["required_evidence_groups"] == "1"
            && trace.Fields["context_evidence_beyond_selection"] == "true");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
    }

    [Fact]
    public async Task AdaptiveFastReview_PresentsEmbeddedSourceWindowInDocumentOrder()
    {
        const string methodText =
            "METHOD. Prepare the pastry shells on a clean tray and let them "
            + "cool completely before handling the filling. Measure every "
            + "component in the documented order. Whisk the cream until it "
            + "holds soft peaks, keep the bowl cold, then transfer the cream "
            + "to the prepared piping bag. Fill the shells with chocolate "
            + "cream and keep their order on the tray.";
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            Completion("Prepare the documented chocolate eclairs [E1]."),
            SemanticReview("accept", "La fenêtre source documente les éclairs au chocolat proposés."));
        var sourceWindowResult = Results(
            "rag.search",
            new
            {
                hits = new[]
                {
                    new
                    {
                        docId = "doc-eclairs-window",
                        docName = "pastry-guide.pdf",
                        docPath = "Knowledge/pastry-guide.pdf",
                        pageStart = 34,
                        pageEnd = 34,
                        chunkId = "eclairs-method",
                        fullText = methodText,
                        score = 0.96,
                        retrievalQuery = "simple documented dessert",
                        sourceWindowAnchorChunkId = "eclairs-method",
                        sourceWindow = new[]
                        {
                            new
                            {
                                chunkId = "eclairs-title",
                                chunkIndex = 100,
                                pageStart = 34,
                                pageEnd = 34,
                                sectionTitle = "CHOCOLATE ECLAIRS",
                                headingPath =
                                    "PASTRY > CHOCOLATE ECLAIRS",
                                text =
                                    "CHOCOLATE ECLAIRS. A documented pastry "
                                    + "entry with a deliberately long neighboring "
                                    + "description used to validate compact source windows."
                            },
                            new
                            {
                                chunkId = "eclairs-components",
                                chunkIndex = 101,
                                pageStart = 34,
                                pageEnd = 34,
                                sectionTitle = string.Empty,
                                headingPath = string.Empty,
                                text = "COMPONENTS. Pastry shells and chocolate cream."
                            },
                            new
                            {
                                chunkId = "eclairs-method",
                                chunkIndex = 102,
                                pageStart = 34,
                                pageEnd = 34,
                                sectionTitle = string.Empty,
                                headingPath = string.Empty,
                                text = methodText
                            }
                        }
                    }
                }
            });
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(sourceWindowResult),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var judgePrompt = RequestText(llm, 0);
        Assert.True(
            judgePrompt.IndexOf(
                "CHOCOLATE ECLAIRS",
                StringComparison.Ordinal)
            < judgePrompt.IndexOf(
                "METHOD. Prepare the pastry shells",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            "COMPONENTS. Pastry shells",
            judgePrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Whisk the cream until it holds soft peaks",
            judgePrompt);
        Assert.Contains(
            "section=CHOCOLATE ECLAIRS",
            judgePrompt);
        Assert.Contains("EXTRAIT 2 [E1] (ancre)", judgePrompt);

        var writerPrompt = RequestText(llm, 1);
        Assert.DoesNotContain(
            "HANDOFF_SEMANTIQUE_DU_JUGE:",
            writerPrompt,
            StringComparison.Ordinal);
        Assert.True(
            writerPrompt.IndexOf(
                "CHOCOLATE ECLAIRS",
                StringComparison.Ordinal)
            < writerPrompt.IndexOf(
                "COMPONENTS. Pastry shells",
                StringComparison.Ordinal));
        var componentsIndex = writerPrompt.IndexOf(
            "COMPONENTS. Pastry shells",
            StringComparison.Ordinal);
        var methodIndex = writerPrompt.IndexOf(
            "METHOD. Prepare the pastry shells",
            StringComparison.Ordinal);
        Assert.True(
            componentsIndex < methodIndex,
            $"components={componentsIndex}; method={methodIndex}; prompt={writerPrompt}");
    }

    [Fact]
    public async Task AdaptiveFastReview_RechecksOnlyAfterTheEvidenceWindowChanges()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "research",
                "NONE",
                "NONE",
                "Aucune option simple dans ce lot."),
            Completion(Call(
                "second-search",
                "submit_research_action",
                new
                {
                    capability = "rag_search",
                    query = "option simple sans cuisson",
                    scope = "",
                    document = "document imagine.pdf",
                    anchor = "",
                    limit = 8,
                    offset = 0,
                    mode = "auto"
                })),
            FastReview(
                "writer",
                "E2",
                "E2"),
            Completion("Je propose l'option simple sans cuisson [E2]."),
            SemanticReview("accept", "E2 documente une option simple sans cuisson."));
        var executor = new ScriptedToolExecutor(
            SearchResultAtPage(
                1,
                "Option documentee avec plusieurs etapes complexes."),
            SearchResultAtPage(
                2,
                "Option simple et sans cuisson, directement utilisable."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Contains(
            "RECHERCHES_DEJA_EFFECTUEES",
            RequestText(llm, 1));
        Assert.Contains(
            "PREUVES_REFUSEES_PAR_LE_JUGE",
            RequestText(llm, 1));
        Assert.Contains(
            "MEILLEURE_PISTE_A_APPROFONDIR",
            RequestText(llm, 1));
        Assert.Contains(
            "anchor=option-1",
            RequestText(llm, 1));
        Assert.Equal(
            "submit_research_action",
            Assert.Single(llm.ToolSets[1]).Name);
        Assert.Equal(
            new[]
            {
                "capability",
                "query",
                "scope",
                "document",
                "anchor",
                "navigationKind",
                "limit",
                "offset"
            },
            Assert.Single(llm.ToolSets[1])
                .Parameters
                .GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        Assert.Equal(
            2,
            result.TraceEvents.Count(static trace =>
                trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.turn.completed"
            && trace.Fields.TryGetValue(
                "compact_follow_up",
                out var compactFollowUp)
            && compactFollowUp == "true");
        Assert.False(
            executor.Arguments[1].TryGetProperty(
                "docPath",
                out _));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.compact_follow_up.mechanical_adjustment"
            && trace.Fields["adjustment"].StartsWith(
                "ungrounded_document_reference_removed:",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdaptiveFastReview_BoundsTwoConsecutiveResearchZeroYieldVerdictsWithTerminalDecision()
    {
        const string insufficiencyReason =
            "Les deux lots observes ne contiennent pas l'option demandee.";
        var llm = new ScriptedAgentLlm(
            FastReview(
                "research",
                "NONE",
                "NONE",
                "Le premier lot ne contient pas l'option demandee."),
            Completion(Call(
                "second-search",
                "submit_research_action",
                new
                {
                    capability = "rag_search",
                    query = "option documentee demandee",
                    scope = "",
                    document = "",
                    anchor = "",
                    limit = 8,
                    offset = 0,
                    mode = "auto"
                })),
            FastReview(
                "research",
                "NONE",
                "NONE",
                "Le second lot ne contient toujours pas l'option demandee."),
            Completion(Call(
                "terminal-yield",
                "resolve_source_yield",
                new
                {
                    decision = "insufficiency",
                    message = insufficiencyReason
                })));
        var executor = new ScriptedToolExecutor(
            SearchResultAtPage(
                1,
                "Information documentaire sans rapport avec l'option demandee."),
            SearchResultAtPage(
                2,
                "Autre information documentaire sans rapport avec la demande."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal("insufficient_evidence", result.JudgeDecision.Decision);
        var userFacingReason = Assert.Single(
            result.JudgeDecision.MissingEvidenceNotes);
        Assert.DoesNotContain(insufficiencyReason, userFacingReason);
        Assert.Contains("2 actions documentaires", userFacingReason);
        Assert.Contains("aucune source citable non rejetée", userFacingReason);
        Assert.Equal(
            userFacingReason,
            Assert.Single(result.ConversationMemory!.SemanticReviewNotes));
        Assert.Equal(new[] { "rag.search", "rag.search" }, executor.ToolNames);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Equal(
            new[] { "resolve_source_yield" },
            llm.ToolSets[3].Select(static tool => tool.Name).ToArray());
        Assert.Equal(128, llm.MaxTokens[3]);
        Assert.Contains(
            "DECISION TERMINALE DE RENDEMENT",
            RequestText(llm, 3),
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.zero_yield.resolution_requested"
            && trace.Fields["consecutive_research_reviews"] == "2"
            && trace.Fields["executed_requests"] == "2"
            && trace.Fields["rejected_evidence"] == "2"
            && trace.Fields["decision_source"]
                == "llm_evidence_judge+mechanical_budget_contract");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_audit_zero_yield.terminal_decision_requested"
            && trace.Fields["continuations"] == "0"
            && trace.Fields["maximum_continuations"] == "0");
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName
                == "source_backed_agent_v2.source_insufficiency.declared"
            && trace.Fields["reason"] == insufficiencyReason);
    }

    [Fact]
    public async Task AdaptiveFastReview_TerminatesWhenUnauditedCandidatesHaveNoAuditPath()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "research",
                "NONE",
                "NONE",
                "Le premier lot ne contient pas la preuve demandee."),
            Completion(Call(
                "second-search",
                "submit_research_action",
                new
                {
                    capability = "rag_search",
                    query = "seconde recherche generique",
                    scope = "",
                    document = "",
                    anchor = "",
                    limit = 8,
                    offset = 0,
                    mode = "auto"
                })),
            FastReview(
                "research",
                "NONE",
                "NONE",
                "Le second lot ne contient toujours pas la preuve demandee."),
            Completion(Call(
                "terminal-yield",
                "resolve_source_yield",
                new
                {
                    decision = "insufficiency",
                    message = "Les lots observes restent insuffisants."
                })));
        var executor = new ScriptedToolExecutor(
            FastReviewWideBatchResult(1),
            FastReviewWideBatchResult(6));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal("insufficient_evidence", result.JudgeDecision.Decision);
        Assert.Equal(new[] { "rag.search", "rag.search" }, executor.ToolNames);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Equal(
            new[] { "resolve_source_yield" },
            llm.ToolSets[3].Select(static tool => tool.Name).ToArray());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_audit_zero_yield.terminal_decision_requested"
            && trace.Fields["candidate_audit_enabled"] == "false"
            && trace.Fields["pending_candidates"] == "4");
    }

    [Fact]
    public async Task AdaptiveFastReview_ExpandsExactLeadContextWithoutAnotherDecisionCall()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "context",
                "E1",
                "E1",
                "Les ingredients sont visibles mais la methode manque."),
            FastReview(
                "writer",
                "E2",
                "E2"),
            Completion(
                "Faites fondre le chocolat, incorporez la creme puis laissez prendre deux heures au frais [E2]."),
            SemanticReview("accept", "E2 fournit les opérations et les deux heures de prise reprises dans le texte."));
        var executor = new ScriptedToolExecutor(
            SearchResultAtPage(
                117,
                "Mousse facile au chocolat. Ingredients: chocolat et creme."),
            DocumentContextResultAtPage(
                117,
                "Mousse facile au chocolat. Ingredients: chocolat et creme. "
                + "Faites fondre le chocolat, incorporez la creme puis laissez "
                + "prendre deux heures au frais."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.context" },
            executor.ToolNames);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Contains(
            "methode actionnable",
            RequestText(llm, 0));
        Assert.Contains(
            "Faites fondre le chocolat",
            RequestText(llm, 1));
        Assert.Equal(
            "Knowledge/Options.pdf",
            executor.Arguments[1].GetProperty("docRef").GetString());
        Assert.Equal(
            "option-117",
            executor.Arguments[1].GetProperty("chunkId").GetString());
        Assert.Equal(
            117,
            executor.Arguments[1].GetProperty("pageStart").GetInt32());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.next_action_scheduled"
            && trace.Fields["decision_source"] == "llm_evidence_judge"
            && trace.Fields["capability"] == "documents_context"
            && trace.Fields["lead_evidence_ids"] == "E1");
        Assert.DoesNotContain(llm.ToolSets, static tools =>
            tools.Any(tool => tool.Name == "submit_research_action"));
    }

    [Fact]
    public async Task AdaptiveFastReview_DoesNotWriteFromAnAnchorOutsideTheSelectedCandidate()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E999"),
            Completion(Call(
                "repair-action",
                "submit_research_action",
                new
                {
                    capability = "documents_context",
                    document = "Knowledge/Options.pdf",
                    anchor = "option-117",
                    pageStart = 117,
                    pageEnd = 117,
                    limit = 8,
                    offset = 0
                })),
            FastReview(
                "writer",
                "E2",
                "E2"),
            Completion(
                "Faites fondre le chocolat, incorporez la creme puis laissez prendre deux heures au frais [E2]."),
            SemanticReview("accept", "E2 fournit les opérations et les deux heures de prise reprises dans le texte."));
        var executor = new ScriptedToolExecutor(
            SearchResultAtPage(
                117,
                "Mousse facile au chocolat. Ingredients: chocolat et creme."),
            DocumentContextResultAtPage(
                117,
                "Mousse facile au chocolat. Ingredients: chocolat et creme. "
                + "Faites fondre le chocolat, incorporez la creme puis laissez "
                + "prendre deux heures au frais."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.context" },
            executor.ToolNames);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Contains(
            "identifiant d'extrait",
            RequestText(llm, 1));
        Assert.Equal(
            "submit_research_action",
            Assert.Single(llm.ToolSets[1]).Name);
        var reviews = result.TraceEvents
            .Where(static trace =>
                trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed")
            .ToArray();
        Assert.Equal(2, reviews.Length);
        Assert.Equal("false", reviews[0].Fields["protocol_valid"]);
        Assert.Equal("true", reviews[1].Fields["protocol_valid"]);
        Assert.Equal(
            "NONE",
            reviews[0].Fields["anchor_excerpt"]);
    }

    [Fact]
    public async Task AdaptiveFastReview_KeepsUnrejectedCandidatesVisibleAfterInvalidReview()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E999"),
            Completion(Call(
                "second-search",
                "submit_research_action",
                new
                {
                    capability = "rag_search",
                    query = "autre option documentee",
                    scope = "",
                    document = "",
                    anchor = "",
                    limit = 3,
                    offset = 0,
                    mode = "auto"
                })),
            FastReview(
                "writer",
                "E2",
                "E2"),
            SingleSelectionScopeReview(
                "accept",
                "E2",
                "",
                ("E1", "nonmatch"),
                ("E2", "match")),
            Completion("Je propose la seconde option documentee [E2]."),
            SemanticReview("accept", "La seconde option est documentée par E2."));
        var executor = new ScriptedToolExecutor(
            SearchResultAtPage(
                1,
                "Premiere option documentee encore non rejetee."),
            SearchResultAtPage(
                2,
                "Seconde option documentee nouvellement observee."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "rag.search" },
            executor.ToolNames);
        Assert.Contains("CANDIDAT [E2]", RequestText(llm, 2));
        Assert.Contains("CANDIDAT [E1]", RequestText(llm, 2));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["protocol_valid"] == "false"
            && trace.Fields["lead_evidence_ids"] == "E1");
    }

    [Fact]
    public async Task AdaptiveFastReview_RedirectsOversizedInlineAnswerToWriter()
    {
        var oversizedAnswer = string.Join(
            " ",
            Enumerable.Repeat(
                "La methode documentee reste ancree sur la preuve selectionnee.",
                8));
        Assert.True(oversizedAnswer.Length > 300);
        var llm = new ScriptedAgentLlm(
            FastReview(
                "answer",
                "E1",
                "E1",
                oversizedAnswer),
            Completion(
                "Suivez la methode documentee pour le dessert selectionne [E1]."),
            SemanticReview("accept", "E1 documente la méthode pour la mousse proposée."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultAtPage(7,
                "Dessert au chocolat facile : mousse. Faites fondre le chocolat, "
                + "incorporez la crème puis laissez prendre deux heures au frais.")),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(
            "PREUVES EXACTEMENT AUTORISEES",
            RequestText(llm, 1));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["selected_evidence_ids"] == "E1"
            && trace.Fields["answer_redirected_to_writer"] == "true");
    }

    [Fact]
    public async Task AdaptiveFastReview_PresentsAllCarriedCandidatesWithNewBatch()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E999"),
            Completion(Call(
                "second-search",
                "submit_research_action",
                new
                {
                    capability = "rag_search",
                    query = "second lot documente",
                    scope = "",
                    document = "",
                    anchor = "",
                    limit = 3,
                    offset = 0,
                    mode = "auto"
                })),
            FastReview(
                "clarify",
                "NONE",
                "NONE",
                "Quel candidat documente souhaitez-vous retenir ?"));
        var executor = new ScriptedToolExecutor(
            FastReviewDistinctBatchResult(1),
            FastReviewDistinctBatchResult(4));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            FastReviewIntake(),
            CancellationToken.None);

        Assert.NotNull(result.Clarification);
        var secondReviewPrompt = RequestText(llm, 2);
        foreach (var evidenceId in new[] { "E1", "E2", "E3", "E4", "E5", "E6" })
            Assert.Contains($"CANDIDAT [{evidenceId}]", secondReviewPrompt);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["presented_candidate_ids"]
                == "E1,E2,E3,E4,E5,E6");
    }

    [Fact]
    public async Task SplitActionWriter_BoundsSimpleDuplicateLoopWithLlmDecisionOnlyTurn()
    {
        var repeated = Call("search-1", "rag_search", new
        {
            query = "dessert au chocolat facile recette"
        });
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une proposition sourcee\nDIMENSIONS: aucune matrice\n"
                + "PREUVES_ATOMIQUES: 1 recette\nINTENTIONS_RECHERCHE: dessert chocolat\n"
                + "APPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: une recette directement soutenue\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune recette exploitable"),
            Completion(repeated),
            Completion(repeated with { Id = "search-2" }),
            Completion(repeated with { Id = "search-3" }),
            Completion("PRET_A_REDIGER: E1 soutient directement la proposition."),
            Completion("Je te propose le dessert au chocolat documente [E1]."),
            SemanticReview("accept", "E1 soutient directement la proposition."));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Je veux un dessert au chocolat facile, tu proposes quoi ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Single(executor.ToolNames);
        Assert.Empty(llm.ToolSets[4]);
        Assert.False(llm.RequireToolCalls[4]);
        Assert.Contains(
            "BUDGET MECANIQUE SANS PROGRES",
            RequestText(llm, 4),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.documentary_no_progress.decision_requested"
            && trace.Fields["consecutive_no_progress_turns"] == "2");
    }

    [Fact]
    public async Task SplitActionWriter_ReopensToolsWhenReviewNeedsMoreEvidenceAfterDuplicateDecision()
    {
        var repeated = Call("search-1", "rag_search", new
        {
            query = "dessert au chocolat facile recette"
        });
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une proposition sourcee\nDIMENSIONS: aucune matrice\n"
                + "PREUVES_ATOMIQUES: 1 recette\nINTENTIONS_RECHERCHE: dessert chocolat\n"
                + "APPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: une recette directement soutenue\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune recette exploitable"),
            Completion(repeated),
            Completion(repeated with { Id = "search-2" }),
            Completion(repeated with { Id = "search-3" }),
            Completion("PRET_A_REDIGER: E1 est le meilleur candidat actuellement."),
            Completion("Je te propose le premier candidat [E1]."),
            SemanticReview(
                "need_more_evidence",
                "Une recherche differente est necessaire."),
            Completion(Call("search-4", "rag_search", new
            {
                query = "fondant chocolat preparation simple"
            })),
            Completion("PRET_A_REDIGER: E2 apporte la recette directement exploitable."),
            Completion("Je te propose le fondant documente [E2]."),
            SemanticReview("accept", "E2 soutient directement la proposition."));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            SearchResultWithCards(
                "Cuisine/Fondant.pdf",
                "Cuisine",
                "fondant",
                page: 2,
                cardCount: 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 9,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Je veux un dessert au chocolat facile, tu proposes quoi ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Empty(llm.ToolSets[4]);
        Assert.Contains(llm.ToolSets[7], static tool => tool.Name == "rag_search");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "need_more_evidence");
    }

    [Fact]
    public async Task SplitActionWriter_ReturnsSemanticRevisionToTheOrchestratorBeforeRewriting()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: valeur technique sourcee."),
            Completion(Call("search-1", "rag_search", new { query = "couple serrage HX-42" })),
            Completion("PRET_A_REDIGER: la preuve technique est disponible."),
            Completion("Le premier brouillon reste vague [E1]."),
            SemanticReview("revise", "Employer la valeur exacte directement soutenue."),
            Completion("PRET_A_REDIGER: employer la valeur exacte de E1."),
            Completion("Le couple prescrit est de 85 NÂ·m [E1]."),
            SemanticReview("accept", "La valeur exacte est directement soutenue."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                MaximumTurns = 6,
                MaximumActionTokens = 320,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("85 NÂ·m [E1]", result.Answer);
        Assert.NotEmpty(llm.ToolSets[2]);
        Assert.Empty(llm.ToolSets[3]);
        Assert.NotEmpty(llm.ToolSets[5]);
        Assert.Empty(llm.ToolSets[6]);
        Assert.Equal(320, llm.MaxTokens[2]);
        Assert.Equal(900, llm.MaxTokens[3]);
        Assert.Contains("DECISION D'ORCHESTRATION UNIQUEMENT", RequestText(llm, 2));
        Assert.Contains("REDACTION FINALE SEPAREE", RequestText(llm, 3));
        Assert.Contains("redacteur final source-backed", RequestText(llm, 3));
        Assert.Contains("REVUE SEMANTIQUE INDEPENDANTE", RequestText(llm, 5));
        Assert.Contains("DECISION D'ORCHESTRATION UNIQUEMENT", RequestText(llm, 5));
        Assert.Contains("REDACTION FINALE SEPAREE", RequestText(llm, 6));
        Assert.Equal(
            new[] { "false", "false" },
            result.TraceEvents
                .Where(static trace => trace.EventName == "source_backed_agent_v2.writer.completed")
            .Select(static trace => trace.Fields["direct_revision"])
            .ToArray());
    }

    [Fact]
    public async Task SplitActionWriter_ReservesFollowUpAfterProductiveFinalTurnRetrieval()
    {
        var approvedIds = new[] { "E1", "E2" };
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: grille sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("cards-final", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 2,
                offset = 0
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-final",
                approvedIds,
                Array.Empty<string>())),
            Completion(Call(
                "selection-final",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = approvedIds,
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "Les deux valeurs retenues sont completes, distinctes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Option documentee A", 1),
                new MealCard("Option documentee B", 2)
            })),
            Options() with
            {
                MaximumTurns = 1,
                MaximumSemanticCorrectionTurns = 0,
                MaximumSelectionProtocolRepairTurns = 2,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de deux options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            approvedIds,
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.productive_retrieval.follow_up_reserved"
            && trace.Fields["turn"] == "1"
            && trace.Fields["new_evidence_count"] == "2"
            && trace.Fields["previous_maximum_run_turns"] == "1"
            && trace.Fields["maximum_run_turns"] == "3"
            && trace.Fields["audit_turn"] == "2"
            && trace.Fields["writer_turn"] == "3"
            && trace.Fields["decision_source"]
                == "mechanical_lifecycle_reservation");
    }

    [Fact]
    public async Task SplitActionWriter_RejectsStoppingAfterNavigationWithoutCitableEvidence()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: reponse sourcee."),
            Completion(Call("nav-1", "documents_navigation", new { categoryPath = "Manuals" })),
            Completion("PRET_A_REDIGER: aucune preuve n'est disponible."),
            Completion(Call("search-1", "rag_search", new { query = "couple serrage HX-42" })),
            Completion("PRET_A_REDIGER: la preuve technique est disponible."),
            Completion("Le couple prescrit est de 85 NÂ·m [E2]."),
            SemanticReview("accept", "La valeur exacte est directement soutenue."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(NavigationResult(), SearchResult()),
            Options() with
            {
                MaximumTurns = 6,
                MaximumActionTokens = 320,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("85 NÂ·m [E2]", result.Answer);
        Assert.Contains(
            "DECISION D'ARRET REFUSEE PAR LE CONTRAT MECANIQUE",
            RequestText(llm, 3));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.no_citable_evidence_rejected");
    }

    [Fact]
    public async Task SplitActionWriter_DuplicateNavigationCannotInviteAnUncitedAnswer()
    {
        var navigation = Call(
            "nav-1",
            "documents_navigation",
            new { categoryPath = "Manuals" });
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: reponse sourcee."),
            Completion(navigation),
            Completion(navigation with { Id = "nav-2" }),
            Completion(Call("search-1", "rag_search", new { query = "couple serrage HX-42" })),
            Completion("PRET_A_REDIGER: la preuve technique est disponible."),
            Completion("Le couple prescrit est de 85 NÃ‚Â·m [E2]."),
            SemanticReview("accept", "La valeur exacte est directement soutenue."));
        var executor = new ScriptedToolExecutor(NavigationResult(), SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumActionTokens = 320,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Quel est le couple prescrit pour HX-42 ?"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.navigation", "rag.search" },
            executor.ToolNames);
        Assert.Contains("PRET_A_REDIGER est invalide", RequestText(llm, 3));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.tool.duplicate_rejected");
    }

    [Fact]
    public async Task SplitActionWriter_EnforcesTheAtomicCountAuthoredByTheLlmPlan()
    {
        var cards = TwentyMealCards();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("search-1", "rag_search", new { query = "recettes nommees" })),
            Completion("PRET_A_REDIGER: quatre recettes sont visibles."),
            Completion(Call("cards-20", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion("PRET_A_REDIGER: vingt-quatre recettes sont visibles."),
            Completion(SelectionCall(
                "selection-20",
                Enumerable.Range(5, 20)
                    .Select(static index => $"E{index}")
                    .ToArray())),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont distinctes et directement soutenues."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Cuisine/recettes.pdf", "Cuisine", "initial", 4, 4),
            ContentCardInventoryResult(cards));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumActionTokens = 320,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.Equal(20, SourceContractVerifier.ExtractEvidenceIds(result.Answer).Count);
        Assert.Equal(
            new[] { "rag.search", "documents.content_cards" },
            executor.ToolNames);
        Assert.Contains("CONTRAT DU PLAN LLM", RequestText(llm, 3));
        Assert.Contains("20 preuves atomiques", RequestText(llm, 3));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.atomic_evidence_count_rejected"
            && trace.Fields["required"] == "20"
            && trace.Fields["observed"] == "4");
        Assert.Contains(
            "CONTRAT MECANIQUE DE TA SELECTION",
            RequestText(llm, 5));
        Assert.Equal(2, llm.Requests[5].Count);
        Assert.Contains(
            "CONTEXTE COMPACT DE SELECTION FINALE",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CANDIDATS CITABLES, SANS ORDRE DE PREFERENCE",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "| preuve=",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            RequestText(llm, 5).Length < 12000,
            $"Selection prompt has {RequestText(llm, 5).Length} characters.");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["required"] == "20"
            && trace.Fields["declared"] == "0");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.structured_renderer.completed"
            && trace.Fields["rows"] == "5"
            && trace.Fields["columns"] == "4"
            && trace.Fields["evidence_ids"] == "20");
        var selectionTool = Assert.Single(
            llm.ToolSets[5],
            static tool => tool.Name == "submit_evidence_selection");
        var selectionProperties = selectionTool.Parameters
            .GetProperty("properties");
        var evidenceIdsSchema = selectionProperties
            .GetProperty("evidenceIds");
        var layoutProperties = selectionProperties
            .GetProperty("layout")
            .GetProperty("properties");
        Assert.Equal(20, evidenceIdsSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(20, evidenceIdsSchema.GetProperty("maxItems").GetInt32());
        Assert.True(evidenceIdsSchema.GetProperty("uniqueItems").GetBoolean());
        Assert.Equal(
            4,
            layoutProperties.GetProperty("columns").GetProperty("minItems").GetInt32());
        Assert.Equal(
            5,
            layoutProperties.GetProperty("rows").GetProperty("minItems").GetInt32());
        Assert.True(
            layoutProperties.GetProperty("rows").GetProperty("uniqueItems").GetBoolean());
    }

    [Fact]
    public async Task SplitActionWriter_LetsTheLlmStopBelowAProvisionalFlatMultiItemTarget()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: options documentees\nDIMENSIONS: aucune grille imposee\n"
                + "PREUVES_ATOMIQUES: 5 options nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: options pertinentes\n"
                + "APPROCHE_OUTILS: recherche puis decision semantique\n"
                + "ACCEPTER_SI: assez d'options pour repondre utilement\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune option pertinente"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                inventoryMode = "representative",
                limit = 3
            })),
            Completion(CandidateAuditCall(
                "audit",
                new[] { "E1", "E2" },
                new[] { "E3" })),
            Completion(Call(
                "adaptive-checkpoint",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E2" },
                    reason = "Ces deux options repondent utilement a la demande ouverte."
                })),
            Completion("Option documentee A [E1]\nOption documentee B [E2]"),
            SemanticReview("accept", "Les deux options nommées sont documentées par E1 et E2."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Option documentee A", 1),
                new MealCard("Option documentee B", 2),
                new MealCard("Rubrique generique", 3)
            })),
            Options() with
            {
                MaximumTurns = 5,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Propose-moi quelques options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "E1", "E2" },
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        Assert.Contains(
            "NOMBRE_DE_PREUVES_SELECTIONNEES: 2",
            RequestText(llm, llm.Requests.Count - 2));
        Assert.Contains(
            "n'omets aucune unite selectionnee",
            RequestText(llm, llm.Requests.Count - 2));
        var adaptiveSelectionIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "submit_flat_evidence_selection"));
        Assert.True(adaptiveSelectionIndex >= 0);
        Assert.Contains(
            "PREUVES APPROUVEES DISPONIBLES",
            RequestText(llm, adaptiveSelectionIndex));
        Assert.Contains(
            "E1 | Option documentee A",
            RequestText(llm, adaptiveSelectionIndex));
        Assert.Contains(
            "E2 | Option documentee B",
            RequestText(llm, adaptiveSelectionIndex));
        var adaptiveSelectionTool = Assert.Single(
            llm.ToolSets[adaptiveSelectionIndex],
            static tool => tool.Name == "submit_flat_evidence_selection");
        var adequacyToolSet = llm.ToolSets[adaptiveSelectionIndex];
        Assert.Contains(
            adequacyToolSet,
            static tool => tool.Name == "submit_flat_evidence_selection");
        Assert.Contains(
            adequacyToolSet,
            static tool => tool.Name == "submit_flat_evidence_gap");
        Assert.Contains(
            adequacyToolSet,
            static tool => tool.Name == "submit_flat_evidence_clarification");
        var evidenceIdsSchema = adaptiveSelectionTool.Parameters
            .GetProperty("properties")
            .GetProperty("evidenceIds");
        Assert.Contains(
            "toutes les composantes explicitement demandees",
            RequestText(llm, adaptiveSelectionIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, evidenceIdsSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(2, evidenceIdsSchema.GetProperty("maxItems").GetInt32());
        Assert.Contains(
            result.TraceEvents,
            static trace =>
                trace.EventName
                    == "source_backed_agent_v2.action.semantic_selection_accepted"
                && trace.Fields["selected_evidence"] == "E1,E2"
                && trace.Fields["count_mode"] == "adaptive_flat_target");
        Assert.DoesNotContain(
            result.TraceEvents,
            static trace =>
                trace.EventName
                    == "source_backed_agent_v2.action.semantic_selection_rejected");
    }

    [Fact]
    public async Task SplitActionWriter_FlatAdequacyCanExpandItsChosenEvidenceContext()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "pompe HX-42 exigences",
            topK = 4
        });
        var intake = Intake("Résume les deux exigences documentées de la pompe HX-42.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées",
                    structuredLayout = false,
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigences documentées",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-search",
                new[] { "E1" },
                Array.Empty<string>())),
            Completion(Call(
                "flat-context-gap",
                "submit_flat_evidence_context_gap",
                new
                {
                    evidenceId = "E1",
                    missingRequirements = new[]
                    {
                        "La seconde exigence documentée du même équipement"
                    },
                    reason =
                        "Le passage visible localise le bon équipement et son contexte peut contenir l'exigence complémentaire."
                })),
            Completion(CandidateAuditCall(
                "audit-context",
                new[] { "E2" },
                Array.Empty<string>())),
            Completion(Call(
                "flat-selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E2" }
                })),
            Completion(
                "Le serrage prescrit est de 85 N·m [E1]. "
                + "Le contrôle complémentaire doit être consigné [E2]."),
            SemanticReview("accept", "E1 décrit le serrage et E2 la consignation du contrôle."));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            Results(
                "documents.context",
                new
                {
                    document = new
                    {
                        docId = "doc-hx42",
                        docName = "HydraulicPumpManual.pdf",
                        docPath = "Manuals/HydraulicPumpManual.pdf"
                    },
                    items = new[]
                    {
                        new
                        {
                            docId = "doc-hx42",
                            docName = "HydraulicPumpManual.pdf",
                            docPath = "Manuals/HydraulicPumpManual.pdf",
                            pageStart = 43,
                            pageEnd = 43,
                            chunkId = "doc-hx42:43:1",
                            text =
                                "Après serrage, consigner le contrôle complémentaire dans le registre de maintenance."
                        }
                    }
                }));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 7,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "rag.search", "documents.context" },
            executor.ToolNames);
        var adequacyToolSetIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "submit_flat_evidence_context_gap"));
        Assert.True(adequacyToolSetIndex >= 0);
        var contextGapTool = Assert.Single(
            llm.ToolSets[adequacyToolSetIndex],
            static tool => tool.Name == "submit_flat_evidence_context_gap");
        Assert.Equal(
            new[] { "E1" },
            contextGapTool.Parameters
                .GetProperty("properties")
                .GetProperty("evidenceId")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        var contextArguments = executor.Arguments[1];
        Assert.Equal("doc-hx42", contextArguments
            .GetProperty("docId").GetString());
        Assert.Equal("Manuals/HydraulicPumpManual.pdf", contextArguments
            .GetProperty("docPath").GetString());
        Assert.Equal("doc-hx42:42:3", contextArguments
            .GetProperty("chunkId").GetString());
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.flat_evidence_adequacy.completed"
            && trace.Fields["decision"] == "expand_context"
            && trace.Fields["approved_evidence_ids"] == "E1");
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.strategy_completed");
    }

    [Fact]
    public async Task SplitActionWriter_FlatAdequacyRejectsUnpublishedContextEvidenceId()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "pompe HX-42 exigences",
            topK = 4
        });
        var intake = Intake("Résume les deux exigences documentées de la pompe HX-42.") with
        {
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    initialSearch.Id,
                    initialSearch.Name,
                    initialSearch.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    planKind = "multi_item",
                    deliverable = "deux exigences documentées",
                    structuredLayout = false,
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigences documentées",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(CandidateAuditCall(
                "audit-search",
                new[] { "E1" },
                Array.Empty<string>())),
            Completion(Call(
                "flat-context-gap-invalid",
                "submit_flat_evidence_context_gap",
                new
                {
                    evidenceId = "E999",
                    missingRequirements = new[]
                    {
                        "Une exigence documentaire complémentaire"
                    },
                    reason = "La preuve inventée prétend localiser le bon document."
                })),
            Completion(Call(
                "fallback-search",
                "start_document_search",
                new
                {
                    query = "exigence complémentaire HX-42",
                    limit = 4
                })));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            new ToolResults());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 3,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                SemanticCandidateStrategyEnabled = false,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.DoesNotContain("documents.context", executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.flat_evidence_adequacy.completed"
            && trace.Fields["decision"] == "continue"
            && trace.Fields["protocol_valid"] == "false"
            && trace.Fields["reason"]
                == "Le checkpoint de contexte n'a pas respecte son contrat.");
    }

    [Fact]
    public async Task SplitActionWriter_FlatAdequacyCheckpointCanClarifyAfterObservation()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: options documentees\nDIMENSIONS: aucune grille imposee\n"
                + "PREUVES_ATOMIQUES: 3 options nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: options pertinentes\n"
                + "APPROCHE_OUTILS: recherche puis decision semantique\n"
                + "ACCEPTER_SI: assez d'options pour repondre utilement\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune option pertinente"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                inventoryMode = "representative",
                limit = 2
            })),
            Completion(CandidateAuditCall(
                "audit",
                new[] { "E1", "E2" },
                Array.Empty<string>())),
            Completion(Call(
                "clarification-checkpoint",
                "submit_flat_evidence_clarification",
                new
                {
                    understanding = "Plusieurs options documentees correspondent a la demande.",
                    options = new[]
                    {
                        "Comparer les options",
                        "Choisir une option precise"
                    },
                    executionImpact =
                        "La reponse determinera si le livrable compare ou detaille une option.",
                    ambiguityKind = "deliverable",
                    reason = "Seul l'utilisateur peut choisir la forme du livrable."
                })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Option documentee A", 1),
                new MealCard("Option documentee B", 2)
            })),
            Options() with
            {
                MaximumTurns = 5,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne-moi les options documentees."),
            CancellationToken.None);

        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Equal("clarify", result.JudgeDecision.Decision);
        Assert.Equal(2, clarification.Options.Count);
        Assert.Contains("Comparer les options", clarification.Options);
        Assert.Null(result.FinalDraft);
        Assert.Contains(
            result.TraceEvents,
            static trace =>
                trace.EventName
                    == "source_backed_agent_v2.flat_evidence_adequacy.completed"
                && trace.Fields["decision"] == "clarify"
                && trace.Fields["protocol_valid"] == "true");
        Assert.Contains(
            result.TraceEvents,
            static trace =>
                trace.EventName
                    == "source_backed_agent_v2.clarification.requested"
                && trace.Fields["decision_source"] == "llm_orchestrator");
    }

    [Fact]
    public async Task SplitActionWriter_UsesTheLlmWorkspaceDecisionAsTheFinalWideSelection()
    {
        var selectedIds = new[] { "E1", "E2" };
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: deux options sourcees\nDIMENSIONS: deux options\n"
                + "PREUVES_ATOMIQUES: 2 options nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: options documentees\n"
                + "APPROCHE_OUTILS: inventaire puis decision\n"
                + "ACCEPTER_SI: deux options distinctes\n"
                + "INSUFFISANT_SEULEMENT_SI: moins de deux options"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 1,
                offset = 0
            })),
            Completion(Call("cards-next", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 1,
                offset = 5
            })),
            Completion(Call("workspace-ready", "manage_evidence_workspace", new
            {
                status = "ready_to_write",
                retainEvidenceIds = selectedIds,
                note = "Ces deux options nommees satisfont la demande."
            })),
            Completion("Option documentee A [E1]\nOption documentee B [E2]"),
            SemanticReview(
                "accept",
                "Les deux options distinctes sont directement soutenues."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                PagedContentCardInventoryResult(),
                ContentCardInventoryResult(new[]
                {
                    new MealCard("Option documentee B", 2)
                })),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne deux options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            selectedIds,
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        Assert.Equal(6, llm.Requests.Count);
        Assert.DoesNotContain(
            llm.ToolSets[2],
            static tool => tool.Name == "manage_evidence_workspace");
        var workspaceTool = Assert.Single(
            llm.ToolSets[3],
            static tool => tool.Name == "manage_evidence_workspace");
        Assert.Contains(
            "ready_to_write",
            workspaceTool.Parameters.GetRawText(),
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
            == "source_backed_agent_v2.evidence_workspace.ready_to_write_accepted"
            && trace.Fields["selected_evidence_ids"] == "E1,E2");
    }

    [Fact]
    public async Task SplitActionWriter_WorkspaceHandoffRejectsDuplicateVisibleSources()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: deux options sourcees\nDIMENSIONS: deux options\n"
                + "PREUVES_ATOMIQUES: 2 options nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: options documentees\n"
                + "APPROCHE_OUTILS: inventaire puis decision\n"
                + "ACCEPTER_SI: deux options distinctes\n"
                + "INSUFFISANT_SEULEMENT_SI: moins de deux options"),
            Completion(Call("context", "documents_context", new
            {
                docPath = "Knowledge/options.pdf",
                pageStart = 1,
                pageEnd = 2
            })),
            Completion(Call("workspace-duplicate", "manage_evidence_workspace", new
            {
                status = "ready_to_write",
                retainEvidenceIds = new[] { "E1", "E2" },
                note = "Premiere selection."
            })),
            Completion(Call("workspace-ready", "manage_evidence_workspace", new
            {
                status = "ready_to_write",
                retainEvidenceIds = new[] { "E1", "E3" },
                note = "Une preuve par source visible."
            })),
            Completion("Option documentee A [E1]\nOption documentee C [E3]"),
            SemanticReview(
                "accept",
                "Les deux options proviennent de sources visibles distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContextResultWithVisibleSourceDuplicate(1, 3)),
            Options() with
            {
                MaximumTurns = 5,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne deux options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "E1", "E3" },
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        var readyWorkspaceTool = Assert.Single(
            llm.ToolSets[2],
            static tool => tool.Name == "manage_evidence_workspace");
        var allowedReadyIds = readyWorkspaceTool.Parameters
            .GetProperty("properties")
            .GetProperty("retainEvidenceIds")
            .GetProperty("items")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        Assert.Equal(new[] { "E1", "E3" }, allowedReadyIds);
        Assert.DoesNotContain("E2", allowedReadyIds);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.evidence_workspace.rejected"
            && trace.Fields["error"].Contains(
                "workspace_ready_to_write_requires_unique_visible_sources",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task SplitActionWriter_RequestsLlmSelectionAfterTwoDuplicateOnlyTurns()
    {
        var inventoryCall = Call("cards-1", "documents_content_cards", new
        {
            categoryPath = "Cuisine",
            limit = 20,
            offset = 0
        });
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(inventoryCall),
            Completion(inventoryCall with { Id = "cards-2" }),
            Completion(inventoryCall with { Id = "cards-3" }),
            Completion(SelectionCall(
                "selection-20",
                Enumerable.Range(1, 20)
                    .Select(static index => $"E{index}")
                    .ToArray())),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont distinctes et directement soutenues."));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(TwentyMealCards()));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Single(executor.ToolNames);
        Assert.Equal(
            new[] { "submit_evidence_selection" },
            llm.ToolSets[4].Select(static tool => tool.Name).ToArray());
        Assert.True(llm.RequireToolCalls[4]);
        Assert.Contains(
            "BUDGET MECANIQUE SANS PROGRES",
            RequestText(llm, 4),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.documentary_no_progress.selection_requested"
            && trace.Fields["consecutive_no_progress_turns"] == "2");
    }

    [Fact]
    public async Task SplitActionWriter_DoesNotRequestImpossibleSelectionAfterDuplicateLoop()
    {
        var repeated = Call("search-1", "rag_search", new
        {
            query = "options documentees"
        });
        var selectedCards = Enumerable.Range(2, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 instances"),
            Completion(repeated),
            Completion(repeated with { Id = "search-2" }),
            Completion(repeated with { Id = "search-3" }),
            Completion(Call("cards-1", "submit_research_action", new
            {
                capability = "documents_content_cards",
                query = "",
                scope = "Cuisine",
                document = "",
                anchor = "",
                navigationKind = "",
                inventoryMode = "representative",
                limit = 20,
                offset = 0
            })),
            Completion(SelectionCall("selection-20", selectedCards)),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont distinctes et directement soutenues."));
        var executor = new ScriptedToolExecutor(
            SearchResult(),
            ContentCardInventoryResult(TwentyMealCards()));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt instances documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[]
            {
                "submit_research_action",
                "request_user_clarification",
                "declare_source_insufficiency"
            },
            llm.ToolSets[4].Select(static tool => tool.Name).ToArray());
        Assert.DoesNotContain(
            llm.ToolSets[4],
            static tool => tool.Name == "submit_evidence_selection");
        Assert.Equal(
            new[] { "submit_evidence_selection" },
            llm.ToolSets[5].Select(static tool => tool.Name).ToArray());
        Assert.True(llm.RequireToolCalls[5]);
        Assert.Equal(
            new[] { "rag.search", "documents.content_cards" },
            executor.ToolNames);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.documentary_no_progress.recovery_requested"
            && trace.Fields["observed"] == "1");
    }

    [Fact]
    public async Task SplitActionWriter_NamesDuplicatedEvidenceIdsInSelectionRepair()
    {
        var duplicatedSelection = Enumerable.Range(1, 19)
            .Select(static index => $"E{index}")
            .Concat(new[] { "E1" })
            .ToArray();
        var validSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion(SelectionCall("selection-duplicate", duplicatedSelection)),
            Completion(SelectionCall("selection-valid", validSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont completes et distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(TwentyMealCards())),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "selection_evidence_ids_not_unique:E1",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["declared"] == "20"
            && trace.Fields["selection_contract_error"].Contains(
                "E1",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SplitActionWriter_RejectsDifferentIdsForTheSameVisibleSourceBeforeRendering()
    {
        var duplicateVisibleSourceSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var validSelection = new[] { "E1" }
            .Concat(Enumerable.Range(3, 19)
                .Select(static index => $"E{index}"))
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 instances"),
            Completion(Call("context-1", "documents_context", new
            {
                docPath = "Knowledge/options.pdf",
                pageStart = 1,
                pageEnd = 9
            })),
            Completion(Call("context-2", "documents_context", new
            {
                docPath = "Knowledge/options.pdf",
                pageStart = 10,
                pageEnd = 19
            })),
            Completion(Call("context-3", "documents_context", new
            {
                docPath = "Knowledge/options.pdf",
                pageStart = 20,
                pageEnd = 21
            })),
            Completion(SelectionCall(
                "selection-duplicate-source",
                duplicateVisibleSourceSelection)),
            Completion(SelectionCall("selection-valid", validSelection)),
            SemanticReview(
                "accept",
                "Les vingt instances sont distinctes et correctement sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContextResultWithVisibleSourceDuplicate(1, 10),
                ContextResultWithVisibleSourceDuplicate(11, 10),
                ContextResultWithVisibleSourceDuplicate(21, 2)),
            Options() with
            {
                MaximumTurns = 6,
                MaximumObservationItems = 10,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt instances documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "E2=meme_source_que_E1",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "groupe_source=V001",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        var firstSelectionPrompt = RequestText(llm, 4);
        Assert.Contains(
            "- E21 |",
            firstSelectionPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "- E2 |",
            firstSelectionPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["duplicate_visible_source_ids"] == "E2");
        Assert.DoesNotContain("[E2]", result.Answer);
        Assert.Equal(
            20,
            SourceContractVerifier.ExtractEvidenceIds(result.Answer).Count);
    }

    [Fact]
    public async Task SplitActionWriter_RejectsDifferentSourcesWithTheSameFinalDisplayValue()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: grille sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 3
            })),
            Completion(Call(
                "selection-duplicate-value",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E2" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            Completion(Call(
                "selection-valid",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1", "E3" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "Les deux valeurs finales sont distinctes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("Repeated value", 1),
                new MealCard("Repeated value", 2),
                new MealCard("Distinct value", 3)
            })),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 8,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de deux instances documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "E2=meme_valeur_que_E1(Repeated value)",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["duplicate_display_value_ids"] == "E2");
        Assert.DoesNotContain("[E2]", result.Answer);
        Assert.Contains("Repeated value [E1]", result.Answer);
        Assert.Contains("Distinct value [E3]", result.Answer);
    }

    [Fact]
    public async Task SplitActionWriter_ExtendsOnlyTheProtocolBudgetForAnInvalidFinalSelection()
    {
        var duplicatedSelection = Enumerable.Range(1, 19)
            .Select(static index => $"E{index}")
            .Concat(new[] { "E1" })
            .ToArray();
        var validSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 instances"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 20
            })),
            Completion(SelectionCall("selection-final-invalid", duplicatedSelection)),
            Completion(SelectionCall("selection-protocol-repair", validSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont completes et distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(TwentyMealCards())),
            Options() with
            {
                MaximumTurns = 2,
                MaximumSemanticCorrectionTurns = 0,
                MaximumSelectionProtocolRepairTurns = 1,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de vingt instances documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(5, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.selection_protocol_repair.extended"
            && trace.Fields["repair_turn"] == "1"
            && trace.Fields["maximum_run_turns"] == "3"
            && trace.Fields["selection_contract_error"].Contains(
                "not_unique",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SplitActionWriter_StopsAfterTwoIdenticalInvalidSelections()
    {
        var duplicatedSelection = Enumerable.Range(1, 19)
            .Select(static index => $"E{index}")
            .Concat(new[] { "E1" })
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 instances"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 20
            })),
            Completion(SelectionCall("selection-invalid-1", duplicatedSelection)),
            Completion(SelectionCall("selection-invalid-2", duplicatedSelection)));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(TwentyMealCards())),
            Options() with
            {
                MaximumTurns = 4,
                MaximumSemanticCorrectionTurns = 0,
                MaximumSelectionProtocolRepairTurns = 2,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de vingt instances documentees."),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.selection_protocol_repeat.stopped"
            && trace.Fields["identical_invalid_selections"] == "2"
            && trace.Fields["selection_contract_error"].Contains(
                "not_unique",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SplitActionWriter_PreservesEvidenceIdsAndExplainsAnInvalidLayout()
    {
        var evidenceIds = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var invalidLayoutCall = Call(
            "selection-invalid-layout",
            "submit_evidence_selection",
            new
            {
                evidenceIds,
                layout = new
                {
                    rowHeader = "Jour",
                    columns = new[]
                    {
                        "Petit-dejeuner",
                        "Dejeuner",
                        "Collation",
                        "Souper"
                    },
                    rows = new[]
                    {
                        new { label = "Lundi" },
                        new { label = "Mardi" },
                        new { label = "Mercredi" },
                        new { label = "Jeudi" },
                        new { label = "Vendredi" }
                    }
                }
            });
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion(invalidLayoutCall),
            Completion(SelectionCall("selection-valid", evidenceIds)),
            SemanticReview(
                "accept",
                "Les vingt valeurs selectionnees sont completes et distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(TwentyMealCards())),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "selection_layout_rows_invalid",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "20 entrees EvidenceId",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["declared"] == "20"
            && trace.Fields["selection_arguments"].Contains(
                "\"label\":\"Lundi\"",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task SplitActionWriter_UsesReservedSemanticCorrectionTurnWithoutReopeningRetrieval()
    {
        var cards = TwentyMealCards()
            .Concat(new[] { new MealCard("Recette documentee 21", 21) })
            .ToArray();
        var firstSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var revisedSelection = Enumerable.Range(2, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 21
            })),
            Completion(SelectionCall("selection-first", firstSelection)),
            SemanticReview(
                "revise",
                "E1 doit etre remplace par E21.",
                rejectedEvidenceIds: new[] { "E1" },
                preferredAlternativeEvidenceIds: new[] { "E21" }),
            Completion(SelectionCall("selection-revised", revisedSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs revisees sont completes et distinctes."));
        var executor = new ScriptedToolExecutor(ContentCardInventoryResult(cards));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                MaximumSemanticCorrectionTurns = 1,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.Contains("[E21]", result.Answer);
        Assert.Equal(new[] { "documents.content_cards" }, executor.ToolNames);
        Assert.Equal(
            new[] { "submit_evidence_selection" },
            llm.ToolSets[4].Select(static tool => tool.Name).ToArray());
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.started"
            && trace.Fields["maximum_turns"] == "2"
            && trace.Fields["maximum_semantic_correction_turns"] == "1"
            && trace.Fields["maximum_run_turns"] == "3");
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_accepted"
            && trace.Fields["turn"] == "3");
    }

    [Fact]
    public async Task SplitActionWriter_ReopensRetrievalWhenJudgeSaysObservedPoolIsInsufficient()
    {
        var cards = TwentyMealCards();
        var firstSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var revisedSelection = Enumerable.Range(2, 19)
            .Select(static index => $"E{index}")
            .Append("E22")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion(SelectionCall("selection-first", firstSelection)),
            SemanticReview(
                "need_more_evidence",
                "Apres rejet de E1, seulement dix-neuf recettes valides restent visibles.",
                rejectedEvidenceIds: new[] { "E1" }),
            Completion(Call("search-more", "rag_search", new
            {
                query = "nouvelle recette complete",
                categoryPath = "Cuisine"
            })),
            Completion(SelectionCall("selection-revised", revisedSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs sont maintenant completes, distinctes et sourcees."));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(cards),
            SearchResultWithCards("Cuisine/Nouvelles-recettes.pdf", "Cuisine", "new", 42, 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                MaximumSemanticCorrectionTurns = 2,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.content_cards", "rag.search" },
            executor.ToolNames);
        Assert.Contains(llm.ToolSets[4], static tool => tool.Name == "rag_search");
        Assert.Contains(llm.ToolSets[4], static tool => tool.Name == "documents_content_cards");
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.Contains("[E22]", result.Answer);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "need_more_evidence");
    }

    [Fact]
    public async Task SplitActionWriter_ReservesTwoCompleteSemanticRepairCycles()
    {
        var firstSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var secondSelection = Enumerable.Range(2, 19)
            .Select(static index => $"E{index}")
            .Append("E22")
            .ToArray();
        var finalSelection = Enumerable.Range(3, 18)
            .Select(static index => $"E{index}")
            .Concat(new[] { "E22", "E24" })
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion(SelectionCall("selection-first", firstSelection)),
            SemanticReview(
                "need_more_evidence",
                "E1 n'est pas une recette nommee.",
                rejectedEvidenceIds: new[] { "E1" }),
            Completion(Call("search-first-repair", "rag_search", new
            {
                query = "recette complete supplementaire",
                categoryPath = "Cuisine"
            })),
            Completion(SelectionCall("selection-second", secondSelection)),
            SemanticReview(
                "need_more_evidence",
                "E2 n'est pas une recette nommee.",
                rejectedEvidenceIds: new[] { "E2" }),
            Completion(Call("search-second-repair", "rag_search", new
            {
                query = "autre recette complete",
                categoryPath = "Cuisine"
            })),
            Completion(SelectionCall("selection-final", finalSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs sont maintenant completes, distinctes et sourcees."));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(TwentyMealCards()),
            SearchResultWithCards("Cuisine/Partiel.pdf", "Cuisine", "partial", 41, 1),
            SearchResultWithCards("Cuisine/Final.pdf", "Cuisine", "final", 42, 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 2,
                MaximumSemanticCorrectionTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "documents.content_cards", "rag.search", "rag.search" },
            executor.ToolNames);
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.DoesNotContain("[E2]", result.Answer);
        Assert.Contains("[E22]", result.Answer);
        Assert.Contains("[E24]", result.Answer);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.started"
            && trace.Fields["maximum_run_turns"] == "6");
        Assert.Equal(
            2,
            result.TraceEvents.Count(trace =>
                trace.EventName == "source_backed_agent_v2.semantic_review.completed"
                && trace.Fields["decision"] == "need_more_evidence"));
    }

    [Fact]
    public async Task SplitActionWriter_DoesNotReserveSelectionWhileRejectedPoolHasOnlyNineteenSources()
    {
        var firstSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var finalSelection = Enumerable.Range(3, 18)
            .Select(static index => $"E{index}")
            .Concat(new[] { "E22", "E24" })
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20
            })),
            Completion(SelectionCall("selection-first", firstSelection)),
            SemanticReview(
                "need_more_evidence",
                "Deux valeurs ne sont pas des recettes nommees.",
                rejectedEvidenceIds: new[] { "E1", "E2" }),
            Completion(Call("search-partial", "rag_search", new
            {
                query = "recette complete supplementaire",
                categoryPath = "Cuisine"
            })),
            Completion(Call("search-final", "rag_search", new
            {
                query = "autre recette complete",
                categoryPath = "Cuisine"
            })),
            Completion(SelectionCall("selection-final", finalSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs sont maintenant distinctes et suffisamment prouvees."));
        var executor = new ScriptedToolExecutor(
            ContentCardInventoryResult(TwentyMealCards()),
            SearchResultWithCards("Cuisine/Partiel.pdf", "Cuisine", "partial", 41, 1),
            SearchResultWithCards("Cuisine/Final.pdf", "Cuisine", "final", 42, 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 6,
                MaximumSemanticCorrectionTurns = 2,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(llm.ToolSets[5], static tool => tool.Name == "rag_search");
        Assert.Contains(
            llm.ToolSets[5],
            static tool => tool.Name == "documents_content_cards");
        Assert.Equal(
            new[] { "documents.content_cards", "rag.search", "rag.search" },
            executor.ToolNames);
    }

    [Fact]
    public async Task SplitActionWriter_DoesNotReselectEvidenceExplicitlyRejectedByTheLlmJudge()
    {
        var cards = TwentyMealCards()
            .Concat(new[] { new MealCard("Recette documentee 21", 21) })
            .ToArray();
        var firstSelection = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var revisedSelection = Enumerable.Range(2, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 21
            })),
            Completion(SelectionCall("selection-1", firstSelection)),
            SemanticReview(
                "revise",
                "E1 est un libelle generique; E21 est une alternative complete.",
                rejectedEvidenceIds: new[] { "E1" },
                preferredAlternativeEvidenceIds: new[] { "E21" }),
            Completion(SelectionCall("selection-repeated", firstSelection)),
            Completion(SelectionCall("selection-2", revisedSelection)),
            SemanticReview(
                "accept",
                "Les vingt valeurs revisees sont completes.",
                rejectedEvidenceIds: Array.Empty<string>(),
                preferredAlternativeEvidenceIds: Array.Empty<string>()));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.DoesNotContain("[E1]", result.Answer);
        Assert.Contains("[E21]", result.Answer);
        Assert.Contains(
            "EvidenceId explicitement rejetes par le juge LLM precedent: E1",
            RequestText(llm, 5),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.action.semantic_selection_rejected"
            && trace.Fields["semantically_rejected_evidence_ids"] == "E1");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["rejected_evidence_ids"] == "E1"
            && trace.Fields["preferred_alternative_evidence_ids"] == "E21");
    }

    [Fact]
    public async Task SplitActionWriter_ReturnsToolsToTheLlmAfterAnIdenticalRejectedRevision()
    {
        const string repeatedDraft = "Un composant incomplet [E2].";
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option concrete et sourcee."),
            Completion(Call("first", "rag_search", new { query = "option initiale" })),
            Completion("PRET_A_REDIGER: une preuve est visible."),
            Completion(repeatedDraft),
            SemanticReview("revise", "Le composant reste generique."),
            Completion("PRET_A_REDIGER: reviser avec la preuve visible."),
            Completion(repeatedDraft),
            SemanticReview("revise", "Le composant reste generique."),
            Completion(Call("second", "rag_search", new { query = "option complete" })),
            Completion("PRET_A_REDIGER: une option complete est maintenant visible."),
            Completion("L'option complete new-card-1 est disponible [E4]."),
            SemanticReview("accept", "E4 soutient une option complete et utilisable."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Knowledge/Initial.pdf", "Knowledge", "old", 2, 1),
            SearchResultWithCards("Knowledge/Complete.pdf", "Knowledge", "new", 4, 1));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 7,
                SeparateActionAndWriter = true
            });

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains(
            "REVISION SANS PROGRES DETECTEE MECANIQUEMENT",
            RequestText(llm, 8));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
            == "source_backed_agent_v2.semantic_revision.no_progress_returned_to_orchestrator");
    }

    [Fact]
    public async Task SemanticJudge_CanRejectADraftAndReturnControlToTheToolUsingOrchestrator()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option concrete et correctement sourcee."),
            Completion(Call("first", "rag_search", new { query = "option initiale" })),
            Completion("Un composant incomplet [E2]."),
            SemanticReview("need_more_evidence", "E2 est un composant, pas une option complete."),
            Completion(Call("second", "rag_search", new { query = "option complete" })),
            Completion("L'option complete new-card-1 est disponible [E4]."),
            SemanticReview("accept", "E4 soutient une option complete et utilisable."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Knowledge/Initial.pdf", "Knowledge", "old", 2, 1),
            SearchResultWithCards("Knowledge/Complete.pdf", "Knowledge", "new", 4, 1));
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("new-card-1", result.Answer);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains("rejectedEvidenceIds", RequestText(llm, 3));
        Assert.True(llm.RequireToolCalls[3]);
        var reviewTool = Assert.Single(
            llm.ToolSets[3],
            static tool => tool.Name == "submit_semantic_review");
        var reviewProperties = reviewTool.Parameters.GetProperty("properties");
        Assert.True(reviewProperties.TryGetProperty("rejectedEvidenceIds", out _));
        Assert.True(reviewProperties.TryGetProperty("preferredAlternativeEvidenceIds", out _));
        Assert.Contains("REVUE SEMANTIQUE INDEPENDANTE", RequestText(llm, 4));
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "need_more_evidence");
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
    }

    [Fact]
    public async Task SemanticJudge_ReopensToolsWhenARevisionRejectsEvidenceWithoutEnoughNamedReplacements()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option concrete et correctement sourcee."),
            Completion(Call("first", "rag_search", new { query = "option initiale" })),
            Completion("Un composant incomplet [E2]."),
            SemanticReview(
                "revise",
                "E2 est un composant invalide qui doit etre remplace.",
                rejectedEvidenceIds: new[] { "E2" },
                preferredAlternativeEvidenceIds: Array.Empty<string>()),
            Completion(Call("second", "rag_search", new { query = "option complete" })),
            Completion("L'option complete new-card-1 est disponible [E4]."),
            SemanticReview("accept", "E4 soutient une option complete et utilisable."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Knowledge/Initial.pdf", "Knowledge", "old", 2, 1),
            SearchResultWithCards("Knowledge/Complete.pdf", "Knowledge", "new", 4, 1));
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("new-card-1", result.Answer);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains(
            "Revision non soutenue",
            RequestText(llm, 4),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "need_more_evidence"
            && trace.Fields["contract_adjusted"] == "true"
            && trace.Fields["rejected_evidence_count"] == "1"
            && trace.Fields["preferred_alternative_count"] == "0");
    }

    [Fact]
    public async Task SemanticJudge_AllowsOneReplacementForSeveralProofFragmentsOfOneValue()
    {
        var cards = new[]
        {
            new MealCard("Fragment provisoire A", 1),
            new MealCard("Fragment provisoire B", 2),
            new MealCard("Option complete", 3)
        };
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: une option\nDIMENSIONS: aucune\n"
                + "PREUVES_ATOMIQUES: 1 option complete\n"
                + "INTENTIONS_RECHERCHE: option\n"
                + "APPROCHE_OUTILS: documents_content_cards\n"
                + "PREMIERE_ACTION: aucune\n"
                + "ACCEPTER_SI: une option sourcee\n"
                + "INSUFFISANT_SEULEMENT_SI: aucune option"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 3
            })),
            Completion("Option provisoire [E1] [E2]."),
            SemanticReview(
                "revise",
                "Les deux fragments soutiennent une seule valeur invalide; E3 la remplace.",
                rejectedEvidenceIds: new[] { "E1", "E2" },
                preferredAlternativeEvidenceIds: new[] { "E3" }),
            Completion("L'option complete est disponible [E3]."),
            SemanticReview(
                "accept",
                "E3 soutient une option complete et utilisable."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options());

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("[E3]", result.Answer);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "revise"
            && trace.Fields["contract_adjusted"] == "false"
            && trace.Fields["rejected_evidence_count"] == "2"
            && trace.Fields["preferred_alternative_count"] == "1");
    }

    [Fact]
    public async Task SemanticJudge_RetriesOnceWithMoreTokensWhenTheToolCallIsTruncated()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option concrete et correctement sourcee."),
            Completion(Call("first", "rag_search", new { query = "option complete" })),
            Completion("L'option complete est disponible [E1]."),
            new SourceBackedAgentCompletion(
                string.Empty,
                Array.Empty<SourceBackedAgentToolCall>(),
                "length",
                200,
                320),
            SemanticReview(
                "accept",
                "Une instance complete, distincte et utilisable a ete verifiee: E1."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                MaximumSemanticReviewTokens = 320
            });

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(320, llm.MaxTokens[3]);
        Assert.Equal(768, llm.MaxTokens[4]);
        Assert.Contains(
            "REPARATION DE PROTOCOLE",
            RequestText(llm, 4),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["attempts"] == "2"
            && trace.Fields["truncation_retry_exhausted"] == "false");
    }

    [Fact]
    public async Task SemanticJudge_RejectsAnUnjustifiedAcceptAndReturnsControlToTheLlm()
    {
        var llm = new ScriptedAgentLlm(
            Completion("Livrable: une option concrete et correctement sourcee."),
            Completion(Call("first", "rag_search", new { query = "option initiale" })),
            Completion("Un composant incomplet [E2]."),
            Completion("""{"decision":"accept","reasons":[]}"""),
            Completion(Call("second", "rag_search", new { query = "option complete" })),
            Completion("L'option complete new-card-1 est disponible [E4]."),
            SemanticReview(
                "accept",
                "Une instance complete, distincte et utilisable a ete verifiee: E4."));
        var executor = new ScriptedToolExecutor(
            SearchResultWithCards("Knowledge/Initial.pdf", "Knowledge", "old", 2, 1),
            SearchResultWithCards("Knowledge/Complete.pdf", "Knowledge", "new", 4, 1));
        var runner = new SourceBackedAgentV2Runner(llm, executor, Options());

        var result = await runner.RunAsync(
            Intake("Donne une option complete et documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(2, executor.ToolNames.Count);
        Assert.Contains(result.TraceEvents, trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "need_more_evidence"
            && trace.Fields["reasons"].Contains(
                "aucune justification",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SemanticJudge_ReceivesTheCompleteTwentyCellDraftAndEveryCitedCard()
    {
        var cards = TwentyMealCards();
        var draft = string.Join(
            Environment.NewLine,
            cards.Select((card, index) => $"{card.Title} [E{index + 1}]"));
        Assert.True(draft.Length > 2600);
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning de repas\nDIMENSIONS: 5 jours x 4 moments\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nACCEPTER_SI: 20 cellules citees\n"
                + "INSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards-20", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 40
            })),
            Completion(draft),
            SemanticReview("accept", "Les vingt recettes distinctes et citees sont toutes visibles."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with { MaximumWorkingEvidenceItems = 40 });

        var result = await runner.RunAsync(
            Intake(
                "Fais un planning du lundi au vendredi avec petit-dejeuner, dejeuner, collation et souper."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var judgeRequest = RequestText(llm, 3);
        Assert.Contains("Vendredi-Souper", judgeRequest);
        Assert.Contains("E20 | valeur=Recette 20", judgeRequest);
        Assert.Contains("fichier=recettes.pdf", judgeRequest);
        Assert.True(llm.RequireToolCalls[3]);
        Assert.Contains(
            "axes, jours, colonnes et affectations",
            judgeRequest,
            StringComparison.OrdinalIgnoreCase);
    }
}
