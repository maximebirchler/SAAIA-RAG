using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class RagContextBudgetRegressionTests
{
    [Fact]
    public void NormalizeRagHits_compacts_already_normalized_hits_before_writer_prompt()
    {
        var longExcerpt = new string('x', 1000);
        var payload = JsonSerializer.Serialize(new
        {
            hits = Enumerable.Range(1, 12).Select(i => new
            {
                docPath = $"Manual{i}.pdf",
                docName = $"Manual {i}",
                pageStart = i,
                pageEnd = i,
                excerpt = longExcerpt,
                score = 0.9
            })
        });

        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(payload);
        var hits = normalized.GetProperty("hits").EnumerateArray().ToList();
        var firstHit = normalized.GetProperty("hits").EnumerateArray().First();
        var excerpt = firstHit.GetProperty("excerpt").GetString();

        Assert.Equal(8, hits.Count);
        Assert.Equal("Manual1.pdf", firstHit.GetProperty("docPath").GetString());
        Assert.Equal(1, firstHit.GetProperty("pageStart").GetInt32());
        Assert.NotNull(excerpt);
        Assert.True(excerpt!.Length <= 243);
        Assert.EndsWith("...", excerpt);
    }

    [Fact]
    public void SerializeTail_compacts_long_messages_before_writer_prompt()
    {
        var history = Enumerable.Range(1, 4)
            .Select(i => (i % 2 == 0 ? "assistant" : "user", new string((char)('a' + i), 1200)))
            .ToList();

        var json = ToolAgentOrchestrator.SerializeTailForTests(history, maxTurns: 3);
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(3, items.Count);
        Assert.All(items, item =>
        {
            var content = item.GetProperty("content").GetString();
            Assert.NotNull(content);
            Assert.True(content!.Length <= 423);
            Assert.EndsWith("...", content);
        });
    }

    [Fact]
    public void Cuisine_rag_hits_use_extractive_answer_to_avoid_recipe_hallucination()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Cuisine/livre-recette-sist-2025-web.pdf",
                    docName = "livre-recette-sist-2025-web.pdf",
                    pageStart = 23,
                    pageEnd = 23,
                    excerpt = "Gateau chocolat-courgette. Ingredients: 150 g de chocolat noir dessert, 30 g de sucre, 4 oeufs, 300 g de courgettes, 70 g de farine.",
                    score = 0.97
                }
            }
        });

        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = doc.RootElement.Clone()
        });

        Assert.True(ToolAgentOrchestrator.ShouldUseCuisineExtractiveAnswerForTests("dessert au chocolat facile", toolResults));

        var answer = ToolAgentOrchestrator.BuildCuisineExtractiveAnswerForTests(toolResults, "dessert au chocolat facile", "fr");

        Assert.Contains("sans ajout", answer);
        Assert.Contains("Gateau chocolat-courgette", answer);
        Assert.Contains("300 g de courgettes", answer);
    }

    [Fact]
    public void Cuisine_meat_sauce_answer_stays_cautious_without_explicit_pairing()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Cuisine/facilitemps.pdf",
                    docName = "facilitemps.pdf",
                    pageStart = 44,
                    pageEnd = 44,
                    excerpt = "Les sauces et les trempettes. Une idee pour rehausser le gout de vos viandes est de cuisiner des sauces et des trempettes.",
                    score = 0.99
                }
            }
        });

        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = doc.RootElement.Clone()
        });

        var answer = ToolAgentOrchestrator.BuildCuisineExtractiveAnswerForTests(toolResults, "quelle sauce avec une entrecote ?", "fr");

        Assert.Contains("pas trouve d'association explicite", answer);
        Assert.Contains("facilitemps.pdf p.44", answer);
    }

    [Fact]
    public void Cuisine_action_query_with_accented_entrecote_uses_extractive_answer()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Cuisine/facilitemps.pdf",
                    docName = "facilitemps.pdf",
                    pageStart = 44,
                    pageEnd = 44,
                    excerpt = "Les sauces et les trempettes. Une idee pour rehausser le gout de vos viandes est de cuisiner des sauces et des trempettes.",
                    score = 0.99
                }
            }
        });

        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = doc.RootElement.Clone()
        });

        Assert.True(ToolAgentOrchestrator.ShouldUseCuisineExtractiveAnswerForTests("Je vais faire une entrecôte, quelle sauce irait bien avec ?", toolResults));
        Assert.True(ToolAgentOrchestrator.LooksLikeCuisineActionRequestForTests("Je vais faire une entrecôte, quelle sauce irait bien avec ?"));
    }

    [Fact]
    public void Cuisine_meal_planning_answer_lists_source_backed_recipe_days()
    {
        var payload = JsonSerializer.Serialize(new
        {
            hits = new[]
            {
                new
                {
                    docPath = "Cuisine/livre-recette-sist-2025-web.pdf",
                    docName = "livre-recette-sist-2025-web.pdf",
                    pageStart = 17,
                    pageEnd = 17,
                    excerpt = "17MenuFILET DE CABILLAUD EN CRUMBLE DE CHORIZO & PARMESAN10 min4Ingrédients4 filets de cabillaud (surgelés)",
                    score = 1.0
                },
                new
                {
                    docPath = "Cuisine/livre-recette-sist-2025-web.pdf",
                    docName = "livre-recette-sist-2025-web.pdf",
                    pageStart = 15,
                    pageEnd = 15,
                    excerpt = "15MenuCHILI CON CARNEHEALTHY50 min4Ingrédients500g de boeuf hache",
                    score = 0.99
                }
            }
        });

        using var doc = JsonDocument.Parse(payload);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.multi_search",
            Result = doc.RootElement.Clone()
        });

        var answer = ToolAgentOrchestrator.BuildCuisineMealPlanningAnswerForTests(toolResults, "fr");

        Assert.Contains("Jour 1", answer);
        Assert.Contains("FILET DE CABILLAUD", answer);
        Assert.Contains("CHILI CON CARNE", answer);
        Assert.Contains("documents Cuisine", answer);
    }

    [Theory]
    [InlineData("fr", "sources Cuisine")]
    [InlineData("en", "Cuisine sources")]
    [InlineData("es", "fuentes de Cocina")]
    [InlineData("pt", "fontes de Cozinha")]
    [InlineData("de", "Kuechenquellen")]
    [InlineData("it", "fonti di Cucina")]
    public void Cuisine_extractive_headers_cover_all_supported_languages(string language, string expectedPhrase)
    {
        var header = ToolAgentOrchestrator.BuildCuisineExtractiveHeaderForTests(language, noExplicitPairing: true);

        Assert.Contains(expectedPhrase, header);
        Assert.DoesNotContain("Here are the leads", header);
    }
}
