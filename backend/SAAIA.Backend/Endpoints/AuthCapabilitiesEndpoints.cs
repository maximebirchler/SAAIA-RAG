using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Contracts;

namespace SAAIA.Backend.Endpoints;

public static class AuthCapabilitiesEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/auth/capabilities", GetCapabilitiesAsync).AllowApiKeyOrAdminKey();
        app.Logger.LogInformation("Mapped auth/capabilities endpoint");
    }

    private static async Task<IResult> GetCapabilitiesAsync(HttpContext ctx, NpgsqlDataSource ds, IConfiguration config)
    {
        var tenantId = ctx.GetTenantId();
        var apiKeyId = ctx.GetApiKeyId();
        var isAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        await using var conn = await ds.OpenConnectionAsync(ct);
        var label = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            @"SELECT label FROM api_keys WHERE tenant_id=@tenant AND api_key_id=@apiKeyId LIMIT 1;",
            new { tenant = tenantId, apiKeyId },
            cancellationToken: ct));

        var locale = NormalizeLocale(config["Localization:DefaultLocale"] ?? config["Ui:DefaultLocale"] ?? "fr-CH");

        var response = new AuthCapabilitiesResponse
        {
            User = new AuthCapabilitiesUser
            {
                IsAuthenticated = true,
                IsAdmin = isAdmin,
                DisplayName = BuildDisplayName(label, isAdmin)
            },
            Ui = new AuthCapabilitiesUi
            {
                DefaultLocale = locale
            },
            Capabilities = new AuthCapabilitiesPayload
            {
                DirectCommands = BuildDirectCommands(),
                AdminCommands = isAdmin ? BuildAdminCommands() : new List<AuthCapabilityCommand>()
            }
        };

        return Results.Ok(response);
    }

    private static List<AuthCapabilityCommand> BuildDirectCommands()
        => new()
        {
            CreateCommand(
                "catalog.categories.list",
                "always",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }),
            CreateCommand(
                "catalog.documents.listAll",
                "always",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new
                    {
                        pageSize = new { type = "integer", minimum = 1, maximum = 200 },
                        cursor = new { type = new[] { "string", "null" } }
                    },
                    additionalProperties = false
                }),
            CreateCommand(
                "catalog.documents.listByCategory",
                "context_or_selector",
                new[] { "snapshot.categories" },
                new
                {
                    type = "object",
                    properties = new
                    {
                        categoryRef = new { type = "string" },
                        pageSize = new { type = "integer", minimum = 1, maximum = 200 },
                        cursor = new { type = new[] { "string", "null" } }
                    },
                    required = new[] { "categoryRef" },
                    additionalProperties = false
                }),
            CreateCommand(
                "catalog.documents.search",
                "always",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string" },
                        pageSize = new { type = "integer", minimum = 1, maximum = 200 },
                        cursor = new { type = new[] { "string", "null" } }
                    },
                    required = new[] { "query" },
                    additionalProperties = false
                }),
            CreateCommand(
                "catalog.stats.view",
                "always",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new
                    {
                        categoryRef = new { type = new[] { "string", "null" } }
                    },
                    additionalProperties = false
                }),
            CreateCommand(
                "catalog.tree.view",
                "always",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new
                    {
                        depth = new { type = "integer", minimum = 1, maximum = 24 },
                        format = new { type = new[] { "string", "null" } }
                    },
                    additionalProperties = false
                })
        };

    private static List<AuthCapabilityCommand> BuildAdminCommands()
        => new()
        {
            CreateCommand(
                "catalog.summaries.missing.count",
                "admin_only",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new
                    {
                        categoryRef = new { type = new[] { "string", "null" } }
                    },
                    additionalProperties = false
                }),
            CreateCommand(
                "catalog.summaries.missing.list",
                "admin_only",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new
                    {
                        categoryRef = new { type = new[] { "string", "null" } },
                        pageSize = new { type = "integer", minimum = 1, maximum = 500 },
                        cursor = new { type = new[] { "string", "null" } }
                    },
                    additionalProperties = false
                }),
            CreateCommand(
                "catalog.summaries.present.count",
                "admin_only",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new
                    {
                        categoryRef = new { type = new[] { "string", "null" } }
                    },
                    additionalProperties = false
                }),
            CreateCommand(
                "catalog.summaries.present.list",
                "admin_only",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new
                    {
                        categoryRef = new { type = new[] { "string", "null" } },
                        pageSize = new { type = "integer", minimum = 1, maximum = 500 },
                        cursor = new { type = new[] { "string", "null" } }
                    },
                    additionalProperties = false
                }),
            CreateCommand(
                "admin.catalog.rescan",
                "admin_only",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }),
            CreateCommand(
                "admin.ingestion.reindexDocument",
                "admin_only",
                Array.Empty<string>(),
                new
                {
                    type = "object",
                    properties = new
                    {
                        documentRef = new { type = "string" },
                        docPath = new { type = new[] { "string", "null" } },
                        docId = new { type = new[] { "string", "null" } },
                        docName = new { type = new[] { "string", "null" } },
                        displayName = new { type = new[] { "string", "null" } }
                    },
                    required = new[] { "documentRef" },
                    additionalProperties = false
                })
        };

    private static AuthCapabilityCommand CreateCommand(string commandId, string visibility, IReadOnlyCollection<string> requiresContext, object argsSchema)
        => new()
        {
            CommandId = commandId,
            Visibility = visibility,
            RequiresContext = requiresContext.ToList(),
            ArgsSchema = JsonSerializer.SerializeToElement(argsSchema)
        };

    private static string BuildDisplayName(string? label, bool isAdmin)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            var trimmed = label.Trim();
            var separator = trimmed.IndexOf(':');
            if (separator >= 0 && separator < trimmed.Length - 1)
                return trimmed[(separator + 1)..].Trim();
            return trimmed;
        }

        return isAdmin ? "Admin" : "User";
    }

    private static string NormalizeLocale(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? "fr-CH" : value;
    }
}
