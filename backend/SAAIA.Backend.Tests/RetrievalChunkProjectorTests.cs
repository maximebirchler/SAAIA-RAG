using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalChunkProjectorTests
{
    [Fact]
    public void Project_maps_chunk_to_best_overlapping_section_and_unit()
    {
        var chunks = new[]
        {
            new Chunk(0, 1, 1, "hello world"),
            new Chunk(1, 2, 3, "next chunk")
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Intro", 1, 1, 1, null, null),
            new ExtractedDocumentSection(1, "Body", 1, 2, 3, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "hello world", 11, 2, [1], 0, 11),
            new ExtractedDocumentUnit(1, 1, 2, 2, "next chunk", 10, 2, [2], 13, 23)
        };

        var projected = RetrievalChunkProjector.Project(chunks, sections, units);

        Assert.Equal(2, projected.Count);
        Assert.Equal(0, projected[0].SectionOrdinal);
        Assert.Equal(0, projected[0].UnitOrdinal);
        Assert.Equal(0, projected[0].OffsetStart);
        Assert.Equal(11, projected[0].OffsetEnd);
        Assert.Equal(1, projected[1].SectionOrdinal);
        Assert.Equal(1, projected[1].UnitOrdinal);
        Assert.Equal(13, projected[1].OffsetStart);
        Assert.Equal(23, projected[1].OffsetEnd);
    }

    [Fact]
    public void Project_returns_empty_when_no_chunks_are_provided()
    {
        var projected = RetrievalChunkProjector.Project([], [], []);

        Assert.Empty(projected);
    }

    [Fact]
    public void Stable_retrieval_chunk_id_is_deterministic_for_same_doc_version_and_chunk_index()
    {
        var docId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        var left = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 7);
        var right = DocumentFoundationRepo.BuildStableRetrievalChunkId(docId, 3, 7);

        Assert.Equal(left, right);
    }

    [Fact]
    public void ProjectStructureAware_keeps_chunks_inside_section_boundaries()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Intro", 1, 1, 1, 1, null),
            new ExtractedDocumentSection(1, "Body", 1, 2, 2, 1, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "alpha beta gamma", 16, 3, [1], 0, 16),
            new ExtractedDocumentUnit(1, 0, 1, 1, "delta epsilon zeta", 18, 3, [2], 20, 38),
            new ExtractedDocumentUnit(2, 1, 2, 2, "theta iota kappa", 16, 3, [3], 42, 58),
            new ExtractedDocumentUnit(3, 1, 2, 2, "lambda mu nu", 12, 3, [4], 62, 74)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 5,
            overlapWords: 1,
            minWords: 1);

        Assert.Equal(4, projected.Count);
        Assert.All(projected.Take(2), chunk => Assert.Equal(0, chunk.SectionOrdinal));
        Assert.All(projected.Skip(2), chunk => Assert.Equal(1, chunk.SectionOrdinal));
        Assert.All(projected, chunk => Assert.NotEqual("legacy_word_window_v1", chunk.ChunkType));
        Assert.All(projected, chunk => Assert.True(chunk.OffsetEnd > chunk.OffsetStart));
    }

    [Fact]
    public void ProjectStructureAware_adds_exact_chunks_for_short_high_signal_units()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 2, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Reference list http://example.test http://example-two.test",
                57,
                5,
                [1],
                0,
                57),
            new ExtractedDocumentUnit(
                1,
                0,
                2,
                2,
                "SAUCE BECHAMEL DE BASE Ingredients beurre farine lait Preparation 1. Faire fondre le beurre. 2. Ajouter la farine et fouetter.",
                125,
                18,
                [2],
                61,
                186)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 200,
            overlapWords: 0,
            minWords: 80);

        Assert.Contains(projected, chunk =>
            chunk.ChunkType == "unit_exact_v1"
            && chunk.UnitOrdinal == 1
            && chunk.Text.StartsWith("SAUCE BECHAMEL", StringComparison.Ordinal));
        Assert.DoesNotContain(projected, chunk =>
            chunk.ChunkType == "unit_exact_v1"
            && chunk.UnitOrdinal == 0);
    }

    [Fact]
    public void ProjectStructureAware_uses_shared_multilingual_lexicon_for_high_signal_units()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "BLOQUE CONTROL Componentes sensor valvula actuator Preparacion 1. Verificar sensor principal. 2. Registrar resultado final.",
                121,
                15,
                [1],
                0,
                121)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 200,
            overlapWords: 0,
            minWords: 80);

        Assert.Contains(projected, chunk =>
            chunk.ChunkType == "unit_exact_v1"
            && chunk.UnitOrdinal == 0
            && chunk.Text.StartsWith("BLOQUE CONTROL", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectStructureAware_does_not_prefix_structured_unit_with_previous_tail_fragment()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 6, 7, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                6,
                6,
                "Si vous preferez temperer la recette, arrosez de creme fraiche. Preparation 20 minutes Pas cher Facile",
                98,
                14,
                [1],
                0,
                98),
            new ExtractedDocumentUnit(
                1,
                0,
                6,
                7,
                "7Gratin dauphinoisPour 4 personnes Ingrédients pommes de terre lait creme ail muscade sel poivre Préparation Epluchez les pommes de terre puis enfournez.",
                151,
                20,
                [2],
                100,
                251)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 120,
            overlapWords: 0,
            minWords: 80);

        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.StartsWith("Si vous preferez", StringComparison.Ordinal)
            && chunk.Text.Contains("Gratin dauphinois", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.Text.StartsWith("Gratin dauphinois", StringComparison.Ordinal)
            && (chunk.ChunkType == "section_window_v1" || chunk.ChunkType == "unit_exact_v1"));
    }

    [Fact]
    public void ProjectStructureAware_does_not_overlap_tail_into_next_recipe_with_production_window_settings()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 6, 7, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                7,
                0,
                6,
                6,
                "6TartiflettePour 4 personnes 1 kg de pommes de terre 250 g de lardons 100 g d'oignons 25 cl de vin blanc de Savoie 500 g de reblochon Sel et poivre Préparation Préchauffez le four. Faites cuire les pommes de terre. Dans une poêle, faites revenir les lardons et les oignons.",
                276,
                162,
                [1],
                0,
                276),
            new ExtractedDocumentUnit(
                8,
                0,
                6,
                6,
                "Si vous préférez au contraire tempérer le caractère de la recette, arrosez de crème fraîche ! Préparation : 20 minutesCuisson : 20 minutesPas cherFacile",
                152,
                24,
                [2],
                278,
                430),
            new ExtractedDocumentUnit(
                9,
                0,
                7,
                7,
                "7Gratin dauphinoisPour 4 personnes 1,5 kg de pommes de terre 50 cl de lait entier 50 cl de crème fraîche liquide entière 1 gousse d'ail Beurre Muscade Sel et poivre Préparation Préchauffez le four à 180 C. Portez le lait à ébullition avec l'ail, le sel, le poivre et la muscade.",
                290,
                105,
                [3],
                432,
                722),
            new ExtractedDocumentUnit(
                10,
                0,
                7,
                7,
                "Disposez quelques noix de beurre sur le dessus. Baissez le four à 160 C et enfournez pour 1 h à 1 h 30. Astuce Pour un gratin Dauphinois réussi, choisissez des variétés de pommes de terre fermes et fondantes.",
                214,
                73,
                [4],
                724,
                938)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.StartsWith("Si vous préférez", StringComparison.Ordinal)
            && chunk.Text.Contains("Gratin dauphinois", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.Text.StartsWith("Gratin dauphinois", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectStructureAware_prefixes_embedded_uppercase_title_and_cleans_pdf_artifacts()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 121, 121, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                426,
                0,
                121,
                121,
                "226227Temps total : 12 minTemps total : 17 min1 c. a c. de poivre concasse1 cl de cognac10 cl de creme liquide1 Dans le robot muni du batteur, mettez le poivre.",
                164,
                31,
                [1],
                0,
                164),
            new ExtractedDocumentUnit(
                427,
                0,
                121,
                121,
                "Ajoutez 15 cl d'eau puis lancez le robot pour 12 min. Servez avec des steaks.6 personnes12 min5 minSAUCE AU POIVRE50 g de parmesan\x07SelPoivre1 Otez la croute.",
                160,
                31,
                [2],
                166,
                326)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 0,
            minWords: 25);

        var sauce = Assert.Single(projected, chunk => chunk.Text.StartsWith("SAUCE AU POIVRE", StringComparison.Ordinal));
        Assert.DoesNotContain("\x07", sauce.Text, StringComparison.Ordinal);
        Assert.Contains("Temps total : 12 min", sauce.Text, StringComparison.Ordinal);
        Assert.Contains("12 min Temps total", sauce.Text, StringComparison.Ordinal);
        Assert.Contains("liquide 1 Dans", sauce.Text, StringComparison.Ordinal);
        Assert.Contains("d'eau puis lancez", sauce.Text, StringComparison.Ordinal);
        Assert.Contains("steaks. 6 personnes 12 min 5 min SAUCE AU POIVRE", sauce.Text, StringComparison.Ordinal);
        Assert.Contains("5 min SAUCE AU POIVRE 50 g", sauce.Text, StringComparison.Ordinal);
        Assert.False(sauce.Text.StartsWith("226227", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectStructureAware_separates_compact_measure_step_and_title_boundaries()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 107, 107, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                107,
                107,
                "Temps total : 22 min 150 g de polenta precuite0,7 L d'eau1 \u0153uf et d'eau1 cube.2 Dans le bol, versez d'eau10 brins de ciboulette.4 A la fin, ajoutez du chorizo2 oignons, servez avec du riz basmati.1,8 kg de dinde (morceaux a sauter)100 g de chorizo, des tomates (en conserve)1 branche de thym, Maizena\u00ae30 cl de bouillon, un saumon sans la peau1 poignee d'aneth et du gorgonzola2 jaunes avec la creme liquideSelPoivre. Mixez 10 s2 h. Servez immediatement.4/6 personnes 17 min5 minPOLENTAVous pouvez ajouter des herbes.",
                216,
                32,
                [1],
                0,
                216)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 0,
            minWords: 1);

        var chunk = Assert.Single(projected);
        Assert.Contains("precuite 0,7 L d'eau 1 \u0153uf et d'eau 1 cube", chunk.Text, StringComparison.Ordinal);
        Assert.Contains(". 2 Dans le bol", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("d'eau 10 brins", chunk.Text, StringComparison.Ordinal);
        Assert.Contains(". 4 A la fin", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("chorizo 2 oignons", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("basmati. 1,8 kg", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("sauter) 100 g", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("conserve) 1 branche", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Maizena\u00ae 30 cl", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("peau 1 poignee", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("gorgonzola 2 jaunes", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("liquide Sel Poivre", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("10 s 2 h", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("immediatement. 4/6 personnes 17 min 5 min POLENTA Vous", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectStructureAware_removes_compact_leading_page_number_before_apostrophe_title()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 19, 19, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                19,
                19,
                "19N'ATTENDEZ PAS D'AVOIR SOIF POUR BOIRE DE L'EAU ! Il est essentiel de boire de l'eau regulierement tout au long de la journee.",
                132,
                22,
                [1],
                0,
                132)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 0,
            minWords: 1);

        var chunk = Assert.Single(projected);
        Assert.StartsWith("N'ATTENDEZ PAS", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("19N'ATTENDEZ", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectStructureAware_marks_compact_index_chunks_as_navigation()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 158, 158, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                158,
                158,
                "IndexA, BAioli 78Boeuf bourguignon 70Boeuf Stroganoff 129Bortsch 126Bouillon de mangue au vivaneau 148Boulettes de viande suedoises accompagnees de sauce 12Boulgour aux crevettes et aux gombos 142Brioches fourrees aux cerises 110Brochettes de poulet grille a l'indonesienne 41Canard en feuille de riz 156Cannelloni aux epinards 102Carpaccio 93Churros avec sauce au chocolat 89Coq au vin 68Creme brulee 72",
                402,
                45,
                [1],
                0,
                402)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 0,
            minWords: 1);

        var chunk = Assert.Single(projected);
        Assert.Equal(RetrievalContentClassifier.NavigationChunkType, chunk.ChunkType);
        Assert.Equal(RetrievalContentClassifier.NavigationRole, chunk.ContentRole);
        Assert.NotNull(chunk.NavigationReason);
        Assert.Equal("unit_exact_v1", chunk.OriginalChunkType);
        Assert.True(chunk.NavigationScore >= 0.72);
        Assert.True(chunk.ContentDensityScore < 0.50);
    }

    [Fact]
    public void ProjectStructureAware_keeps_structured_content_with_pdf_index_artifact_as_content()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 16, 16, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                16,
                16,
                "16 Asperges vertes au miel [Index: ] MCRC01072833_BO_Gruener_Spargel_m_Honig-010 INGREDIENTS : 50 g de cerneaux de noix, 1 botte d'asperges vertes, 3 c. a s. de miel. PREPARATION 1. Faire chauffer la poele comme indique. 2. Faire griller les asperges.",
                257,
                34,
                [1],
                0,
                257)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 0,
            minWords: 1);

        var chunk = Assert.Single(projected);
        Assert.NotEqual(RetrievalContentClassifier.NavigationChunkType, chunk.ChunkType);
        Assert.Equal(RetrievalContentClassifier.ContentRole, chunk.ContentRole);
        Assert.Null(chunk.NavigationReason);
        Assert.Null(chunk.OriginalChunkType);
        Assert.True(chunk.ContentDensityScore >= 0.50);
    }

    [Fact]
    public void ProjectStructureAware_keeps_mixed_navigation_content_searchable_with_scores()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                1,
                1,
                "Safety overview 3 Maintenance plan 18 Alarm reset 22 Lockout checklist 27 Appendix 31 Procedure body: Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy and document the result.",
                160,
                23,
                [1],
                0,
                160)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 0,
            minWords: 1);

        var chunk = Assert.Single(projected);
        Assert.NotEqual(RetrievalContentClassifier.NavigationChunkType, chunk.ChunkType);
        Assert.Equal(RetrievalContentClassifier.MixedNavigationContentRole, chunk.ContentRole);
        Assert.True(chunk.NavigationScore >= 0.55);
        Assert.True(chunk.ContentDensityScore >= 0.50);
    }
}
