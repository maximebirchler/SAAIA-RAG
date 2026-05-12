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
    public void Rag_query_normalization_extracts_the_actual_topic(string input, string expected)
    {
        Assert.Equal(expected, ToolAgentOrchestrator.NormalizeRagQueryForTests(input));
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
    [InlineData("en", "I found 2 document(s) with indexed content about roasting probe")]
    [InlineData("es", "He encontrado 2 documento(s) con contenido indexado sobre roasting probe")]
    [InlineData("pt", "Encontrei 2 documento(s) com conteudo indexado sobre roasting probe")]
    [InlineData("de", "Ich habe 2 Dokument(e) mit indexiertem Inhalt zu roasting probe")]
    [InlineData("it", "Ho trovato 2 documento/i con contenuti indicizzati su roasting probe")]
    [InlineData("fr", "J'ai trouve 2 document(s) avec du contenu indexe sur roasting probe")]
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
        Assert.DoesNotContain("GuideA.pdf\n- GuideA.pdf", answer, StringComparison.Ordinal);
    }
}
