using System.Text.RegularExpressions;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalText;

namespace SAAIA.Backend.Retrieval;

internal static class RequestedStructuredEvidence
{
    private static readonly HashSet<string> FieldHeads = new(StringComparer.Ordinal)
    {
        "propriete", "proprietes", "property", "properties", "caracteristique", "caracteristiques",
        "characteristic", "characteristics", "condition", "conditions", "critere", "criteres",
        "criterion", "criteria", "exigence", "exigences", "requirement", "requirements",
        "limite", "limites", "limit", "limits", "seuil", "seuils", "threshold", "thresholds"
    };

    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal)
    {
        "de", "d", "du", "des", "l", "la", "le", "les", "of", "the", "for"
    };

    internal static IReadOnlyList<string> Resolve(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var requestedText = TryExtractDelimitedUserDemandTopic(query, out var topic) ? topic : query;
        var normalized = NormalizeForLexicalSignal(requestedText);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 2 or > 12)
            return [];

        var phrases = new HashSet<string>(TranslateExplicitPropertyPhrases(normalized), StringComparer.Ordinal);
        var meaningful = ExtractLexicalQueryTokens(normalized).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!FieldHeads.Contains(tokens[i]))
                continue;

            if (i > 0 && IsAttribute(tokens[i - 1]))
                phrases.Add($"{tokens[i - 1]} {tokens[i]}");

            var next = i + 1;
            while (next < tokens.Length && next <= i + 3 && Connectors.Contains(tokens[next]))
                next++;
            if (next < tokens.Length && next <= i + 3 && IsAttribute(tokens[next]))
                phrases.Add(string.Join(' ', tokens[i..(next + 1)]));
        }

        return phrases.Order(StringComparer.Ordinal).Take(8).ToArray();

        bool IsAttribute(string token) => meaningful.Contains(token)
            && !FieldHeads.Contains(token) && !Connectors.Contains(token)
            && !PrimaryAnchorStopwords.Contains(token) && !SpecificAnchorStopwords.Contains(token);
    }

    internal static int Score(IReadOnlyList<string> phrases, RagMatch match)
    {
        if (phrases.Count == 0 || string.IsNullOrWhiteSpace(match.Text)
            || IsDocumentProfileMatch(match) || LooksLikeNavigationalChunk(match))
            return 0;

        // A heading, embedding text or another chunk's card cannot stand in for
        // a local documentary body. Structure only strengthens a body match.
        var body = $" {NormalizeForLexicalSignal(match.Text)} ";
        foreach (var phrase in phrases)
        {
            var position = body.IndexOf($" {phrase} ", StringComparison.Ordinal);
            if (position < 0)
                continue;

            var valueWindowStart = position + phrase.Length + 1;
            var valueWindow = body.AsSpan(valueWindowStart, Math.Min(96, body.Length - valueWindowStart));
            if (GetStructuredAnswerPriority(match) >= 2 || match.ChunkType == "docling_canonical_table_v1"
                || Regex.IsMatch(valueWindow, @"\b\d", RegexOptions.CultureInvariant))
                return 4;
        }

        return phrases.Any(phrase => body.Contains($" {phrase} ", StringComparison.Ordinal)) ? 2 : 0;
    }
}
