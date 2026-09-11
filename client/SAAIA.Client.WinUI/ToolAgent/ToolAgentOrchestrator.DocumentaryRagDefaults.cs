using System;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private void ApplyDocumentaryRagDefaults(RouterPlan plan, string effectiveUserMessage)
    {
        if (plan.Origin == RouterPlanOrigin.Llm
            && plan.NeedClarification)
        {
            EmitRagTrace(
                "documentary_defaults.skipped",
                ("reason", "llm_clarification_owns_interaction"),
                ("intent", plan.Intent));
            return;
        }

        if (plan.Origin == RouterPlanOrigin.Llm
            && plan.SourceBackedMission is not null)
        {
            EmitRagTrace(
                "documentary_defaults.skipped",
                ("reason", "llm_source_mission_owns_retrieval"),
                ("intent", plan.Intent),
                ("source_plan_kind", plan.SourceBackedMission.PlanKind),
                ("router_tools", plan.ToolCalls
                    .Select(static call => call.Name)
                    .ToArray()));
            return;
        }

        if (ShouldRespectLlmRouterGeneralWithoutTools(plan))
        {
            EmitRagTrace(
                "documentary_defaults.skipped",
                ("reason", "router_general_no_tools"),
                ("intent", plan.Intent));
            return;
        }

        var context = BuildDocumentaryRagDefaultsContext(effectiveUserMessage);
        if (context is null)
            return;

        plan.NeedClarification = false;
        plan.ClarificationQuestions.Clear();
        plan.Intent = "rag.answer";

        if (!HasDocumentaryRagCall(plan))
        {
            plan.Intent = "rag.answer";
            plan.ToolCalls.Clear();
            AddDefaultDocumentaryRagCall(plan, context);
        }

        foreach (var call in plan.ToolCalls)
        {
            call.Name = NormalizeToolName(call.Name);
            var trustedCategoryScope = ResolveTrustedRouterRagCategoryScope(
                TryGetStringArg(call.Args, "category"),
                context.CategoryScope,
                context.EffectiveUserMessage);

            if (string.Equals(call.Name, "rag.search", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDocumentaryRagSearchDefaults(call, context, trustedCategoryScope);
            }
            else if (string.Equals(call.Name, "rag.multi_search", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDocumentaryRagMultiSearchDefaults(call, context, trustedCategoryScope);
            }
        }
    }

    private static bool HasDocumentaryRagCall(RouterPlan plan)
        => plan.ToolCalls.Any(call =>
        {
            var normalizedName = NormalizeToolName(call.Name);
            return string.Equals(normalizedName, "rag.search", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedName, "rag.multi_search", StringComparison.OrdinalIgnoreCase);
        });
}
