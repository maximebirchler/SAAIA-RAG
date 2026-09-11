using System;
using System.Collections.Generic;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ClarificationMemoryRegressionTests
{
    [Fact]
    public void Llm_router_clarification_renders_exact_message_and_options()
    {
        var plan = new RouterPlan
        {
            NeedClarification = true,
            Clarification = new RouterPlan.ClarificationDecisionPlan
            {
                Message = "J'ai compris que vous souhaitez un planning. Quelle approche preferez-vous ?",
                Options = new()
                {
                    "Composer chaque creneau avec une recette sourcee",
                    "Chercher un planning deja constitue"
                }
            }
        };

        var rendered =
            ToolAgentOrchestrator.RenderRouterClarificationForTests(plan);

        Assert.StartsWith(plan.Clarification.Message, rendered, StringComparison.Ordinal);
        Assert.Contains("- Composer chaque creneau", rendered, StringComparison.Ordinal);
        Assert.Contains("- Chercher un planning", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Llm_router_pending_clarification_gives_the_next_router_full_resolution_context()
    {
        var mem = new ToolMemory
        {
            PendingClarification = new ToolMemory.PendingClarificationState
            {
                Kind = "llm_router",
                OriginalUserMessage =
                    "J'ai besoin d'un planning de repas pour la semaine.",
                Question =
                    "Souhaitez-vous une composition recette par recette ou un planning deja constitue ?",
                Options = new()
                {
                    "Composer chaque creneau",
                    "Chercher un planning constitue"
                },
                ExecutionImpact =
                    "La reponse determine la strategie de recherche.",
                ResumeRoute = "source_backed_grid",
                Language = "fr"
            }
        };

        var prepared = ToolAgentOrchestrator.PreparePendingClarificationForTests(
            mem,
            Array.Empty<(string role, string content)>(),
            "Compose chaque creneau avec une recette differente.");

        Assert.True(prepared.Consumed);
        Assert.Contains("PREVIOUS_USER_REQUEST", prepared.EffectiveUserMessage);
        Assert.Contains("CLARIFICATION_ASKED", prepared.EffectiveUserMessage);
        Assert.Contains("OPTIONS_OFFERED", prepared.EffectiveUserMessage);
        Assert.Contains("CURRENT_USER_TURN", prepared.EffectiveUserMessage);
        Assert.Contains("EXPECTED_RESUME_ROUTE", prepared.EffectiveUserMessage);
        Assert.Contains("source_backed_grid", prepared.EffectiveUserMessage);
        Assert.Contains(
            "Decide whether CURRENT_USER_TURN answers",
            prepared.EffectiveUserMessage);
        Assert.Null(mem.PendingClarification);
    }

    [Fact]
    public void Corpus_probe_clarification_preserves_question_options_and_user_reply()
    {
        var mem = new ToolMemory
        {
            PendingClarification = new ToolMemory.PendingClarificationState
            {
                Kind = "rag_probe",
                OriginalUserMessage = "Prepare un planning a partir du corpus.",
                Question = "Faut-il composer le planning ou utiliser celui deja trouve ?",
                Options = new() { "Composer", "Utiliser l'existant" },
                ExecutionImpact = "La strategie de recherche change.",
                ResumeRoute = "source_backed",
                Language = "fr"
            }
        };

        var prepared = ToolAgentOrchestrator.PreparePendingClarificationForTests(
            mem,
            Array.Empty<(string role, string content)>(),
            "Compose-le avec des elements differents.");

        Assert.True(prepared.Consumed);
        Assert.Contains("CLARIFICATION_ASKED", prepared.EffectiveUserMessage);
        Assert.Contains("OPTIONS_OFFERED", prepared.EffectiveUserMessage);
        Assert.Contains("Compose-le", prepared.EffectiveUserMessage);
        Assert.Contains("source_backed", prepared.EffectiveUserMessage);
        Assert.Null(mem.PendingClarification);
    }

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

    [Theory]
    [InlineData("rag_probe")]
    [InlineData("rag_guidance")]
    public void Broadened_source_search_confirmation_reuses_previous_request_for_rag_followups(string pendingKind)
    {
        var mem = new ToolMemory();
        mem.PendingClarification = new ToolMemory.PendingClarificationState
        {
            Kind = pendingKind,
            OriginalUserMessage = "Je cherche a construire un planning a partir des documents disponibles.",
            Language = "fr",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var prepared = ToolAgentOrchestrator.PreparePendingClarificationForTests(
            mem,
            Array.Empty<(string role, string content)>(),
            "vas-y");

        Assert.True(prepared.Consumed);
        Assert.Contains("PREVIOUS_USER_REQUEST", prepared.EffectiveUserMessage);
        Assert.Contains("USER_CONFIRMED_BROADER_SOURCE_SEARCH", prepared.EffectiveUserMessage);
        Assert.Contains("planning", prepared.EffectiveUserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("broader retrieval exploration", prepared.EffectiveUserMessage);
        Assert.DoesNotContain("RETRIEVAL_REFINEMENT", prepared.EffectiveUserMessage);
        Assert.Null(mem.PendingClarification);
    }

    [Theory]
    [InlineData("fr", "oui vas-y")]
    [InlineData("en", "yes, run the broader search")]
    [InlineData("es", "si, amplia la busqueda")]
    [InlineData("pt", "sim, pesquisa mais ampla")]
    [InlineData("de", "ja, breitere Suche")]
    [InlineData("it", "si, allarga la ricerca")]
    public void Broadened_source_search_confirmation_is_multilingual(
        string language,
        string confirmation)
    {
        var mem = new ToolMemory();
        mem.PendingClarification = new ToolMemory.PendingClarificationState
        {
            Kind = "source_backed_broaden_search",
            OriginalUserMessage = "Can you prepare a weekly plan from the available documents?",
            Language = language,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var prepared = ToolAgentOrchestrator.PreparePendingClarificationForTests(
            mem,
            Array.Empty<(string role, string content)>(),
            confirmation);

        Assert.True(prepared.Consumed);
        Assert.Contains("USER_CONFIRMED_BROADER_SOURCE_SEARCH", prepared.EffectiveUserMessage);
        Assert.Contains("weekly plan", prepared.EffectiveUserMessage, StringComparison.OrdinalIgnoreCase);
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
            ("assistant", "Je peux lancer une recherche plus large si tu veux completer les parties non couvertes.")
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

    [Theory]
    [InlineData("fr", "oui vas-y")]
    [InlineData("en", "yes, run the broader search")]
    [InlineData("es", "si, amplia la busqueda")]
    [InlineData("pt", "sim, pesquisa mais ampla")]
    [InlineData("de", "ja, breitere Suche")]
    [InlineData("it", "si, allarga la ricerca")]
    public void Broadened_source_search_offer_generated_by_ui_is_recovered_from_chat_history(
        string language,
        string confirmation)
    {
        var mem = new ToolMemory();
        var history = new List<(string role, string content)>
        {
            ("user", "Can you prepare a weekly plan from the available documents?"),
            ("assistant", DeterministicAgentText.SourceBackedExpandedSearchOffer(language))
        };

        var prepared = ToolAgentOrchestrator.PreparePendingClarificationForTests(
            mem,
            history,
            confirmation);

        Assert.True(prepared.Consumed);
        Assert.Contains("PREVIOUS_USER_REQUEST", prepared.EffectiveUserMessage);
        Assert.Contains("weekly plan", prepared.EffectiveUserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USER_CONFIRMED_BROADER_SOURCE_SEARCH", prepared.EffectiveUserMessage);
        Assert.Contains("broader retrieval exploration", prepared.EffectiveUserMessage);
    }
}
