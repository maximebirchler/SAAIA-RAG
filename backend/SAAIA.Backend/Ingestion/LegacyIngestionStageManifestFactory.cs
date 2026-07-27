using SAAIA.Contracts.DocumentIntelligence;

internal sealed record LegacyIngestionStageTimings(
    long ExtractionMs,
    long OcrMs,
    long SectionMs,
    long UnitMs,
    long ChunkingMs,
    long EmbeddingMs,
    long VectorIndexMs);

internal static class LegacyIngestionStageManifestFactory
{
    public static IReadOnlyList<IngestionStageManifest> Create(
        IngestionOptions ingestion,
        RagOptions rag,
        PdfExtractionResult extraction,
        bool ocrAttempted,
        bool ocrApplied,
        string? ocrLanguages,
        PdfOcrDiagnostics? ocrDiagnostics,
        LegacyIngestionStageTimings timings,
        string codeRevision)
    {
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(rag);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(timings);
        codeRevision = IngestionProvenanceConfiguration.Validate(
            rag,
            codeRevision);

        var stages = new List<IngestionStageManifest>
        {
            new()
            {
                StageId = "native_parse",
                StageType = "parse",
                Engine = "PdfPig",
                EngineVersion = typeof(UglyToad.PdfPig.PdfDocument).Assembly.GetName().Version?.ToString() ?? "unavailable",
                OptionsSha256 = HashOptions(new
                {
                    layout = "pdfpig_layout_aware_v1",
                    sanitization = "pdf_text_sanitizer_v1",
                    repeatedFurniture = "repeated_page_boilerplate_v1"
                }),
                DeviceId = "cpu:0",
                Concurrency = 1,
                DurationMs = timings.ExtractionMs
            }
        };

        if (ocrAttempted)
        {
            var ocrCommand = string.IsNullOrWhiteSpace(ingestion.OcrCommand)
                ? "ocrmypdf"
                : ingestion.OcrCommand.Trim();
            stages.Add(new()
            {
                StageId = "ocr",
                StageType = "ocr",
                Engine = ocrCommand,
                EngineVersion = RuntimeToolVersionProbe.Resolve(ocrCommand),
                OptionsSha256 = HashOptions(new
                {
                    ingestion.OcrArguments,
                    ingestion.OcrForceArguments,
                    languages = ocrLanguages,
                    ingestion.OcrImagePageEnabled,
                    ingestion.OcrImageRendererCommand,
                    ingestion.OcrImageTextCommand,
                    ingestion.OcrImagePageRenderDpi,
                    ingestion.OcrImagePageSegmentationMode,
                    ingestion.OcrImagePageMinWords
                }),
                DeviceId = "cpu:0",
                Concurrency = Math.Max(1, ingestion.OcrMaxConcurrency),
                DurationMs = timings.OcrMs,
                Attributes = new(StringComparer.Ordinal)
                {
                    ["applied"] = ocrApplied.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["languages"] = ocrLanguages ?? "",
                    ["coverage"] = ocrDiagnostics?.CoverageStatus ?? "",
                    ["imageTextEngineVersion"] = RuntimeToolVersionProbe.Resolve(
                        string.IsNullOrWhiteSpace(ingestion.OcrImageTextCommand)
                            ? "tesseract"
                            : ingestion.OcrImageTextCommand.Trim())
                }
            });
        }

        stages.Add(new()
        {
            StageId = "canonical_projection",
            StageType = "canonical_projection",
            Engine = "SAAIA.Backend",
            EngineVersion = codeRevision,
            OptionsSha256 = HashOptions(new
            {
                documentSchema = CanonicalSchema.DocumentVersion,
                source = extraction.Source,
                projection = "legacy_page_block_v1"
            }),
            DeviceId = "cpu:0",
            Concurrency = 1,
            DurationMs = timings.SectionMs + timings.UnitMs,
            Attributes = new(StringComparer.Ordinal)
            {
                ["source"] = extraction.Source,
                ["anchorPrecision"] = "page_block"
            }
        });
        stages.Add(new()
        {
            StageId = "retrieval_projection",
            StageType = "chunk",
            Engine = "SAAIA.Backend",
            EngineVersion = codeRevision,
            OptionsSha256 = HashOptions(new
            {
                ingestion.ChunkMaxWords,
                ingestion.ChunkOverlapWords,
                ingestion.ChunkMinWords
            }),
            DeviceId = "cpu:0",
            Concurrency = 1,
            DurationMs = timings.ChunkingMs
        });
        stages.Add(new()
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
            BatchSize = IngestionOptions.ResolveEmbeddingsBatchSize(ingestion.EmbeddingsBatchSize),
            Concurrency = Math.Max(1, ingestion.TeiMaxConcurrency),
            DurationMs = timings.EmbeddingMs
        });
        stages.Add(new()
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
        });

        return stages;
    }

    private static string HashOptions<T>(T value)
        => CanonicalContractJson.ComputeSha256(value);
}
