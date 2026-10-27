using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using SAAIA.Backend;
using SAAIA.Contracts.DocumentIntelligence;

/// <summary>Immutable secondary evidence; publication shares the canonical bundle transaction.</summary>
internal static partial class CanonicalTableObservationRepo
{
    internal const int MaximumRawBytes = CanonicalTableRegionObservationValidator.MaximumRawBytes;
    internal const int MaximumCanonicalBytes = CanonicalTableRegionObservationValidator.MaximumCanonicalBytes;
    internal const int MaximumDocumentBytes = CanonicalTableRegionObservationValidator.MaximumDocumentBytes;

    internal static async Task StoreAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, CanonicalDocument document, CancellationToken ct)
    {
        using var probe = BackendDiagnostics.Start("ingestion.table_observations.publish");
        try
        {
            CanonicalContractValidator.ValidateOrThrow(document);
            ValidateReadScope(tenantId, document.DocumentId, document.RevisionId, document.Source.Sha256);
            if (document.Pages.All(page => page.TableRegionObservations is null))
            {
                var alreadyCaptured = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                    SELECT EXISTS(SELECT 1 FROM document_revision_table_observation_catalog
                        WHERE tenant_id=@tenant AND revision_id=@revision);
                    """, new { tenant = tenantId, revision = document.RevisionId }, transaction, cancellationToken: ct));
                if (alreadyCaptured)
                    throw new InvalidDataException("canonical_table_observation_capture_cannot_be_erased_within_revision");
                probe.Complete("skipped", "feature_disabled_or_legacy");
                return;
            }
            if (document.Pages.Any(page => page.TableRegionObservations is null))
                throw new InvalidDataException("canonical_table_observation_mixed_capture_state");
            var rows = document.Pages.SelectMany(page => page.TableRegionObservations!.Observations ?? [])
                .Select(PrepareObservation).ToArray();
            if (rows.Length > 8 || rows.Sum(row => (long)row.Payload.Length + row.RawPayload.Length) > MaximumDocumentBytes)
                throw new InvalidDataException("canonical_table_observation_document_budget_exceeded");
            var inventory = BuildInventory(document);
            var catalogPayload = Encoding.UTF8.GetBytes(CanonicalContractJson.SerializeCanonical(inventory));
            if (catalogPayload.Length > 1_048_576)
                throw new InvalidDataException("canonical_table_observation_catalog_budget_exceeded");
            var scope = new
            {
                tenant = tenantId, document = document.DocumentId, revision = document.RevisionId,
                source = Convert.FromHexString(document.Source.Sha256), availability = inventory.Availability,
                count = inventory.ObservationCount, hash = SHA256.HashData(catalogPayload), payload = catalogPayload
            };
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO document_revision_table_observation_catalog(tenant_id,doc_id,revision_id,
                    source_hash,availability,observation_count,content_hash,payload)
                VALUES(@tenant,@document,@revision,@source,@availability,@count,@hash,@payload)
                ON CONFLICT(tenant_id,revision_id) DO NOTHING;
                """, scope, transaction, cancellationToken: ct));
            var sameCatalog = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM document_revision_table_observation_catalog
                    WHERE tenant_id=@tenant AND doc_id=@document AND revision_id=@revision AND source_hash=@source
                      AND availability=@availability AND observation_count IS NOT DISTINCT FROM @count
                      AND content_hash=@hash AND payload=@payload);
                """, scope, transaction, cancellationToken: ct));
            if (!sameCatalog) throw new InvalidDataException("canonical_table_observation_catalog_publication_conflict");
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                await StoreObservationAsync(connection, transaction, tenantId, row, ct);
            }
            probe.Set("observation_count", rows.Length);
            probe.Set("raw_bytes", rows.Sum(row => (long)row.RawPayload.Length));
            probe.Set("canonical_bytes", rows.Sum(row => (long)row.Payload.Length));
            probe.Set("line_count", rows.Sum(row => (long)row.Observation.Lines.Count));
            probe.Complete(inventory.Availability == "observed" ? "success" : "degraded", inventory.Availability);
        }
        catch (Exception error) { probe.Fail(error); throw; }
    }

    private sealed record PreparedObservation(CanonicalTableRegionObservation Observation, byte[] Payload, byte[] RawPayload);

    private static PreparedObservation PrepareObservation(CanonicalTableRegionObservation observation)
    {
        CanonicalTableRegionObservationValidator.ValidateOrThrow(observation, requireRawObservation: true);
        var payload = Encoding.UTF8.GetBytes(CanonicalContractJson.SerializeCanonical(observation));
        var raw = observation.PublicationRawBytes!;
        if (payload.Length is < 2 or > MaximumCanonicalBytes || raw.Length is < 2 or > MaximumRawBytes)
            throw new InvalidDataException("canonical_table_observation_payload_budget_exceeded");
        return new(observation, payload, raw);
    }

    private static async Task StoreObservationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid tenantId, PreparedObservation row, CancellationToken ct)
    {
        var item = row.Observation;
        var scope = new
        {
            tenant = tenantId, document = item.DocumentId, revision = item.RevisionId,
            source = Convert.FromHexString(item.SourceSha256), page = item.PhysicalPage,
            table = item.TableId, observation = item.ObservationId,
            rawHash = Convert.FromHexString(item.RawObservationSha256), raw = row.RawPayload,
            rawSize = row.RawPayload.Length, lines = item.Lines.Count,
            hash = SHA256.HashData(row.Payload), payload = row.Payload
        };
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO document_revision_table_observations(tenant_id,doc_id,revision_id,source_hash,
                page_number,table_id,observation_id,raw_observation_hash,raw_observation_byte_count,
                raw_payload,line_count,content_hash,payload)
            VALUES(@tenant,@document,@revision,@source,@page,@table,@observation,@rawHash,@rawSize,
                @raw,@lines,@hash,@payload)
            ON CONFLICT(tenant_id,revision_id,observation_id) DO NOTHING;
            """, scope, transaction, cancellationToken: ct));
        var exact = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM document_revision_table_observations
                WHERE tenant_id=@tenant AND doc_id=@document AND revision_id=@revision AND source_hash=@source
                  AND page_number=@page AND table_id=@table AND observation_id=@observation
                  AND raw_observation_hash=@rawHash AND raw_observation_byte_count=@rawSize
                  AND raw_payload=@raw AND line_count=@lines AND content_hash=@hash AND payload=@payload);
            """, scope, transaction, cancellationToken: ct));
        if (!exact) throw new InvalidDataException("canonical_table_observation_immutable_publication_conflict");
    }

    private static CanonicalTableObservationInventory BuildInventory(CanonicalDocument document)
    {
        var pages = document.Pages.OrderBy(page => page.PageNumber).Select(page =>
        {
            var set = page.TableRegionObservations!;
            return new CanonicalTableObservationPageInventory(page.PageNumber, set.Availability,
                set.Observations?.Count, set.Attempts?.Count(attempt => attempt.Attempted));
        }).ToArray();
        var availability = AggregateAvailability(pages.Select(page => page.Availability));
        var observations = document.Pages.SelectMany(page => page.TableRegionObservations!.Observations ?? [])
            .OrderBy(item => item.PhysicalPage).ThenBy(item => item.ObservationId, StringComparer.Ordinal)
            .Select(item => new CanonicalTableObservationDescriptor(item.ObservationId, item.PhysicalPage,
                item.TableId, item.SourceTableRef, item.Lines.Count, item.RawObservationSha256,
                item.PhysicalReading.Availability, item.PhysicalReading.Bands?.Count,
                item.PhysicalReading.UnassignedLineIds.Count)).ToArray();
        return new(document.Source.Sha256.ToLowerInvariant(), availability,
            availability == "unavailable" ? null : observations.Length,
            pages.All(page => page.AttemptCount.HasValue) ? pages.Sum(page => page.AttemptCount!.Value) : null,
            availability == "unavailable" ? null : pages, availability == "unavailable" ? null : observations);
    }

    private static string AggregateAvailability(IEnumerable<string> availability)
    {
        var values = availability.Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : "partial";
    }

    private static CanonicalTableObservationInventory DecodeCatalog(CatalogRow row, string sourceSha256)
    {
        VerifyPayload(row.Payload, row.ContentHash, 1_048_576);
        var inventory = CanonicalContractJson.Deserialize<CanonicalTableObservationInventory>(Encoding.UTF8.GetString(row.Payload));
        if (!string.Equals(inventory.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)
            || inventory.Availability != row.Availability || inventory.ObservationCount != row.ObservationCount
            || (inventory.Availability == "unavailable"
                ? inventory.ObservationCount is not null || inventory.Pages is not null || inventory.Observations is not null
                : inventory.Availability is not ("observed" or "partial") || inventory.Pages is not { Count: > 0 }
                  || inventory.Observations is null || inventory.ObservationCount != inventory.Observations.Count)
            || inventory.Observations is { Count: > 8 })
            throw new InvalidDataException("canonical_table_observation_catalog_integrity_failed");
        return inventory;
    }

    private static CanonicalTableRegionObservation DecodeObservation(ObservationRow row, Guid document, Guid revision,
        string sourceSha256, int pageNumber, string tableId, string observationId)
    {
        VerifyPayload(row.Payload, row.ContentHash, MaximumCanonicalBytes);
        VerifyPayload(row.RawPayload, row.RawObservationHash, MaximumRawBytes);
        var item = CanonicalContractJson.Deserialize<CanonicalTableRegionObservation>(Encoding.UTF8.GetString(row.Payload));
        item.PublicationRawBytes = row.RawPayload;
        CanonicalTableRegionObservationValidator.ValidateOrThrow(item, requireRawObservation: true);
        if (item.DocumentId != document || item.RevisionId != revision || item.PhysicalPage != pageNumber
            || !string.Equals(item.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)
            || item.TableId != tableId || item.ObservationId != observationId || item.Lines.Count != row.LineCount
            || row.RawPayload.Length != row.RawObservationByteCount
            || !string.Equals(item.RawObservationSha256, Convert.ToHexString(row.RawObservationHash), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("canonical_table_observation_stored_identity_failed");
        return item;
    }
}
