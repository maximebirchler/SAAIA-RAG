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
        Assert.Equal([0], projected[0].SourceUnitOrdinals);
        Assert.Equal(1, projected[0].SourceUnitCount);
        Assert.Equal("single_unit", projected[0].ChunkComposition);
        Assert.Equal(1, projected[1].SectionOrdinal);
        Assert.Equal(1, projected[1].UnitOrdinal);
        Assert.Equal(13, projected[1].OffsetStart);
        Assert.Equal(23, projected[1].OffsetEnd);
        Assert.Equal([1], projected[1].SourceUnitOrdinals);
        Assert.Equal(1, projected[1].SourceUnitCount);
        Assert.Equal("single_unit", projected[1].ChunkComposition);
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
    public void ProjectStructureAware_exposes_composite_source_unit_trace()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 1, 1, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(0, 0, 1, 1, "first paragraph with usable content", 35, 5, [1], 0, 35),
            new ExtractedDocumentUnit(1, 0, 1, 1, "second paragraph with related detail", 36, 5, [2], 37, 73)
        };

        var chunk = Assert.Single(RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 20,
            overlapWords: 0,
            minWords: 1));

        Assert.Null(chunk.UnitOrdinal);
        Assert.Equal([0, 1], chunk.SourceUnitOrdinals);
        Assert.Equal(0, chunk.SourceUnitStartOrdinal);
        Assert.Equal(1, chunk.SourceUnitEndOrdinal);
        Assert.Equal(2, chunk.SourceUnitCount);
        Assert.Equal("multi_unit_window", chunk.ChunkComposition);
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
    public void Project_does_not_prefix_quantity_phrase_as_embedded_title()
    {
        const string text =
            "600 g salmon 1 orange 2 c. a s. de Fond de Volaille MAGGI "
            + "300 g carrots 200 ml water 10 stems chives 1 c. a s. cream "
            + "1 Cut the components into large cubes. 2 Verify the result and record the note.";

        var chunk = Assert.Single(RetrievalChunkProjector.Project(
            [new Chunk(0, 1, 1, text)],
            [],
            []));

        Assert.StartsWith("600 g salmon", chunk.Text, StringComparison.Ordinal);
        Assert.False(chunk.Text.StartsWith("Fond de Volaille MAGGI", StringComparison.Ordinal));
        Assert.DoesNotContain("Fond de Volaille MAGGI" + Environment.NewLine + Environment.NewLine, chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_still_prefixes_real_embedded_structured_title_after_sentence_boundary()
    {
        const string text =
            "Previous paragraph ends cleanly. CONTROL VALVE CHECKLIST "
            + "Materials gasket seal kit actuator module. Procedure isolate the device, "
            + "verify zero energy, replace the component, test the assembly, and record the result.";

        var chunk = Assert.Single(RetrievalChunkProjector.Project(
            [new Chunk(0, 1, 1, text)],
            [],
            []));

        Assert.StartsWith("CONTROL VALVE CHECKLIST" + Environment.NewLine + Environment.NewLine, chunk.Text, StringComparison.Ordinal);
        Assert.Contains("Previous paragraph ends cleanly", chunk.Text, StringComparison.Ordinal);
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
    public void ProjectStructureAware_does_not_merge_metadata_prefixed_new_item_below_min_words()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 14, 15, null, null)
        };
        const string first =
            "Accessible Standard Alpha module • 12 units inspected before activation. • Record the panel value and close the cover. • Confirm the final report before release. • Verify the cable marker, archive the checklist, note the operator initials and keep the measurement sheet with the module file.";
        const string second =
            "Duration : 1 hour Beta module Standard setup For the housing • Place the module in the enclosure and record the 12 V value.";
        var units = new[]
        {
            new ExtractedDocumentUnit(
                21,
                0,
                14,
                14,
                first,
                first.Length,
                first.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [14],
                0,
                first.Length),
            new ExtractedDocumentUnit(
                22,
                0,
                15,
                15,
                second,
                second.Length,
                second.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [15],
                first.Length + 2,
                first.Length + 2 + second.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 180,
            overlapWords: 0,
            minWords: 80);

        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.Contains("Alpha module", StringComparison.Ordinal)
            && chunk.Text.Contains("Beta module", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([21]));
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([22]));
    }

    [Fact]
    public void ProjectStructureAware_does_not_overflow_window_for_short_continuation_tail()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 21, 21, null, null)
        };
        var first = string.Join(' ', Enumerable.Range(1, 216).Select(index => $"evidence{index}"));
        var second = "continuation " + string.Join(' ', Enumerable.Range(1, 23).Select(index => $"tail{index}")) + ".";
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                21,
                21,
                first,
                first.Length,
                first.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [21],
                0,
                first.Length),
            new ExtractedDocumentUnit(
                1,
                0,
                21,
                21,
                second,
                second.Length,
                second.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [21],
                first.Length + 2,
                first.Length + 2 + second.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 0,
            minWords: 1);

        Assert.DoesNotContain(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([0, 1]));
        Assert.All(projected, chunk => Assert.True(chunk.TokenCount <= 238, $"Chunk {chunk.ChunkIndex} had {chunk.TokenCount} tokens."));
    }

    [Fact]
    public void ProjectStructureAware_excludes_short_metadata_schedule_cards_from_retrieval_windows()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 12, 12, null, null)
        };
        const string metadataOnly =
            "Preparation : 1 hour Duration : 3 h 30 Cost tier Intermediate";
        const string substantive =
            "Validated operating method The technician inspects the module, records the result, confirms the release evidence, and stores the signed checklist with the equipment file.";
        var units = new[]
        {
            new ExtractedDocumentUnit(
                13,
                0,
                12,
                12,
                metadataOnly,
                metadataOnly.Length,
                metadataOnly.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [12],
                0,
                metadataOnly.Length),
            new ExtractedDocumentUnit(
                14,
                0,
                12,
                12,
                substantive,
                substantive.Length,
                substantive.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [12],
                metadataOnly.Length + 2,
                metadataOnly.Length + 2 + substantive.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 180,
            overlapWords: 0,
            minWords: 12);

        Assert.DoesNotContain(projected, chunk => chunk.Text.Contains("Duration : 3 h 30", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([14])
            && chunk.Text.Contains("Validated operating method", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectStructureAware_excludes_low_substance_standalone_fragments_from_retrieval_windows()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 20, 20, null, null)
        };
        const string substantive =
            "Operational validation method The technician inspects the module, records the pressure value, confirms the release evidence, stores the signed checklist, and reports the final status before handover.";
        var units = new[]
        {
            new ExtractedDocumentUnit(20, 0, 20, 20, "Sel", 3, 1, [20], 0, 3),
            new ExtractedDocumentUnit(21, 0, 20, 20, "31", 2, 1, [20], 5, 7),
            new ExtractedDocumentUnit(22, 0, 20, 20, "2 units", 7, 2, [20], 9, 16),
            new ExtractedDocumentUnit(23, 0, 20, 20, "For", 3, 1, [20], 18, 21),
            new ExtractedDocumentUnit(
                24,
                0,
                20,
                20,
                substantive,
                substantive.Length,
                substantive.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [20],
                23,
                23 + substantive.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 180,
            overlapWords: 0,
            minWords: 25);

        var chunk = Assert.Single(projected);
        Assert.Equal([24], chunk.SourceUnitOrdinals);
        Assert.Contains("Operational validation method", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sel", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("31", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("2 units", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("For", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectStructureAware_keeps_short_substantive_tail_separate_before_next_high_signal_unit()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 17, 18, null, null)
        };
        const string tomatoTail = "Farcissez-en les tomates. 1 pincee d'Herbes de Provence. Enfournez pour 30 minutes. Servez a la sortie du four. Sel et poivre";
        const string magret = "Repos : 2 heures Magret de canard Facile miel au 2 magrets de canard Pelez les gousses d'ail et retirez le germe. Faites des entailles dans la peau des magrets. Melangez 3 c. a soupe de miel avec 2 c. a cafe de sauce soja. Prechauffez le four. Servez accompagne de riz et de legumes !";
        var magretStart = tomatoTail.Length + 1;
        var units = new[]
        {
            new ExtractedDocumentUnit(
                33,
                0,
                17,
                17,
                tomatoTail,
                tomatoTail.Length,
                24,
                [1],
                0,
                tomatoTail.Length),
            new ExtractedDocumentUnit(
                34,
                0,
                18,
                18,
                magret,
                magret.Length,
                58,
                [2],
                magretStart,
                magretStart + magret.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 200,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.Contains("Sel et poivre", StringComparison.Ordinal)
            && chunk.Text.Contains("Magret de canard", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitCount == 1
            && chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([33])
            && chunk.ChunkType == "unit_exact_v1");
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitCount == 1
            && chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([34])
            && chunk.Text.StartsWith("Repos : 2 heures Magret de canard", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectStructureAware_does_not_append_short_new_page_tail_to_complete_structured_unit()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 28, 29, null, null)
        };
        const string creme =
            "Creme brulee Pour 6 personnes 50 cl de creme liquide 6 jaunes d'oeufs 80 g de sucre Preparation Fouettez les jaunes. Faites cuire la creme. Au moment de servir, parsemez de cassonade et faites carameliser. Degustez sans attendre !";
        const string profiterolesTail =
            "Refermez avec la moitie superieure. Nappez les profiteroles de chocolat et servez aussitot !";
        var units = new[]
        {
            new ExtractedDocumentUnit(43, 0, 28, 28, creme, creme.Length, 38, [28], 0, creme.Length),
            new ExtractedDocumentUnit(44, 0, 29, 29, profiterolesTail, profiterolesTail.Length, 13, [29], creme.Length + 2, creme.Length + 2 + profiterolesTail.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.Contains("Creme brulee", StringComparison.Ordinal)
            && chunk.Text.Contains("Nappez les profiteroles", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([43]));
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([44]));
    }

    [Fact]
    public void ProjectStructureAware_stops_short_new_page_continuation_after_complete_substantive_unit()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 12, 13, null, null)
        };
        const string completePrevious =
            "Control panel calibration uses 4 units, a 120 V supply, a 2 A fuse, a 45 min stabilization window and 3 parts recorded. Verify connector torque before powering the module. Record baseline pressure at 2 bar and compare it with the acceptance sheet. Close the cabinet after the display reports stable operation. Keep the completed checklist with the module file.";
        const string nextPageTail =
            "Replace the upper cover. Confirm the actuator reset report and store the spare label.";
        var units = new[]
        {
            new ExtractedDocumentUnit(
                120,
                0,
                12,
                12,
                completePrevious,
                completePrevious.Length,
                completePrevious.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [12],
                0,
                completePrevious.Length),
            new ExtractedDocumentUnit(
                121,
                0,
                13,
                13,
                nextPageTail,
                nextPageTail.Length,
                nextPageTail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [13],
                completePrevious.Length + 2,
                completePrevious.Length + 2 + nextPageTail.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.Contains("Control panel calibration", StringComparison.Ordinal)
            && chunk.Text.Contains("Confirm the actuator reset report", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([120]));
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([121]));
    }

    [Fact]
    public void ProjectStructureAware_repairs_short_lowercase_continuation_tail_when_budget_would_split()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 42, 42, null, null)
        };
        const string previous =
            "Operational note 45 ml of sealant is applied after inspection. The operator checks the housing, records the pressure, validates the screen output, confirms the relay status and keeps the unit in";
        const string tail =
            "service for the validation window. 10 ml of tracer fluid, approximately 4-5 branches 79";
        var units = new[]
        {
            new ExtractedDocumentUnit(
                128,
                0,
                42,
                42,
                previous,
                previous.Length,
                previous.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [42],
                0,
                previous.Length),
            new ExtractedDocumentUnit(
                129,
                0,
                42,
                42,
                tail,
                tail.Length,
                tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [42],
                previous.Length + 2,
                previous.Length + 2 + tail.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 32,
            overlapWords: 0,
            minWords: 12);

        var repaired = Assert.Single(projected);
        Assert.Equal([128, 129], repaired.SourceUnitOrdinals);
        Assert.Contains("keeps the unit in", repaired.Text, StringComparison.Ordinal);
        Assert.Contains("service for the validation window", repaired.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectStructureAware_repairs_short_quantity_tail_that_looks_like_footer_split()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 52, 52, null, null)
        };
        const string previous =
            "Mix the dry elements, verify the enclosure and record the final value. Add the remaining component before closing. Stable operation is confirmed.";
        const string tail = "2 modules 5 ml of calibration fluid 99";
        var units = new[]
        {
            new ExtractedDocumentUnit(
                168,
                0,
                52,
                52,
                previous,
                previous.Length,
                previous.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [52],
                0,
                previous.Length),
            new ExtractedDocumentUnit(
                169,
                0,
                52,
                52,
                tail,
                tail.Length,
                tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                [52],
                previous.Length + 2,
                previous.Length + 2 + tail.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 64,
            overlapWords: 0,
            minWords: 12);

        var repaired = Assert.Single(projected);
        Assert.Equal([168, 169], repaired.SourceUnitOrdinals);
        Assert.Contains("Stable operation is confirmed.", repaired.Text, StringComparison.Ordinal);
        Assert.Contains("2 modules 5 ml of calibration fluid", repaired.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectStructureAware_repairs_realistic_short_continuation_tail_after_overlapped_windows()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 42, 42, null, null)
        };
        const string first =
            "INGRÉDIENTS PRÉPARATION 1 rôti de surlonge de • Placer la grille au centre du four. porc d’environ 1 kg (2 lb) Préchauffer le four à 200 °C (400 °F). 30 ml (2 c. à soupe) • Dans une poêle, dorer le rôti de tous les côtés dans l’huile. d’huile Saler et poivrer. Réserver. Dans la même poêle, faire revenir les échalotes françaises et l’ail. Déglacer à l’aide du bouillon";
        const string second =
            "de poulet, ajouter la moutarde, les pommes et le thym frais. coupées deux en sur Saler et poivrer. la longueur • Cuire au four une heure. Laisser reposer 10 minutes après la 1 tête d’ail, soit environ sortie du four.";
        const string third =
            "Garnir de thym frais et servir avec du couscous, une dizaine de gousses une purée de patates douces et des légumes grillés.";
        const string previous =
            "TRUCS CULINAIRES 45 ml (3 c. à soupe) de Il est possible d’utiliser d’autres coupes du porc, par exemple, les moutarde à l’ancienne côtelettes, le filet ou même la bavette pour cette recette. Pensez 4 coupées à acheter vos protéines lorsqu’elles se retrouvent en solde, ceci pommes, cubes (Red vous permettra d’économiser et de miser au maximum sur la en";
        const string tail =
            "diversité de vos aliments. 10 ml (2 c. à thé) de thym frais, environ 4-5 branches 79";
        const string next =
            "d’huile végétale anti-adhésive et la transférer dans un plat allant au four. 80";
        var units = new[]
        {
            new ExtractedDocumentUnit(123, 0, 42, 42, first, first.Length, 69, [42], 0, first.Length),
            new ExtractedDocumentUnit(124, 0, 42, 42, second, second.Length, 40, [42], first.Length + 2, first.Length + 2 + second.Length),
            new ExtractedDocumentUnit(125, 0, 42, 42, third, third.Length, 22, [42], first.Length + second.Length + 4, first.Length + second.Length + 4 + third.Length),
            new ExtractedDocumentUnit(126, 0, 42, 42, previous, previous.Length, 60, [42], first.Length + second.Length + third.Length + 6, first.Length + second.Length + third.Length + 6 + previous.Length),
            new ExtractedDocumentUnit(127, 0, 42, 42, tail, tail.Length, 17, [42], first.Length + second.Length + third.Length + previous.Length + 8, first.Length + second.Length + third.Length + previous.Length + 8 + tail.Length),
            new ExtractedDocumentUnit(128, 0, 42, 42, next, next.Length, 13, [42], first.Length + second.Length + third.Length + previous.Length + tail.Length + 10, first.Length + second.Length + third.Length + previous.Length + tail.Length + 10 + next.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 200,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([127]));
        var repaired = Assert.Single(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.Contains(126)
            && chunk.SourceUnitOrdinals.Contains(127)
            && chunk.Text.Contains("diversité de vos aliments", StringComparison.Ordinal));
        Assert.Null(IngestionWorker.ResolveRetrievalChunkEmbeddingRejectionReason(repaired));
    }

    [Fact]
    public void ProjectStructureAware_does_not_append_new_page_short_title_after_complete_structured_unit()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 34, 35, null, null)
        };
        const string eclairs =
            "Eclairs au chocolat Pour 8 pieces 250 ml d'eau 80 g de beurre 150 g de farine 4 oeufs Preparation Faites la pate a choux. Garnissez les eclairs puis glacez-les. Vous preferez les eclairs au cafe?";
        const string tarteLead =
            "Facile Tarte citron au meringuee Pour la pate minute sucree Dans un saladier, melangez la farine et le beurre.";
        var units = new[]
        {
            new ExtractedDocumentUnit(60, 0, 34, 34, eclairs, eclairs.Length, 34, [34], 0, eclairs.Length),
            new ExtractedDocumentUnit(61, 0, 35, 35, tarteLead, tarteLead.Length, 17, [35], eclairs.Length + 2, eclairs.Length + 2 + tarteLead.Length)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk =>
            chunk.Text.Contains("Eclairs au chocolat", StringComparison.Ordinal)
            && chunk.Text.Contains("Tarte citron", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([60]));
        Assert.Contains(projected, chunk =>
            chunk.SourceUnitOrdinals is not null
            && chunk.SourceUnitOrdinals.SequenceEqual([61]));
    }

    [Fact]
    public void ProjectStructureAware_keeps_clean_short_title_with_body_without_inline_index_metadata()
    {
        const string firstTitle =
            "Alpha Beta Module [Index: ] MCRC01072833_BO_Alpha_Beta_Module-010 MCRC01072992_SE_Alpha_Beta_Module-007";
        const string firstMetadata =
            "016CategoryControlsModes classificationCategory entriesFor 4 items 16";
        const string firstBody =
            "ITEMS STEPS 4 units 1. Inspect the module and record the result.";
        const string secondTitle =
            "Gamma Delta Module [Index: ] MCRC01072834_BO_Gamma_Delta_Module-010 MCRC01072993_SE_Gamma_Delta_Module-007";
        const string secondMetadata =
            "020CategoryControlsModes classificationCategory entriesFor 2 items 20";
        const string secondBody =
            "ITEMS STEPS 2 units 1. Calibrate the panel and save the report.";
        var pageText = string.Join(
            Environment.NewLine + Environment.NewLine,
            firstTitle,
            firstMetadata,
            firstBody,
            secondTitle,
            secondMetadata,
            secondBody);
        var pages = new[]
        {
            new ExtractedPdfPage(
                16,
                pageText,
                pageText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                pageText.Length,
                [16])
        };
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 16, 16, null, null)
        };
        var units = DocumentUnitExtractor.Extract(pages, sections);

        Assert.Contains(units, unit => string.Equals(unit.Text, "Alpha Beta Module", StringComparison.Ordinal));
        Assert.Contains(units, unit => string.Equals(unit.Text, "Gamma Delta Module", StringComparison.Ordinal));
        Assert.DoesNotContain(units, unit => unit.Text.Contains("MCRC010", StringComparison.Ordinal));

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 120,
            overlapWords: 0,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk => chunk.Text.Contains("MCRC010", StringComparison.Ordinal));
        Assert.DoesNotContain(projected, chunk => chunk.Text.Contains("classificationCategory", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.Text.Contains("Alpha Beta Module", StringComparison.Ordinal)
            && chunk.Text.Contains("Inspect the module", StringComparison.Ordinal)
            && !chunk.Text.Contains("Gamma Delta Module", StringComparison.Ordinal));
        Assert.Contains(projected, chunk =>
            chunk.Text.Contains("Gamma Delta Module", StringComparison.Ordinal)
            && chunk.Text.Contains("Calibrate the panel", StringComparison.Ordinal)
            && !chunk.Text.Contains("Alpha Beta Module", StringComparison.Ordinal));
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
    public void ProjectStructureAware_does_not_add_footer_window_for_spaced_letter_layout_noise()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 21, 21, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                21,
                21,
                "6 jaunes d'oeufs 80 g de sucre 50 cl de lait 1 c. a c. de vanille liquide Dans le robot muni du batteur, mettez les jaunes d'oeufs puis mixez en vitesse 7 pendant 7 min.",
                188,
                38,
                [1],
                0,
                188),
            new ExtractedDocumentUnit(
                1,
                0,
                21,
                21,
                "Lancez le robot en vitesse 4 a 85 C pendant 12 min. A la fin de la cuisson, laissez refroidir, puis servez. A S I QU E Cette recette permet de preparer 125 g de beurre.",
                176,
                34,
                [2],
                190,
                366)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk => chunk.ChunkType == "footer_titled_item_window_v1");
    }

    [Fact]
    public void ProjectStructureAware_does_not_add_footer_window_for_short_digit_brand_fragment()
    {
        var sections = new[]
        {
            new ExtractedDocumentSection(0, "Document", 1, 52, 52, null, null)
        };
        var units = new[]
        {
            new ExtractedDocumentUnit(
                0,
                0,
                52,
                52,
                "1 kg de courgettes 500 ml d'eau 2 cubes de bouillon legumes et herbes 150 g de ricotta 20 feuilles de menthe",
                112,
                23,
                [1],
                0,
                112),
            new ExtractedDocumentUnit(
                1,
                0,
                52,
                52,
                "Duo legumes et herbes du marche MAGGI 2 Demarrer la cuisson en lancant le programme soupe pour 25 min. Ajoutez la menthe et la ricotta puis mixez en pulse pendant 10 s.",
                170,
                31,
                [2],
                114,
                284)
        };

        var projected = RetrievalChunkProjector.ProjectStructureAware(
            sections,
            units,
            maxWords: 220,
            overlapWords: 35,
            minWords: 25);

        Assert.DoesNotContain(projected, chunk => chunk.ChunkType == "footer_titled_item_window_v1");
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
