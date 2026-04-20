using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityPersistenceStore
{
    internal static Task UpsertCapabilityStateAsync(
        NpgsqlConnection conn,
        AdminRuntimeCapabilityStateDto state,
        CancellationToken ct)
        => RuntimeCapabilityStateStore.UpsertAsync(conn, state, ct);

    internal static async Task InsertWarmupResultAsync(
        NpgsqlConnection conn,
        AdminRuntimeWarmupResultDto result,
        CancellationToken ct)
    {
        const string sql = """
INSERT INTO runtime_warmup_results(
  warmup_result_id,
  capability_key,
  profile_key,
  pass_count,
  passed,
  measured_at,
  details)
VALUES(
  @warmup_result_id,
  @capability_key,
  @profile_key,
  @pass_count,
  @passed,
  @measured_at,
  CAST(@details AS jsonb));
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            warmup_result_id = result.WarmupResultId,
            capability_key = result.CapabilityKey,
            profile_key = result.ProfileKey,
            pass_count = result.PassCount,
            passed = result.Passed,
            measured_at = result.MeasuredAt.UtcDateTime,
            details = JsonSerializer.Serialize(result.Details ?? new Dictionary<string, object?>())
        }, cancellationToken: ct));
    }

    internal static async Task InsertCapabilityEventAsync(
        NpgsqlConnection conn,
        AdminRuntimeCapabilityEventDto evt,
        CancellationToken ct)
    {
        const string sql = """
INSERT INTO runtime_capability_events(
  event_id,
  capability_key,
  profile_key,
  event_type,
  actor,
  reason,
  details,
  occurred_at)
VALUES(
  @event_id,
  @capability_key,
  @profile_key,
  @event_type,
  @actor,
  @reason,
  CAST(@details AS jsonb),
  @occurred_at);
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            event_id = evt.EventId,
            capability_key = evt.CapabilityKey,
            profile_key = evt.ProfileKey,
            event_type = evt.EventType,
            actor = evt.Actor,
            reason = evt.Reason,
            details = JsonSerializer.Serialize(evt.Details ?? new Dictionary<string, object?>()),
            occurred_at = evt.OccurredAt.UtcDateTime
        }, cancellationToken: ct));

        RuntimeGovernanceTelemetry.RecordCapabilityEventWritten(evt.CapabilityKey, evt.EventType);
    }

    internal static async Task PersistEvaluationAsync(
        NpgsqlConnection conn,
        AdminRuntimeCapabilityStateDto state,
        AdminRuntimeWarmupResultDto? warmupResult,
        AdminRuntimeCapabilityEventDto evt,
        CancellationToken ct)
    {
        await UpsertCapabilityStateAsync(conn, state, ct);
        if (warmupResult is not null)
            await InsertWarmupResultAsync(conn, warmupResult, ct);
        await InsertCapabilityEventAsync(conn, evt, ct);
    }

    internal static async Task PersistStateWithEventAsync(
        NpgsqlConnection conn,
        AdminRuntimeCapabilityStateDto state,
        AdminRuntimeCapabilityEventDto evt,
        CancellationToken ct)
    {
        await UpsertCapabilityStateAsync(conn, state, ct);
        await InsertCapabilityEventAsync(conn, evt, ct);
    }
}
