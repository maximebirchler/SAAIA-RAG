using System.Globalization;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record DoclingIngestionStageTimings(
    long CanonicalProjectionMs,
    long ChunkingMs,
    long EmbeddingMs,
    long VectorIndexMs);

internal static class DoclingIngestionStageManifestFactory
{
    public static IReadOnlyList<IngestionStageManifest> Create(
        DocumentIntelligenceOptions documentIntelligence,
        IngestionOptions ingestion,
        RagOptions rag,
        DoclingConvertResponse response,
        DoclingIngestionStageTimings timings,
        string codeRevision,
        NativeTextCoverageReconciliationSummary?
            nativeTextReconciliation = null,
        NativePdfImageInventorySummary?
            nativePdfImageInventory = null,
        NativeTypographyProjectionSummary? nativeTypography = null,
        PdfExtractionResult? nativeVectorExtraction = null)
    {
        ArgumentNullException.ThrowIfNull(documentIntelligence);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(rag);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timings);
        codeRevision = IngestionProvenanceConfiguration.Validate(
            rag,
            codeRevision);
        var document = response.Document.JsonContent
            ?? throw new InvalidDataException("Docling JSON content is required.");
        var deviceId = ResolveDeviceId(documentIntelligence.Device);
        var workerConcurrency = Math.Max(1, documentIntelligence.ServiceWorkerConcurrency);
        var modelRevision = documentIntelligence.DeploymentRevision;
        var sharedAttributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["deploymentRevision"] = documentIntelligence.DeploymentRevision,
            ["doclingSchema"] = document.SchemaName,
            ["doclingSchemaVersion"] = document.Version,
            ["executionMetadataSource"] = response.Execution is null ? "unattested" : "sidecar_response",
            ["deploymentRevisionSource"] = "declared_configuration",
            ["observedPackageVersions"] = string.Join(";", new[] { "docling-serve", "docling-slim", "docling-parse", "docling-ibm-models", "rapidocr", "onnxruntime", "torch" }
                .Select(package => (Package: package, Version: DoclingExecutionMetadataPolicy.Package(response, package)))
                .Where(item => item.Version is not null).Select(item => item.Package + "=" + item.Version))
        };

        var stages = new List<IngestionStageManifest>
        {
            new()
            {
                StageId = "docling_parse",
                StageType = "parse",
                Engine = "docling-parse",
                EngineVersion = DoclingExecutionMetadataPolicy.Package(response, "docling-parse") ?? documentIntelligence.ParseEngineVersion,
                OptionsSha256 = HashOptions(new
                {
                    documentIntelligence.PdfBackend,
                    pipeline = "standard"
                }),
                DeviceId = deviceId,
                Concurrency = workerConcurrency,
                DurationMs = ResolveTimingMs(response, "page_parse"),
                Attributes = Copy(sharedAttributes)
            },
            new()
            {
                StageId = "docling_ocr",
                StageType = "ocr",
                Engine = "RapidOCR",
                EngineVersion = DoclingExecutionMetadataPolicy.Package(response, "rapidocr") ?? documentIntelligence.OcrEngineVersion,
                ModelId = "PP-OCRv6_det+PP-OCRv4_cls+PP-OCRv6_rec",
                ModelRevision = modelRevision,
                OptionsSha256 = HashOptions(new
                {
                    documentIntelligence.DoOcr,
                    forceOcr = response.RequestedForceOcr ?? response.Execution?.ForceOcr ?? documentIntelligence.ForceOcr,
                    documentIntelligence.OcrPreset
                }),
                DeviceId = deviceId,
                Concurrency = workerConcurrency,
                DurationMs = ResolveTimingMs(response, "ocr"),
                Attributes = WithConfidence(
                    sharedAttributes,
                    "ocrScore",
                    response.Confidence?.OcrScore)
            },
            new()
            {
                StageId = "docling_layout",
                StageType = "layout",
                Engine = "docling",
                EngineVersion = DoclingExecutionMetadataPolicy.DescribeEngine(documentIntelligence, response),
                ModelId = "docling-layout",
                ModelRevision = modelRevision,
                OptionsSha256 = HashOptions(new
                {
                    modelPackage = documentIntelligence.ModelPackageVersion,
                    documentIntelligence.IncludeImages,
                    documentIntelligence.IncludePageImages
                }),
                DeviceId = deviceId,
                Concurrency = workerConcurrency,
                DurationMs = ResolveTimingMs(response, "layout"),
                Attributes = WithConfidence(
                    sharedAttributes,
                    "layoutScore",
                    response.Confidence?.LayoutScore)
            },
            new()
            {
                StageId = "docling_table_structure",
                StageType = "table_structure",
                Engine = "TableFormer",
                EngineVersion = DoclingExecutionMetadataPolicy.Package(response, "docling-ibm-models") ?? documentIntelligence.ModelPackageVersion,
                ModelId = "docling-tableformer",
                ModelRevision = modelRevision,
                OptionsSha256 = HashOptions(new
                {
                    documentIntelligence.DoTableStructure,
                    documentIntelligence.TableMode,
                    documentIntelligence.TableCellMatching
                }),
                DeviceId = deviceId,
                Concurrency = workerConcurrency,
                DurationMs = ResolveTimingMs(response, "table_structure"),
                Attributes = WithConfidence(
                    sharedAttributes,
                    "tableScore",
                    response.Confidence?.TableScore)
            },
            new()
            {
                StageId = "canonical_projection",
                StageType = "canonical_projection",
                Engine = "SAAIA.Backend",
                EngineVersion = codeRevision,
                OptionsSha256 = HashOptions(new
                {
                    documentSchema = CanonicalSchema.DocumentVersion,
                    projection = "docling_spatial_structure_v1"
                }),
                DeviceId = "cpu:0",
                Concurrency = 1,
                DurationMs = timings.CanonicalProjectionMs,
                Attributes = new(StringComparer.Ordinal)
                {
                    ["source"] = "docling",
                    ["anchorPrecision"] = "page_block_and_table_cell"
                }
            },
            new()
            {
                StageId = "retrieval_projection",
                StageType = "chunk",
                Engine = "SAAIA.Backend",
                EngineVersion = codeRevision,
                OptionsSha256 = HashOptions(new
                {
                    ingestion.ChunkMaxWords,
                    ingestion.ChunkMinWords,
                    projection = "docling_canonical_structure_v1",
                    sourceLinks = "block_span_table_cell"
                }),
                DeviceId = "cpu:0",
                Concurrency = 1,
                DurationMs = timings.ChunkingMs
            },
            new()
            {
                StageId = "embeddings",
                StageType = "embedding",
                Engine = "text-embeddings-inference",
                EngineVersion = rag.EmbeddingsRuntimeRevision,
                ModelId = rag.EmbeddingsModel,
                ModelRevision = rag.EmbeddingsModelRevision,
                OptionsSha256 = HashOptions(new
                {
                    inputFormat = IngestionWorker.ResolveEmbeddingInputFormat(rag.EmbeddingsModel),
                    ingestion.EmbeddingsBatchSize,
                    ingestion.EmbeddingsBatchAdaptiveRetryEnabled
                }),
                DeviceId = "cpu:0",
                BatchSize = IngestionOptions.ResolveEmbeddingsBatchSize(
                    ingestion.EmbeddingsBatchSize),
                Concurrency = Math.Max(1, ingestion.TeiMaxConcurrency),
                DurationMs = timings.EmbeddingMs
            },
            new()
            {
                StageId = "vector_index",
                StageType = "index",
                Engine = "Qdrant",
                EngineVersion = rag.QdrantRuntimeRevision,
                OptionsSha256 = HashOptions(new
                {
                    rag.QdrantCollection,
                    schema = "qdrant_point_v1"
                }),
                DeviceId = "cpu:0",
                Concurrency = Math.Max(1, ingestion.QdrantMaxConcurrency),
                DurationMs = timings.VectorIndexMs
            }
        };

        if (nativeTextReconciliation is { Enabled: true })
        {
            var canonicalStageIndex = stages.FindIndex(static stage =>
                string.Equals(
                    stage.StageId,
                    "canonical_projection",
                    StringComparison.Ordinal));
            stages.Insert(canonicalStageIndex + 1, new()
            {
                StageId =
                    CanonicalNativeTextCoverageReconciler.StageId,
                StageType = "text_coverage_reconciliation",
                Engine = "PdfPig",
                EngineVersion =
                    nativeTextReconciliation.EngineVersion,
                OptionsSha256 = HashOptions(new
                {
                    documentIntelligence
                        .NativeTextCoverageReconciliationEnabled,
                    minimumLineCoverage = Math.Clamp(
                        documentIntelligence
                            .NativeTextCoverageMinimumLineCoverage,
                        0.50,
                        1.0),
                    algorithms = new[]
                    {
                        "line_lcs_v1",
                        PdfNativeLayoutExtractor.Algorithm,
                        "layout_order_agreement_v1"
                    },
                    output =
                        "page_anchored_supplemental_blocks_and_layout_alternatives"
                }),
                DeviceId = "cpu:0",
                Concurrency = 1,
                DurationMs = nativeTextReconciliation.DurationMs,
                Attributes = new(StringComparer.Ordinal)
                {
                    ["availability"] = nativeTextReconciliation.Availability,
                    ["retentionPolicy"] = "literal_content_not_similarity",
                    ["minimumLineCoverageRole"] = "context_expansion_diagnostic_only",
                    ["reconciledTableCellCount"] = nativeTextReconciliation.ReconciledTableCellCount.ToString(CultureInfo.InvariantCulture),
                    ["tableCellSelectionPolicy"] = "unique_spatial_literal_typography_only",
                    ["nativePageCount"] =
                        nativeTextReconciliation.NativePageCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["auditedPageCount"] =
                        nativeTextReconciliation.AuditedPageCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["candidateLineCount"] =
                        nativeTextReconciliation.CandidateLineCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["recoveredLineCount"] =
                        nativeTextReconciliation.RecoveredLineCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["recoveredBlockCount"] =
                        nativeTextReconciliation.RecoveredBlockCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["recoveredCharacterCount"] =
                        nativeTextReconciliation
                            .RecoveredCharacterCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["candidateLayoutBlockCount"] =
                        nativeTextReconciliation
                            .CandidateLayoutBlockCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["recoveredLayoutBlockCount"] =
                        nativeTextReconciliation
                            .RecoveredLayoutBlockCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["recoveredLayoutPageCount"] =
                        nativeTextReconciliation
                            .RecoveredLayoutPageCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["recoveredLayoutCharacterCount"] =
                        nativeTextReconciliation
                            .RecoveredLayoutCharacterCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["skippedUndersegmentedLayoutPageCount"] =
                        nativeTextReconciliation
                            .SkippedUndersegmentedLayoutPageCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["skippedLowQualityPageCount"] =
                        nativeTextReconciliation
                            .SkippedLowQualityPageCount
                            .ToString(CultureInfo.InvariantCulture),
                    ["semanticDecisionOwner"] = "llm_client"
                }
            });
        }

        if (nativePdfImageInventory is { Enabled: true })
        {
            var nativeTextStageIndex = stages.FindIndex(static stage =>
                string.Equals(
                    stage.StageId,
                    CanonicalNativeTextCoverageReconciler.StageId,
                    StringComparison.Ordinal));
            var canonicalStageIndex = stages.FindIndex(static stage =>
                string.Equals(
                    stage.StageId,
                    "canonical_projection",
                    StringComparison.Ordinal));
            var insertIndex = nativeTextStageIndex >= 0
                ? nativeTextStageIndex + 1
                : canonicalStageIndex + 1;
            stages.Insert(insertIndex, new()
            {
                StageId = CanonicalNativePdfImageInventoryReconciler.StageId,
                StageType = "image_inventory",
                Engine = "PdfPig",
                EngineVersion = nativePdfImageInventory.EngineVersion,
                OptionsSha256 = HashOptions(new
                {
                    documentIntelligence.NativePdfImageInventoryEnabled,
                    algorithm = "native_pdf_image_placement_inventory_v1",
                    output = "page_anchored_figure_inventory"
                }),
                DeviceId = "cpu:0",
                Concurrency = 1,
                DurationMs = nativePdfImageInventory.DurationMs,
                Attributes = new(StringComparer.Ordinal)
                {
                    ["nativePageCount"] =
                        nativePdfImageInventory.NativePageCount.ToString(
                            CultureInfo.InvariantCulture),
                    ["candidateImageCount"] =
                        nativePdfImageInventory.CandidateImageCount.ToString(
                            CultureInfo.InvariantCulture),
                    ["addedFigureCount"] =
                        nativePdfImageInventory.AddedFigureCount.ToString(
                            CultureInfo.InvariantCulture),
                    ["enrichedPageCount"] =
                        nativePdfImageInventory.EnrichedPageCount.ToString(
                            CultureInfo.InvariantCulture),
                    ["semanticDecisionOwner"] = "llm_client"
                }
            });
        }

        if (documentIntelligence.HeadingHierarchyEnabled)
        {
            stages.Insert(3, new()
            {
                StageId = "docling_heading_hierarchy",
                StageType = "heading_hierarchy",
                Engine = "docling",
                EngineVersion = DoclingExecutionMetadataPolicy.DescribeEngine(documentIntelligence, response),
                ModelId = "docling-heading-hierarchy",
                ModelRevision = modelRevision,
                OptionsSha256 = HashOptions(new
                {
                    documentIntelligence.HeadingHierarchyEnabled,
                    documentIntelligence.HeadingHierarchyUseBookmarks,
                    documentIntelligence.HeadingHierarchyUseNumbering,
                    documentIntelligence.HeadingHierarchyUseStyle,
                    maxLevel = Math.Clamp(
                        documentIntelligence.HeadingHierarchyMaxLevel,
                        1,
                        100),
                    bookmarkMatchThreshold = Math.Clamp(
                        documentIntelligence
                            .HeadingHierarchyBookmarkMatchThreshold,
                        0d,
                        1d)
                }),
                DeviceId = "cpu:0",
                Concurrency = workerConcurrency,
                DurationMs = ResolveTimingMs(
                    response,
                    "heading_hierarchy"),
                Attributes = new(StringComparer.Ordinal)
                {
                    ["deploymentRevision"] =
                        documentIntelligence.DeploymentRevision,
                    ["signals"] = "bookmarks_numbering_style",
                    ["semanticDecisionOwner"] = "llm_client"
                }
            });
        }

        if (DoclingRegionalTableRepairStage.Create(documentIntelligence, response) is { } regionalTableStage)
            stages.Insert(stages.FindIndex(stage => stage.StageId == "canonical_projection"), regionalTableStage);
        if (DoclingTableRegionObservationStage.Create(documentIntelligence, response) is { } tableObservationStage)
            stages.Insert(stages.FindIndex(stage => stage.StageId == "canonical_projection"), tableObservationStage);

        if (DoclingRegionalTextRepairStage.Create(documentIntelligence, response) is { } regionalTextStage)
            stages.Insert(stages.FindIndex(stage => stage.StageId == "canonical_projection"), regionalTextStage);

        if (documentIntelligence.NativeTypographyObservationsEnabled)
            stages.Insert(stages.FindIndex(stage => stage.StageId == "canonical_projection"), CanonicalNativeTypographyProjector.CreateStage(nativeTypography));
        if (documentIntelligence.RegionalTableAlternativeReadingsEnabled
            && PdfNativeVectorDiagnosticsStage.Create(nativeVectorExtraction) is { } nativeVectorStage)
            stages.Insert(stages.FindIndex(stage => stage.StageId == "canonical_projection"), nativeVectorStage);
        if (documentIntelligence.DoclingInputGeometryCaptureEnabled && document.InputGeometryCapture is { } geometry)
            stages.Add(geometry.CreateStage());
        return stages;
    }

    private static long? ResolveTimingMs(
        DoclingConvertResponse response,
        string timingName)
    {
        if (!response.Timings.TryGetValue(timingName, out var timing)
            || timing.Times.Count == 0)
        {
            return null;
        }

        var totalSeconds = timing.Times
            .Where(static value => double.IsFinite(value) && value >= 0)
            .Sum();
        return (long)Math.Round(totalSeconds * 1_000, MidpointRounding.AwayFromZero);
    }

    private static Dictionary<string, string> WithConfidence(
        IReadOnlyDictionary<string, string> source,
        string key,
        double? value)
    {
        var attributes = Copy(source);
        if (value.HasValue && double.IsFinite(value.Value))
        {
            attributes[key] = Math.Clamp(value.Value, 0d, 1d)
                .ToString("0.####", CultureInfo.InvariantCulture);
        }

        return attributes;
    }

    private static Dictionary<string, string> Copy(
        IReadOnlyDictionary<string, string> source)
        => new(source, StringComparer.Ordinal);

    private static string ResolveDeviceId(string? value)
    {
        var device = string.IsNullOrWhiteSpace(value)
            ? "cpu"
            : value.Trim().ToLowerInvariant();
        return device.Contains(':', StringComparison.Ordinal)
            ? device
            : $"{device}:0";
    }

    private static string HashOptions<T>(T value)
        => CanonicalContractJson.ComputeSha256(value);
}
