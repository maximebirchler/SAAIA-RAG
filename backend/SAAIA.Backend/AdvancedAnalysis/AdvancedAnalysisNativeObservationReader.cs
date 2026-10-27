using System.Data;
using Dapper;
using Npgsql;
using SAAIA.Contracts;
using SAAIA.Contracts.DocumentIntelligence;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed record AdvancedAnalysisAlternativeNativeReadings(
    string DocId, string RevisionId, string SourceSha256, int PhysicalPage,
    AdvancedAnalysisNativeObservationInventory? Inventory, AdvancedAnalysisNativeObservationWindow? View);

internal sealed record AdvancedAnalysisNativeObservationRead(
    AdvancedAnalysisAlternativeNativeReadings Readings,
    IReadOnlyList<AdvancedAnalysisEvidenceReference> References);

internal sealed record AdvancedAnalysisNativeObservationLineSource(
    AdvancedAnalysisNativeObservationReference Reference, int ViewLineCount, int LineOffset,
    int LineLimit, int? NextLineOffset, IReadOnlyList<string> QualitySignals,
    string Role = "secondary_native_audit", string SemanticAccuracy = "not_verified",
    bool SourceFidelityApproved = false);

/// <summary>Reads only immutable captured canonical data. It never extracts, indexes or replaces primary text.</summary>
internal static class AdvancedAnalysisNativeObservationReader
{
    internal sealed class Cache
    {
        internal readonly Dictionary<(Guid Tenant, Guid Document, Guid Revision, string Source), StoredDocument> Documents = [];
        internal long UncompressedBytes;
    }

    internal sealed class StoredDocument(CanonicalDocument document, string fileName, string docPath)
    {
        internal CanonicalDocument Document { get; } = document;
        internal string FileName { get; } = fileName;
        internal string DocPath { get; } = docPath;
        private readonly Dictionary<(int Page, string Block, string RawSha), IReadOnlyList<AdvancedAnalysisNativeObservationLine>> lines = [];

        internal AdvancedAnalysisNativeObservationLine Resolve(AdvancedAnalysisNativeObservationReference reference, CancellationToken ct)
        {
            AdvancedAnalysisNativeObservationWindows.ValidateReference(reference);
            var key = (reference.PhysicalPage, reference.BlockId, reference.RawTextSha256.ToLowerInvariant());
            if (!lines.TryGetValue(key, out var captured))
            {
                captured = AdvancedAnalysisNativeObservationWindows.ObservedLines(Document,
                    key.PhysicalPage, key.BlockId, key.Item3, ct);
                lines.Add(key, captured);
            }
            return AdvancedAnalysisNativeObservationWindows.ValidateLine(reference, captured);
        }

        internal AdvancedAnalysisNativeObservationLineSource Source(AdvancedAnalysisNativeObservationLine line)
        {
            var reference = line.Source;
            var page = Document.Pages.Single(p => p.PageNumber == reference.PhysicalPage);
            var block = page.Blocks.Single(b => b.BlockId == reference.BlockId);
            var count = lines[(reference.PhysicalPage, reference.BlockId, reference.RawTextSha256.ToLowerInvariant())].Count;
            return new(reference, count, reference.LineOrdinal, 1,
                reference.LineOrdinal + 1 < count ? reference.LineOrdinal + 1 : null,
                page.QualityFlags.Concat(block.QualityFlags).Append("canonical_native_audit_literal_not_primary")
                    .Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    internal static bool HasRequest(AdvancedAnalysisSearchRequest request)
        => request.NativeObservationView is not null || request.NativeObservationBlockId is not null
            || request.NativeObservationRawSha256 is not null || request.NativeObservationOffset is not null
            || request.NativeObservationLimit is not null;

    internal static void ValidateRequest(AdvancedAnalysisSearchRequest request)
    {
        if (!HasRequest(request))
            return;
        if (request.Operation != "read_source" || request.PageStart != request.PageEnd || request.PageStart is not > 0
            || request.TableOrdinal is not null || request.TableRowOffset is not null || request.TableRowLimit is not null
            || request.TableObservationView is not null || request.TableObservationId is not null
            || request.TableObservationBandOrdinal is not null || request.TableObservationLineOffset is not null
            || request.TableObservationLineLimit is not null || request.NativeObservationView is not ("inventory" or "raw")
            || request.NativeObservationOffset is < 0 || request.NativeObservationLimit is < 1 or > 64)
            throw new AdvancedAnalysisToolException("native_observation_read_window_invalid");
        if (request.NativeObservationView == "inventory")
        {
            if (request.NativeObservationBlockId is not null || request.NativeObservationRawSha256 is not null)
                throw new AdvancedAnalysisToolException("native_observation_inventory_arguments_invalid");
        }
        else if (string.IsNullOrWhiteSpace(request.NativeObservationBlockId) || request.NativeObservationBlockId.Length > 200
            || request.NativeObservationRawSha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit))
            throw new AdvancedAnalysisToolException("native_observation_read_window_invalid");
    }

    internal static async Task<AdvancedAnalysisNativeObservationRead> ReadAsync(NpgsqlDataSource dataSource,
        Guid tenant, AdvancedAnalysisSearchRequest request, AdvancedAnalysisResultEvidence observed,
        int maximumCharacters, CancellationToken ct, int maximumLineCharacters = 200_000,
        int maximumTotalLineCharacters = 2_000_000)
    {
        ValidateRequest(request);
        if (!HasRequest(request))
            throw new AdvancedAnalysisToolException("native_observation_read_window_invalid");
        if (!Guid.TryParse(request.DocId, out var document) || !Guid.TryParse(request.RevisionId, out var revision)
            || !Guid.TryParse(observed.DocId, out var observedDocument) || observedDocument != document
            || !Guid.TryParse(observed.RevisionId, out var observedRevision) || observedRevision != revision
            || observed.SourceHash is not { Length: 64 } source || !source.All(char.IsAsciiHexDigit))
            throw new AdvancedAnalysisToolException("native_observation_source_identity_invalid");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await connection.ExecuteAsync(new CommandDefinition("SET TRANSACTION READ ONLY", transaction: transaction, cancellationToken: ct));
        var stored = await LoadAsync(connection, tenant, document, revision, source, new(), ct, transaction)
            ?? throw new AdvancedAnalysisToolException("native_observation_source_unavailable");
        if (!string.IsNullOrWhiteSpace(request.DocPath)
            && !string.Equals(request.DocPath.Replace('\\', '/'), stored.DocPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new AdvancedAnalysisToolException("native_observation_source_identity_invalid");
        AdvancedAnalysisNativeObservationInventory? inventory = null;
        AdvancedAnalysisNativeObservationWindow? view = null;
        if (request.NativeObservationView == "inventory")
            inventory = AdvancedAnalysisNativeObservationWindows.Inventory(stored.Document, request.PageStart!.Value,
                request.NativeObservationOffset ?? 0, request.NativeObservationLimit ?? 16, maximumCharacters, ct);
        else
            view = AdvancedAnalysisNativeObservationWindows.Raw(stored.Document, request.PageStart!.Value,
                request.NativeObservationBlockId!, request.NativeObservationRawSha256!, request.NativeObservationOffset ?? 0,
                request.NativeObservationLimit ?? 16, maximumCharacters, ct);
        if (view is not null)
            view = BoundToEvidenceLimits(view, maximumLineCharacters, maximumTotalLineCharacters);
        var readings = new AdvancedAnalysisAlternativeNativeReadings(document.ToString("D"), revision.ToString("D"),
            source, request.PageStart!.Value, inventory, view);
        // Reserve the outer identity wrapper too; whole lines/descriptors are the
        // only removable units. The provider separately measures its final wire.
        while (System.Text.Json.JsonSerializer.Serialize(readings, new System.Text.Json.JsonSerializerOptions(
                   System.Text.Json.JsonSerializerDefaults.Web)).Length > maximumCharacters)
        {
            if (view is { Lines.Count: > 0 })
                view = view with { Lines = view.Lines.Take(view.Lines.Count - 1).ToArray(),
                    NextOffset = view.Offset + view.Lines.Count - 1,
                    Status = view.Lines.Count == 1 ? "line_does_not_fit" : "partial_budget" };
            else if (inventory is { Observations.Count: > 0 })
                inventory = inventory with { Observations = inventory.Observations.Take(inventory.Observations.Count - 1).ToArray(),
                    NextOffset = inventory.Offset + inventory.Observations.Count - 1,
                    Reason = inventory.Observations.Count == 1 ? "descriptor_does_not_fit" : "partial_budget" };
            else
                throw new AdvancedAnalysisToolException("native_observation_budget_diagnostic_does_not_fit");
            readings = readings with { Inventory = inventory, View = view };
        }
        var references = (view?.Lines ?? []).Select(line => new AdvancedAnalysisEvidenceReference
        {
            DocId = document.ToString("D"), RevisionId = revision.ToString("D"), SourceHash = source,
            DocPath = stored.DocPath, FileName = stored.FileName, PageStart = request.PageStart!.Value,
            PageEnd = request.PageStart.Value, NativeObservation = line.Source
        }).ToArray();
        await transaction.CommitAsync(ct);
        return new(readings, references);
    }

    internal static AdvancedAnalysisNativeObservationWindow BoundToEvidenceLimits(
        AdvancedAnalysisNativeObservationWindow view, int maximumLineCharacters, int maximumTotalLineCharacters)
    {
        var total = 0;
        var accepted = new List<AdvancedAnalysisNativeObservationLine>();
        foreach (var line in view.Lines)
        {
            var reason = line.Text.Length > maximumLineCharacters ? "evidence_item_limit"
                : line.Text.Length > maximumTotalLineCharacters - total ? "evidence_total_limit" : null;
            if (reason is not null)
                return view with { Lines = accepted.ToArray(), NextOffset = view.Offset + accepted.Count,
                    Status = accepted.Count == 0 ? "line_does_not_fit_" + reason : "partial_" + reason };
            accepted.Add(line);
            total += line.Text.Length;
        }
        return view;
    }

    internal static async Task<StoredDocument?> LoadAsync(NpgsqlConnection connection, Guid tenant, Guid document,
        Guid revision, string source, Cache cache, CancellationToken ct, NpgsqlTransaction? transaction = null)
    {
        if (tenant == Guid.Empty || document == Guid.Empty || revision == Guid.Empty
            || source.Length != 64 || !source.All(char.IsAsciiHexDigit))
            throw new AdvancedAnalysisToolException("native_observation_source_identity_invalid");
        var key = (tenant, document, revision, source.ToLowerInvariant());
        if (cache.Documents.TryGetValue(key, out var captured))
            return captured;
        if (cache.Documents.Count >= 64)
            throw new AdvancedAnalysisToolException("native_observation_cache_budget_exceeded");
        var row = await connection.QuerySingleOrDefaultAsync<ArtifactRow>(new CommandDefinition("""
            SELECT d.doc_name AS "FileName",d.doc_path AS "DocPath",a.artifact_type AS "ArtifactType",
              a.schema_version AS "SchemaVersion",a.content_hash AS "ContentHash",a.byte_size AS "ByteSize",a.payload AS "Payload"
            FROM document_revision_binary_artifacts a
            JOIN document_revisions dr ON dr.tenant_id=a.tenant_id AND dr.revision_id=a.revision_id
            JOIN documents d ON d.tenant_id=dr.tenant_id AND d.doc_id=dr.doc_id
            WHERE a.tenant_id=@tenant AND dr.doc_id=@document AND a.revision_id=@revision AND dr.source_hash=@source
              AND d.indexed_version>0 AND d.status<>'deleted' AND a.artifact_type='canonical_ingestion_bundle'
              AND a.compression='gzip' AND a.byte_size BETWEEN 1 AND 134217728
              AND a.stored_size BETWEEN 1 AND 33554432 AND octet_length(a.payload)<=33554432;
            """, new { tenant, document, revision, source = Convert.FromHexString(source) }, transaction, cancellationToken: ct));
        if (row is null)
            return null;
        if (row.ByteSize > 134_217_728 - cache.UncompressedBytes)
            throw new AdvancedAnalysisToolException("native_observation_cache_budget_exceeded");
        ct.ThrowIfCancellationRequested();
        CanonicalIngestionBundle bundle;
        try
        {
            // The artifact reader checks gzip size, SHA and the full canonical
            // bundle once. Never expose validator messages containing raw text.
            bundle = CanonicalArtifactRepo.ReadBundleArtifact(new(row.ArtifactType, row.SchemaVersion,
                row.ContentHash, row.ByteSize, row.Payload), 134_217_728);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException
            or CanonicalContractException or ArgumentException)
        {
            throw new AdvancedAnalysisToolException("native_observation_bundle_integrity_invalid");
        }
        var canonical = bundle.Document;
        if (canonical.DocumentId != document || canonical.RevisionId != revision
            || !string.Equals(canonical.Source.Sha256, source, StringComparison.OrdinalIgnoreCase))
            throw new AdvancedAnalysisToolException("native_observation_source_identity_invalid");
        ct.ThrowIfCancellationRequested();
        captured = new(canonical, row.FileName, row.DocPath);
        cache.Documents.Add(key, captured);
        cache.UncompressedBytes += row.ByteSize;
        return captured;
    }

    private sealed record ArtifactRow(string FileName, string DocPath, string ArtifactType, string SchemaVersion,
        byte[] ContentHash, long ByteSize, byte[] Payload);
}
