using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace SAAIA.Backend.Auth;

public static class ApiKeyAuth
{
    public const string TenantIdItemKey = "tenant_id";
    public const string ApiKeyIdItemKey = "api_key_id";
    public const string IsAdminItemKey = "is_admin";

    public static string Prefix(string key)
        => key.Length <= 8 ? key : key.Substring(0, 8);

    public static byte[] Sha256Bytes(string apiKey, string? pepper)
    {
        // SHA256(pepper + apiKey)
        var bytes = Encoding.UTF8.GetBytes((pepper ?? "") + apiKey);
        return SHA256.HashData(bytes);
    }

    public sealed record ApiKeyPrincipal(Guid ApiKeyId, Guid TenantId, bool IsAdmin);

    public static async Task<ApiKeyPrincipal?> ResolvePrincipalAsync(NpgsqlDataSource ds, string apiKey, string? pepper, CancellationToken ct)
    {
        var hash = Sha256Bytes(apiKey, pepper);
        var prefix = Prefix(apiKey);

        const string sql = @"
SELECT api_key_id AS ""ApiKeyId"",
       tenant_id  AS ""TenantId"",
       is_admin   AS ""IsAdmin""
FROM api_keys
WHERE revoked_at IS NULL
  AND key_prefix = @prefix
  AND key_hash = @hash
LIMIT 1;";

        await using var conn = await ds.OpenConnectionAsync(ct);
        var principal = await conn.QueryFirstOrDefaultAsync<ApiKeyPrincipal>(
            new CommandDefinition(sql, new { prefix, hash }, cancellationToken: ct));

        if (principal is null)
            return null;

        // best-effort : last_used_at
        const string upd = @"UPDATE api_keys SET last_used_at = now() WHERE api_key_id=@id;";
        await conn.ExecuteAsync(new CommandDefinition(upd, new { id = principal.ApiKeyId }, cancellationToken: ct));

        return principal;
    }
}
