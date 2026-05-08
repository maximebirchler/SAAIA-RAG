using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityHistoryStore
{
    internal static async Task<AdminRuntimeWarmupResultDto[]> LoadWarmupResultsAsync(
        NpgsqlConnection conn,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<WarmupResultRow>(new CommandDefinition(
            """
SELECT
  warmup_result_id AS "WarmupResultId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  pass_count AS "PassCount",
  passed AS "Passed",
  measured_at AS "MeasuredAt",
  details AS "DetailsJson"
FROM runtime_warmup_results
WHERE (@capability_key IS NULL OR capability_key = @capability_key)
ORDER BY measured_at DESC
LIMIT @limit;
""",
            new { capability_key = string.IsNullOrWhiteSpace(capabilityKey) ? null : capabilityKey.Trim(), limit },
            cancellationToken: ct)))
            .Select(MapWarmupResultRow)
            .ToArray();

    internal static async Task<AdminRuntimeCapabilityEventDto[]> LoadCapabilityEventsAsync(
        NpgsqlConnection conn,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<CapabilityEventRow>(new CommandDefinition(
            """
SELECT
  event_id AS "EventId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  event_type AS "EventType",
  actor AS "Actor",
  reason AS "Reason",
  occurred_at AS "OccurredAt",
  details AS "DetailsJson"
FROM runtime_capability_events
WHERE (@capability_key IS NULL OR capability_key = @capability_key)
ORDER BY occurred_at DESC
LIMIT @limit;
""",
            new { capability_key = string.IsNullOrWhiteSpace(capabilityKey) ? null : capabilityKey.Trim(), limit },
            cancellationToken: ct)))
            .Select(MapCapabilityEventRow)
            .ToArray();

    private static AdminRuntimeWarmupResultDto MapWarmupResultRow(WarmupResultRow row)
        => new(
            row.WarmupResultId,
            row.CapabilityKey,
            row.ProfileKey,
            row.PassCount,
            row.Passed,
            ToUtcOffset(row.MeasuredAt),
            RuntimeGovernanceJson.ParseDetails(row.DetailsJson));

    private static AdminRuntimeCapabilityEventDto MapCapabilityEventRow(CapabilityEventRow row)
        => new(
            row.EventId,
            row.CapabilityKey,
            row.ProfileKey,
            row.EventType,
            row.Actor,
            row.Reason,
            ToUtcOffset(row.OccurredAt),
            RuntimeGovernanceJson.ParseDetails(row.DetailsJson));

    private static DateTimeOffset ToUtcOffset(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utc);
    }

    private sealed record WarmupResultRow(
        Guid WarmupResultId,
        string CapabilityKey,
        string ProfileKey,
        int PassCount,
        bool Passed,
        DateTime MeasuredAt,
        string? DetailsJson);

    private sealed record CapabilityEventRow(
        Guid EventId,
        string CapabilityKey,
        string? ProfileKey,
        string EventType,
        string Actor,
        string? Reason,
        DateTime OccurredAt,
        string? DetailsJson);
}
