#if DEBUG || SAAIA_TEST_HOOKS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    internal Task<RouterPlan> RouteOnlyForTests(
        string userMessage,
        CancellationToken ct)
        => RouterAsync(
            Array.Empty<(string role, string content)>(),
            userMessage,
            ct,
            disallowMetaSetLanguage: false);

    internal async Task<RouterPlan> RouteOnlyWithCatalogForTests(
        string userMessage,
        CancellationToken ct)
    {
        await EnsureCatalogSnapshotCacheAsync(ct).ConfigureAwait(false);
        return await RouterAsync(
                Array.Empty<(string role, string content)>(),
                userMessage,
                ct,
                disallowMetaSetLanguage: false)
            .ConfigureAwait(false);
    }

    internal static bool LooksLikeLowStructureShortProcedureTextForTests(string text)
        => LooksLikeLowStructureShortProcedureHit(new RagHitSummary(
            TestHookDocPath,
            TestHookDocName,
            1,
            1,
            text));

    internal static bool LooksLikeLowSignalContentCandidateForTests(string excerpt, int pageStart = 1, string? fullText = null)
        => LooksLikeLowSignalContentCandidateHit(new RagHitSummary(
            TestHookDocPath,
            TestHookDocName,
            pageStart,
            pageStart,
            excerpt,
            FullText: fullText));

    internal static string SerializeTailForTests(IReadOnlyList<(string role, string content)> history, int maxTurns)
        => SerializeTail(history, maxTurns);

    internal static string BuildCompactSourceBackedRouterUserPromptForTests(
        ToolMemory memory,
        IReadOnlyList<(string role, string content)> history,
        string userMessage)
    {
        var sut = new ToolAgentOrchestrator(
            api: null!,
            llm: null!,
            mem: memory);
        return sut.BuildCompactSourceBackedRouterUserPrompt(
            history,
            userMessage);
    }

    internal static (bool Accepted, RouterPlan Plan, string FailureReason)
        TryBuildNativeRouterPlanForTests(
            ToolMemory memory,
            string sourceRequest,
            string toolName,
            string argumentsJson,
            bool categoryHintsIncluded = true)
    {
        var sut = new ToolAgentOrchestrator(
            api: null!,
            llm: null!,
            mem: memory);
        using var arguments = JsonDocument.Parse(argumentsJson);
        var completion = new SourceBackedAgentCompletion(
            string.Empty,
            new[]
            {
                new SourceBackedAgentToolCall(
                    "native-route-test",
                    toolName,
                    arguments.RootElement.Clone())
            },
            "tool_calls");
        var accepted = sut.TryBuildNativeRouterPlan(
            completion,
            sourceRequest,
            "fr",
            disallowMetaSetLanguage: false,
            categoryHintsIncluded: categoryHintsIncluded,
            prevalidatedCategoryScope: null,
            out var plan,
            out var failureReason);
        return (accepted, plan, failureReason);
    }

    internal static (bool Accepted, RouterPlan Plan, string FailureReason)
        TryBuildNativeRouterPlanForCallsForTests(
            ToolMemory memory,
            string sourceRequest,
            IReadOnlyList<(string ToolName, string ArgumentsJson)> calls)
    {
        var sut = new ToolAgentOrchestrator(
            api: null!,
            llm: null!,
            mem: memory);
        var toolCalls = new List<SourceBackedAgentToolCall>(calls.Count);
        for (var index = 0; index < calls.Count; index++)
        {
            using var arguments = JsonDocument.Parse(
                calls[index].ArgumentsJson);
            toolCalls.Add(new SourceBackedAgentToolCall(
                "native-route-test-" + (index + 1),
                calls[index].ToolName,
                arguments.RootElement.Clone()));
        }

        var completion = new SourceBackedAgentCompletion(
            string.Empty,
            toolCalls,
            "tool_calls");
        var accepted = sut.TryBuildNativeRouterPlan(
            completion,
            sourceRequest,
            "fr",
            disallowMetaSetLanguage: false,
            categoryHintsIncluded: true,
            prevalidatedCategoryScope: null,
            out var plan,
            out var failureReason);
        return (accepted, plan, failureReason);
    }

    internal static (string EffectiveUserMessage, bool Consumed) PreparePendingClarificationForTests(
        ToolMemory mem,
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var prepared = sut.PrepareUserMessageForPendingClarification(chatHistory, userMessage);
        return (prepared.EffectiveUserMessage, prepared.Consumed);
    }

    internal static string RenderRouterClarificationForTests(RouterPlan plan)
        => RenderRouterClarification(plan);

    internal static string NormalizeRagQueryForTests(string query)
        => NormalizeRagQueryForRetrieval(query);

    internal static string ResolveSourceBackedFallbackIntentQueryForTests(string query)
        => ResolveSourceBackedFallbackIntentQuery(query);

    internal static string ResolveRagSearchExecutionQueryForTests(string query)
        => ResolveRagSearchExecutionQuery(query);

    internal static bool LooksLikeStandaloneDocumentaryTopicForTests(string query)
        => LooksLikeStandaloneDocumentaryTopic(query);

    internal static bool LooksLikeSourceBackedActionRequestForTests(string query)
        => LooksLikeSourceBackedActionRequest(query);

    internal static bool LooksLikeStructuredItemCardRequestForTests(string query)
        => LooksLikeStructuredItemCardRequest(query);

    internal static bool LooksLikeSourceAbsentAssertionPolicyRequestForTests(string query)
        => LooksLikeSourceAbsentAssertionPolicyRequest(query);

    internal static bool ShouldAttachSourceAnchorForSourceAbsentAssertionPolicyRequestForTests(string query)
        => ShouldAttachSourceAnchorForSourceAbsentAssertionPolicyRequest(query);

    internal static bool LooksLikeBinaryAnswerWithSourceUncertaintyRequestForTests(string query)
        => LooksLikeBinaryAnswerWithSourceUncertaintyRequest(query);

    internal static bool LooksLikeSourceBackedPlanningRequestForTests(string query)
        => LooksLikeSourceBackedPlanningRequest(query);

    internal static bool LooksLikeDocumentContentSelectionExplanationRequestForTests(string query)
        => LooksLikeDocumentContentSelectionExplanationRequest(query);

    internal static bool LooksLikeUnresolvedSourceBackedDeicticFollowupForTests(string query)
        => LooksLikeUnresolvedSourceBackedDeicticFollowup(query);

    internal static string? TryExtractRequestedItemTitleForTests(string query)
        => TryExtractRequestedItemTitle(query);

    internal static string? TryExtractPdfFileNameRequestedTitleForTests(string query)
        => TryExtractPdfFileNameRequestedTitle(query);

    internal static IReadOnlyList<string> ExtractExplicitDocumentFileReferenceQueriesForTests(string query)
        => ExtractExplicitDocumentFileReferenceQueries(query);

    internal static string? ResolveSourceBackedRequestedDocumentNameForTests(
        string effectiveUserMessage,
        RouterPlan routerPlan)
        => ResolveSourceBackedRequestedDocumentName(
            effectiveUserMessage,
            routerPlan);

    internal static string? ResolveSourceBackedNamedReferenceKindForTests(
        string effectiveUserMessage,
        RouterPlan routerPlan)
        => ResolveSourceBackedNamedReferenceKind(
            effectiveUserMessage,
            routerPlan);

    internal static bool LooksLikeNoRagDataAnswerForTests(string answer)
        => LooksLikeNoRagDataAnswer(answer);

    internal static bool ShouldFallbackFromNoRagDataAnswerForTests(string answer)
        => ShouldFallbackFromNoRagDataAnswer(answer);

    internal static string TryBuildNoRagEvidenceAnswerForTests(ToolResults toolResults, string language, string query)
        => TryBuildNoRagEvidenceAnswerForEmptySearch(toolResults, language, query);

    internal static bool ShouldReplaceOverPromotedSourceBackedOptionAnswerForTests(
        string answer,
        ToolResults toolResults,
        string query)
        => ShouldReplaceOverPromotedSourceBackedOptionAnswer(answer, toolResults, query);

    internal static bool LooksLikeMissingExactItemWithoutSourceLeadsForTests(string answer)
        => LooksLikeMissingExactItemWithoutSourceLeads(answer);

    internal static bool LooksLikeDegenerateLlmOutputForTests(string answer)
        => LooksLikeDegenerateLlmOutput(answer);

    internal static string[] BuildPlanningRetrievalQueriesForTests(string query)
        => BuildPlanningRetrievalQueries(query);

    internal static string[] BuildSourceBackedActionRetrievalQueriesForTests(string query)
        => BuildSourceBackedActionRetrievalQueries(query);

    internal static int NormalizeSourceBackedActionTopKForTests(int? requestedTopK, string query)
        => NormalizeSourceBackedActionTopK(requestedTopK, query);

    internal static int ResolveSourceBackedActionRetrievalQueryLimitForTests(string query)
        => ResolveSourceBackedActionRetrievalQueryLimit(query);

    internal static int ResolveRagMultiSearchQueryBudgetForTests(
        int topK,
        int availableQueries,
        string? researchMode = null,
        bool includeResearchSurfaces = false)
        => ResolveRagMultiSearchQueryBudget(topK, availableQueries, researchMode, includeResearchSurfaces);

    internal static TimeSpan ResolveRagSourceExplorationQueryTimeoutForTests()
        => RagSourceExplorationQueryTimeout;

    internal static IDisposable OverrideRagSourceExplorationQueryTimeoutForTests(TimeSpan timeout)
    {
        var previous = RagSourceExplorationQueryTimeoutOverrideForTests;
        RagSourceExplorationQueryTimeoutOverrideForTests = timeout;
        return new TestHookScope(() => RagSourceExplorationQueryTimeoutOverrideForTests = previous);
    }

    private sealed class TestHookScope(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            dispose();
        }
    }

    internal static string[] BuildPreciseRetrievalQueriesForTests(string exactTitle, string retrievalQuery, string? originalQuery = null)
        => BuildPreciseRetrievalQueries(exactTitle, retrievalQuery, originalQuery);

    internal static bool ShouldTryPreciseMultiSearchForExactItemForTests(
        JsonElement ragResult,
        string query,
        string exactItemTitle,
        string? requestedExplicitDocument = null,
        bool isCompactTechnicalExactItem = false,
        bool isDocumentVersionTraceabilityRequest = false)
        => ShouldTryPreciseMultiSearchForExactItem(
            ragResult,
            query,
            exactItemTitle,
            requestedExplicitDocument,
            isCompactTechnicalExactItem,
            isDocumentVersionTraceabilityRequest);

    internal static bool LooksLikeComparativeDocumentaryRequestForTests(string query)
        => LooksLikeComparativeDocumentaryRequest(query);

    internal static string[] BuildComparativeRetrievalQueriesForTests(string query)
        => BuildComparativeRetrievalQueries(query);

    internal static int CountExplicitDocumentFileReferencesForTests(string query)
        => CountExplicitDocumentFileReferences(query);

    internal static bool ShouldRunDocumentaryProbeForTests(string query, RouterPlan plan)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        return sut.ShouldRunDocumentaryProbe(query, plan);
    }

    internal static bool ShouldForceRagForStandaloneTopicForTests(string query, RouterPlan plan)
        => ShouldForceRagForStandaloneTopic(query, plan);

    internal static (bool Matched, string Topic) TryExtractDocumentContentSearchTopicForTests(string query)
    {
        var matched = TryExtractDocumentContentSearchTopic(query, out var topic);
        return (matched, topic);
    }

    internal static bool LooksLikeExactDocumentPassageLocalizationRequestForTests(string query)
        => LooksLikeExactDocumentPassageLocalizationRequest(query);

    internal static string BuildDocumentContentSearchAnswerForTests(ToolResults toolResults, string topic, string language)
        => BuildDocumentContentSearchAnswer(toolResults, topic, language);

    internal static string FilterDocumentContentSearchResultForTests(string json, string topic)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return FilterDocumentContentSearchResult(doc.RootElement, topic).GetRawText();
    }

    internal static string? ResolveKnownDocumentContentSearchCategoryScopeForTests(
        string query,
        params string[] knownCategoryPaths)
    {
        var memory = new ToolMemory
        {
            CatalogSnapshotCache = new ToolMemory.RuntimeCatalogSnapshot
            {
                Categories = knownCategoryPaths
                    .Select(path => new ToolMemory.CategorySnapshot
                    {
                        CategoryPath = path,
                        DisplayName = path.Split('/', '\\').Last()
                    })
                    .ToList()
            }
        };
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: memory);
        return sut.ResolveKnownDocumentContentSearchCategoryScope(query);
    }

    internal static bool ShouldExpandDocumentContentSearchForTests(string query, string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return ShouldExpandDocumentContentSearch(query, doc.RootElement);
    }

    internal static bool ShouldUseWriterForDocumentContentSearchAnswerForTests(string query, string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return ShouldUseWriterForDocumentContentSearchAnswer(query, doc.RootElement);
    }

    internal static bool IsBetterDocumentContentSearchCoverageForTests(string currentJson, string candidateJson)
    {
        using var current = JsonDocument.Parse(string.IsNullOrWhiteSpace(currentJson) ? "{}" : currentJson);
        using var candidate = JsonDocument.Parse(string.IsNullOrWhiteSpace(candidateJson) ? "{}" : candidateJson);
        return IsBetterDocumentContentSearchCoverage(current.RootElement, candidate.RootElement);
    }
}
#endif
