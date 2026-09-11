using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    [Fact]
    public void SemanticAnswerTransaction_IsDisabledByDefault()
        => Assert.False(Options().SemanticAnswerTransactionEnabled);

    [Fact]
    public async Task SemanticAnswerTransaction_IsOneFinalStructuredDecisionAfterEvidence()
    {
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"NONE","presentation":"paragraphs","claims":[{"text":"La pompe HX-42 exige un serrage en croix à 85 N·m","evidenceIds":["E1"]}],"reason":"La preuve répond exactement à la demande."}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle consigne documentée s'applique à la pompe HX-42 ?",
                "pompe HX-42 consigne"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        Assert.Equal(
            "La pompe HX-42 exige un serrage en croix à 85 N·m [E1].",
            result.Answer);
        Assert.Single(llm.Requests);
        var contract = Assert.Single(llm.StructuredOutputContracts);
        Assert.Equal(
            "source_backed_semantic_answer_transaction_v3",
            contract.Name);
        Assert.Equal(new[] { "E1" }, ReadSemanticTransactionEvidenceIdEnum(contract));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["finish_reason"] == "semantic_answer_transaction"
            && trace.Fields["protocol_valid"] == "true");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection");
        Assert.DoesNotContain(llm.StructuredOutputContracts, static contract =>
            contract.Name is "source_backed_fast_evidence_review_v1"
                or "source_backed_single_selection_scope_v1"
                or "source_backed_flat_writer_v1");
    }

    [Fact]
    public async Task SemanticAnswerTransaction_LlmCanAssignSiblingEvidenceFromBoundedPool()
    {
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"NONE","presentation":"bullets","claims":[{"text":"The guard prevents access to the hazard zone","evidenceIds":["E2"]},{"text":"The stop control remains readily accessible to the operator","evidenceIds":["E3"]}],"reason":"Both claims are directly supported by the visible source window."}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Summarize the documented machinery safeguards.",
                "documented machinery safeguards"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        Assert.Equal(new[] { "E2", "E3" }, result.CitedEvidence
            .Select(static item => item.EvidenceId));
        Assert.Equal(
            new[] { "E1", "E2", "E3" },
            ReadSemanticTransactionEvidenceIdEnum(
                Assert.Single(llm.StructuredOutputContracts)));
        Assert.Single(llm.Requests);
    }

    [Fact]
    public async Task SemanticAnswerTransaction_AcceptsGroundedLeadEvidenceForAnswer()
    {
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"E1","presentation":"paragraphs","claims":[{"text":"La pompe HX-42 exige un serrage en croix à 85 N·m","evidenceIds":["E1"]}],"reason":"La preuve répond exactement à la demande."}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle consigne documentée s'applique à la pompe HX-42 ?",
                "pompe HX-42 consigne"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        Assert.Equal(
            "La pompe HX-42 exige un serrage en croix à 85 N·m [E1].",
            result.Answer);
        var trace = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed");
        Assert.Equal("true", trace.Fields["protocol_valid"]);
        Assert.Equal("E1", trace.Fields["lead_evidence_ids"]);
    }

    [Fact]
    public async Task SemanticAnswerTransaction_AcceptsGroundedLeadEvidenceForClarification()
    {
        const string question =
            "Quelle variante du manuel HX-42 souhaitez-vous appliquer ?";
        var llm = new ScriptedAgentLlm(Completion(JsonSerializer.Serialize(new
        {
            requestedDeliverableComplete = false,
            missingUserInputPreventsUniqueResult = true,
            visibleContextEvidenceId = "NONE",
            decision = "clarify",
            answerAdequacy = "user_clarification_required",
            leadEvidenceId = "E1",
            presentation = "paragraphs",
            claims = Array.Empty<object>(),
            reason = question
        })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Applique la consigne HX-42.",
                "pompe HX-42 consigne"),
            CancellationToken.None);

        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Equal(question, clarification.Message);
        Assert.Equal("clarify", result.JudgeDecision.Decision);
        var trace = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed");
        Assert.Equal("true", trace.Fields["protocol_valid"]);
        Assert.Equal("E1", trace.Fields["lead_evidence_ids"]);
    }

    [Fact]
    public async Task SemanticAnswerTransaction_RejectsAnswerLeadNotLinkedToAnyClaim()
    {
        var raw =
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"E2","presentation":"paragraphs","claims":[{"text":"The stop control remains readily accessible to the operator","evidenceIds":["E3"]}],"reason":"The claim is directly supported by the visible evidence."}
            """;
        var llm = new ScriptedAgentLlm(Completion(raw));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Summarize the documented machinery safeguard.",
                "documented machinery safeguard"),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.Answer);
        Assert.Empty(result.CitedEvidence);
        var trace = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed");
        Assert.Equal(
            "semantic_answer_transaction_lead_not_cited",
            trace.Fields["protocol_error"]);
        Assert.Equal(raw, trace.Fields["raw_content"]);
    }

    [Fact]
    public async Task SemanticAnswerTransaction_PromptDefinesGenericDecisionAndLeadSemantics()
    {
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"NONE","presentation":"paragraphs","claims":[{"text":"La pompe HX-42 exige un serrage en croix à 85 N·m","evidenceIds":["E1"]}],"reason":"La preuve répond exactement à la demande."}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle consigne documentée s'applique à la pompe HX-42 ?",
                "pompe HX-42 consigne"),
            CancellationToken.None);

        var system = Assert.Single(llm.Requests)[0].Content ?? string.Empty;
        Assert.Contains("ambiguite utilisateur", system, StringComparison.Ordinal);
        Assert.Contains("information manque", system, StringComparison.Ordinal);
        Assert.Contains("preuve visible precise", system, StringComparison.Ordinal);
        Assert.Contains("answer", system, StringComparison.Ordinal);
        Assert.Contains("claim", system, StringComparison.Ordinal);
        Assert.Contains("leadEvidenceId", system, StringComparison.Ordinal);
        Assert.Contains("NONE", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticAnswerTransaction_ReducesFinalDecisionCallsFromTwoToOne()
    {
        const string claim =
            "La pompe HX-42 exige un serrage en croix à 85 N·m";
        var intake = SemanticTransactionIntake(
            "Quelle consigne documentée s'applique à la pompe HX-42 ?",
            "pompe HX-42 consigne");
        var legacyLlm = new ScriptedAgentLlm(
            FastReview("answer", "E1", "E1", claim),
            SemanticReview(
                "accept",
                "La réponse reprend exactement la preuve visible."));
        var transactionLlm = new ScriptedAgentLlm(Completion(
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"NONE","presentation":"paragraphs","claims":[{"text":"La pompe HX-42 exige un serrage en croix à 85 N·m","evidenceIds":["E1"]}],"reason":"La preuve répond exactement à la demande."}
            """));
        var baseline = Options() with
        {
            SeparateActionAndWriter = true,
            RequireEvidenceSelectionBeforeWriter = true
        };
        var legacyRunner = new SourceBackedAgentV2Runner(
            legacyLlm,
            new ScriptedToolExecutor(SearchResult()),
            baseline);
        var transactionRunner = new SourceBackedAgentV2Runner(
            transactionLlm,
            new ScriptedToolExecutor(SearchResult()),
            baseline with { SemanticAnswerTransactionEnabled = true });

        var legacyResult = await legacyRunner.RunAsync(
            intake,
            CancellationToken.None);
        var transactionResult = await transactionRunner.RunAsync(
            intake,
            CancellationToken.None);

        Assert.True(legacyResult.IsSourceVerified);
        Assert.True(transactionResult.IsSourceVerified);
        Assert.Equal(
            legacyResult.Answer?.TrimEnd('.'),
            transactionResult.Answer?.TrimEnd('.'));
        Assert.Equal(2, legacyLlm.Requests.Count);
        Assert.Single(transactionLlm.Requests);
        Assert.Equal(
            "source_backed_fast_evidence_review_v1",
            Assert.Single(legacyLlm.StructuredOutputContracts).Name);
        Assert.Equal(
            "source_backed_semantic_answer_transaction_v3",
            Assert.Single(transactionLlm.StructuredOutputContracts).Name);
    }

    [Fact]
    public async Task SemanticAnswerTransaction_RejectsEvidenceOutsidePoolWithoutFallback()
    {
        const string raw =
            "{\"requestedDeliverableComplete\":true,\"missingUserInputPreventsUniqueResult\":false,\"visibleContextEvidenceId\":\"NONE\",\"decision\":\"answer\",\"answerAdequacy\":\"requested_information_present\",\"leadEvidenceId\":\"NONE\",\"presentation\":\"paragraphs\",\"claims\":[{\"text\":\"Un fait non autorisé\",\"evidenceIds\":[\"E99\"]}],\"reason\":\"Le modèle a choisi une preuve absente.\"}";
        var llm = new ScriptedAgentLlm(Completion(raw));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle consigne documentée s'applique à la pompe HX-42 ?",
                "pompe HX-42 consigne"),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.Answer);
        Assert.Empty(result.CitedEvidence);
        Assert.Single(llm.Requests);
        var trace = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed");
        Assert.Equal(
            "semantic_answer_transaction_evidence_id_not_allowed",
            trace.Fields["protocol_error"]);
        Assert.Contains("E99", trace.Fields["raw_content"],
            StringComparison.Ordinal);
        Assert.DoesNotContain(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.writer.completed");
    }

    [Theory]
    [InlineData("UNITE_SELECTIONNEE 1 - fait interne")]
    [InlineData("Fait déjà cité [E1]")]
    public async Task SemanticAnswerTransaction_RejectsControlTextOrModelCitation(
        string claimText)
    {
        var raw = JsonSerializer.Serialize(new
        {
            requestedDeliverableComplete = true,
            missingUserInputPreventsUniqueResult = false,
            visibleContextEvidenceId = "NONE",
            decision = "answer",
            answerAdequacy = "requested_information_present",
            leadEvidenceId = "NONE",
            presentation = "paragraphs",
            claims = new[]
            {
                new { text = claimText, evidenceIds = new[] { "E1" } }
            },
            reason = "La preuve visible paraît répondre à la demande."
        });
        var llm = new ScriptedAgentLlm(Completion(raw));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle consigne documentée s'applique à la pompe HX-42 ?",
                "pompe HX-42 consigne"),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.Answer);
        Assert.Single(llm.Requests);
        var trace = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed");
        Assert.StartsWith(
            "semantic_answer_transaction_claim_",
            trace.Fields["protocol_error"],
            StringComparison.Ordinal);
        Assert.Equal(raw, trace.Fields["raw_content"]);
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_PreservesThirdEvidenceFromSameWindowWithinGlobalBudget()
    {
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"E3","presentation":"paragraphs","claims":[{"text":"Route Lac maintient les colis entre 2 et 8 degres pendant 18 heures","evidenceIds":["E3"]}],"reason":"La preuve E3 fournit la route admissible et sa duree."}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ThreeSameWindowEvidenceSearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle route respecte la plage 2-8 degres ?",
                "routes chaine du froid"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        Assert.Equal(new[] { "E3" }, result.CitedEvidence
            .Select(static item => item.EvidenceId));
        Assert.Equal(
            new[] { "E1", "E2", "E3" },
            ReadSemanticTransactionEvidenceIdEnum(
                Assert.Single(llm.StructuredOutputContracts)));
        var trace = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed");
        Assert.Equal("6", trace.Fields["evidence_pool_budget"]);
        Assert.Equal("3", trace.Fields["evidence_pool_eligible_item_count"]);
        Assert.Equal("3", trace.Fields["source_window_item_count"]);
        Assert.Equal("0", trace.Fields["evidence_pool_truncated_item_count"]);
        Assert.Equal(
            "requested_information_present",
            trace.Fields["answer_adequacy"]);
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_RequiresLlmOwnedAdequacyInVersionedContract()
    {
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"NONE","presentation":"paragraphs","claims":[{"text":"La pompe HX-42 exige un serrage en croix a 85 N.m","evidenceIds":["E1"]}],"reason":"La preuve fournit directement la consigne demandee."}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle consigne documentee s'applique a la pompe HX-42 ?",
                "pompe HX-42 consigne"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified, DescribeSemanticTransactionTraces(result));
        var contract = Assert.Single(llm.StructuredOutputContracts);
        Assert.Equal("source_backed_semantic_answer_transaction_v3", contract.Name);
        var schema = contract.Schema;
        Assert.Contains(
            schema.GetProperty("required").EnumerateArray(),
            static item => item.GetString() == "answerAdequacy");
        Assert.Equal(
            new[]
            {
                "requested_information_present",
                "requested_information_missing",
                "user_clarification_required",
                "visible_context_required"
            },
            schema.GetProperty("properties")
                .GetProperty("answerAdequacy")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString()));
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_RejectsAdequacyDecisionMismatchWithDedicatedError()
    {
        const string raw =
            "{\"requestedDeliverableComplete\":true,\"missingUserInputPreventsUniqueResult\":false,\"visibleContextEvidenceId\":\"NONE\",\"decision\":\"answer\",\"answerAdequacy\":\"requested_information_missing\",\"leadEvidenceId\":\"NONE\",\"presentation\":\"paragraphs\",\"claims\":[{\"text\":\"La duree exacte n'est pas fournie\",\"evidenceIds\":[\"E1\"]}],\"reason\":\"Le pool ne contient pas la valeur exacte demandee.\"}";
        var llm = new ScriptedAgentLlm(Completion(raw));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        var result = await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle est la duree exacte d'autonomie ?",
                "duree exacte autonomie"),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Null(result.Answer);
        Assert.Empty(result.CitedEvidence);
        var trace = Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed");
        Assert.Equal(
            "semantic_answer_transaction_adequacy_decision_mismatch",
            trace.Fields["protocol_error"]);
        Assert.Equal(raw, trace.Fields["raw_content"]);
    }

    [Fact]
    public async Task SemanticAnswerTransactionV3_PromptRejectsNegativeMetaAnswerWhenRequestedFactIsMissing()
    {
        var llm = new ScriptedAgentLlm(Completion(
            """
            {"requestedDeliverableComplete":true,"missingUserInputPreventsUniqueResult":false,"visibleContextEvidenceId":"NONE","decision":"answer","answerAdequacy":"requested_information_present","leadEvidenceId":"NONE","presentation":"paragraphs","claims":[{"text":"La pompe HX-42 exige un serrage en croix a 85 N.m","evidenceIds":["E1"]}],"reason":"La preuve fournit directement la consigne demandee."}
            """));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResult()),
            Options() with
            {
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true,
                SemanticAnswerTransactionEnabled = true
            });

        await runner.RunAsync(
            SemanticTransactionIntake(
                "Quelle consigne documentee s'applique a la pompe HX-42 ?",
                "pompe HX-42 consigne"),
            CancellationToken.None);

        var system = Assert.Single(llm.Requests)[0].Content ?? string.Empty;
        Assert.Contains("answerAdequacy", system, StringComparison.Ordinal);
        Assert.Contains("constat d'absence", system, StringComparison.Ordinal);
        Assert.Contains("requested_information_missing", system,
            StringComparison.Ordinal);
        Assert.Contains("decision=research", system, StringComparison.Ordinal);
    }

    private static string[] ReadSemanticTransactionEvidenceIdEnum(
        LlmStructuredOutputContract contract)
        => contract.Schema
            .GetProperty("properties")
            .GetProperty("claims")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("evidenceIds")
            .GetProperty("items")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();

    private static ToolResults ThreeSameWindowEvidenceSearchResult()
        => Results(
            "rag.search",
            new
            {
                hits = new object[]
                {
                    new
                    {
                        docId = "cold-chain-routes",
                        docName = "Cold Chain Routes.pdf",
                        docPath = "Logistics/Cold Chain Routes.pdf",
                        pageStart = 12,
                        pageEnd = 12,
                        chunkId = "cold-chain:route-directe",
                        excerpt =
                            "Route Directe dure 12 heures et maintient 15 a 20 degres.",
                        score = 0.97
                    },
                    new
                    {
                        docId = "cold-chain-routes",
                        docName = "Cold Chain Routes.pdf",
                        docPath = "Logistics/Cold Chain Routes.pdf",
                        pageStart = 12,
                        pageEnd = 12,
                        chunkId = "cold-chain:route-nord",
                        excerpt =
                            "Route Nord dure 24 heures et maintient 2 a 8 degres.",
                        score = 0.95
                    },
                    new
                    {
                        docId = "cold-chain-routes",
                        docName = "Cold Chain Routes.pdf",
                        docPath = "Logistics/Cold Chain Routes.pdf",
                        pageStart = 12,
                        pageEnd = 12,
                        chunkId = "cold-chain:route-lac",
                        excerpt =
                            "Route Lac dure 18 heures et maintient 2 a 8 degres.",
                        score = 0.93
                    }
                }
            });

    private static SourceBackedIntake SemanticTransactionIntake(
        string question,
        string query)
    {
        var routerAction = Call("router-semantic-transaction", "rag_search", new
        {
            query,
            topK = 5
        });
        return Intake(question) with
        {
            QuestionFocus = "content",
            InitialToolCalls = new[]
            {
                new SourceBackedInitialToolCall(
                    routerAction.Id,
                    routerAction.Name,
                    routerAction.Arguments,
                    "llm_router")
            },
            InitialSemanticMission = new SourceBackedInitialSemanticMission(
                JsonSerializer.SerializeToElement(new
                {
                    deliverable = "une réponse documentaire exacte",
                    structuredLayout = false,
                    rowCount = 1,
                    columnCount = 1,
                    atomicEvidenceCount = 1,
                    atomicEvidenceType = "claim documentaire",
                    answerUnitType = "claim documentaire",
                    answerUnitMode = "content_claim",
                    initialCapability = "rag_search",
                    rowHeader = "",
                    rowLabels = Array.Empty<string>(),
                    columns = Array.Empty<string>()
                }),
                "llm_router")
        };
    }

    private static string DescribeSemanticTransactionTraces(
        SourceBackedPipelineResult result)
        => string.Join(
            Environment.NewLine,
            result.TraceEvents.Select(trace =>
                trace.EventName + " | " + string.Join(
                    ";",
                    trace.Fields.Select(static field =>
                        field.Key + "=" + field.Value))));
}
