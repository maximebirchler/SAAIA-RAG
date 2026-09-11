using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static List<SourceBackedInitialToolCall>
        CoalesceIdenticalInitialResearchRoutes(
            IReadOnlyList<SourceBackedInitialToolCall> actions)
    {
        var coalesced = new List<SourceBackedInitialToolCall>(actions.Count);
        var indexesByRoute = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            var routeIdentity = BuildInitialResearchRouteIdentity(action);
            if (!indexesByRoute.TryGetValue(routeIdentity, out var existingIndex))
            {
                indexesByRoute[routeIdentity] = coalesced.Count;
                coalesced.Add(action);
                continue;
            }

            var existing = coalesced[existingIndex];
            var existingLimit = ReadInitialResearchLimit(existing.Arguments);
            var duplicateLimit = ReadInitialResearchLimit(action.Arguments);
            coalesced[existingIndex] = existing with
            {
                Arguments = ReplaceInitialResearchLimit(
                    existing.Arguments,
                    checked(existingLimit + duplicateLimit))
            };
        }

        return coalesced;
    }

    private static string BuildInitialResearchRouteIdentity(
        SourceBackedInitialToolCall action)
        => action.ToolName.ToUpperInvariant()
           + "|"
           + string.Join(
               "|",
               action.Arguments
                   .EnumerateObject()
                   .Where(static property => property.Name is not (
                       "limit" or "topK"))
                   .OrderBy(
                       static property => property.Name,
                       StringComparer.OrdinalIgnoreCase)
                   .Select(static property =>
                       property.Name.ToUpperInvariant()
                       + "="
                       + property.Value.GetRawText()));

    private static int ReadInitialResearchLimit(JsonElement arguments)
        => TryGetInteger(arguments, "limit", out var limit)
            ? limit
            : TryGetInteger(arguments, "topK", out var topK)
                ? topK
                : 1;

    private static JsonElement ReplaceInitialResearchLimit(
        JsonElement arguments,
        int limit)
    {
        var normalized = arguments
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        var limitName = normalized.ContainsKey("limit") ? "limit" : "topK";
        normalized[limitName] = JsonSerializer.SerializeToElement(limit);
        return JsonSerializer.SerializeToElement(
            normalized,
            ClientJson.CamelCase);
    }

    private static List<SourceBackedInitialToolCall>
        NormalizeInitialResearchAggregateLimits(
            IReadOnlyList<SourceBackedInitialToolCall> actions,
            int maximumAggregateLimit)
    {
        var originalLimits = actions
            .Select(static action =>
                TryGetInteger(action.Arguments, "limit", out var limit)
                    ? limit
                    : TryGetInteger(action.Arguments, "topK", out var topK)
                        ? topK
                        : 1)
            .ToArray();
        var originalTotal = originalLimits.Sum();
        if (originalTotal <= maximumAggregateLimit)
            return actions.ToList();

        var scaledLimits = originalLimits
            .Select(limit => Math.Max(
                1,
                (int)Math.Floor(
                    (double)limit * maximumAggregateLimit / originalTotal)))
            .ToArray();
        var remaining = maximumAggregateLimit - scaledLimits.Sum();
        for (var index = 0; remaining > 0; index = (index + 1) % actions.Count)
        {
            if (scaledLimits[index] >= originalLimits[index])
                continue;
            scaledLimits[index]++;
            remaining--;
        }

        return actions.Select((action, index) =>
        {
            var arguments = action.Arguments
                .EnumerateObject()
                .ToDictionary(
                    static property => property.Name,
                    static property => property.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase);
            var limitName = arguments.ContainsKey("limit") ? "limit" : "topK";
            arguments[limitName] = JsonSerializer.SerializeToElement(
                scaledLimits[index]);
            return action with
            {
                Arguments = JsonSerializer.SerializeToElement(
                    arguments,
                    ClientJson.CamelCase)
            };
        }).ToList();
    }

    private static JsonElement ApplySharedInitialResearchScope(
        JsonElement action,
        string sharedScope)
    {
        if (action.ValueKind != JsonValueKind.Object)
            return action;

        var normalized = action.EnumerateObject()
            .Where(static property => !string.Equals(
                property.Name,
                "scope",
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.Ordinal);
        normalized["scope"] = JsonSerializer.SerializeToElement(sharedScope);
        return JsonSerializer.SerializeToElement(
            normalized,
            ClientJson.CamelCase);
    }

    private static bool HasGroundedInitialDocumentContextPointer(
        JsonElement arguments)
        => TryGetPropertyIgnoreCase(arguments, "docRef", out var document)
           && document.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(document.GetString());
}
