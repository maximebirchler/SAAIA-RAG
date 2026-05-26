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
    public void ProjectStructureAware_propagates_unit_extraction_quality_to_chunks()
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
                "Sparse recovered note",
                21,
                3,
                [1],
                0,
                21,
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: false,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["ocr_candidate_text"]),
            new ExtractedDocumentUnit(
                1,
                0,
                2,
                2,
                "Structured body with enough reliable content to build the chunk.",
                62,
                10,
                [2],
                23,
                85,
                ExtractionTextStatus: "ok",
                ExtractionTextSparse: false,
                ExtractionOcrCandidate: false,
                ExtractionQualitySignals: ["text_extraction_ok"])
        };

        var chunk = Assert.Single(RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 100,
            overlapWords: 0,
            minWords: 1));

        Assert.Equal("low_text", chunk.ExtractionTextStatus);
        Assert.False(chunk.ExtractionTextSparse);
        Assert.True(chunk.ExtractionOcrCandidate);
        Assert.Contains("ocr_candidate_text", chunk.ExtractionQualitySignals!);
        Assert.Contains("text_extraction_ok", chunk.ExtractionQualitySignals!);
    }

    [Fact]
    public void ProjectStructureAware_excludes_restricted_units_from_dense_windows_when_clean_units_exist()
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
                "EN 15281 sparse recovered note",
                29,
                5,
                [1],
                0,
                29,
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"]),
            new ExtractedDocumentUnit(
                1,
                0,
                2,
                2,
                "Reliable body content with enough context to support regular semantic retrieval.",
                74,
                10,
                [2],
                31,
                105,
                ExtractionTextStatus: "ok",
                ExtractionTextSparse: false,
                ExtractionOcrCandidate: false,
                ExtractionQualitySignals: ["text_extraction_ok"])
        };

        var chunk = Assert.Single(RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 100,
            overlapWords: 0,
            minWords: 1));

        Assert.DoesNotContain("EN 15281", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Reliable body content", chunk.Text, StringComparison.Ordinal);
        Assert.Equal("ok", chunk.ExtractionTextStatus);
        Assert.False(chunk.ExtractionTextSparse);
        Assert.DoesNotContain("sparse_text_on_page", chunk.ExtractionQualitySignals!);
    }

    [Fact]
    public void ProjectStructureAware_excludes_same_page_restricted_units_when_clean_units_exist()
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
                "Sparse OCR fragment without reliable reference",
                44,
                6,
                [1],
                0,
                44,
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"]),
            new ExtractedDocumentUnit(
                1,
                0,
                1,
                1,
                "Reliable same-page body content with enough context to support semantic retrieval.",
                78,
                11,
                [2],
                46,
                124,
                ExtractionTextStatus: "ok",
                ExtractionTextSparse: false,
                ExtractionOcrCandidate: false,
                ExtractionQualitySignals: ["text_extraction_ok"])
        };

        var chunk = Assert.Single(RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 100,
            overlapWords: 0,
            minWords: 1));

        Assert.DoesNotContain("Sparse OCR fragment", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Reliable same-page body content", chunk.Text, StringComparison.Ordinal);
        Assert.Equal("ok", chunk.ExtractionTextStatus);
        Assert.False(chunk.ExtractionTextSparse);
        Assert.DoesNotContain("sparse_text_on_page", chunk.ExtractionQualitySignals!);
    }

    [Fact]
    public void ProjectStructureAware_does_not_promote_sparse_low_quality_units_as_exact_chunks()
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
                "EN 15281 sparse recovered note",
                29,
                5,
                [1],
                0,
                29,
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"])
        };

        var chunk = Assert.Single(RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 100,
            overlapWords: 0,
            minWords: 1));

        Assert.NotEqual("unit_exact_v1", chunk.ChunkType);
        Assert.Equal("section_window_v1", chunk.ChunkType);
        Assert.Equal("low_text", chunk.ExtractionTextStatus);
    }

    [Fact]
    public void ProjectStructureAware_returns_no_chunks_for_restricted_units_without_targeted_reference()
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
                "small shim",
                10,
                2,
                [1],
                0,
                10,
                ExtractionTextStatus: "low_text",
                ExtractionTextSparse: true,
                ExtractionOcrCandidate: true,
                ExtractionQualitySignals: ["sparse_text_on_page"])
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 100,
            overlapWords: 0,
            minWords: 1);

        Assert.Empty(projected);
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
    public void ProjectStructureAware_does_not_cross_late_structured_title_boundary()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 123, 123, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                123,
                123,
                "Temps total : 40 min 80 g de beurre 25 cl d'eau 150 g de farine 4 oeufs 100 g de sucre perle Sel 1 Prechauffez le four. 2 Ajoutez la farine. 3 Ajoutez les oeufs un a un. 4 Saupoudrez de sucre perle. 4 personnes 15 min CHOUQUETTES",
                240,
                45,
                [1],
                0,
                240),
            new ExtractedDocumentUnit(
                1,
                0,
                123,
                123,
                "CHURROS SAUCE CHOCOLAT 30 cl de lait demi-ecreme 15 cl d'eau 200 g de farine 1 sachet de levure chimique 3 pincees de sel 1 blanc d'oeuf 165 g de chocolat noir Preparation 1. Melangez la pate. 2. Formez des boudins. 3. Preparez la sauce chocolat.",
                254,
                42,
                [2],
                242,
                496)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.Contains("CHOUQUETTES", StringComparison.Ordinal)
            && chunk.Text.Contains("CHURROS SAUCE CHOCOLAT", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.Text.StartsWith("CHURROS SAUCE CHOCOLAT", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectStructureAware_does_not_cross_footer_title_into_next_structured_body()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 123, 123, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                123,
                123,
                "Temps total : 40 min 80 g de beurre 25 cl d'eau 150 g de farine 4 oeufs 1 Prechauffez le four. 2 Ajoutez la farine. 3 Ajoutez les oeufs. 4 Enfournez les petits tas. 4 personnes 15 min ALPHA CAKES Decorez avec des eclats.",
                236,
                45,
                [1],
                0,
                236),
            new ExtractedDocumentUnit(
                1,
                0,
                123,
                123,
                "30 cl de lait 200 g de farine 1 sachet de levure 1 Melangez la pate. 2 Formez des boudins. 3 Preparez la sauce. 4 personnes 12 min BETA STICKS Utilisez un appareil pour des formes regulieres.",
                204,
                38,
                [2],
                238,
                442)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.Contains("ALPHA CAKES", StringComparison.Ordinal)
            && chunk.Text.Contains("BETA STICKS", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.Text.Contains("BETA STICKS", StringComparison.Ordinal)
            && chunk.Text.StartsWith("BETA STICKS", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectStructureAware_does_not_cross_compact_footer_title_into_next_body()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 123, 123, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                123,
                123,
                "Enfournez et faites cuire pendant 25 \u00e0 30 min.4/6 personnes12 min30 min15 minCHOUQUETTESD\u00e9corez d\u2019\u00e9clats de pistaches, de pralines, de noisettes.",
                151,
                20,
                [1],
                0,
                151),
            new ExtractedDocumentUnit(
                1,
                0,
                123,
                123,
                "30 cl de lait demi-\u00e9cr\u00e9m\u00e915 cl d'eau200 g de farine1 sachet de levure chimique3 pinc\u00e9es de sel1 blanc d\u2019\u0153uf165 g de chocolat noir1 c. \u00e0 c. d\u2019ar\u00f4me vanille1 Dans le robot muni du couteau pour p\u00e9trir/concasser, mettez 15 cl de lait et 15 cl d\u2019eau.",
                238,
                45,
                [2],
                153,
                391)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.Contains("CHOUQUETTES", StringComparison.Ordinal)
            && chunk.Text.Contains("30 cl de lait", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.Text.StartsWith("30 cl de lait", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectStructureAware_keeps_dangling_quantity_line_with_short_continuation()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 34, 34, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                34,
                34,
                "INGREDIENTS Pour la creme anglaise 600 ml de lait 1 gousse de vanille 2 c. a s. de sucre fin 4 jaunes d'oeufs De plus 250 g de fond de genoise 110 g de confiture 175 g de framboises 4 Amaretti emiettes 100 ml de",
                218,
                42,
                [1],
                0,
                218),
            new ExtractedDocumentUnit(
                1,
                0,
                34,
                34,
                "Sherry Amontillado 300 g de creme liquide legerement fouettee 2 c. a s. d'amandes effilees pour la decoration",
                111,
                17,
                [2],
                220,
                331)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 45,
            overlapWords: 0,
            minWords: 25);

        Assert.Contains(projected, chunk =>
        {
            var compactText = string.Join(' ', chunk.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return chunk.ChunkType == "unit_exact_v1"
                && compactText.Contains("100 ml de Sherry Amontillado", StringComparison.Ordinal)
                && compactText.Contains("300 g de creme liquide", StringComparison.Ordinal);
        });
        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.EndsWith("100 ml de", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProjectStructureAware_adds_footer_titled_window_for_title_at_recipe_end()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 123, 123, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                123,
                123,
                "Temps total : 40 min 80 g de beurre25 cl d'eau150 g de farine4 \u0153ufs100 g de sucre perl\u00e9Sel1 Pr\u00e9chauffez le four \u00e0 180\u00b0C (th 6).",
                145,
                29,
                [1],
                0,
                145),
            new ExtractedDocumentUnit(
                1,
                0,
                123,
                123,
                "Dans le robot muni du couteau pour p\u00e9trir/concasser, mettez 25 cl d\u2019eau, le beurre en morceaux et le sel. Faites fonctionner le robot en vitesse 3 \u00e0 90\u00b0C pendant 8 min.2 Une fois le programme achev\u00e9, ajoutez la farine et m\u00e9langez en vitesse 6 pendant 2 min.",
                270,
                48,
                [2],
                147,
                417),
            new ExtractedDocumentUnit(
                2,
                0,
                123,
                123,
                "Laissez tourner pendant 2 min.4 Recouvrez une plaque de papier cuisson. \u00c0 l\u2019aide d\u2019une cuill\u00e8re faites de petits tas de p\u00e2te, puis saupou-drez-les de sucre perl\u00e9.",
                162,
                26,
                [3],
                419,
                581),
            new ExtractedDocumentUnit(
                3,
                0,
                123,
                123,
                "Enfournez et faites cuire pendant 25 \u00e0 30 min.4/6 personnes12 min30 min15 minCHOUQUETTESD\u00e9corez d\u2019\u00e9clats de pistaches, de pralines, de noisettes.",
                151,
                20,
                [4],
                583,
                734),
            new ExtractedDocumentUnit(
                4,
                0,
                123,
                123,
                "30 cl de lait demi-\u00e9cr\u00e9m\u00e915 cl d'eau200 g de farine1 sachet de levure chimique3 pinc\u00e9es de sel1 blanc d\u2019\u0153uf165 g de chocolat noir.",
                128,
                24,
                [5],
                736,
                864)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.Contains(projected, chunk =>
            chunk.ChunkType == "footer_titled_item_window_v1"
            && chunk.Text.StartsWith("CHOUQUETTES", StringComparison.Ordinal)
            && chunk.Text.Contains("80 g de beurre", StringComparison.Ordinal)
            && chunk.Text.Contains("sucre perlé", StringComparison.Ordinal)
            && !chunk.Text.Contains("30 cl de lait", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectStructureAware_footer_title_window_keeps_same_page_ingredient_head_past_soft_budget()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Entrees", 1, 41, 41, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                201,
                0,
                41,
                41,
                "72 Temps total : 1 h 38 min 6 asperges vertes 150 g de petits pois écossés 3 carottes 1 c. à s. bombée de farine 8 œufs 35 cl de crème liquide 50 g de parmesan râpé 4 c. à s. d'herbes ciselées 1 noisette de beurre 1 pincée de paprika 2 c. à s. d'huile d'olive Sel, Poivre 1 Coupez les extrémités dures des asperges vertes. Écossez les petits pois.",
                330,
                61,
                [1],
                0,
                330),
            new ExtractedDocumentUnit(202, 0, 41, 41, "Pelez les carottes et coupez-les en petits des. Versez 0,7 L d'eau dans le bol du robot.", 95, 17, [2], 331, 426),
            new ExtractedDocumentUnit(203, 0, 41, 41, "Deposez les legumes dans le panier vapeur et lancez le programme vapeur pour 15 min. A la fin du programme, laissez-les tiedir.", 129, 32, [3], 427, 556),
            new ExtractedDocumentUnit(204, 0, 41, 41, "Lavez et sechez le bol du robot. 2 Dans le bol du robot muni du batteur, mettez la farine et 4 oeufs.", 112, 21, [4], 557, 669),
            new ExtractedDocumentUnit(205, 0, 41, 41, "Lancez le robot vitesse 6 pendant 2 min. Au bout de 20 s, versez progressivement la creme liquide puis ajoutez le paprika, les herbes, le sel et le poivre. 3 Ajoutez les carottes et les petits pois. 4 Prechauffez le four a 180 C. Beurrez un moule et deposez au fond du plat les asperges vertes.", 302, 83, [5], 670, 972),
            new ExtractedDocumentUnit(206, 0, 41, 41, "Versez la moitie de la preparation aux legumes sur les asperges vertes.", 69, 12, [6], 973, 1042),
            new ExtractedDocumentUnit(207, 0, 41, 41, "Cassez delicatement les 4 oeufs restants et versez doucement le reste de la preparation. 5 Rabattez le papier cuisson sur la terrine puis enfournez-la pour 1 h. A la fin de la cuisson, laissez refroidir la terrine avant de la demouler. 8 personnes 18 min 1 h 20 min TERRINE DE LEGUMES", 290, 47, [7], 1043, 1333)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 250,
            overlapWords: 0,
            minWords: 1);

        var footerChunk = Assert.Single(projected, chunk =>
            chunk.Text.StartsWith("TERRINE DE LEGUMES", StringComparison.Ordinal)
            && chunk.Text.Contains("6 asperges vertes", StringComparison.Ordinal));
        Assert.Contains("6 asperges vertes", footerChunk.Text, StringComparison.Ordinal);
        Assert.Contains("Pelez les carottes", footerChunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectStructureAware_footer_title_window_does_not_absorb_previous_titled_item_inventory()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Entrees", 1, 41, 41, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                200,
                0,
                41,
                41,
                "ALPHA CAKES 80 g de farine 30 cl de lait 4 oeufs 50 g de sucre 1 c. a s. d'huile 1 Prechauffez le four. 2 Melangez les ingredients.",
                135,
                33,
                [0],
                0,
                135),
            new ExtractedDocumentUnit(201, 0, 41, 41, "Pelez les carottes et coupez-les en petits des. Versez 0,7 L d'eau dans le bol du robot.", 95, 17, [1], 136, 231),
            new ExtractedDocumentUnit(202, 0, 41, 41, "Deposez les legumes dans le panier vapeur et lancez le programme vapeur pour 15 min.", 84, 16, [2], 232, 316),
            new ExtractedDocumentUnit(203, 0, 41, 41, "Cassez delicatement les oeufs restants et versez doucement le reste de la preparation. 5 Rabattez le papier cuisson sur la terrine puis enfournez-la pour 1 h. TERRINE DE LEGUMES", 174, 31, [3], 317, 491)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 55,
            overlapWords: 0,
            minWords: 1);

        var footerChunk = Assert.Single(projected, chunk =>
            chunk.Text.StartsWith("TERRINE DE LEGUMES", StringComparison.Ordinal)
            && chunk.Text.Contains("Deposez les legumes", StringComparison.Ordinal));
        Assert.DoesNotContain("ALPHA CAKES", footerChunk.Text, StringComparison.Ordinal);
        Assert.Contains("Deposez les legumes", footerChunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectStructureAware_adds_footer_titled_window_when_title_is_glued_to_elided_note()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 123, 123, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                123,
                123,
                "Enfournez et faites cuire pendant 25 \u00e0 30 min.4/6 personnes12 min30 min15 minCHOUQUETTESD\u00e9corez d\u2019\u00e9clats de pistaches.",
                126,
                16,
                [1],
                0,
                126),
            new ExtractedDocumentUnit(
                1,
                0,
                123,
                123,
                "30 cl de lait demi-\u00e9cr\u00e9m\u00e915 cl d'eau200 g de farine1 sachet de levure chimique3 pinc\u00e9es de sel1 blanc d\u2019\u0153uf165 g de chocolat noir1 c. \u00e0 c. d\u2019ar\u00f4me vanille1 Dans le robot muni du couteau pour p\u00e9trir/concasser, mettez 15 cl de lait et 15 cl d\u2019eau.",
                238,
                45,
                [2],
                128,
                366),
            new ExtractedDocumentUnit(
                2,
                0,
                123,
                123,
                "Lancez le robot en vitesse 6 \u00e0 100\u00b0C pour 4 min. Ajoutez la farine, le blanc d\u2019\u0153uf, la levure et le sel, mixez en vitesse 4 pendant 30 s avec le bouchon.2 Formez des boudins en les roulant sur le plan de travail farin\u00e9, puis faites-les cuire \u00e0 la friteuse.",
                256,
                46,
                [3],
                368,
                624),
            new ExtractedDocumentUnit(
                3,
                0,
                123,
                123,
                "Versez dans un bol. Trempez les churros dans la sauce au chocolat et d\u00e9gustez.4 personnes13 min12 min15 minCHURROS SAUCE CHOCOLATL\u2019id\u00e9al pour cette recette est d\u2019avoir un appareil \u00e0 churros qui vous permettra d\u2019avoir des boudins de forme r\u00e9guli\u00e8re.",
                250,
                42,
                [4],
                626,
                876)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.Contains(projected, chunk =>
            chunk.ChunkType == "footer_titled_item_window_v1"
            && chunk.Text.StartsWith("CHURROS SAUCE CHOCOLAT", StringComparison.Ordinal)
            && chunk.Text.Contains("30 cl de lait", StringComparison.Ordinal)
            && chunk.Text.Contains("Formez des boudins", StringComparison.Ordinal)
            && !chunk.Text.Contains("CHOUQUETTES", StringComparison.Ordinal));
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
    public void ProjectStructureAware_prefixes_embedded_title_case_structured_item()
    {
        const string title = "Module au relais";
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 44, 44, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                44,
                44,
                "Controlez la sortie et laissez stabiliser pendant 20 min. 4 operators 30 min"
                + title
                + " Pour 4 operators Materials: relay, sensor. Procedure 1. Inspect status. 2. Record evidence.",
                176,
                29,
                [1],
                0,
                176)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 0,
            minWords: 25);

        var chunk = Assert.Single(projected);
        Assert.StartsWith(title, chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Controlez la sortie", chunk.Text, StringComparison.Ordinal);
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
