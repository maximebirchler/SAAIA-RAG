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
    public string ApiKeyPlain { get; set; } = "";
}

public static class Bootstrapper
{
    public static async Task EnsureBootstrapAsync(NpgsqlDataSource ds, IOptions<BootstrapOptions> opt, CancellationToken ct)
    {
        if (!opt.Value.Enabled) return;
        if (opt.Value.TenantId == Guid.Empty) return;
        if (string.IsNullOrWhiteSpace(opt.Value.ApiKeyPlain)) return;

        await using var conn = await ds.OpenConnectionAsync(ct);

        var tenantsCount = await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM tenants", cancellationToken: ct));
        if (tenantsCount > 0) return;

        var tenantId = opt.Value.TenantId;
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO tenants(tenant_id, name) VALUES(@id, @name)",
            new { id = tenantId, name = opt.Value.TenantName },
            cancellationToken: ct
        ));

        var hash = ApiKeyAuth.Sha256Bytes(opt.Value.ApiKeyPlain);
        var prefix = ApiKeyAuth.Prefix(opt.Value.ApiKeyPlain);

        await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO api_keys(api_key_id, tenant_id, key_prefix, key_hash)
VALUES(@kid, @tid, @prefix, @hash);",
            new { kid = Guid.NewGuid(), tid = tenantId, prefix, hash },
            cancellationToken: ct
        ));
    }
}
