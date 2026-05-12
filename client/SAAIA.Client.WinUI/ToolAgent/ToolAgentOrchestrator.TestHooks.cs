#if DEBUG
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const string TestHookDocPath = "TestFixtures/reference-document.pdf";
    private const string TestHookDocName = "reference-document.pdf";

    internal static string NormalizeRouterIntentForTests(string? intent)
        => NormalizeRouterIntent(intent);

    internal static string InferIntentFromToolCallsForTests(params RouterPlan.ToolCall[] toolCalls)
        => InferIntentFromToolCalls(toolCalls) ?? string.Empty;

    internal static string DetectMessageLanguageForTests(string? message)
        => DetectMessageLanguage(message);

    internal static string ResolveTurnLanguageForTests(string userMessage, string? routerLanguage, string interactionLanguage)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        return sut.ResolveTurnLanguage(userMessage, routerLanguage, interactionLanguage);
    }

    internal static RouterPlan.ToolCall[] SanitizeToolCallsForTests(params RouterPlan.ToolCall[] toolCalls)
        => SanitizeToolCalls(toolCalls).ToArray();

    internal static RouterPlan ApplyDocumentaryRagDefaultsForTests(RouterPlan plan, string userMessage)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        sut.ApplyDocumentaryRagDefaults(plan, userMessage);
        return plan;
    }

    internal static RouterPlan ApplySourceBackedClarificationOverrideForTests(RouterPlan plan, string userMessage)
    {
        ApplySourceBackedClarificationOverride(plan, userMessage);
        return plan;
    }

    internal static JsonElement NormalizeToolArgsForTests(string toolName, string jsonArgs)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(jsonArgs) ? "{}" : jsonArgs);
        return NormalizeToolArgs(toolName, doc.RootElement);
    }

    internal static string ResolveAdminSummarySubmitDocLanguageForTests(string jsonArgs)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(jsonArgs) ? "{}" : jsonArgs);
        return ResolveAdminSummarySubmitDocLanguage(doc.RootElement);
    }

    internal static JsonElement NormalizeRagHitsForTests(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return NormalizeRagHits(doc.RootElement);
    }

    internal static string ClassifyRagHitRoleForTests(string json, string? query = null)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return ClassifyRagHitEvidenceProfile(BuildRagHitSummary(doc.RootElement), query).Role;
    }

    internal static string SerializeWriterRagResultsForTests(string toolName, string json, string userMessage)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = doc.RootElement.Clone()
        });

        var plan = new RouterPlan
        {
            Intent = "rag.answer",
            Language = "fr",
            Mode = "strict"
        };

        return SerializeToolResults(BuildWriterToolResults(plan, toolResults, userMessage));
    }

    internal static string BuildProbeRagToolResultsJsonForTests(IReadOnlyList<RagItem> hits)
    {
        var toolResults = BuildProbeRagToolResults(hits);
        return JsonSerializer.Serialize(toolResults.Items.Single().Result);
    }

    internal static bool ShouldUseSourceBackedExtractiveAnswerForTests(string query, ToolResults toolResults)
        => ShouldUseSourceBackedExtractiveAnswer(query, toolResults);

    internal static string BuildSourceBackedExtractiveAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedExtractiveAnswer(toolResults, query, language);

    internal static string[] DeriveSourceBackedExtractiveSourceLabelsForTests(ToolResults toolResults, string query)
        => DeriveSourcesFromExtractiveHits(toolResults, query).Select(source => source.Label).ToArray();

    internal static string BuildSourceBackedExtractiveSourcesPayloadForTests(ToolResults toolResults, string query)
        => JsonSerializer.Serialize(BuildSourcesPayload(DeriveSourcesFromExtractiveHits(toolResults, query)));

    internal static string BuildRagSearchSourcesPayloadForTests(string json, string toolName = "rag.search")
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = doc.RootElement.Clone()
        });

        return JsonSerializer.Serialize(BuildSourcesPayload(DeriveSourcesFromRagHits(toolResults)));
    }

    internal static string BuildSummarySearchSourcesPayloadForTests(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "summary.search",
            Result = doc.RootElement.Clone()
        });

        return JsonSerializer.Serialize(BuildSourcesPayload(DeriveSourcesFromSummarySearch(toolResults)));
    }

    internal static string BuildSourceResolveSourcesPayloadForTests(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "sources.resolve",
            Result = doc.RootElement.Clone()
        });

        var source = TryBuildSourceFromResolveResult(toolResults);
        return JsonSerializer.Serialize(BuildSourcesPayload(source is null ? [] : [source]));
    }

    internal static string BuildEnrichedSourceResolveSourcesPayloadForTests(
        ToolMemory mem,
        string rawRef,
        string backendRef,
        string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (!doc.RootElement.TryGetProperty("source", out var sourceEl) || sourceEl.ValueKind != JsonValueKind.Object)
            return JsonSerializer.Serialize(BuildSourcesPayload([]));

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var enriched = sut.EnrichSourceResolveResult(rawRef, backendRef, doc.RootElement.Clone(), sourceEl.Clone());
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "sources.resolve",
            Result = enriched
        });

        var source = TryBuildSourceFromResolveResult(toolResults);
        return JsonSerializer.Serialize(BuildSourcesPayload(source is null ? [] : [source]));
    }

    internal static string BuildLocalSourceResolveFallbackPayloadForTests(ToolMemory mem, string sourceRef)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var source = sut.ResolveSourceRef(sourceRef);
        return JsonSerializer.Serialize(BuildSourcesPayload(source is null ? [] : [source]));
    }

    internal static string BuildSummarySourcesPayloadForTests(string toolName, string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = doc.RootElement.Clone()
        });

        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        var (_, sourcesPayload, _, _, _) = sut.TryBuildSummaryAnswer(toolResults);
        return JsonSerializer.Serialize(sourcesPayload);
    }

    internal static string BuildSummaryRetrievalQueryForTests(string docName, string strategy, string language, string level)
        => BuildSummaryRetrievalQuery(
            new ResolvedDocRef("doc-1", TestHookDocPath, docName, null, null, null),
            strategy,
            language,
            level);

    internal static string BuildSummaryRetrievalQueryWithSourceCardsForTests(
        string docName,
        string strategy,
        string language,
        string level,
        string? categoryPath,
        params string[] cardTitles)
        => BuildSummaryRetrievalQuery(
            new ResolvedDocRef("doc-1", TestHookDocPath, docName, null, categoryPath, null),
            strategy,
            language,
            level,
            new ToolMemory.SourceRef
            {
                DocPath = TestHookDocPath,
                Label = docName,
                CategoryPath = categoryPath,
                MatchedContentCards = cardTitles
                    .Where(static title => !string.IsNullOrWhiteSpace(title))
                    .Select(static title => new ToolMemory.SourceContentCardRef
                    {
                        Title = title.Trim()
                    })
                    .ToList()
            });

    internal static string BuildLiveSummaryFallbackSourcePayloadForTests(
        string docId,
        string docPath,
        string docName,
        string? categoryPath,
        int? pages,
        string? categoryRef,
        string? sourceHash,
        string? docLanguage,
        string? profileLanguage,
        string? category = null)
    {
        var source = BuildLiveSummaryFallbackSourceMetadata(new ResolvedDocRef(
            docId,
            docPath,
            docName,
            Category: category,
            categoryPath,
            pages,
            categoryRef,
            sourceHash,
            docLanguage,
            profileLanguage));

        return JsonSerializer.Serialize(BuildSourcesPayload([source]));
    }

    internal async Task<(bool Queued, string? JobId, string? Error)> TryQueueAdminSummaryGenerationForTests(
        string docRef,
        CancellationToken ct)
    {
        var outcome = await TryQueueAdminSummaryGenerationAsync(docRef, ct).ConfigureAwait(false);
        return (outcome.Queued, outcome.JobId, outcome.Error);
    }

    internal Task<JsonElement> ExecuteSourcesResolveForTests(string sourceRef, CancellationToken ct)
        => ExecSourcesResolveV2Async(CreateJsonArgs(new { @ref = sourceRef }), ct);

    internal static string BuildSourceBackedExtractiveHeaderForTests(string language, bool noExplicitPairing)
        => BuildSourceBackedExtractiveHeader(language, noExplicitPairing);

    internal static string BuildSourceBackedPlanningAnswerForTests(ToolResults toolResults, string language, string? query = null)
        => BuildSourceBackedPlanningAnswer(toolResults, language, query: query);

    internal static string ExtractPlanItemTitleV2ForTests(string text)
        => ExtractPlanItemTitleV2(text);

    internal static bool ExactItemTextMatchesRequestOrStructureForTests(string requestedTitle, string text)
        => ExactItemTextMatchesRequestOrStructure(requestedTitle, text);

    internal static string TrimAfterLikelyExactItemBoundaryForTests(string text)
        => TrimAfterLikelyExactItemBoundary(text);

    internal static string BuildSourceBackedPlanningOrExtractiveAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedPlanningOrExtractiveAnswer(toolResults, query, language);

    internal static string BuildSourceBackedOptionAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedOptionAnswer(toolResults, language, query: query);

    internal static string TryBuildMissingBroadCompositionAnchorAnswerForTests(ToolResults toolResults, string query, string language)
        => TryBuildMissingBroadCompositionAnchorAnswer(toolResults, query, language);

    internal static string TryBuildBackendGuidanceClarificationAnswerForTests(ToolResults toolResults, string language)
        => TryBuildBackendGuidanceClarificationAnswer(toolResults, language);

    internal static string BuildRagEvidenceFallbackAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildRagEvidenceFallbackAnswer(toolResults, query, language);

    internal static string TryBuildSourcePolicyGuardAnswerForTests(ToolResults toolResults, string query, string language)
        => TryBuildSourcePolicyGuardAnswer(toolResults, query, language);

    internal static string BuildSourcePolicyRetrievalQueryForTests(string query)
        => BuildSourcePolicyRetrievalQuery(query);

    internal static string[] DeriveSourceBackedOptionSourceLabelsForTests(ToolResults toolResults, string query)
        => DeriveSourcesFromOptionHits(toolResults, query).Select(source => source.Label).ToArray();

    internal static bool LooksLikeSourceBackedOptionRequestForTests(string query)
        => LooksLikeSourceBackedOptionRequest(query);

    internal static bool LooksLikeSourceBackedCountdownPlanningRequestForTests(string query)
        => LooksLikeSourceBackedCountdownPlanningRequest(query);

    internal static string BuildSourceBackedCountdownPlanningAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedCountdownPlanningAnswer(toolResults, query, language);

    internal static string[] DeriveSourceBackedCountdownSourceLabelsForTests(ToolResults toolResults, string query)
        => DeriveSourcesFromCountdownPlanningHits(toolResults, query).Select(source => source.Label).ToArray();

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

    internal static (string EffectiveUserMessage, bool Consumed) PreparePendingClarificationForTests(
        ToolMemory mem,
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: mem);
        var prepared = sut.PrepareUserMessageForPendingClarification(chatHistory, userMessage);
        return (prepared.EffectiveUserMessage, prepared.Consumed);
    }

    internal static string NormalizeRagQueryForTests(string query)
        => NormalizeRagQueryForRetrieval(query);

    internal static bool LooksLikeStandaloneDocumentaryTopicForTests(string query)
        => LooksLikeStandaloneDocumentaryTopic(query);

    internal static bool LooksLikeSourceBackedActionRequestForTests(string query)
        => LooksLikeSourceBackedActionRequest(query);

    internal static bool LooksLikeSourceBackedPlanningRequestForTests(string query)
        => LooksLikeSourceBackedPlanningRequest(query);

    internal static string? TryExtractRequestedItemTitleForTests(string query)
        => TryExtractRequestedItemTitle(query);

    internal static bool LooksLikeNoRagDataAnswerForTests(string answer)
        => LooksLikeNoRagDataAnswer(answer);

    internal static bool LooksLikeMissingExactItemWithoutSourceLeadsForTests(string answer)
        => LooksLikeMissingExactItemWithoutSourceLeads(answer);

    internal static bool LooksLikeDegenerateLlmOutputForTests(string answer)
        => LooksLikeDegenerateLlmOutput(answer);

    internal static string[] BuildPlanningRetrievalQueriesForTests(string query)
        => BuildPlanningRetrievalQueries(query);

    internal static string[] BuildSourceBackedActionRetrievalQueriesForTests(string query)
        => BuildSourceBackedActionRetrievalQueries(query);

    internal static bool LooksLikeComparativeDocumentaryRequestForTests(string query)
        => LooksLikeComparativeDocumentaryRequest(query);

    internal static string[] BuildComparativeRetrievalQueriesForTests(string query)
        => BuildComparativeRetrievalQueries(query);

    internal static bool ShouldRunDocumentaryProbeForTests(string query, RouterPlan plan)
    {
        var sut = new ToolAgentOrchestrator(api: null!, llm: null!, mem: new ToolMemory());
        return sut.ShouldRunDocumentaryProbe(query, plan);
    }

    internal static (bool Matched, string Topic) TryExtractDocumentContentSearchTopicForTests(string query)
    {
        var matched = TryExtractDocumentContentSearchTopic(query, out var topic);
        return (matched, topic);
    }

    internal static string BuildDocumentContentSearchAnswerForTests(ToolResults toolResults, string topic, string language)
        => BuildDocumentContentSearchAnswer(toolResults, topic, language);
}
#endif
