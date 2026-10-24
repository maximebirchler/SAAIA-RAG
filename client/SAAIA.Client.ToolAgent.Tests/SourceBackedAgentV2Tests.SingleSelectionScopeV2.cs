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
    public async Task SingleSelectionScopeV2_AcceptsOneUsefulTechnicalProcedureWhenSeveralCandidatesMatch()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReviewWithReason(
                "accept",
                "E1",
                "",
                "Une seule procedure conforme suffit et E1 respecte toutes les contraintes visibles.",
                ("E1", "match"),
                ("E2", "match")),
            Completion(
                "Coupez l'alimentation pendant 10 secondes puis relancez le capteur [E1]."),
            SemanticReview("accept", "E1 décrit la coupure de dix secondes et le redémarrage."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionTechnicalProceduresResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Donne-moi une procedure documentee pour relancer le capteur.",
                "une procedure technique exploitable"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Null(result.Clarification);
        Assert.Equal(4, llm.Requests.Count);
        Assert.Equal(
            "source_backed_single_selection_scope_review_v2",
            llm.StructuredOutputContracts[1].Name);
        Assert.True(
            llm.StructuredOutputContracts[1].Schema
                .GetProperty("properties")
                .TryGetProperty("reason", out _));
        var scopePrompt = RequestText(llm, 1);
        Assert.Contains(
            "simple pluralite",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "information que seul",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "l'utilisateur peut fournir",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Une preference facultative",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "n'est pas une information manquante",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "decision=accept seulement si exactement un candidat est match",
            scopePrompt,
            StringComparison.Ordinal);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_decision"] == "accept"
            && trace.Fields["single_selection_scope_basis"]
                == "multiple_matching_candidates_non_blocking"
            && trace.Fields["single_selection_scope_reason"].Contains(
                "Une seule procedure conforme suffit",
                StringComparison.Ordinal)
            && trace.Fields["single_selection_scope_normalized"] == "false"
            && trace.Fields["single_selection_scope_protocol_valid"] == "true");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_AcceptsAVisibleNonFirstMatchWithoutOrderPreference()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E2",
                "E2"),
            SingleSelectionScopeReviewWithReason(
                "accept",
                "E2",
                "",
                "E2 est visible, correspond a la demande et un seul exemple suffit.",
                ("E1", "match"),
                ("E2", "match")),
            Completion(
                "Maintenez les deux commandes pendant 5 secondes [E2]."),
            SemanticReview("accept", "E2 décrit le maintien des deux commandes pendant cinq secondes."));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionTechnicalProceduresResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Donne-moi un exemple de procedure documentee pour relancer le capteur.",
                "un exemple de procedure technique"),
            CancellationToken.None);

        Assert.True(result.IsSourceVerified);
        Assert.Null(result.Clarification);
        Assert.Equal("E2", Assert.Single(result.CitedEvidence).EvidenceId);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_decision"] == "accept"
            && trace.Fields["single_selection_scope_basis"]
                == "multiple_matching_candidates_non_blocking"
            && trace.Fields["single_selection_scope_normalized"] == "false");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_ClarifiesWhenMissingRegionChangesTheCorrectComplianceLabel()
    {
        const string question =
            "Dans quelle region le produit sera-t-il utilise : Region Nord ou Region Sud ?";
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReviewWithReason(
                "clarify",
                "NONE",
                question,
                "La region d'utilisation determine laquelle des deux etiquettes est correcte.",
                ("E1", "match"),
                ("E2", "match")));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionComplianceLabelsResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Quelle etiquette de conformite dois-je appliquer au produit ?",
                "une etiquette correcte pour le contexte d'utilisation"),
            CancellationToken.None);

        var clarification = Assert.IsType<SourceBackedClarificationDecision>(
            result.Clarification);
        Assert.Equal(question, clarification.Message);
        Assert.False(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_decision"] == "clarify"
            && trace.Fields["single_selection_scope_basis"]
                == "multiple_matching_candidates_blocking"
            && trace.Fields["single_selection_scope_reason"].Contains(
                "region d'utilisation determine",
                StringComparison.Ordinal)
            && trace.Fields["single_selection_scope_normalized"] == "false"
            && trace.Fields["single_selection_scope_protocol_valid"] == "true");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_RejectsClarifyThatCarriesASelectedCandidate()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReviewWithReason(
                "clarify",
                "E1",
                "Quelle region faut-il appliquer ?",
                "La region manque.",
                ("E1", "match"),
                ("E2", "match")));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionComplianceLabelsResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Quelle etiquette de conformite dois-je appliquer au produit ?",
                "une etiquette correcte pour le contexte d'utilisation"),
            CancellationToken.None);

        Assert.Null(result.Clarification);
        Assert.False(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_basis"]
                == "invalid_scope_review"
            && trace.Fields["single_selection_scope_protocol_valid"] == "false"
            && trace.Fields["single_selection_scope_normalized"] == "false");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_RejectsAcceptWhenSelectedCandidateIsNotAMatch()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReviewWithReason(
                "accept",
                "E1",
                "",
                "Le candidat serait selectionne.",
                ("E1", "nonmatch"),
                ("E2", "match")));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionTechnicalProceduresResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Donne-moi une procedure documentee pour relancer le capteur.",
                "une procedure technique exploitable"),
            CancellationToken.None);

        Assert.Null(result.Clarification);
        Assert.False(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_basis"]
                == "invalid_scope_review"
            && trace.Fields["single_selection_scope_protocol_valid"] == "false");
    }

    [Fact]
    public async Task SingleSelectionScopeV2_RejectsIncompleteCandidateClassification()
    {
        var llm = new ScriptedAgentLlm(
            FastReview(
                "writer",
                "E1",
                "E1"),
            SingleSelectionScopeReviewWithReason(
                "accept",
                "E1",
                "",
                "E1 correspond a la demande.",
                ("E1", "match")));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(SingleSelectionTechnicalProceduresResult()),
            Options() with
            {
                MaximumTurns = 1,
                SeparateActionAndWriter = true,
                RequireEvidenceSelectionBeforeWriter = true
            });

        var result = await runner.RunAsync(
            SingleSelectionScopeIntake(
                "Donne-moi une procedure documentee pour relancer le capteur.",
                "une procedure technique exploitable"),
            CancellationToken.None);

        Assert.Null(result.Clarification);
        Assert.False(result.IsSourceVerified);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains(result.TraceEvents, static trace =>
            trace.EventName
                == "source_backed_agent_v2.fast_evidence_review.completed"
            && trace.Fields["single_selection_scope_basis"]
                == "invalid_scope_review"
            && trace.Fields["single_selection_scope_protocol_valid"] == "false");
    }
}
