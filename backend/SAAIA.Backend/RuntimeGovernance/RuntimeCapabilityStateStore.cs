using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityStateStore
{
    internal static async Task<Dictionary<string, AdminRuntimeCapabilityStateDto>> LoadPersistedStatesAsync(
        NpgsqlConnection conn,
        CancellationToken ct)
        => (await conn.QueryAsync<CapabilityStateRow>(new CommandDefinition(
            """
SELECT
  capability_key AS "CapabilityKey",
  capability_family AS "CapabilityFamily",
  display_name AS "DisplayName",
  runtime_key AS "RuntimeKey",
  profile_key AS "ProfileKey",
  implemented AS "Implemented",
  desired_enabled AS "DesiredEnabled",
  installed AS "Installed",
  configured AS "Configured",
  healthy AS "Healthy",
  qualified AS "Qualified",
  authorized AS "Authorized",
  selected AS "Selected",
  pass_count AS "PassCount",
  last_checked_at AS "LastCheckedAt",
  last_qualified_at AS "LastQualifiedAt",
  last_error AS "LastError",
  details AS "DetailsJson"
FROM runtime_capability_state
ORDER BY capability_key;
""", cancellationToken: ct)))
            .Select(MapStateRow)
            .ToDictionary(static row => row.Key, StringComparer.Ordinal);

    internal static async Task UpsertAsync(
        NpgsqlConnection conn,
        AdminRuntimeCapabilityStateDto state,
        CancellationToken ct)
    {
        const string sql = """
INSERT INTO runtime_capability_state(
  capability_key,
  capability_family,
  display_name,
  runtime_key,
  profile_key,
  implemented,
  desired_enabled,
  installed,
  configured,
  healthy,
  qualified,
  authorized,
  selected,
  pass_count,
  last_checked_at,
  last_qualified_at,
  last_error,
  details)
VALUES(
  @capability_key,
  @capability_family,
  @display_name,
  @runtime_key,
  @profile_key,
  @implemented,
  @desired_enabled,
  @installed,
  @configured,
  @healthy,
  @qualified,
  @authorized,
  @selected,
  @pass_count,
  @last_checked_at,
  @last_qualified_at,
  @last_error,
  CAST(@details AS jsonb))
ON CONFLICT (capability_key) DO UPDATE SET
  capability_family = EXCLUDED.capability_family,
  display_name = EXCLUDED.display_name,
  runtime_key = EXCLUDED.runtime_key,
  profile_key = EXCLUDED.profile_key,
  implemented = EXCLUDED.implemented,
  desired_enabled = EXCLUDED.desired_enabled,
  installed = EXCLUDED.installed,
  configured = EXCLUDED.configured,
  healthy = EXCLUDED.healthy,
  qualified = EXCLUDED.qualified,
  authorized = EXCLUDED.authorized,
  selected = EXCLUDED.selected,
  pass_count = EXCLUDED.pass_count,
  last_checked_at = EXCLUDED.last_checked_at,
  last_qualified_at = EXCLUDED.last_qualified_at,
  last_error = EXCLUDED.last_error,
  details = EXCLUDED.details,
  updated_at = now();
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            capability_key = state.Key,
            capability_family = state.Family,
            display_name = state.DisplayName,
            runtime_key = state.RuntimeKey,
            profile_key = state.ProfileKey,
            implemented = state.Implemented,
            desired_enabled = state.DesiredEnabled,
            installed = state.Installed,
            configured = state.Configured,
            healthy = state.Healthy,
            qualified = state.Qualified,
            authorized = state.Authorized,
            selected = state.Selected,
            pass_count = state.PassCount,
            last_checked_at = state.LastCheckedAt?.UtcDateTime,
            last_qualified_at = state.LastQualifiedAt?.UtcDateTime,
            last_error = state.LastError,
            details = JsonSerializer.Serialize(state.Details ?? new Dictionary<string, object?>())
        }, cancellationToken: ct));
    }

    private static AdminRuntimeCapabilityStateDto MapStateRow(CapabilityStateRow row)
    {
        var details = RuntimeGovernanceJson.ParseDetails(row.DetailsJson);
        return new AdminRuntimeCapabilityStateDto(
            Key: row.CapabilityKey,
            DisplayName: row.DisplayName,
            Family: row.CapabilityFamily,
            RuntimeKey: row.RuntimeKey,
            Implemented: row.Implemented,
            DesiredEnabled: row.DesiredEnabled,
            Installed: row.Installed,
            Configured: row.Configured,
            Healthy: row.Healthy,
            Qualified: row.Qualified,
            Authorized: row.Authorized,
            Selected: row.Selected,
            ProfileKey: row.ProfileKey,
            PassCount: row.PassCount,
            LastCheckedAt: ToUtcOffset(row.LastCheckedAt),
            LastQualifiedAt: ToUtcOffset(row.LastQualifiedAt),
            LastError: row.LastError,
            Details: details,
            Stale: false,
            QualificationFingerprint: RuntimeCapabilityStateProjector.ResolveQualificationFingerprint(details),
            StaleReason: null,
            QualificationAgeHours: null,
            QualificationExpiresAt: null,
            PersistedAuthorized: row.Authorized,
            PersistedSelected: row.Selected,
            EffectiveAuthorized: row.Authorized,
            EffectiveSelected: row.Selected);
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        var utc = value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
        return new DateTimeOffset(utc);
    }

    private sealed record CapabilityStateRow(
        string CapabilityKey,
        string CapabilityFamily,
        string DisplayName,
        string RuntimeKey,
        string ProfileKey,
        bool Implemented,
        bool DesiredEnabled,
        bool Installed,
        bool Configured,
        bool Healthy,
        bool Qualified,
        bool Authorized,
        bool Selected,
        int PassCount,
        DateTime? LastCheckedAt,
        DateTime? LastQualifiedAt,
        string? LastError,
        string? DetailsJson);
}
