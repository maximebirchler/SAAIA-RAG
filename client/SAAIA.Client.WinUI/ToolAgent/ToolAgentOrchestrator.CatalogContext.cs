
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static readonly TimeSpan RuntimeContextTtl = TimeSpan.FromMinutes(5);

    private async Task EnsureRuntimeCatalogContextAsync(CancellationToken ct)
    {
        await EnsureCapabilitiesCacheAsync(ct).ConfigureAwait(false);
        await EnsureCatalogSnapshotCacheAsync(ct).ConfigureAwait(false);

        if ((_mem.LastPresentedCategories?.Count ?? 0) == 0 && _mem.CatalogSnapshotCache?.Categories is { Count: > 0 })
            _mem.LastPresentedCategories = _mem.CatalogSnapshotCache.Categories.Select(CloneCategorySnapshot).ToList();
    }

    private async Task EnsureCapabilitiesCacheAsync(CancellationToken ct)
    {
        if (_mem.CapabilitiesCache is not null && (DateTimeOffset.UtcNow - _mem.CapabilitiesCache.LoadedAtUtc) < RuntimeContextTtl)
            return;

        var json = await _api.AuthCapabilitiesAsync(ct).ConfigureAwait(false);
        _mem.CapabilitiesCache = new ToolMemory.RuntimeCapabilitiesSnapshot
        {
            IsAuthenticated = json.TryGetProperty("user", out var user) && TryGetBoolProp(user, "isAuthenticated") == true,
            IsAdmin = json.TryGetProperty("user", out user) && TryGetBoolProp(user, "isAdmin") == true,
            DefaultLocale = json.TryGetProperty("ui", out var ui) ? TryGetString(ui, "defaultLocale") ?? "fr-CH" : "fr-CH",
            LoadedAtUtc = DateTimeOffset.UtcNow,
            DirectCommandIds = ParseCommandIds(json, "directCommands"),
            AdminCommandIds = ParseCommandIds(json, "adminCommands")
        };
    }

    private async Task EnsureCatalogSnapshotCacheAsync(CancellationToken ct)
    {
        if (_mem.CatalogSnapshotCache is not null && (DateTimeOffset.UtcNow - _mem.CatalogSnapshotCache.LoadedAtUtc) < RuntimeContextTtl)
            return;

        var json = await _api.CatalogSnapshotAsync(ct).ConfigureAwait(false);
        _mem.CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
        {
            SnapshotId = TryGetString(json, "snapshotId"),
            CatalogVersion = TryGetString(json, "catalogVersion"),
            LoadedAtUtc = DateTimeOffset.UtcNow,
            TotalDocuments = json.TryGetProperty("totals", out var totals) ? TryGetInt(totals, "documents") : null,
            TotalCategories = json.TryGetProperty("totals", out totals) ? TryGetInt(totals, "categories") : null,
            Categories = ParseCategoriesFromCatalogJson(json)
        };
    }

    private static List<string> ParseCommandIds(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            return new List<string>();
        if (!capabilities.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return arr.EnumerateArray()
            .Select(x => TryGetString(x, "commandId"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ToolMemory.CategorySnapshot> ParseCategoriesFromCatalogJson(JsonElement result)
    {
        JsonElement items;
        if (result.TryGetProperty("categories", out items) && items.ValueKind == JsonValueKind.Array)
        {
        }
        else if (result.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array)
        {
        }
        else if (result.TryGetProperty("items", out items) && items.ValueKind == JsonValueKind.Array)
        {
        }
        else
        {
            return new List<ToolMemory.CategorySnapshot>();
        }

        var list = new List<ToolMemory.CategorySnapshot>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var aliases = new List<string>();
            if (entry.TryGetProperty("aliases", out var aliasArray) && aliasArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var alias in aliasArray.EnumerateArray())
                {
                    if (alias.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alias.GetString()))
                        aliases.Add(alias.GetString()!.Trim());
                }
            }

            list.Add(new ToolMemory.CategorySnapshot
            {
                CategoryRef = TryGetString(entry, "categoryRef") ?? TryGetString(entry, "path") ?? TryGetString(entry, "name") ?? string.Empty,
                CategoryPath = TryGetString(entry, "categoryPath") ?? TryGetString(entry, "path") ?? string.Empty,
                DisplayName = TryGetString(entry, "canonicalName") ?? TryGetString(entry, "name") ?? TryGetString(entry, "categoryPath") ?? string.Empty,
                Ordinal = TryGetInt(entry, "displayOrder") ?? TryGetInt(entry, "ordinal") ?? 0,
                TotalDocuments = TryGetInt(entry, "documentCount") ?? TryGetInt(entry, "totalDocuments") ?? 0,
                Aliases = aliases
            });
        }

        return list;
    }

    private void StagePendingDirectCommand(string commandId, object args, string source, ToolMemory.CategorySnapshot? category = null)
    {
        _mem.StagedDirectCommand = new ToolMemory.PendingDirectCommand
        {
            CommandId = commandId,
            ArgsJson = JsonSerializer.Serialize(args),
            CategoryRef = category?.CategoryRef,
            CategoryPath = category?.CategoryPath,
            Source = source,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
