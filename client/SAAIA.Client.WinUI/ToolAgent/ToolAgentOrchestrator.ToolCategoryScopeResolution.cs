using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<(string? categoryPath, string? categoryRef)> ResolveCategoryScopeArgsAsync(JsonElement args, CancellationToken ct)
    {
        var rawPath = GetStringArg(args, "categoryPath") ?? GetStringArg(args, "path") ?? GetStringArg(args, "category");
        var rawRef = GetStringArg(args, "categoryRef");

        var normalizedPath = NormalizeCategoryPathArg(rawPath);
        var normalizedRef = string.IsNullOrWhiteSpace(rawRef) ? null : rawRef.Trim();

        if (!string.IsNullOrWhiteSpace(normalizedRef) || !string.IsNullOrWhiteSpace(normalizedPath))
        {
            try
            {
                var resolved = await _api.ResolveCategoryAsync(normalizedPath, normalizedRef, ct).ConfigureAwait(false);
                if (resolved.ValueKind == JsonValueKind.Object
                    && resolved.TryGetProperty("items", out var items)
                    && items.ValueKind == JsonValueKind.Array)
                {
                    var item = items.EnumerateArray().FirstOrDefault();
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var resolvedPath = GetStringArg(item, "categoryPath");
                        var resolvedRef = GetStringArg(item, "categoryRef");
                        if (!string.IsNullOrWhiteSpace(resolvedPath) || !string.IsNullOrWhiteSpace(resolvedRef))
                        {
                            return (string.IsNullOrWhiteSpace(resolvedPath) ? null : resolvedPath.Trim(),
                                    string.IsNullOrWhiteSpace(resolvedRef) ? null : resolvedRef.Trim());
                        }
                    }
                }
            }
            catch
            {
                // Keep the current local fallback behavior if the backend contract is not available yet.
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedRef) || !string.IsNullOrWhiteSpace(normalizedPath))
        {
            var snapshot = ResolveCategorySnapshotFromReference(normalizedRef, normalizedPath);
            if (snapshot is not null)
            {
                var resolvedPath = !string.IsNullOrWhiteSpace(snapshot.CategoryPath) ? snapshot.CategoryPath : normalizedPath;
                var resolvedRef = !string.IsNullOrWhiteSpace(snapshot.CategoryRef) ? snapshot.CategoryRef : normalizedRef;
                return (string.IsNullOrWhiteSpace(resolvedPath) ? null : resolvedPath,
                        string.IsNullOrWhiteSpace(resolvedRef) ? null : resolvedRef);
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedRef)
            || !string.IsNullOrWhiteSpace(normalizedPath))
        {
            EmitRagTrace(
                "category_scope.unresolved_filter_removed",
                ("requested_path", normalizedPath),
                ("requested_ref", normalizedRef),
                ("reason", "not_resolved_by_backend_or_known_catalog"));
            ClientLog.Warn(
                "ToolAgent unresolved category scope removed before retrieval: "
                + $"path={TruncateForPrompt(normalizedPath, 120)}|"
                + $"ref={TruncateForPrompt(normalizedRef, 80)}");
        }

        // An unresolved category is not a trustworthy corpus boundary. Keeping it
        // silently produces an empty search surface; removing it preserves recall
        // while leaving the LLM responsible for semantic candidate selection.
        return (null, null);
    }

    private IEnumerable<ToolMemory.CategorySnapshot> EnumerateKnownCategories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_mem.LastResolvedCategory is not null)
        {
            var key = !string.IsNullOrWhiteSpace(_mem.LastResolvedCategory.CategoryRef)
                ? _mem.LastResolvedCategory.CategoryRef
                : _mem.LastResolvedCategory.CategoryPath;
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                yield return _mem.LastResolvedCategory;
        }

        if (_mem.LastPresentedCategories is not null)
        {
            foreach (var category in _mem.LastPresentedCategories)
            {
                var key = !string.IsNullOrWhiteSpace(category.CategoryRef) ? category.CategoryRef : category.CategoryPath;
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    yield return category;
            }
        }

        if (_mem.CatalogSnapshotCache?.Categories is { Count: > 0 })
        {
            foreach (var category in _mem.CatalogSnapshotCache.Categories)
            {
                var key = !string.IsNullOrWhiteSpace(category.CategoryRef) ? category.CategoryRef : category.CategoryPath;
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    yield return category;
            }
        }
    }

    private bool LooksLikeKnownCategoryReference(string? value)
    {
        var normalized = NormalizeDocumentLookupText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        foreach (var category in EnumerateKnownCategories())
        {
            if (NormalizeDocumentLookupText(category.CategoryRef) == normalized
                || NormalizeDocumentLookupText(category.DisplayName) == normalized
                || NormalizeDocumentLookupText(category.CategoryPath) == normalized
                || NormalizeDocumentLookupText(category.Ordinal.ToString()) == normalized)
            {
                return true;
            }

            foreach (var alias in category.Aliases)
            {
                if (NormalizeDocumentLookupText(alias) == normalized)
                    return true;
            }
        }

        return false;
    }
}
