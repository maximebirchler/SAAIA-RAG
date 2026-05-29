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
WITH revision AS (
  SELECT revision_id
  FROM document_revisions
  WHERE tenant_id=@tenant
    AND doc_id=@docId
    AND indexed_version=@indexedVersion
),
has_chunks AS (
  SELECT EXISTS(
    SELECT 1
    FROM retrieval_chunks rc
    JOIN revision r ON r.revision_id = rc.revision_id
  ) AS value
),
ranked_chunks AS (
  SELECT
    rc.text_content,
    rc.chunk_index AS ordinal,
    row_number() OVER (
      ORDER BY
        CASE COALESCE(rc.metadata->>'contentRole', 'content')
          WHEN 'content' THEN 0
          WHEN 'mixed_navigation_content' THEN 1
          ELSE 2
        END,
        CASE WHEN (rc.metadata->>'navigationScore') ~ '^-?[0-9]{1,3}([.][0-9]{1,12})?$' THEN (rc.metadata->>'navigationScore')::double precision ELSE 0 END ASC,
        CASE WHEN (rc.metadata->>'contentDensityScore') ~ '^-?[0-9]{1,3}([.][0-9]{1,12})?$' THEN (rc.metadata->>'contentDensityScore')::double precision ELSE 0 END DESC,
        rc.chunk_index ASC
    ) AS quality_rank
  FROM revision r
  JOIN retrieval_chunks rc ON rc.revision_id = r.revision_id
  WHERE length(trim(rc.text_content)) > 0
    AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
    AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
),
selected AS (
  SELECT text_content, ordinal
  FROM ranked_chunks
  WHERE quality_rank <= @limit
  UNION ALL
  SELECT du.text_content, du.ordinal
  FROM revision r
  JOIN document_units du ON du.revision_id = r.revision_id
  CROSS JOIN has_chunks hc
  WHERE NOT hc.value
    AND length(trim(du.text_content)) > 0
  ORDER BY ordinal
  LIMIT @limit
)
SELECT text_content
FROM selected
ORDER BY ordinal;
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

    internal static async Task<string[]> LoadCapabilityBRepresentativeUnitExcerptsAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        int indexedVersion,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<string>(new CommandDefinition(
            """
WITH revision AS (
  SELECT revision_id
  FROM document_revisions
  WHERE tenant_id=@tenant
    AND doc_id=@docId
    AND indexed_version=@indexedVersion
),
source_candidates AS (
  SELECT
    rc.text_content,
    rc.chunk_index AS ordinal,
    0 AS source_rank,
    CASE COALESCE(rc.metadata->>'contentRole', 'content')
      WHEN 'content' THEN 0
      WHEN 'mixed_navigation_content' THEN 1
      ELSE 2
    END AS role_rank,
    CASE WHEN (rc.metadata->>'navigationScore') ~ '^-?[0-9]{1,3}([.][0-9]{1,12})?$' THEN (rc.metadata->>'navigationScore')::double precision ELSE 0 END AS navigation_score,
    CASE WHEN (rc.metadata->>'contentDensityScore') ~ '^-?[0-9]{1,3}([.][0-9]{1,12})?$' THEN (rc.metadata->>'contentDensityScore')::double precision ELSE 0 END AS content_density_score
  FROM revision r
  JOIN retrieval_chunks rc ON rc.revision_id = r.revision_id
  WHERE length(trim(rc.text_content)) > 0
    AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
    AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
  UNION ALL
  SELECT
    du.text_content,
    du.ordinal,
    1 AS source_rank,
    0 AS role_rank,
    0::double precision AS navigation_score,
    0::double precision AS content_density_score
  FROM revision r
  JOIN document_units du ON du.revision_id = r.revision_id
  WHERE length(trim(du.text_content)) > 0
    AND NOT EXISTS (
      SELECT 1
      FROM retrieval_chunks rc_any
      WHERE rc_any.revision_id = r.revision_id
    )
),
ordered_units AS (
  SELECT
    text_content,
    ordinal,
    source_rank,
    role_rank,
    navigation_score,
    content_density_score,
    row_number() OVER (ORDER BY ordinal) AS rn,
    count(*) OVER () AS total
  FROM source_candidates
),
scored_units AS (
  SELECT
    text_content,
    ordinal,
    source_rank,
    role_rank,
    navigation_score,
    content_density_score,
    rn,
    total,
    CASE
      WHEN text_content ~ '\.{8,}' THEN 1
      WHEN text_content ~* '(^|[[:space:]])(r.f.rence|reference|referencia|referenz|riferiment|refer.ncia)' THEN 1
      WHEN text_content ~* '(https?://|www\.)' THEN 1
      WHEN text_content ~* '(r.daction|remerciements|acknowledg|copyright|isbn)' THEN 1
      ELSE 0
    END AS low_value_rank
  FROM ordered_units
),
bucketed AS (
  SELECT
    text_content,
    ordinal,
    source_rank,
    role_rank,
    navigation_score,
    content_density_score,
    floor(((rn - 1)::numeric * @limit) / greatest(total, 1))::integer AS bucket,
    length(text_content) AS char_count,
    low_value_rank
  FROM scored_units
),
ranked AS (
  SELECT
    text_content,
    ordinal,
    row_number() OVER (
      PARTITION BY bucket
      ORDER BY source_rank ASC, role_rank ASC, low_value_rank ASC, navigation_score ASC, content_density_score DESC, char_count DESC, ordinal ASC
    ) AS bucket_rank
  FROM bucketed
)
SELECT text_content
FROM ranked
WHERE bucket_rank = 1
ORDER BY ordinal
LIMIT @limit;
""",
            new
            {
                tenant = tenantId,
                docId,
                indexedVersion,
                limit = Math.Clamp(limit, 1, 30)
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
       candidate.text_content AS "Value",
       candidate.ordinal AS "Ordinal"
FROM unnest(@docIds::uuid[], @indexedVersions::integer[]) AS input(doc_id, indexed_version)
JOIN document_revisions dr
  ON dr.tenant_id=@tenant
 AND dr.doc_id=input.doc_id
 AND dr.indexed_version=input.indexed_version
JOIN LATERAL (
    SELECT text_content, ordinal
    FROM (
      SELECT
        rc.text_content,
        rc.chunk_index AS ordinal,
        0 AS source_rank,
        CASE COALESCE(rc.metadata->>'contentRole', 'content')
          WHEN 'content' THEN 0
          WHEN 'mixed_navigation_content' THEN 1
          ELSE 2
        END AS role_rank,
        CASE WHEN (rc.metadata->>'navigationScore') ~ '^-?[0-9]{1,3}([.][0-9]{1,12})?$' THEN (rc.metadata->>'navigationScore')::double precision ELSE 0 END AS navigation_score,
        CASE WHEN (rc.metadata->>'contentDensityScore') ~ '^-?[0-9]{1,3}([.][0-9]{1,12})?$' THEN (rc.metadata->>'contentDensityScore')::double precision ELSE 0 END AS content_density_score
      FROM retrieval_chunks rc
      WHERE rc.revision_id = dr.revision_id
        AND length(trim(rc.text_content)) > 0
        AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
        AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
      UNION ALL
      SELECT
        du.text_content,
        du.ordinal,
        1 AS source_rank,
        0 AS role_rank,
        0::double precision AS navigation_score,
        0::double precision AS content_density_score
      FROM document_units du
      WHERE du.revision_id = dr.revision_id
        AND length(trim(du.text_content)) > 0
        AND NOT EXISTS (
          SELECT 1
          FROM retrieval_chunks rc_any
          WHERE rc_any.revision_id = dr.revision_id
        )
    ) candidates
    ORDER BY source_rank, role_rank, navigation_score ASC, content_density_score DESC, ordinal ASC
    LIMIT @limit
) candidate ON TRUE;
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
