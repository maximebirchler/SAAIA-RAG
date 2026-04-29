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
}
