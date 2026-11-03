using System.Text.RegularExpressions;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;

namespace SAAIA.Backend.Retrieval;

internal static class RetrievalDocumentTypes
{
    // These are grammatical heads, not a catalogue of permitted document types.
    // An explicitly labelled type can use any phrase, including tenant vocabulary.
    private static readonly Regex DocumentHead = new(
        @"\b(?:sheets?|datasheets?|fiches?|manuals?|manuels?|guides?|handbooks?|reports?|rapports?|certificates?|certificats?|declarations?|protocols?|protocoles?|specifications?|bulletins?|brochures?|notices?|polic(?:y|ies)|politiques?|registers?|registres?|catalog(?:ue)?s?)\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex ExplicitType = new(
        @"\b(?:type de document|document type|kind of document)\s*[:=]\s*[""«“]?(?<type>[^""»”\r\n,;.!?]{2,120})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex OutputDocumentLead = new(
        @"\b(?:faire|fais|creer|cree|preparer|prepare|rediger|redige|ecrire|ecris|generer|genere|write|draft|create|make|generate|prepare)\s+(?:(?:me|moi)\s+)?(?:un|une|des|le|la|a|an|the|some)(?<modifiers>(?:\s+\p{L}+){0,2})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex PossibleAcronym = new(
        @"\b\p{L}{2,8}\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal)
    {
        "de", "d", "du", "des", "la", "le", "les", "l", "the", "of"
    };

    private static readonly HashSet<string> PhraseBoundaries = new(StringComparer.Ordinal)
    {
        "a", "au", "aux", "ce", "ces", "cette", "celles", "un", "une", "dans", "sur", "en", "pour", "avec", "sans", "et", "ou",
        "an", "and", "or", "in", "on", "for", "from", "with", "without", "by", "about", "to", "vs", "versus",
        "quel", "quelle", "quels", "quelles", "what", "which", "find", "show", "give", "get", "compare", "search", "read", "need", "want",
        "trouve", "trouver", "donne", "donner", "cherche", "chercher", "resume", "resumer", "explique", "explain", "veux", "besoin",
        "ancienne", "ancien", "actuelle", "actuel", "derniere", "dernier", "previous", "old", "current", "latest", "historical"
    };

    internal sealed record Request(IReadOnlyList<string> Aliases, IReadOnlyList<string> NormalizedMatchPhrases);

    internal static Request? ResolveRequestedType(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var explicitTypes = ExplicitType.Matches(query)
            .Select(match => NormalizeForLexicalSignal(match.Groups["type"].Value))
            .Where(static phrase => phrase.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (explicitTypes.Length > 0)
            return BuildSingleRequest(explicitTypes);

        var normalized = NormalizeForLexicalSignal(query);
        var descriptors = DocumentHead.Matches(normalized)
            .Where(match => !IsRequestedOutputType(normalized, match.Index))
            .Select(match => ExpandHeadPhrase(normalized, match))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // An acronym alone has no document semantics unless the shared lexical
        // infrastructure can expand it to a phrase with a documentary head.
        foreach (Match word in PossibleAcronym.Matches(normalized))
        {
            if (!DocumentHead.IsMatch(word.Value) && BuildLexicalTokenVariants(word.Value).Any(IsDocumentaryAliasPhrase)
                && !IsRequestedOutputType(normalized, word.Index))
                descriptors.Add(word.Value);
        }

        return BuildSingleRequest(descriptors);
    }

    private static bool IsRequestedOutputType(string normalized, int index)
    {
        var lead = OutputDocumentLead.Match(normalized[..index].TrimEnd());
        return lead.Success && !Split(lead.Groups["modifiers"].Value)
            .Any(word => Connectors.Contains(word) || PhraseBoundaries.Contains(word));
    }

    private static Request? BuildSingleRequest(IEnumerable<string> descriptors)
    {
        var aliases = new List<string>();
        foreach (var descriptor in descriptors.Distinct(StringComparer.Ordinal))
        {
            var next = BuildAliases(descriptor);
            if (aliases.Count > 0 && !aliases.Intersect(next, StringComparer.Ordinal).Any())
                return null; // A multi-type request must not privilege one surface.
            aliases.AddRange(next);
        }

        if (aliases.Count == 0)
            return null;
        var distinct = aliases.Distinct(StringComparer.Ordinal).ToArray();
        return new Request(distinct, distinct.Select(NormalizeDescriptorForMatch).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<string> BuildAliases(string descriptor)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal) { descriptor };
        var tokens = Split(descriptor);
        foreach (var alias in BuildCrossLanguageTechnicalPhraseVariants(tokens))
        {
            if (IsDocumentaryAliasPhrase(alias)
                || !alias.Contains(' ') && BuildLexicalTokenVariants(alias).Any(IsDocumentaryAliasPhrase))
                aliases.Add(alias);
        }
        if (tokens.Length == 1)
        {
            var variants = BuildLexicalTokenVariants(descriptor);
            if (variants.Any(IsDocumentaryAliasPhrase))
            {
                foreach (var alias in variants.Where(alias => IsDocumentaryAliasPhrase(alias) || !alias.Contains(' ')))
                    aliases.Add(alias);
            }
            else if (DocumentHead.IsMatch(descriptor))
            {
                foreach (var alias in variants.Where(alias => !alias.Contains(' ') && DocumentHead.IsMatch(alias)))
                    aliases.Add(alias);
            }
        }

        return aliases.Order(StringComparer.Ordinal).ToArray();
    }

    internal static int ComputeTitleSignal(Request? request, string? title)
    {
        if (request is null || string.IsNullOrWhiteSpace(title))
            return 0;

        var titleWords = $" {NormalizeDescriptorForMatch(title)} ";
        return request.NormalizedMatchPhrases.Any(alias => titleWords.Contains($" {alias} ", StringComparison.Ordinal))
            ? 3
            : 0;
    }

    private static string ExpandHeadPhrase(string normalized, Match head)
    {
        var before = Split(normalized[..head.Index]);
        var after = Split(normalized[(head.Index + head.Length)..]);
        if (after.Length > 0)
        {
            var postpositivePhrase = $"{head.Value} {after[0]}";
            if (BuildLexicalTokenVariants(head.Value).Contains(postpositivePhrase, StringComparer.Ordinal))
                return postpositivePhrase;
        }
        if (after.Length >= 2 && Connectors.Contains(after[0]))
        {
            var suffix = new List<string>();
            for (var index = 0; index < after.Length && suffix.Count < 4; index++)
            {
                var word = after[index];
                if (PhraseBoundaries.Contains(word) || word.Any(char.IsDigit))
                    break;
                suffix.Add(word);
            }
            if (suffix.Count >= 2 && suffix.Any(word => !Connectors.Contains(word)))
                return $"{head.Value} {string.Join(' ', suffix)}";
        }

        var modifiers = new List<string>();
        for (var index = before.Length - 1; index >= 0 && modifiers.Count < 2; index--)
        {
            var word = before[index];
            if (word.Length < 3 || Connectors.Contains(word) || PhraseBoundaries.Contains(word)
                || word.Any(char.IsDigit) || DocumentHead.IsMatch(word)
                || BuildLexicalTokenVariants(word).Any(IsDocumentaryAliasPhrase))
                break;
            modifiers.Insert(0, word);
        }

        return modifiers.Count == 0 ? head.Value : $"{string.Join(' ', modifiers)} {head.Value}";
    }

    private static bool IsDocumentaryAliasPhrase(string value)
        => value.Contains(' ') && DocumentHead.IsMatch(value);

    private static string NormalizeDescriptorForMatch(string value)
        => string.Join(' ', Split(NormalizeForLexicalSignal(value))
            .Where(word => !Connectors.Contains(word)));

    private static string[] Split(string value)
        => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
