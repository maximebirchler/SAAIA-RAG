using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private async Task<RouterPlan> RouterAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        CancellationToken ct,
        bool disallowMetaSetLanguage)
    {
        var sw = Stopwatch.StartNew();
        var manifestJson = ToolManifest.BuildRouterConversationManifestJson();
        var toolbook = ToolManifest.ConversationToolbookText;
        var repairHint = DocumentRefResolver.IsRepairMessage(userMessage);
        var resolverHint = DocumentRefResolver.Analyze(userMessage, _mem.LastFocusedDocument, _mem.LastListedDocuments, _mem.LastRequestedDocumentRef);

        var memoryCtx = BuildCompactRouterMemoryContext(resolverHint);

        var detectedMessageLanguage = ResolveInteractionLanguage(userMessage);
        var useCompactSourceBackedRouter = ShouldUseCompactSourceBackedRouterPrompt(userMessage);
        var system = useCompactSourceBackedRouter
            ? BuildCompactSourceBackedRouterSystemPrompt(detectedMessageLanguage, disallowMetaSetLanguage)
            : PromptCatalog.BuildCompactRouterSystemPrompt(manifestJson, toolbook) + $@"

Additional runtime rules:
- Last answer language (informational only): {_mem.LastLanguage}
- Current message language hint: {detectedMessageLanguage}
- Disallow meta.set_language for this turn: {(disallowMetaSetLanguage ? "true" : "false")}
- The current message looks like a repair/correction turn: {(repairHint ? "true" : "false")}
- If document resolution hint says clarification is needed, prefer a short clarification over a blind tool call.
";

        var user = useCompactSourceBackedRouter
            ? BuildCompactSourceBackedRouterUserPrompt(chatHistory, userMessage)
            : $@"
MEMORY (json):
{JsonSerializer.Serialize(memoryCtx)}

CHAT_TAIL (for context):
{SerializeTail(chatHistory, maxTurns: 4)}

USER_MESSAGE:
{userMessage}
";

        string raw;
        var routerTimeoutMs = useCompactSourceBackedRouter
            ? SourceBackedRouterLlmTimeoutMs
            : _llm.SupportsStructuredOutput
                ? StructuredRouterLlmTimeoutMs
                : RouterLlmTimeoutMs;
        if (_llm is ISourceBackedAgentLlmClient nativeRouterLlm)
        {
            var nativeRouterSw = Stopwatch.StartNew();
            // The structured grid call can legitimately need around 150 output
            // tokens. Qwen may first emit a longer valid tool argument object;
            // truncating it at 256 forces a full repair call and is slower than
            // letting the first call stop naturally.
            const int nativeRouterOutputTokens = 384;
            var nativeRouterTimeoutMs = ResolveNativeRouterTimeoutMs(
                inputTokens: null,
                nativeRouterOutputTokens);
            using var nativeRouterTimeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            nativeRouterTimeoutCts.CancelAfter(nativeRouterTimeoutMs);
            try
            {
                var classifierSw = Stopwatch.StartNew();
                var nativeRouterRuntimePolicy = BuildNativeRouterRuntimePolicy();
                var classifierMessages = new[]
                {
                    SourceBackedAgentMessage.System(
                        BuildNativeRouterClassifierSystemPrompt() + nativeRouterRuntimePolicy),
                    SourceBackedAgentMessage.User(
                        "CHAT_TAIL:\n"
                        + SerializeTail(chatHistory, maxTurns: 2)
                        + "\n\nUSER_MESSAGE:\n"
                        + userMessage)
                };
                var classifierTools = BuildNativeRouterClassifierTools();
                var structuredClassifier = _llm as ISourceBackedAgentStructuredLlmClient;
                var useStructuredClassifier = structuredClassifier is not null
                    && AppSettings.NormalizeActiveMode(_settings?.ActiveMode) == "strict";
                if (useStructuredClassifier)
                    classifierMessages[0] = SourceBackedAgentMessage.System(BuildStructuredRouterFamilyPrompt());
                nativeRouterTimeoutCts.CancelAfter(
                    ResolveNativeRouterTimeoutMs(
                        inputTokens: null,
                        maximumOutputTokens: 64));
                var classifierCompletion = useStructuredClassifier
                    ? await structuredClassifier!.CompleteStructuredAsync(
                            classifierMessages,
                            BuildStructuredRouterFamilyContract(),
                            maxTokens: 64,
                            nativeRouterTimeoutCts.Token,
                            temperatureOverride: 0)
                        .ConfigureAwait(false)
                    : await nativeRouterLlm.CompleteAsync(
                            classifierMessages,
                            classifierTools,
                            maxTokens: 64,
                            nativeRouterTimeoutCts.Token,
                            temperatureOverride: 0,
                            requireToolCall: true)
                        .ConfigureAwait(false);
                var selectedRouteToolName = string.Empty;
                var classifierAccepted = useStructuredClassifier
                    ? TryResolveStructuredRouterFamily(classifierCompletion, out selectedRouteToolName)
                    : classifierCompletion.ToolCalls.Count == 1
                    && TryResolveNativeRouterClassifierSelection(
                        classifierCompletion.ToolCalls[0].Name,
                        out selectedRouteToolName);
                selectedRouteToolName = classifierAccepted
                    ? selectedRouteToolName
                    : string.Empty;
                var useSpecializedRoute = classifierAccepted;
                var useSpecializedGridRoute = useSpecializedRoute
                    && string.Equals(
                        selectedRouteToolName,
                        SubmitSourceBackedGridRouteToolName,
                        StringComparison.Ordinal);
                var useSpecializedDocumentOverviewRoute = useSpecializedRoute
                    && string.Equals(
                        selectedRouteToolName,
                        SubmitDocumentOverviewRouteToolName,
                        StringComparison.Ordinal);
                var nativeRouterMaximumOutputTokens =
                    useSpecializedDocumentOverviewRoute
                        ? NativeDocumentOverviewRouteMaximumOutputTokens
                        : nativeRouterOutputTokens;
                EmitRagTrace(
                    "router.native.classifier.completed",
                    ("accepted", classifierAccepted),
                    ("classifier_contract", useStructuredClassifier ? "saaia_work_family_v1" : "native_tools"),
                    ("selected_contract", selectedRouteToolName),
                    ("finish_reason", classifierCompletion.FinishReason),
                    ("prompt_tokens", classifierCompletion.PromptTokens),
                    ("completion_tokens", classifierCompletion.CompletionTokens),
                    ("server_cache_tokens", classifierCompletion.ServerCacheTokens),
                    ("server_prompt_evaluated_tokens",
                        classifierCompletion.ServerPromptTokensEvaluated),
                    ("server_prompt_ms",
                        classifierCompletion.ServerPromptMilliseconds),
                    ("server_predicted_tokens",
                        classifierCompletion.ServerPredictedTokens),
                    ("server_predicted_ms",
                        classifierCompletion.ServerPredictedMilliseconds),
                    ("ms", classifierSw.ElapsedMilliseconds));
                var nativeRouterSystem = useSpecializedRoute
                    ? BuildNativeRouterSpecializedSystemPrompt(
                        selectedRouteToolName,
                        detectedMessageLanguage,
                        disallowMetaSetLanguage)
                    : BuildNativeRouterSystemPrompt(
                        detectedMessageLanguage,
                        disallowMetaSetLanguage);
                nativeRouterSystem += nativeRouterRuntimePolicy;
                var nativeRouterUser =
                    BuildCompactSourceBackedRouterUserPrompt(
                        chatHistory,
                        userMessage);
                var allNativeRouterTools = BuildNativeRouterTools();
                GroundedAnswerUnits? groundedAnswerUnits = null;
                GroundedGridShape? groundedGridShape = null;
                string? groundedGridEvidenceMode = null;
                string? groundedGridCategoryScope = null;
                if (useStructuredClassifier && selectedRouteToolName == SubmitSourceBackedRouteToolName
                    && CanUseStandaloneAnswerUnits(chatHistory))
                {
                    nativeRouterTimeoutCts.CancelAfter(ResolveNativeRouterTimeoutMs(null, nativeRouterOutputTokens));
                    groundedAnswerUnits = await CompleteGroundedAnswerUnitsAsync(
                        structuredClassifier!, userMessage, nativeRouterOutputTokens, nativeRouterTimeoutCts.Token).ConfigureAwait(false);
                    if (groundedAnswerUnits is not null)
                    {
                        allNativeRouterTools = BindGroundedAnswerUnits(allNativeRouterTools, groundedAnswerUnits);
                        nativeRouterSystem += DescribeGroundedAnswerUnits(groundedAnswerUnits);
                    }
                }
                if (useStructuredClassifier && useSpecializedGridRoute)
                {
                    const int groundedGridShapeOutputTokens = 280;
                    nativeRouterTimeoutCts.CancelAfter(
                        ResolveNativeRouterTimeoutMs(
                            inputTokens: null,
                            groundedGridShapeOutputTokens));
                    groundedGridShape = await CompleteGroundedGridShapeAsync(
                            structuredClassifier!,
                            userMessage,
                            groundedGridShapeOutputTokens,
                            nativeRouterTimeoutCts.Token)
                        .ConfigureAwait(false);
                    if (groundedGridShape?.IsComplete == true)
                    {
                        allNativeRouterTools = BindGroundedGridShape(
                            allNativeRouterTools,
                            groundedGridShape);
                        nativeRouterSystem += DescribeGroundedGridShape(
                            groundedGridShape);
                        if (groundedGridShape.CellCount
                            < AdvancedStructuredAnswerUnitThreshold)
                        {
                            const int groundedGridEvidenceModeOutputTokens = 48;
                            nativeRouterTimeoutCts.CancelAfter(
                                ResolveNativeRouterTimeoutMs(
                                    inputTokens: null,
                                    groundedGridEvidenceModeOutputTokens));
                            groundedGridEvidenceMode =
                                await CompleteGroundedGridEvidenceModeAsync(
                                        nativeRouterLlm,
                                        userMessage,
                                        groundedGridShape,
                                        groundedGridEvidenceModeOutputTokens,
                                        nativeRouterTimeoutCts.Token)
                                    .ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(
                                    groundedGridEvidenceMode))
                            {
                                allNativeRouterTools =
                                    BindGroundedGridEvidenceMode(
                                        allNativeRouterTools,
                                        groundedGridEvidenceMode);
                                nativeRouterSystem +=
                                    DescribeGroundedGridEvidenceMode(
                                        groundedGridEvidenceMode);
                            }

                            var catalogHints = BuildSourceBackedCatalogHints();
                            if (catalogHints.Count > 0)
                            {
                                const int groundedGridCategoryOutputTokens = 64;
                                nativeRouterTimeoutCts.CancelAfter(
                                    ResolveNativeRouterTimeoutMs(
                                        inputTokens: null,
                                        groundedGridCategoryOutputTokens));
                                groundedGridCategoryScope =
                                    await CompleteGroundedGridCategoryScopeAsync(
                                            structuredClassifier!,
                                            userMessage,
                                            groundedGridShape,
                                            catalogHints,
                                            groundedGridCategoryOutputTokens,
                                            nativeRouterTimeoutCts.Token)
                                        .ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(
                                        groundedGridCategoryScope))
                                {
                                    allNativeRouterTools =
                                        BindGroundedGridCategoryScope(
                                            allNativeRouterTools,
                                            groundedGridCategoryScope);
                                    nativeRouterSystem +=
                                        DescribeGroundedGridCategoryScope(
                                            groundedGridCategoryScope);
                                }
                            }
                        }
                    }
                }
                var nativeRouterTools = useSpecializedRoute
                    ? BuildNativeRouterSecondStageTools(
                        allNativeRouterTools,
                        selectedRouteToolName)
                    : allNativeRouterTools;
                if (useSpecializedGridRoute
                    && (groundedGridShape?.IsComplete == true
                        || HasCompleteExplicitStructuredGridAxes(
                            userMessage,
                            detectedMessageLanguage)))
                {
                    nativeRouterTools = nativeRouterTools
                        .Where(tool => !string.Equals(
                            tool.Name,
                            RequestMissingUserInputToolName,
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    EmitRagTrace(
                        "router.grid_complete_axes.missing_input_suppressed",
                        ("reason", groundedGridShape?.IsComplete == true
                            ? "explicit_count_grounded_row_and_column_axes"
                            : "explicit_planning_row_and_column_axes"),
                        ("remaining_tools", nativeRouterTools
                            .Select(static tool => tool.Name)
                            .ToArray()));
                }
                IReadOnlyList<SourceBackedAgentMessage> nativeRouterMessages =
                    new[]
                    {
                        SourceBackedAgentMessage.System(nativeRouterSystem),
                        SourceBackedAgentMessage.User(nativeRouterUser)
                };
                int? nativeRouterInputTokens = null;
                int? nativeRouterContextTokens = null;
                var nativeRouterPromptCompacted = false;
                if (nativeRouterLlm
                    is ISourceBackedAgentInputTokenCounter tokenCounter)
                {
                    nativeRouterInputTokens =
                        await tokenCounter.CountInputTokensAsync(
                                nativeRouterMessages,
                                nativeRouterTools,
                                nativeRouterTimeoutCts.Token,
                                requireToolCall: true)
                            .ConfigureAwait(false);
                    if (nativeRouterLlm
                        is ISourceBackedAgentRuntimeContextProvider
                        runtimeContextProvider)
                    {
                        nativeRouterContextTokens =
                            await runtimeContextProvider
                                .GetRuntimeContextTokensAsync(
                                    nativeRouterTimeoutCts.Token)
                                .ConfigureAwait(false);
                    }

                    nativeRouterContextTokens ??=
                        _settings?.QualifiedProfile?.CtxSize is > 0
                            ? _settings.QualifiedProfile
                                .ResolvePerSlotContextSize()
                            : 4096;
                    const int nativeRouterSafetyTokens = 96;
                    if (nativeRouterInputTokens is { } inputTokens
                        && inputTokens
                           + nativeRouterMaximumOutputTokens
                           + nativeRouterSafetyTokens
                           > nativeRouterContextTokens)
                    {
                        nativeRouterPromptCompacted = true;
                        nativeRouterUser =
                            BuildCompactSourceBackedRouterUserPrompt(
                                chatHistory,
                                userMessage,
                                includeCategoryHints: false);
                        nativeRouterMessages = new[]
                        {
                            SourceBackedAgentMessage.System(nativeRouterSystem),
                            SourceBackedAgentMessage.User(nativeRouterUser)
                        };
                        nativeRouterInputTokens =
                            await tokenCounter.CountInputTokensAsync(
                                    nativeRouterMessages,
                                    nativeRouterTools,
                                    nativeRouterTimeoutCts.Token,
                                    requireToolCall: true)
                                .ConfigureAwait(false);
                        if (nativeRouterInputTokens is { } compactedInputTokens
                            && compactedInputTokens
                               + nativeRouterMaximumOutputTokens
                               + nativeRouterSafetyTokens
                               > nativeRouterContextTokens)
                        {
                            nativeRouterUser =
                                BuildCompactSourceBackedRouterUserPrompt(
                                    Array.Empty<(string role, string content)>(),
                                    userMessage,
                                    includeCategoryHints: false);
                            nativeRouterMessages = new[]
                            {
                                SourceBackedAgentMessage.System(
                                    nativeRouterSystem),
                                SourceBackedAgentMessage.User(
                                    nativeRouterUser)
                            };
                            nativeRouterInputTokens =
                                await tokenCounter.CountInputTokensAsync(
                                        nativeRouterMessages,
                                        nativeRouterTools,
                                        nativeRouterTimeoutCts.Token,
                                        requireToolCall: true)
                                    .ConfigureAwait(false);
                        }
                    }
                }

                nativeRouterTimeoutMs = ResolveNativeRouterTimeoutMs(
                    nativeRouterInputTokens,
                    nativeRouterMaximumOutputTokens);
                nativeRouterTimeoutCts.CancelAfter(nativeRouterTimeoutMs);
                EmitRagTrace(
                    "router.native.start",
                    ("history", chatHistory.Count),
                    ("user_chars", userMessage.Length),
                    ("prompt_chars", nativeRouterUser.Length),
                    ("prompt_tokens", nativeRouterInputTokens),
                    ("context_tokens", nativeRouterContextTokens),
                    ("prompt_compacted", nativeRouterPromptCompacted),
                    ("classifier_contract", selectedRouteToolName),
                    ("classifier_fallback_all_contracts", !useSpecializedRoute),
                    ("timeout_ms", nativeRouterTimeoutMs),
                    ("timeout_profile", "prompt_tokens"));
                var nativeCompletion = await nativeRouterLlm.CompleteAsync(
                        nativeRouterMessages,
                        nativeRouterTools,
                        maxTokens: nativeRouterMaximumOutputTokens,
                        nativeRouterTimeoutCts.Token,
                        temperatureOverride: 0,
                        requireToolCall: true)
                    .ConfigureAwait(false);
                var nativeRouteAccepted = TryBuildNativeRouterPlan(
                        nativeCompletion,
                         userMessage,
                         detectedMessageLanguage,
                         disallowMetaSetLanguage,
                         categoryHintsIncluded:
                             !nativeRouterPromptCompacted
                             || !string.IsNullOrWhiteSpace(
                                 groundedGridCategoryScope),
                         prevalidatedCategoryScope:
                             groundedGridCategoryScope,
                         out var nativePlan,
                         out var nativeFailureReason);
                if (nativeRouteAccepted)
                    nativeRouteAccepted = ValidateGroundedAnswerUnits(groundedAnswerUnits, nativePlan, ref nativeFailureReason);
                if (nativeRouteAccepted)
                    nativeRouteAccepted = ValidateGroundedGridShape(groundedGridShape, nativePlan, ref nativeFailureReason);
                var nativeRouteRepairAttempted = false;
                SourceBackedAgentCompletion? invalidNativeCompletion = null;
                if (!nativeRouteAccepted)
                {
                    nativeRouteRepairAttempted = true;
                    invalidNativeCompletion = nativeCompletion;
                    var useFocusedNamedReferencePairRepair =
                        ShouldUseNativeRouterNamedReferencePairRepair(
                            nativeFailureReason);
                    EmitRagTrace(
                        "router.native.repair_requested",
                        ("reason", nativeFailureReason),
                        ("repair_strategy", useFocusedNamedReferencePairRepair
                            ? "focused_named_reference_pair"
                            : "full_route"),
                        ("finish_reason", nativeCompletion.FinishReason),
                        ("completion_tokens", nativeCompletion.CompletionTokens),
                        ("route_tool", nativeCompletion.ToolCalls.Count == 1
                            ? nativeCompletion.ToolCalls[0].Name
                            : string.Join(
                                ",",
                                nativeCompletion.ToolCalls.Select(
                                    static call => call.Name))),
                        ("route_arguments", nativeCompletion.ToolCalls.Count == 1
                            ? nativeCompletion.ToolCalls[0].Arguments.GetRawText()
                            : JsonSerializer.Serialize(
                                nativeCompletion.ToolCalls.Select(
                                    static call => new
                                    {
                                        name = call.Name,
                                        args = call.Arguments
                                    }))));
                    if (useFocusedNamedReferencePairRepair)
                    {
                        nativeRouterTimeoutCts.CancelAfter(
                            ResolveNativeRouterTimeoutMs(
                                nativeRouterInputTokens,
                                NativeRouterNamedReferenceRepairMaximumOutputTokens));
                        var focusedRepair =
                            await CompleteNativeRouterNamedReferencePairRepairAsync(
                                    nativeRouterLlm,
                                    userMessage,
                                    nativeCompletion,
                                    nativeRouterTimeoutCts.Token)
                                .ConfigureAwait(false);
                        nativeCompletion = focusedRepair ?? nativeCompletion;
                    }
                    else
                    {
                        var previousRoute = nativeCompletion.ToolCalls.Count == 1
                            ? nativeCompletion.ToolCalls[0].Arguments.GetRawText()
                            : JsonSerializer.Serialize(
                                nativeCompletion.ToolCalls.Select(static call => new
                                {
                                    name = call.Name,
                                    args = call.Arguments
                                }));
                        var releaseGridFastPath = useSpecializedGridRoute
                            && string.Equals(
                                nativeFailureReason,
                                "native_source_route_axes_not_grounded",
                                StringComparison.Ordinal);
                        var releaseInvalidDocumentOverview =
                            useSpecializedDocumentOverviewRoute;
                        var releaseInvalidClarification =
                            nativeCompletion.ToolCalls.Count == 1
                            && (string.Equals(
                                    nativeCompletion.ToolCalls[0].Name,
                                    RequestUserClarificationToolName,
                                    StringComparison.OrdinalIgnoreCase)
                                || string.Equals(
                                    nativeCompletion.ToolCalls[0].Name,
                                    RequestMissingUserInputToolName,
                                    StringComparison.OrdinalIgnoreCase))
                            && (string.Equals(
                                    nativeFailureReason,
                                    "native_clarification_route_contract_invalid",
                                    StringComparison.Ordinal)
                                || string.Equals(
                                    nativeFailureReason,
                                    ExplicitDocumentIdentityAlreadySuppliedFailure,
                                    StringComparison.Ordinal));
                        var releaseRouteFamily = releaseGridFastPath
                                                 || releaseInvalidClarification
                                                 || releaseInvalidDocumentOverview;
                        var repairUser = nativeRouterUser
                                         + Environment.NewLine
                                         + Environment.NewLine
                                         + "REPAIR_REQUIRED: "
                                         + nativeFailureReason
                                         + Environment.NewLine
                                         + (releaseInvalidDocumentOverview
                                             ? "The specialized document-overview contract was mechanically invalid. Fall back to the general non-grid source-backed route, preserving the exact document and every requested facet. Call exactly one available route function."
                                             : releaseGridFastPath
                                             ? "The proposed grid axes were not grounded in USER_MESSAGE. Re-evaluate the route family; do not invent axes. Call exactly one available route function."
                                             : releaseInvalidClarification
                                                 ? "The clarification is incompatible with the grounded request contract. Re-evaluate the route family among the remaining functions; do not ask another clarification. Call exactly one available route function."
                                             : "The previous route was mechanically invalid. Preserve every correct axis, explicit user requirement and semantic decision, but repair only the invalid transport fields. Omit document unless the exact filename occurs in USER_MESSAGE. Call exactly one available route function.")
                                         + Environment.NewLine
                                         + "PREVIOUS_INVALID_ROUTE: "
                                         + TruncateForPrompt(previousRoute, 1200);
                        var repairSystem = releaseInvalidDocumentOverview
                            ? BuildNativeRouterSpecializedSystemPrompt(
                                SubmitSourceBackedRouteToolName,
                                detectedMessageLanguage,
                                disallowMetaSetLanguage)
                            : releaseRouteFamily
                            ? BuildNativeRouterSystemPrompt(
                                detectedMessageLanguage,
                                disallowMetaSetLanguage)
                            : nativeRouterSystem;
                        if (releaseInvalidDocumentOverview || releaseRouteFamily)
                            repairSystem += nativeRouterRuntimePolicy;
                        var repairMessages = new[]
                        {
                        SourceBackedAgentMessage.System(repairSystem),
                        SourceBackedAgentMessage.User(repairUser)
                    };
                        var repairTools = releaseInvalidDocumentOverview
                            ? BuildNativeRouterRepairTools(
                                allNativeRouterTools,
                                ResolveNativeRouterRepairRouteToolName(
                                    selectedRouteToolName,
                                    nativeCompletion.ToolCalls.Count == 1
                                        ? nativeCompletion.ToolCalls[0].Name
                                        : string.Empty))
                            : releaseInvalidClarification
                            ? BuildNativeRouterAlternativesAfterInvalidClarification(
                                allNativeRouterTools)
                            : releaseGridFastPath
                                ? allNativeRouterTools
                                : BuildNativeRouterRepairTools(
                                nativeRouterTools,
                                nativeCompletion.ToolCalls.Count == 1
                                    ? nativeCompletion.ToolCalls[0].Name
                                    : string.Empty);
                        var repairOutputTokens = Math.Max(
                            nativeRouterMaximumOutputTokens,
                            nativeRouterOutputTokens);
                        nativeRouterTimeoutCts.CancelAfter(
                            ResolveNativeRouterTimeoutMs(
                                nativeRouterInputTokens,
                                repairOutputTokens));
                        nativeCompletion = await nativeRouterLlm.CompleteAsync(
                                repairMessages,
                                repairTools,
                                maxTokens: repairOutputTokens,
                                nativeRouterTimeoutCts.Token,
                                temperatureOverride: 0,
                                requireToolCall: true)
                            .ConfigureAwait(false);
                    }
                    nativeRouteAccepted = TryBuildNativeRouterPlan(
                        nativeCompletion,
                         userMessage,
                         detectedMessageLanguage,
                         disallowMetaSetLanguage,
                         categoryHintsIncluded:
                             !nativeRouterPromptCompacted
                             || !string.IsNullOrWhiteSpace(
                                 groundedGridCategoryScope),
                         prevalidatedCategoryScope:
                             groundedGridCategoryScope,
                         out nativePlan,
                         out nativeFailureReason);
                    if (nativeRouteAccepted)
                        nativeRouteAccepted = ValidateGroundedAnswerUnits(groundedAnswerUnits, nativePlan, ref nativeFailureReason);
                    if (nativeRouteAccepted)
                        nativeRouteAccepted = ValidateGroundedGridShape(groundedGridShape, nativePlan, ref nativeFailureReason);
                }

                if (nativeRouteAccepted)
                {
                    if (groundedGridShape?.IsComplete == true)
                    {
                        nativePlan.GroundedGridDiscoveryQueries =
                            groundedGridShape.Rows.ToList();
                    }
                    if (invalidNativeCompletion is not null)
                    {
                        PreserveExplicitDocumentAcrossNativeRouterRepair(
                            userMessage,
                            invalidNativeCompletion,
                            nativePlan);
                    }
                    EmitRagTrace(
                        "router.native.accepted",
                        ("repair_attempted", nativeRouteRepairAttempted),
                        ("intent", nativePlan.Intent),
                        ("tools", nativePlan.ToolCalls
                            .Select(static call => call.Name)
                            .ToArray()),
                        ("has_source_mission",
                            nativePlan.SourceBackedMission is not null),
                        ("source_plan_kind",
                            nativePlan.SourceBackedMission?.PlanKind),
                        ("source_goal",
                            nativePlan.SourceBackedMission?.Deliverable),
                        ("source_proof",
                            nativePlan.SourceBackedMission?.AtomicEvidenceType),
                        ("source_question_focus",
                            nativePlan.SourceBackedMission?.QuestionFocus),
                        ("source_uses_focused_document",
                            nativePlan.SourceBackedMission?.UsesFocusedDocument),
                        ("source_atomic_evidence_count",
                            nativePlan.SourceBackedMission?.AtomicEvidenceCount),
                        ("source_row_header",
                            nativePlan.SourceBackedMission?.RowHeader),
                        ("source_rows",
                            nativePlan.SourceBackedMission?.RowLabels),
                        ("source_columns",
                            nativePlan.SourceBackedMission?.Columns),
                        ("source_requested_document",
                            nativePlan.SourceBackedMission?.RequestedDocumentName),
                        ("source_named_reference_kind",
                            nativePlan.SourceBackedMission?.NamedReferenceKind),
                        ("source_candidate_scopes",
                            nativePlan.SourceBackedMission?.CandidateScopePaths),
                        ("initial_actions",
                            nativePlan.ToolCalls
                                .Select(static call => new
                                {
                                    name = call.Name,
                                    args = call.Args.GetRawText()
                                })
                                .ToArray()),
                        ("prompt_tokens", nativeCompletion.PromptTokens),
                        ("completion_tokens",
                            nativeCompletion.CompletionTokens),
                        ("server_cache_tokens",
                            nativeCompletion.ServerCacheTokens),
                        ("server_prompt_evaluated_tokens",
                            nativeCompletion.ServerPromptTokensEvaluated),
                        ("server_prompt_ms",
                            nativeCompletion.ServerPromptMilliseconds),
                        ("server_predicted_tokens",
                            nativeCompletion.ServerPredictedTokens),
                        ("server_predicted_ms",
                            nativeCompletion.ServerPredictedMilliseconds),
                        ("ms", nativeRouterSw.ElapsedMilliseconds));
                    ClientLog.Info(
                        "ToolAgent native router accepted: "
                        + $"intent={nativePlan.Intent}|tools={string.Join(",", nativePlan.ToolCalls.Select(static call => call.Name))}|sourceMission={nativePlan.SourceBackedMission is not null}|ms={nativeRouterSw.ElapsedMilliseconds}");
                    return nativePlan;
                }

                EmitRagTrace(
                    "router.native.rejected",
                    ("reason", nativeFailureReason),
                    ("finish_reason", nativeCompletion.FinishReason),
                    ("completion_tokens", nativeCompletion.CompletionTokens),
                    ("route_tool", nativeCompletion.ToolCalls.Count == 1
                        ? nativeCompletion.ToolCalls[0].Name
                        : string.Join(
                            ",",
                            nativeCompletion.ToolCalls.Select(
                                static call => call.Name))),
                    ("route_arguments", nativeCompletion.ToolCalls.Count == 1
                        ? nativeCompletion.ToolCalls[0].Arguments.GetRawText()
                        : JsonSerializer.Serialize(
                            nativeCompletion.ToolCalls.Select(
                                static call => new
                                {
                                    name = call.Name,
                                    args = call.Arguments
                                }))),
                    ("ms", nativeRouterSw.ElapsedMilliseconds));
                ClientLog.Warn(
                    "ToolAgent native router rejected: "
                    + $"reason={nativeFailureReason}|ms={nativeRouterSw.ElapsedMilliseconds}");
                return new RouterPlan
                {
                    Mode = "auto",
                    Language = detectedMessageLanguage,
                    Intent = "chat.general",
                    Origin = RouterPlanOrigin.LocalFallback
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                EmitRagTrace(
                    "router.native.fallback",
                    ("reason", "llm_timeout"),
                    ("timeout_ms", nativeRouterTimeoutMs),
                    ("ms", nativeRouterSw.ElapsedMilliseconds));
                return new RouterPlan
                {
                    Mode = "auto",
                    Language = detectedMessageLanguage,
                    Intent = "chat.general",
                    Origin = RouterPlanOrigin.LocalFallback
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SourceBackedLlmBudgetExceededException)
            {
                var budgetSnapshot =
                    SourceBackedLlmCumulativeBudgetContext.Current?
                        .GetSnapshot();
                EmitRagTrace(
                    "router.native.cumulative_budget_stopped",
                    ("reason", budgetSnapshot?.LastAdmissionReason
                               ?? "cumulative_budget_stopped"),
                    ("charged_tokens", budgetSnapshot?.ChargedTokens),
                    ("reserved_tokens", budgetSnapshot?.ReservedTokens),
                    ("remaining_tokens", budgetSnapshot?.RemainingTokens),
                    ("remaining_ms", budgetSnapshot?.RemainingMilliseconds),
                    ("ms", nativeRouterSw.ElapsedMilliseconds));
                throw;
            }
            catch (Exception ex)
            {
                EmitRagTrace(
                    "router.native.fallback",
                    ("reason", "llm_error"),
                    ("error", TruncateForPrompt(ex.Message, 260)),
                    ("ms", nativeRouterSw.ElapsedMilliseconds));
                return new RouterPlan
                {
                    Mode = "auto",
                    Language = detectedMessageLanguage,
                    Intent = "chat.general",
                    Origin = RouterPlanOrigin.LocalFallback
                };
            }
        }

        try
        {
            EmitRagTrace(
                "router.llm.start",
                ("disallow_meta_set_language", disallowMetaSetLanguage),
                ("compact_source_router", useCompactSourceBackedRouter),
                ("history", chatHistory.Count),
                ("user_chars", userMessage.Length),
                ("prompt_chars", system.Length + user.Length));
            ClientLog.Info(
                "ToolAgent router llm request: " +
                $"disallowMetaSetLanguage={disallowMetaSetLanguage}|compactSourceRouter={useCompactSourceBackedRouter}|history={chatHistory.Count}|userChars={userMessage.Length}|promptChars={(system.Length + user.Length)}");
            using var routerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            routerTimeoutCts.CancelAfter(routerTimeoutMs);
            var routerMessages = new[]
            {
                ("system", system),
                ("user", user)
            };
            raw = _llm.SupportsStructuredOutput
                ? await CompleteStructuredWithRetryAsync(
                        routerMessages,
                        BuildRouterStructuredOutputContract(),
                        routerTimeoutCts.Token)
                    .ConfigureAwait(false)
                : await CompleteWithRetryAsync(
                        routerMessages,
                        forceJson: true,
                        routerTimeoutCts.Token)
                    .ConfigureAwait(false);
            EmitRagTrace(
                "router.llm.end",
                ("disallow_meta_set_language", disallowMetaSetLanguage),
                ("compact_source_router", useCompactSourceBackedRouter),
                ("raw_chars", raw?.Length ?? 0),
                ("ms", sw.ElapsedMilliseconds));
            ClientLog.Info(
                "ToolAgent router llm response: " +
                $"rawChars={raw?.Length ?? 0}|ms={sw.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            EmitRagTrace(
                "router.llm.fallback",
                ("reason", "llm_timeout"),
                ("disallow_meta_set_language", disallowMetaSetLanguage),
                ("compact_source_router", useCompactSourceBackedRouter),
                ("timeout_ms", routerTimeoutMs),
                ("ms", sw.ElapsedMilliseconds));
            ClientLog.Info(
                "ToolAgent router fallback: " +
                $"reason=llm_timeout|timeoutMs={routerTimeoutMs}|ms={sw.ElapsedMilliseconds}");
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general", Origin = RouterPlanOrigin.LocalFallback };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitRagTrace(
                "router.llm.fallback",
                ("reason", "llm_error"),
                ("disallow_meta_set_language", disallowMetaSetLanguage),
                ("compact_source_router", useCompactSourceBackedRouter),
                ("ms", sw.ElapsedMilliseconds),
                ("error", TruncateForPrompt(ex.Message, 260)));
            ClientLog.Info(
                "ToolAgent router fallback: " +
                $"reason=llm_error|ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 260)}");
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general", Origin = RouterPlanOrigin.LocalFallback };
        }

        if (!TryExtractJsonObject(raw ?? string.Empty, out var planJson))
        {
            // Fallback safe: conversationnel sans tools
            EmitRagTrace(
                "router.llm.no_json",
                ("raw_chars", raw?.Length ?? 0),
                ("ms", sw.ElapsedMilliseconds),
                ("preview", TruncateForPrompt(raw, 260)));
            ClientLog.Info(
                "ToolAgent router fallback: " +
                $"reason=no_json|rawChars={raw?.Length ?? 0}|ms={sw.ElapsedMilliseconds}|preview={TruncateForPrompt(raw, 260)}");
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general", Origin = RouterPlanOrigin.LocalFallback };
        }

        try
        {
            var sanitized = ParseAndSanitizeLlmRouterPlanJson(planJson, detectedMessageLanguage, disallowMetaSetLanguage);
            ClientLog.Info(
                "ToolAgent router parsed: " +
                $"intent={sanitized.Intent}|mode={sanitized.Mode}|lang={sanitized.Language}|tools={sanitized.ToolCalls.Count}|" +
                $"toolNames={string.Join(",", sanitized.ToolCalls.Select(x => x.Name))}|clarification={sanitized.NeedClarification}|ms={sw.ElapsedMilliseconds}");
            return sanitized;
        }
        catch (Exception ex)
        {
            if (TryRepairJsonObjectForParsing(planJson, out var repairedPlanJson)
                && !string.Equals(planJson, repairedPlanJson, StringComparison.Ordinal))
            {
                try
                {
                    var sanitized = ParseAndSanitizeLlmRouterPlanJson(repairedPlanJson, detectedMessageLanguage, disallowMetaSetLanguage);
                    EmitRagTrace(
                        "router.llm.parse_repaired",
                        ("ms", sw.ElapsedMilliseconds),
                        ("error", TruncateForPrompt(ex.Message, 180)),
                        ("original_json", TruncateForPrompt(planJson, 220)),
                        ("repaired_json", TruncateForPrompt(repairedPlanJson, 220)));
                    ClientLog.Info(
                        "ToolAgent router parsed after json repair: " +
                        $"intent={sanitized.Intent}|mode={sanitized.Mode}|lang={sanitized.Language}|tools={sanitized.ToolCalls.Count}|" +
                        $"toolNames={string.Join(",", sanitized.ToolCalls.Select(x => x.Name))}|clarification={sanitized.NeedClarification}|ms={sw.ElapsedMilliseconds}");
                    return sanitized;
                }
                catch (Exception repairEx)
                {
                    EmitRagTrace(
                        "router.llm.parse_repair_failed",
                        ("ms", sw.ElapsedMilliseconds),
                        ("error", TruncateForPrompt(ex.Message, 180)),
                        ("repair_error", TruncateForPrompt(repairEx.Message, 180)),
                        ("json", TruncateForPrompt(planJson, 220)));
                    ClientLog.Info(
                        "ToolAgent router json repair failed: " +
                        $"ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 180)}|repairError={TruncateForPrompt(repairEx.Message, 180)}");
                }
            }

            EmitRagTrace(
                "router.llm.parse_error",
                ("ms", sw.ElapsedMilliseconds),
                ("error", TruncateForPrompt(ex.Message, 260)),
                ("json", TruncateForPrompt(planJson, 260)));
            ClientLog.Info(
                "ToolAgent router fallback: " +
                $"reason=parse_error|ms={sw.ElapsedMilliseconds}|error={TruncateForPrompt(ex.Message, 260)}|json={TruncateForPrompt(planJson, 260)}");
            return new RouterPlan { Mode = "auto", Language = detectedMessageLanguage, Intent = "chat.general", Origin = RouterPlanOrigin.LocalFallback };
        }
    }

}
