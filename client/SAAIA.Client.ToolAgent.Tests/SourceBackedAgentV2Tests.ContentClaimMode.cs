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
    public async Task ContentClaimMode_UsesFlatAdequacyAndKeepsDistinctSamePageClaims()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "ANSI B11.0-2023 exigences",
            topK = 5
        });
        var intake = Intake("Donne deux exigences documentées de ANSI B11.0-2023.") with
        {
            QuestionFocus = "content",
            RequestedDocumentName =
                "ANSI B11.0-2023 - Safety of Machinery.pdf",
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
                    deliverable = "deux exigences documentées de ANSI B11.0-2023",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason =
                        "Les deux exigences distinctes couvrent exactement la demande."
                })),
            Completion(
                "Le protecteur doit empêcher l'accès à la zone dangereuse [E2]. "
                + "L'arrêt doit être accessible à l'opérateur [E3]."),
            SemanticReview(
                "accept",
                "Les deux exigences distinctes sont directement soutenues par E2 et E3."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(DocumentIdentityAndContentSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(
            new[] { "E2", "E3" },
            SourceContractVerifier.ExtractEvidenceIds(result.Answer));
        var adequacyIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool =>
                tool.Name == "submit_flat_evidence_selection"));
        Assert.True(adequacyIndex >= 0);
        Assert.Contains("E1 | B11.0-2023", RequestText(llm, adequacyIndex));
        Assert.Contains(
            "E2 | The guard shall prevent access to the hazard zone",
            RequestText(llm, adequacyIndex));
        Assert.Contains(
            "E3 | The stop control shall be readily accessible to the operator",
            RequestText(llm, adequacyIndex));
        Assert.Contains(
            "fusion silencieuse",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "plusieurs documents",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "STRUCTURE DU LIVRABLE",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "n'a pas besoin d'exister deja",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "DOCUMENT EXPLICITEMENT NOMME",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ANSI B11.0-2023",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "submit_flat_evidence_context_gap",
            RequestText(llm, adequacyIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            llm.ToolSets.SelectMany(static tools => tools),
            static tool => tool.Name == "submit_resolved_candidate_audit");
        Assert.DoesNotContain(
            result.TraceEvents,
            static trace => trace.EventName
                            == "source_backed_agent_v2.candidate_audit.completed");
        var semanticReviewIndex = llm.ToolSets.FindIndex(static toolSet =>
            toolSet.Any(static tool => tool.Name == "submit_semantic_review"));
        Assert.True(semanticReviewIndex >= 0);
        Assert.Contains(
            "MODE CONTENT_CLAIM",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "une instruction",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ou une action est une preuve valide",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "fusion silencieuse",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "verbe, relation, negation, ordre et portee",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Une valeur doit etre une instance nommee",
            RequestText(llm, semanticReviewIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.action.semantic_selection_accepted"
            && trace.Fields["selected_evidence"] == "E2,E3");
        var writerIndex = adequacyIndex + 1;
        Assert.Empty(llm.ToolSets[writerIndex]);
        Assert.Contains(
            "[E2]",
            RequestText(llm, writerIndex),
            StringComparison.Ordinal);
        Assert.Contains(
            "[E3]",
            RequestText(llm, writerIndex),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentClaimMode_TreatsSelectedEvidenceAsAProofPoolForMultipleClaims()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "safety requirements",
            topK = 4
        });
        var intake = Intake("Donne deux exigences documentées de Safety Standard.") with
        {
            QuestionFocus = "content",
            RequestedDocumentName = "Safety Standard.pdf",
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
                    deliverable = "deux exigences documentées de Safety Standard",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        const string answer =
            "Le protecteur doit empêcher l'accès à la zone dangereuse [E1]. "
            + "L'arrêt doit rester accessible à l'opérateur [E1].";
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-proof-pool",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1" },
                    reason =
                        "La fenêtre contient directement les deux exigences demandées."
                })),
            Completion(answer),
            SemanticReview(
                "accept",
                "Les deux claims sont distincts et directement soutenus par E1."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleEvidenceWithTwoClaimsSearchResult()),
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(answer, result.Answer);
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                result.Answer!,
                @"\[E1\]").Count);
        var writerIndex = llm.ToolSets.FindIndex(static tools => tools.Count == 0);
        Assert.True(writerIndex >= 0);
        Assert.Contains(
            "POOL DE PREUVES CONTENT_CLAIM",
            RequestText(llm, writerIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Un meme EvidenceId",
            RequestText(llm, writerIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "plusieurs claims distincts",
            RequestText(llm, writerIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Cite chaque EvidenceId exactement une fois",
            RequestText(llm, writerIndex),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName == "source_backed_agent_v2.semantic_review.completed"
            && trace.Fields["decision"] == "accept");
    }

    [Fact]
    public async Task ContentClaimMode_RevisesTheDraftDirectlyWhenEvidenceRemainsValid()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "ANSI B11.0-2023 exigences",
            topK = 5
        });
        var intake = Intake("Donne deux exigences documentées de ANSI B11.0-2023.") with
        {
            QuestionFocus = "content",
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
                    deliverable = "deux exigences documentées de ANSI B11.0-2023",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-adequacy",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E2", "E3" },
                    reason = "Les deux exigences couvrent la demande."
                })),
            Completion(
                "Le protecteur réduit l'accès à la zone dangereuse [E2]. "
                + "L'arrêt est disponible pour l'opérateur [E3]."),
            SemanticReview(
                "revise",
                "Les preuves restent valides, mais les verbes doivent reprendre leur portée exacte.",
                preferredAlternativeEvidenceIds: new[] { "E1" }),
            Completion(
                "Le protecteur doit empêcher l'accès à la zone dangereuse [E2]. "
                + "La commande d'arrêt doit être facilement accessible à l'opérateur [E3]."),
            SemanticReview(
                "accept",
                "Chaque exigence est maintenant formulée fidèlement et citée séparément."));
        var executor = new ScriptedToolExecutor(
            DocumentIdentityAndContentSearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Equal(new[] { "E2", "E3" }, result.CitedEvidence
            .Select(static evidence => evidence.EvidenceId));
        Assert.Equal(new[] { "rag.search" }, executor.ToolNames);
        Assert.Equal(5, llm.ToolSets.Count);
        Assert.Empty(llm.ToolSets[3]);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.content_claim_revision.returned_to_writer"
            && trace.Fields["selected_evidence"] == "E2,E3");
    }

    [Fact]
    public async Task ContentClaimMode_ReturnsControlToResearchAfterOneFailedDirectRevision()
    {
        var initialSearch = Call("router-search", "rag_search", new
        {
            query = "safety requirements",
            topK = 4
        });
        var intake = Intake("Donne deux exigences documentées de Safety Standard.") with
        {
            QuestionFocus = "content",
            RequestedDocumentName = "Safety Standard.pdf",
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
                    deliverable = "deux exigences documentées de Safety Standard",
                    atomicEvidenceCount = 2,
                    atomicEvidenceType = "exigence documentée",
                    atomicEvidenceMode = "content_claim",
                    selectionPolicy = "explicit_set",
                    initialCapability = "rag_search"
                }),
                "llm_router")
        };
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "content-claim-proof-pool",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[] { "E1" },
                    reason = "La fenêtre paraît couvrir les deux exigences."
                })),
            Completion(
                "Le protecteur réduit l'accès à la zone dangereuse [E1]."),
            SemanticReview(
                "revise",
                "Le second claim doit être rédigé à partir de la preuve visible."),
            Completion(
                "Le protecteur empêche l'accès à la zone dangereuse [E1]."),
            SemanticReview(
                "revise",
                "Le pool visible reste trop limité pour couvrir le second claim demandé."),
            Completion(Call(
                "search-inside-visible-document",
                "refine_focused_document_search",
                new
                {
                    documentFocusEvidenceId = "E1",
                    query = "operator stop control requirement",
                    limit = 4
                })));
        var executor = new ScriptedToolExecutor(
            SingleEvidenceWithTwoClaimsSearchResult(),
            SingleEvidenceWithTwoClaimsSearchResult());
        var runner = new SourceBackedAgentV2Runner(
            llm,
            executor,
            Options() with
            {
                MaximumTurns = 4,
                SeparateActionAndWriter = true,
                SemanticCandidateAuditEnabled = true,
                StructuredSemanticPlanningEnabled = true
            });

        var result = await runner.RunAsync(intake, CancellationToken.None);

        Assert.False(result.IsSourceVerified);
        Assert.Equal(new[] { "rag.search", "rag.search" }, executor.ToolNames);
        Assert.Single(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.content_claim_revision.returned_to_writer");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.content_claim_revision.budget_returned_to_orchestrator"
            && trace.Fields["selected_evidence"] == "E1"
            && trace.Fields["direct_revision_budget"] == "1");
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.research_transition.accepted"
            && trace.Fields["decision"] == "search_focused_document"
            && trace.Fields["document_focus_evidence_id"] == "E1");
    }
}
