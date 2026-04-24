using System.Runtime.InteropServices;
using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeGovernanceService
{
    private const string AdminRuntimeActor = "admin_runtime_endpoint";
    private sealed record CapabilityBDocTextRow(Guid DocId, string? Value, int Ordinal);

    internal static async Task<string[]> LoadCapabilityBSectionTitlesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        int indexedVersion,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<string>(new CommandDefinition(
            """
SELECT ds.title
FROM document_revisions dr
JOIN document_sections ds ON ds.revision_id = dr.revision_id
WHERE dr.tenant_id=@tenant
  AND dr.doc_id=@docId
  AND dr.indexed_version=@indexedVersion
ORDER BY ds.ordinal
LIMIT @limit;
""",
            new
            {
                tenant = tenantId,
                docId,
                indexedVersion,
                limit = Math.Clamp(limit, 1, 20)
            },
            cancellationToken: ct)))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title.Trim())
            .ToArray();

    internal static async Task<IReadOnlyDictionary<Guid, string[]>> LoadCapabilityBSectionTitlesBatchAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        IReadOnlyList<(Guid DocId, int IndexedVersion)> docs,
        int limit,
        CancellationToken ct)
    {
        if (docs.Count == 0)
            return new Dictionary<Guid, string[]>();

        var clampedLimit = Math.Clamp(limit, 1, 20);
        var docIds = docs.Select(static x => x.DocId).ToArray();
        var indexedVersions = docs.Select(static x => x.IndexedVersion).ToArray();

        var rows = await conn.QueryAsync<CapabilityBDocTextRow>(new CommandDefinition(
            """
SELECT dr.doc_id AS "DocId",
       ds.title AS "Value",
       ds.ordinal AS "Ordinal"
FROM unnest(@docIds::uuid[], @indexedVersions::integer[]) AS input(doc_id, indexed_version)
JOIN document_revisions dr
  ON dr.tenant_id=@tenant
 AND dr.doc_id=input.doc_id
 AND dr.indexed_version=input.indexed_version
JOIN LATERAL (
    SELECT title, ordinal
    FROM document_sections
    WHERE revision_id = dr.revision_id
    ORDER BY ordinal
    LIMIT @limit
) ds ON TRUE;
""",
            new
            {
                tenant = tenantId,
                docIds,
                indexedVersions,
                limit = clampedLimit
            },
            cancellationToken: ct));

        return rows
            .Where(static row => !string.IsNullOrWhiteSpace(row.Value))
            .GroupBy(static row => row.DocId)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static row => row.Ordinal)
                    .Select(static row => row.Value!.Trim())
                    .ToArray());
    }

    internal static async Task<string[]> LoadCapabilityBUnitExcerptsAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        int indexedVersion,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<string>(new CommandDefinition(
            """
SELECT du.text_content
FROM document_revisions dr
JOIN document_units du ON du.revision_id = dr.revision_id
WHERE dr.tenant_id=@tenant
  AND dr.doc_id=@docId
  AND dr.indexed_version=@indexedVersion
ORDER BY du.ordinal
LIMIT @limit;
""",
            new
            {
                tenant = tenantId,
                docId,
                indexedVersion,
                limit = Math.Clamp(limit, 1, 20)
            },
            cancellationToken: ct)))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim())
            .ToArray();

    internal static async Task<IReadOnlyDictionary<Guid, string[]>> LoadCapabilityBUnitExcerptsBatchAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        IReadOnlyList<(Guid DocId, int IndexedVersion)> docs,
        int limit,
        CancellationToken ct)
    {
        if (docs.Count == 0)
            return new Dictionary<Guid, string[]>();

        var clampedLimit = Math.Clamp(limit, 1, 20);
        var docIds = docs.Select(static x => x.DocId).ToArray();
        var indexedVersions = docs.Select(static x => x.IndexedVersion).ToArray();

        var rows = await conn.QueryAsync<CapabilityBDocTextRow>(new CommandDefinition(
            """
SELECT dr.doc_id AS "DocId",
       du.text_content AS "Value",
       du.ordinal AS "Ordinal"
FROM unnest(@docIds::uuid[], @indexedVersions::integer[]) AS input(doc_id, indexed_version)
JOIN document_revisions dr
  ON dr.tenant_id=@tenant
 AND dr.doc_id=input.doc_id
 AND dr.indexed_version=input.indexed_version
JOIN LATERAL (
    SELECT text_content, ordinal
    FROM document_units
    WHERE revision_id = dr.revision_id
    ORDER BY ordinal
    LIMIT @limit
) du ON TRUE;
""",
            new
            {
                tenant = tenantId,
                docIds,
                indexedVersions,
                limit = clampedLimit
            },
            cancellationToken: ct));

        return rows
            .Where(static row => !string.IsNullOrWhiteSpace(row.Value))
            .GroupBy(static row => row.DocId)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static row => row.Ordinal)
                    .Select(static row => row.Value!.Trim())
                    .ToArray());
    }

    internal static AdminRuntimeCapabilityEventDto CreateCapabilityEvent(
        string capabilityKey,
        string? profileKey,
        string eventType,
        string? reason,
        IReadOnlyDictionary<string, object?>? details = null)
        => new(
            EventId: Guid.NewGuid(),
            CapabilityKey: capabilityKey,
            ProfileKey: profileKey,
            EventType: eventType,
            Actor: AdminRuntimeActor,
            Reason: reason,
            OccurredAt: DateTimeOffset.UtcNow,
            Details: details);

    internal static IReadOnlyDictionary<string, object?> BuildRuntimeEnvironmentSnapshot()
        => new Dictionary<string, object?>
        {
            ["machineName"] = Environment.MachineName,
            ["osVersion"] = Environment.OSVersion.VersionString,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["frameworkDescription"] = RuntimeInformation.FrameworkDescription,
            ["processorCount"] = Environment.ProcessorCount,
            ["is64BitProcess"] = Environment.Is64BitProcess
        };

    internal static IReadOnlyList<string> BuildCapabilityAHypotheticalQuestions(
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
        => RuntimeCapabilityAEnrichmentStore.BuildHypotheticalQuestions(
            docName,
            sectionTitles,
            excerpts);

    internal static Task<IReadOnlyDictionary<Guid, AdminRuntimeCapabilityBBackofficeCandidateDto>> LoadCapabilityBBackofficeCandidateLookupAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? categoryPath,
        RuntimeGovernanceOptions options,
        CancellationToken ct)
        => RuntimeCapabilityBBackofficeStore.LoadCandidateLookupAsync(
            conn,
            tenantId,
            categoryPath,
            options,
            ct);

}
