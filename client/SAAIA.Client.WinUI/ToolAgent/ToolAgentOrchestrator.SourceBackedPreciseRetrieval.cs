using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool RagResultContainsUsableRequestedTitle(JsonElement ragResult, string requestedTitle)
    {
        if (!HasRagHits(ragResult) || string.IsNullOrWhiteSpace(requestedTitle))
            return false;

        var requestedPdfFile = requestedTitle.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = ragResult
        });

        return EnumerateRagHitSummaries(toolResults)
            .Any(hit => (RagHitContainsRequestedTitle(hit, requestedTitle)
                    || (requestedPdfFile && CandidateMatchesDocumentIdentity(requestedTitle, hit.DocName, hit.DocPath))
                    || RagHitHasUsableRequestedTitleAnchor(requestedTitle, hit))
                && !LooksLikeExactItemReferenceOnlyHit(requestedTitle, hit));
    }

    private static bool RagResultContainsExplicitDocumentHit(JsonElement ragResult, string requestedDocument)
    {
        if (!HasRagHits(ragResult) || string.IsNullOrWhiteSpace(requestedDocument))
            return false;

        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = ragResult
        });

        return EnumerateRagHitSummaries(toolResults)
            .Any(hit => CandidateMatchesDocumentIdentity(requestedDocument, hit.DocName, hit.DocPath));
    }

    private static string[] BuildPreciseRetrievalQueries(string exactTitle, string retrievalQuery, string? originalQuery = null)
    {
        var title = CollapseWhitespace(exactTitle);
        var combined = CollapseWhitespace(retrievalQuery);
        var original = CollapseWhitespace(originalQuery ?? string.Empty);

        if (string.IsNullOrWhiteSpace(title))
        {
            var fallbackQueries = new List<string>();
            AddDistinctQuery(fallbackQueries, combined);
            if (!string.IsNullOrWhiteSpace(original)
                && !string.Equals(original, combined, StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctQuery(fallbackQueries, original);
            }

            return fallbackQueries.ToArray();
        }

        var quotedTitle = QuoteLookupTitle(title);
        var queries = new List<string>();
        AddDistinctQuery(queries, title);
        AddDistinctQuery(queries, quotedTitle);
        foreach (var variant in BuildTypoTolerantQueryVariants(title))
        {
            AddDistinctQuery(queries, variant);
            AddDistinctQuery(queries, QuoteLookupTitle(variant));
        }

        foreach (var topic in ExtractDelimitedNonFileTopics(original).Take(3))
        {
            AddDistinctQuery(queries, $"{title} {topic}");
            AddDistinctQuery(queries, topic);
        }

        if (LooksLikeItemLocationLookupRequest(combined))
        {
            AddDistinctQuery(queries, $"{title} source");
            AddDistinctQuery(queries, $"{title} document");
            AddDistinctQuery(queries, $"{title} livre");
            AddDistinctQuery(queries, $"{title} reference");
            return queries.Take(8).ToArray();
        }

        if (LooksLikeStructuredItemCardRequest(combined))
        {
            AddDistinctQuery(queries, $"{title} details");
            AddDistinctQuery(queries, $"{title} procedure");
            AddDistinctQuery(queries, $"{title} quantities");
            AddDistinctQuery(queries, $"{title} timing");
            AddDistinctQuery(queries, $"{title} source");
        }

        if (!string.IsNullOrWhiteSpace(combined) && !string.Equals(title, combined, StringComparison.OrdinalIgnoreCase))
            AddDistinctQuery(queries, combined);

        if (!string.IsNullOrWhiteSpace(original)
            && !string.Equals(original, title, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(original, combined, StringComparison.OrdinalIgnoreCase))
        {
            AddDistinctQuery(queries, original);
        }

        return queries.Take(8).ToArray();
    }

    private static IEnumerable<string> ExtractDelimitedNonFileTopics(string? query)
    {
        var s = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(s))
            yield break;

        foreach (Match match in Regex.Matches(
                     s,
                     """(?:\u00ab|\u201c|"|`)(?<topic>.{3,180}?)(?:\u00bb|\u201d|"|`)""",
                     RegexOptions.CultureInvariant))
        {
            var topic = CollapseWhitespace(match.Groups["topic"].Value);
            if (string.IsNullOrWhiteSpace(topic)
                || Regex.IsMatch(topic, @"\.(?:pdf|docx?|xlsx?|pptx?|md|txt|csv)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            yield return topic;
        }
    }

    private static string QuoteLookupTitle(string title)
        => "\"" + title.Replace("\"", string.Empty, StringComparison.Ordinal).Trim() + "\"";

    private static bool LooksLikeItemLocationLookupRequest(string? query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:dans\s+quel(?:le)?\s+(?:livre|document|pdf|fichier|source)|quel(?:le)?\s+(?:livre|document|pdf|fichier|source)|where\s+(?:is|can\s+i\s+find)|which\s+(?:book|document|pdf|file|source)|en\s+que\s+(?:libro|documento|pdf|archivo|fuente)|em\s+que\s+(?:livro|documento|pdf|ficheiro|fonte)|in\s+welchem\s+(?:buch|dokument|pdf|datei)|in\s+quale\s+(?:libro|documento|pdf|file|fonte))\b",
            RegexOptions.CultureInvariant);
    }
}
