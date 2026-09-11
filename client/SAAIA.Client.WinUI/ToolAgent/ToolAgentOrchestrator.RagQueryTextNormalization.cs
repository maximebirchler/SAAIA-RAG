using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string NormalizeRagQueryForRetrieval(string? query)
    {
        var s = CollapseWhitespace(RepairReplacementAccentMarkers(query ?? string.Empty));
        if (s.Length == 0)
            return string.Empty;

        if (IsBroadenedSourceSearchConfirmationEnvelope(s)
            && TryExtractPreviousUserRequestFromEnvelope(s, out var broadenedRequest))
        {
            s = broadenedRequest;
        }

        var clarification = Regex.Match(s, @"(?is)\bUSER_CLARIFICATION:\s*(?<topic>.+?)(?:\s+RESOLVED_REQUEST:|$)");
        if (clarification.Success)
            s = clarification.Groups["topic"].Value.Trim();

        if (TryExtractDelimitedUserDemandTopic(s, out var delimitedTopic))
            return delimitedTopic;

        var topicPatterns = new[]
        {
            @"(?i)\b(?:si|whether|se|ob)\s+(?<topic>[^?.!;]+?)\s+(?:est|sont|is|are|es|esta|est[a\u00e1]|est[a\u00e3]o|ist|sind|[e\u00e8])\s+(?:mentionn\w*|mentioned|mencion\w*|erwaehn\w*|erw[a\u00e4]hn\w*|menzion\w*)\b",
            @"(?i)\b(?:document|documents?|source|sources?)\s+(?:qui\s+)?(?:parle|parlent|mentionne|mentionnent|traite|traitent)\s+(?:de|du|des|d['â€™])?\s*(?<topic>[^?.!;]+)",
            @"(?i)\b(?:parle|parlent|mentionne|mentionnent|traite|traitent)\s+(?:de|du|des|d['â€™])?\s*(?<topic>[^?.!;]+)",
            @"(?i)\b(?:about|regarding|concerning)\s+(?<topic>[^?.!;]+)"
        };

        foreach (var pattern in topicPatterns)
        {
            var match = Regex.Match(s, pattern);
            if (match.Success)
            {
                var topic = CleanupStandaloneTopic(match.Groups["topic"].Value);
                if (!string.IsNullOrWhiteSpace(topic))
                    return topic;
            }
        }

        return CleanupStandaloneTopic(s);
    }

    private static bool IsBroadenedSourceSearchConfirmationEnvelope(string? query)
        => !string.IsNullOrWhiteSpace(query)
            && query.Contains("USER_CONFIRMED_BROADER_SOURCE_SEARCH:", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractPreviousUserRequestFromEnvelope(string? query, out string previousRequest)
    {
        previousRequest = string.Empty;
        var s = CollapseWhitespace(RepairReplacementAccentMarkers(query ?? string.Empty));
        if (s.Length == 0)
            return false;

        var match = Regex.Match(
            s,
            @"(?is)\bPREVIOUS_USER_REQUEST:\s*(?<topic>.+?)(?:\s+USER_CONFIRMED_BROADER_SOURCE_SEARCH:|\s+RESOLVED_REQUEST:|$)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        previousRequest = CollapseWhitespace(match.Groups["topic"].Value);
        return previousRequest.Length > 0;
    }

    private static bool TryExtractDelimitedUserDemandTopic(string? query, out string topic)
    {
        topic = string.Empty;
        var s = CollapseWhitespace(RepairReplacementAccentMarkers(query ?? string.Empty));
        if (s.Length == 0)
            return false;

        foreach (Match match in Regex.Matches(
                     s,
                     """(?:\u00ab|\u201c|"|`)(?<topic>.{3,180}?)(?:\u00bb|\u201d|"|`)""",
                     RegexOptions.CultureInvariant))
        {
            var candidate = CleanupStandaloneTopic(match.Groups["topic"].Value);
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var before = s[..match.Index];
            var after = s[(match.Index + match.Length)..];
            var context = NormalizeLexicalLookup(CollapseWhitespace(before + " " + after));
            if (!Regex.IsMatch(
                    context,
                    @"\b(?:demande|demandes|demander|question|questions|requete|requetes|dit|disant|orienter|orientation|utilisateur|client|asked|asks|ask|request|requests|says|said|user|customer|customers|prepare|preparer|response|answer|answers|responder|rispondere|antwort|antworten)\b",
                    RegexOptions.CultureInvariant))
            {
                continue;
            }

            topic = candidate;
            return true;
        }

        return false;
    }

    private static string RepairReplacementAccentMarkers(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('?', StringComparison.Ordinal))
            return value;

        var repaired = Regex.Replace(value, @"(?<=\p{L})\?(?=\p{L})", "e", RegexOptions.CultureInvariant);
        repaired = Regex.Replace(repaired, @"(?<!\p{L})\?(?=\p{L})", "e", RegexOptions.CultureInvariant);
        return repaired;
    }

    private static bool LooksLikeStandaloneDocumentaryTopic(string? userMessage)
    {
        var s = NormalizeRagQueryForRetrieval(userMessage);
        if (s.Length < 3 || s.Length > 80)
            return false;
        if (s.Contains('?', StringComparison.Ordinal))
            return false;
        if (Regex.IsMatch(s, @"(?i)^(?:hi|hello|bonjour|salut|merci|thanks?|ok|okay|oui|non|qui\s+es[- ]?tu|comment\s+vas[- ]?tu)$"))
            return false;
        if (!Regex.IsMatch(s, @"\p{L}"))
            return false;

        var tokenCount = Regex.Matches(s, @"[\p{L}\p{N}]+").Count;
        if (tokenCount is < 1 or > 6)
            return false;

        return true;
    }

    private static string CleanupStandaloneTopic(string? value)
    {
        var s = CollapseWhitespace(value ?? string.Empty).Trim(' ', '.', '?', '!', ':', ';', '"', '\'');
        s = Regex.Replace(s, @"(?i)^(?:l['â€™]|d['â€™]|de\s+l['â€™]|de\s+la\s+|du\s+|des\s+|le\s+|la\s+|les\s+|un\s+|une\s+)", string.Empty).Trim();
        s = Regex.Replace(s, @"(?i)\s+(?:pr[Ã©e]cis[Ã©e]ment|exactement)$", string.Empty).Trim();
        s = Regex.Replace(
            s,
            @"(?i)\s+(?:pr(?:e|\u00e9)cis(?:e|\u00e9)ment|precisement|precisamente|exactement|exactly|precisely)$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();
        return s;
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var start = 0;
        var end = value.Length - 1;
        while (start <= end && char.IsWhiteSpace(value[start]))
            start++;
        while (end >= start && char.IsWhiteSpace(value[end]))
            end--;
        if (start > end)
            return string.Empty;

        var hasWhitespaceRun = false;
        for (var i = start; i <= end; i++)
        {
            if (!char.IsWhiteSpace(value[i]))
                continue;

            if (i == start || i == end || char.IsWhiteSpace(value[i - 1]) || value[i] != ' ')
            {
                hasWhitespaceRun = true;
                break;
            }
        }

        if (!hasWhitespaceRun && start == 0 && end == value.Length - 1)
            return value;

        var sb = new StringBuilder(end - start + 1);
        var pendingSpace = false;
        for (var i = start; i <= end; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0)
                sb.Append(' ');
            sb.Append(c);
            pendingSpace = false;
        }

        return sb.ToString();
    }
}
