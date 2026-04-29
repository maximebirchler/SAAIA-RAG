using System;
using System.Collections.Generic;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ClarificationMemoryRegressionTests
{
    [Fact]
    public void Generic_pending_clarification_consumes_short_topic_answer()
    {
        var mem = new ToolMemory();
        mem.PendingClarification = new ToolMemory.PendingClarificationState
        {
            Kind = "generic",
            OriginalUserMessage = "Un client m'a parle d'inertage et j'aimerais que tu m'aides sur ce sujet.",
            Language = "fr",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var prepared = ToolAgentOrchestrator.PreparePendingClarificationForTests(
            mem,
            Array.Empty<(string role, string content)>(),
            "l'inertage precisement");

        Assert.True(prepared.Consumed);
        Assert.Contains("PREVIOUS_USER_REQUEST", prepared.EffectiveUserMessage);
        Assert.Contains("inertage", prepared.EffectiveUserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(mem.PendingClarification);
    }

    [Fact]
    public void Generic_clarification_can_be_recovered_from_chat_history()
    {
        var mem = new ToolMemory();
        var history = new List<(string role, string content)>
        {
            ("user", "Un client m'a parle d'inertage et j'aimerais que tu m'aides sur ce sujet."),
            ("assistant", "- Quel sujet precis voulez-vous que je traite ?")
        };

        var prepared = ToolAgentOrchestrator.PreparePendingClarificationForTests(
            mem,
            history,
            "l'inertage precisement");

        Assert.True(prepared.Consumed);
        Assert.Contains("USER_CLARIFICATION", prepared.EffectiveUserMessage);
        Assert.Contains("l'inertage precisement", prepared.EffectiveUserMessage);
    }
}
