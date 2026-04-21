using System.Text.Json;
using Dapper;
using Npgsql;

namespace SAAIA.Backend.Audit;

public static class AuditWriter
{
    public static async Task WriteAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        Guid? actorApiKeyId,
        bool actorIsAdmin,
        string action,
        string? target,
        object? payload,
        string? ip,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        const string sql = @"
INSERT INTO audit_events(audit_id, tenant_id, actor_api_key_id, actor_is_admin, action, target, payload_json, ip)
VALUES (@id, @tenant_id, @actor_api_key_id, @actor_is_admin, @action, @target, @payload_json, @ip);";

        var payloadJson = payload is null ? null : JsonSerializer.Serialize(payload);

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            id = Guid.NewGuid(),
            tenant_id = tenantId,
            actor_api_key_id = actorApiKeyId,
            actor_is_admin = actorIsAdmin,
            action,
            target,
            payload_json = payloadJson,
            ip
        }, transaction: tx, cancellationToken: ct));
    }
}
