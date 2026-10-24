using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Models;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class RetrievalRuntimeSwitchTests
{

    [Fact]
    public void StructuredContinuationBackfill_accepts_nobilia_same_page_ingredients_after_title_route()
    {
        const string anchorId = "e95cf988-88c4-7c1d-9d1f-0cd599ca64f6";
        const string previousId = "c61abeee-c8d1-1cbf-c007-f4db33d8b7cc";
        const string prepId = "18f89121-2ce3-7dd9-0984-2a5da4b7464b";
        const string sauceId = "3c0b9e09-1502-d48f-aff1-5191245d8856";
        const string ingredientsId = "ad15c7b7-9545-6360-3302-be198033ac7b";
        const string sectionTitle = "4 Homes ila, Egouttez-les et coupez-les en rondelles epaisses. Mettez-les dans un saladier, ajoutez-y les";
        const string query = "Tu peux me faire une fiche claire pour \"Brochettes de poulet grille a l'indonesienne\" : materials, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "Brochettes de poulet grille a l'indonesienne. L'Indonesie actuelle appartenait aux colonies neerlandaises d'Extreme-Orient. Pour 6 personnes 1. Coupez les blancs de poulet en fines lanieres.",
            embedText: "Matched local title token route",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 42,
            chunkId: anchorId,
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 0.918) with
        {
            ChunkIndex = 51,
            SectionTitle = sectionTitle,
            HeadingPath = sectionTitle,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            NextChunkId = prepId,
            SameSectionChunkId = prepId
        };
        var prep = TestMatch(
            text: "Mettez tous les ingredients necessaires a la marinade dans un grand saladier et melangez avec une cuillere. Ajoutez la viande et badigeonnez-la delicatement de marinade. Recouvrez d'un film alimentaire et mettez au refrigerateur 3-12 heures.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 42,
            chunkId: prepId,
            embeddingBasis: "linked_context_v1",
            chunkType: "section_window_v1",
            score: 0.873) with
        {
            ChunkIndex = 52,
            SectionTitle = sectionTitle,
            HeadingPath = sectionTitle,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = anchorId
        };
        var previous = TestMatch(
            text: "Pour 4 personnes poivre noir du moulin. Coupez les filets de harengs en petits morceaux et poivrez-les. Pressez le jus des citrons verts.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 40,
            chunkId: previousId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.882) with
        {
            ChunkIndex = 50,
            SectionTitle = sectionTitle,
            HeadingPath = sectionTitle,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            NextChunkId = anchorId,
            SameSectionChunkId = anchorId
        };
        var ingredients = TestMatch(
            text: "CONSEIL Faites mariner les brochettes des le matin pour les servir le soir. INGREDIENTS 1,5 kg de blancs de poulet huile pour la grille. Pour la marinade 3 echalotes, 2 gousses d'ail, piment en poudre, coriandre, gingembre, sauce de soja, vinaigre et huile.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 42,
            chunkId: ingredientsId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.871) with
        {
            ChunkIndex = 54,
            SectionTitle = sectionTitle,
            HeadingPath = sectionTitle,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70
        };
        var sauce = TestMatch(
            text: "Mettez les cacahouetes, l'oignon, l'ail, les flocons de piment, le gingembre, le sucre et le jus de citron dans un mixeur et reduisez en puree tres lisse. Incorporez environ 375 ml d'eau chaude afin d'obtenir une sauce bien liquide.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 42,
            chunkId: sauceId,
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.889) with
        {
            ChunkIndex = 53,
            SectionTitle = sectionTitle,
            HeadingPath = sectionTitle,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = prepId
        };

        var selected = new List<RagMatch> { anchor };
        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(query, selected, limit: 4);
        var selectedAnchor = Assert.Single(anchors);
        Assert.Equal(anchorId, selectedAnchor.ChunkId);

        var continuations = RetrievalTitleSelection.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { previous, prep, sauce, ingredients },
            limit: 4);

        Assert.DoesNotContain(continuations, match => match.ChunkId == previousId);
        Assert.Contains(continuations, match => match.ChunkId == prepId);
        Assert.Contains(continuations, match => match.ChunkId == sauceId);
        Assert.Contains(continuations, match => match.ChunkId == ingredientsId);
    }

    [Fact]
    public void StructuredContinuationBackfill_accepts_previous_chunk_when_linked_context_starts_mid_procedure()
    {
        const string titleId = "455c86c5-d8ab-b160-bb5c-08f9ebaadf84";
        const string previousId = "7841da55-0997-aa1d-0c4d-f0a5e34e60ef";
        const string continuationId = "66bdcae6-3438-5489-9a8c-f0561c606a73";
        const string query = "Tu peux me faire une fiche claire pour \"Salade de harengs\" : ingredients, etapes, temps et source ?";
        var title = TestMatch(
            text: "Salade de harengs superposee Preparee 1 ou 2 jours a l'avance et gardee au frais, cette salade n'en sera que meilleure. 1.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 9,
            chunkId: titleId,
            embeddingBasis: "exact_match_v1",
            score: 0.94) with
        {
            ChunkIndex = 3,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            NextChunkId = previousId
        };
        var continuation = TestMatch(
            text: "Procedez de la meme facon avec les ingredients restants. 3. Recouvrez hermetiquement la salade avec un film alimentaire et mettez-la au frais pendant 5 heures minimum. INGREDIENTS 1 oignon doux, 250 g de creme aigre, 120 g de yaourt, 300 g de filets de harengs.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 9,
            chunkId: continuationId,
            embeddingBasis: "linked_context_v1",
            score: 0.88) with
        {
            ChunkIndex = 5,
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = previousId
        };
        var previous = TestMatch(
            text: "Recouvrez les oignons d'eau froide et laissez-les tremper 15 minutes. Egouttez-les puis melangez-les avec la creme aigre, le yaourt, le jus de citron et le sucre. 2. Mettez la moitie des filets de harengs dans un plat et recouvrez-les avec la moitie du melange a base de creme.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 9,
            chunkId: previousId,
            embeddingBasis: "linked_context_v1",
            score: 0.86) with
        {
            ChunkIndex = 4,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72,
            NextChunkId = continuationId
        };

        var selected = new List<RagMatch> { title, continuation };
        var anchors = RetrievalTitleSelection.SelectStructuredContinuationAnchors(query, selected, limit: 4);

        Assert.Contains(anchors, match => match.ChunkId == continuationId);

        var continuations = RetrievalTitleSelection.FilterStructuredContinuationMatches(
            query,
            anchors,
            new[] { previous },
            limit: 4);

        var linked = Assert.Single(continuations);
        Assert.Equal(previousId, linked.ChunkId);
    }

    [Fact]
    public void ReattachStructuredContinuationCompanions_keeps_same_page_chunks_next_to_anchor_after_final_sort()
    {
        var anchorId = Guid.NewGuid().ToString();
        var ingredientsId = Guid.NewGuid().ToString();
        const string query = "Tu peux me faire une fiche claire pour \"Brochettes de poulet grille a l'indonesienne\" : materials, etapes, temps et source ?";
        var anchor = TestMatch(
            text: "Brochettes de poulet grille a l'indonesienne. Pour 6 personnes. 1. Coupez les blancs de poulet en fines lanieres.",
            embedText: "Matched local title token route",
            docPath: "Cuisine/nobilia.pdf",
            page: 42,
            chunkId: anchorId,
            embeddingBasis: "local_title_token_route_v1",
            score: 0.918) with
        {
            ChunkIndex = 51,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            SameSectionChunkId = ingredientsId
        };
        var unrelated = TestMatch(
            text: "INGREDIENTS 6 cuisses de poulet et tomates. PREPARATION faire cuire le poulet.",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            page: 58,
            chunkId: Guid.NewGuid().ToString(),
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918) with
        {
            ChunkIndex = 200,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var ingredients = TestMatch(
            text: "INGREDIENTS 1,5 kg de blancs de poulet. Pour la marinade 3 echalotes, 2 gousses d'ail, gingembre, sauce de soja, vinaigre et huile.",
            docPath: "Cuisine/nobilia.pdf",
            page: 42,
            chunkId: ingredientsId,
            embeddingBasis: "linked_context_v1",
            score: 0.873) with
        {
            ChunkIndex = 54,
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.70,
            PrevChunkId = anchorId
        };
        var selected = new List<RagMatch> { anchor, unrelated, ingredients };

        RetrievalTitleSelection.ReattachStructuredContinuationCompanions(query, selected, topK: 3);

        Assert.Collection(
            selected,
            first => Assert.Equal(anchorId, first.ChunkId),
            second => Assert.Equal(ingredientsId, second.ChunkId),
            third => Assert.Equal(unrelated.ChunkId, third.ChunkId));
    }

    [Theory]
    [InlineData("Prepare a documented onboarding plan for 4 people from the corpus.", "pour4")]
    [InlineData("Compare documented options for 4 people and pick the best candidate.", "pour 4")]
    [InlineData("Build a weekly plan for 6 people from the corpus.", "for 6 people")]
    public void Weak_count_exact_terms_are_pruned_for_broad_synthesis_queries(string query, string term)
    {
        Assert.True(RetrievalChannels.ShouldPruneWeakExactLookupTermsForQuery(query));
        Assert.True(RetrievalChannels.IsWeakCountQuantityExactLookupTerm(term));
    }
}
