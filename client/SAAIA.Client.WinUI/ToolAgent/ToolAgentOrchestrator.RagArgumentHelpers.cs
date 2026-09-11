using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string? TryGetStringArg(JsonElement args, string name)
    {
        try
        {
            return args.ValueKind == JsonValueKind.Object ? GetStringArg(args, name) : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetIntArg(JsonElement args, string name)
    {
        try
        {
            return args.ValueKind == JsonValueKind.Object ? GetIntArg(args, name) : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> TryGetStringArrayArg(JsonElement args, string name)
    {
        var values = new List<string>();
        try
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(name, out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return values;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var rawValue = item.GetString();
                var value = LooksLikeQuotedLookupQuery(rawValue)
                    ? CollapseWhitespace(rawValue ?? string.Empty)
                    : NormalizeRagQueryForRetrieval(rawValue);
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }
        }
        catch
        {
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool LooksLikeQuotedLookupQuery(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        return Regex.IsMatch(
            s,
            "^[\\u00ab\\u201c\"]([^\\u00bb\\u201d\"]{3,90})[\\u00bb\\u201d\"]$",
            RegexOptions.CultureInvariant);
    }

    private static string[] NormalizeRagMultiSearchQueries(JsonElement args)
    {
        var queries = new List<string>();
        foreach (var query in GetStringArrayArg(args, "queries") ?? new List<string>())
        {
            AddDistinctRagQuery(queries, query);
            if (LooksLikeComparativeDocumentaryRequest(query))
            {
                foreach (var expanded in BuildComparativeRetrievalQueries(query))
                    AddDistinctRagQuery(queries, expanded);
            }
        }

        var singleQuery = GetStringArg(args, "query");
        if (!string.IsNullOrWhiteSpace(singleQuery))
        {
            AddDistinctRagQuery(queries, singleQuery);
            if (LooksLikeComparativeDocumentaryRequest(singleQuery))
            {
                foreach (var expanded in BuildComparativeRetrievalQueries(singleQuery))
                    AddDistinctRagQuery(queries, expanded);
            }
        }

        return queries.Take(8).ToArray();
    }

    private static string[] NormalizeEvidenceOverviewFacets(JsonElement args)
        => (GetStringArrayArg(args, "overviewFacets") ?? new List<string>())
            .Select(static facet => CollapseWhitespace(facet))
            .Where(static facet => facet.Length is > 0 and <= 180)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumEvidenceOverviewPointCount)
            .ToArray();

    private static void AddDistinctRagQuery(List<string> queries, string? query)
    {
        var value = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!queries.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            queries.Add(value);
    }
}
