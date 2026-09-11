using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string InjectInlineSources(string answer, List<ToolMemory.SourceRef> sources, string language)
    {
        if (sources is null || sources.Count == 0) return answer;
        var visibleSources = MergeSourceRefsByPagePreservingOrder(sources);
        if (visibleSources.Count == 0) return answer;

        answer = RemoveTrailingModelEmittedSourceList(answer);

        // If the LLM already emitted clickable tokens, do not add more.
        if (answer.Contains("[[open|", StringComparison.OrdinalIgnoreCase))
            return answer;

        var heading = DeterministicAgentText.SourceHeading(language);

        var sb = new StringBuilder();
        sb.AppendLine(answer.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"{heading}:");

        for (var i = 0; i < visibleSources.Count; i++)
        {
            var s = visibleSources[i];
            var dp = (s.DocPath ?? "").Replace('\\', '/').TrimStart('/');
            var mainCat = "";
            var slash = dp.IndexOf('/');
            if (slash > 0) mainCat = dp.Substring(0, slash);

            var label = (s.Label ?? "").Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                var fn = Path.GetFileName(dp);
                label = string.IsNullOrWhiteSpace(mainCat) ? fn : $"{fn} ({mainCat})";
            }

            var page = s.PageStart > 0 ? s.PageStart : 0;
            label = AppendPageToOpenTokenLabel(label, page, language);

            sb.AppendLine($"{i + 1}. [[open|{dp}|{page}|{label}]]");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RemoveTrailingModelEmittedSourceList(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return answer;

        var trimmed = answer.TrimEnd();
        var naturalSourceHeadingPattern = @"(?im)^\s*(?:sources?\s+(?:used|cited|consulted)|sources?\s+(?:utilisees?|citees?|consultees?)|fuentes\s+(?:usadas?|utilizadas?|consultadas?)|fontes\s+(?:usadas?|utilizadas?|consultadas?)|verwendete\s+quellen|genutzte\s+quellen|zitierte\s+quellen|fonti\s+(?:consultate|citate|usate))\s*:\s*.*$";
        var naturalMatches = Regex.Matches(trimmed, naturalSourceHeadingPattern, RegexOptions.CultureInvariant);
        if (naturalMatches.Count > 0)
        {
            var naturalMatch = naturalMatches[^1];
            var naturalWithoutInlineSourceSection = RemoveModelEmittedSourceLinesFromFinalBlock(trimmed, naturalMatch);
            if (!string.Equals(naturalWithoutInlineSourceSection, trimmed, StringComparison.Ordinal))
                return naturalWithoutInlineSourceSection.TrimEnd();

            var naturalTrailing = trimmed[naturalMatch.Index..];
            var naturalSourceLineCount = Regex.Matches(
                naturalTrailing,
                $@"(?im)^\s*(?:[-*\u2022]|\d+[.)])?\s*(?:\[\[open\|[^\r\n]+|[^\r\n]*(?:{SourceReferenceExtensionRegex}|p\.?\s*\d+|page\s+\d+)[^\r\n]*)\s*$",
                RegexOptions.CultureInvariant).Count;
            var naturalNonEmptyLineCount = Regex.Matches(naturalTrailing, @"(?m)^\s*\S.*$", RegexOptions.CultureInvariant).Count;
            if (naturalSourceLineCount >= 1 && naturalNonEmptyLineCount <= naturalSourceLineCount + 1)
                return trimmed[..naturalMatch.Index].TrimEnd();
        }
        var sourceHeadingPattern = @"(?im)^\s*(?:source|sources|references?|r[eÃƒÂ©]f[eÃƒÂ©]rences?|fuente|fuentes|fonte|fontes|quelle|quellen|fonti)(?:\s+(?:used|cited|consulted|utilis[eÃƒÂ©]es?|cit[eÃƒÂ©]es?|consult[eÃƒÂ©]es?|usadas?|utilizadas?|consultadas?|verwendete|consultate|citate))?\s*:\s*.*$";
        var matches = Regex.Matches(trimmed, sourceHeadingPattern, RegexOptions.CultureInvariant);
        if (matches.Count == 0)
            return trimmed;

        var match = matches[^1];
        var trailing = trimmed[match.Index..];
        var sourceLineCount = Regex.Matches(
            trailing,
            $@"(?im)^\s*(?:[-*\u2022]|\d+[.)])?\s*(?:\[\[open\|[^\r\n]+|[^\r\n]*(?:{SourceReferenceExtensionRegex}|p\.?\s*\d+|page\s+\d+)[^\r\n]*)\s*$",
            RegexOptions.CultureInvariant).Count;
        var nonEmptyLineCount = Regex.Matches(trailing, @"(?m)^\s*\S.*$", RegexOptions.CultureInvariant).Count;

        if (sourceLineCount >= 1 && nonEmptyLineCount <= sourceLineCount + 1)
            return trimmed[..match.Index].TrimEnd();

        var withoutInlineSourceSection = RemoveModelEmittedSourceLinesFromFinalBlock(trimmed, match);
        if (!string.Equals(withoutInlineSourceSection, trimmed, StringComparison.Ordinal))
            return withoutInlineSourceSection.TrimEnd();

        return trimmed;
    }

    private static string RemoveModelEmittedSourceLinesFromFinalBlock(string text, Match sourceHeadingMatch)
    {
        var trailingLength = text.Length - sourceHeadingMatch.Index;
        if (trailingLength > 1600)
            return text;

        var before = text[..sourceHeadingMatch.Index].TrimEnd();
        var trailing = text[sourceHeadingMatch.Index..];
        var lines = trailing.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        if (lines.Length <= 1)
            return text;

        var sourceLinePattern =
            $@"(?i)^\s*(?:[-*\u2022]|\d+[.)])?\s*(?:\[\[open\|.+|.*(?:{SourceReferenceExtensionRegex}|\(?\s*p\.?\s*\d+\s*\)?|page\s+\d+).*)\s*$";
        var sourceHeadingLinePattern =
            @"(?i)^\s*(?:source|sources|references?|r[eÃƒÂ©]f[eÃƒÂ©]rences?|fuente|fuentes|fonte|fontes|quelle|quellen|fonti)(?:\s+(?:used|cited|consulted|utilis[eÃƒÂ©]es?|cit[eÃƒÂ©]es?|consult[eÃƒÂ©]es?|usadas?|utilizadas?|consultadas?|verwendete|consultate|citate))?\s*:\s*$";
        var naturalSourceHeadingLinePattern =
            @"(?i)^\s*(?:sources?\s+(?:used|cited|consulted)|sources?\s+(?:utilisees?|citees?|consultees?)|fuentes\s+(?:usadas?|utilizadas?|consultadas?)|fontes\s+(?:usadas?|utilizadas?|consultadas?)|verwendete\s+quellen|genutzte\s+quellen|zitierte\s+quellen|fonti\s+(?:consultate|citate|usate))\s*:\s*$";
        var removedSourceLines = 0;
        var firstKeptLine = -1;
        var passedHeading = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!passedHeading)
            {
                if (Regex.IsMatch(line, sourceHeadingLinePattern, RegexOptions.CultureInvariant)
                    || Regex.IsMatch(line, naturalSourceHeadingLinePattern, RegexOptions.CultureInvariant))
                {
                    passedHeading = true;
                    continue;
                }

                continue;
            }

            if (Regex.IsMatch(line, sourceLinePattern, RegexOptions.CultureInvariant))
            {
                removedSourceLines++;
                continue;
            }

            firstKeptLine = i;
            break;
        }

        if (removedSourceLines == 0)
            return text;

        var keptAfter = firstKeptLine >= 0
            ? string.Join(Environment.NewLine, lines.Skip(firstKeptLine)).Trim()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(keptAfter))
            return before;

        return $"{before}{Environment.NewLine}{Environment.NewLine}{keptAfter}";
    }

}
