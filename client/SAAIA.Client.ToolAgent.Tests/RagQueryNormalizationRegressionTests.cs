using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class RagQueryNormalizationRegressionTests
{
    [Theory]
    [InlineData("tu n'as aucun document qui parle de l'inertage ?", "inertage")]
    [InlineData("documents qui mentionnent de l'inertage", "inertage")]
    [InlineData("Je veux que tu me trouves les documents qui parlent d'inertage", "inertage")]
    [InlineData("l'inertage précisément", "inertage")]
    [InlineData("Cherche dans les documents si le controle SLA est mentionne et reponds avec les sources.", "controle SLA")]
    public void Rag_query_normalization_extracts_the_actual_topic(string input, string expected)
    {
        Assert.Equal(expected, ToolAgentOrchestrator.NormalizeRagQueryForTests(input));
    }

    [Fact]
    public void Rag_query_normalization_extracts_delimited_user_demand()
    {
        var input = "Prepare une reponse courte pour orienter un utilisateur qui demande `Alpha Beta proprietes thermiques`.";

        Assert.Equal("Alpha Beta proprietes thermiques", ToolAgentOrchestrator.NormalizeRagQueryForTests(input));
    }

    [Fact]
    public void Rag_query_normalization_repairs_replacement_accent_markers_in_delimited_user_demand()
    {
        var input = "Pr?pare une r?ponse courte pour orienter un utilisateur qui demande `propri?t?s ?lectriques`.";

        Assert.Equal("proprietes electriques", ToolAgentOrchestrator.NormalizeRagQueryForTests(input));
    }

    [Fact]
    public void Rag_search_execution_preserves_delimited_user_demand_for_backend_ranking()
    {
        var input = "Pr?pare une r?ponse courte pour orienter un utilisateur qui demande `propri?t?s ?lectriques`.";

        Assert.Equal(input, ToolAgentOrchestrator.ResolveRagSearchExecutionQueryForTests(input));
    }

    [Fact]
    public void Source_backed_action_queries_prioritize_delimited_user_demand_and_drop_wrapper_terms()
    {
        var input = "Prepare une reponse courte pour orienter un utilisateur qui demande `Alpha Beta proprietes thermiques`.";

        var queries = ToolAgentOrchestrator.BuildSourceBackedActionRetrievalQueriesForTests(input);
        var joined = string.Join(" | ", queries);

        Assert.Equal("Alpha Beta proprietes thermiques", queries[0]);
        Assert.Contains(queries, query => query.Contains("thermal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queries, query => query.Contains("melt point", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => string.Equals(query, "orienter", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => string.Equals(query, "utilisateur", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queries, query => string.Equals(query, "demande", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("orienter utilisateur demande", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clarification_payload_uses_the_user_clarification_as_retrieval_topic()
    {
        var payload = """
PREVIOUS_USER_REQUEST:
Un client m'a parle d'inertage et j'aimerais que tu m'aides sur ce sujet.

USER_CLARIFICATION:
l'inertage precisement

RESOLVED_REQUEST:
Continue the previous request using the clarification as the intended topic or scope.
""";

        Assert.Equal("inertage", ToolAgentOrchestrator.NormalizeRagQueryForTests(payload));
    }

    [Fact]
    public void Broadened_source_search_payload_uses_previous_request_as_retrieval_topic()
    {
        var payload = """
PREVIOUS_USER_REQUEST:
Prepare un plan hebdomadaire varie a partir des documents disponibles.

USER_CONFIRMED_BROADER_SOURCE_SEARCH:
oui vas-y

RESOLVED_REQUEST:
Continue the previous source-backed request by running a broader retrieval exploration.
""";

        var normalized = ToolAgentOrchestrator.NormalizeRagQueryForTests(payload);

        Assert.Equal("Prepare un plan hebdomadaire varie a partir des documents disponibles", normalized);
        Assert.DoesNotContain("oui", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("USER_CONFIRMED", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("broader", normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Broadened_source_search_fallback_uses_original_intent_not_short_confirmation()
    {
        var payload = """
PREVIOUS_USER_REQUEST:
Prepare une proposition sourcee pour organiser les controles hebdomadaires.

USER_CONFIRMED_BROADER_SOURCE_SEARCH:
oui vas-y

RESOLVED_REQUEST:
Continue the previous source-backed request by running a broader retrieval exploration.
""";

        var intent = ToolAgentOrchestrator.ResolveSourceBackedFallbackIntentQueryForTests(payload);

        Assert.Equal("Prepare une proposition sourcee pour organiser les controles hebdomadaires.", intent);
        Assert.DoesNotContain("oui", intent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("USER_CONFIRMED", intent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("broader", intent, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("l'inertage", true)]
    [InlineData("ATEX", true)]
    [InlineData("qui es-tu", false)]
    [InlineData("bonjour", false)]
    public void Standalone_topic_detection_keeps_short_technical_topics_out_of_general_chat(string input, bool expected)
    {
        Assert.Equal(expected, ToolAgentOrchestrator.LooksLikeStandaloneDocumentaryTopicForTests(input));
    }

    [Theory]
    [InlineData("Je veux que tu me trouves les documents qui parlent d'inertage", true, "inertage")]
    [InlineData("Cherche les documents qui mentionnent l'inertage", true, "inertage")]
    [InlineData("Cherche dans les documents si le controle SLA est mentionne et reponds avec les sources.", true, "controle SLA")]
    [InlineData("I am looking for advice about a roasting probe and doneness levels. Which documents mention this?", true, "a roasting probe and doneness levels")]
    [InlineData("Busco consejos sobre una sonda de asado y los puntos de coccion. Que documentos hablan de eso?", true, "una sonda de asado y los puntos de coccion")]
    [InlineData("Procuro conselhos sobre uma sonda de assar e os pontos de cozedura. Que documentos falam disso?", true, "uma sonda de assar e os pontos de cozedura")]
    [InlineData("Ich suche Hinweise zu Bratenthermometer und Garstufen. Welche Dokumente sprechen darueber?", true, "Bratenthermometer und Garstufen")]
    [InlineData("Cerco consigli su una sonda per arrosti e sui livelli di cottura. Quali documenti ne parlano?", true, "una sonda per arrosti e sui livelli di cottura")]
    [InlineData("Cherche les documents qui correspondent a PumpManual.pdf", false, "")]
    public void Document_content_search_requests_are_not_treated_as_inventory_title_search(
        string input,
        bool expectedMatched,
        string expectedTopic)
    {
        var (matched, topic) = ToolAgentOrchestrator.TryExtractDocumentContentSearchTopicForTests(input);

        Assert.Equal(expectedMatched, matched);
        Assert.Equal(expectedTopic, topic);
    }

    [Theory]
    [InlineData("bonjour")]
    [InlineData("Dis simplement bonjour.")]
    [InlineData("Merci.")]
    public void Standalone_topic_rag_does_not_force_retrieval_for_general_chat(string input)
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "auto"
        };

        Assert.False(ToolAgentOrchestrator.ShouldForceRagForStandaloneTopicForTests(input, plan));
    }

    [Fact]
    public void Standalone_topic_rag_still_forces_retrieval_for_document_content_search_when_router_fell_back()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "auto"
        };

        Assert.True(ToolAgentOrchestrator.ShouldForceRagForStandaloneTopicForTests(
            "Cherche dans les documents si le controle SLA est mentionne et reponds avec les sources.",
            plan));
    }

    [Fact]
    public void Standalone_topic_rag_respects_llm_general_without_tools_for_document_content_search()
    {
        var plan = new RouterPlan
        {
            Intent = "chat.general",
            Language = "fr",
            Mode = "auto",
            Origin = RouterPlanOrigin.Llm
        };

        Assert.False(ToolAgentOrchestrator.ShouldForceRagForStandaloneTopicForTests(
            "Cherche dans les documents si le controle SLA est mentionne et reponds avec les sources.",
            plan));
    }

    [Theory]
    [InlineData("en", "I found 2 document(s) with indexed content about roasting probe")]
    [InlineData("es", "He encontrado 2 documento(s) con contenido indexado sobre roasting probe")]
    [InlineData("pt", "Encontrei 2 documento(s) com conteudo indexado sobre roasting probe")]
    [InlineData("de", "Ich habe 2 Dokument(e) mit indexiertem Inhalt zu roasting probe")]
    [InlineData("it", "Ho trovato 2 documento/i con contenuti indicizzati su roasting probe")]
    [InlineData("fr", "J'ai trouvé 2 document(s) avec du contenu indexé sur roasting probe")]
    public void Document_content_search_answer_is_deterministic_and_localized(string language, string expectedHeader)
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/GuideA.pdf",
                    docName = "GuideA.pdf",
                    pageStart = 12,
                    excerpt = "Use the roasting probe and check doneness levels.",
                    score = 0.99
                },
                new
                {
                    docPath = "Knowledge/GuideB.pdf",
                    docName = "GuideB.pdf",
                    pageStart = 5,
                    excerpt = "Doneness levels are listed with probe guidance.",
                    score = 0.91
                },
                new
                {
                    docPath = "Knowledge/GuideA.pdf",
                    docName = "GuideA.pdf",
                    pageStart = 13,
                    excerpt = "More guidance for the same document.",
                    score = 0.88
                }
            }
        });
        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = doc.RootElement.Clone() });

        var answer = ToolAgentOrchestrator.BuildDocumentContentSearchAnswerForTests(toolResults, "roasting probe", language);

        Assert.Contains(expectedHeader, answer, StringComparison.Ordinal);
        Assert.Contains("- GuideA.pdf", answer, StringComparison.Ordinal);
        Assert.Contains("- GuideB.pdf", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Use the roasting probe", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Doneness levels", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("GuideA.pdf\n- GuideA.pdf", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_content_search_expands_sparse_reason_requests()
    {
        var singleDocPayload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/GuideA.pdf",
                    docName = "GuideA.pdf",
                    pageStart = 12,
                    excerpt = "A short passage about the requested topic.",
                    score = 0.99
                }
            }
        });
        var richerPayload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/GuideA.pdf",
                    docName = "GuideA.pdf",
                    pageStart = 12,
                    excerpt = "A short passage about the requested topic.",
                    score = 0.99
                },
                new
                {
                    docPath = "Knowledge/GuideB.pdf",
                    docName = "GuideB.pdf",
                    pageStart = 5,
                    excerpt = "A second document explains why this topic matters.",
                    score = 0.92
                },
                new
                {
                    docPath = "Knowledge/GuideC.pdf",
                    docName = "GuideC.pdf",
                    pageStart = 8,
                    excerpt = "A third document gives another relevant angle.",
                    score = 0.89
                }
            }
        });

        Assert.True(ToolAgentOrchestrator.ShouldExpandDocumentContentSearchForTests(
            "Which documents are useful for this topic and why?",
            singleDocPayload));
        Assert.True(ToolAgentOrchestrator.IsBetterDocumentContentSearchCoverageForTests(singleDocPayload, richerPayload));
        Assert.False(ToolAgentOrchestrator.ShouldExpandDocumentContentSearchForTests(
            "Which documents mention this topic?",
            richerPayload));
    }

    [Fact]
    public void Document_content_selection_explanations_use_writer_when_sources_exist()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Knowledge/GuideA.pdf",
                    docName = "GuideA.pdf",
                    pageStart = 12,
                    excerpt = "This passage explains the topic and why it matters.",
                    score = 0.99
                },
                new
                {
                    docPath = "Knowledge/GuideB.pdf",
                    docName = "GuideB.pdf",
                    pageStart = 5,
                    excerpt = "This second document gives another useful angle.",
                    score = 0.92
                }
            }
        });

        Assert.True(ToolAgentOrchestrator.ShouldUseWriterForDocumentContentSearchAnswerForTests(
            "Which documents are useful for this topic and why?",
            payload));
        Assert.False(ToolAgentOrchestrator.ShouldUseWriterForDocumentContentSearchAnswerForTests(
            "Which documents mention this topic?",
            payload));
    }
}
