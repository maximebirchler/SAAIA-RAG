using SAAIA.Contracts.DocumentIntelligence;

internal sealed record LegacyCanonicalBundleRequest(
    Guid DocumentId,
    Guid RevisionId,
    Guid ProcessingRunId,
    int IndexedVersion,
    string SourceSha256,
    long SourceSizeBytes,
    string SourceDisplayName,
    string CodeRevision,
    DateTimeOffset CreatedAtUtc,
    IngestionHardwareProfile Hardware,
    IReadOnlyList<IngestionStageManifest> Stages,
    PdfExtractionResult Extraction,
    IReadOnlyList<ExtractedDocumentSection> Sections,
    IReadOnlyList<ProjectedRetrievalChunk> RetrievalChunks);

internal static class LegacyCanonicalBundleFactory
{
    public static CanonicalIngestionBundle Create(LegacyCanonicalBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectionStage = request.Stages.FirstOrDefault(
            static stage => string.Equals(stage.StageType, "canonical_projection", StringComparison.Ordinal));
        if (projectionStage is null)
            throw new InvalidOperationException("A canonical projection stage is required to project a canonical document.");

        var manifest = new IngestionManifest
        {
            ProcessingRunId = request.ProcessingRunId,
            DocumentId = request.DocumentId,
            RevisionId = request.RevisionId,
            SourceSha256 = request.SourceSha256,
            CreatedAtUtc = request.CreatedAtUtc,
            CodeRevision = request.CodeRevision,
            Hardware = request.Hardware,
            Stages = request.Stages.ToList(),
            Artifacts =
            [
                new()
                {
                    ArtifactId = CanonicalStableId.Create(
                        "artifact",
                        CanonicalArtifactTypes.SourceDocument,
                        request.SourceSha256),
                    ArtifactType = CanonicalArtifactTypes.SourceDocument,
                    SchemaVersion = "application/pdf",
                    Sha256 = request.SourceSha256,
                    StorageKey = $"content://sha256/{request.SourceSha256}",
                    SizeBytes = request.SourceSizeBytes
                }
            ],
            SchemaBindings = new(StringComparer.Ordinal)
            {
                ["canonicalDocument"] = CanonicalSchema.DocumentVersion,
                ["sourceAnchor"] = CanonicalSchema.SourceAnchorVersion,
                ["ingestionBundle"] = CanonicalSchema.BundleVersion,
                ["retrievalChunk"] = "retrieval_chunk_v1"
            }
        };
        var envelope = CanonicalContractJson.CreateEnvelope(manifest);
        var document = LegacyPdfCanonicalDocumentAdapter.Project(
            new(
                request.DocumentId,
                request.RevisionId,
                request.SourceSha256,
                request.SourceSizeBytes,
                request.SourceDisplayName,
                envelope.ManifestSha256,
                projectionStage.StageId,
                request.Extraction.Source,
                projectionStage.Engine,
                projectionStage.EngineVersion,
                request.Extraction.OcrLanguages),
            request.Extraction,
            request.Sections);
        var anchors = request.RetrievalChunks
            .OrderBy(static chunk => chunk.ChunkIndex)
            .Select(chunk => LegacyCanonicalSourceAnchorProjector.ProjectPageRange(
                document,
                DocumentFoundationRepo
                    .BuildStableRetrievalChunkId(request.DocumentId, request.IndexedVersion, chunk.ChunkIndex)
                    .ToString("D"),
                "retrieval_chunk",
                chunk.PageStart,
                chunk.PageEnd))
            .ToList();
        var bundle = new CanonicalIngestionBundle
        {
            Document = document,
            ManifestEnvelope = envelope,
            SourceAnchors = anchors
        };

        CanonicalIngestionBundleValidator.ValidateOrThrow(bundle);
        return bundle;
    }
}
