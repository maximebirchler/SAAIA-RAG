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
    public async Task StructuredCandidateAudit_TellsTheOrchestratorWhenNoNewCandidateWasApproved()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: grille sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("cards-no-yield", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 1,
                offset = 0
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-no-yield",
                Array.Empty<string>(),
                new[] { "E1" })),
            Completion(Call("cards-pivot", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 2,
                offset = 1
            })),
            Completion(CandidateAuditCall(
                "candidate-audit-pivot",
                new[] { "E2", "E3" },
                Array.Empty<string>())),
            Completion(Call(
                "selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "Les deux valeurs retenues sont completes et distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(
                ContentCardInventoryResult(new[]
                {
                    new MealCard("INGREDIENTS PREPARATION", 1)
                }),
                ContentCardInventoryResult(new[]
                {
                    new MealCard("Option documentee A", 2),
                    new MealCard("Option documentee B", 3)
                })),
            Options() with
            {
                MaximumTurns = 6,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de deux options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains(
            "0 sources distinctes approuvees sur 2",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "poursuis librement la collecte",
            RequestText(llm, 3),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StructuredCandidateAudit_ClassifiesEveryCandidateAndRetainsOnlyNamedSourceItems()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: reponse sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("cards", "documents_content_cards", new
            {
                categoryPath = "Knowledge",
                limit = 6
            })),
            Completion(CandidateAuditCall(
                "candidate-audit",
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["E1"] = 0,
                    ["E2"] = 0,
                    ["E3"] = 1,
                    ["E4"] = 0,
                    ["E5"] = 0,
                    ["E6"] = 1
                })),
            Completion(Call(
                "selection",
                "submit_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E3", "E6" },
                    layout = new
                    {
                        rowHeader = "Ligne",
                        columns = new[] { "Option" },
                        rows = new[] { "A", "B" }
                    }
                })),
            SemanticReview(
                "accept",
                "L'instance nommee est directement soutenue."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard("DEJEUNER", 1),
                new MealCard("Petit-dejeuner (4h-5h)", 2),
                new MealCard("Boeuf bourguignon", 3),
                new MealCard("Planification des repas", 4),
                new MealCard("Quantite de farine", 5),
                new MealCard("Tarte aux pommes", 6)
            })),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Donne une option documentee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "E3", "E6" },
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        Assert.Contains("Boeuf bourguignon [E3]", result.Answer);
        Assert.Contains("Tarte aux pommes [E6]", result.Answer);
        Assert.DoesNotContain("DEJEUNER [E1]", result.Answer);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["approved_evidence_ids"] == "E3,E6"
            && trace.Fields["rejected_evidence_ids"] == "E1,E2,E4,E5"
            && trace.Fields["audit_semantic_decision_counts"].Contains(
                "named_source_item=2",
                StringComparison.Ordinal)
            && trace.Fields["audit_semantic_decision_counts"].Contains(
                "other_wrong_type=4",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task StructuredCandidateAudit_SuppressesAnEvidenceItemWithoutRenderableTextBeforeLlmJudgment()
    {
        var llm = new ScriptedAgentLlm(
            Completion(
                "LIVRABLE: grille sourcee\nDIMENSIONS: 2 x 1\n"
                + "PREUVES_ATOMIQUES: 2 instances nommees et sourcees\n"
                + "INTENTIONS_RECHERCHE: instances\nAPPROCHE_OUTILS: choix libre\n"
                + "ACCEPTER_SI: 2 cellules\nINSUFFISANT_SEULEMENT_SI: moins de 2 instances"),
            Completion(Call("search", "rag_search", new
            {
                query = "documented options"
            })),
            Completion(CandidateAuditCall(
                "candidate-audit",
                new[] { "E1", "E3" },
                Array.Empty<string>())),
            Completion(Call(
                "selection",
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
                "Les deux valeurs retenues sont completes et distinctes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SearchResultWithAnEmptyEvidenceItem()),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 12,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis une grille de deux options documentees."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        var auditPrompt = Enumerable.Range(0, llm.Requests.Count)
            .Select(index => RequestText(llm, index))
            .Single(static text => text.Contains(
                "CANDIDATS A JUGER DANS CE LOT:",
                StringComparison.Ordinal))
            ;
        Assert.Contains("[E1]", auditPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[E2]", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("[E3]", auditPrompt, StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
            == "source_backed_agent_v2.evidence.non_renderable_suppressed"
            && trace.Fields["evidence_ids"] == "E2"
            && trace.Fields["decision_source"] == "mechanical_contract");
    }

    [Fact]
    public async Task StructuredCandidateAudit_PreservesTheBackendCanonicalTitleWithoutRelabeling()
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
                limit = 2
            })),
            Completion(CandidateAuditCall(
                "candidate-audit",
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["E1"] = 1,
                    ["E2"] = 1
                })),
            Completion(Call(
                "selection",
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
            SemanticReview(
                "accept",
                "Les deux ancres canoniques nomment des objets distincts."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(ContentCardInventoryResult(new[]
            {
                new MealCard(
                    "Tartiflette",
                    1,
                    "Plats > Tartiflette"),
                new MealCard(
                    "Gratin dauphinois",
                    2,
                    "Plats > Gratin dauphinois")
            })),
            Options() with
            {
                MaximumTurns = 4,
                MaximumWorkingEvidenceItems = 8,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true
            });

        var result = await runner.RunAsync(
            Intake("Construis la grille sourcee demandee."),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Contains("Tartiflette [E1]", result.Answer);
        Assert.Contains("Gratin dauphinois [E2]", result.Answer);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["approved_display_values"]
                .Contains("E1=Tartiflette", StringComparison.Ordinal)
            && trace.Fields["approved_display_values"]
                .Contains("E2=Gratin dauphinois", StringComparison.Ordinal));
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.candidate_audit.completed"
            && trace.Fields["label_review_llm_calls"] == "0");
    }
}
