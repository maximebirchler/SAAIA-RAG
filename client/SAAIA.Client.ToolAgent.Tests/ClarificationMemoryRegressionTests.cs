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

    [Fact]
    public void Broadened_source_search_confirmation_reuses_previous_request()
    {
        var mem = new ToolMemory();
        mem.PendingClarification = new ToolMemory.PendingClarificationState
        {
            Kind = "source_backed_broaden_search",
            OriginalUserMessage = "Je cherche a avoir un plan pour la semaine avec les documents.",
            Language = "fr",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var prepared = ToolAgentOrchestrator.PreparePendingClarificationForTests(
            mem,
            Array.Empty<(string role, string content)>(),
            "oui vas-y");

        Assert.True(prepared.Consumed);
        Assert.Contains("PREVIOUS_USER_REQUEST", prepared.EffectiveUserMessage);
        Assert.Contains("USER_CONFIRMED_BROADER_SOURCE_SEARCH", prepared.EffectiveUserMessage);
        Assert.Contains("plan pour la semaine", prepared.EffectiveUserMessage);
        Assert.Contains("broader retrieval exploration", prepared.EffectiveUserMessage);
        Assert.Null(mem.PendingClarification);
    }

    [Fact]
    public void Broadened_source_search_pending_does_not_swallow_a_new_full_request()
    {
        var mem = new ToolMemory();
        mem.PendingClarification = new ToolMemory.PendingClarificationState
        {
            Kind = "source_backed_broaden_search",
            OriginalUserMessage = "Prepare un plan hebdomadaire a partir des documents.",
            Language = "fr",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var prepared = ToolAgentOrchestrator.PreparePendingClarificationForTests(
            mem,
            Array.Empty<(string role, string content)>(),
            "Donne moi juste une liste de procedures disponibles.");

        Assert.False(prepared.Consumed);
        Assert.DoesNotContain("PREVIOUS_USER_REQUEST", prepared.EffectiveUserMessage);
        Assert.Equal("Donne moi juste une liste de procedures disponibles.", prepared.EffectiveUserMessage);
        Assert.Null(mem.PendingClarification);
    }

    [Fact]
    public void Broadened_source_search_offer_can_be_recovered_from_chat_history()
    {
        var mem = new ToolMemory();
        var history = new List<(string role, string content)>
        {
            ("user", "Je cherche a avoir un plan pour la semaine avec les documents."),
            ("assistant", "Les sources recuperees ne suffisent pas a construire une reponse fiable sans inventer. Si tu veux, je peux lancer une recherche plus large dans le corpus ou tu peux preciser le perimetre a explorer.")
        };

        var prepared = ToolAgentOrchestrator.PreparePendingClarificationForTests(
            mem,
            history,
            "vas-y");

        Assert.True(prepared.Consumed);
        Assert.Contains("PREVIOUS_USER_REQUEST", prepared.EffectiveUserMessage);
        Assert.Contains("plan pour la semaine", prepared.EffectiveUserMessage);
        Assert.Contains("broader retrieval exploration", prepared.EffectiveUserMessage);
    }
}
