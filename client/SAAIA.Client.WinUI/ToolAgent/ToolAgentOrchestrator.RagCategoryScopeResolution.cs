using System;
using System.Collections.Generic;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private string? ResolveRagCategoryScope(string effectiveUserMessage)
    {
        var message = NormalizeLooseLookup(effectiveUserMessage);
        var candidates = _mem.CatalogSnapshotCache?.Categories ?? new List<ToolMemory.CategorySnapshot>();
        foreach (var category in candidates)
        {
            var names = new[]
                {
                    category.CategoryPath,
                    category.DisplayName,
                    category.CategoryRef
                }
                .Concat(category.Aliases ?? new List<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(NormalizeLooseLookup)
                .Where(name => name.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (names.Any(name => message.Contains(name, StringComparison.Ordinal)))
                return string.IsNullOrWhiteSpace(category.CategoryPath) ? category.DisplayName : category.CategoryPath;
        }

        var lexicalCategoryScope = TryResolveLexicalRagCategoryScope(effectiveUserMessage, candidates);
        if (!string.IsNullOrWhiteSpace(lexicalCategoryScope))
            return lexicalCategoryScope;

        if (!string.IsNullOrWhiteSpace(_mem.LastResolvedCategory?.CategoryPath))
            return _mem.LastResolvedCategory!.CategoryPath;

        var explicitScope = TryExtractExplicitRagCategoryScope(effectiveUserMessage);
        if (!string.IsNullOrWhiteSpace(explicitScope))
            return explicitScope;

        var lastSourceCategories = (_mem.LastSourcesUsed ?? new List<ToolMemory.SourceRef>())
            .Select(static source => TryExtractTopLevelCategoryFromDocPath(source.DocPath))
            .Where(static category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return lastSourceCategories.Length == 1 ? lastSourceCategories[0] : null;
    }

    private string? ResolveKnownDocumentContentSearchCategoryScope(string effectiveUserMessage)
    {
        var normalizedMessage = NormalizeLooseLookup(effectiveUserMessage);
        var knownCategories = _mem.CatalogSnapshotCache?.Categories
            ?? new List<ToolMemory.CategorySnapshot>();
        foreach (var category in knownCategories)
        {
            var names = new[]
                {
                    category.CategoryPath,
                    category.DisplayName,
                    category.CategoryRef
                }
                .Concat(category.Aliases ?? new List<string>())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(NormalizeLooseLookup)
                .Where(static value => value.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (names.Any(name => normalizedMessage.Contains(name, StringComparison.Ordinal)))
            {
                return string.IsNullOrWhiteSpace(category.CategoryPath)
                    ? category.DisplayName
                    : category.CategoryPath;
            }
        }

        var lastResolved = NormalizeLooseLookup(_mem.LastResolvedCategory?.CategoryPath);
        if (!string.IsNullOrWhiteSpace(lastResolved))
            return _mem.LastResolvedCategory!.CategoryPath;

        var explicitScope = NormalizeLooseLookup(TryExtractExplicitRagCategoryScope(effectiveUserMessage));
        if (string.IsNullOrWhiteSpace(explicitScope))
            return null;

        foreach (var category in knownCategories)
        {
            var names = new[]
                {
                    category.CategoryPath,
                    category.DisplayName,
                    category.CategoryRef
                }
                .Concat(category.Aliases ?? new List<string>())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(NormalizeLooseLookup);
            if (names.Any(name => string.Equals(name, explicitScope, StringComparison.Ordinal)))
            {
                return string.IsNullOrWhiteSpace(category.CategoryPath)
                    ? category.DisplayName
                    : category.CategoryPath;
            }
        }

        return null;
    }
}
