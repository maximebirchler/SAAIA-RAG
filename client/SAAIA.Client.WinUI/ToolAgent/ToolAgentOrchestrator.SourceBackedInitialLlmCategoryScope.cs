using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private JsonElement TrustResolvedLlmRagCategoryScopeArg(JsonElement args)
    {
        if (GetRagTrustCategoryScopeArg(args))
            return args;

        var resolvedCategoryScope = ResolveLlmPlannedRagCategoryScope(GetRagCategoryScopeArg(args));
        if (string.IsNullOrWhiteSpace(resolvedCategoryScope))
            return args;

        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.GetRawText())
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        map["category"] = JsonSerializer.SerializeToElement(resolvedCategoryScope);
        map["categoryPath"] = JsonSerializer.SerializeToElement(resolvedCategoryScope);
        map["trustCategoryScope"] = JsonSerializer.SerializeToElement(true);

        EmitRagTrace(
            "router.llm.category_scope.trusted",
            ("category", resolvedCategoryScope));

        return JsonSerializer.SerializeToElement(map);
    }

    private async Task<JsonElement> TryApplyInitialLlmSourceBackedCategoryScopeArgAsync(
        RouterPlan plan,
        JsonElement args,
        string userMessage,
        CancellationToken ct,
        Action<string>? onProgress)
    {
        if (!ShouldRunInitialLlmSourceBackedCategoryScopeAdjudication(plan, args, userMessage))
            return args;

        try
        {
            await EnsureCatalogSnapshotCacheAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            EmitRagTrace(
                "router.llm_category_scope.skipped",
                ("reason", "catalog_unavailable"));
            return args;
        }

        var categoryHints = BuildSourceBackedLlmCategoryHintsForPrompt(
            userMessage,
            MaxSourceBackedLlmEvidencePlannerCategoryHints);
        if (!HasSourceBackedLlmCategoryHints(categoryHints))
        {
            EmitRagTrace(
                "router.llm_category_scope.skipped",
                ("reason", "no_category_hints"));
            return args;
        }

        var queries = NormalizeRagMultiSearchQueries(args);
        if (queries.Length == 0)
        {
            EmitRagTrace(
                "router.llm_category_scope.skipped",
                ("reason", "no_queries"));
            return args;
        }

        var currentAnalysis = AnalyzeSourceBackedEvidenceSufficiency(new ToolResults(), userMessage, plan.Language);
        var plannedPasses = new[]
        {
            new SourceBackedEvidenceExplorationPass(
                "router_initial",
                "LLM adjudicates whether the first source-backed retrieval should use a catalog scope.",
                queries,
                Origin: "router_llm_initial")
        };

        if (!ShouldRunLlmSourceBackedCategoryScopeAdjudication(
                plannedPasses,
                currentAnalysis,
                userMessage,
                plan.Language,
                categoryHints))
        {
            EmitRagTrace(
                "router.llm_category_scope.skipped",
                ("reason", "adjudication_not_needed"),
                ("queries", queries),
                ("category_hint_lines", categoryHints.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length));
            return args;
        }

        EmitRagTrace(
            "router.llm_category_scope.start",
            ("queries", queries),
            ("category_hint_lines", categoryHints.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length),
            ("planning", LooksLikeAnyDocumentaryPlanningRequest(userMessage)),
            ("structured_planning", ShouldGateStructuredSourceBackedPlanningCoverage(userMessage)));

        var plannerPasses = await TryBuildLlmSourceBackedEvidenceExplorationPassesAsync(
                new ToolResults(),
                currentAnalysis,
                userMessage,
                plan.Language,
                ct,
                onProgress)
            .ConfigureAwait(false);
        string? rawPlannerCategoryScope = null;
        string? resolvedCategoryScope = null;
        var plannerQueryCount = 0;
        var plannerQueries = plannerPasses
            .SelectMany(static pass => pass.Queries)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourceBackedLlmEvidenceExplorationQueries)
            .ToArray();
        foreach (var plannerPass in plannerPasses)
        {
            plannerQueryCount += plannerPass.Queries.Length;
            if (!string.IsNullOrWhiteSpace(rawPlannerCategoryScope))
                continue;

            var resolvedPlannerCategoryScope = ResolveLlmPlannedRagCategoryScope(plannerPass.CategoryScope);
            if (string.IsNullOrWhiteSpace(resolvedPlannerCategoryScope))
                continue;

            rawPlannerCategoryScope = plannerPass.CategoryScope;
            resolvedCategoryScope = resolvedPlannerCategoryScope;
        }

        EmitRagTrace(
            "router.llm_category_scope.decision",
            ("decision", string.IsNullOrWhiteSpace(resolvedCategoryScope) ? "none" : "use_scope"),
            ("confidence", (object?)(string.IsNullOrWhiteSpace(resolvedCategoryScope) ? null : "planner")),
            ("raw_category", rawPlannerCategoryScope),
            ("resolved_category", resolvedCategoryScope),
                ("accepted", !string.IsNullOrWhiteSpace(resolvedCategoryScope)),
                ("planner_passes", plannerPasses.Count),
                ("planner_queries", plannerQueryCount));
        var applyPlannerQueries = ShouldApplyInitialLlmPlannerQueries(
            queries,
            plannerQueries,
            userMessage,
            plan.Language,
            out var plannerQueryReason,
            out var missingBeforePlannerQueries,
            out var missingAfterPlannerQueries,
            out var regressedPlannerQueryAxes);
        var plannerQueriesToApply = applyPlannerQueries
            ? BuildInitialLlmPlannerQueriesToApply(queries, plannerQueries, userMessage, plan.Language)
            : plannerQueries;
        if (plannerQueries.Length > 0)
        {
            EmitRagTrace(
                applyPlannerQueries
                    ? "router.llm_category_scope.queries_applied"
                    : "router.llm_category_scope.queries_skipped",
                ("reason", plannerQueryReason),
                ("queries_before", queries),
                ("queries_after", plannerQueriesToApply),
                ("planner_queries", plannerQueries),
                ("missing_before", missingBeforePlannerQueries),
                ("missing_after", missingAfterPlannerQueries),
                ("regressed_axes", regressedPlannerQueryAxes));
        }

        if (string.IsNullOrWhiteSpace(resolvedCategoryScope) && !applyPlannerQueries)
            return args;

        var updated = string.IsNullOrWhiteSpace(resolvedCategoryScope)
            ? args
            : ApplyResolvedInitialSourceBackedLlmCategoryScopeArg(args, resolvedCategoryScope);
        if (applyPlannerQueries)
            updated = ApplyInitialSourceBackedLlmPlannerQueriesArg(updated, plannerQueriesToApply);

        EmitRagTrace(
            "router.llm_category_scope.applied",
            ("category", resolvedCategoryScope),
            ("queries", applyPlannerQueries ? plannerQueriesToApply : queries));
        return updated;
    }

    private static string[] BuildInitialLlmPlannerQueriesToApply(
        IReadOnlyList<string> currentQueries,
        IReadOnlyList<string> plannerQueries,
        string userMessage,
        string language)
    {
        return plannerQueries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourceBackedLlmEvidenceExplorationQueries)
            .ToArray();
    }

    private static bool ShouldApplyInitialLlmPlannerQueries(
        IReadOnlyList<string> currentQueries,
        IReadOnlyList<string> plannerQueries,
        string userMessage,
        string language,
        out string reason,
        out string[] missingBefore,
        out string[] missingAfter,
        out string[] regressedAxes)
    {
        missingBefore = Array.Empty<string>();
        missingAfter = Array.Empty<string>();
        regressedAxes = Array.Empty<string>();

        var normalizedPlannerQueries = plannerQueries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPlannerQueries.Length == 0)
        {
            reason = "no_planner_queries";
            return false;
        }

        if (!ShouldGateStructuredSourceBackedPlanningCoverage(userMessage))
        {
            reason = "planner_queries_available";
            return true;
        }

        missingBefore = DetectMissingStructuredRouterSearchAxesForQueries(currentQueries, userMessage, language);
        missingAfter = DetectMissingStructuredRouterSearchAxesForQueries(normalizedPlannerQueries, userMessage, language);
        regressedAxes = FindStructuredRouterSearchAxisRegressions(missingBefore, missingAfter);
        if (regressedAxes.Length > 0)
        {
            reason = "coverage_regressed";
            return false;
        }

        if (missingAfter.Length > missingBefore.Length)
        {
            reason = "coverage_worse";
            return false;
        }

        reason = missingAfter.Length < missingBefore.Length
            ? "coverage_improved"
            : "coverage_preserved";
        return true;
    }

    private static bool ShouldRunInitialLlmSourceBackedCategoryScopeAdjudication(
        RouterPlan plan,
        JsonElement args,
        string? userMessage)
    {
        if (plan.Origin != RouterPlanOrigin.Llm)
            return false;

        if (!string.IsNullOrWhiteSpace(GetRagCategoryScopeArg(args))
            || GetRagTrustCategoryScopeArg(args)
            || !string.IsNullOrWhiteSpace(GetRagDocIdArg(args))
            || !string.IsNullOrWhiteSpace(GetRagDocPathArg(args))
            || GetRagPageStartArg(args).HasValue
            || GetRagPageEndArg(args).HasValue)
        {
            return false;
        }

        var sourceExploration =
            string.Equals(GetRagResearchModeArg(args), "source_exploration", StringComparison.OrdinalIgnoreCase)
            || GetRagIncludeResearchSurfacesArg(args) == true;
        if (!sourceExploration)
            return false;

        return LooksLikeAnyDocumentaryPlanningRequest(userMessage)
               || LooksLikeGenericCollectionOrListRequest(userMessage)
               || LooksLikeMultipleCandidateSynthesisRequest(userMessage)
               || LooksLikeBroadSourceBackedCompositionRequest(userMessage)
               || LooksLikeBroadSynthesisRequestShape(userMessage)
               || LooksLikeUserNeedsSynthesizedDecisionOrPlan(userMessage)
               || ShouldGateStructuredSourceBackedPlanningCoverage(userMessage)
               || ShouldOfferBroadenedSourceSearch(userMessage);
    }

    private static JsonElement ApplyResolvedInitialSourceBackedLlmCategoryScopeArg(JsonElement args, string resolvedCategoryScope)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.GetRawText())
                  ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        map["category"] = JsonSerializer.SerializeToElement(resolvedCategoryScope);
        map["categoryPath"] = JsonSerializer.SerializeToElement(resolvedCategoryScope);
        map["trustCategoryScope"] = JsonSerializer.SerializeToElement(true);
        return JsonSerializer.SerializeToElement(map);
    }

    private static JsonElement ApplyInitialSourceBackedLlmPlannerQueriesArg(JsonElement args, IReadOnlyList<string> plannerQueries)
    {
        var queries = plannerQueries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSourceBackedLlmEvidenceExplorationQueries)
            .ToArray();
        if (queries.Length == 0)
            return args;

        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.GetRawText())
                  ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        map["queries"] = JsonSerializer.SerializeToElement(queries);
        return JsonSerializer.SerializeToElement(map);
    }
}