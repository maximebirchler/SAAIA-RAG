using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend;

internal sealed record NativeRawPageIdentity(Guid TenantId, Guid DocumentId, Guid RevisionId, string SourceSha256);
internal sealed record NativeRawPageRootBinding(NativeRawPageIdentity Identity, string CanonicalBundleSha256);
internal sealed record NativeRawPageGenerationBinding(string GenerationId, string ProjectionVersion,
    Guid ProcessingRunId, string ManifestSha256);

internal sealed record NativeRawBlockSnapshot(string BlockId, string BlockType, int Ordinal, int ReadingOrder,
    string Raw, string RawSha256, CanonicalPolygon? ObservedPolygon, IReadOnlyList<string> QualityFlags,
    string Engine, string EngineVersion, string Method, string StageId);

internal sealed record NativeRawPageSnapshot(string SchemaVersion, NativeRawPageRootBinding Root,
    NativeRawPageGenerationBinding Generation, int PhysicalPage, int PageCount, CanonicalPageGeometry Geometry,
    IReadOnlyList<string> PageQualityFlags, string Availability, string? Reason,
    IReadOnlyList<NativeRawBlockSnapshot>? Blocks);

internal enum NativeRawPageReadKind { ScopeMissing, LegacyAbsent, ExplicitInline, RawPage }

internal sealed record NativeRawPageExpectedBinding(string CanonicalBundleSha256, NativeRawPageReadKind Kind,
    string? GenerationId, string? ProjectionVersion);

internal sealed record NativeRawPageLoadResult(NativeRawPageReadKind Kind, NativeRawPageRootBinding? Root,
    NativeRawPageGenerationBinding? Generation, NativeRawPageSnapshot? Page, string? Reason,
    string? FileName, string? DocPath, long? PageCompressedBytesMaterialized, long? PageUtf8BytesAdmitted);

internal static class NativeRawPageSchema
{
    internal const string Snapshot = "native_raw_page_snapshot_v1";
    internal const string Catalog = "native_raw_page_catalog_v1";
    internal const string Projection = "native_raw_page_v1";
    internal const int MaximumPageBytes = 4 * 1024 * 1024;
    internal const int MaximumDocumentBytes = 32 * 1024 * 1024;
    internal const int MaximumCatalogBytes = 1024 * 1024;
}
