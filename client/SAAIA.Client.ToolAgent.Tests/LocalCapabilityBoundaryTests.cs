using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalCapabilityBoundaryTests
{
    [Theory]
    [InlineData(5, 4, 20, true)]
    [InlineData(2, 4, 8, true)]
    [InlineData(2, 3, 6, true)]
    [InlineData(1, 5, 5, false)]
    [InlineData(1, 7, 7, true)]
    public void Structured_layout_uses_the_largest_declared_answer_unit_count(
        int rows,
        int columns,
        int atomicEvidenceCount,
        bool expectedAdvanced)
    {
        var plan = BuildPlan("structured_layout", atomicEvidenceCount, rows, columns);

        var decision = ToolAgentOrchestrator.EvaluateLocalCapabilityBoundaryForTests(plan);

        Assert.Equal(expectedAdvanced, decision.RequiresAdvancedAnalysis);
        Assert.Equal(Math.Max(atomicEvidenceCount, rows * columns), decision.AnswerUnitCount);
        Assert.Equal("structured_layout", decision.PlanKind);
    }

    [Fact]
    public void Structured_layout_boundary_reports_the_effective_six_unit_threshold()
    {
        var decision = ToolAgentOrchestrator.EvaluateLocalCapabilityBoundaryForTests(
            BuildPlan("structured_layout", 6, rows: 2, columns: 3));

        Assert.True(decision.RequiresAdvancedAnalysis);
        Assert.Equal("structured_answer_units_at_or_above_6", decision.ReasonCode);
        Assert.Equal(6, decision.AnswerUnitCount);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(4, false)]
    public void Multi_item_boundary_is_five_answer_units(int answerUnits, bool expectedAdvanced)
    {
        var decision = ToolAgentOrchestrator.EvaluateLocalCapabilityBoundaryForTests(
            BuildPlan("multi_item", answerUnits));

        Assert.Equal(expectedAdvanced, decision.RequiresAdvancedAnalysis);
        Assert.Equal(answerUnits, decision.AnswerUnitCount);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public void Bounded_named_document_extraction_avoids_count_only_handoff(
        int answerUnits,
        bool expectedAdvanced)
    {
        var plan = BuildPlan("multi_item", answerUnits);
        plan.SourceBackedMission!.RequestedDocumentName = "named-document.pdf";

        var decision = ToolAgentOrchestrator.EvaluateLocalCapabilityBoundaryForTests(plan);

        Assert.Equal(expectedAdvanced, decision.RequiresAdvancedAnalysis);
        Assert.Equal(answerUnits, decision.AnswerUnitCount);
    }

    [Fact]
    public void Single_item_stays_local()
    {
        var decision = ToolAgentOrchestrator.EvaluateLocalCapabilityBoundaryForTests(
            BuildPlan("single_item", 1));

        Assert.False(decision.RequiresAdvancedAnalysis);
        Assert.Equal("within_local_boundary", decision.ReasonCode);
    }

    [Fact]
    public void Missing_mission_stays_local()
    {
        var decision = ToolAgentOrchestrator.EvaluateLocalCapabilityBoundaryForTests(new RouterPlan());

        Assert.False(decision.RequiresAdvancedAnalysis);
        Assert.Equal(0, decision.AnswerUnitCount);
    }

    [Theory]
    [InlineData("Tu peux comparer simplement pressure-swing, vacuum-swing et flow-through inerting pour un client ?")]
    [InlineData("Compare le rôle de l'inertage décrit par FD CEN TR 15281 2023 avec IEC 60079-14.")]
    [InlineData("Compare deux recettes de quiche lorraine du corpus : ingrédients, méthode et difficulté.")]
    [InlineData("Mets en parallèle FD CEN TR 15281 2023 et IEC 60079-14.")]
    [InlineData("Put ISO 9001 and ISO 14001 side by side.")]
    public void Explicit_documentary_comparisons_require_advanced_analysis(string request)
    {
        var plan = new RouterPlan
        {
            Intent = "clarification",
            NeedClarification = true,
            ClarificationQuestions = ["Quels documents ?"]
        };

        var decision = ToolAgentOrchestrator.EvaluateLocalCapabilityBoundaryForTests(plan, request);

        Assert.True(decision.RequiresAdvancedAnalysis);
        Assert.Equal("explicit_documentary_comparison_outside_local_envelope", decision.ReasonCode);
        Assert.Equal("comparison", decision.PlanKind);
    }

    [Fact]
    public void Diversity_adjective_is_not_a_documentary_comparison_operator()
    {
        const string request =
            "Trouve quatre préparations différentes dans plusieurs documents.";

        Assert.False(
            ToolAgentOrchestrator
                .LooksLikeComparativeDocumentaryRequestForTests(request));
    }

    [Fact]
    public void Unresolved_deictic_comparison_without_prior_sources_stays_in_clarification_lane()
    {
        var decision = ToolAgentOrchestrator.EvaluateLocalCapabilityBoundaryForTests(
            new RouterPlan { NeedClarification = true },
            "Compare ces deux documents.",
            hasResolvedPriorSources: false);

        Assert.False(decision.RequiresAdvancedAnalysis);
    }

    [Fact]
    public void Deictic_comparison_with_prior_sources_requires_advanced_analysis()
    {
        var decision = ToolAgentOrchestrator.EvaluateLocalCapabilityBoundaryForTests(
            new RouterPlan { NeedClarification = true },
            "Compare ces deux documents.",
            hasResolvedPriorSources: true);

        Assert.True(decision.RequiresAdvancedAnalysis);
    }

    [Theory]
    [InlineData("Compare ces deux fichiers.")]
    [InlineData("Compare these two documents.")]
    public void Deictic_comparison_without_identified_documents_requests_references(
        string request)
    {
        Assert.True(
            ToolAgentOrchestrator
                .ShouldRequestUnresolvedComparativeDocumentReferencesForTests(
                    request));
    }

    [Theory]
    [InlineData("Compare ces deux fichiers A.pdf et B.pdf.")]
    [InlineData("Compare ces deux normes ISO 9001 et ISO 14001.")]
    public void Deictic_comparison_with_two_explicit_references_is_resolved(
        string request)
    {
        Assert.False(
            ToolAgentOrchestrator
                .ShouldRequestUnresolvedComparativeDocumentReferencesForTests(
                    request));
    }

    [Fact]
    public void Deictic_comparison_with_prior_sources_does_not_request_references()
    {
        Assert.False(
            ToolAgentOrchestrator
                .ShouldRequestUnresolvedComparativeDocumentReferencesForTests(
                    "Compare ces deux fichiers.",
                    hasResolvedPriorSources: true));
    }

    [Theory]
    [InlineData("fr", "Quels sont les deux fichiers")]
    [InlineData("en", "Which two files")]
    public void Comparative_reference_clarification_is_localized(
        string language,
        string expected)
    {
        Assert.Contains(
            expected,
            ToolAgentOrchestrator
                .BuildUnresolvedComparativeDocumentReferenceClarificationForTests(
                    language));
    }

    [Fact]
    public void Advanced_message_is_explicit_and_localized()
    {
        var answer = DeterministicAgentText.AdvancedAnalysisRequired(20, "fr");

        Assert.Contains("20 éléments", answer);
        Assert.Contains("capacité locale", answer);
        Assert.Contains("analyse avancée", answer);
        Assert.Contains("sources", answer);
    }

    [Theory]
    [InlineData(
        "J'ai besoin d'un planning du lundi au vendredi incluant petit-déjeuner, déjeuner, collation et souper.",
        true)]
    [InlineData(
        "Prépare un planning du lundi au vendredi.",
        false)]
    [InlineData(
        "Prépare un planning avec déjeuner et souper.",
        false)]
    public void Grid_axes_are_complete_only_when_both_explicit_axes_have_multiple_labels(
        string request,
        bool expectedComplete)
    {
        Assert.Equal(
            expectedComplete,
            ToolAgentOrchestrator.HasCompleteExplicitStructuredGridAxesForTests(request));
    }

    private static RouterPlan BuildPlan(
        string planKind,
        int atomicEvidenceCount,
        int rows = 1,
        int columns = 1)
        => new()
        {
            Intent = "rag.answer",
            Origin = RouterPlanOrigin.Llm,
            SourceBackedMission = new RouterPlan.SourceBackedMissionPlan
            {
                PlanKind = planKind,
                StructuredLayout = planKind == "structured_layout",
                AtomicEvidenceCount = atomicEvidenceCount,
                RowCount = rows,
                ColumnCount = columns
            }
        };
}
