using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SAAIA.Backend.Bootstrap;

namespace SAAIA.Backend.Tenancy;

public static class TenantResolver
{
    /// <summary>
    /// Résout un tenant "valide" (existant en DB). Empêche les FK violations si Bootstrap:TenantId
    /// pointe vers un GUID qui n'existe pas.
    /// </summary>
    public static async Task<Guid> ResolveSingleTenantIdAsync(
        NpgsqlDataSource ds,
        BootstrapOptions bootstrap,
        ILogger log,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        // 1) Si un TenantId est fourni en config, on ne l'accepte QUE s'il existe vraiment
        if (bootstrap.TenantId != Guid.Empty)
        {
            var exists = await conn.ExecuteScalarAsync<int?>(
                new CommandDefinition(
                    "SELECT 1 FROM tenants WHERE tenant_id=@tid AND is_active=true LIMIT 1;",
                    new { tid = bootstrap.TenantId },
                    cancellationToken: ct));

            if (exists.HasValue)
                return bootstrap.TenantId;

            // DB vide ? => message actionnable (bootstrap / create tenant)
            var anyTenant = await conn.ExecuteScalarAsync<int?>(
                new CommandDefinition(
                    "SELECT 1 FROM tenants LIMIT 1;",
                    cancellationToken: ct));

            if (!anyTenant.HasValue)
            {
                throw new InvalidOperationException(
                    "No tenant exists in DB. For first install, set Bootstrap:Enabled=true, Bootstrap:TenantId=<guid> and Bootstrap:ApiKeyPlain=<key>, then start once to bootstrap.");
            }

            // Sinon : tenant config invalide => fallback safe
            log.LogWarning(
                "Bootstrap TenantId {TenantId} not found in DB; falling back to first active tenant.",
                bootstrap.TenantId);
        }

        // 2) Fallback : premier tenant actif
        var tid = await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(
                "SELECT tenant_id FROM tenants WHERE is_active=true ORDER BY created_at ASC LIMIT 1;",
                cancellationToken: ct));

        if (tid is null || tid == Guid.Empty)
            throw new InvalidOperationException("No active tenant found. Create a tenant or enable Bootstrap.");

        return tid.Value;
    }
}
