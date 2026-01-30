using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace SAAIA.Backend.Auth;

public static class ApiKeyAuth
{
    public const string TenantIdItemKey = "tenant_id";

    public static byte[] Sha256Bytes(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        return SHA256.HashData(bytes);
    }

    public static string Prefix(string key)
        => key.Length <= 8 ? key : key.Substring(0, 8);

    public static async Task<Guid?> ResolveTenantIdAsync(NpgsqlDataSource ds, string apiKey, CancellationToken ct)
    {
        var hash = Sha256Bytes(apiKey);
        var prefix = Prefix(apiKey);

        const string sql = @"
SELECT tenant_id
FROM api_keys
WHERE revoked_at IS NULL
  AND key_prefix = @prefix
  AND key_hash = @hash
LIMIT 1;";

        await using var conn = await ds.OpenConnectionAsync(ct);
        var tenant = await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(sql, new { prefix, hash }, cancellationToken: ct));
        return tenant;
    }
}
