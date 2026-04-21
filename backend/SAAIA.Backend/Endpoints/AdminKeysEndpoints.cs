using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Endpoints;

public static class AdminKeysEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/admin/keys");

        g.MapPost("", CreateKeyAsync);
        g.MapGet("", ListKeysAsync);
        g.MapPost("/{apiKeyId:guid}/revoke", RevokeKeyAsync);
        g.MapPost("/{apiKeyId:guid}/rotate", RotateKeyAsync);
    }

    public sealed record CreateKeyRequest(string Label, bool IsAdmin = false);
    public sealed record CreateKeyResponse(Guid ApiKeyId, string ApiKey, string Label, bool IsAdmin, DateTimeOffset CreatedAtUtc);

    private static async Task<IResult> CreateKeyAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<ApiKeyAuthOptions> authOpt,
        CreateKeyRequest req)
    {
        AdminAuth.EnsureAdmin(ctx);

        var ct = ctx.RequestAborted;
        var tenantId = ctx.GetTenantId();

        var label = (req.Label ?? "").Trim();
        if (label.Length < 2) return Results.BadRequest(new { error = "label must be at least 2 chars" });
        if (label.Length > 80) label = label.Substring(0, 80);

        var apiKeyPlain = ApiKeyGenerator.Generate();
        var prefix = ApiKeyAuth.Prefix(apiKeyPlain);
        var hash = ApiKeyAuth.Sha256Bytes(apiKeyPlain, authOpt.Value.Pepper);
        var apiKeyId = Guid.NewGuid();

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
INSERT INTO api_keys(api_key_id, tenant_id, key_prefix, key_hash, label, is_admin, created_at, revoked_at, last_used_at)
VALUES (@id, @tenant, @prefix, @hash, @label, @is_admin, now(), NULL, NULL);";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            id = apiKeyId,
            tenant = tenantId,
            prefix,
            hash,
            label,
            is_admin = req.IsAdmin
        }, cancellationToken: ct));

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            ctx.GetApiKeyIdOrNull(),
            true,
            "admin.key.create",
            apiKeyId.ToString(),
            new { label, isAdmin = req.IsAdmin },
            ctx.Connection.RemoteIpAddress?.ToString(),
            ct
        );

        // IMPORTANT: on ne renvoie la clé en clair qu'une seule fois (ici)
        return Results.Ok(new CreateKeyResponse(apiKeyId, apiKeyPlain, label, req.IsAdmin, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> ListKeysAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        AdminAuth.EnsureAdmin(ctx);

        var ct = ctx.RequestAborted;
        var tenantId = ctx.GetTenantId();

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  api_key_id   AS ""ApiKeyId"",
  label        AS ""Label"",
  is_admin     AS ""IsAdmin"",
  created_at   AS ""CreatedAt"",
  revoked_at   AS ""RevokedAt"",
  last_used_at AS ""LastUsedAt"",
  key_prefix   AS ""KeyPrefix""
FROM api_keys
WHERE tenant_id=@tenant
ORDER BY created_at DESC;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId });
        return Results.Ok(rows);
    }

    private static async Task<IResult> RevokeKeyAsync(HttpContext ctx, NpgsqlDataSource ds, Guid apiKeyId)
    {
        AdminAuth.EnsureAdmin(ctx);

        var ct = ctx.RequestAborted;
        var tenantId = ctx.GetTenantId();

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
UPDATE api_keys
SET revoked_at=now()
WHERE tenant_id=@tenant
  AND api_key_id=@id
  AND revoked_at IS NULL;";

        var updated = await conn.ExecuteAsync(new CommandDefinition(sql, new { tenant = tenantId, id = apiKeyId }, cancellationToken: ct));

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            ctx.GetApiKeyIdOrNull(),
            true,
            "admin.key.revoke",
            apiKeyId.ToString(),
            new { updated },
            ctx.Connection.RemoteIpAddress?.ToString(),
            ct
        );

        return Results.Ok(new { ok = true, updated });
    }

    private static async Task<IResult> RotateKeyAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<ApiKeyAuthOptions> authOpt,
        Guid apiKeyId)
    {
        AdminAuth.EnsureAdmin(ctx);

        var ct = ctx.RequestAborted;
        var tenantId = ctx.GetTenantId();

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string getSql = @"
SELECT label AS ""Label"", is_admin AS ""IsAdmin""
FROM api_keys
WHERE tenant_id=@tenant AND api_key_id=@id
LIMIT 1;";

        var existing = await conn.QueryFirstOrDefaultAsync<ExistingKey>(
            new CommandDefinition(getSql, new { tenant = tenantId, id = apiKeyId }, transaction: tx, cancellationToken: ct));

        if (existing is null)
            return Results.NotFound(new { error = "api key not found" });

        // Revoke old
        const string revokeSql = @"
UPDATE api_keys
SET revoked_at=now()
WHERE tenant_id=@tenant AND api_key_id=@id AND revoked_at IS NULL;";
        await conn.ExecuteAsync(new CommandDefinition(revokeSql, new { tenant = tenantId, id = apiKeyId }, transaction: tx, cancellationToken: ct));

        // Create new
        var newPlain = ApiKeyGenerator.Generate();
        var prefix = ApiKeyAuth.Prefix(newPlain);
        var hash = ApiKeyAuth.Sha256Bytes(newPlain, authOpt.Value.Pepper);
        var newId = Guid.NewGuid();

        const string insSql = @"
INSERT INTO api_keys(api_key_id, tenant_id, key_prefix, key_hash, label, is_admin, created_at, revoked_at, last_used_at)
VALUES (@id, @tenant, @prefix, @hash, @label, @is_admin, now(), NULL, NULL);";

        await conn.ExecuteAsync(new CommandDefinition(insSql, new
        {
            id = newId,
            tenant = tenantId,
            prefix,
            hash,
            label = existing.Label,
            is_admin = existing.IsAdmin
        }, transaction: tx, cancellationToken: ct));

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            ctx.GetApiKeyIdOrNull(),
            true,
            "admin.key.rotate",
            apiKeyId.ToString(),
            new { newApiKeyId = newId },
            ctx.Connection.RemoteIpAddress?.ToString(),
            ct,
            tx
        );

        await tx.CommitAsync(ct);

        return Results.Ok(new
        {
            oldApiKeyId = apiKeyId,
            newApiKeyId = newId,
            apiKey = newPlain,
            label = existing.Label,
            isAdmin = existing.IsAdmin
        });
    }

    private sealed class ExistingKey
    {
        public string? Label { get; set; }
        public bool IsAdmin { get; set; }
    }
}

internal static class ApiKeyGenerator
{
    public static string Generate()
    {
        Span<byte> b = stackalloc byte[32];
        RandomNumberGenerator.Fill(b);
        return "saaia_" + Base64UrlEncode(b.ToArray());
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
