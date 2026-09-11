using System;
using System.Collections.Generic;
using System.Linq;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int SourceBackedCatalogHintLimit = 40;

    private IReadOnlyList<SourceBackedCatalogHint> BuildSourceBackedCatalogHints()
    {
        var categories = new List<ToolMemory.CategorySnapshot>();
        if (_mem.LastResolvedCategory is not null)
            categories.Add(_mem.LastResolvedCategory);

        categories.AddRange(_mem.CatalogSnapshotCache?.Categories ?? new List<ToolMemory.CategorySnapshot>());
        categories.AddRange(_mem.LastPresentedCategories ?? new List<ToolMemory.CategorySnapshot>());

        var defaultCategoryPath = string.IsNullOrWhiteSpace(_mem.LastResolvedCategory?.CategoryPath)
            ? string.Empty
            : _mem.LastResolvedCategory!.CategoryPath.Trim();
        var unique = new Dictionary<string, ToolMemory.CategorySnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in categories)
        {
            if (category is null || string.IsNullOrWhiteSpace(category.CategoryPath))
                continue;

            var key = category.CategoryPath.Trim();
            if (unique.TryGetValue(key, out var existing))
            {
                unique[key] = MergeCatalogHintCategory(existing, category);
            }
            else
            {
                unique[key] = category;
            }
        }

        return unique.Values
            .OrderBy(static category => category.Ordinal <= 0 ? int.MaxValue : category.Ordinal)
            .ThenBy(static category => category.CategoryPath, StringComparer.OrdinalIgnoreCase)
            .Take(SourceBackedCatalogHintLimit)
            .Select(category =>
            {
                var categoryPath = category.CategoryPath.Trim();
                return new SourceBackedCatalogHint(
                    categoryPath,
                    string.IsNullOrWhiteSpace(category.DisplayName) ? categoryPath : category.DisplayName.Trim(),
                    category.TotalDocuments > 0 ? category.TotalDocuments : null,
                    category.Aliases?
                        .Where(static alias => !string.IsNullOrWhiteSpace(alias))
                        .Select(static alias => alias.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(6)
                        .ToArray() ?? Array.Empty<string>(),
                    IsDefaultScope: string.Equals(categoryPath, defaultCategoryPath, StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();
    }

    private static ToolMemory.CategorySnapshot MergeCatalogHintCategory(
        ToolMemory.CategorySnapshot existing,
        ToolMemory.CategorySnapshot incoming)
    {
        var aliases = (existing.Aliases ?? new List<string>())
            .Concat(incoming.Aliases ?? new List<string>())
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Select(static alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ToolMemory.CategorySnapshot
        {
            CategoryRef = string.IsNullOrWhiteSpace(incoming.CategoryRef) ? existing.CategoryRef : incoming.CategoryRef,
            CategoryPath = string.IsNullOrWhiteSpace(incoming.CategoryPath) ? existing.CategoryPath : incoming.CategoryPath,
            DisplayName = string.IsNullOrWhiteSpace(incoming.DisplayName) ? existing.DisplayName : incoming.DisplayName,
            Ordinal = incoming.Ordinal > 0 ? incoming.Ordinal : existing.Ordinal,
            TotalDocuments = incoming.TotalDocuments > 0 ? incoming.TotalDocuments : existing.TotalDocuments,
            Aliases = aliases
        };
    }
}
