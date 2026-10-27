using System.Security.Cryptography;
using Dapper;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;

internal sealed record CanonicalTableObservationPageInventory(
    int PageNumber, string Availability, int? ObservationCount, int? AttemptCount);
internal sealed record CanonicalTableObservationDescriptor(
    string ObservationId, int PageNumber, string TableId, string SourceTableRef, int LineCount,
    string RawObservationSha256, string PhysicalReadingAvailability, int? BandCount, int? UnassignedCount);
internal sealed record CanonicalTableObservationInventory(
    string SourceSha256, string Availability, int? ObservationCount, int? AttemptCount,
    IReadOnlyList<CanonicalTableObservationPageInventory>? Pages,
    IReadOnlyList<CanonicalTableObservationDescriptor>? Observations);

internal static partial class CanonicalTableObservationRepo
{
    private const string VisibleRevisionScope = """
        JOIN document_revisions dr ON dr.tenant_id=c.tenant_id AND dr.doc_id=c.doc_id
          AND dr.revision_id=c.revision_id AND dr.source_hash=c.source_hash
        JOIN documents d ON d.tenant_id=dr.tenant_id AND d.doc_id=dr.doc_id
        WHERE c.tenant_id=@tenant AND c.doc_id=@document AND c.revision_id=@revision
          AND c.source_hash=@source AND d.indexed_version>0 AND d.status<>'deleted'
        """;

    internal static async Task<CanonicalTableObservationInventory?> ListAsync(
        NpgsqlDataSource dataSource, Guid tenant, Guid document, Guid revision, string sourceSha256,
        int? pageStart, int? pageEnd, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ListAsync(connection, tenant, document, revision, sourceSha256, pageStart, pageEnd, ct);
    }

    internal static async Task<CanonicalTableObservationInventory?> ListAsync(
        NpgsqlConnection connection, Guid tenant, Guid document, Guid revision, string sourceSha256,
        int? pageStart, int? pageEnd, CancellationToken ct)
    {
        using var probe = BackendDiagnostics.Start("document.table_observations.inventory");
        try
        {
            var result = await ListCoreAsync(connection, tenant, document, revision, sourceSha256, pageStart, pageEnd, ct);
            probe.Set("catalog_available", result is not null);
            probe.Set("observation_count", result?.ObservationCount);
            probe.Set("attempt_count", result?.AttemptCount);
            probe.Complete("success", result?.Availability ?? "legacy_or_disabled_or_not_visible");
            return result;
        }
        catch (Exception error) { probe.Fail(error); throw; }
    }

    private static async Task<CanonicalTableObservationInventory?> ListCoreAsync(
        NpgsqlConnection connection, Guid tenant, Guid document, Guid revision, string sourceSha256,
        int? pageStart, int? pageEnd, CancellationToken ct)
    {
        ValidateReadScope(tenant, document, revision, sourceSha256);
        if (pageStart is <= 0 || pageEnd is <= 0 || pageStart > pageEnd)
            throw new ArgumentException("canonical_table_observation_page_scope_invalid");
        var scope = new { tenant, document, revision, source = Convert.FromHexString(sourceSha256) };
        var row = await connection.QuerySingleOrDefaultAsync<CatalogRow>(new CommandDefinition(
            "SELECT c.availability AS \"Availability\",c.observation_count AS \"ObservationCount\","
            + "c.content_hash AS \"ContentHash\",c.payload AS \"Payload\" "
            + "FROM document_revision_table_observation_catalog c " + VisibleRevisionScope,
            scope, cancellationToken: ct));
        // Missing catalog is legacy/disabled/unknown, never an observed empty list.
        if (row is null) return null;
        var inventory = DecodeCatalog(row, sourceSha256);
        var stored = (await connection.QueryAsync<StoredDescriptor>(new CommandDefinition(
            """
            SELECT o.observation_id AS "ObservationId",o.page_number AS "PageNumber",
              o.table_id AS "TableId",o.line_count AS "LineCount",
              encode(o.raw_observation_hash,'hex') AS "RawObservationSha256"
            FROM document_revision_table_observations o
            JOIN document_revision_table_observation_catalog c ON c.tenant_id=o.tenant_id
              AND c.doc_id=o.doc_id AND c.revision_id=o.revision_id AND c.source_hash=o.source_hash
            """ + "\n" + VisibleRevisionScope + " ORDER BY o.page_number,o.observation_id LIMIT 9;",
            scope, cancellationToken: ct))).ToArray();
        var expected = inventory.Observations ?? [];
        if (stored.Length > 8 || stored.Length != expected.Count
            || stored.Any(item => !expected.Any(descriptor => descriptor.ObservationId == item.ObservationId
                && descriptor.PageNumber == item.PageNumber && descriptor.TableId == item.TableId
                && descriptor.LineCount == item.LineCount
                && descriptor.RawObservationSha256 == item.RawObservationSha256)))
            throw new InvalidDataException("canonical_table_observation_catalog_rows_mismatch");
        if (pageStart is null && pageEnd is null) return inventory;
        var pages = inventory.Pages?.Where(page => (!pageStart.HasValue || page.PageNumber >= pageStart.Value)
            && (!pageEnd.HasValue || page.PageNumber <= pageEnd.Value)).ToArray();
        var observations = inventory.Observations?.Where(item => (!pageStart.HasValue || item.PageNumber >= pageStart.Value)
            && (!pageEnd.HasValue || item.PageNumber <= pageEnd.Value)).ToArray();
        // A requested page outside this captured inventory is unknown, not observed empty.
        if (pages is null || pages.Length == 0)
            return new(inventory.SourceSha256, "unavailable", null, null, null, null);
        var availability = AggregateAvailability(pages.Select(page => page.Availability));
        return new(inventory.SourceSha256, availability,
            availability == "unavailable" ? null : observations?.Length,
            pages.All(page => page.AttemptCount.HasValue) ? pages.Sum(page => page.AttemptCount!.Value) : null,
            availability == "unavailable" ? null : pages,
            availability == "unavailable" ? null : observations);
    }

    internal static async Task<CanonicalTableRegionObservation?> ReadAsync(
        NpgsqlDataSource dataSource, Guid tenant, Guid document, Guid revision, string sourceSha256,
        int pageNumber, string tableId, string observationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ReadAsync(connection, tenant, document, revision, sourceSha256, pageNumber, tableId, observationId, ct);
    }

    internal static async Task<CanonicalTableRegionObservation?> ReadAsync(
        NpgsqlConnection connection, Guid tenant, Guid document, Guid revision, string sourceSha256,
        int pageNumber, string tableId, string observationId, CancellationToken ct)
    {
        using var probe = BackendDiagnostics.Start("document.table_observations.read");
        try
        {
            var result = await ReadCoreAsync(connection, tenant, document, revision, sourceSha256,
                pageNumber, tableId, observationId, ct);
            probe.Set("observation_available", result is not null);
            probe.Set("line_count", result?.Lines.Count);
            probe.Set("raw_bytes", result?.PublicationRawBytes?.LongLength);
            probe.Complete("success", result is null ? "not_visible_or_not_found" : "observed_unverified");
            return result;
        }
        catch (Exception error) { probe.Fail(error); throw; }
    }

    private static async Task<CanonicalTableRegionObservation?> ReadCoreAsync(
        NpgsqlConnection connection, Guid tenant, Guid document, Guid revision, string sourceSha256,
        int pageNumber, string tableId, string observationId, CancellationToken ct)
    {
        ValidateReadScope(tenant, document, revision, sourceSha256);
        if (pageNumber <= 0 || !IsIdentifier(tableId) || !IsIdentifier(observationId)) return null;
        var row = await connection.QuerySingleOrDefaultAsync<ObservationRow>(new CommandDefinition(
            """
            SELECT o.content_hash AS "ContentHash",o.payload AS "Payload",
              o.raw_observation_hash AS "RawObservationHash",o.line_count AS "LineCount",
              o.raw_observation_byte_count AS "RawObservationByteCount",o.raw_payload AS "RawPayload"
            FROM document_revision_table_observations o
            JOIN document_revision_table_observation_catalog c ON c.tenant_id=o.tenant_id
              AND c.doc_id=o.doc_id AND c.revision_id=o.revision_id AND c.source_hash=o.source_hash
            """ + "\n" + VisibleRevisionScope
            + " AND o.page_number=@pageNumber AND o.table_id=@tableId AND o.observation_id=@observationId;",
            new { tenant, document, revision, source = Convert.FromHexString(sourceSha256), pageNumber, tableId, observationId },
            cancellationToken: ct));
        if (row is null) return null;
        return DecodeObservation(row, document, revision, sourceSha256, pageNumber, tableId, observationId);
    }

    private static void ValidateReadScope(Guid tenant, Guid document, Guid revision, string sourceSha256)
    {
        if (tenant == Guid.Empty || document == Guid.Empty || revision == Guid.Empty
            || sourceSha256 is not { Length: 64 } || !sourceSha256.All(char.IsAsciiHexDigit))
            throw new ArgumentException("canonical_table_observation_scope_invalid");
    }

    private static bool IsIdentifier(string? id) => id is { Length: > 0 and <= 200 }
        && !id.Any(char.IsControl) && !string.IsNullOrWhiteSpace(id);

    private static void VerifyPayload(byte[] payload, byte[] hash, int maximumBytes)
    {
        if (payload.Length is < 2 || payload.Length > maximumBytes || hash.Length != 32
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash))
            throw new InvalidDataException("canonical_table_observation_stored_integrity_failed");
    }

    private sealed class CatalogRow
    {
        public string Availability { get; init; } = "";
        public int? ObservationCount { get; init; }
        public byte[] ContentHash { get; init; } = [];
        public byte[] Payload { get; init; } = [];
    }

    private sealed class StoredDescriptor
    {
        public string ObservationId { get; init; } = "";
        public int PageNumber { get; init; }
        public string TableId { get; init; } = "";
        public int LineCount { get; init; }
        public string RawObservationSha256 { get; init; } = "";
    }

    private sealed class ObservationRow
    {
        public byte[] ContentHash { get; init; } = [];
        public byte[] Payload { get; init; } = [];
        public byte[] RawObservationHash { get; init; } = [];
        public int LineCount { get; init; }
        public int RawObservationByteCount { get; init; }
        public byte[] RawPayload { get; init; } = [];
    }
}
