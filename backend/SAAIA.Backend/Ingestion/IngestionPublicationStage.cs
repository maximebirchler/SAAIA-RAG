using Npgsql;
using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record IngestionPublicationIdentity(Guid TenantId, Guid JobId, string RelativePath,
    byte[] SourceHash, long SourceBytes, DateTime ModifiedUtc, int Version);
internal sealed record IngestionPublicationArtifacts(IReadOnlyList<ExtractedPdfPage> Pages,
    IReadOnlyList<ExtractedDocumentSection> Sections, IReadOnlyList<ExtractedDocumentUnit> Units,
    IReadOnlyList<ProjectedRetrievalChunk> Chunks, IReadOnlyList<ExtractedExactMatchEntry> ExactEntries,
    IReadOnlyList<ProjectedContextualTextEntry> ContextualEntries, CanonicalIngestionBundle? CanonicalBundle,
    IReadOnlyList<ExtractedDocumentUnit>? ProfileUnits);
internal sealed record IngestionExtractionMetadata(string Source, bool OcrAttempted, bool OcrApplied,
    string? OcrLanguages, long? OcrMilliseconds, PdfOcrDiagnostics? OcrDiagnostics,
    PdfExtractionQualitySummary NativeQuality, IngestionCapabilityAProfileSeed? ProfileSeed);

internal static class IngestionPublicationStage
{
    /// <summary>One transaction remains owned by JobRepo; a lost/superseded lease never counts as publication.</summary>
    internal static async Task<bool> PublishAsync(NpgsqlDataSource dataSource, IngestionPublicationIdentity identity,
        IngestionPublicationArtifacts artifacts, IngestionExtractionMetadata extraction, ILogger logger, CancellationToken cancellationToken)
    {
        using var probe = BackendDiagnostics.Start("ingestion.publish_transaction", logger);
        probe.Set("job_id", identity.JobId);
        probe.Set("document_version", identity.Version);
        probe.Set("pages", artifacts.Pages.Count);
        probe.Set("chunks", artifacts.Chunks.Count);
        probe.Set("canonical_bundle", artifacts.CanonicalBundle is not null);
        try
        {
            var committed = await JobRepo.CompleteUpsertAsync(dataSource, identity.TenantId, identity.JobId,
                identity.RelativePath, identity.SourceHash, identity.SourceBytes, identity.ModifiedUtc, identity.Version,
                artifacts.Pages, artifacts.Sections, artifacts.Units, artifacts.Chunks, artifacts.ExactEntries,
                artifacts.ContextualEntries, cancellationToken, extraction.Source, extraction.OcrAttempted,
                extraction.OcrApplied, extraction.OcrLanguages, extraction.OcrMilliseconds, extraction.OcrDiagnostics,
                extraction.NativeQuality, extraction.ProfileSeed, artifacts.CanonicalBundle, artifacts.ProfileUnits).ConfigureAwait(false);
            probe.Complete(committed ? "success" : "superseded", committed ? null : "not_published");
            return committed;
        }
        catch (Exception exception) { probe.Fail(exception); throw; }
    }
}
