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
    public void OrderMatchesForSelection_prefers_explicit_file_doc_path_over_sibling_text_hits()
    {
        var wrongSibling = new RagMatch(
            1.02,
            "doc-wrong",
            "Docs/ABC 12.5.pdf",
            "ABC 12.5.pdf",
            8,
            8,
            "wrong",
            4,
            "Foreword mentioning ABC 12.2 and ABC 12.4 as related standards.",
            1,
            "hash-wrong",
            "Matched title_anchor_route: ABC 12.2\nForeword mentioning ABC 12.2",
            "title_anchor_route_v1",
            null,
            null,
            "ABC 12.2",
            "ABC 12.2",
            "section_window_v1",
            null,
            null,
            null);
        var explicitTarget = new RagMatch(
            0.82,
            "doc-target",
            "Docs/ABC 12.2.pdf",
            "ABC 12.2.pdf",
            1,
            1,
            "target",
            0,
            "OCR cover page with weak title extraction.",
            1,
            "hash-target",
            "Matched explicit_document_title_route: abc 12 2 pdf\nOCR cover page with weak title extraction.",
            "explicit_document_title_route_v1",
            null,
            null,
            "Cover",
            "Cover",
            "section_window_v1",
            null,
            null,
            null);

        var ordered = RetrievalSelection.OrderMatchesForSelection(
            [wrongSibling, explicitTarget],
            prioritizeDocumentProfiles: false,
            query: "Fais un resume prudent de ABC 12.2.pdf.");

        Assert.Equal("target", ordered[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_requested_safety_data_sheet_document_type()
    {
        var genericDataSheet = TestMatch(
            text: "PTFE technical data sheet. Handling precautions are summarized in the general instructions for use.",
            docPath: "Docs/FIT-PTFE Technical Data Sheet.pdf",
            chunkId: "data-sheet",
            score: 0.98);
        var safetyDataSheet = TestMatch(
            text: "PTFE material safety data sheet. Handling precautions include storage, disposal and personal protection notes.",
            docPath: "Docs/MSDS-PTFE Safety Data Sheet.pdf",
            chunkId: "safety-data-sheet",
            score: 0.76);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "safety data sheet handling precautions",
            [genericDataSheet, safetyDataSheet]);

        Assert.Equal("safety-data-sheet", calibrated[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_prioritizes_document_profile_constraint_coverage()
    {
        var genericSafety = TestMatch(
            text: "Document profile about safety colors, signs, sources and confidence notes.",
            docPath: "Docs/ansi-safety-colors.pdf",
            chunkId: "generic-safety",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.92);
        var electricalRequirement = TestMatch(
            text: "Document profile covering electrical standard requirements for industrial machinery.",
            docPath: "Docs/electrical-standard.pdf",
            chunkId: "electrical-requirement",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.84);

        var ordered = RetrievalSelection.OrderMatchesForSelection(
            [genericSafety, electricalRequirement],
            prioritizeDocumentProfiles: false,
            query: "exigence electrique issue d un scan");

        Assert.Equal("electrical-requirement", ordered[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_does_not_put_weak_profiles_before_actionable_content()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";
        var weakProfile = TestMatch(
            "Document profile with generic procedures and setup notes.",
            chunkId: "weak-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.92);
        var content = TestMatch(
            "Procedure for apprentices. Material checklist, failure risk, setup notes and verification steps.",
            chunkId: "actionable-content",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.78);

        var ordered = RetrievalSelection.OrderMatchesForSelection(
            [weakProfile, content],
            prioritizeDocumentProfiles: true,
            query);

        Assert.Equal("actionable-content", ordered[0].ChunkId);
        Assert.Equal(0.0, RetrievalCandidateSignals.ComputeDocumentProfileSelectionConstraintCoverage(
            RetrievalLexicalExpansion.BuildDocumentProfileSpecificityTokens(query),
            weakProfile));
    }

    [Fact]
    public void OrderMatchesForSelection_keeps_supported_profiles_ahead_for_broad_comparisons()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";
        var supportedProfile = TestMatch(
            "Document profile for apprentices with material checklist, failure risk notes and setup controls.",
            chunkId: "supported-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.70);
        var content = TestMatch(
            "Generic procedure with setup notes but no audience or risk details.",
            chunkId: "generic-content",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.90);

        var ordered = RetrievalSelection.OrderMatchesForSelection(
            [content, supportedProfile],
            prioritizeDocumentProfiles: true,
            query);

        Assert.Equal("supported-profile", ordered[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_promotes_card_page_chunk_before_its_profile()
    {
        const string query = "Compare three onboarding procedures for apprentices: material, failure risk and setup notes.";
        var supportedProfile = TestMatch(
            "Document profile for apprentices with material checklist, failure risk notes and setup controls.",
            docPath: "Docs/Apprentices.pdf",
            chunkId: "supported-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.70) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Alpha beta procedure",
                    PageStart: 4,
                    PageEnd: 4,
                    Kind: "unit_lead",
                    Signals: ["structured_facts"])
            ]
        };
        var actionableChunk = TestMatch(
            "Alpha beta procedure. Material checklist: gloves and tray. Failure risk: overheating. Setup notes: cool before handling.",
            docPath: "Docs/Apprentices.pdf",
            page: 4,
            chunkId: "actionable-chunk",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 0.64);

        var ordered = RetrievalSelection.OrderMatchesForSelection(
            [supportedProfile, actionableChunk],
            prioritizeDocumentProfiles: true,
            query);

        Assert.Equal("actionable-chunk", ordered[0].ChunkId);
        Assert.Equal("supported-profile", ordered[1].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_prefers_constraint_covered_content_for_situational_choices()
    {
        const string query = "Which field procedure should I choose for a winter deployment?";
        var genericActionable = TestMatch(
            "Field procedure. Materials, steps, validation and deployment notes.",
            docPath: "Docs/GeneralProcedure.pdf",
            chunkId: "generic",
            score: 0.94);
        var constrained = TestMatch(
            "Winter field procedure. Materials, steps, deployment notes and cold-weather validation.",
            docPath: "Docs/WinterFieldProcedure.pdf",
            chunkId: "winter",
            score: 0.72);

        var ordered = RetrievalSelection.OrderMatchesForSelection(
            [genericActionable, constrained],
            prioritizeDocumentProfiles: false,
            query);

        Assert.Equal("winter", ordered[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_uses_document_and_card_constraints_for_broad_choice_queries()
    {
        var genericDessert = TestMatch(
            "Cheesecake with berries. Ingredients, preparation and serving notes.",
            docPath: "Cuisine/student-desserts.pdf",
            chunkId: "generic-dessert",
            score: 0.94) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Cheesecake aux fraises",
                    Signals: ["quantity_list", "structured_facts", "dessert"])
            ]
        };
        var constrainedDessert = TestMatch(
            "Tarte Tatin. Recettes sucrees, preparation au four et service pour un repas.",
            docPath: "Cuisine/30-recettes-preferees-des-francais.pdf",
            chunkId: "french-dessert",
            score: 0.70) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Tarte Tatin",
                    Signals: ["quantity_list", "structured_facts", "recettes sucrees"])
            ]
        };
        var selected = new List<RagMatch> { genericDessert, constrainedDessert };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Quel dessert francais choisir pour un repas chic ?",
            selected);

        Assert.Equal("french-dessert", selected[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_preserves_catalog_overview_profile_floor()
    {
        const string query = "Quels documents contient cette categorie et a quoi servent-ils chacun ?";
        var profileA = TestMatch(
            "Procurement regulation profile summary.",
            docPath: "Achats/A.pdf",
            chunkId: "profile-a",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.08);
        var profileB = TestMatch(
            "Supplier conduct profile summary.",
            docPath: "Achats/B.pdf",
            chunkId: "profile-b",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.07);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(query, [profileA, profileB], query);

        Assert.All(calibrated, match => Assert.True(match.Score >= 0.46));
    }

    [Fact]
    public void CalibrateFusedMatches_preserves_catalog_overview_profile_floor_for_english_overview()
    {
        const string query = "Can you give me a concise English overview of this category and tell me which documents are useful for real business questions?";
        var profile = TestMatch(
            "Procurement profile summary.",
            docPath: "Achats/A.pdf",
            chunkId: "profile-a",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.05);
        var profileB = TestMatch(
            "Supplier profile summary.",
            docPath: "Achats/B.pdf",
            chunkId: "profile-b",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.05);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "Which documents are available in this category?",
            [profile, profileB],
            query);

        Assert.All(calibrated, match => Assert.True(match.Score >= 0.46));
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_direct_short_technical_property_phrase_over_temperature_neighbor()
    {
        var processingNeighbor = new RagMatch(
            Score: 0.98,
            DocId: "neighbor",
            DocPath: "Docs/reference.pdf",
            DocName: "reference.pdf",
            PageStart: 2,
            PageEnd: 2,
            ChunkId: "neighbor",
            ChunkIndex: 2,
            Text: "To achieve optimum properties, compression molding should be carried out within a temperature range of 23 C to 26 C.",
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: "To achieve optimum properties, compression molding should be carried out within a temperature range of 23 C to 26 C.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Processing recommendations",
            HeadingPath: "Processing recommendations",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            MatchedContentCards: new[]
            {
                new RagMatchedContentCard(
                    Title: "Thermal properties",
                    Kind: "table",
                    Signals: new[] { "thermal", "properties" })
            });
        var directPropertyTable = new RagMatch(
            Score: 0.72,
            DocId: "direct",
            DocPath: "Docs/reference.pdf",
            DocName: "reference.pdf",
            PageStart: 1,
            PageEnd: 1,
            ChunkId: "direct",
            ChunkIndex: 1,
            Text: "Thermal properties Property Value Unit Test Method. Melt point 342 C. Service Temperature Range -200 C to 260 C.",
            IngestionVersion: 1,
            HashDoc: "hash",
            EmbedText: "Thermal properties Property Value Unit Test Method. Melt point 342 C. Service Temperature Range -200 C to 260 C.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Thermal properties",
            HeadingPath: "Technical data > Thermal properties",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            MatchedContentCards: new[]
            {
                new RagMatchedContentCard(
                    Title: "Thermal properties",
                    Kind: "table",
                    Signals: new[] { "thermal", "properties", "temperature", "melt" })
            });

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "proprietes thermiques thermal properties temperature properties",
            [processingNeighbor, directPropertyTable]);
        var orderedForSelection = RetrievalSelection.OrderMatchesForSelection(
            [processingNeighbor, directPropertyTable],
            prioritizeDocumentProfiles: false,
            query: "proprietes thermiques");

        Assert.Equal("direct", calibrated[0].ChunkId);
        Assert.Equal("direct", orderedForSelection[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_sparse_for_high_overlap_lexical_query()
    {
        var sparse = new RagMatch(0.74, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "sparse-1", 0, "Inerting safety controls and gas flow monitoring requirements", 1, "hash-a", "Inerting safety controls and gas flow monitoring requirements", "sparse_bm25_v1", 1, 1, "Inerting", "Chapter 2 > Inerting", "unit_exact_v1", null, null, null);
        var dense = new RagMatch(0.76, "doc-b", "ATEX/General.pdf", "General.pdf", 1, 1, "dense-1", 0, "General safety guidance overview", 1, "hash-b", "General safety guidance overview", "contextual_text_v1", 1, 1, "Overview", "Chapter 1 > Overview", "section_window_v1", null, null, null);

        var calibrated = RetrievalSelection.CalibrateFusedMatches("Quels documents parlent d inerting safety controls en zone ATEX ?", [dense, sparse]);

        Assert.Equal("sparse-1", calibrated[0].ChunkId);
        Assert.Equal("sparse_bm25_v1", calibrated[0].EmbeddingBasis);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_direct_title_chunk_over_neighbor_context_title()
    {
        var neighbor = new RagMatch(1.02, "doc-a", "Ops/Robot.pdf", "Robot.pdf", 66, 66, "neighbor", 10,
            "Mijote details and timings without the requested item title.",
            1,
            "hash-a",
            "document_name: Robot.pdf\nprevious_context:\nBoeuf bourguignon Pour 4 personnes\nexcerpt:\nMijote details and timings without the requested item title.",
            "sparse_bm25_v1",
            1,
            1,
            "Mijote details",
            "Mijote details",
            "unit_exact_v1",
            null,
            null,
            null);
        var direct = new RagMatch(0.91, "doc-b", "Ops/Top30.pdf", "Top30.pdf", 5, 5, "direct", 2,
            "Boeuf bourguignon Pour 4 personnes ingredients and preparation.",
            1,
            "hash-b",
            "Matched profile title: Boeuf bourguignon\nBoeuf bourguignon Pour 4 personnes ingredients and preparation.",
            "sparse_bm25_v1",
            1,
            1,
            "Boeuf bourguignon",
            "Boeuf bourguignon",
            "unit_exact_v1",
            null,
            null,
            null,
            MatchedContentCards: [new RagMatchedContentCard("Boeuf bourguignon")]);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "boeuf bourguignon",
            [neighbor, direct],
            "C'est quoi les grandes etapes du boeuf bourguignon ?");

        Assert.Equal("direct", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_primary_text_title_tokens_over_synthetic_route_title()
    {
        var syntheticRouteNeighbor = new RagMatch(
            0.918,
            "doc-a",
            "Cuisine/si-on-cuisinait.pdf",
            "si-on-cuisinait.pdf",
            30,
            30,
            "synthetic-route-neighbor",
            30,
            "Ingredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre. Materiel saladier couteau planche. Technique ouvrir les boites, rincer les haricots et melanger.",
            1,
            "hash-a",
            "Matched title_anchor_route: Salade de pates\nIngredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre. Materiel saladier couteau planche. Technique ouvrir les boites, rincer les haricots et melanger.",
            "title_anchor_route_v1",
            1,
            1,
            "Fromage en des",
            "Fromage en des",
            "unit_exact_v1",
            null,
            null,
            null);
        var primaryTitleTokens = syntheticRouteNeighbor with
        {
            ChunkId = "primary-title-tokens",
            ChunkIndex = 29,
            PageStart = 29,
            PageEnd = 29,
            Text = "Ingredients 150 g de pates, surimi, feta, olives, poivron, huile, vinaigre, sel et poivre. Materiel casserole passoire saladier. Technique faire cuire les pates, les egoutter, laisser refroidir puis melanger.",
            EmbedText = "Ingredients 150 g de pates, surimi, feta, olives, poivron, huile, vinaigre, sel et poivre. Materiel casserole passoire saladier. Technique faire cuire les pates, les egoutter, laisser refroidir puis melanger.",
            EmbeddingBasis = "local_title_token_route_v1"
        };

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "Tu peux me faire une fiche claire pour « Salade de pâtes » : ingrédients, étapes, temps et source ?",
            [syntheticRouteNeighbor, primaryTitleTokens]);

        Assert.Equal("primary-title-tokens", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_caps_sparse_neighbors_with_partial_quoted_title_coverage()
    {
        var mexicanSalad = new RagMatch(
            0.918,
            "doc-a",
            "Cuisine/si-on-cuisinait.pdf",
            "si-on-cuisinait.pdf",
            30,
            30,
            "mexican-salad",
            30,
            "Ingredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre. Materiel saladier couteau planche. Technique ouvrir les boites, rincer les haricots et melanger.",
            1,
            "hash-a",
            "document_name: si-on-cuisinait.pdf\nprevious_context:\nSalade de pates ingredients pates surimi feta olives poivron.\ncontext:\nIngredients haricots rouges ananas mais poivron olives tomates oignon huile vinaigre moutarde persil sel poivre.",
            "sparse_bm25_v1",
            1,
            1,
            "Fromage en des",
            "Fromage en des",
            "unit_exact_v1",
            null,
            null,
            null) with
        {
            MatchedContentCards = [new RagMatchedContentCard("Salade de pates", Signals: ["structured_facts"])]
        };
        var lentilSalad = mexicanSalad with
        {
            Score = 0.8145,
            ChunkId = "lentil-salad",
            PageStart = 27,
            PageEnd = 27,
            ChunkIndex = 27,
            Text = "Salade de lentilles. Ingredients lentilles artichauts poivrons oignon citron huile moutarde sel poivre. Technique ouvrir les boites et melanger.",
            EmbedText = "Salade de lentilles. Ingredients lentilles artichauts poivrons oignon citron huile moutarde sel poivre. Technique ouvrir les boites et melanger."
        };
        var pastaTimbale = mexicanSalad with
        {
            Score = 0.5985,
            ChunkId = "pasta-timbale",
            PageStart = 52,
            PageEnd = 52,
            ChunkIndex = 52,
            Text = "Ingredients 300 g de pates champignons beurre jambon gruyere. Technique preparer une sauce tomate et cuire au four.",
            EmbedText = "Ingredients 300 g de pates champignons beurre jambon gruyere. Technique preparer une sauce tomate et cuire au four."
        };
        var targetPastaSalad = mexicanSalad with
        {
            Score = 0.918,
            ChunkId = "target-pasta-salad",
            PageStart = 29,
            PageEnd = 29,
            ChunkIndex = 29,
            Text = "Ingredients 150 g de pates surimi feta olives poivron huile vinaigre sel poivre. Materiel casserole passoire saladier. Technique cuire les pates, egoutter, laisser refroidir, melanger et conserver au refrigerateur. © Cemea 2003 Salade de pates3300110033",
            EmbedText = "Ingredients 150 g de pates surimi feta olives poivron huile vinaigre sel poivre. Materiel casserole passoire saladier. Technique cuire les pates, egoutter, laisser refroidir, melanger et conserver au refrigerateur. © Cemea 2003 Salade de pates3300110033",
            EmbeddingBasis = "local_title_token_route_v1",
            ContentDensityScore = 0.88
        };

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "Tu peux me faire une fiche claire pour « Salade de pâtes » : ingrédients, étapes, temps et source ?",
            [mexicanSalad, lentilSalad, pastaTimbale, targetPastaSalad]);

        Assert.Equal("target-pasta-salad", calibrated[0].ChunkId);
        Assert.True(Array.FindIndex(calibrated.ToArray(), match => match.ChunkId == "target-pasta-salad")
            < Array.FindIndex(calibrated.ToArray(), match => match.ChunkId == "mexican-salad"));
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_navigation_chunks_for_content_queries()
    {
        var navigation = new RagMatch(
            0.99,
            "doc-index",
            "Docs/Device.pdf",
            "Device.pdf",
            176,
            176,
            "nav",
            0,
            "Vegetarian items 153Burger vegetarian 154Stuffed cabbage 155Tomato clafoutis 156Vegetable flan 157Potato galette 158Quiche lorraine 186Chocolate cake 187",
            1,
            "hash-index",
            "Vegetarian items 153Burger vegetarian 154Stuffed cabbage 155Tomato clafoutis 156Vegetable flan 157Potato galette 158Quiche lorraine 186Chocolate cake 187",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);
        var content = new RagMatch(
            0.94,
            "doc-recipe",
            "Docs/Recipes.pdf",
            "Recipes.pdf",
            15,
            15,
            "content",
            1,
            "Quiche lorraine ingredients include eggs and bacon. Preparation method: make the dough, fill the tart and bake until golden.",
            1,
            "hash-content",
            "Quiche lorraine ingredients include eggs and bacon. Preparation method: make the dough, fill the tart and bake until golden.",
            "sparse_bm25_v1",
            1,
            1,
            "Guide",
            "Guide",
            "unit_exact_v1",
            null,
            null,
            null);

        var calibrated = RetrievalSelection.CalibrateFusedMatches("Compare les quiches lorraines du corpus : ingredients et methode.", [navigation, content]);

        Assert.Equal("content", calibrated[0].ChunkId);
        Assert.True(RetrievalCandidateSignals.LooksLikeNavigationalChunk(navigation));
    }

    [Fact]
    public void CalibrateFusedMatches_promotes_structured_answer_unit_over_sources_and_indexes()
    {
        var sourceList = new RagMatch(
            1.02,
            "doc-source",
            "Cuisine/Sources.pdf",
            "Sources.pdf",
            52,
            55,
            "sources",
            63,
            "Ma boite http://maboite.qc.ca MAPAQ https://mapaq.gouv.qc.ca Maxi https://maxi.ca Metro https://metro.ca Naître et grandir https://naitreetgrandir.com/fr Sauce bechamel",
            1,
            "hash-source",
            "Ma boite http://maboite.qc.ca MAPAQ https://mapaq.gouv.qc.ca Maxi https://maxi.ca Metro https://metro.ca Naître et grandir https://naitreetgrandir.com/fr Sauce bechamel",
            "sparse_bm25_v1",
            1,
            1,
            "Sources",
            "Sources",
            "section_window_v1",
            null,
            null,
            null);
        var index = new RagMatch(
            1.02,
            "doc-index",
            "Cuisine/Index.pdf",
            "Index.pdf",
            7,
            7,
            "index",
            7,
            "Sauce bechamel 43 Sauce tomate 44 Sauce fromage 45 Sauce rosee 46 Sauce moutarde 47 Sauce pizza 48",
            1,
            "hash-index",
            "Sauce bechamel 43 Sauce tomate 44 Sauce fromage 45 Sauce rosee 46 Sauce moutarde 47 Sauce pizza 48",
            "sparse_bm25_v1",
            1,
            1,
            "Sommaire",
            "Sommaire",
            "unit_exact_v1",
            null,
            null,
            null);
        var recipe = new RagMatch(
            0.88,
            "doc-recipe",
            "Cuisine/Recipes.pdf",
            "Recipes.pdf",
            55,
            55,
            "recipe",
            131,
            "SAUCE BECHAMEL DE BASE Ingredients 45 ml de beurre 45 ml de farine 500 ml de lait Preparation 1. Faire fondre le beurre. Ajouter la farine puis le lait.",
            1,
            "hash-recipe",
            "SAUCE BECHAMEL DE BASE Ingredients 45 ml de beurre 45 ml de farine 500 ml de lait Preparation 1. Faire fondre le beurre. Ajouter la farine puis le lait.",
            "sparse_bm25_v1",
            1,
            1,
            "Sauce bechamel",
            "Sauce bechamel",
            "unit_exact_v1",
            null,
            null,
            null);

        var calibrated = RetrievalSelection.CalibrateFusedMatches("Je veux une sauce bechamel", [sourceList, index, recipe]);

        Assert.True(RetrievalCandidateSignals.LooksLikeStrongNavigationalChunk(index));
        Assert.Equal("recipe", calibrated[0].ChunkId);
        Assert.True(calibrated[0].Score > calibrated[1].Score);
        Assert.True(calibrated.Single(match => match.ChunkId == "index").Score <= 0.68);
    }

    [Fact]
    public void CalibrateFusedMatches_uses_quoted_title_coverage_to_break_capped_score_ties()
    {
        var partial = new RagMatch(
            1.02,
            "doc-partial",
            "Cuisine/chefbot.pdf",
            "chefbot.pdf",
            112,
            112,
            "partial",
            171,
            "Haricots verts vapeur avec huile d'olive et citron.",
            IngestionVersion: 1,
            HashDoc: "a",
            EmbedText: "Haricots verts vapeur avec huile d'olive et citron.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: "Document",
            HeadingPath: "Document",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);
        var target = partial with
        {
            DocId = "doc-target",
            DocPath = "Cuisine/si-on-cuisinait.pdf",
            DocName = "si-on-cuisinait.pdf",
            ChunkId = "target",
            ChunkIndex = 53,
            PageStart = 28,
            PageEnd = 28,
            Text = "Ingrédients: haricots verts, avocat, tomates. Technique: préparer la sauce. Salade de haricotsverts à l'avocat.",
            EmbedText = "Document: si-on-cuisinait.pdf\nExcerpt: Ingrédients: haricots verts, avocat, tomates. Technique: préparer la sauce."
        };

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "Tu peux me faire une fiche claire pour « Salade de haricots verts à l'avocat » : ingredients, etapes, temps et source ?",
            [partial, target]);

        Assert.Equal("target", calibrated[0].ChunkId);

        var selected = new List<RagMatch> { partial, target };
        RetrievalSelection.PrioritizeQuotedTitleSelections(
            "Tu peux me faire une fiche claire pour « Salade de haricots verts à l'avocat » : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("target", selected[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_uses_supplemental_comparative_phrase_terms()
    {
        const string query = "Compare trois plats mijot\u00e9s fran\u00e7ais et dis lequel choisir pour un repas de famille.";
        var generic = new RagMatch(
            Score: 0.70,
            DocId: "doc-generic",
            DocPath: "Generic/Overview.pdf",
            DocName: "Overview.pdf",
            PageStart: 12,
            PageEnd: 12,
            ChunkId: "generic",
            ChunkIndex: 12,
            Text: "Guide familial avec plusieurs plats fran\u00e7ais et des conseils de service.",
            IngestionVersion: 1,
            HashDoc: "hash-generic",
            EmbedText: "Guide familial avec plusieurs plats fran\u00e7ais et des conseils de service.",
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: 1,
            UnitOrdinal: 1,
            SectionTitle: "Guide",
            HeadingPath: "Guide",
            ChunkType: "unit_exact_v1",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null,
            ContentRole: "content",
            ContentDensityScore: 0.72);
        var target = generic with
        {
            Score = 0.69,
            DocId = "doc-target",
            DocPath = "Target/Recipe.pdf",
            DocName = "Recipe.pdf",
            PageStart = 34,
            PageEnd = 34,
            ChunkId = "target",
            ChunkIndex = 34,
            Text = "Preparation du plat francais: couvrez et laissez mijoter le plat 45 minutes a petit feu. Ingredients: viande, vin, aromates.",
            EmbedText = "Preparation du plat francais: couvrez et laissez mijoter le plat 45 minutes a petit feu. Ingredients: viande, vin, aromates."
        };

        var calibrated = RetrievalSelection.CalibrateFusedMatches(query, [generic, target]);

        Assert.Equal("target", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_boosts_serving_fit_chunks_with_multiple_specific_anchors()
    {
        var genericSauce = new RagMatch(
            0.98,
            "doc-generic",
            "Cuisine/Sauces.pdf",
            "Sauces.pdf",
            18,
            18,
            "generic",
            18,
            "SAUCE MOUTARDE Ingredients moutarde vinaigre huile sel poivre Realisation melanger dans un bol.",
            1,
            "hash-generic",
            "SAUCE MOUTARDE Ingredients moutarde vinaigre huile sel poivre Realisation melanger dans un bol.",
            "sparse_bm25_v1",
            1,
            1,
            "Sauces",
            "Sauces",
            "section_window_v1",
            null,
            null,
            null);
        var steakSauce = new RagMatch(
            0.88,
            "doc-steak",
            "Cuisine/Robot.pdf",
            "Robot.pdf",
            121,
            121,
            "steak-sauce",
            306,
            "1 c. a c. de poivre concasse 10 cl de creme liquide. Ajoutez l'eau puis lancez la cuisson. Servez avec une entrecote. SAUCE AU POIVRE.",
            1,
            "hash-steak",
            "1 c. a c. de poivre concasse 10 cl de creme liquide. Ajoutez l'eau puis lancez la cuisson. Servez avec une entrecote. SAUCE AU POIVRE.",
            "sparse_bm25_v1",
            1,
            1,
            "Sauces",
            "Sauces",
            "section_window_v1",
            null,
            null,
            null);
        var retrievalQuery = RetrievalLexicalExpansion.ExpandRetrievalQuery("Quelle sauce irait bien avec une entrecote ?", "cuisine");

        var calibrated = RetrievalSelection.CalibrateFusedMatches(retrievalQuery, [genericSauce, steakSauce]);

        Assert.Equal("steak-sauce", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_breaks_capped_ties_with_specific_anchor_coverage()
    {
        var genericMarinade = new RagMatch(
            1.02,
            "doc-generic",
            "Cuisine/Marinades.pdf",
            "Marinades.pdf",
            46,
            46,
            "generic-marinade",
            46,
            "Marinade pour poulet avec mayonnaise, paprika, sel et poivre.",
            1,
            "hash-generic",
            "Marinade pour poulet avec mayonnaise, paprika, sel et poivre.",
            "sparse_bm25_v1",
            1,
            1,
            "Marinades",
            "Marinades",
            "unit_exact_v1",
            null,
            null,
            null);
        var steakSauce = genericMarinade with
        {
            DocId = "doc-steak",
            DocPath = "Cuisine/Robot.pdf",
            DocName = "Robot.pdf",
            ChunkId = "steak-sauce",
            ChunkIndex = 121,
            PageStart = 121,
            PageEnd = 121,
            Text = "Sauce au poivre avec creme, fond de veau et cognac. Servez avec une entrecote.",
            EmbedText = "Sauce au poivre avec creme, fond de veau et cognac. Servez avec une entrecote.",
            ChunkType = "section_window_v1"
        };
        var retrievalQuery = RetrievalLexicalExpansion.ExpandRetrievalQuery("Quelle sauce irait bien avec une entrecote ?", "cuisine");

        var calibrated = RetrievalSelection.CalibrateFusedMatches(retrievalQuery, [genericMarinade, steakSauce]);

        Assert.Equal("steak-sauce", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_ignores_answer_format_words_for_specific_recipe_queries()
    {
        var genericOnionRecipe = new RagMatch(
            1.02,
            "doc-generic",
            "Cuisine/Other.pdf",
            "Other.pdf",
            83,
            83,
            "generic-onion",
            0,
            "Ingredients: oignons grelots, sucre, vinaigre. Preparation: cuire les oignons.",
            1,
            "hash-generic",
            "Matched profile title: Oignons caramelises\nIngredients: oignons grelots, sucre, vinaigre. Preparation: cuire les oignons.",
            "sparse_bm25_v1",
            1,
            1,
            "Oignons",
            "Oignons",
            "unit_exact_v1",
            null,
            null,
            null);
        var specificRecipe = new RagMatch(
            0.82,
            "doc-rumsteck",
            "Cuisine/Grill.pdf",
            "Grill.pdf",
            38,
            38,
            "rumsteck-oignons",
            0,
            "Ingredients: 1 gros oignon, paprika, farine, huile vegetale, 2 rumstecks. Preparation: peler les oignons et faire cuire les rumstecks.",
            1,
            "hash-rumsteck",
            "Matched profile title: Rumsteck aux oignons grilles\nIngredients: 1 gros oignon, paprika, farine, huile vegetale, 2 rumstecks. Preparation: peler les oignons et faire cuire les rumstecks.",
            "sparse_bm25_v1",
            1,
            1,
            "Rumsteck aux oignons",
            "Poissons et viandes > Rumsteck aux oignons",
            "unit_exact_v1",
            null,
            null,
            null);

        var query = "Tu peux me faire une fiche claire pour Rumsteck grille : details, etapes, temps et source ?";
        var tokens = RetrievalLexicalExpansion.ExtractLexicalQueryTokens(query);
        var calibrated = RetrievalSelection.CalibrateFusedMatches(query, [genericOnionRecipe, specificRecipe]);

        Assert.DoesNotContain("fiche", tokens);
        Assert.DoesNotContain("claire", tokens);
        Assert.DoesNotContain("etapes", tokens);
        Assert.DoesNotContain("source", tokens);
        Assert.Contains("rumsteck", tokens);
        Assert.True(RetrievalCandidateSignals.HasProfileTitleHint(specificRecipe));
        Assert.True(RetrievalCandidateSignals.HasProfileTitleHint(genericOnionRecipe));
        Assert.True(RetrievalCandidateSignals.RequiresPrimarySpecificLexicalAnchor(tokens));
        Assert.True(RetrievalCandidateSignals.ContainsPrimarySpecificLexicalAnchor(tokens, specificRecipe.EmbedText));
        Assert.False(RetrievalCandidateSignals.ContainsPrimarySpecificLexicalAnchor(tokens, genericOnionRecipe.EmbedText));
        Assert.Equal("rumsteck-oignons", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_domain_anchor_over_generic_requirement_overlap()
    {
        const string query = "On installe une sorbonne en laboratoire : quelles exigences et quels tests sur site dois-je verifier ?";
        var genericRequirements = TestMatch(
            text: "General structural requirements, calculation basis and durability requirements for equipment.",
            docPath: "Standards/General.pdf",
            chunkId: "generic-requirements",
            score: 0.64);
        var anchoredProcedure = TestMatch(
            text: "Fume cupboards for laboratories: safety and performance requirements. On site test methods define checks to be performed by the user.",
            docPath: "Standards/LabEquipment.pdf",
            chunkId: "anchored-procedure",
            score: 0.60);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(query, [genericRequirements, anchoredProcedure]);

        Assert.Equal("anchored-procedure", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_keeps_dense_index_below_recipe_chunk()
    {
        var recipe = new RagMatch(
            0.88,
            "doc-recipe",
            "Cuisine/Classiques.pdf",
            "Classiques.pdf",
            5,
            5,
            "recipe",
            2,
            "Boeuf bourguignon Pour 4 personnes. Ingredients boeuf champignons lardons carottes oignons vin rouge ail bouquet garni. Preparation degraisser la viande puis faire revenir et mijoter.",
            1,
            "hash-recipe",
            "Boeuf bourguignon Pour 4 personnes. Ingredients boeuf champignons lardons carottes oignons vin rouge ail bouquet garni. Preparation degraisser la viande puis faire revenir et mijoter.",
            "sparse_bm25_v1",
            1,
            1,
            "Classiques",
            "Classiques > Boeuf bourguignon",
            "unit_exact_v1",
            null,
            null,
            null);
        var indexChunk = new RagMatch(
            1.02,
            "doc-index",
            "Cuisine/International.pdf",
            "International.pdf",
            159,
            159,
            "index",
            234,
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41Canard en feuille de riz 156Cannelloni aux epinards 102Carpaccio 93Churros avec sauce au chocolat 89Coq au vin 68Creme brulee 72",
            1,
            "hash-index",
            "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41Canard en feuille de riz 156Cannelloni aux epinards 102Carpaccio 93Churros avec sauce au chocolat 89Coq au vin 68Creme brulee 72",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);

        var calibrated = RetrievalSelection.CalibrateFusedMatches("boeuf bourguignon grandes etapes", [indexChunk, recipe]);

        Assert.Equal("recipe", calibrated[0].ChunkId);
        Assert.True(calibrated.Single(match => match.ChunkId == "index").Score < 0.70);
    }

    [Fact]
    public void CalibrateFusedMatches_prioritizes_exact_title_candidate_over_loose_term_overlap()
    {
        var looseOverlap = new RagMatch(
            1.02,
            "doc-loose",
            "Cuisine/International.pdf",
            "International.pdf",
            135,
            135,
            "loose",
            1,
            "Falafels servis avec une sauce concombre et quelques feuilles de laitue romaine.",
            1,
            "hash-loose",
            "Falafels servis avec une sauce concombre et quelques feuilles de laitue romaine.",
            "sparse_bm25_v1",
            1,
            1,
            "Falafels",
            "Falafels",
            "section_window_v1",
            null,
            null,
            null);
        var exactTitle = new RagMatch(
            1.02,
            "doc-exact",
            "Cuisine/si-on-cuisinait.pdf",
            "si-on-cuisinait.pdf",
            33,
            33,
            "exact-title",
            2,
            "Materials: 2 concombres, creme, moutarde. Procedure: melanger et servir frais. CONCOMBRES\u00e0 LA ROMAINE110077",
            1,
            "hash-exact",
            "Materials: 2 concombres, creme, moutarde. Procedure: melanger et servir frais. CONCOMBRES\u00e0 LA ROMAINE110077",
            "sparse_bm25_v1",
            1,
            1,
            "Document",
            "Document",
            "unit_exact_v1",
            null,
            null,
            null);

        var calibrated = RetrievalSelection.CalibrateFusedMatches("Concombres a la romaine", [looseOverlap, exactTitle]);
        var filtered = RetrievalSelection.SuppressNavigationalNoise("Concombres a la romaine", calibrated);

        Assert.Equal("exact-title", calibrated[0].ChunkId);
        Assert.Equal("exact-title", filtered[0].ChunkId);
        Assert.True(RetrievalCandidateSignals.ComputeExactTitleCandidateScore("Concombres a la romaine", exactTitle)
            > RetrievalCandidateSignals.ComputeExactTitleCandidateScore("Concombres a la romaine", looseOverlap));
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_dense_noise_when_lexical_anchor_exists()
    {
        var sparseAnchor = new RagMatch(0.91, "doc-a", "ATEX/CEN.pdf", "CEN.pdf", 1, 1, "sparse-anchor", 0, "Inerting prevents explosion by controlling oxygen concentration and purge conditions.", 1, "hash-a", "Inerting prevents explosion by controlling oxygen concentration and purge conditions.", "sparse_bm25_v1", 1, 1, "Inerting", "Inerting", "unit_exact_v1", null, null, null);
        var denseNoise = new RagMatch(0.98, "doc-b", "Kitchen/Menu.pdf", "Menu.pdf", 1, 1, "dense-noise", 0, "Balanced meals, vegetables and weekly menu planning.", 1, "hash-b", "Balanced meals, vegetables and weekly menu planning.", "contextual_text_v1", 1, 1, "Meals", "Meals", "unit_exact_v1", null, null, null);

        var calibrated = RetrievalSelection.CalibrateFusedMatches("Quels documents parlent d inerting explosion oxygen ?", [denseNoise, sparseAnchor]);

        Assert.Equal("sparse-anchor", calibrated[0].ChunkId);
        Assert.Equal("sparse_bm25_v1", calibrated[0].EmbeddingBasis);
    }

    [Fact]
    public void CalibrateFusedMatches_keeps_exact_reference_above_dense_for_reference_query()
    {
        var exact = new RagMatch(0.96, "doc-a", "ATEX/CEN TR 15281 2006.pdf", "CEN TR 15281 2006.pdf", null, null, "exact-1", -1, "EN 15281", 1, "hash-a", "EN 15281", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);
        var dense = new RagMatch(0.98, "doc-a", "ATEX/CEN TR 15281 2006.pdf", "CEN TR 15281 2006.pdf", 3, 3, "dense-1", 1, "Maintenance guidance around EN 15281", 1, "hash-a", "Maintenance guidance around EN 15281", "contextual_text_v1", 1, 1, "Maintenance", "Chapter 3 > Maintenance", "unit_exact_v1", null, null, null);

        var calibrated = RetrievalSelection.CalibrateFusedMatches("Ou trouve-t-on EN 15281 ?", [dense, exact]);

        Assert.Equal("exact-1", calibrated[0].ChunkId);
        Assert.Equal("exact_match_v1", calibrated[0].EmbeddingBasis);
    }

    [Fact]
    public void CalibrateFusedMatches_keeps_numeric_exact_reference_above_unrelated_dense_noise()
    {
        var exact = new RagMatch(0.965, "doc-a", "ATEX/CEN TR 15281 2006 Guidance on inerting.pdf", "CEN TR 15281 2006 Guidance on inerting.pdf", null, null, "exact-15281", -1, "15281", 1, "hash-a", "15281", "exact_match_v1", null, null, null, null, "document_metadata_ref", null, null, null);
        var denseNoise = new RagMatch(0.985, "doc-b", "General/Accord sur le transfert du code source.pdf", "Accord sur le transfert du code source.pdf", 2, 2, "dense-15281-noise", 0, "general maintenance guidance around standard references", 1, "hash-b", "general maintenance guidance around standard references", "contextual_text_v1", 1, 1, "Integration", "Chapter 2 > Integration", "unit_exact_v1", null, null, null);

        var calibrated = RetrievalSelection.CalibrateFusedMatches("Ou trouve-t-on 15281 ?", [denseNoise, exact]);

        Assert.Equal("exact-15281", calibrated[0].ChunkId);
        Assert.Equal("exact_match_v1", calibrated[0].EmbeddingBasis);
        Assert.Contains("15281", $"{calibrated[0].DocName} {calibrated[0].DocPath}", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_sparse_noise_when_lexical_overlap_is_low()
    {
        var sparseNoise = new RagMatch(0.79, "doc-a", "ATEX/Appendix.pdf", "Appendix.pdf", 1, 1, "sparse-noise", 0, "general safety appendix summary", 1, "hash-a", "general safety appendix summary", "sparse_bm25_v1", 1, 1, "Appendix", "Appendix > Summary", "unit_exact_v1", null, null, null);
        var denseRelevant = new RagMatch(0.77, "doc-b", "Maintenance/Consignation.pdf", "Consignation.pdf", 2, 2, "dense-relevant", 0, "procedure de consignation electrique et verrouillage", 1, "hash-b", "procedure de consignation electrique et verrouillage", "contextual_text_v1", 1, 1, "Procedure", "Chapter 2 > Procedure", "unit_exact_v1", null, null, null);

        var calibrated = RetrievalSelection.CalibrateFusedMatches("Resume la procedure de consignation electrique.", [sparseNoise, denseRelevant]);

        Assert.Equal("dense-relevant", calibrated[0].ChunkId);
        Assert.Equal("contextual_text_v1", calibrated[0].EmbeddingBasis);
    }

    [Fact]
    public void PrioritizeQuotedTitleSelections_prefers_local_actionable_content_over_route_title_list()
    {
        var titleList = TestMatch(
            text: "Contents Safety Valves 12 Gamma Delta 18 Calibration Steps 31 Backup Restore 42 Control Cabinet 57",
            embedText: "Matched title_anchor_route: Safety Valves\nContents Safety Valves 12 Gamma Delta 18 Calibration Steps 31 Backup Restore 42 Control Cabinet 57",
            page: 2,
            score: 1.02,
            chunkId: "title-list",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1") with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.76,
            ContentDensityScore = 0.30
        };
        var actionablePage = TestMatch(
            text: "Safety Valve. Materials: gasket wrench sealant. Procedure: 1. Isolate pressure. 2. Replace the valve. 3. Leak-test the assembly.",
            embedText: "Matched direct_title_token_route: safety valves\nSafety Valve. Materials: gasket wrench sealant. Procedure: 1. Isolate pressure. 2. Replace the valve. 3. Leak-test the assembly.",
            page: 12,
            score: 0.98,
            chunkId: "actionable-page",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "unit_exact_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.95
        };
        var selected = new List<RagMatch> { titleList, actionablePage };

        RetrievalSelection.PrioritizeQuotedTitleSelections("Give me a clear sheet for \"Safety Valves\".", selected);

        Assert.Equal("actionable-page", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeQuotedTitleSelections_prefers_exact_local_phrase_over_dense_token_overlap()
    {
        var denseTokenOverlap = TestMatch(
            text: "Procedure. The pressure circuit uses a calibrated relief spring and valve body. Materials: wrench gasket sealant. Steps: isolate, replace, test.",
            embedText: "Matched direct_title_token_route: pressure relief\nProcedure. The pressure circuit uses a calibrated relief spring and valve body.",
            page: 4,
            score: 1.02,
            chunkId: "dense-token-overlap",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "unit_exact_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 1.0
        };
        var exactLocalPhrase = TestMatch(
            text: "PRESSURE RELIEF. Materials: gauge valve gasket. Procedure: 1. Depressurize. 2. Inspect the relief path. 3. Record the setting.",
            embedText: "Matched title_anchor_route: Pressure Relief\nPRESSURE RELIEF. Materials: gauge valve gasket. Procedure: 1. Depressurize.",
            page: 9,
            score: 0.94,
            chunkId: "exact-local-phrase",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "unit_exact_v1") with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.72
        };
        var selected = new List<RagMatch> { denseTokenOverlap, exactLocalPhrase };

        RetrievalSelection.PrioritizeQuotedTitleSelections("Give me a clear sheet for \"Pressure Relief\".", selected);

        Assert.Equal("exact-local-phrase", selected[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_route_exact_label_over_loose_overlap()
    {
        var looseOverlap = TestMatch(
            text: "The beta appendix references alpha separately without a combined title.",
            embedText: "The beta appendix references alpha separately without a combined title.",
            chunkId: "loose-overlap",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "section_window_v1",
            score: 1.02);
        var navigationRoute = TestMatch(
            text: "Alpha Beta. This page has the operational details and confirms the title from navigation.",
            embedText: "Matched navigation_route: Alpha Beta\nAlpha Beta. This page has the operational details and confirms the title from navigation.",
            chunkId: "navigation-route",
            embeddingBasis: "navigation_route_v1",
            chunkType: "section_window_v1",
            score: 0.86);

        var calibrated = RetrievalSelection.CalibrateFusedMatches("alpha beta", [looseOverlap, navigationRoute]);

        Assert.Equal("navigation-route", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_direct_title_token_route_over_partial_sparse_overlap()
    {
        var partialSparse = TestMatch(
            text: "Broccoli noodles with a bacon garnish and unrelated notes.",
            embedText: "Broccoli noodles with a bacon garnish and unrelated notes.",
            page: 8,
            chunkId: "partial-sparse",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.96);
        var directRoute = TestMatch(
            text: "ONE POT PASTA BROCOLI DINDE ET BACONPLATS PRINCIPAUX. Ingredients: pasta, broccoli, cooked turkey and bacon.",
            embedText: "Matched direct_title_token_route: ONE POT PASTA BROCOLI DINDE ET BACON\nONE POT PASTA BROCOLI DINDE ET BACONPLATS PRINCIPAUX. Ingredients: pasta, broccoli, cooked turkey and bacon.",
            page: 44,
            chunkId: "direct-route",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 0.88);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "one pot pasta brocoli dinde bacon",
            [partialSparse, directRoute]);

        Assert.Equal("direct-route", calibrated[0].ChunkId);
        Assert.True(RetrievalSelection.IsResolvedTitleOrNavigationRoute(calibrated[0]));
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_direct_title_route_when_title_is_only_ingredient()
    {
        var ingredientMention = TestMatch(
            text: "Osso buco Pour 4 personnes 1,5 kg de jarret de veau, 1 kg de tomates, farine, vin blanc, oignons, carottes, celeri, bouquet garni, ail, persil, thym, laurier, sel, poivre, puis 75 cl de bouillon de volaille. Preparation: mijoter longuement.",
            embedText: "Matched direct_title_token_route: Bouillon de volaille\nOsso buco ingredient list with bouillon de volaille.",
            docPath: "Cuisine/Plats.pdf",
            chunkId: "ingredient-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 1.02);
        var titleChunk = TestMatch(
            text: "Bouillon de volaille. Ingredients: carcasse de volaille, carotte, oignon, celeri et bouquet garni. Preparation: couvrir d'eau, mijoter, filtrer puis refroidir.",
            embedText: "Bouillon de volaille. Ingredients and preparation.",
            docPath: "Cuisine/Base.pdf",
            chunkId: "title-chunk",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "Bouillon de volaille source",
            [ingredientMention, titleChunk]);

        Assert.Equal("title-chunk", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_card_confirmed_direct_title_route_over_ingredient_mention()
    {
        var ingredientMention = TestMatch(
            text: "Osso buco Pour 4 personnes 1,5 kg de jarret de veau, 1 kg de tomates, farine, vin blanc, oignons, carottes, celeri, bouquet garni, ail, persil, thym, laurier, sel, poivre, puis 75 cl de bouillon de volaille. Preparation: mijoter longuement.",
            embedText: "Matched direct_title_token_route: Bouillon de volaille\nOsso buco ingredient list with bouillon de volaille.",
            docPath: "Cuisine/Plats.pdf",
            chunkId: "ingredient-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 1.02);
        var cardBackedRoute = TestMatch(
            text: "Grains de poivre. Ingredients: carcasse de volaille, legumes, aromates et eau froide. Preparation: couvrir, mijoter, filtrer puis refroidir.",
            embedText: "Matched direct_title_token_route: Bouillon de volaille\nGrains de poivre and bouillon sauce content card.",
            docPath: "Cuisine/Je_cuisine_simplement.pdf",
            chunkId: "card-backed-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.87) with
        {
            MatchedContentCards = [new RagMatchedContentCard("BOUILLON DE VOLAILLE SAUCES")]
        };

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "Bouillon de volaille source",
            [ingredientMention, cardBackedRoute]);

        Assert.Equal("card-backed-route", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_short_suffix_title_phrase()
    {
        var generic = TestMatch(
            text: "Alpha procedure materials and steps from a generic guide.",
            docPath: "Docs/general.pdf",
            chunkId: "generic",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.02);
        var shortSuffix = TestMatch(
            text: "ALPHA A JD. Materials: gasket and wrench. Procedure: inspect and record.",
            embedText: "Matched title_anchor_route: Alpha a JD\nALPHA A JD. Materials: gasket and wrench. Procedure: inspect and record.",
            docPath: "Docs/short-suffix.pdf",
            chunkId: "short-suffix",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.91);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "alpha a jd",
            [generic, shortSuffix],
            "C'est quoi l'alpha a JD ?");

        Assert.Equal("short-suffix", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_uses_terminal_short_title_disambiguator()
    {
        var modeB = TestMatch(
            text: "Access Mode B. Procedure: validate the alternate channel and record the mode B result.",
            embedText: "Matched title_anchor_route: Access Mode B\nAccess Mode B. Procedure: validate the alternate channel.",
            docPath: "Docs/mode-b.pdf",
            chunkId: "mode-b",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.02);
        var modeA = TestMatch(
            text: "Access Mode A. Procedure: enable the primary channel and record the mode A result.",
            embedText: "Matched title_anchor_route: Access Mode A\nAccess Mode A. Procedure: enable the primary channel.",
            docPath: "Docs/mode-a.pdf",
            chunkId: "mode-a",
            embeddingBasis: "title_anchor_route_v1",
            score: 0.91);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "access mode a",
            [modeB, modeA],
            "Give me the procedure for access mode A from the manual.");

        Assert.Equal("mode-a", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_document_hint_match_for_homonymous_titles()
    {
        var otherBook = TestMatch(
            text: "Coq au vin ingredients and steps from a generic recipe book.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "other-book",
            embeddingBasis: "dense_qdrant_v1",
            score: 0.72);
        var hintedBook = TestMatch(
            text: "Coq au vin ingredients and steps from the international collection.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            chunkId: "hinted-book",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.62);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "coq au vin",
            [otherBook, hintedBook],
            "Donne-moi la recette du coq au vin dans le livre international.");

        Assert.Equal("hinted-book", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_document_hint_after_document_type_reference()
    {
        var genericGuide = TestMatch(
            text: "Compote de pommes ingredients and steps from a generic guide.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "generic-guide",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.02);
        var hintedGuide = TestMatch(
            text: "Compote de pommes ingredients and steps from the named guide.",
            docPath: "Cuisine/facilitemps.pdf",
            chunkId: "hinted-guide",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.02);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "compote de pommes",
            [genericGuide, hintedGuide],
            "Comment faire la compote de pommes du guide Facilitemps ?");

        Assert.Equal("hinted-guide", calibrated[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_document_hint_over_homonymous_exact_title()
    {
        var genericExact = TestMatch(
            text: "COMPOTE DE POMMES Ingredients and steps from another recipe book.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "generic-exact",
            embeddingBasis: "exact_match_v1",
            chunkType: "exact_match_entry",
            score: 1.02);
        var hintedGuide = TestMatch(
            text: "COMPOTE DE POMMES DE BASE Ingredients and steps from the named guide.",
            docPath: "Cuisine/facilitemps.pdf",
            chunkId: "hinted-guide",
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 0.918);
        var selected = new List<RagMatch> { genericExact, hintedGuide };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Comment faire la compote de pommes du guide Facilitemps ?",
            selected);

        Assert.Equal("hinted-guide", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_uses_candidate_document_name_as_implicit_hint()
    {
        var otherRobot = TestMatch(
            text: "PAIN SANS GLUTEN AUX GRAINES ET HOUMOUS AUX PETITS POIS Lancez le programme pate.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "other-robot",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918);
        var hintedButOffTopic = TestMatch(
            text: "BECHAMEL Ingredients lait, farine, beurre et noix de muscade. Placer le fouet dans le recipient.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "hinted-off-topic",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.94);
        var chefbot = TestMatch(
            text: "HOUMOUS DE BETTERAVE Preparation au robot culinaire avec pois chiches, tahini, ail, citron vert et eau glacee.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "chefbot",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.86);
        var selected = new List<RagMatch> { otherRobot, hintedButOffTopic, chefbot };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Je veux faire le houmous au Chefbot, tu me sors les etapes ?",
            selected);

        Assert.Equal("chefbot", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_does_not_treat_common_title_words_as_implicit_document_hints()
    {
        var genericRice = TestMatch(
            text: "INGREDIENTS PREPARATION Pour 5 personnes 240 g de riz basmati et 1 l d'eau. Servir pour accompagner un ragout ou un plat au curry.",
            docPath: "Cuisine/curry-basics.pdf",
            chunkId: "generic-rice",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var focusedCurry = TestMatch(
            text: "CURRY DE CREVETTES A L'ANANAS. Ajoutez les crevettes, le lait de coco puis salez et poivrez. Servez le curry de crevettes avec du riz basmati.",
            docPath: "Cuisine/robot-guide.pdf",
            chunkId: "focused-curry",
            embeddingBasis: "linked_context_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var selected = new List<RagMatch> { genericRice, focusedCurry };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Detaille le curry de crevettes et riz basmati.",
            selected);

        Assert.Equal("focused-curry", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_demotes_linked_context_that_starts_mid_procedure()
    {
        var continuation = TestMatch(
            text: "BOEUF BOURGUIGNON Paris et cuire encore 30 min. 6 Degustez chaud avec des pommes vapeur.",
            docPath: "Cuisine/moulinex.pdf",
            chunkId: "linked-continuation",
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.918);
        var complete = TestMatch(
            text: "Boeuf bourguignon Pour 4 personnes ingredients. Preparation: degraisser la viande, saisir, ajouter les legumes, le vin et laisser mijoter.",
            docPath: "Cuisine/30-recettes-preferees-des-francais.pdf",
            chunkId: "complete-recipe",
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 1.02);
        var selected = new List<RagMatch> { continuation, complete };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "C'est quoi les grandes etapes du boeuf bourguignon ?",
            selected);

        Assert.Equal("complete-recipe", selected[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_uses_trailing_acronym_as_implicit_document_hint()
    {
        var genericGuide = TestMatch(
            text: "Alpha beta ingredients and settings from a generic guide.",
            docPath: "Docs/general-guide.pdf",
            chunkId: "generic-guide",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91);
        var hintedGuide = TestMatch(
            text: "Alpha beta ingredients and settings from the named guide.",
            docPath: "Docs/VND-service-guide.pdf",
            chunkId: "hinted-guide",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.88);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "alpha beta",
            [genericGuide, hintedGuide],
            "Pour les alpha beta VND, quels sont les reglages ?");

        Assert.Equal("hinted-guide", calibrated[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_tolerates_single_letter_document_hint_typo()
    {
        var genericProfile = TestMatch(
            text: "Document profile for a broad guide with desserts, planning and quick meals.",
            docPath: "Docs/family-planning-guide.pdf",
            chunkId: "generic-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.97);
        var hintedProfile = TestMatch(
            text: "Document profile for a short labelled collection with desserts and balanced meals.",
            docPath: "Docs/livre-recette-sist-2025-web.pdf",
            chunkId: "hinted-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.82);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "compare desserts sante",
            [genericProfile, hintedProfile],
            "Compare les desserts sante du PDF SIIST.");

        Assert.Equal("hinted-profile", calibrated[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_reorders_late_high_confidence_title_evidence()
    {
        var partialIngredientHit = TestMatch(
            text: "Alpha vegetable cream mentions musquee as an ingredient in a different preparation.",
            docPath: "Docs/generic-guide.pdf",
            chunkId: "partial",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var exactTitleHit = TestMatch(
            text: "Tarte fine a la courge musquee. Ingredients: dough, squash and seasoning. Preparation: bake until crisp.",
            docPath: "Docs/specific-guide.pdf",
            chunkId: "exact-title",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 0.91);
        var selected = new List<RagMatch> { partialIngredientHit, exactTitleHit };

        RetrievalTitleSelection.PrioritizeFinalSelections("Comment faire la tarte fine a la courge musquee ?", selected);

        Assert.Equal("exact-title", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_promotes_comparative_profile_with_constraint_coverage()
    {
        var genericProfile = TestMatch(
            text: "Document profile with recettes salees and a passing mention of enfants around a family meal.",
            docPath: "Cuisine/generic-recipes.pdf",
            chunkId: "generic-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 1.01);
        var targetedProfile = TestMatch(
            text: "Document profile with 57 recettes pour enfants, animateurs, temps, materiel, pizzas rigolotes, cake olives and tarte tomate.",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            chunkId: "target-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.72);
        var selected = new List<RagMatch> { genericProfile, targetedProfile };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Compare trois recettes salees pour enfants : temps, materiel, risque de ratage.",
            selected);

        Assert.Equal("target-profile", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_document_hint_over_generic_comparative_profile()
    {
        var genericProfile = TestMatch(
            text: "Document profile with desserts sante, anti gaspillage, chocolat, collations, prevention and several comparison cues.",
            docPath: "Cuisine/family-desserts-guide.pdf",
            chunkId: "generic-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.91);
        var hintedProfile = TestMatch(
            text: "Document profile for a broad labelled workplace cookbook with balanced meals and varied preparations.",
            docPath: "Cuisine/livre-recette-sist-2025-web.pdf",
            chunkId: "hinted-profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.59);
        var selected = new List<RagMatch> { genericProfile, hintedProfile };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Compare les desserts sante/anti-gaspi du PDF SIIST.",
            selected);

        Assert.Equal("hinted-profile", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_demotes_weak_direct_title_token_route_for_broad_comparison()
    {
        var weakDirectRoute = TestMatch(
            text: "This body mentions dessert and chocolate, but the route title is not a standalone title.",
            embedText: "Matched direct_title_token_route: dessert chocolat\nThis body mentions dessert and chocolate, but not as a heading.",
            docPath: "Cuisine/generic-desserts.pdf",
            chunkId: "weak-direct",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.94);
        var concreteDessert = TestMatch(
            text: "Fondant au chocolat Pour 6 personnes. Preparation simple, ingredients and cooking time.",
            docPath: "Cuisine/top-desserts.pdf",
            chunkId: "concrete-dessert",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.90);
        var selected = new List<RagMatch> { weakDirectRoute, concreteDessert };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Si je veux un dessert chocolate ou cremeux, lequel est le plus simple ?",
            selected);

        Assert.Equal("concrete-dessert", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_demotes_only_weak_content_card_title_for_broad_comparison()
    {
        var weakCardTitle = TestMatch(
            text: "Ingredients: 200 g chocolat, farine, butter, sugar and eggs. Bake, let cool, then cut into portions.",
            docPath: "Cuisine/children-desserts.pdf",
            chunkId: "weak-card",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Laisser refroidir dans le plat et couper en",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var concreteDessertCard = TestMatch(
            text: "Profiteroles au chocolat. Pour 8 personnes. Preparation simple, ingredients, cooking time and serving notes.",
            docPath: "Cuisine/top-desserts.pdf",
            chunkId: "concrete-card",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.90) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Profiteroles au chocolat",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { weakCardTitle, concreteDessertCard };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Si je veux un dessert chocolate ou cremeux, lequel est le plus simple ?",
            selected);

        Assert.Equal("concrete-card", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_does_not_use_weak_card_signals_as_title_evidence()
    {
        var weakCardSignal = TestMatch(
            text: "Ingredients and procedure notes mention alpha beta once, but this is not the named item.",
            docPath: "Docs/weak-card.pdf",
            chunkId: "weak-card-signal",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Prepare before opening the cabinet",
                    Signals: ["alpha", "beta", "procedure"])
            ]
        };
        var concreteTitle = TestMatch(
            text: "Alpha Beta Procedure. Materials: gloves and tray. Failure risk: overheating. Setup notes: cool before handling.",
            docPath: "Docs/concrete-title.pdf",
            chunkId: "concrete-title",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.90) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Alpha Beta Procedure",
                    Signals: ["structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { weakCardSignal, concreteTitle };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Compare alpha beta procedure options: material, risks and setup.",
            selected);

        Assert.Equal("concrete-title", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_moves_concrete_card_ahead_of_procedural_card_in_broad_dessert_choice()
    {
        var muffins = TestMatch(
            text: "Bake muffins with fruit and sugar. Six servings, short preparation and baking time.",
            docPath: "Cuisine/machine.pdf",
            chunkId: "muffins",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "MUFFINS AUX CERISES ET AMANDES",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var truffles = TestMatch(
            text: "TRUFFES CHOCOLAT. Bake biscuits and finish chocolate truffles.",
            docPath: "Cuisine/machine.pdf",
            chunkId: "truffles",
            score: 0.918,
            page: 2) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "TRUFFES CHOCOLAT",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var proceduralCard = TestMatch(
            text: "Ingredients: 200 g chocolat, farine, beurre, sucre, eggs and nuts. Let cool then cut into pieces.",
            docPath: "Cuisine/children-desserts.pdf",
            chunkId: "procedural-card",
            score: 0.90,
            page: 84) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            SectionTitle = "FICHE-DESSERT CHOCOLAT",
            HeadingPath = "FICHE-DESSERT CHOCOLAT",
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Laisser refroidir dans le plat et couper en",
                    Signals: ["scale_basis", "quantity_list", "structured_facts", "chocolat", "plat", "beurre"])
            ]
        };
        var profiteroles = TestMatch(
            text: "Profiteroles au chocolat Pour 8 personnes. Preparation simple, ingredients, cooking time and serving notes.",
            docPath: "Cuisine/top-desserts.pdf",
            chunkId: "profiteroles",
            score: 0.905,
            page: 29) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Profiteroles au chocolat",
                    Signals: ["quantity_list", "structured_facts", "Profiteroles au chocolat", "chocolat"])
            ]
        };
        var selected = new List<RagMatch> { muffins, truffles, proceduralCard, profiteroles };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Si je veux un dessert chocolaté ou crémeux, lequel est le plus simple ?",
            selected);

        Assert.Equal(["muffins", "profiteroles", "truffles", "procedural-card"], selected.Select(static match => match.ChunkId));
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_unqualified_title_over_unrequested_qualified_variant()
    {
        var qualifiedVariant = TestMatch(
            text: "Creme brulee aux champignons. Ingredients: mushrooms, onion, cream and parmesan.",
            embedText: "Matched direct_title_token_route: Creme brulee aux champignons\nCreme brulee aux champignons.",
            docPath: "Cuisine/qualified.pdf",
            chunkId: "qualified",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Creme brulee aux champignons",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var exactTitle = TestMatch(
            text: "Creme brulee. Ingredients: egg yolks, sugar, cream and vanilla.",
            embedText: "Matched direct_title_token_route: Creme brulee\nCreme brulee.",
            docPath: "Cuisine/exact.pdf",
            chunkId: "exact",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.90) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Creme brulee",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { qualifiedVariant, exactTitle };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Il y a plusieurs cremes brulees ? Compare-les si oui.",
            selected);

        Assert.Equal("exact", selected[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_unqualified_direct_route_over_unrequested_qualified_variant()
    {
        var qualifiedVariant = TestMatch(
            text: "Champignons a la grecque. Creme brulee aux champignons. Ingredients: mushrooms, onion, cream and parmesan.",
            embedText: "Matched direct_title_token_route: Creme brulee aux champignons\nChampignons a la grecque. Creme brulee aux champignons.",
            docPath: "Cuisine/qualified.pdf",
            chunkId: "qualified",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Creme brulee aux champignons",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var exactTitle = TestMatch(
            text: "Creme brulee. Ingredients: egg yolks, sugar, cream and vanilla.",
            embedText: "Matched direct_title_token_route: Creme brulee\nCreme brulee.",
            docPath: "Cuisine/exact.pdf",
            chunkId: "exact",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.90) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Creme brulee",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "Il y a plusieurs cremes brulees ? Compare-les si oui.",
            [qualifiedVariant, exactTitle]);

        Assert.Equal("exact", calibrated[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_demotes_overqualified_homonym_for_short_comparison_subject()
    {
        var overqualified = TestMatch(
            text: "Clafoutis de legumes sans gluten. Ingredients: vegetables, eggs, cream and cheese.",
            docPath: "Cuisine/vegetables.pdf",
            chunkId: "vegetables",
            score: 0.91) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Clafoutis de legumes sans gluten",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var focusedVariant = TestMatch(
            text: "Clafoutis aux cerises. Ingredients: cherries, sugar, flour, milk and eggs.",
            docPath: "Cuisine/cherries.pdf",
            chunkId: "cherries",
            score: 0.87) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0,
            MatchedContentCards =
            [
                new RagMatchedContentCard(
                    "Clafoutis aux cerises",
                    Signals: ["quantity_list", "structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { overqualified, focusedVariant };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Compare les clafoutis et dis ce qui change.",
            selected);

        Assert.Equal("cherries", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_is_noop_without_specialized_signal()
    {
        var structurallyClean = TestMatch(
            text: "Ingredients: alpha beta gamma. Preparation: mix and serve.",
            docPath: "Docs/clean.pdf",
            chunkId: "clean",
            score: 0.72);
        var rerankedFirst = TestMatch(
            text: "Operational summary that should stay first for a broad query.",
            docPath: "Docs/reranked.pdf",
            chunkId: "reranked-first",
            score: 0.68);
        var selected = new List<RagMatch> { rerankedFirst, structurallyClean };

        RetrievalTitleSelection.PrioritizeFinalSelections("Que peux-tu me dire sur ce dossier ?", selected);

        Assert.Equal("reranked-first", selected[0].ChunkId);
        Assert.Equal("clean", selected[1].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_quoted_phrase_in_section_title_over_body_mention()
    {
        var bodyMention = TestMatch(
            text: "Tarte au citron. Pendant ce temps, preparez la creme au citron pour garnir la tarte.",
            docPath: "Cuisine/body-mention.pdf",
            chunkId: "body-mention",
            score: 1.02);
        var sectionTitle = TestMatch(
            text: "AU CITRON Mixez en vitesse 6 pendant 1 min. Repartissez la creme dans des ramequins.",
            docPath: "Cuisine/title-hit.pdf",
            chunkId: "section-title",
            score: 0.91) with
        {
            SectionTitle = "CREME AU CITRON",
            HeadingPath = "CREME AU CITRON"
        };
        var selected = new List<RagMatch> { bodyMention, sectionTitle };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Tu peux me faire une fiche claire pour \"Creme au citron\" : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("section-title", selected[0].ChunkId);
        Assert.True(RetrievalCandidateSignals.ComputeQuotedTitleAnchorSignal(
            "fiche pour \"Creme au citron\"",
            sectionTitle) > 0.0);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_quoted_phrase_lead_over_generic_same_token_body()
    {
        var genericSauce = TestMatch(
            text: "SAUCE CHOCOLAT. Ingredients: creme fraiche, chocolat noir. Suggestion: servir avec une sauce au fromage blanc.",
            docPath: "Cuisine/generic.pdf",
            chunkId: "generic",
            score: 1.02);
        var exactLead = TestMatch(
            text: "12 min SAUCE AU POIVRE SAUCE AUX 4 FROMAGES. Ingredients: parmesan, pecorino, comte, gorgonzola. Preparation: mixer puis cuire.",
            docPath: "Cuisine/exact.pdf",
            chunkId: "exact",
            score: 0.91,
            chunkType: "exact_match_entry",
            embeddingBasis: "exact_match_v1");
        var selected = new List<RagMatch> { genericSauce, exactLead };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Tu peux me faire une fiche claire pour \u00ab Sauce aux 4 fromages \u00bb : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("exact", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_near_exact_ocr_title_over_qualified_prefix_title()
    {
        const string query = "Tu peux me faire une fiche claire pour \u00ab Sauce bolognaise \u00bb : materials, etapes, temps et source ?";
        var qualifiedPrefix = TestMatch(
            text: "SAUCE BOLOGNAISE AU SOJA TEXTURE Ingredients: soja texture, tomates, carottes. Preparation: hydrater le soja puis mijoter.",
            docPath: "Cuisine/qualified.pdf",
            chunkId: "qualified",
            embeddingBasis: "local_title_token_route_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var linkedQualified = TestMatch(
            text: "Preparation: ajouter le soja texture et cuire 10 minutes.",
            docPath: "Cuisine/qualified.pdf",
            chunkId: "linked-qualified",
            embeddingBasis: "linked_context_v1",
            score: 1.00) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72,
            MatchedContentCards = [new RagMatchedContentCard("SAUCE BOLOGNAISE AU SOJA TEXTURE", Signals: ["structured_facts"])]
        };
        var ocrTitle = TestMatch(
            text: "SAUCE BOLOGNANE Accompagnements Viandes Boeuf Modes de preparation Frire Cuire Categories de recettes Pour 4 portions SAUCE BOLOGNANE",
            docPath: "Cuisine/target.pdf",
            chunkId: "ocr-title",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.8775) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.545
        };
        var selected = new List<RagMatch> { qualifiedPrefix, linkedQualified, ocrTitle };
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(query);

        Assert.True(RetrievalChannels.HasQuotedTitlePlacementEvidence(phrases, ocrTitle));

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("ocr-title", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_local_title_footer_over_linked_ingredient_mention()
    {
        var linkedIngredient = TestMatch(
            text: "RISOTTO PETITS POIS ET JAMBON. Ingredients: 300 g de riz, 90 cl de bouillon de volaille, parmesan. Preparation: cuire le risotto.",
            docPath: "Cuisine/ingredient.pdf",
            chunkId: "linked-ingredient",
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var localTitleFooter = TestMatch(
            text: "Laisser mijoter une carcasse avec aromates. Passer le bouillon au tamis. Ingredients: carcasse de volaille, oignons, ail, laurier, thym, carottes. BOUILLON DE VOLAILLE SAUCES 3334 PREPARATION.",
            docPath: "Cuisine/title.pdf",
            chunkId: "local-title",
            embeddingBasis: "local_title_token_route_v1",
            chunkType: "section_window_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.69,
            ContentDensityScore = 0.9767
        };
        var selected = new List<RagMatch> { linkedIngredient, localTitleFooter };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Tu peux me faire une fiche claire pour \u00ab Bouillon de volaille \u00bb : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("local-title", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_navigation_route_page_over_previous_linked_context_for_footer_title()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Salade mexicaine\" : ingredients, etapes, temps et source ?";
        var previousLinkedRecipe = TestMatch(
            text: "Ingredients 150 g de pates, surimi, feta, olives, poivron. Materiel casserole passoire saladier. Technique faire cuire les pates, egoutter, laisser refroidir puis melanger. Cemea 2003 Salade de pates3300110033",
            embedText: "Matched linked_anchor_title: Salade mexicaine\nIngredients 150 g de pates, surimi, feta, olives, poivron. Technique cuire les pates. Cemea 2003 Salade de pates3300110033",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            page: 29,
            chunkId: "previous-linked",
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.882) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.88
        };
        var routedRecipePage = TestMatch(
            text: "Ingredients 1 boite 4/4 de haricots rouges, ananas, mais, poivron, olives, tomates, oignon, huile, vinaigre, moutarde, persil, sel et poivre. Materiel ouvre-boites passoire saladier fourchette couteau planche. Technique nettoyer les boites de conserve, les ouvrir, rincer et egoutter les haricots rouges, egoutter les ananas, laver le poivron et les tomates, couper les legumes, melanger le tout dans le saladier, preparer une vinaigrette relevee et saupoudrer de persil hache. Suggestions la quantite des ingredients peut varier suivant les gouts. Cemea 2003 Salade mexicaine 440044",
            embedText: "Matched navigation_route: Salade mexicaine\nIngredients 1 boite 4/4 de haricots rouges, ananas, mais, poivron, olives, tomates, oignon, huile, vinaigre, moutarde, persil, sel et poivre. Materiel ouvre-boites passoire saladier fourchette couteau planche. Technique nettoyer les boites de conserve, les ouvrir, rincer et egoutter les haricots rouges, egoutter les ananas, laver le poivron et les tomates, couper les legumes, melanger le tout dans le saladier, preparer une vinaigrette relevee et saupoudrer de persil hache. Cemea 2003 Salade mexicaine 440044",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            page: 30,
            chunkId: "mexican-navigation",
            embeddingBasis: "navigation_route_v1",
            chunkType: "unit_exact_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var selected = new List<RagMatch> { previousLinkedRecipe, routedRecipePage };
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(query);

        Assert.False(RetrievalChannels.HasQuotedTitlePlacementEvidence(phrases, previousLinkedRecipe));
        Assert.True(RetrievalCandidateSignals.NavigationRouteHasTargetTitleEvidence(routedRecipePage));
        Assert.False(RetrievalSelection.IsWeakResolvedRouteTarget(routedRecipePage));
        Assert.True(RetrievalChannels.HasQuotedTitlePlacementEvidence(phrases, routedRecipePage));

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("mexican-navigation", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_complete_recipe_card_over_title_only_ingredient_chunk()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Taboule\" : ingredients, etapes, temps et source ?";
        var titleOnlyIngredients = TestMatch(
            text: "TABOULE Pour 5 personnes 250 g de boulghour tomates menthe persil citron huile. Le taboule est un plat oriental.",
            docPath: "Cuisine/chefbot.pdf",
            page: 58,
            chunkId: "chefbot-taboule",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var completeFooterTitledCard = TestMatch(
            text: "Ingredients 200 g de semoule, tomates, concombre, oignon, persil, menthe, citrons, huile et sel. Materiel saladier, presse-citron et planche. Technique laver les tomates et le concombre, les couper en des, faire gonfler la semoule, ajouter les legumes, mettre au refrigerateur pendant 1 heure, ajouter huile et citron puis decorer avec la menthe. Notes de page et fragments OCR sans titre en tete. Encore du texte de liaison pour que le titre ne ressemble pas a un titre de debut. Cemea Taboule330011220055.",
            docPath: "Cuisine/si-on-cuisinait.pdf",
            page: 31,
            chunkId: "si-on-taboule",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.756) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var selected = new List<RagMatch> { titleOnlyIngredients, completeFooterTitledCard };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("si-on-taboule", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_keeps_local_title_route_above_generic_complete_card()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Sauce au poivre\" : ingredients, etapes, temps et source ?";
        var genericCompleteCard = TestMatch(
            text: "INGREDIENTS PREPARATION 1. Placer la lame dans le robot. 2. Ajouter l'oignon et une pincee de poivre. 3. Verser la sauce chaude sur la preparation.",
            docPath: "Cuisine/chefbot.pdf",
            page: 54,
            chunkId: "chefbot-generic-sauce",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var localTitleRoute = TestMatch(
            text: "SAUCE AU POIVRE Temps total : 17 min. 1 c. a c. de poivre concasse, cognac, creme liquide, fond de veau et farine. Versez dans le robot, lancez la cuisson puis servez.",
            docPath: "Cuisine/moulinex.pdf",
            page: 121,
            chunkId: "moulinex-sauce-poivre",
            embeddingBasis: "local_title_token_route_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var selected = new List<RagMatch> { genericCompleteCard, localTitleRoute };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("moulinex-sauce-poivre", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_exact_footer_title_window_over_token_only_direct_route()
    {
        var tokenOnlyDirectRoute = TestMatch(
            text: "Alpha with beta procedure and extra notes. Materials: wrench, seal, gauge. Procedure: isolate the equipment, replace the seal, test the line.",
            embedText: "Matched direct_title_token_route: alpha beta procedure\nAlpha with beta procedure and extra notes. Materials: wrench, seal, gauge.",
            docPath: "Docs/token-overlap.pdf",
            chunkId: "token-overlap",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var exactFooterWindow = TestMatch(
            text: "ALPHA BETA PROCEDURE 20 min Materials: valve gasket sealant. Steps: 1. Isolate the device. 2. Replace the gasket. 3. Verify the pressure.",
            embedText: "Matched direct_title_token_route: alpha beta procedure\nALPHA BETA PROCEDURE 20 min Materials: valve gasket sealant.",
            docPath: "Docs/exact-footer.pdf",
            chunkId: "exact-footer",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "footer_titled_item_window_v1",
            score: 0.666) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var selected = new List<RagMatch> { tokenOnlyDirectRoute, exactFooterWindow };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Give me a clear sheet for \"Alpha Beta Procedure\": materials, steps, time and source.",
            selected);

        Assert.Equal("exact-footer", selected[0].ChunkId);
        Assert.True(RetrievalCandidateSignals.LooksLikeStructuredAnswerUnit(exactFooterWindow));
    }

    [Fact]
    public void PrioritizeFinalSelections_falls_back_to_quality_order_for_precise_cards_without_title_signal()
    {
        var weakerInsertedFirst = TestMatch(
            text: "Procedure entree. Ingredients: pois chiches, tahini. Preparation: mixer.",
            docPath: "Cuisine/weaker.pdf",
            chunkId: "weaker",
            score: 0.68) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.74
        };
        var strongerSpecificContent = TestMatch(
            text: "Ingredients: legumes cuits, petits pois, pommes de terre. Technique: cuire, couper, melanger, puis incorporer la sauce.",
            docPath: "Cuisine/stronger.pdf",
            chunkId: "stronger",
            score: 0.76) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.95
        };
        var selected = new List<RagMatch> { weakerInsertedFirst, strongerSpecificContent };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Tu peux me faire une fiche claire pour \u00ab Alpha Beta \u00bb : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("stronger", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_does_not_let_partial_title_overlap_override_score()
    {
        var partialLowerScore = TestMatch(
            text: "HOUMOUS DE BETTERAVE. Ingredients: betterave, pois chiches, tahini. Preparation: mixer.",
            docPath: "Cuisine/partial.pdf",
            chunkId: "partial",
            score: 0.68) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.90
        };
        var strongerPartialContent = TestMatch(
            text: "Ingredients: 700 g de betteraves rouges cuites, petits pois, pommes de terre. Technique: cuire, couper, melanger, puis incorporer la sauce.",
            docPath: "Cuisine/stronger.pdf",
            chunkId: "stronger",
            score: 0.76) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.95
        };
        var selected = new List<RagMatch> { partialLowerScore, strongerPartialContent };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Tu peux me faire une fiche claire pour \u00ab Betteraves caucasiennes \u00bb : ingredients, etapes, temps et source ?",
            selected);

        Assert.Equal("stronger", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_preserves_operational_setting_intent()
    {
        var genericDessert = TestMatch(
            text: "Tarte au citron meringuee. Ingredients: farine, beurre, citron, sucre. Robot: vitesse 6 pendant 8 min.",
            docPath: "Cuisine/generic.pdf",
            chunkId: "generic",
            score: 1.02);
        var robotSettings = TestMatch(
            text: "CREME AU CITRON. Dans le bol du robot muni du batteur, mettez les oeufs et le sucre. Lancez vitesse 6 a 50 C pendant 8 min.",
            docPath: "Cuisine/robot.pdf",
            chunkId: "robot",
            score: 0.87,
            chunkType: "exact_match_entry",
            embeddingBasis: "exact_match_v1");
        var selected = new List<RagMatch> { genericDessert, robotSettings };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Je veux la creme au citron, avec les parametres robot.",
            selected);

        Assert.Equal("robot", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_short_duration_for_fast_title_request()
    {
        var longMachineRecipe = TestMatch(
            text: "FONDANT AU CHOCOLAT Temps total : 1 h. Robot mode dessert vitesse 6. Ingredients chocolat, beurre, oeufs. Preparation complete.",
            docPath: "Cuisine/machine.pdf",
            chunkId: "long-machine",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var shortRecipe = TestMatch(
            text: "Fondant au chocolat Pour 4 personnes ingredients chocolat beurre oeufs. Preparation: prechauffez le four, melangez et enfournez pendant 7 min.",
            docPath: "Cuisine/classic.pdf",
            chunkId: "short-classic",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.98);
        var selected = new List<RagMatch> { longMachineRecipe, shortRecipe };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Donne-moi la recette du fondant au chocolat en mode rapide.",
            selected);

        Assert.Equal("short-classic", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_short_duration_for_generic_fast_procedure_request()
    {
        var longProcedure = TestMatch(
            text: "ALPHA BETA PROCEDURE Total time: 2 h. Materials: valve, gasket and sealant. Steps: isolate, replace, verify.",
            docPath: "Docs/long-guide.pdf",
            chunkId: "long-procedure",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var shortProcedure = TestMatch(
            text: "Alpha beta procedure. Materials: valve and gasket. Steps: isolate, replace and verify in 9 min.",
            docPath: "Docs/short-guide.pdf",
            chunkId: "short-procedure",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.98);
        var selected = new List<RagMatch> { longProcedure, shortProcedure };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Give me the Alpha Beta Procedure quickly.",
            selected);

        Assert.Equal("short-procedure", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_focused_title_head_over_broad_qualifier_match()
    {
        var broadChocolate = TestMatch(
            text: "Mousse au chocolat express Pour 4 personnes ingredients chocolat et oeufs. Preparation avec chocolat fondu rapide.",
            docPath: "Cuisine/generic.pdf",
            chunkId: "broad-chocolate",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.01);
        var focusedFondue = TestMatch(
            text: "SAUCE CHOCOLAT Cette sauce est excellente avec de la glace, des fruits au sirop, en fondue. Materiel et ingredients pour un groupe.",
            docPath: "Cuisine/children-guide.pdf",
            chunkId: "focused-fondue",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918);
        var selected = new List<RagMatch> { broadChocolate, focusedFondue };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Je cherche la fondue au chocolat pour un groupe d'enfants.",
            selected);

        Assert.Equal("focused-fondue", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_structured_content_covering_specific_request_tokens()
    {
        var genericRice = TestMatch(
            text: "INGREDIENTS PREPARATION Pour 5 personnes 240 g de riz long 1 l d'eau. Servir pour accompagner un ragout ou un plat au curry.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "generic-rice",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var focusedCurry = TestMatch(
            text: "CURRY DE CREVETTES A L'ANANAS. Ajoutez les crevettes, le lait de coco puis salez et poivrez. Servez le curry de crevettes avec du riz basmati. Ingredients et preparation completes.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "focused-curry",
            embeddingBasis: "linked_context_v1",
            score: 0.918);
        var selected = new List<RagMatch> { genericRice, focusedCurry };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Detaille le curry de crevettes et riz basmati.",
            selected);

        Assert.Equal("focused-curry", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_uses_same_document_specific_coverage_for_linked_chunks()
    {
        var genericRice = TestMatch(
            text: "INGREDIENTS PREPARATION Pour 5 personnes 240 g de riz long 1 l d'eau. Servir pour accompagner un ragout ou un plat au curry.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "generic-rice",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02);
        var currySteps = TestMatch(
            text: "CURRY DE CREVETTES A L'ANANAS. Ajoutez les crevettes, le lait de coco puis salez et poivrez. Lancez le programme mijote.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "curry-steps",
            embeddingBasis: "linked_context_v1",
            score: 0.918);
        var curryServing = TestMatch(
            text: "Servez le curry de crevettes saupoudre de coriandre. Accompagnez ce plat de riz basmati.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "curry-serving",
            embeddingBasis: "linked_context_v1",
            score: 0.807);
        var selected = new List<RagMatch> { genericRice, currySteps, curryServing };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Detaille le curry de crevettes et riz basmati.",
            selected);

        Assert.Equal("curry-steps", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_specific_document_coverage_over_generic_structured_body_match()
    {
        var genericRice = TestMatch(
            text: "INGREDIENTS PREPARATION Pour 5 personnes 240 g de riz basmati et 1 l d'eau. Servir pour accompagner un ragout ou un plat au curry.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            chunkId: "generic-rice",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var currySteps = TestMatch(
            text: "CURRY DE CREVETTES A L'ANANAS. Ajoutez les crevettes, le lait de coco puis salez et poivrez. Lancez le programme mijote.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "curry-steps",
            embeddingBasis: "linked_context_v1",
            chunkType: "linked_context_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var curryServing = TestMatch(
            text: "Servez le curry de crevettes saupoudre de coriandre. Accompagnez ce plat de riz basmati.",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "curry-serving",
            embeddingBasis: "linked_context_v1",
            chunkType: "linked_context_v1",
            score: 0.807,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var selected = new List<RagMatch> { genericRice, currySteps, curryServing };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Detaille le curry de crevettes et riz basmati.",
            selected);

        Assert.Equal("curry-steps", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_generic_focused_head_over_body_variant()
    {
        var bodyVariant = TestMatch(
            text: "Access mode alpha explains credentials, permissions and fallback routes for the system.",
            docPath: "Docs/access-guide.pdf",
            chunkId: "body-variant",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.01);
        var focusedHead = TestMatch(
            text: "MODULE ALPHA Access mode, setup checklist and operating notes for the module.",
            docPath: "Docs/module-guide.pdf",
            chunkId: "focused-head",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918);
        var selected = new List<RagMatch> { bodyVariant, focusedHead };

        RetrievalTitleSelection.PrioritizeFinalSelections(
            "Show Module Alpha access mode.",
            selected);

        Assert.Equal("focused-head", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeOperationalSettingSelections_prefers_machine_settings_when_requested()
    {
        var plainRecipe = TestMatch(
            text: "Alpha procedure with ingredients and preparation.",
            docPath: "Docs/plain-guide.pdf",
            chunkId: "plain",
            embeddingBasis: "direct_title_token_route_v1",
            score: 1.02);
        var settingsRecipe = TestMatch(
            text: "Alpha procedure. Put the ingredients in the machine bowl. Use speed 6 at 50 C for 8 minutes.",
            docPath: "Docs/machine-guide.pdf",
            chunkId: "settings",
            embeddingBasis: "navigation_route_v1",
            score: 0.96);
        var selected = new List<RagMatch> { plainRecipe, settingsRecipe };

        RetrievalTitleSelection.PrioritizeOperationalSettingSelections("Give me alpha with machine settings.", selected);

        Assert.Equal("settings", selected[0].ChunkId);
        Assert.Equal("plain", selected[1].ChunkId);
    }

    [Fact]
    public void PrioritizeOperationalSettingSelections_does_not_change_plain_lookup()
    {
        var plainRecipe = TestMatch(
            text: "Alpha procedure with ingredients and preparation.",
            docPath: "Docs/plain-guide.pdf",
            chunkId: "plain",
            embeddingBasis: "direct_title_token_route_v1",
            score: 1.02);
        var settingsRecipe = TestMatch(
            text: "Alpha procedure. Use speed 6 at 50 C for 8 minutes.",
            docPath: "Docs/machine-guide.pdf",
            chunkId: "settings",
            embeddingBasis: "navigation_route_v1",
            score: 0.96);
        var selected = new List<RagMatch> { plainRecipe, settingsRecipe };

        RetrievalTitleSelection.PrioritizeOperationalSettingSelections("Give me alpha.", selected);

        Assert.Equal("plain", selected[0].ChunkId);
        Assert.Equal("settings", selected[1].ChunkId);
    }

    [Fact]
    public void PrioritizeOperationalSettingSelections_ignores_fast_mode_without_device_context()
    {
        var plainRecipe = TestMatch(
            text: "Alpha procedure with ingredients and preparation. Bake for 7 min.",
            docPath: "Docs/plain-guide.pdf",
            chunkId: "plain",
            embeddingBasis: "direct_title_token_route_v1",
            score: 1.02);
        var settingsRecipe = TestMatch(
            text: "Alpha procedure. Machine mode dessert with speed 6 at 50 C for 1 h.",
            docPath: "Docs/machine-guide.pdf",
            chunkId: "settings",
            embeddingBasis: "navigation_route_v1",
            score: 0.96);
        var selected = new List<RagMatch> { plainRecipe, settingsRecipe };

        RetrievalTitleSelection.PrioritizeOperationalSettingSelections("Give me alpha in quick mode.", selected);

        Assert.Equal("plain", selected[0].ChunkId);
        Assert.Equal("settings", selected[1].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_prioritizes_resolved_title_routes_before_neighboring_sparse_chunks()
    {
        var sparseChunk = TestMatch(
            text: "Ingredients and steps mention asparagus, honey and walnuts but the title was on the previous page.",
            embedText: "Ingredients and steps mention asparagus, honey and walnuts but the title was on the previous page.",
            chunkId: "sparse-neighbor",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 1.02);
        var titleRoute = TestMatch(
            text: "Ingredients and steps mention asparagus, honey and walnuts but the title was on the previous page.",
            embedText: "Matched title_anchor_route: Green asparagus with honey\nIngredients and steps mention asparagus, honey and walnuts.",
            chunkId: "title-route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 0.91);

        var ordered = RetrievalSelection.OrderMatchesForSelection([sparseChunk, titleRoute], prioritizeDocumentProfiles: false);

        Assert.Equal("title-route", ordered[0].ChunkId);
        Assert.True(RetrievalSelection.IsResolvedTitleOrNavigationRoute(ordered[0]));
    }

    [Fact]
    public void PreferRequestedPageWindowMatches_promotes_requested_page_window_over_far_hits()
    {
        var farHigherScore = TestMatch(
            text: "Relevant words, but far away from the requested document page window.",
            page: 40,
            chunkId: "far",
            score: 1.10);
        var adjacentLowerScore = TestMatch(
            text: "Nearby content after the requested document page window.",
            page: 13,
            chunkId: "adjacent",
            score: 0.70);
        var overlappingLowerScore = TestMatch(
            text: "Content overlapping the requested document page window.",
            page: 10,
            chunkId: "overlap",
            score: 0.60) with
        {
            PageEnd = 11
        };

        var ordered = RetrievalSelection.PreferRequestedPageWindowMatches(
            [farHigherScore, adjacentLowerScore, overlappingLowerScore],
            requestedPageStart: 10,
            requestedPageEnd: 12);

        Assert.Equal("overlap", ordered[0].ChunkId);
        Assert.Equal("adjacent", ordered[1].ChunkId);
        Assert.Equal("far", ordered[2].ChunkId);
        Assert.True(ordered[0].Score > overlappingLowerScore.Score);
        Assert.True(ordered[1].Score > adjacentLowerScore.Score);
    }

    [Fact]
    public void CalibrateFusedMatches_prefers_linked_structured_anchor_title_over_exact_advisory_mention()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Croquettes de poulet\" : ingredients, etapes, temps et source ?";
        var advisoryMention = TestMatch(
            text: "pizza, croquettes de poulet, soupes en sachet, nouilles instantanees. Un petit pas a la fois consiste a reduire les aliments transformes.",
            embedText: "pizza, croquettes de poulet, soupes en sachet, nouilles instantanees. Un petit pas a la fois consiste a reduire les aliments transformes.",
            docPath: "Cuisine/advisory.pdf",
            chunkId: "advisory",
            embeddingBasis: "exact_match_v1",
            score: 1.02) with
        {
            ContentDensityScore = 0.20
        };
        var linkedStructured = TestMatch(
            text: "INGREDIENTS : 400 g de blanc de poulet, sel, poivre, 2 oeufs, cornflakes, farine, huile. PREPARATION : couper le poulet, paner les morceaux, puis faire revenir 15-20 minutes de tous les cotes.",
            embedText: "Matched linked_anchor_title: Croquettes de poulet\nINGREDIENTS : 400 g de blanc de poulet, sel, poivre, 2 oeufs, cornflakes, farine, huile. PREPARATION : couper le poulet, paner les morceaux, puis faire revenir 15-20 minutes de tous les cotes.",
            docPath: "Cuisine/structured.pdf",
            chunkId: "structured",
            embeddingBasis: "linked_context_v1",
            chunkType: "section_window_v1",
            score: 0.74) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.92
        };
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(query);

        Assert.False(RetrievalChannels.HasQuotedTitlePlacementEvidence(phrases, advisoryMention));
        Assert.True(RetrievalCandidateSignals.ComputeQuotedTitleAnchorSignal(query, linkedStructured) > 0.0);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(query, [advisoryMention, linkedStructured]);

        Assert.Equal("structured", calibrated[0].ChunkId);
        Assert.True(RetrievalCandidateSignals.HasProfileTitleHint(linkedStructured));
        Assert.True(RetrievalCandidateSignals.ComputeExactTitleCandidateScore(query, linkedStructured) > 0.0);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_linked_structured_anchor_title_over_exact_advisory_mention()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Croquettes de poulet\" : ingredients, etapes, temps et source ?";
        var advisoryMention = TestMatch(
            text: "pizza, croquettes de poulet, soupes en sachet, nouilles instantanees. Un petit pas a la fois consiste a reduire les aliments transformes.",
            embedText: "pizza, croquettes de poulet, soupes en sachet, nouilles instantanees. Un petit pas a la fois consiste a reduire les aliments transformes.",
            docPath: "Cuisine/advisory.pdf",
            chunkId: "advisory",
            embeddingBasis: "exact_match_v1",
            score: 1.02) with
        {
            ContentDensityScore = 0.20
        };
        var titleMetadata = TestMatch(
            text: "Volailles Poule Modes de preparation Frire Categories de recettes Volailles Pour 4 portions, env. 20 pieces",
            embedText: "Matched title_anchor_route: Croquettes de poulet\nVolailles Poule Modes de preparation Frire Categories de recettes Volailles Pour 4 portions, env. 20 pieces",
            docPath: "Cuisine/structured.pdf",
            chunkId: "metadata",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 0.91) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var linkedStructured = TestMatch(
            text: "INGREDIENTS : 400 g de blanc de poulet, sel, poivre, 2 oeufs, cornflakes, farine, huile. PREPARATION : couper le poulet, paner les morceaux, puis faire revenir 15-20 minutes de tous les cotes.",
            embedText: "Matched linked_anchor_title: Croquettes de poulet\nINGREDIENTS : 400 g de blanc de poulet, sel, poivre, 2 oeufs, cornflakes, farine, huile. PREPARATION : couper le poulet, paner les morceaux, puis faire revenir 15-20 minutes de tous les cotes.",
            docPath: "Cuisine/structured.pdf",
            chunkId: "structured",
            embeddingBasis: "linked_context_v1",
            chunkType: "section_window_v1",
            score: 0.74) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.92
        };
        var selected = new List<RagMatch> { advisoryMention, titleMetadata, linkedStructured };
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(query);

        Assert.False(RetrievalChannels.HasQuotedTitlePlacementEvidence(phrases, advisoryMention));
        Assert.True(RetrievalCandidateSignals.ComputeQuotedTitleAnchorSignal(query, linkedStructured) > 0.0);

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("structured", selected[0].ChunkId);
        Assert.True(Array.FindIndex(selected.ToArray(), match => match.ChunkId == "structured")
            < Array.FindIndex(selected.ToArray(), match => match.ChunkId == "advisory"));
        Assert.True(Array.FindIndex(selected.ToArray(), match => match.ChunkId == "structured")
            < Array.FindIndex(selected.ToArray(), match => match.ChunkId == "metadata"));
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_structured_linked_recipe_over_mixed_navigation_phrase_mention()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Boulettes de poulet a la sauce tomate\" : ingredients, etapes, temps et source ?";
        var mixedNavigationMention = TestMatch(
            text: "MES INDISPENSABLES Qu'elles soient entreposees au refrigerateur, au congelateur ou dans les armoires, il est primordial d'avoir des denrees de base: poulet, sauce tomate, chapelure et epices pour preparer rapidement des boulettes.",
            embedText: "Matched quoted title: boulettes de poulet a la sauce tomate\nMES INDISPENSABLES Qu'elles soient entreposees au refrigerateur, au congelateur ou dans les armoires, il est primordial d'avoir des denrees de base: poulet, sauce tomate, chapelure et epices pour preparer rapidement des boulettes.",
            docPath: "Cuisine/pantry.pdf",
            chunkId: "mixed-navigation",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.MixedNavigationContentRole,
            contentDensityScore: 0.50) with
        {
            NavigationScore = 0.64
        };
        var linkedStructuredRecipe = TestMatch(
            text: "50 min PREPARATION Placer l'accessoire lame dans le recipient. Ajouter l'oignon et l'ail puis programmer 10 secondes. Ajouter la viande de poulet, former les boulettes, les cuire avec la sauce tomate et servir chaud.",
            embedText: "103 BE A MASTER. BECOME A CHEF 50 min PREPARATION Placer l'accessoire lame dans le recipient. Ajouter l'oignon et l'ail puis programmer 10 secondes. Ajouter la viande de poulet, former les boulettes, les cuire avec la sauce tomate et servir chaud.",
            docPath: "Cuisine/chefbot_livre_de_recettes_fr.pdf",
            page: 103,
            chunkId: "linked-structured",
            embeddingBasis: "linked_context_v1",
            score: 0.84,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var selected = new List<RagMatch> { mixedNavigationMention, linkedStructuredRecipe };
        var phrases = RetrievalLexicalExpansion.ExtractTitleLookupPhrases(query);

        Assert.NotEmpty(phrases);
        Assert.Equal(RetrievalContentClassifier.MixedNavigationContentRole, mixedNavigationMention.ContentRole);
        Assert.False(RetrievalChannels.HasQuotedTitlePlacementEvidence(phrases, mixedNavigationMention));
        Assert.True(RetrievalSelection.IsWeakStructuredQuotedNavigationMentionForTesting(query, mixedNavigationMention));

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("linked-structured", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_complete_title_route_over_partial_homonymous_recipe()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Terrines de legumes\" : ingredients, etapes, temps et source ?";
        var partialHomonym = TestMatch(
            text: "Menu TERRINEDE LEGUMES20 min 4 Ingredients Quelques feuilles de salade 250 g de restes de legumes Huile d'olive 2 echalotes 50 g emmental rape 2 oeufs 60 g de creme 60 g de pain rassis 4 cuilleres a soupe de lait Sel, poivre Noix de muscade Sauce tartare La terrine peut etre degustee tiede ou froide.",
            embedText: "Menu TERRINEDE LEGUMES20 min 4 Ingredients Quelques feuilles de salade 250 g de restes de legumes Huile d'olive 2 echalotes 50 g emmental rape 2 oeufs 60 g de creme 60 g de pain rassis 4 cuilleres a soupe de lait Sel, poivre Noix de muscade Sauce tartare La terrine peut etre degustee tiede ou froide.",
            docPath: "Cuisine/livre-recette-sist-2025-web.pdf",
            chunkId: "partial-homonym",
            embeddingBasis: "linked_context_v1",
            chunkType: "section_window_v1",
            score: 0.98) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var completeTitleRoute = TestMatch(
            text: "TERRINE DE LEGUMES Pelez les carottes et coupez-les en petits des. Versez 0,7 L d'eau dans le bol du robot. Deposez les legumes dans le panier vapeur et lancez le programme vapeur P1 pour 15 min. Ajoutez les carottes et les petits pois. Prechauffez le four a 180 C. Enfournez-la pour 1 h. 8 personnes 18 min 1 h 20 min TERRINE DE LEGUMES",
            embedText: "Matched title_anchor_route: Terrine de legumes\nTERRINE DE LEGUMES Pelez les carottes et coupez-les en petits des. Versez 0,7 L d'eau dans le bol du robot. Deposez les legumes dans le panier vapeur et lancez le programme vapeur P1 pour 15 min. Ajoutez les carottes et les petits pois. Prechauffez le four a 180 C. Enfournez-la pour 1 h. 8 personnes 18 min 1 h 20 min TERRINE DE LEGUMES",
            docPath: "Cuisine/Tag249008277_1_MOULINEX_HF93D810_8020007485_IFU.pdf",
            chunkId: "complete-title-route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 0.918) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var selected = new List<RagMatch> { partialHomonym, completeTitleRoute };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("complete-title-route", selected[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_prefers_local_quoted_title_content_before_title_catalog()
    {
        var titleCatalog = TestMatch(
            text: "Contents Alpha Beta Procedure 12 Alpha Gamma Procedure 18 Alpha Delta Procedure 24",
            embedText: "Matched direct_title_token_route: Alpha Beta Procedure\nContents Alpha Beta Procedure 12 Alpha Gamma Procedure 18 Alpha Delta Procedure 24",
            chunkId: "catalog",
            embeddingBasis: "direct_title_token_route_v1",
            chunkType: "unit_exact_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.76,
            ContentDensityScore = 0.50
        };
        var content = TestMatch(
            text: "Alpha Beta Procedure. Materials: valve gasket sealant. Procedure: isolate, replace and verify the assembly.",
            embedText: "Alpha Beta Procedure. Materials: valve gasket sealant. Procedure: isolate, replace and verify the assembly.",
            chunkId: "content",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 0.82) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.72
        };
        var partial = TestMatch(
            text: "Alpha and beta are separate entries in this glossary section.",
            embedText: "Alpha and beta are separate entries in this glossary section.",
            chunkId: "partial",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 1.02);

        var ordered = RetrievalSelection.OrderMatchesForSelection(
            [titleCatalog, partial, content],
            prioritizeDocumentProfiles: false,
            query: "Give me a clear sheet for \"Alpha Beta Procedure\".");

        Assert.Equal("content", ordered[0].ChunkId);
    }

    [Fact]
    public void PrioritizeQuotedTitleSelections_prefers_title_lead_over_exact_body_mention()
    {
        var bodyMention = TestMatch(
            text: "This accessory can also be served inside an Alpha Beta Procedure bundle when the operator wants a lighter variant.",
            embedText: "This accessory can also be served inside an Alpha Beta Procedure bundle when the operator wants a lighter variant.",
            docPath: "Docs/body-mention.pdf",
            chunkId: "body-mention",
            embeddingBasis: "exact_match_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.20
        };
        var titleLead = TestMatch(
            text: """
ALPHA BETA PROCEDURE
Materials: valve gasket sealant lock tag gauge.
Procedure: 1. Isolate the device. 2. Replace the component. 3. Verify the assembly and record the result.
""",
            embedText: """
ALPHA BETA PROCEDURE
Materials: valve gasket sealant lock tag gauge.
Procedure: 1. Isolate the device. 2. Replace the component. 3. Verify the assembly and record the result.
""",
            docPath: "Docs/title-lead.pdf",
            chunkId: "title-lead",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.82) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.95
        };
        var selected = new List<RagMatch> { bodyMention, titleLead };

        RetrievalSelection.PrioritizeQuotedTitleSelections("Give me a clear sheet for \"Alpha Beta Procedure\".", selected);

        Assert.Equal("title-lead", selected[0].ChunkId);
        Assert.False(RetrievalChannels.HasQuotedTitlePlacementEvidence(["Alpha Beta Procedure"], bodyMention));
        Assert.True(RetrievalChannels.HasQuotedTitlePlacementEvidence(["Alpha Beta Procedure"], titleLead));
    }

    [Fact]
    public void CalibrateFusedMatches_does_not_treat_exact_body_phrase_as_title_evidence()
    {
        var bodyMention = TestMatch(
            text: "This accessory can also be served inside an Alpha Beta Procedure bundle when the operator wants a lighter variant.",
            embedText: "This accessory can also be served inside an Alpha Beta Procedure bundle when the operator wants a lighter variant.",
            docPath: "Docs/body-mention.pdf",
            chunkId: "body-mention",
            embeddingBasis: "exact_match_v1",
            score: 0.97) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.20
        };
        var titleLead = TestMatch(
            text: """
ALPHA BETA PROCEDURE
Materials: valve gasket sealant lock tag gauge.
Procedure: 1. Isolate the device. 2. Replace the component. 3. Verify the assembly and record the result.
""",
            embedText: """
ALPHA BETA PROCEDURE
Materials: valve gasket sealant lock tag gauge.
Procedure: 1. Isolate the device. 2. Replace the component. 3. Verify the assembly and record the result.
""",
            docPath: "Docs/title-lead.pdf",
            chunkId: "title-lead",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.82) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.95
        };

        var calibrated = RetrievalSelection.CalibrateFusedMatches(
            "Give me a clear sheet for \"Alpha Beta Procedure\".",
            [bodyMention, titleLead]);

        Assert.Equal("title-lead", calibrated[0].ChunkId);
        Assert.Equal(0.0, RetrievalCandidateSignals.ComputeExactTitleCandidateScore(
            "Give me a clear sheet for \"Alpha Beta Procedure\".",
            bodyMention));
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_card_title_placement_over_dense_body_mention()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Aioli epice\" : ingredients, etapes, temps et source ?";
        var denseBodyMention = TestMatch(
            text: "Conseils sauces: aoli classique, mayonnaise, ail, citron, piment doux et variante epicee pour accompagner les legumes. Ingredients generiques et etapes rapides pour servir une sauce froide.",
            embedText: "Conseils sauces: aoli classique, mayonnaise, ail, citron, piment doux et variante epicee pour accompagner les legumes. Ingredients generiques et etapes rapides pour servir une sauce froide.",
            docPath: "Cuisine/generic-sauces.pdf",
            chunkId: "dense-body",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.04,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.96);
        var cardTitleHit = TestMatch(
            text: "SAUCES AIOLI EPICE\nIngredients: ail, jaune d'oeuf, huile, citron, piment. Preparation: monter la sauce, assaisonner et servir avec les legumes. Temps: 10 min.",
            embedText: "Matched content_card: SAUCES AIOLI EPICE\nIngredients: ail, jaune d'oeuf, huile, citron, piment. Preparation: monter la sauce, assaisonner et servir avec les legumes. Temps: 10 min.",
            docPath: "Cuisine/jecuisine.pdf",
            chunkId: "card-title-hit",
            embeddingBasis: "linked_context_v1",
            score: 0.74,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.91) with
        {
            MatchedContentCards = [new RagMatchedContentCard("SAUCES AIOLI EPICE", Signals: ["structured_facts"])]
        };
        var selected = new List<RagMatch> { denseBodyMention, cardTitleHit };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("card-title-hit", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_full_compact_card_title_over_partial_structured_title()
    {
        const string query = "Tu peux me faire une fiche claire pour « Aïoli épicé » : ingrédients, étapes, temps et source ?";
        var partialStructuredTitle = TestMatch(
            text: "Pour 4 personnes Cette mayonnaise a l'ail se marie tres bien avec les legumes nouveaux. Aioli 1. Mettez le vinaigre, l'oeuf, les jaunes d'oeuf, la moutarde et le sucre dans un robot. Assaisonnez puis versez lentement l'huile. Ingredients: vinaigre, oeuf, moutarde, huile, ail, jus de citron.",
            embedText: "Pour 4 personnes Cette mayonnaise a l'ail se marie tres bien avec les legumes nouveaux. Aioli 1. Mettez le vinaigre, l'oeuf, les jaunes d'oeuf, la moutarde et le sucre dans un robot. Assaisonnez puis versez lentement l'huile. Ingredients: vinaigre, oeuf, moutarde, huile, ail, jus de citron.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 79,
            chunkId: "partial-aioli",
            embeddingBasis: "dense_qdrant",
            score: 0.374109,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.70);
        var compactFullTitle = TestMatch(
            text: "Ajouter tous les ingredients dans un cul-de-poule a l'exception de l'huile. En fouettant vigoureusement, ajouter l'huile en filet jusqu'a ce que la mayonnaise soit lisse et homogene. Ingredients: 5 gousses d'ail roties, citron, jaune d'oeuf, moutarde, huile, sauce piquante, sel et poivre. SAUCESAIOLI EPICE",
            embedText: "Matched profile title: SAUCESAIOLI EPICE; Sel et poivreSAUCESAIOLI EPICE\nAjouter tous les ingredients dans un cul-de-poule a l'exception de l'huile. En fouettant vigoureusement, ajouter l'huile en filet jusqu'a ce que la mayonnaise soit lisse et homogene. Ingredients: 5 gousses d'ail roties, citron, jaune d'oeuf, moutarde, huile, sauce piquante, sel et poivre. SAUCESAIOLI EPICE",
            docPath: "Cuisine/Je_cuisine_simplement.pdf",
            page: 21,
            chunkId: "compact-full-title",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard("SAUCESAIOLI EPICE", Signals: ["structured_facts"]),
                new RagMatchedContentCard("Sel et poivreSAUCESAIOLI EPICE", Signals: ["structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { partialStructuredTitle, compactFullTitle };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("compact-full-title", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_complete_direct_token_route_over_partial_sparse_body_match()
    {
        const string query = "Tu peux me faire une fiche claire pour « Endives au jambon » : materials, étapes, temps et source ?";
        var partialSparse = TestMatch(
            text: "Preparation 1. Cuire les pates puis les egoutter. Couper l'endive en fines lamelles, ajouter les tomates et melanger. Servir avec une vinaigrette.",
            docPath: "Cuisine/livre-recette-sist-2025-web.pdf",
            page: 16,
            chunkId: "partial-sparse",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.62,
            contentRole: RetrievalContentClassifier.MixedNavigationContentRole,
            contentDensityScore: 1.0);
        var directRoute = TestMatch(
            text: "Pour la sauce bechamel\n\nPour 4 personnes Pour ce plat traditionnel, les endives baignent avec le jambon dans une sauce cremeuse et relevee. Gratin d'endives 1. Prechauffez le four a 180 C. Mettez les endives dans un moule. 2. Faites chauffer le lait avec l'oignon. 3. Enveloppez chaque tete d'endive d'une tranche de jambon. Nappez avec la sauce bechamel. 4. Faites gratiner 20-25 minutes. Ingredients: 8 tetes d'endives, 8 tranches de jambon cuit, gouda, lait, beurre, farine.",
            embedText: "Matched direct_title_token_route: endives au jambon\nPour la sauce bechamel\n\nPour 4 personnes Pour ce plat traditionnel, les endives baignent avec le jambon dans une sauce cremeuse et relevee. Gratin d'endives 1. Prechauffez le four a 180 C. Mettez les endives dans un moule. 2. Faites chauffer le lait avec l'oignon. 3. Enveloppez chaque tete d'endive d'une tranche de jambon. Nappez avec la sauce bechamel. 4. Faites gratiner 20-25 minutes. Ingredients: 8 tetes d'endives, 8 tranches de jambon cuit, gouda, lait, beurre, farine.",
            docPath: "Cuisine/nobilia-recettes-internationales-FR.pdf",
            page: 37,
            chunkId: "direct-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.774,
            contentRole: RetrievalContentClassifier.MixedNavigationContentRole,
            contentDensityScore: 0.595);
        var selected = new List<RagMatch> { partialSparse, directRoute };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("direct-route", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_card_title_route_over_ingredient_phrase_mentions()
    {
        const string query = "Tu peux me faire une fiche claire pour « Bouillon de volaille » : ingrédients, étapes, temps et source ?";
        var ingredientMention = TestMatch(
            text: "RISOTTO PETITS POIS ET JAMBON Temps total: 23 min. 300 g de riz arborio, echalote, vin blanc, 90 cl de bouillon de volaille, petits pois, parmesan. Preparation: verser le bouillon de volaille puis cuire le risotto.",
            docPath: "Cuisine/robot-rice.pdf",
            page: 96,
            chunkId: "ingredient-mention",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72) with
        {
            MatchedContentCards = [new RagMatchedContentCard("RISOTTO PETITS POIS ET JAMBON", Signals: ["structured_facts"])]
        };
        var cardTitleRoute = TestMatch(
            text: "TRUCS CULINAIRES 1\n\nPREPARATION Dans une grande casserole, faire revenir l'oignon, l'ail, le celeri et les carottes. SAUCE AUX TOMATES SAUCES PREPARATION Dans une grande casserole, faire revenir les parties de la volaille, l'oignon et l'ail dans l'huile. Ajouter les herbes et couvrir d'eau froide. Laisser mijoter pour 1 h. Filtrer. INGREDIENTS parties de volaille, oignon, ail, carottes, celeri, thym, romarin, persil, grains de poivre. BOUILLON DE VOLAILLE SAUCES",
            embedText: "Matched direct_title_token_route: bouillon de volaille\nTRUCS CULINAIRES 1\n\nPREPARATION Dans une grande casserole, faire revenir l'oignon, l'ail, le celeri et les carottes. SAUCE AUX TOMATES SAUCES PREPARATION Dans une grande casserole, faire revenir les parties de la volaille, l'oignon et l'ail dans l'huile. Ajouter les herbes et couvrir d'eau froide. Laisser mijoter pour 1 h. Filtrer. INGREDIENTS parties de volaille, oignon, ail, carottes, celeri, thym, romarin, persil, grains de poivre. BOUILLON DE VOLAILLE SAUCES",
            docPath: "Cuisine/Je_cuisine_simplement.pdf",
            page: 18,
            chunkId: "card-title-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.895) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard("Grains de poivreBOUILLON DE VOLAILLE SAUCES"),
                new RagMatchedContentCard("BOUILLON DE VOLAILLE SAUCES", Signals: ["structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { ingredientMention, cardTitleRoute };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("card-title-route", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_card_title_route_over_procedure_lead_ingredient_phrase()
    {
        const string query = "Tu peux me faire une fiche claire pour \"Bouillon de volaille\" : ingredients, etapes, temps et source ?";
        var procedureLeadIngredientMention = TestMatch(
            text: "Lorsqu'il ne reste que 1 min, ajoutez le vin blanc. A la fin du programme, versez le bouillon de volaille et lancez le programme mijote pour 20 min. Rectifiez l'assaisonnement. Servez sans attendre. RISOTTO D'ETE Astuce: ne pas laisser au chaud.",
            embedText: "Matched quoted title: Lorsque le minuteur indique qu'il reste; Lorsqu'il ne reste que; RISOTTO CLASSIQUE",
            docPath: "Cuisine/robot-rice.pdf",
            page: 95,
            chunkId: "procedure-lead-ingredient",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard("Lorsque le minuteur indique qu'il reste", Signals: ["structured_facts"]),
                new RagMatchedContentCard("Lorsqu'il ne reste que", Signals: ["structured_facts"]),
                new RagMatchedContentCard("RISOTTO CLASSIQUE", Signals: ["structured_facts"])
            ]
        };
        var cardTitleRoute = TestMatch(
            text: "TRUCS CULINAIRES 1\n\nPREPARATION Dans une grande casserole, faire revenir l'oignon, l'ail, le celeri et les carottes. Ajouter les parties de volaille, les herbes et couvrir d'eau froide. Laisser mijoter pour 1 h. Filtrer. INGREDIENTS parties de volaille, oignon, ail, carottes, celeri, thym, romarin, persil, grains de poivre. BOUILLON DE VOLAILLE SAUCES",
            embedText: "Matched direct_title_token_route: bouillon de volaille\nTRUCS CULINAIRES 1\n\nPREPARATION Dans une grande casserole, faire revenir l'oignon, l'ail, le celeri et les carottes. Ajouter les parties de volaille, les herbes et couvrir d'eau froide. Laisser mijoter pour 1 h. Filtrer. INGREDIENTS parties de volaille, oignon, ail, carottes, celeri, thym, romarin, persil, grains de poivre. BOUILLON DE VOLAILLE SAUCES",
            docPath: "Cuisine/Je_cuisine_simplement.pdf",
            page: 18,
            chunkId: "card-title-route",
            embeddingBasis: "direct_title_token_route_v1",
            score: 0.918,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.895) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard("Grains de poivreBOUILLON DE VOLAILLE SAUCES"),
                new RagMatchedContentCard("BOUILLON DE VOLAILLE SAUCES", Signals: ["structured_facts"])
            ]
        };
        var selected = new List<RagMatch> { procedureLeadIngredientMention, cardTitleRoute };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("card-title-route", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_trailing_quoted_title_over_partial_head_title()
    {
        const string query = "Donne-moi le one pot pasta brocoli dinde bacon.";
        Assert.Equal("one pot pasta brocoli dinde bacon", RetrievalCandidateSignals.BuildFocusedLexicalBackfillQuery(query));

        var partialHeadTitle = TestMatch(
            text: "ONE POT PASTA AUX CREVETTES ET MASCARPONE Ajouter un peu de parmesan au moment de servir. Ingredients: pates, crevettes, mascarpone. Preparation: cuire et melanger.",
            embedText: "Matched profile title: ONE POT PASTA AUX CREVETTES ET MASCARPONE\nONE POT PASTA AUX CREVETTES ET MASCARPONE Ajouter un peu de parmesan au moment de servir. Ingredients: pates, crevettes, mascarpone. Preparation: cuire et melanger.",
            docPath: "Cuisine/partial.pdf",
            page: 94,
            chunkId: "partial-head-title",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.549,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.94) with
        {
            MatchedContentCards =
            [
                new RagMatchedContentCard("ONE POT PASTA AUX CREVETTES ET MASCARPONE", Signals: ["structured_facts"])
            ]
        };
        var trailingTitle = TestMatch(
            text: "TRUCS CULINAIRES Ajouter le reste des ingredients, a l'exception de la dinde et des bouquets de brocoli. Porter a ebullition, reduire la puissance et laisser mijoter pour 10 minutes. En fin de cuisson, ajouter la dinde afin de la rechauffer. Couper le bacon en laniere et preparer le service. Ingredients 3 tranches de bacon, hachees 1 brocoli, bouquet et pied separes 5 gousses d'ail, hachees finement 1.5 L d'eau, spaghettini, sel, poivre frais, sauce piquante au choix, 2 tasses de dinde, cuite\u00ab ONE POT PASTA \u00bb BROCOLI, DINDE ET BACONPLATS PRINCI- PAUX",
            docPath: "Cuisine/target.pdf",
            page: 44,
            chunkId: "trailing-quoted-title",
            embeddingBasis: "linked_context_v1",
            score: 0.918,
            prevChunkId: "target-prev",
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.6485) with
        {
            NavigationScore = 0.69,
            MatchedContentCards =
            [
                new RagMatchedContentCard("HUITRES TOMATEESPLATS PRINCI"),
                new RagMatchedContentCard("DINDE ET BACONPLATS PRINCI- PAUX", Signals: ["structured_facts"]),
                new RagMatchedContentCard("TOMATEESPLATS PRINCI")
            ]
        };
        var selected = new List<RagMatch> { partialHeadTitle, trailingTitle };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);
        Assert.Equal("trailing-quoted-title", selected[0].ChunkId);
        RetrievalTitleSelection.ReattachStructuredContinuationCompanions(query, selected, topK: 20);
        RetrievalTitleSelection.PruneQualifiedQuotedTitleCompetitorDocuments(query, selected);
        RetrievalTitleSelection.PrunePreciseTitleLocalNeighborhoodSelections(query, selected);

        Assert.Equal("trailing-quoted-title", selected[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_deprioritizes_route_when_title_only_appears_as_trailing_lead()
    {
        var trailingRoute = TestMatch(
            text: "Materials: gasket and wrench. Procedure: inspect the previous assembly and close the checklist. Index entry Alpha Beta Procedure",
            embedText: "Matched title_anchor_route: Alpha Beta Procedure\nMaterials: gasket and wrench. Procedure: inspect the previous assembly and close the checklist. Index entry Alpha Beta Procedure",
            chunkId: "trailing-route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.MixedNavigationContentRole,
            NavigationScore = 0.69,
            ContentDensityScore = 0.95
        };
        var denseNeighbor = TestMatch(
            text: "Materials: lock, tag, gauge, seal kit and wrench. Procedure: isolate the device, verify zero energy, replace the component, test the assembly and record the result.",
            chunkId: "dense-neighbor",
            embeddingBasis: "linked_context_v1",
            chunkType: "unit_exact_v1",
            score: 0.84) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            NavigationScore = 0.0,
            ContentDensityScore = 0.90
        };

        var ordered = RetrievalSelection.OrderMatchesForSelection(
            [trailingRoute, denseNeighbor],
            prioritizeDocumentProfiles: false,
            query: "Give me Alpha Beta Procedure.");

        Assert.True(RetrievalSelection.IsWeakResolvedRouteTarget(trailingRoute));
        Assert.Equal("dense-neighbor", ordered[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_keeps_resolved_routes_first_when_document_diversity_is_active()
    {
        var sparseChunk = TestMatch(
            text: "A different page in the same document mentions honey but not the requested title.",
            embedText: "A different page in the same document mentions honey but not the requested title.",
            chunkId: "sparse-same-doc",
            embeddingBasis: "sparse_bm25_v1",
            chunkType: "unit_exact_v1",
            score: 1.02);
        var titleRoute = TestMatch(
            text: "The routed page contains the concrete procedure.",
            embedText: "Matched title_anchor_route: Green asparagus with honey\nThe routed page contains the concrete procedure.",
            chunkId: "title-route",
            embeddingBasis: "title_anchor_route_v1",
            chunkType: "section_window_v1",
            score: 0.91);

        var ordered = RetrievalSelection.OrderMatchesForSelection([sparseChunk, titleRoute], prioritizeDocumentProfiles: true);

        Assert.Equal("title-route", ordered[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_unquoted_complete_title_over_generic_body_match()
    {
        const string query = "Comment je fais le magret de canard au miel ?";
        var genericBodyMatch = TestMatch(
            text: "Les alternatives a la viande. Les legumineuses constituent une solution de remplacement interessante a la viande. Pour cuisiner les legumineuses seches, consultez le guide.",
            docPath: "Cuisine/advisory.pdf",
            chunkId: "generic-body",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.36) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 0.72
        };
        var completeTitle = TestMatch(
            text: "Magret de canard au miel Pour 4 personnes. Ingredients : 2 magrets de canard, 3 c. a soupe de miel, sauce soja, vinaigre de riz. Preparation : laisser mariner puis cuire au four 30 min.",
            embedText: "Matched title_anchor_route: Magret de canard au miel\nMagret de canard au miel Pour 4 personnes. Ingredients : 2 magrets de canard, 3 c. a soupe de miel, sauce soja, vinaigre de riz. Preparation : laisser mariner puis cuire au four 30 min.",
            docPath: "Cuisine/30-recettes-preferees-des-francais.pdf",
            chunkId: "complete-title",
            embeddingBasis: "title_anchor_route_v1",
            score: 1.02) with
        {
            ContentRole = RetrievalContentClassifier.ContentRole,
            ContentDensityScore = 1.0
        };
        var selected = new List<RagMatch> { genericBodyMatch, completeTitle };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal(["magret"], RetrievalTitleSelection.ExtractFocusedLookupPrimaryHeadTokens(query));
        Assert.Equal("complete-title", selected[0].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_prefers_content_covering_two_distinctive_terms_over_single_generic_mention()
    {
        const string query = "Comment controler le module vert avec la sonde de rotissage ?";
        var partialHighScore = TestMatch(
            text: "Module vert. Le module vert doit etre nettoye puis inspecte avant lancement.",
            docPath: "Knowledge/module-generic.pdf",
            chunkId: "partial",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var directTarget = TestMatch(
            text: "Module vert. Procedure : brancher la sonde de rotissage, controler le module vert, puis enregistrer la mesure de stabilisation.",
            docPath: "Knowledge/module-procedure.pdf",
            chunkId: "target",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.64,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);

        var ordered = RetrievalSelection.OrderMatchesForSelection(
            [partialHighScore, directTarget],
            prioritizeDocumentProfiles: false,
            query);

        Assert.Equal("target", ordered[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_specific_setting_target_over_generic_context_match()
    {
        const string query = "Quels reglages de rotissage pour le materiau composite haute densite ?";
        var genericSettingMatch = TestMatch(
            text: "Guide de calibration thermique. Les reglages de rotissage et de chauffe doivent etre controles avant usage. Le document mentionne plusieurs materiaux sans procedure specifique.",
            docPath: "Knowledge/generic-settings.pdf",
            chunkId: "generic",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var specificTarget = TestMatch(
            text: "Materiau composite haute densite. Procedure : verifier le support, appliquer la temperature de reference, puis ajuster la sonde de rotissage selon la densite mesuree.",
            docPath: "Knowledge/specific-settings.pdf",
            chunkId: "specific",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.63,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var selected = new List<RagMatch> { genericSettingMatch, specificTarget };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("specific", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_prefers_focused_item_covering_two_distinctive_terms()
    {
        const string query = "Comment controler le module vert au miel avec la sonde de rotissage ?";
        var firstPartial = TestMatch(
            text: "Module vert. Le module vert doit etre nettoye puis inspecte avant lancement.",
            docPath: "Knowledge/module-generic.pdf",
            chunkId: "module-generic",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.02,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var secondPartial = TestMatch(
            text: "Controle au miel. Le miel est cite comme exemple de matiere collante dans la procedure de nettoyage.",
            docPath: "Knowledge/honey-generic.pdf",
            chunkId: "honey-generic",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.91,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 1.0);
        var focusedTarget = TestMatch(
            text: "Module vert au miel. Procedure : placer le module vert sur le banc, regler la sonde de rotissage, puis controler le depot de miel apres stabilisation.",
            docPath: "Knowledge/focused.pdf",
            chunkId: "focused",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.64,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.72);
        var selected = new List<RagMatch> { firstPartial, secondPartial, focusedTarget };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("focused", selected[0].ChunkId);
    }

    [Fact]
    public void PrioritizeFinalSelections_does_not_promote_low_evidence_two_anchor_noise()
    {
        const string query = "Comment controler le module vert avec la sonde de rotissage ?";
        var supportedContent = TestMatch(
            text: "Module vert. Procedure detaillee : verifier le module vert, preparer le banc de test, puis executer le controle.",
            docPath: "Knowledge/supported.pdf",
            chunkId: "supported",
            embeddingBasis: "sparse_bm25_v1",
            score: 0.82,
            contentRole: RetrievalContentClassifier.ContentRole,
            contentDensityScore: 0.95);
        var noisyNavigation = TestMatch(
            text: "Index module vert sonde rotissage autres chapitres annexes contacts revision sommaire.",
            docPath: "Knowledge/noisy.pdf",
            chunkId: "noise",
            embeddingBasis: "sparse_bm25_v1",
            score: 1.04,
            contentRole: RetrievalContentClassifier.NavigationRole,
            contentDensityScore: 0.05);
        var selected = new List<RagMatch> { noisyNavigation, supportedContent };

        RetrievalTitleSelection.PrioritizeFinalSelections(query, selected);

        Assert.Equal("supported", selected[0].ChunkId);
    }

    [Fact]
    public void CalibrateFusedMatches_penalizes_short_count_exact_match_for_broad_planning_query()
    {
        const string query = "Compare documented options for 4 people and pick the best candidate.";
        var shortCountExact = TestMatch(
            "Pour4",
            chunkId: "weak-count",
            embeddingBasis: "exact_match_v1",
            chunkType: "exact_match_entry",
            score: 0.99);
        var planningContext = TestMatch(
            "Document profile: comparison planning, budget constraints, documented options and selection criteria.",
            chunkId: "planning-context",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.78);

        var calibrated = RetrievalSelection.CalibrateFusedMatches(query, [shortCountExact, planningContext]);

        Assert.True(RetrievalSelection.ShouldPenalizeWeakExactMatchForBroadQuery(query, shortCountExact));
        Assert.Equal("planning-context", calibrated[0].ChunkId);
        Assert.Equal("weak-count", calibrated[1].ChunkId);
    }

    [Fact]
    public void OrderMatchesForSelection_places_weak_count_exact_after_profiles_for_broad_query()
    {
        const string query = "Prepare a documented onboarding plan for 4 people from the corpus.";
        var shortCountExact = TestMatch(
            "Pour4",
            chunkId: "weak-count",
            embeddingBasis: "exact_match_v1",
            chunkType: "exact_match_entry",
            score: 0.99);
        var profile = TestMatch(
            "Document profile: onboarding plan, audience constraints, documented tasks and preparation steps.",
            chunkId: "profile",
            embeddingBasis: "document_profile_v1",
            chunkType: "document_profile",
            score: 0.72);

        var ordered = RetrievalSelection.OrderMatchesForSelection(
            [shortCountExact, profile],
            prioritizeDocumentProfiles: false,
            query);

        Assert.Equal("profile", ordered[0].ChunkId);
        Assert.Equal("weak-count", ordered[1].ChunkId);
    }
}
