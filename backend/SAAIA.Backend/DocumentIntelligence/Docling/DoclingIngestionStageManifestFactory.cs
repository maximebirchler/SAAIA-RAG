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
            nativeTextReconciliation = null)
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
            ["doclingSchemaVersion"] = document.Version
        };

        var stages = new List<IngestionStageManifest>
        {
            new()
            {
                StageId = "docling_parse",
                StageType = "parse",
                Engine = "docling-parse",
                EngineVersion = documentIntelligence.ParseEngineVersion,
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
                EngineVersion = documentIntelligence.OcrEngineVersion,
                ModelId = "PP-OCRv6_det+PP-OCRv4_cls+PP-OCRv6_rec",
                ModelRevision = modelRevision,
                OptionsSha256 = HashOptions(new
                {
                    documentIntelligence.DoOcr,
                    documentIntelligence.ForceOcr,
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
                EngineVersion = documentIntelligence.EngineVersion,
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
                EngineVersion = documentIntelligence.ModelPackageVersion,
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

        if (documentIntelligence.HeadingHierarchyEnabled)
        {
            stages.Insert(3, new()
            {
                StageId = "docling_heading_hierarchy",
                StageType = "heading_hierarchy",
                Engine = "docling",
                EngineVersion = documentIntelligence.EngineVersion,
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

        var appliedTableRepairs = response.TableRepairs.Count(static repair =>
            string.Equals(repair.Status, "applied", StringComparison.Ordinal));
        if (appliedTableRepairs > 0)
        {
            var canonicalStageIndex = stages.FindIndex(static stage =>
                string.Equals(
                    stage.StageId,
                    "canonical_projection",
                    StringComparison.Ordinal));
            stages.Insert(canonicalStageIndex, new()
            {
                StageId = "regional_table_ocr_repair",
                StageType = "table_text_repair",
                Engine = "RapidOCR",
                EngineVersion = documentIntelligence.OcrEngineVersion,
                ModelId = "PP-OCRv6_det_small+PP-OCRv6_rec_small",
                ModelRevision = modelRevision,
                OptionsSha256 = HashOptions(new
                {
                    documentIntelligence.RegionalTableRepairEnabled,
                    documentIntelligence.RegionalTableRepairScale,
                    documentIntelligence.RegionalTableRepairPaddingPoints,
                    documentIntelligence.RegionalTableRepairMinimumScore,
                    trigger = "sparse_non_overlapping_table_grid"
                }),
                DeviceId = "cpu:0",
                Concurrency = 1,
                DurationMs = ResolveTimingMs(response, "saaia_table_repair"),
                Attributes = new(StringComparer.Ordinal)
                {
                    ["deploymentRevision"] = documentIntelligence.DeploymentRevision,
                    ["repairSchema"] = "saaia_table_text_repair_v1",
                    ["appliedTableCount"] =
                        appliedTableRepairs.ToString(CultureInfo.InvariantCulture),
                    ["semanticDecisionOwner"] = "llm_client"
                }
            });
        }

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
