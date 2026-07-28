using SAAIA.Contracts.DocumentIntelligence;
using Xunit;

public sealed class DoclingCanonicalDocumentAdapterTests
{
    [Fact]
    public void Project_PreservesStructureCoordinatesReadingOrderAndProvenance()
    {
        var source = BuildDocument();
        var context = BuildContext();

        var document = DoclingCanonicalDocumentAdapter.Project(context, source);

        var page = Assert.Single(document.Pages);
        Assert.Equal(4, page.Blocks.Count);
        Assert.Single(page.Tables);
        Assert.Single(page.Figures);
        Assert.Equal("heading", page.Blocks[0].Text.Raw);
        Assert.Equal("heading", page.Blocks[0].Text.Canonical);
        Assert.Equal("heading", page.Blocks[0].Text.Normalized);
        Assert.Equal("section_header", page.Blocks[0].BlockType);
        var blockPolygon = Assert.IsType<CanonicalPolygon>(page.Blocks[0].Polygon);
        Assert.Equal(0.1, blockPolygon.Points[0].X, 6);
        Assert.Equal(0.05, blockPolygon.Points[0].Y, 6);
        Assert.Equal(0.9, blockPolygon.Points[2].X, 6);
        Assert.Equal(0.15, blockPolygon.Points[2].Y, 6);

        var footer = Assert.Single(
            page.Blocks,
            static block => block.BlockType == "page_footer");
        Assert.True(footer.IsRepeatedFurniture);
        Assert.Equal("docling", footer.Provenance.Engine);
        Assert.Equal("docling-serve-test", footer.Provenance.EngineVersion);
        Assert.Equal(
            "sha256:test",
            footer.Provenance.Attributes["deploymentRevision"]);

        var table = page.Tables[0];
        Assert.Equal(2, table.RowCount);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(4, table.Cells.Count);
        Assert.Equal("column_header", table.Cells[0].CellRole);
        var cellPolygon = Assert.IsType<CanonicalPolygon>(table.Cells[0].Polygon);
        Assert.Equal(0.1, cellPolygon.Points[0].X, 6);
        Assert.Equal(0.5, cellPolygon.Points[0].Y, 6);
        Assert.Equal(0.5, cellPolygon.Points[2].X, 6);
        Assert.Equal(0.6, cellPolygon.Points[2].Y, 6);

        var section = Assert.Single(document.SectionTree);
        Assert.Equal(1, section.Level);
        Assert.Contains(page.Blocks[0].BlockId, section.TitleBlockIds);
        Assert.Contains(
            page.Blocks.Single(static block => block.BlockType == "text").BlockId,
            section.ContentBlockIds);
        Assert.DoesNotContain(footer.BlockId, section.ContentBlockIds);
        Assert.Equal(2, document.Relations.Count);
        Assert.All(
            document.Relations,
            static relation => Assert.Equal("caption_for", relation.RelationType));
        Assert.Empty(CanonicalContractValidator.Validate(document));
    }

    [Fact]
    public void Project_IsStableForTheSameSourceAndModelOutput()
    {
        var source = BuildDocument();
        var context = BuildContext();

        var first = DoclingCanonicalDocumentAdapter.Project(context, source);
        var second = DoclingCanonicalDocumentAdapter.Project(context, source);

        Assert.Equal(
            CanonicalContractJson.SerializeCanonical(first),
            CanonicalContractJson.SerializeCanonical(second));
    }

    [Fact]
    public void Project_RejectsCellsOutsideDeclaredTableDimensions()
    {
        var source = BuildDocument();
        source.Tables[0].Data.Cells[0].RowSpan = 3;

        var exception = Assert.Throws<InvalidDataException>(
            () => DoclingCanonicalDocumentAdapter.Project(BuildContext(), source));

        Assert.Contains("exceeds its declared dimensions", exception.Message);
    }

    [Fact]
    public void Project_FlagsSparseAndOverlappingTableGridsAndCarriesSignalsToChunks()
    {
        var response = BuildResponse();
        var source = response.Document.JsonContent!;
        source.Tables[0].Data.Cells.RemoveAt(3);
        source.Tables[0].Data.Cells.Add(
            Cell(0, 0, "duplicate", Box(10, 100, 50, 120, "TOPLEFT")));

        var document = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            source);
        var page = Assert.Single(document.Pages);

        Assert.Contains("canonical_table_sparse_grid", page.QualityFlags);
        Assert.Contains("canonical_table_overlapping_grid", page.QualityFlags);

        var chunks = DoclingCanonicalRetrievalProjector.Project(
            document,
            source,
            maxWords: 220,
            minWords: 1);
        var tableChunk = Assert.Single(
            chunks,
            chunk => chunk.ChunkType
                     == DoclingCanonicalRetrievalProjector.TableChunkType);
        Assert.NotNull(tableChunk.ExtractionQualitySignals);
        Assert.Contains(
            "canonical_table_sparse_grid",
            tableChunk.ExtractionQualitySignals!);
        Assert.Contains(
            "canonical_table_overlapping_grid",
            tableChunk.ExtractionQualitySignals!);
    }

    [Fact]
    public void Project_PreservesRegionalTableRepairRawTextProvenanceAndChunkSignal()
    {
        var response = BuildResponse();
        var source = response.Document.JsonContent!;
        var tableSource = source.Tables[0];
        tableSource.Data.Cells[2].OriginalText = "C D";
        tableSource.Data.Cells[2].Text = "C";
        tableSource.Data.Cells[2].TextRepair = TextRepair("split_absorbed_text", 0.99);
        tableSource.Data.Cells[3].OriginalText = "D";
        tableSource.Data.Cells[3].TextRepair = TextRepair("add_missing_cell", 0.98);
        tableSource.TableRepair = new()
        {
            SchemaVersion = "saaia_table_text_repair_v1",
            Engine = "RapidOCR",
            EngineVersion = "3.9.1",
            ModelId = "PP-OCRv6_det_small+PP-OCRv6_rec_small",
            Reason = "sparse_grid_with_ocr_coverage",
            OriginalCellCount = 3,
            FinalCellCount = 4,
            RepairedCellCount = 1,
            AddedCellCount = 1,
            OcrLineCount = 6,
            MeanConfidence = 0.985,
            DurationMs = 727
        };
        response.TableRepairs.Add(new()
        {
            TableRef = tableSource.SelfRef,
            Status = "applied",
            Reason = "sparse_grid_repaired",
            OcrDurationMs = 727
        });
        response.Timings["saaia_table_repair"] = new()
        {
            Scope = "document",
            Count = 1,
            Times = [0.75]
        };

        var document = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            source);
        var page = Assert.Single(document.Pages);
        var table = Assert.Single(page.Tables);
        var splitCell = Assert.Single(
            table.Cells,
            static cell => cell.RowIndex == 1 && cell.ColumnIndex == 0);
        Assert.Equal("C D", splitCell.Text.Raw);
        Assert.Equal("C", splitCell.Text.Canonical);
        var addedCell = Assert.Single(
            table.Cells,
            static cell => cell.RowIndex == 1 && cell.ColumnIndex == 1);
        Assert.Equal("D", addedCell.Text.Raw);
        Assert.Equal("D", addedCell.Text.Canonical);
        Assert.Contains(
            "canonical_table_regional_ocr_repaired",
            page.QualityFlags);
        Assert.DoesNotContain("canonical_table_sparse_grid", page.QualityFlags);
        Assert.Equal(
            "RapidOCR",
            table.Provenance.Attributes["regionalTableRepairEngine"]);
        Assert.Equal(
            "4",
            table.Provenance.Attributes["regionalTableRepairFinalCellCount"]);

        var chunks = DoclingCanonicalRetrievalProjector.Project(
            document,
            source,
            maxWords: 220,
            minWords: 1);
        var tableChunk = Assert.Single(
            chunks,
            chunk => chunk.ChunkType
                     == DoclingCanonicalRetrievalProjector.TableChunkType);
        Assert.Contains(
            "canonical_table_regional_ocr_repaired",
            tableChunk.ExtractionQualitySignals!);

        var stages = DoclingIngestionStageManifestFactory.Create(
            new(),
            new()
            {
                ChunkMaxWords = 220,
                ChunkMinWords = 1,
                EmbeddingsBatchSize = 16
            },
            BuildRagOptions(),
            response,
            new(3, 4, 5, 6),
            "head-source-hash");
        var repairStage = Assert.Single(
            stages,
            static stage => stage.StageId == "regional_table_ocr_repair");
        Assert.Equal("table_text_repair", repairStage.StageType);
        Assert.Equal(750, repairStage.DurationMs);
        Assert.Equal("1", repairStage.Attributes["appliedTableCount"]);
    }

    [Fact]
    public void Project_SlicesMultiRegionTextUsingDoclingUnicodeCharacterSpans()
    {
        var source = BuildDocument();
        source.Pages["2"] = new()
        {
            PageNumber = 2,
            Size = new() { Width = 100, Height = 200 }
        };
        var body = source.Texts.Single(
            static item => item.SelfRef == "#/texts/1");
        body.OriginalText = "Alpha 😀 Beta Gamma";
        body.Text = "A normalized value whose offsets are not provenance offsets";
        body.Provenance =
        [
            new()
            {
                PageNumber = 1,
                BoundingBox = Box(10, 150, 90, 130, "BOTTOMLEFT"),
                CharacterSpan = [0, 12]
            },
            new()
            {
                PageNumber = 2,
                BoundingBox = Box(10, 190, 90, 170, "BOTTOMLEFT"),
                CharacterSpan = [13, 18]
            }
        ];

        var document = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            source);
        var blocks = document.Pages
            .SelectMany(static page => page.Blocks)
            .Where(block =>
                block.Provenance.Attributes["doclingSelfRef"] == body.SelfRef)
            .ToArray();

        Assert.Equal(2, blocks.Length);
        Assert.Equal("Alpha 😀 Beta", blocks[0].Text.Raw);
        Assert.Equal("Alpha 😀 Beta", blocks[0].Text.Canonical);
        Assert.Equal("Gamma", blocks[1].Text.Raw);
        Assert.Equal("Gamma", blocks[1].Text.Canonical);
        Assert.NotEqual(blocks[0].BlockId, blocks[1].BlockId);
        Assert.All(
            blocks,
            static block => Assert.Contains(
                "multi_region_source_item",
                block.QualityFlags));
        Assert.Equal(
            "0",
            blocks[0].Provenance.Attributes["doclingCharSpanStart"]);
        Assert.Equal(
            "12",
            blocks[0].Provenance.Attributes["doclingCharSpanEndExclusive"]);
        Assert.Equal(
            "13",
            blocks[1].Provenance.Attributes["doclingCharSpanStart"]);
        Assert.Equal(
            "18",
            blocks[1].Provenance.Attributes["doclingCharSpanEndExclusive"]);
        Assert.Empty(CanonicalContractValidator.Validate(document));
    }

    [Fact]
    public void Project_RejectsInvalidMultiRegionCharacterSpan()
    {
        var source = BuildDocument();
        source.Pages["2"] = new()
        {
            PageNumber = 2,
            Size = new() { Width = 100, Height = 200 }
        };
        var body = source.Texts.Single(
            static item => item.SelfRef == "#/texts/1");
        body.OriginalText = "short";
        body.Text = "short";
        body.Provenance =
        [
            new()
            {
                PageNumber = 1,
                BoundingBox = Box(10, 150, 90, 130, "BOTTOMLEFT"),
                CharacterSpan = [0, 5]
            },
            new()
            {
                PageNumber = 2,
                BoundingBox = Box(10, 190, 90, 170, "BOTTOMLEFT"),
                CharacterSpan = [6, 10]
            }
        ];

        var exception = Assert.Throws<InvalidDataException>(
            () => DoclingCanonicalDocumentAdapter.Project(
                BuildContext(),
                source));

        Assert.Contains(body.SelfRef, exception.Message);
        Assert.Contains("[6, 10)", exception.Message);
        Assert.Contains("length 5", exception.Message);
    }

    [Fact]
    public void LegacyProjection_KeepsModelReadingOrderAndTableContent()
    {
        var source = BuildDocument();
        var response = new DoclingConvertResponse
        {
            Status = "success",
            Document = new()
            {
                FileName = "fixture.pdf",
                JsonContent = source
            }
        };

        var extraction = DoclingLegacyExtractionAdapter.Project(response);

        var page = Assert.Single(extraction.Pages);
        Assert.Equal("docling", extraction.Source);
        Assert.Equal(1, page.ImageCount);
        Assert.Equal(100, page.WidthPoints);
        Assert.Equal(200, page.HeightPoints);
        Assert.Contains("Canonical body", page.Text);
        Assert.Contains("A | B", page.Text);
        Assert.Contains("C | D", page.Text);
        Assert.Contains("document_intelligence_docling", page.Quality!.Signals);
        Assert.Contains("spatial_blocks_available", page.Quality.Signals);
        Assert.Contains("table_structure_available", page.Quality.Signals);
        Assert.NotEmpty(extraction.Tokens);
    }

    [Fact]
    public void StageManifest_RecordsPinnedEnginesModelsOptionsAndTimings()
    {
        var response = BuildResponse();
        response.Timings["page_parse"] = new()
        {
            Scope = "page",
            Count = 2,
            Times = [0.1, 0.2]
        };
        response.Timings["ocr"] = new()
        {
            Scope = "page",
            Count = 1,
            Times = [1.25]
        };
        response.Confidence = new()
        {
            OcrScore = 0.98,
            LayoutScore = 0.91,
            TableScore = 0.89
        };
        var documentIntelligence = new DocumentIntelligenceOptions();
        var ingestion = new IngestionOptions
        {
            ChunkMaxWords = 220,
            ChunkMinWords = 25,
            EmbeddingsBatchSize = 16
        };
        var rag = BuildRagOptions();

        var stages = DoclingIngestionStageManifestFactory.Create(
            documentIntelligence,
            ingestion,
            rag,
            response,
            new(3, 4, 5, 6),
            "head-source-hash");

        Assert.Equal(9, stages.Count);
        var parse = Assert.Single(stages, static stage => stage.StageType == "parse");
        Assert.Equal(300, parse.DurationMs);
        Assert.Equal(documentIntelligence.ParseEngineVersion, parse.EngineVersion);
        Assert.Equal(
            documentIntelligence.DeploymentRevision,
            parse.Attributes["deploymentRevision"]);
        var ocr = Assert.Single(stages, static stage => stage.StageType == "ocr");
        Assert.Equal(1250, ocr.DurationMs);
        Assert.Equal(documentIntelligence.OcrEngineVersion, ocr.EngineVersion);
        Assert.Equal("0.98", ocr.Attributes["ocrScore"]);
        var hierarchy = Assert.Single(
            stages,
            static stage => stage.StageType == "heading_hierarchy");
        Assert.Equal(
            "bookmarks_numbering_style",
            hierarchy.Attributes["signals"]);
        Assert.All(stages, static stage => Assert.Equal(64, stage.OptionsSha256.Length));
    }

    [Fact]
    public void BundleFactory_ProducesSpatialBlocksTableCellsAndPublishedChunkAnchor()
    {
        var response = BuildResponse();
        response.Confidence = new() { MeanScore = 0.92 };
        var options = new DocumentIntelligenceOptions();
        var ingestion = new IngestionOptions
        {
            ChunkMaxWords = 220,
            ChunkMinWords = 25,
            EmbeddingsBatchSize = 16
        };
        var rag = BuildRagOptions();
        var stages = DoclingIngestionStageManifestFactory.Create(
            options,
            ingestion,
            rag,
            response,
            new(3, 4, 5, 6),
            "head-source-hash");
        var documentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var canonical = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            response.Document.JsonContent!);
        var chunk = new ProjectedRetrievalChunk(
            0,
            0,
            0,
            1,
            1,
            "Canonical body",
            2,
            [1],
            "unit_exact_v1");

        var bundle = DoclingCanonicalBundleFactory.Create(new(
            documentId,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            7,
            new string('a', 64),
            1234,
            "fixture.pdf",
            "head-source-hash",
            DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            new()
            {
                ProfileId = "hardware_fixture",
                LogicalProcessorCount = 8,
                MemoryBytes = 16_000_000_000,
                Devices = [new() { DeviceId = "cpu:0", DeviceType = "cpu" }]
            },
            options,
            stages,
            canonical,
            response,
            [chunk]));

        var page = Assert.Single(bundle.Document.Pages);
        Assert.Equal(4, page.Blocks.Count);
        Assert.Equal(4, Assert.Single(page.Tables).Cells.Count);
        var anchor = Assert.Single(bundle.SourceAnchors);
        Assert.Equal("page_block_and_table_cell", anchor.Precision);
        Assert.Equal(4, Assert.Single(anchor.Regions).TableCellIds.Count);
        Assert.Equal(
            DocumentFoundationRepo
                .BuildStableRetrievalChunkId(documentId, 7, 0)
                .ToString("D"),
            anchor.ProjectionId);
        Assert.Empty(CanonicalIngestionBundleValidator.Validate(bundle));
    }

    [Fact]
    public void CanonicalRetrievalProjection_CarriesExactBlocksAndTableCells()
    {
        var response = BuildResponse();
        var canonical = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            response.Document.JsonContent!);

        var chunks = DoclingCanonicalRetrievalProjector.Project(
            canonical,
            response.Document.JsonContent!,
            maxWords: 220,
            minWords: 1);
        var headingBlockId = canonical.Pages
            .SelectMany(static page => page.Blocks)
            .Single(static block => block.BlockType == "section_header")
            .BlockId;

        var content = Assert.Single(
            chunks,
            chunk => chunk.ChunkType
                     == DoclingCanonicalRetrievalProjector.ContentChunkType);
        Assert.Contains("Canonical body", content.Text);
        Assert.NotEmpty(content.CanonicalBlockIds!);
        Assert.DoesNotContain(headingBlockId, content.CanonicalBlockIds!);
        Assert.Contains(headingBlockId, content.CanonicalContextBlockIds!);
        Assert.Empty(content.CanonicalTableCellIds!);

        var table = Assert.Single(
            chunks,
            chunk => chunk.ChunkType
                     == DoclingCanonicalRetrievalProjector.TableChunkType);
        Assert.Contains("A | B", table.Text);
        Assert.Contains("C | D", table.Text);
        Assert.Equal(4, table.CanonicalTableCellIds!.Count);
        Assert.DoesNotContain(headingBlockId, table.CanonicalBlockIds!);
        Assert.Contains(headingBlockId, table.CanonicalContextBlockIds!);
        Assert.DoesNotContain(
            chunks,
            chunk => chunk.ContentRole
                     == RetrievalContentClassifier.NavigationRole);

        var options = new DocumentIntelligenceOptions();
        var stages = DoclingIngestionStageManifestFactory.Create(
            options,
            new()
            {
                ChunkMaxWords = 220,
                ChunkMinWords = 1,
                EmbeddingsBatchSize = 16
            },
            BuildRagOptions(),
            response,
            new(3, 4, 5, 6),
            "head-source-hash");
        var bundle = DoclingCanonicalBundleFactory.Create(new(
            canonical.DocumentId,
            canonical.RevisionId,
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            7,
            canonical.Source.Sha256,
            canonical.Source.SizeBytes,
            "fixture.pdf",
            "head-source-hash",
            DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            new()
            {
                ProfileId = "hardware_fixture",
                LogicalProcessorCount = 8,
                MemoryBytes = 16_000_000_000,
                Devices = [new() { DeviceId = "cpu:0", DeviceType = "cpu" }]
            },
            options,
            stages,
            canonical,
            response,
            chunks.Where(IngestionWorker.ShouldPublishRetrievalChunk).ToArray()));

        Assert.Equal(2, bundle.SourceAnchors.Count);
        var tableAnchor = Assert.Single(
            bundle.SourceAnchors,
            anchor => anchor.Precision == "table_cell");
        Assert.Equal(
            4,
            tableAnchor.Regions.SelectMany(region => region.TableCellIds).Count());
        var contentAnchor = Assert.Single(
            bundle.SourceAnchors,
            anchor => anchor.Precision == "block");
        Assert.NotEmpty(
            contentAnchor.Regions.SelectMany(region => region.BlockIds));
        Assert.DoesNotContain(
            headingBlockId,
            contentAnchor.Regions.SelectMany(region => region.BlockIds));
        Assert.DoesNotContain(
            bundle.SourceAnchors,
            anchor => anchor.Regions
                .SelectMany(region => region.BlockIds)
                .Contains(headingBlockId, StringComparer.Ordinal));
        Assert.Empty(CanonicalIngestionBundleValidator.Validate(bundle));
    }

    [Fact]
    public void NativeTextCoverageReconciliation_RecoversOnlyProvenMissingNavigationText()
    {
        var response = BuildResponse();
        var source = response.Document.JsonContent!;
        source.Tables[0].Label = "document_index";
        var canonical = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            source);
        const string nativeText = """
heading
Canonical body
A B
C D
D C B A
APPENDIX B: REFERENCES................................17
""";
        var nativePage = new ExtractedPdfPage(
            1,
            nativeText,
            12,
            nativeText.Length,
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(nativeText)),
            PdfPageExtractionQuality.FromText(
                nativeText,
                12,
                nativeText.Length),
            WidthPoints: 100,
            HeightPoints: 200);
        var nativeExtraction = new PdfExtractionResult(
            [],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var summary =
            CanonicalNativeTextCoverageReconciler.Apply(
                canonical,
                nativeExtraction,
                enabled: true,
                minimumLineCoverage: 0.90);

        Assert.Equal(1, summary.AuditedPageCount);
        Assert.Equal(1, summary.RecoveredLineCount);
        Assert.Equal(1, summary.RecoveredBlockCount);
        var recovery = Assert.Single(
            canonical.Pages[0].Blocks,
            static block =>
                block.BlockType
                == CanonicalNativeTextCoverageReconciler
                    .RecoveryBlockType);
        Assert.Equal(
            "APPENDIX B: REFERENCES................................17",
            recovery.Text.Retrieval);
        Assert.Contains(
            CanonicalNativeTextCoverageReconciler
                .RecoveryQualityFlag,
            recovery.QualityFlags);
        Assert.Equal(
            "PdfPig",
            recovery.Provenance.Engine);
        Assert.Equal(
            RetrievalContentClassifier.NavigationRole,
            recovery.Provenance.Attributes["contentRoleHint"]);

        var chunks = DoclingCanonicalRetrievalProjector.Project(
            canonical,
            source,
            maxWords: 220,
            minWords: 1);
        var recoveredChunk = Assert.Single(
            chunks,
            chunk => chunk.Text.Contains(
                "APPENDIX B: REFERENCES",
                StringComparison.Ordinal));
        Assert.Equal(
            RetrievalContentClassifier.NavigationRole,
            recoveredChunk.ContentRole);
        Assert.Contains(
            recovery.BlockId,
            recoveredChunk.CanonicalBlockIds!);

        var options = new DocumentIntelligenceOptions();
        var stages = DoclingIngestionStageManifestFactory.Create(
            options,
            new()
            {
                ChunkMaxWords = 220,
                ChunkMinWords = 1,
                EmbeddingsBatchSize = 16
            },
            BuildRagOptions(),
            response,
            new(3, 4, 5, 6),
            "head-source-hash",
            summary);
        var reconciliationStage = Assert.Single(
            stages,
            static stage =>
                stage.StageId
                == CanonicalNativeTextCoverageReconciler.StageId);
        Assert.Equal(
            "text_coverage_reconciliation",
            reconciliationStage.StageType);
        Assert.Equal(
            "1",
            reconciliationStage.Attributes["recoveredBlockCount"]);

        var bundle = DoclingCanonicalBundleFactory.Create(new(
            canonical.DocumentId,
            canonical.RevisionId,
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            7,
            canonical.Source.Sha256,
            canonical.Source.SizeBytes,
            "fixture.pdf",
            "head-source-hash",
            DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            new()
            {
                ProfileId = "hardware_fixture",
                LogicalProcessorCount = 8,
                MemoryBytes = 16_000_000_000,
                Devices =
                [
                    new()
                    {
                        DeviceId = "cpu:0",
                        DeviceType = "cpu"
                    }
                ]
            },
            options,
            stages,
            canonical,
            response,
            chunks
                .Where(IngestionWorker.ShouldPublishRetrievalChunk)
                .ToArray()));
        Assert.Contains(
            bundle.Document.Pages[0].Blocks,
            block => block.BlockId == recovery.BlockId);
        Assert.Empty(
            CanonicalIngestionBundleValidator.Validate(bundle));
    }

    [Fact]
    public void CanonicalRetrievalProjection_PublishesNativeLayoutAlternative()
    {
        var response = BuildResponse();
        var source = response.Document.JsonContent!;
        var canonical = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            source);
        const string recoveredText =
            "LEFT COLUMN ALPHA then LEFT COLUMN BRAVO";
        canonical.Pages[0].Blocks.Add(new()
        {
            BlockId = "native_layout_alternative_test",
            BlockType =
                CanonicalNativeLayoutReconciler.RecoveryBlockType,
            Ordinal = canonical.Pages[0].Blocks.Count,
            ReadingOrder = canonical.Pages[0].Blocks.Count,
            Text = new()
            {
                Raw = recoveredText,
                Canonical = recoveredText,
                Normalized = recoveredText.ToLowerInvariant(),
                Retrieval = recoveredText,
                Display = recoveredText,
                RawSha256 = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(
                                recoveredText)))
                    .ToLowerInvariant()
            },
            QualityFlags =
            [
                CanonicalNativeLayoutReconciler
                    .RecoveryQualityFlag
            ],
            Provenance = new()
            {
                StageId =
                    CanonicalNativeTextCoverageReconciler.StageId,
                Method =
                    "native_layout_reading_order_reconciliation",
                Engine = "PdfPig",
                EngineVersion = "test",
                Attributes = new(StringComparer.Ordinal)
                {
                    ["contentRoleHint"] =
                        RetrievalContentClassifier.ContentRole,
                    ["semanticDecisionOwner"] = "llm_client"
                }
            }
        });

        var chunks = DoclingCanonicalRetrievalProjector.Project(
            canonical,
            source,
            maxWords: 220,
            minWords: 1);

        var recoveredChunk = Assert.Single(
            chunks,
            chunk => chunk.Text.Contains(
                recoveredText,
                StringComparison.Ordinal));
        Assert.Contains(
            "native_layout_alternative_test",
            recoveredChunk.CanonicalBlockIds!);
        Assert.Contains(
            CanonicalNativeLayoutReconciler.RecoveryQualityFlag,
            recoveredChunk.ExtractionQualitySignals!);
        Assert.Equal(
            DoclingCanonicalRetrievalProjector
                .NativeLayoutAlternativeChunkType,
            recoveredChunk.ChunkType);
        Assert.Equal(recoveredText, recoveredChunk.Text);
        Assert.Equal(
            "canonical_native_layout_alternative",
            recoveredChunk.ChunkComposition);
        Assert.Null(recoveredChunk.SectionOrdinal);
        Assert.Null(recoveredChunk.SectionTitle);
        Assert.Null(recoveredChunk.HeadingPath);
        Assert.Empty(recoveredChunk.CanonicalContextBlockIds!);
    }

    [Fact]
    public void NativeTextCoverageReconciliation_SkipsPagesWithInvalidControlCharacters()
    {
        var response = BuildResponse();
        var canonical = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            response.Document.JsonContent!);
        const string nativeText =
            "Ô\u008e±º corrupted native text layer";
        var nativePage = new ExtractedPdfPage(
            1,
            nativeText,
            5,
            nativeText.Length,
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(nativeText)),
            PdfPageExtractionQuality.FromText(
                nativeText,
                5,
                nativeText.Length));
        var nativeExtraction = new PdfExtractionResult(
            [],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var summary =
            CanonicalNativeTextCoverageReconciler.Apply(
                canonical,
                nativeExtraction);

        Assert.Equal(1, summary.SkippedLowQualityPageCount);
        Assert.Equal(0, summary.AuditedPageCount);
        Assert.Equal(0, summary.RecoveredBlockCount);
        Assert.DoesNotContain(
            canonical.Pages[0].Blocks,
            static block =>
                block.BlockType
                == CanonicalNativeTextCoverageReconciler
                    .RecoveryBlockType);
    }

    [Fact]
    public void NativeTextCoverageReconciliation_IncludesPartiallyCoveredPreviousLineAsContext()
    {
        var response = BuildResponse();
        var source = response.Document.JsonContent!;
        source.Tables[0].Label = "document_index";
        var body = source.Texts.Single(
            static item => item.SelfRef == "#/texts/1");
        body.OriginalText =
            "Review provisional impact levels and finalize information";
        body.Text = body.OriginalText;
        source.Tables[0].Data.Cells[0].Text = "type impact";
        var canonical = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            source);
        const string nativeText = """
Review provisional impact levels and finalize information type impact
Levels................................23
""";
        var nativePage = new ExtractedPdfPage(
            1,
            nativeText,
            11,
            nativeText.Length,
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(nativeText)),
            PdfPageExtractionQuality.FromText(
                nativeText,
                11,
                nativeText.Length));
        var nativeExtraction = new PdfExtractionResult(
            [],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var summary =
            CanonicalNativeTextCoverageReconciler.Apply(
                canonical,
                nativeExtraction);

        Assert.Equal(2, summary.RecoveredLineCount);
        var recovery = Assert.Single(
            canonical.Pages[0].Blocks,
            static block =>
                block.BlockType
                == CanonicalNativeTextCoverageReconciler
                    .RecoveryBlockType);
        Assert.Equal(
            "Review provisional impact levels and finalize information type impact"
            + Environment.NewLine
            + "Levels................................23",
            recovery.Text.Retrieval);
    }

    [Fact]
    public void NativeTextCoverageReconciliation_DoesNotReinjectCoveredHyphenFragmentOrNativeNoise()
    {
        var response = BuildResponse();
        var source = response.Document.JsonContent!;
        var body = source.Texts.Single(
            static item => item.SelfRef == "#/texts/1");
        body.OriginalText =
            "Coupez la poitrine fumée en lardons puis plongezles dans une casserole. "
            + "Stoppez la cuisson après ébullition.";
        body.Text = body.OriginalText;
        var canonical = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            source);
        const string nativeText = """
• Coupez la poitrine fumée en lardons puis plongez-
ffl e s u r Stoppez la cuisson après ébullition.
""";
        var nativePage = new ExtractedPdfPage(
            1,
            nativeText,
            17,
            nativeText.Length,
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(nativeText)),
            PdfPageExtractionQuality.FromText(
                nativeText,
                17,
                nativeText.Length));
        var nativeExtraction = new PdfExtractionResult(
            [],
            [nativePage],
            PdfExtractionQualitySummary.FromPages([nativePage]));

        var summary =
            CanonicalNativeTextCoverageReconciler.Apply(
                canonical,
                nativeExtraction);

        Assert.True(
            summary.RecoveredLineCount == 0,
            string.Join(
                Environment.NewLine,
                canonical.Pages[0].Blocks
                    .Where(static block =>
                        block.BlockType
                        == CanonicalNativeTextCoverageReconciler
                            .RecoveryBlockType)
                    .Select(block =>
                        $"{block.Text.Canonical} | "
                        + string.Join(
                            ",",
                            block.Provenance.Attributes))));
        Assert.Equal(0, summary.RecoveredBlockCount);
        Assert.DoesNotContain(
            canonical.Pages[0].Blocks,
            static block =>
                block.BlockType
                == CanonicalNativeTextCoverageReconciler
                    .RecoveryBlockType);
    }

    [Fact]
    public void CanonicalProfileInputProjection_KeepsOnlyPublishableContentChunks()
    {
        var contentChecksum = new byte[] { 1, 2, 3 };
        var tableChecksum = new byte[] { 4, 5, 6 };
        var chunks = new[]
        {
            new ProjectedRetrievalChunk(
                ChunkIndex: 7,
                SectionOrdinal: 3,
                UnitOrdinal: null,
                PageStart: 2,
                PageEnd: 2,
                Text: "Canonical operating procedure with grounded details.",
                TokenCount: 7,
                Checksum: contentChecksum,
                ChunkType: DoclingCanonicalRetrievalProjector.ContentChunkType,
                OffsetStart: 10,
                OffsetEnd: 64,
                ContentRole: RetrievalContentClassifier.ContentRole),
            new ProjectedRetrievalChunk(
                ChunkIndex: 8,
                SectionOrdinal: 3,
                UnitOrdinal: null,
                PageStart: 2,
                PageEnd: 2,
                Text: "Setting | Value\nPressure | 6 bar",
                TokenCount: 6,
                Checksum: tableChecksum,
                ChunkType: DoclingCanonicalRetrievalProjector.TableChunkType,
                ContentRole: RetrievalContentClassifier.ContentRole),
            new ProjectedRetrievalChunk(
                ChunkIndex: 9,
                SectionOrdinal: null,
                UnitOrdinal: null,
                PageStart: 2,
                PageEnd: 2,
                Text: "TRANSLATION SMS 1145",
                TokenCount: 3,
                Checksum: [9],
                ChunkType: "navigation",
                ContentRole: RetrievalContentClassifier.NavigationRole,
                NavigationReason: "repeated_page_furniture")
        };

        var units = CanonicalProfileInputProjector.Project(chunks);

        Assert.Equal(2, units.Count);
        Assert.Equal([7, 8], units.Select(static unit => unit.Ordinal));
        var content = Assert.Single(
            units,
            static unit => unit.Ordinal == 7);
        Assert.Equal(3, content.SectionOrdinal);
        Assert.Equal(2, content.PageStart);
        Assert.Equal(10, content.OffsetStart);
        Assert.Equal(64, content.OffsetEnd);
        Assert.Equal(contentChecksum, content.Checksum);
        Assert.DoesNotContain(
            units,
            static unit => unit.Text.Contains(
                "TRANSLATION",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalRetrievalProjection_CarriesTheCompleteHeadingPath()
    {
        var response = BuildResponse();
        var source = response.Document.JsonContent!;
        source.Texts[0].OriginalText = "Recipe title";
        source.Texts[0].Text = "Recipe title";
        var subsection = Text(
            "#/texts/4",
            "section_header",
            "Preparation",
            "Preparation",
            "body",
            Box(10, 165, 90, 155, "BOTTOMLEFT"),
            level: 5);
        source.Texts.Add(subsection);
        source.Body.Children.Insert(
            1,
            new() { Ref = subsection.SelfRef });

        var canonical = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            source);

        var chunks = DoclingCanonicalRetrievalProjector.Project(
            canonical,
            source,
            maxWords: 220,
            minWords: 1);

        var content = Assert.Single(
            chunks,
            chunk => chunk.ChunkType
                     == DoclingCanonicalRetrievalProjector.ContentChunkType);
        Assert.StartsWith(
            $"Recipe title{Environment.NewLine}"
            + $"Preparation{Environment.NewLine}",
            content.Text,
            StringComparison.Ordinal);
        Assert.Equal(2, content.CanonicalContextBlockIds!.Count);
        Assert.Equal(1, content.SectionOrdinal);
        Assert.Equal("Preparation", content.SectionTitle);
        Assert.Equal(
            "Recipe title > Preparation",
            content.HeadingPath);

        var sections =
            CanonicalDocumentSectionProjector.Project(canonical);
        Assert.Collection(
            sections,
            section =>
            {
                Assert.Equal(0, section.Ordinal);
                Assert.Equal("Recipe title", section.Title);
                Assert.Equal(1, section.Level);
            },
            section =>
            {
                Assert.Equal(1, section.Ordinal);
                Assert.Equal("Preparation", section.Title);
                Assert.Equal(5, section.Level);
            });
    }

    [Fact]
    public void Project_InfersRepeatedMarginFurnitureAndOmitsItFromRetrieval()
    {
        var source = new DoclingDocument
        {
            SchemaName = "DoclingDocument",
            Version = "1.10.0"
        };
        for (var pageNumber = 1; pageNumber <= 6; pageNumber++)
        {
            source.Pages[pageNumber.ToString()] = new()
            {
                PageNumber = pageNumber,
                Size = new() { Width = 100, Height = 200 }
            };
            var body = Text(
                $"#/texts/{source.Texts.Count}",
                "text",
                "Repeated substantive warning",
                "Repeated substantive warning",
                "body",
                Box(10, 120, 90, 80, "BOTTOMLEFT"),
                pageNumber: pageNumber);
            source.Texts.Add(body);
            source.Body.Children.Add(new() { Ref = body.SelfRef });

            var parserClassified = pageNumber <= 3;
            var footer = Text(
                $"#/texts/{source.Texts.Count}",
                parserClassified ? "page_footer" : "text",
                parserClassified ? $"{pageNumber} Example Corp" : "Example Corp",
                parserClassified ? $"{pageNumber} Example Corp" : "Example Corp",
                parserClassified ? "furniture" : "body",
                Box(10, 20, 90, 10, "BOTTOMLEFT"),
                pageNumber: pageNumber);
            source.Texts.Add(footer);
            (parserClassified ? source.Furniture : source.Body)
                .Children.Add(new() { Ref = footer.SelfRef });

            if (pageNumber == 6)
            {
                var contaminatedBody = Text(
                    $"#/texts/{source.Texts.Count}",
                    "list_item",
                    "Keep this substantive instruction. Example Corp",
                    "Keep this substantive instruction. Example Corp",
                    "body",
                    Box(10, 35, 90, 20, "BOTTOMLEFT"),
                    pageNumber: pageNumber);
                source.Texts.Add(contaminatedBody);
                source.Body.Children.Add(
                    new() { Ref = contaminatedBody.SelfRef });
            }
        }

        var canonical = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            source);

        var inferred = canonical.Pages
            .SelectMany(static page => page.Blocks)
            .Where(block =>
                string.Equals(
                    block.Text.Canonical,
                    "Example Corp",
                    StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, inferred.Length);
        Assert.All(inferred, block =>
        {
            Assert.True(block.IsRepeatedFurniture);
            Assert.Contains(
                CanonicalRepeatedPageFurnitureClassifier.InferredQualityFlag,
                block.QualityFlags);
            Assert.Equal(
                CanonicalRepeatedPageFurnitureClassifier.ClassifierVersion,
                block.Provenance.Attributes["repeatedFurnitureClassifier"]);
            Assert.Equal(
                "6",
                block.Provenance.Attributes["repeatedFurnitureDistinctPages"]);
        });
        Assert.All(
            canonical.Pages.SelectMany(static page => page.Blocks)
                .Where(block =>
                    string.Equals(
                        block.Text.Canonical,
                        "Repeated substantive warning",
                    StringComparison.Ordinal)),
            static block => Assert.False(block.IsRepeatedFurniture));
        var trimmed = Assert.Single(
            canonical.Pages
                .SelectMany(static page => page.Blocks),
            block => block.Text.Canonical.Contains(
                "Keep this substantive instruction",
                StringComparison.Ordinal));
        Assert.False(trimmed.IsRepeatedFurniture);
        Assert.Equal(
            "Keep this substantive instruction.",
            trimmed.Text.Retrieval);
        Assert.Equal(
            "Keep this substantive instruction. Example Corp",
            trimmed.Text.Canonical);
        Assert.Contains(
            CanonicalRepeatedPageFurnitureClassifier.TrimmedQualityFlag,
            trimmed.QualityFlags);

        var chunks = DoclingCanonicalRetrievalProjector.Project(
            canonical,
            source,
            maxWords: 220,
            minWords: 1);

        Assert.DoesNotContain(
            chunks,
            chunk => chunk.Text.Contains(
                "Example Corp",
                StringComparison.Ordinal));
        Assert.Contains(
            chunks,
            chunk => chunk.Text.Contains(
                "Repeated substantive warning",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalRetrievalProjection_DoesNotMergeDifferentHeadingPaths()
    {
        var response = BuildResponse();
        var source = response.Document.JsonContent!;
        source.Texts[0].OriginalText = "First procedure";
        source.Texts[0].Text = "First procedure";
        var secondHeading = Text(
            "#/texts/4",
            "section_header",
            "Second procedure",
            "Second procedure",
            "body",
            Box(10, 125, 90, 115, "BOTTOMLEFT"),
            level: 1);
        var secondBody = Text(
            "#/texts/5",
            "text",
            "Second body",
            "Second body",
            "body",
            Box(10, 110, 90, 105, "BOTTOMLEFT"));
        source.Texts.Add(secondHeading);
        source.Texts.Add(secondBody);
        source.Body.Children.Insert(
            2,
            new() { Ref = secondHeading.SelfRef });
        source.Body.Children.Insert(
            3,
            new() { Ref = secondBody.SelfRef });

        var canonical = DoclingCanonicalDocumentAdapter.Project(
            BuildContext(),
            source);
        var chunks = DoclingCanonicalRetrievalProjector.Project(
            canonical,
            source,
            maxWords: 220,
            minWords: 50);
        var content = chunks
            .Where(chunk =>
                chunk.ChunkType
                == DoclingCanonicalRetrievalProjector.ContentChunkType)
            .ToArray();

        Assert.Equal(2, content.Length);
        Assert.Equal("First procedure", content[0].HeadingPath);
        Assert.Equal("Second procedure", content[1].HeadingPath);
        Assert.DoesNotContain(
            "Second body",
            content[0].Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Canonical body",
            content[1].Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingOrder_VisitsChildrenOfEveryContentItem_AndKeepsFallbackSourceOrder()
    {
        var source = BuildDocument();
        var heading = source.Texts.Single(
            static item => item.SelfRef == "#/texts/0");
        var body = source.Texts.Single(
            static item => item.SelfRef == "#/texts/1");
        heading.Children.Add(new() { Ref = body.SelfRef });
        body.Parent = new() { Ref = heading.SelfRef };
        source.Body.Children.RemoveAll(reference =>
            string.Equals(
                reference.Ref,
                body.SelfRef,
                StringComparison.Ordinal));

        var fallbackFirst = Text(
            "#/texts/10",
            "text",
            "fallback first",
            "fallback first",
            "body",
            Box(10, 40, 90, 35, "BOTTOMLEFT"));
        var fallbackSecond = Text(
            "#/texts/9",
            "text",
            "fallback second",
            "fallback second",
            "body",
            Box(10, 35, 90, 30, "BOTTOMLEFT"));
        source.Texts.Add(fallbackFirst);
        source.Texts.Add(fallbackSecond);

        var order = DoclingDocumentTraversal.BuildReadingOrder(source);

        Assert.Equal(order[heading.SelfRef] + 1, order[body.SelfRef]);
        Assert.Equal(order[body.SelfRef] + 1, order["#/texts/2"]);
        Assert.True(
            order[fallbackFirst.SelfRef] < order[fallbackSecond.SelfRef]);
    }

    [Fact]
    public void ReadingOrder_RejectsCyclesThroughNonGroupContentItems()
    {
        var source = BuildDocument();
        var heading = source.Texts.Single(
            static item => item.SelfRef == "#/texts/0");
        var body = source.Texts.Single(
            static item => item.SelfRef == "#/texts/1");
        heading.Children.Add(new() { Ref = body.SelfRef });
        body.Children.Add(new() { Ref = heading.SelfRef });
        source.Body.Children.RemoveAll(reference =>
            string.Equals(
                reference.Ref,
                body.SelfRef,
                StringComparison.Ordinal));

        var error = Assert.Throws<InvalidDataException>(
            () => DoclingDocumentTraversal.BuildReadingOrder(source));

        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DoclingCanonicalProjectionContext BuildContext()
        => new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            new string('a', 64),
            1234,
            "fixture.pdf",
            new string('b', 64),
            "stage_fixture",
            "docling-serve-test",
            "sha256:test",
            0.92);

    private static DoclingConvertResponse BuildResponse()
        => new()
        {
            Status = "success",
            ProcessingTimeSeconds = 2,
            Document = new()
            {
                FileName = "fixture.pdf",
                JsonContent = BuildDocument()
            }
        };

    private static RagOptions BuildRagOptions()
        => new()
        {
            QdrantCollection = "knowledge_base",
            EmbeddingsModel = "intfloat/multilingual-e5-base",
            EmbeddingsModelRevision = "d128750597153bb5987e10b1c3493a34e5a4502a",
            EmbeddingsRuntimeRevision = "sha256:tei",
            QdrantRuntimeRevision = "sha256:qdrant"
        };

    private static DoclingDocument BuildDocument()
    {
        var heading = Text(
            "#/texts/0",
            "section_header",
            "heading",
            "heading",
            "body",
            Box(10, 190, 90, 170, "BOTTOMLEFT"),
            level: 1);
        var body = Text(
            "#/texts/1",
            "text",
            "Raw body",
            "Canonical body",
            "body",
            Box(10, 150, 90, 130, "BOTTOMLEFT"));
        var caption = Text(
            "#/texts/2",
            "caption",
            "Table caption",
            "Table caption",
            "body",
            Box(10, 125, 90, 115, "BOTTOMLEFT"));
        var footer = Text(
            "#/texts/3",
            "page_footer",
            "1",
            "1",
            "furniture",
            Box(45, 20, 55, 10, "BOTTOMLEFT"));
        var table = new DoclingTableItem
        {
            SelfRef = "#/tables/0",
            Parent = new() { Ref = "#/body" },
            Label = "table",
            ContentLayer = "body",
            Provenance =
            [
                Provenance(Box(10, 100, 90, 50, "BOTTOMLEFT"))
            ],
            Captions = [new() { Ref = caption.SelfRef }],
            Data = new()
            {
                RowCount = 2,
                ColumnCount = 2,
                Cells =
                [
                    Cell(0, 0, "A", Box(10, 100, 50, 120, "TOPLEFT"), columnHeader: true),
                    Cell(0, 1, "B", Box(50, 100, 90, 120, "TOPLEFT"), columnHeader: true),
                    Cell(1, 0, "C", Box(10, 120, 50, 140, "TOPLEFT")),
                    Cell(1, 1, "D", Box(50, 120, 90, 140, "TOPLEFT"))
                ]
            }
        };
        var picture = new DoclingPictureItem
        {
            SelfRef = "#/pictures/0",
            Parent = new() { Ref = "#/body" },
            Label = "picture",
            ContentLayer = "body",
            Provenance =
            [
                Provenance(Box(20, 45, 80, 25, "BOTTOMLEFT"))
            ],
            Captions = [new() { Ref = caption.SelfRef }]
        };

        return new()
        {
            SchemaName = "DoclingDocument",
            Version = "1.10.0",
            Pages =
            {
                ["1"] = new()
                {
                    PageNumber = 1,
                    Size = new() { Width = 100, Height = 200 }
                }
            },
            Texts = [heading, body, caption, footer],
            Tables = [table],
            Pictures = [picture],
            Body = new()
            {
                Children =
                [
                    new() { Ref = heading.SelfRef },
                    new() { Ref = body.SelfRef },
                    new() { Ref = caption.SelfRef },
                    new() { Ref = table.SelfRef },
                    new() { Ref = picture.SelfRef }
                ]
            },
            Furniture = new()
            {
                Children = [new() { Ref = footer.SelfRef }]
            }
        };
    }

    private static DoclingTextItem Text(
        string sourceRef,
        string label,
        string original,
        string canonical,
        string contentLayer,
        DoclingBoundingBox box,
        int? level = null,
        int pageNumber = 1)
        => new()
        {
            SelfRef = sourceRef,
            Parent = new()
            {
                Ref = string.Equals(
                    contentLayer,
                    "furniture",
                    StringComparison.Ordinal)
                    ? "#/furniture"
                    : "#/body"
            },
            Label = label,
            OriginalText = original,
            Text = canonical,
            ContentLayer = contentLayer,
            Level = level,
            Provenance = [Provenance(box, pageNumber)]
        };

    private static DoclingTableCell Cell(
        int row,
        int column,
        string text,
        DoclingBoundingBox box,
        bool columnHeader = false)
        => new()
        {
            RowIndex = row,
            ColumnIndex = column,
            RowSpan = 1,
            ColumnSpan = 1,
            Text = text,
            BoundingBox = box,
            IsColumnHeader = columnHeader
        };

    private static DoclingCellTextRepair TextRepair(
        string action,
        double confidence)
        => new()
        {
            SchemaVersion = "saaia_table_text_repair_v1",
            Action = action,
            Engine = "RapidOCR",
            EngineVersion = "3.9.1",
            ModelId = "PP-OCRv6_det_small+PP-OCRv6_rec_small",
            Confidence = confidence
        };

    private static DoclingProvenance Provenance(
        DoclingBoundingBox box,
        int pageNumber = 1)
        => new()
        {
            PageNumber = pageNumber,
            BoundingBox = box
        };

    private static DoclingBoundingBox Box(
        double left,
        double top,
        double right,
        double bottom,
        string origin)
        => new()
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
            CoordinateOrigin = origin
        };
}
