using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record PaginationContinuationOption(
        string RouteId,
        RetrievalRequest Request);

    private static void AppendPendingPaginationContinuations(
        StringBuilder builder,
        IReadOnlyList<RetrievalRequest> executedRequests,
        int maximumCount,
        int queryCharacters,
        int documentCharacters,
        bool constrainedContext)
    {
        var pending = BuildPaginationContinuationOptions(
            executedRequests,
            maximumCount);
        if (pending.Count == 0)
            return;

        builder.AppendLine("PAGINATIONS ENCORE DISPONIBLES (possibilites, jamais obligations):");
        foreach (var option in pending)
        {
            var request = option.Request;
            builder.Append("- ").Append(request.ToolName);
            if (!string.IsNullOrWhiteSpace(request.Query))
            {
                builder.Append(" | requete=")
                    .Append(TrimPromptValue(request.Query, queryCharacters));
            }
            if (!string.IsNullOrWhiteSpace(request.CategoryPath))
            {
                builder.Append(" | categorie=")
                    .Append(TrimPromptValue(
                        request.CategoryPath,
                        constrainedContext ? 48 : 100));
            }
            if (!string.IsNullOrWhiteSpace(request.DocPath)
                || !string.IsNullOrWhiteSpace(request.DocRef))
            {
                builder.Append(" | document=")
                    .Append(TrimPromptValue(
                        request.DocPath ?? request.DocRef,
                        documentCharacters));
            }
            if (request.Limit is > 0)
                builder.Append(" | limite=").Append(request.Limit);
            builder.Append(" | prochain_offset_exact=")
                .Append(request.NextOffset)
                .Append(" | route_id=")
                .Append(option.RouteId)
                .AppendLine();
        }
    }

    private static IReadOnlyList<PaginationContinuationOption>
        BuildPaginationContinuationOptions(
            IReadOnlyList<RetrievalRequest> executedRequests,
            int maximumCount)
        => SelectPendingPaginationContinuations(executedRequests, maximumCount)
            .Select(static (request, index) => new PaginationContinuationOption(
                "P" + (index + 1).ToString(CultureInfo.InvariantCulture),
                request))
            .ToArray();

    private static IReadOnlyList<RetrievalRequest> SelectPendingPaginationContinuations(
        IReadOnlyList<RetrievalRequest> executedRequests,
        int maximumCount)
    {
        if (maximumCount <= 0 || executedRequests.Count == 0)
            return Array.Empty<RetrievalRequest>();

        var pending = new List<(RetrievalRequest Request, int Index)>();
        for (var index = 0; index < executedRequests.Count; index++)
        {
            var request = executedRequests[index];
            if (request.NextOffset is not >= 0)
                continue;

            var continuationWasConsumed = executedRequests
                .Skip(index + 1)
                .Any(candidate =>
                    candidate.Offset == request.NextOffset
                    && SamePaginationRoute(request, candidate));
            if (!continuationWasConsumed)
                pending.Add((request, index));
        }

        return pending
            .GroupBy(
                static entry => BuildPaginationRouteKey(entry.Request),
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.MaxBy(static entry => entry.Index))
            .OrderBy(static entry => string.IsNullOrWhiteSpace(entry.Request.Query) ? 0 : 1)
            .ThenByDescending(static entry => entry.Index)
            .Take(maximumCount)
            .Select(static entry => entry.Request)
            .ToArray();
    }

    private static bool SamePaginationRoute(
        RetrievalRequest left,
        RetrievalRequest right)
        => string.Equals(
            BuildPaginationRouteKey(left),
            BuildPaginationRouteKey(right),
            StringComparison.OrdinalIgnoreCase);

    private static string BuildPaginationRouteKey(RetrievalRequest request)
    {
        if (request.ToolArguments is JsonElement arguments
            && arguments.ValueKind == JsonValueKind.Object)
        {
            var canonicalArguments = arguments
                .EnumerateObject()
                .Where(static property => !string.Equals(
                    property.Name,
                    "offset",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(static property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static property => property.Name.ToLowerInvariant()
                    + "=" + property.Value.GetRawText());
            return request.ToolName.Trim() + "|" + string.Join("&", canonicalArguments);
        }

        return string.Join(
            "|",
            request.ToolName.Trim(),
            request.Query.Trim(),
            request.CategoryPath?.Trim() ?? string.Empty,
            request.DocId?.Trim() ?? string.Empty,
            request.DocPath?.Trim() ?? string.Empty,
            request.DocRef?.Trim() ?? string.Empty,
            request.PageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.PageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static bool TryBuildPaginationContinuationCall(
        string routeId,
        IReadOnlyList<RetrievalRequest> executedRequests,
        int maximumCount,
        string callId,
        out SourceBackedAgentToolCall call,
        out string error)
    {
        call = default!;
        error = string.Empty;
        var option = BuildPaginationContinuationOptions(
                executedRequests,
                maximumCount)
            .FirstOrDefault(candidate => string.Equals(
                candidate.RouteId,
                routeId,
                StringComparison.OrdinalIgnoreCase));
        if (option is null || option.Request.NextOffset is not >= 0)
        {
            error = "pagination_transition_route_invalid";
            return false;
        }
        if (option.Request.ToolArguments is not JsonElement arguments
            || arguments.ValueKind != JsonValueKind.Object)
        {
            error = "pagination_transition_route_not_replayable";
            return false;
        }

        var replay = arguments
            .EnumerateObject()
            .Where(static property => !string.Equals(
                property.Name,
                "offset",
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.Ordinal);
        replay["offset"] = JsonSerializer.SerializeToElement(
            option.Request.NextOffset.Value);
        call = new SourceBackedAgentToolCall(
            callId,
            SourceBackedAgentToolCatalog.ToExternalName(
                option.Request.ToolName),
            JsonSerializer.SerializeToElement(replay, ClientJson.CamelCase));
        return true;
    }

    private static string DescribePaginationContinuationOptions(
        IReadOnlyList<RetrievalRequest> executedRequests,
        int maximumCount)
    {
        var options = BuildPaginationContinuationOptions(
            executedRequests,
            maximumCount);
        if (options.Count == 0)
            return "Aucune route paginee n'est actuellement disponible.";

        return "Routes paginees disponibles: " + string.Join(
            " ; ",
            options.Select(static option =>
            {
                var request = option.Request;
                return option.RouteId
                       + "=" + SourceBackedAgentToolCatalog.ToExternalName(
                           request.ToolName)
                       + "(requete="
                       + (string.IsNullOrWhiteSpace(request.Query)
                           ? "vide"
                           : TrimPromptValue(request.Query, 70))
                       + ", categorie="
                       + TrimPromptValue(request.CategoryPath, 48)
                       + ", offset_suivant=" + request.NextOffset
                       + ", limite=" + request.Limit
                       + ", rendement=" + request.NewEvidenceCount
                       + "/" + request.MaterializedEvidenceCount
                       + ").";
            }));
    }

    private static string TrimPromptValue(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maximum ? normalized : normalized[..maximum].TrimEnd() + "…";
    }

    private static string TrimPromptBlock(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        return normalized.Length <= maximum
            ? normalized
            : normalized[..maximum].TrimEnd() + "…";
    }
}
