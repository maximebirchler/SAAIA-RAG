using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend.AdvancedAnalysis;

internal static class AdvancedAnalysisVisualSourceReader
{
    internal static void ValidateRequest(AdvancedAnalysisSearchRequest request, bool enabled)
    {
        if (!request.IncludeVisualSources && request.VisualSourceOffset is null && request.VisualSourceLimit is null)
            return;

        if (!enabled)
            throw new AdvancedAnalysisToolException("visual_source_citations_disabled");

        if (!request.IncludeVisualSources || request.Operation != "read_source"
            || request.VisualSourceOffset is < 0 or > CanonicalVisualAssetRepo.MaximumDocumentAssets
            || request.VisualSourceLimit is < 1 or > 64)
            throw new AdvancedAnalysisToolException("visual_source_request_invalid");
    }

    internal static async Task<AdvancedAnalysisVisualSources> ReadAsync(
        NpgsqlDataSource source,
        Guid tenant,
        AdvancedAnalysisSearchRequest request,
        AdvancedAnalysisResultEvidence observed,
        IReadOnlyList<AdvancedAnalysisEvidenceReference> references,
        int maximumCharacters,
        CancellationToken ct)
    {
        var document = Guid.Parse(request.DocId!);
        var revision = Guid.Parse(request.RevisionId!);
        var first = request.PageStart!.Value;
        var last = request.PageEnd!.Value;
        var offset = request.VisualSourceOffset ?? 0;
        var limit = request.VisualSourceLimit ?? 4;

        await using var connection = await source.OpenConnectionAsync(ct);
        var inventory = await CanonicalVisualAssetRepo.ListCitationsAsync(
            connection, tenant, document, revision, observed.SourceHash, first, last, offset, limit, ct);
        var regions = await ReadObservedRegionsAsync(
            connection, tenant, document, revision, observed.SourceHash, first, last, references, ct);

        return AdvancedAnalysisVisualSourceProjector.Project(
            document, revision, observed.SourceHash, first, last,
            inventory.ScopeExists, inventory.TotalCount, inventory.Assets,
            offset, limit, maximumCharacters, regions);
    }

    private sealed record AnchorRow(string AnchorId, string Payload, string ContentSha256);

    private static async Task<IReadOnlyDictionary<int, AdvancedAnalysisVisualObservedRegion>> ReadObservedRegionsAsync(
        NpgsqlConnection connection,
        Guid tenant,
        Guid document,
        Guid revision,
        string source,
        int first,
        int last,
        IReadOnlyList<AdvancedAnalysisEvidenceReference> references,
        CancellationToken ct)
    {
        var candidates = references
            .Where(reference => reference.AnchorId is not null && !string.IsNullOrWhiteSpace(reference.EvidenceId)
                && string.Equals(reference.DocId, document.ToString("D"), StringComparison.OrdinalIgnoreCase)
                && string.Equals(reference.RevisionId, revision.ToString("D"), StringComparison.OrdinalIgnoreCase)
                && string.Equals(reference.SourceHash, source, StringComparison.OrdinalIgnoreCase))
            .OrderBy(reference => reference.AnchorId, StringComparer.Ordinal)
            .ToArray();

        // This is one explicitly observed anchor per requested page. It is not
        // a complete region inventory or a bbox-to-figure association.
        var ids = Enumerable.Range(first, last - first + 1)
            .Select(page => candidates.FirstOrDefault(reference => reference.PageStart <= page && reference.PageEnd >= page)?.AnchorId)
            .Where(id => id is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<int, AdvancedAnalysisVisualObservedRegion>();

        var rows = await connection.QueryAsync<AnchorRow>(new CommandDefinition("""
            SELECT sa.anchor_id AS "AnchorId", sa.payload::text AS "Payload",
                encode(sa.content_hash,'hex') AS "ContentSha256"
            FROM document_source_anchors sa
            JOIN document_revisions dr ON dr.tenant_id=sa.tenant_id AND dr.revision_id=sa.revision_id
            WHERE sa.tenant_id=@tenant AND dr.doc_id=@document AND dr.revision_id=@revision
                AND dr.source_hash=@hash AND sa.anchor_id=ANY(@ids);
            """, new { tenant, document, revision, hash = Convert.FromHexString(source), ids }, cancellationToken: ct));

        var result = new Dictionary<int, AdvancedAnalysisVisualObservedRegion>();
        foreach (var row in rows.OrderBy(row => row.AnchorId, StringComparer.Ordinal))
        {
            SourceAnchor? anchor;
            try
            {
                anchor = JsonSerializer.Deserialize<SourceAnchor>(row.Payload);
            }
            catch (JsonException)
            {
                throw new AdvancedAnalysisToolException("visual_source_anchor_integrity_failed");
            }
            if (anchor is null
                || CanonicalContractJson.ComputeSha256(anchor) != row.ContentSha256
                || anchor.AnchorId != row.AnchorId
                || anchor.DocumentId != document
                || anchor.RevisionId != revision
                || !string.Equals(anchor.SourceSha256, source, StringComparison.OrdinalIgnoreCase))
                throw new AdvancedAnalysisToolException("visual_source_anchor_integrity_failed");

            var proof = candidates.First(reference => reference.AnchorId == row.AnchorId);
            foreach (var region in anchor.Regions.Where(region => region.PageNumber >= first
                && region.PageNumber <= last && region.Polygon is not null))
            {
                var polygon = region.Polygon!;
                if (polygon.Points.Count < 3 || polygon.Points.Any(point => !double.IsFinite(point.X)
                    || !double.IsFinite(point.Y) || point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1))
                    throw new AdvancedAnalysisToolException("visual_source_anchor_geometry_invalid");

                result.TryAdd(region.PageNumber, new AdvancedAnalysisVisualObservedRegion(
                    proof.EvidenceId ?? "", anchor.AnchorId, anchor.Precision, region.BlockIds, polygon));
            }
        }

        return result;
    }
}
