using System;
using System.Collections.Generic;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal sealed record SourceBackedScopedRetrievalPlan(
    RetrievalPlan Plan,
    string? DefaultCategoryPath,
    int AppliedRequestCount);

internal static class SourceBackedRetrievalScope
{
    public static IReadOnlyList<string> GetAvailableCategoryPaths(SourceBackedIntake? intake)
        => intake?.CatalogHints?
            .Where(static hint => hint is not null && !string.IsNullOrWhiteSpace(hint.CategoryPath))
            .Select(static hint => hint.CategoryPath.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
           ?? Array.Empty<string>();

    public static int GetCategoryScopeId(SourceBackedIntake? intake, string? categoryPath)
    {
        if (string.IsNullOrWhiteSpace(categoryPath))
            return 0;

        var paths = GetAvailableCategoryPaths(intake);
        for (var index = 0; index < paths.Count; index++)
        {
            if (string.Equals(paths[index], categoryPath.Trim(), StringComparison.OrdinalIgnoreCase))
                return index + 1;
        }

        return -1;
    }

    public static bool TryResolveCategoryScopeId(
        SourceBackedIntake? intake,
        int? scopeId,
        out string? categoryPath)
    {
        categoryPath = null;
        if (scopeId is null || scopeId < 0)
            return false;
        if (scopeId == 0)
            return true;

        var paths = GetAvailableCategoryPaths(intake);
        if (scopeId > paths.Count)
            return false;

        categoryPath = paths[scopeId.Value - 1];
        return true;
    }

    public static string? TryGetDefaultCategoryPath(SourceBackedIntake? intake)
        => intake?.CatalogHints?
            .Where(static hint => hint is not null
                                  && hint.IsDefaultScope
                                  && !string.IsNullOrWhiteSpace(hint.CategoryPath))
            .Select(static hint => hint.CategoryPath.Trim())
            .FirstOrDefault();

    public static SourceBackedScopedRetrievalPlan ApplyDefaultCategoryScope(
        SourceBackedIntake intake,
        RetrievalPlan plan)
    {
        var defaultCategoryPath = TryGetDefaultCategoryPath(intake);
        if (string.IsNullOrWhiteSpace(defaultCategoryPath) || plan.Requests.Count == 0)
            return new SourceBackedScopedRetrievalPlan(plan, defaultCategoryPath, AppliedRequestCount: 0);

        var applied = 0;
        var scopedRequests = plan.Requests
            .Select(request =>
            {
                if (!ShouldApplyDefaultCategoryScope(request))
                    return request;

                applied++;
                return request with { CategoryPath = defaultCategoryPath };
            })
            .ToArray();

        return new SourceBackedScopedRetrievalPlan(
            plan with { Requests = scopedRequests },
            defaultCategoryPath,
            applied);
    }

    private static bool ShouldApplyDefaultCategoryScope(RetrievalRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CategoryPath))
            return false;

        var toolName = (request.ToolName ?? string.Empty).Trim().ToLowerInvariant();
        return toolName is "rag.search" or "rag.multi_search" or "documents.navigation";
    }
}
