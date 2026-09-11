using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class TypedTerminalOutcomeTests
{
    [Theory]
    [InlineData("rag.answer", "insufficient_evidence", false, "insufficient_evidence")]
    [InlineData("rag.answer", "clarify", true, "clarification")]
    [InlineData("rag.answer", "answer_ready", false, "rag.answer")]
    [InlineData("rag.compare", "answer_ready", false, "rag.compare")]
    public void Terminal_decision_is_exposed_as_the_final_intent(
        string pipelineIntent,
        string judgeDecision,
        bool isClarification,
        string expectedIntent)
    {
        Assert.Equal(
            expectedIntent,
            ToolAgentOrchestrator.ResolveTerminalSourceBackedIntentForTests(
                pipelineIntent,
                judgeDecision,
                isClarification));
    }
}
