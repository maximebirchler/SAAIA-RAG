using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Bootstrap;

public sealed class BootstrapOptions
{
    public bool Enabled { get; set; } = false;
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = "Default";

    /// <summary>
    /// Clé initiale (affichée/connue au déploiement). Doit être stockée uniquement en local.
    /// Elle sera créée comme ADMIN pour permettre la gestion des clés.
    /// </summary>
    public string ApiKeyPlain { get; set; } = "";

    /// <summary>Label stocké en DB pour la clé bootstrap.</summary>
    public string ApiKeyLabel { get; set; } = "bootstrap";
}

public static class Bootstrapper
{
    public static async Task EnsureBootstrapAsync(
        NpgsqlDataSource ds,
        IOptions<BootstrapOptions> bootOpt,
        IOptions<ApiKeyAuthOptions> authOpt,
        CancellationToken ct)
    {
        var opt = bootOpt.Value;
        if (!opt.Enabled) return;
        if (opt.TenantId == Guid.Empty) return;
        if (string.IsNullOrWhiteSpace(opt.ApiKeyPlain)) return;

        await using var conn = await ds.OpenConnectionAsync(ct);

        var tenantsCount = await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM tenants", cancellationToken: ct));
        if (tenantsCount > 0) return;

        var tenantId = opt.TenantId;
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO tenants(tenant_id, name) VALUES(@id, @name)",
            new { id = tenantId, name = opt.TenantName },
            cancellationToken: ct
        ));

        var prefix = ApiKeyAuth.Prefix(opt.ApiKeyPlain);
        var hash = ApiKeyAuth.Sha256Bytes(opt.ApiKeyPlain, authOpt.Value.Pepper);

        await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO api_keys(api_key_id, tenant_id, key_prefix, key_hash, label, is_admin, created_at, revoked_at, last_used_at)
VALUES(@kid, @tid, @prefix, @hash, @label, TRUE, now(), NULL, NULL);",
            new
            {
                kid = Guid.NewGuid(),
                tid = tenantId,
                prefix,
                hash,
                label = string.IsNullOrWhiteSpace(opt.ApiKeyLabel) ? "bootstrap" : opt.ApiKeyLabel.Trim()
            },
            cancellationToken: ct
        ));
    }
}
