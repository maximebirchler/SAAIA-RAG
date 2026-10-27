using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisTableObservationRead(
    AdvancedAnalysisAlternativeTableReadings Readings,
    IReadOnlyList<AdvancedAnalysisEvidenceReference> References);

internal sealed record AdvancedAnalysisTableObservationAnchor(
    SourceAnchor Anchor, CanonicalTableRegionObservation Observation, CanonicalTableRegionObservationLine Line,
    string FileName, string DocPath);

/// <summary>Bounded secondary reads and exact line anchors, with no retrieval or OCR fallback.</summary>
internal static class AdvancedAnalysisTableObservationReader
{
    internal static void ValidateRequest(AdvancedAnalysisSearchRequest request)
    {
        var present = request.TableObservationId is not null || request.TableObservationView is not null
            || request.TableObservationBandOrdinal is not null || request.TableObservationLineOffset is not null
            || request.TableObservationLineLimit is not null;
        if (!present) return;
        if (request.Operation != "read_source" || request.PageStart != request.PageEnd || request.PageStart is not > 0
            || request.TableOrdinal is not null || request.TableRowOffset is not null || request.TableRowLimit is not null
            || request.TableObservationView is not ("inventory" or "raw" or "physical_band" or "unassigned")
            || request.TableObservationLineOffset is < 0 or > 5_000 || request.TableObservationLineLimit is < 1 or > 64)
            throw new AdvancedAnalysisToolException("table_observation_read_window_invalid");
        if (request.TableObservationView == "inventory")
        {
            if (request.TableObservationId is not null || request.TableObservationBandOrdinal is not null
                || request.TableObservationLineOffset is not null || request.TableObservationLineLimit is not null)
                throw new AdvancedAnalysisToolException("table_observation_inventory_arguments_invalid");
        }
        else if (string.IsNullOrWhiteSpace(request.TableObservationId) || request.TableObservationId.Length > 200
            || (request.TableObservationView == "physical_band") != request.TableObservationBandOrdinal.HasValue
            || request.TableObservationBandOrdinal is < 0 or > 31)
            throw new AdvancedAnalysisToolException("table_observation_read_window_invalid");
    }

    internal static async Task<AdvancedAnalysisTableObservationRead> ReadAsync(
        NpgsqlDataSource dataSource, Guid tenantId, AdvancedAnalysisSearchRequest request,
        AdvancedAnalysisResultEvidence observed, int maximumCharacters, CancellationToken ct)
    {
        ValidateRequest(request);
        var document = Guid.Parse(request.DocId!);
        var revision = Guid.Parse(request.RevisionId!);
        if (!Guid.TryParse(observed.DocId, out var observedDocument) || observedDocument != document
            || !Guid.TryParse(observed.RevisionId, out var observedRevision) || observedRevision != revision
            || string.IsNullOrWhiteSpace(observed.SourceHash))
            throw new AdvancedAnalysisToolException("table_observation_source_identity_invalid");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await connection.ExecuteAsync(new CommandDefinition("SET TRANSACTION READ ONLY", transaction: transaction, cancellationToken: ct));
        var inventory = await CanonicalTableObservationRepo.ListAsync(connection, tenantId, document, revision,
            observed.SourceHash, request.PageStart, request.PageEnd, ct);
        if (request.TableObservationView == "inventory")
        {
            await transaction.CommitAsync(ct);
            return new(new(inventory, null), []);
        }
        var descriptor = inventory?.Observations?.SingleOrDefault(item =>
            item.ObservationId == request.TableObservationId && item.PageNumber == request.PageStart);
        if (descriptor is null)
            throw new AdvancedAnalysisToolException("table_observation_not_observed");
        var observation = await CanonicalTableObservationRepo.ReadAsync(connection, tenantId, document, revision,
            observed.SourceHash, descriptor.PageNumber, descriptor.TableId, descriptor.ObservationId, ct)
            ?? throw new AdvancedAnalysisToolException("table_observation_unavailable");
        var sequence = request.TableObservationView switch
        {
            "raw" => observation.Lines.OrderBy(line => line.RawOrdinal).Select(line => line.LineId).ToArray(),
            "unassigned" when observation.PhysicalReading.Availability == "candidate_only"
                => observation.PhysicalReading.UnassignedLineIds.ToArray(),
            "physical_band" when observation.PhysicalReading.Availability == "candidate_only"
                => observation.PhysicalReading.Bands?.SingleOrDefault(band => band.BandOrdinal == request.TableObservationBandOrdinal)
                    ?.OrderedLineIds.ToArray() ?? throw new AdvancedAnalysisToolException("table_observation_band_not_observed"),
            _ => []
        };
        var ids = sequence.Skip(request.TableObservationLineOffset ?? 0).Take(request.TableObservationLineLimit ?? 16).ToArray();
        var cache = new Dictionary<string, CanonicalTableRegionObservation>(StringComparer.Ordinal)
        {
            [CacheKey(document, revision, observed.SourceHash, descriptor.PageNumber, descriptor.TableId, descriptor.ObservationId)] = observation
        };
        var anchors = await ReadRowsAsync(connection, tenantId, document, revision, ids, null, ct);
        var byLine = new Dictionary<string, AdvancedAnalysisTableObservationAnchor>(StringComparer.Ordinal);
        foreach (var row in anchors)
        {
            var bound = await BindAsync(connection, tenantId, row, cache, ct);
            if (bound.Observation.ObservationId != observation.ObservationId
                || !byLine.TryAdd(bound.Line.LineId, bound))
                throw new AdvancedAnalysisToolException("table_observation_anchor_identity_invalid");
        }
        if (byLine.Count != ids.Length)
            throw new AdvancedAnalysisToolException("table_observation_line_anchor_unavailable");
        var view = AdvancedAnalysisTableObservationWindows.Build(observation, request.TableObservationView!,
            request.TableObservationBandOrdinal, request.TableObservationLineOffset ?? 0,
            request.TableObservationLineLimit ?? 16, byLine.ToDictionary(item => item.Key, item => item.Value.Anchor.AnchorId),
            maximumCharacters);
        var references = (view.Lines ?? []).Select(line =>
        {
            var bound = byLine[line.LineId];
            return new AdvancedAnalysisEvidenceReference
            {
                DocId = document.ToString("D"), RevisionId = revision.ToString("D"), SourceHash = observed.SourceHash,
                DocPath = bound.DocPath, FileName = bound.FileName, PageStart = observation.PhysicalPage,
                PageEnd = observation.PhysicalPage, AnchorId = bound.Anchor.AnchorId
            };
        }).ToArray();
        await transaction.CommitAsync(ct);
        return new(new(inventory, view), references);
    }

    internal static async Task<AdvancedAnalysisTableObservationAnchor?> ResolveAnchorAsync(
        NpgsqlConnection connection, Guid tenantId, string anchorId,
        Dictionary<string, CanonicalTableRegionObservation> cache, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(anchorId) || anchorId.Length > 200)
            throw new AdvancedAnalysisToolException("table_observation_anchor_identity_invalid");
        var rows = await ReadRowsAsync(connection, tenantId, null, null, null, anchorId, ct);
        return rows.Length == 0 ? null : rows.Length == 1 ? await BindAsync(connection, tenantId, rows[0], cache, ct)
            : throw new AdvancedAnalysisToolException("table_observation_anchor_identity_invalid");
    }

    internal static AdvancedAnalysisTableObservationLineSource Source(
        AdvancedAnalysisTableObservationAnchor bound, AdvancedAnalysisTableObservationView? window = null)
    {
        var lineView = window?.Lines?.SingleOrDefault(line => line.LineId == bound.Line.LineId);
        var rawIndex = bound.Observation.Lines.OrderBy(line => line.RawOrdinal).Select(line => line.LineId)
            .ToList().IndexOf(bound.Line.LineId);
        return new(bound.Observation.ObservationId, bound.Observation.TableId, bound.Line.LineId,
            bound.Line.RawOrdinal, bound.Line.TextSha256, bound.Observation.RawObservationSha256,
            window?.View ?? "raw", lineView?.ViewOrdinal ?? rawIndex, window?.BandOrdinal,
            window?.ViewLineCount ?? bound.Observation.Lines.Count, window?.LineOffset ?? rawIndex,
            window?.LineLimit ?? 1, window is not null ? window.NextLineOffset
                : rawIndex + 1 < bound.Observation.Lines.Count ? rawIndex + 1 : null,
            bound.Observation.QualitySignals.ToArray());
    }

    private static async Task<AnchorRow[]> ReadRowsAsync(NpgsqlConnection connection, Guid tenantId,
        Guid? document, Guid? revision, string[]? lineIds, string? anchorId, CancellationToken ct)
    {
        if (lineIds is { Length: 0 }) return [];
        if (lineIds?.Length > 64) throw new AdvancedAnalysisToolException("table_observation_anchor_limit_exceeded");
        return (await connection.QueryAsync<AnchorRow>(new CommandDefinition("""
            SELECT d.doc_id AS "DocId",dr.revision_id AS "RevisionId",d.doc_name AS "FileName",d.doc_path AS "DocPath",
              encode(dr.source_hash,'hex') AS "SourceHash",sa.anchor_id AS "AnchorId",sa.projection_id AS "ProjectionId",
              sa.payload::text AS "Payload",encode(sa.content_hash,'hex') AS "ContentHash",
              encode(sa.manifest_hash,'hex') AS "ManifestHash",o.page_number AS "PageNumber",
              o.table_id AS "TableId",o.observation_id AS "ObservationId"
            FROM document_source_anchors sa
            JOIN document_revisions dr ON dr.tenant_id=sa.tenant_id AND dr.revision_id=sa.revision_id
            JOIN documents d ON d.tenant_id=dr.tenant_id AND d.doc_id=dr.doc_id
            JOIN document_revision_table_observations o ON o.tenant_id=sa.tenant_id AND o.revision_id=sa.revision_id
              AND o.doc_id=dr.doc_id AND o.source_hash=dr.source_hash
              AND o.observation_id=sa.payload #>> '{regions,0,tableRegionObservationId}'
            WHERE sa.tenant_id=@tenant AND sa.projection_type='table_region_observation'
              AND d.status<>'deleted' AND d.indexed_version>0
              AND (@document::uuid IS NULL OR d.doc_id=@document)
              AND (@revision::uuid IS NULL OR dr.revision_id=@revision)
              AND (@anchor::text IS NULL OR sa.anchor_id=@anchor)
              AND (@lines::text[] IS NULL OR sa.projection_id=ANY(@lines))
            ORDER BY sa.projection_id LIMIT 65;
            """, new { tenant = tenantId, document, revision, anchor = anchorId, lines = lineIds },
            cancellationToken: ct))).ToArray();
    }

    private static async Task<AdvancedAnalysisTableObservationAnchor> BindAsync(NpgsqlConnection connection,
        Guid tenantId, AnchorRow row, Dictionary<string, CanonicalTableRegionObservation> cache, CancellationToken ct)
    {
        if (row.Payload.Length > 65_536)
            throw new AdvancedAnalysisToolException("table_observation_anchor_payload_limit_exceeded");
        var anchor = CanonicalContractJson.Deserialize<SourceAnchor>(row.Payload);
        if (CanonicalContractJson.ComputeSha256(anchor) != row.ContentHash || anchor.AnchorId != row.AnchorId
            || anchor.DocumentId != row.DocId || anchor.RevisionId != row.RevisionId
            || !string.Equals(anchor.SourceSha256, row.SourceHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(anchor.ManifestSha256, row.ManifestHash, StringComparison.OrdinalIgnoreCase)
            || anchor.ProjectionType != CanonicalTableObservationAnchors.ProjectionType
            || anchor.Precision != CanonicalTableObservationAnchors.Precision || anchor.ProjectionId != row.ProjectionId
            || anchor.Regions.Count != 1)
            throw new AdvancedAnalysisToolException("table_observation_anchor_identity_invalid");
        var region = anchor.Regions[0];
        if (region.TableRegionObservationId != row.ObservationId || region.PageNumber != row.PageNumber
            || region.TableRegionObservationLineIds is not { Count: 1 } || region.BlockIds.Count != 0
            || region.SpanIds.Count != 0 || region.TableCellIds.Count != 0)
            throw new AdvancedAnalysisToolException("table_observation_anchor_identity_invalid");
        var key = CacheKey(row.DocId, row.RevisionId, row.SourceHash, row.PageNumber, row.TableId, row.ObservationId);
        if (!cache.TryGetValue(key, out var observation))
        {
            observation = await CanonicalTableObservationRepo.ReadAsync(connection, tenantId, row.DocId,
                row.RevisionId, row.SourceHash, row.PageNumber, row.TableId, row.ObservationId, ct)
                ?? throw new AdvancedAnalysisToolException("table_observation_unavailable");
            cache.Add(key, observation);
        }
        var line = observation.Lines.SingleOrDefault(line => line.LineId == region.TableRegionObservationLineIds[0]);
        if (line is null || row.ProjectionId != line.LineId
            || !string.Equals(line.TextSha256, region.RawTextSha256, StringComparison.OrdinalIgnoreCase)
            || anchor.AnchorId != CanonicalTableObservationAnchors.BuildAnchorId(observation, line, anchor.ManifestSha256)
            || CanonicalContractJson.SerializeCanonical(region.Polygon) != CanonicalContractJson.SerializeCanonical(line.Polygon))
            throw new AdvancedAnalysisToolException("table_observation_anchor_identity_invalid");
        return new(anchor, observation, line, row.FileName, row.DocPath);
    }

    private static string CacheKey(Guid document, Guid revision, string source, int page, string table, string observation)
        => string.Join("|", document.ToString("D"), revision.ToString("D"), source.ToLowerInvariant(), page, table, observation);

    private sealed record AnchorRow(Guid DocId, Guid RevisionId, string FileName, string DocPath, string SourceHash,
        string AnchorId, string ProjectionId, string Payload, string ContentHash, string ManifestHash,
        int PageNumber, string TableId, string ObservationId);
}
