using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    internal static string NormalizeRouterIntentForTests(string? intent)
        => NormalizeRouterIntent(intent);

    internal static string InferIntentFromToolCallsForTests(params RouterPlan.ToolCall[] toolCalls)
        => InferIntentFromToolCalls(toolCalls) ?? string.Empty;

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

    internal static JsonElement NormalizeRagHitsForTests(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return NormalizeRagHits(doc.RootElement);
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

    internal static bool ShouldUseSourceBackedExtractiveAnswerForTests(string query, ToolResults toolResults)
        => ShouldUseSourceBackedExtractiveAnswer(query, toolResults);

    internal static string BuildSourceBackedExtractiveAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedExtractiveAnswer(toolResults, query, language);

    internal static string[] DeriveSourceBackedExtractiveSourceLabelsForTests(ToolResults toolResults, string query)
        => DeriveSourcesFromExtractiveHits(toolResults, query).Select(source => source.Label).ToArray();

    internal static string BuildSourceBackedExtractiveHeaderForTests(string language, bool noExplicitPairing)
        => BuildSourceBackedExtractiveHeader(language, noExplicitPairing);

    internal static string BuildSourceBackedPlanningAnswerForTests(ToolResults toolResults, string language)
        => BuildSourceBackedPlanningAnswer(toolResults, language);

    internal static string BuildSourceBackedPlanningOrExtractiveAnswerForTests(ToolResults toolResults, string query, string language)
        => BuildSourceBackedPlanningOrExtractiveAnswer(toolResults, query, language);

    internal static bool LooksLikeColdAssemblyProcedureTextForTests(string text)
        => LooksLikeColdAssemblyProcedureHit(new RagHitSummary(
            "test.pdf",
            "test.pdf",
            1,
            1,
            text));

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

    internal static bool LooksLikeDegenerateLlmOutputForTests(string answer)
        => LooksLikeDegenerateLlmOutput(answer);

    internal static string[] BuildPlanningRetrievalQueriesForTests(string query)
        => BuildPlanningRetrievalQueries(query);

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
}
