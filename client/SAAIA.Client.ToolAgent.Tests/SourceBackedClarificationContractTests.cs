using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedClarificationContractTests
{
    private const string ValidArguments = """
        {
          "understanding": "Le corpus contient des composants separes et un document de planning.",
          "options": [
            "Composer le resultat a partir des composants sources",
            "Utiliser le document deja constitue"
          ],
          "executionImpact": "La reponse change le perimetre et la strategie de recherche.",
          "ambiguityKind": "source_strategy"
        }
        """;

    [Fact]
    public void Source_backed_clarification_tool_has_a_bounded_explicit_contract()
    {
        var tool = SourceBackedAgentV2Runner
            .BuildSourceBackedClarificationToolForTests();

        Assert.Equal("request_user_clarification", tool.Name);
        Assert.Equal(2, tool.Parameters
            .GetProperty("properties")
            .GetProperty("options")
            .GetProperty("minItems")
            .GetInt32());
        Assert.Equal(4, tool.Parameters
            .GetProperty("properties")
            .GetProperty("options")
            .GetProperty("maxItems")
            .GetInt32());
        Assert.Contains(
            "seul l'utilisateur peut trancher",
            tool.Description,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Source_backed_clarification_parser_preserves_llm_owned_content()
    {
        var result = SourceBackedAgentV2Runner
            .ReadSourceBackedClarificationForTests(ValidArguments);

        Assert.True(result.Accepted, result.FailureReason);
        var decision = Assert.IsType<SourceBackedClarificationDecision>(
            result.Decision);
        Assert.Equal("source_strategy", decision.AmbiguityKind);
        Assert.Equal(2, decision.Options.Count);
        Assert.Contains("Quelle option", decision.Message);
    }

    [Fact]
    public void Source_backed_clarification_must_be_the_only_action()
    {
        var result = SourceBackedAgentV2Runner
            .ReadSourceBackedClarificationForTests(
                ValidArguments,
                includeAnotherCall: true);

        Assert.False(result.Accepted);
        Assert.Equal(
            "source_backed_clarification_single_call_required",
            result.FailureReason);
    }

    [Fact]
    public void Terminal_answer_renders_exact_clarification_and_options()
    {
        var clarification = new SourceBackedClarificationDecision(
            "J'ai compris la demande. Quelle strategie preferez-vous ?",
            new[] { "Composer les elements", "Utiliser un plan existant" },
            "La reponse change la recherche.",
            "source_strategy");
        var intake = new SourceBackedIntake(
            "Demande ambigue",
            "answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            false,
            "fr");
        var result = new SourceBackedPipelineResult(
            "clarification-result",
            intake,
            new RetrievalPlan(
                "LLM clarification",
                Array.Empty<RetrievalRequest>(),
                NeedsClarification: true),
            EvidenceBundle.Empty(intake.UserQuestion),
            new EvidenceJudgeDecision(
                "clarify",
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<RetrievalRequest>(),
                Array.Empty<string>()),
            null,
            null,
            null,
            false,
            Array.Empty<SourceBackedTraceEvent>(),
            Clarification: clarification);

        var answer = SourceBackedTerminalAnswer.Build(result);

        Assert.StartsWith(clarification.Message, answer, StringComparison.Ordinal);
        Assert.Contains("- Composer les elements", answer);
        Assert.Contains("- Utiliser un plan existant", answer);
    }
}
