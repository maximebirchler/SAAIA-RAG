using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static class SourceBackedRouterPlanAdapter
{
    public static RouterPlan ToRouterPlan(SourceBackedIntake intake, RetrievalPlan plan)
    {
        var scopedPlan = SourceBackedRetrievalScope.ApplyDefaultCategoryScope(intake, plan).Plan;
        return new RouterPlan
        {
            Mode = "auto",
            Language = NormalizeLanguage(intake.Language),
            Intent = "rag.answer",
            ResponseFormat = "auto",
            Origin = RouterPlanOrigin.Llm,
            NeedClarification = scopedPlan.NeedsClarification,
            ReasoningTracePublic = BuildReasoningTrace(scopedPlan),
            ToolCalls = BuildToolCalls(intake, scopedPlan.Requests)
        };
    }

    private static List<RouterPlan.ToolCall> BuildToolCalls(
        SourceBackedIntake intake,
        IReadOnlyList<RetrievalRequest> requests)
    {
        var calls = new List<RouterPlan.ToolCall>();
        for (var index = 0; index < requests.Count; index++)
        {
            var request = PreserveSpecificDocumentNavigationAnchor(intake, requests[index]);
            if (!string.Equals(NormalizeToolName(request.ToolName), "rag.multi_search", StringComparison.OrdinalIgnoreCase))
            {
                var call = ToToolCall(intake, request);
                if (call is not null)
                    calls.Add(call);
                continue;
            }

            var compatible = new List<RetrievalRequest> { request };
            while (index + 1 < requests.Count)
            {
                var next = PreserveSpecificDocumentNavigationAnchor(intake, requests[index + 1]);
                if (!CanBatchMultiSearchRequests(request, next))
                    break;

                compatible.Add(next);
                index++;
            }

            calls.Add(new RouterPlan.ToolCall
            {
                Name = "rag.multi_search",
                Args = BuildMultiSearchArgs(intake, compatible)
            });
        }

        return calls;
    }

    private static bool CanBatchMultiSearchRequests(RetrievalRequest first, RetrievalRequest candidate)
        => string.Equals(NormalizeToolName(candidate.ToolName), "rag.multi_search", StringComparison.OrdinalIgnoreCase)
           && string.Equals(NullIfBlank(first.CategoryPath), NullIfBlank(candidate.CategoryPath), StringComparison.OrdinalIgnoreCase)
           && string.Equals(NullIfBlank(first.DocId), NullIfBlank(candidate.DocId), StringComparison.OrdinalIgnoreCase)
           && string.Equals(NullIfBlank(first.DocPath), NullIfBlank(candidate.DocPath), StringComparison.OrdinalIgnoreCase)
           && first.PageStart == candidate.PageStart
           && first.PageEnd == candidate.PageEnd;

    private static RouterPlan.ToolCall? ToToolCall(SourceBackedIntake intake, RetrievalRequest request)
    {
        request = PreserveSpecificDocumentNavigationAnchor(intake, request);
        var toolName = NormalizeToolName(request.ToolName);
        if (!IsAllowedSourceBackedTool(toolName))
            return null;

        var args = toolName switch
        {
            "rag.multi_search" => BuildMultiSearchArgs(intake, new[] { request }),
            "rag.search" => BuildSearchArgs(intake, request),
            "documents.navigation" => BuildDocumentsNavigationArgs(request),
            "documents.context" => BuildDocumentsContextArgs(request),
            _ => BuildGenericArgs(request)
        };

        return new RouterPlan.ToolCall
        {
            Name = toolName,
            Args = args
        };
    }

    private static RetrievalRequest PreserveSpecificDocumentNavigationAnchor(
        SourceBackedIntake intake,
        RetrievalRequest request)
    {
        if (!string.Equals(NormalizeToolName(request.ToolName), "documents.navigation", StringComparison.OrdinalIgnoreCase))
            return request;

        if (!string.IsNullOrWhiteSpace(request.Query)
            || !string.IsNullOrWhiteSpace(request.DocRef)
            || !string.IsNullOrWhiteSpace(request.DocId)
            || !string.IsNullOrWhiteSpace(request.DocPath))
        {
            return request;
        }

        var requestedDocument = SourceBackedQuestionFocus.ExtractFirstSpecificDocumentName(intake);
        return string.IsNullOrWhiteSpace(requestedDocument)
            ? request
            : request with
            {
                Query = requestedDocument,
                DocRef = requestedDocument
            };
    }

    private static bool IsAllowedSourceBackedTool(string toolName)
        => ToolManifest.IsKnownTool(toolName)
           && !ToolManifest.IsAdminTool(toolName)
           && toolName is "rag.search" or "rag.multi_search" or "documents.navigation" or "documents.context";

    private static JsonElement BuildMultiSearchArgs(
        SourceBackedIntake intake,
        IReadOnlyList<RetrievalRequest> requests)
    {
        var first = requests[0];
        return ToJsonElement(new
        {
            queries = requests
                .SelectMany(static request =>
                    request.QueryVariants is { Count: > 0 }
                        ? request.QueryVariants
                        : new[] { request.Query })
                .Select(static query => query?.Trim())
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToArray(),
            topK = ResolveSourceBackedTopK(intake),
            categoryPath = NullIfBlank(first.CategoryPath),
            docId = NullIfBlank(first.DocId),
            docPath = NullIfBlank(first.DocPath),
            pageStart = first.PageStart,
            pageEnd = first.PageEnd,
            mode = "broad",
            researchMode = "source_exploration",
            includeResearchSurfaces = true,
            sourceBackedCanonical = true,
            disableAutomaticCategoryScoping = true,
            trustCategoryScope = !string.IsNullOrWhiteSpace(first.CategoryPath)
        });
    }

    private static JsonElement BuildSearchArgs(
        SourceBackedIntake intake,
        RetrievalRequest request)
        => ToJsonElement(new
        {
            query = request.Query,
            topK = ResolveSourceBackedTopK(intake),
            categoryPath = NullIfBlank(request.CategoryPath),
            docId = NullIfBlank(request.DocId),
            docPath = NullIfBlank(request.DocPath),
            pageStart = request.PageStart,
            pageEnd = request.PageEnd,
            mode = "balanced",
            researchMode = "source_exploration",
            includeResearchSurfaces = true,
            sourceBackedCanonical = true,
            disableAutomaticCategoryScoping = true,
            trustCategoryScope = !string.IsNullOrWhiteSpace(request.CategoryPath)
        });

    private static int ResolveSourceBackedTopK(SourceBackedIntake intake)
    {
        if (!SourceBackedCanonicalContentCardInventory.IsEnabled(intake))
            return 12;

        var requestedCellCount = SourceContractVerifier.CountRequestedStructuredTableCells(intake);
        return Math.Clamp(requestedCellCount, 12, 20);
    }

    private static JsonElement BuildDocumentsNavigationArgs(RetrievalRequest request)
        => ToJsonElement(new
        {
            path = NullIfBlank(request.CategoryPath),
            categoryPath = NullIfBlank(request.CategoryPath),
            docRef = NullIfBlank(request.DocRef),
            docId = NullIfBlank(request.DocId),
            docPath = NullIfBlank(request.DocPath),
            q = NullIfBlank(request.Query),
            limit = request.Limit,
            offset = request.Offset
        });

    private static JsonElement BuildDocumentsContextArgs(RetrievalRequest request)
        => ToJsonElement(new
        {
            docRef = NullIfBlank(request.DocRef),
            docId = NullIfBlank(request.DocId),
            docPath = NullIfBlank(request.DocPath),
            chunkId = NullIfBlank(request.ChunkId),
            pageStart = request.PageStart,
            pageEnd = request.PageEnd,
            limit = request.Limit,
            offset = request.Offset
        });

    private static JsonElement BuildGenericArgs(RetrievalRequest request)
        => ToJsonElement(new
        {
            query = request.Query,
            categoryPath = NullIfBlank(request.CategoryPath)
        });

    private static List<string> BuildReasoningTrace(RetrievalPlan plan)
        => string.IsNullOrWhiteSpace(plan.PlannerReasoningSummary)
            ? new List<string>()
            : new List<string> { plan.PlannerReasoningSummary };

    private static JsonElement ToJsonElement(object payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();

    private static string NormalizeLanguage(string? language)
        => string.IsNullOrWhiteSpace(language) ? "fr" : language.Trim().ToLowerInvariant();

    private static string NormalizeToolName(string? toolName)
        => string.IsNullOrWhiteSpace(toolName) ? string.Empty : toolName.Trim().ToLowerInvariant();

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
