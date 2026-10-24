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
    public async Task StructuredInventory_LetsLlmAuditEveryCandidateBeforeFinalSelection()
    {
        var cards = TwentyMealCards()
            .Concat(new[]
            {
                new MealCard("INGREDIENTS PREPARATION", 21),
                new MealCard("Servir cette recette", 22),
                new MealCard("Astuce Realiser la recette", 23),
                new MealCard("Pliez afin de terminer", 24)
            })
            .ToArray();
        var approvedIds = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var rejectedIds = Enumerable.Range(21, 4)
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
                limit = 24
            })),
            Completion(CandidateAuditCall(
                "candidate-audit",
                approvedIds,
                rejectedIds)),
            Completion(SelectionCall("selection", approvedIds)),
            SemanticReview(
                "accept",
                "Les vingt recettes retenues sont completes, distinctes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                MaximumSemanticCandidatesPerAuditBatch = 40,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var auditRequestIndex = Enumerable.Range(0, llm.Requests.Count)
            .Where(index => RequestText(llm, index).Contains(
                "CANDIDATS A JUGER DANS CE LOT:",
                StringComparison.Ordinal))
            .Single();
        Assert.Empty(llm.ToolSets[auditRequestIndex]);
        Assert.Contains(llm.StructuredOutputContracts, static contract =>
            contract.Name == "source_backed_candidate_batch_audit_v5");
        Assert.Contains("c24 [E24]", RequestText(llm, auditRequestIndex));
        var selectionIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool => tool.Name == "submit_evidence_selection"));
        Assert.True(selectionIndex >= 0);
        Assert.DoesNotContain("E21 |", RequestText(llm, selectionIndex));
        Assert.DoesNotContain(
            "\"evidenceId\":\"E21\"",
            RequestText(llm, selectionIndex));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["candidates"] == "24"
            && trace.Fields["approved"] == "20"
            && trace.Fields["rejected"] == "4"
            && trace.Fields["rejected_evidence_ids"] == "E21,E22,E23,E24");
    }

    [Fact]
    public async Task StructuredInventory_RejectsInvalidIndependentCandidateProtocol()
    {
        var cards = TwentyMealCards()
            .Concat(new[]
            {
                new MealCard("Recette documentee 21", 21),
                new MealCard("Recette documentee 22", 22),
                new MealCard("Recette documentee 23", 23),
                new MealCard("Recette documentee 24", 24)
            })
            .ToArray();
        var partiallyApprovedIds = Enumerable.Range(1, 20)
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
                limit = 24
            })),
            Completion("invalid independent decision"),
            Completion("still invalid independent decision"));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 3,
                MaximumSemanticCorrectionTurns = 0,
                MaximumWorkingEvidenceItems = 40,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "false"
            && trace.Fields["approved"] == "0"
            && trace.Fields["rejected"] == "0"
            && trace.Fields["llm_calls"] == "2"
            && trace.Fields["withheld"] == "24"
            && trace.Fields["failure_reason"]
                == "candidate_batch_audit_structured_object_required");
        Assert.DoesNotContain(
            llm.ToolSets.SelectMany(static toolSet => toolSet),
            static tool => tool.Name == "submit_evidence_selection");
    }

    [Fact]
    public async Task StructuredInventory_RecompactsCollectionDecisionAfterContextOverflow()
    {
        var cards = TwentyMealCards()
            .Concat(new[]
            {
                new MealCard("Recette documentee 21", 21),
                new MealCard("Recette documentee 22", 22),
                new MealCard("Recette documentee 23", 23),
                new MealCard("Recette documentee 24", 24)
            })
            .ToArray();
        var approvedIds = Enumerable.Range(1, 24)
            .Select(static index => $"E{index}")
            .ToArray();
        var selectedIds = approvedIds.Take(20).ToArray();
        var llm = new ContextOverflowOnceLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 24
            })),
            new HttpRequestException(
                "LLM request failed: 400 Bad Request. Body: request (4300 tokens) exceeds the available context size (4096 tokens)",
                null,
                System.Net.HttpStatusCode.BadRequest),
            Completion(CandidateAuditCall(
                "candidate-audit-compact-global",
                approvedIds,
                Array.Empty<string>())),
            Completion(SelectionCall("selection", selectedIds)),
            SemanticReview(
                "accept",
                "Les vingt recettes selectionnees sont completes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                MaximumSemanticCandidatesPerAuditBatch = 40,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                MaximumContextTokens = 4096
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var e1AuditPrompts = Enumerable.Range(0, llm.Requests.Count)
            .Select(index => llm.Requests[index]
                .Select(static message => message.Content ?? string.Empty)
                .LastOrDefault(static text => text.Contains(
                    "CANDIDATS A JUGER DANS CE LOT:",
                    StringComparison.Ordinal))
                ?? string.Empty)
            .Where(static text => text.Contains(
                "[E1]",
                StringComparison.Ordinal))
            .ToArray();
        Assert.InRange(e1AuditPrompts.Length, 2, 4);
        var retriedE1Prompt = e1AuditPrompts[^1];
        Assert.DoesNotContain(
            " | fichier=",
            retriedE1Prompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["mode"] == "batched_semantic_decision"
            && trace.Fields["llm_calls"] == "1");
    }

    [Fact]
    public async Task StructuredInventory_AuditsAPaginationPageWithoutPadding()
    {
        var approvedIds = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: planning\nDIMENSIONS: 5 x 4\n"
                + "PREUVES_ATOMIQUES: 20 recettes nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: recettes\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 20 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 20 recettes"),
            Completion(Call("cards-page", "documents_content_cards", new
            {
                categoryPath = "Cuisine",
                limit = 20,
                offset = 40
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-page",
                approvedIds,
                Array.Empty<string>())),
            Completion(SelectionCall("selection", approvedIds)),
            SemanticReview(
                "accept",
                "Les vingt recettes de la page sont completes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(TwentyMealCards())),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                MaximumSemanticCandidatesPerAuditBatch = 20,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            1,
            Enumerable.Range(0, llm.Requests.Count).Count(index =>
                RequestText(llm, index).Contains(
                    "CANDIDATS A JUGER DANS CE LOT:",
                    StringComparison.Ordinal)));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["candidates"] == "20"
            && trace.Fields["approved"] == "20");
    }

    [Fact]
    public async Task StructuredInventory_AuditsTheGlobalCollectionThroughIndependentDecisions()
    {
        var cards = TwentyMealCards()
            .Concat(Enumerable.Range(21, 20)
                .Select(static index => new MealCard(
                    "Libelle candidat " + index,
                    index)))
            .ToArray();
        var finalApprovedIds = Enumerable.Range(1, 20)
            .Select(static index => $"E{index}")
            .ToArray();
        var rejectedIds = Enumerable.Range(21, 20)
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
                limit = 40
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-global",
                finalApprovedIds,
                rejectedIds)),
            Completion(SelectionCall("selection", finalApprovedIds)),
            SemanticReview(
                "accept",
                "La reconciliation globale a conserve vingt recettes distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 40,
                MaximumSemanticCandidatesPerAuditTurn = 40,
                MaximumSemanticCandidatesPerAuditBatch = 40,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var e40Prompt = Enumerable.Range(0, llm.Requests.Count)
            .Select(index => RequestText(llm, index))
            .Single(static text => text.Contains(
                "[E40]",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            "reconciliateur semantique global",
            e40Prompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["protocol_valid"] == "true"
            && trace.Fields["mode"] == "batched_semantic_decision"
            && trace.Fields["llm_calls"] == "1"
            && trace.Fields["candidates"] == "40"
            && trace.Fields["approved"] == "20"
            && trace.Fields["rejected"] == "20");
    }

    [Fact]
    public async Task StructuredInventory_PagesCandidateAuditWithoutLosingTheRemainder()
    {
        var cards = Enumerable.Range(1, 80)
            .Select(static index => new MealCard(
                "Recette documentee " + index,
                index))
            .ToArray();
        var firstApprovedIds = Enumerable.Range(1, 12)
            .Select(static index => $"E{index}")
            .ToArray();
        var firstRejectedIds = Enumerable.Range(13, 60)
            .Select(static index => $"E{index}")
            .ToArray();
        var secondApprovedIds = Enumerable.Range(73, 8)
            .Select(static index => $"E{index}")
            .ToArray();
        var finalSelection = firstApprovedIds
            .Concat(secondApprovedIds)
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
                limit = 80
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-page-1",
                firstApprovedIds,
                firstRejectedIds)),
            Completion(CandidateAuditCall(
                "candidate-audit-page-2",
                secondApprovedIds,
                Array.Empty<string>())),
            Completion(SelectionCall("selection", finalSelection)),
            SemanticReview(
                "accept",
                "Les vingt recettes retenues sont completes, distinctes et sourcees."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(cards)),
            Options() with
            {
                MaximumTurns = 4,
                MaximumContextTokens = 8192,
                MaximumWorkingEvidenceItems = 80,
                MaximumSemanticCandidatesPerAuditTurn = 72,
                MaximumSemanticCandidatesPerAuditBatch = 72,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne vingt recettes documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var auditPrompts = Enumerable.Range(0, llm.Requests.Count)
            .Select(index => RequestText(llm, index))
            .Where(static text => text.Contains(
                "CANDIDATS A JUGER DANS CE LOT:",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(auditPrompts, static text => text.Contains(
            "[E72]",
            StringComparison.Ordinal));
        Assert.Contains(auditPrompts, static text => text.Contains(
            "[E73]",
            StringComparison.Ordinal));
        Assert.Contains(auditPrompts, static text => text.Contains(
            "[E80]",
            StringComparison.Ordinal));
        var auditTraces = result.TraceEvents
            .Where(static trace => trace.EventName
                == "source_backed_agent_v2.candidate_audit.completed")
            .ToArray();
        Assert.Collection(
            auditTraces,
            first =>
            {
                Assert.Equal("72", first.Fields["candidates"]);
                Assert.Equal("80", first.Fields["pending_before_audit"]);
                Assert.Equal("12", first.Fields["approved_sources"]);
            },
            second =>
            {
                Assert.Equal("8", second.Fields["candidates"]);
                Assert.Equal("8", second.Fields["pending_before_audit"]);
                Assert.Equal("20", second.Fields["approved_sources"]);
            });
    }
}
