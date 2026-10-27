using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record NativeObservationReadIdentity(
    Guid TenantId, Guid DocumentId, Guid RevisionId, string SourceSha256, string ManifestSha256);

/// <summary>A private read view of one captured page, never a partial canonical document.</summary>
internal sealed record NativeObservationPageReadContext(
    NativeObservationReadIdentity Identity, int PhysicalPage, int PageCount,
    CanonicalPageGeometry Geometry, IReadOnlyList<string> PageQualityFlags,
    string Availability, string? Reason, IReadOnlyList<NativeRawBlockSnapshot>? Blocks)
{
    internal static NativeObservationPageReadContext FromInline(
        Guid tenantId, CanonicalDocument document, int physicalPage)
    {
        ValidateIdentity(document.DocumentId, document.RevisionId, document.Source.Sha256, document.ManifestSha256);
        var matching = document.Pages.Where(page => page.PageNumber == physicalPage && physicalPage > 0).ToArray();
        if (matching.Length != 1)
            throw Invalid();
        var page = matching[0];
        var blocks = page.Blocks.Where(CanonicalObservationSelector.IsNativeObservation)
            .OrderBy(block => block.ReadingOrder).ThenBy(block => block.Ordinal)
            .ThenBy(block => block.BlockId, StringComparer.Ordinal)
            .Select(block => new NativeRawBlockSnapshot(block.BlockId, block.BlockType, block.Ordinal, block.ReadingOrder,
                block.Text.Raw, block.Text.RawSha256, CopyPolygon(block.Polygon), block.QualityFlags.ToArray(),
                block.Provenance.Engine, block.Provenance.EngineVersion, block.Provenance.Method, block.Provenance.StageId))
            .ToArray();
        return new(new(tenantId, document.DocumentId, document.RevisionId, document.Source.Sha256, document.ManifestSha256),
            physicalPage, Math.Max(document.PageCount, document.Pages.Max(item => item.PageNumber)),
            CopyGeometry(page.Geometry), page.QualityFlags.ToArray(), blocks.Length > 0 ? "observed" : "unavailable",
            blocks.Length > 0 ? null : "no_captured_native_observation", blocks.Length > 0 ? blocks : null);
    }

    internal static NativeObservationPageReadContext FromStored(NativeRawPageSnapshot snapshot)
    {
        if (snapshot.Root is not { Identity: { } identity } || snapshot.Generation is not { } generation)
            throw Invalid();
        ValidateIdentity(identity.DocumentId, identity.RevisionId, identity.SourceSha256, generation.ManifestSha256);
        if (snapshot.SchemaVersion != NativeRawPageSchema.Snapshot || identity.TenantId == Guid.Empty
            || generation.ProjectionVersion != NativeRawPageSchema.Projection || generation.ProcessingRunId == Guid.Empty
            || !IsHash(snapshot.Root.CanonicalBundleSha256) || !IsHash(generation.GenerationId)
            || snapshot.PhysicalPage <= 0 || snapshot.PhysicalPage > snapshot.PageCount
            || snapshot.Availability is not ("observed" or "unavailable")
            || snapshot.Availability == "observed" && snapshot.Blocks is not { Count: > 0 }
            || snapshot.Availability == "unavailable" && (snapshot.Blocks is not null
                || snapshot.Reason != "no_captured_native_observation"))
            throw Invalid();
        var blocks = snapshot.Blocks?.Select(block => block with
        {
            ObservedPolygon = CopyPolygon(block.ObservedPolygon),
            QualityFlags = block.QualityFlags.ToArray()
        }).ToArray();
        return new(new(identity.TenantId, identity.DocumentId, identity.RevisionId, identity.SourceSha256, generation.ManifestSha256),
            snapshot.PhysicalPage, snapshot.PageCount, CopyGeometry(snapshot.Geometry), snapshot.PageQualityFlags.ToArray(),
            snapshot.Availability, snapshot.Reason, blocks);
    }

    internal static void ValidateIdentity(Guid documentId, Guid revisionId, string sourceSha256, string manifestSha256)
    {
        if (documentId == Guid.Empty || revisionId == Guid.Empty || !IsHash(sourceSha256) || !IsHash(manifestSha256))
            throw Invalid();
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static CanonicalPolygon? CopyPolygon(CanonicalPolygon? polygon)
        => polygon is null ? null : new() { Points = polygon.Points.Select(point => new CanonicalPoint { X = point.X, Y = point.Y }).ToList() };

    private static CanonicalPageGeometry CopyGeometry(CanonicalPageGeometry geometry) => new()
    {
        Width = geometry.Width, Height = geometry.Height, Unit = geometry.Unit,
        RotationDegrees = geometry.RotationDegrees, CoordinateSpace = geometry.CoordinateSpace
    };

    private static AdvancedAnalysisToolException Invalid() => new("native_observation_identity_or_window_invalid");
}
