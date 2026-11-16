using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static partial class SourceBackedAcronymDefinitionProbe
{
    internal static bool TryExtractAcronym(string? question, out string acronym)
    {
        acronym = string.Empty;
        if (string.IsNullOrWhiteSpace(question) || question.Length > 160)
            return false;

        // A longer question may supply a document or domain that the broad
        // acronym probe cannot use. Leave those questions to the agent.
        var match = DirectDefinitionRegex().Match(question.Replace('’', '\''));
        if (!match.Success)
            return false;

        acronym = match.Groups["acronym"].Value;
        return true;
    }

    // This is a narrow, mechanical absence check. Dense similarity alone can
    // return unrelated passages for an unknown abbreviation. A lexical hit,
    // a literal uppercase mention, or a degraded search leaves the decision
    // to the normal source-backed agent.
    internal static bool HasNoReliableAcronymAnchor(JsonElement searchResponse, string acronym)
    {
        if (searchResponse.ValueKind != JsonValueKind.Object
            || !searchResponse.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || !searchResponse.TryGetProperty("metrics", out var metrics)
            || metrics.ValueKind != JsonValueKind.Object
            || !TryGetNonnegativeCount(metrics, "exactMatchReturned", out var exact)
            || !TryGetNonnegativeCount(metrics, "sparseReturned", out var sparse)
            || exact > 0 || sparse > 0)
            return false;

        if (metrics.TryGetProperty("degradedRetrievers", out var degraded)
            && degraded.ValueKind == JsonValueKind.Array
            && degraded.GetArrayLength() > 0)
            return false;

        var literal = new Regex(
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(acronym)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.CultureInvariant);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var key in new[] { "text", "snippet", "contextualSnippet",
                         "docName", "sectionTitle", "headingPath" })
            {
                if (item.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && literal.IsMatch(value.GetString() ?? string.Empty))
                    return false;
            }
        }

        return true;
    }

    private static bool TryGetNonnegativeCount(JsonElement metrics, string key, out int count)
    {
        count = 0;
        return metrics.TryGetProperty(key, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out count)
               && count >= 0;
    }

    [GeneratedRegex(@"^\s*(?i:qu'est[\s-]*ce\s+qu(?:e|'(?:un|une))|c'est\s+quoi|que\s+signifie|que\s+veut\s+dire|définition\s+de|definition\s+de|définis|what\s+is|define)\s+(?:(?i:un|une|le|la|l')\s*)?(?<acronym>[A-Z]{2,8})\s*[?.!]*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DirectDefinitionRegex();
}
