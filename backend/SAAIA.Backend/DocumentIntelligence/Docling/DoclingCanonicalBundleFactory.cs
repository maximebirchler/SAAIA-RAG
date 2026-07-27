using SAAIA.Contracts.DocumentIntelligence;

internal sealed record DoclingCanonicalBundleRequest(
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
    DocumentIntelligenceOptions Options,
    IReadOnlyList<IngestionStageManifest> Stages,
    CanonicalDocument CanonicalDocument,
    DoclingConvertResponse Conversion,
    IReadOnlyList<ProjectedRetrievalChunk> RetrievalChunks);

internal static class DoclingCanonicalBundleFactory
{
    public static CanonicalIngestionBundle Create(DoclingCanonicalBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectionStage = request.Stages.FirstOrDefault(
            static stage => string.Equals(
                stage.StageType,
                "canonical_projection",
                StringComparison.Ordinal));
        if (projectionStage is null)
        {
            throw new InvalidOperationException(
                "A canonical projection stage is required to project a canonical document.");
        }

        var source = request.Conversion.Document.JsonContent
            ?? throw new InvalidDataException("Docling JSON content is required.");
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
                ["retrievalChunk"] = "retrieval_chunk_v1",
                ["doclingDocument"] = $"{source.SchemaName}:{source.Version}"
            }
        };
        var envelope = CanonicalContractJson.CreateEnvelope(manifest);
        var document = request.CanonicalDocument
            ?? throw new InvalidDataException(
                "The reconciled canonical document is required.");
        if (document.DocumentId != request.DocumentId
            || document.RevisionId != request.RevisionId
            || !string.Equals(
                document.Source.Sha256,
                request.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The reconciled canonical document identity does not match the bundle request.");
        }

        document.ManifestSha256 = envelope.ManifestSha256;
        CanonicalContractValidator.ValidateOrThrow(document);
        var anchors = request.RetrievalChunks
            .OrderBy(static chunk => chunk.ChunkIndex)
            .Select(chunk => DoclingCanonicalSourceAnchorProjector.ProjectSelection(
                document,
                DocumentFoundationRepo
                    .BuildStableRetrievalChunkId(
                        request.DocumentId,
                        request.IndexedVersion,
                        chunk.ChunkIndex)
                    .ToString("D"),
                "retrieval_chunk",
                chunk.PageStart,
                chunk.PageEnd,
                chunk.CanonicalBlockIds,
                chunk.CanonicalSpanIds,
                chunk.CanonicalTableCellIds))
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
