using System;
using System.Collections.Generic;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent.NativeRouting;

/// <summary>Restores only a unique explicitly written path ending in the announced exact filename.</summary>
internal static class ExplicitDocumentPath
{
    internal static string PreserveUniquePath(string request, string announced)
    {
        if (string.IsNullOrWhiteSpace(announced) || announced.Contains('/') || announced.Contains('\\'))
            return announced;
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var quotedRanges = new List<(int Start, int End)>();
        var unsafeReference = false;
        foreach (var (open, close) in new[] { ('«', '»'), ('“', '”'), ('"', '"'), ('\'', '\''), ('`', '`') })
        {
            var offset = 0;
            while (offset < request.Length)
            {
                var start = request.IndexOf(open, offset);
                if (start < 0) break;
                var end = request.IndexOf(close, start + 1);
                if (end < 0) break;
                if ((start == 0 || !char.IsLetterOrDigit(request[start - 1]))
                    && AddCandidate(request[(start + 1)..end]))
                    quotedRanges.Add((start, end));
                offset = end + 1;
            }
        }
        for (var offset = 0; offset < request.Length;)
        {
            var name = request.IndexOf(announced, offset, StringComparison.OrdinalIgnoreCase);
            if (name < 0) break;
            var end = name + announced.Length;
            offset = end;
            if (name == 0 || request[name - 1] is not ('/' or '\\')
                || quotedRanges.Any(range => name > range.Start && name < range.End)
                || (end < request.Length && (char.IsLetterOrDigit(request[end]) || request[end] is '_' or '/' or '\\'
                    || (request[end] == '.' && end + 1 < request.Length && char.IsLetterOrDigit(request[end + 1])))))
                continue;
            var start = name - 1;
            while (start > 0 && IsUnquotedPathCharacter(request[start - 1])) start--;
            AddCandidate(request[start..end]);
        }
        return !unsafeReference && candidates.Count == 1 ? candidates.Values.Single() : announced;

        bool AddCandidate(string raw)
        {
            var candidate = raw.Trim();
            var normalized = candidate.Replace('\\', '/');
            if (!normalized.EndsWith("/" + announced, StringComparison.OrdinalIgnoreCase)
                || normalized.Length <= announced.Length + 1)
                return false;
            var colon = normalized.IndexOf(':');
            var validDrive = colon == 1 && char.IsAsciiLetter(normalized[0]) && normalized.Length > 2 && normalized[2] == '/';
            if (candidate.Length > 1024 || candidate.Any(char.IsControl)
                || candidate.IndexOfAny(['*', '?', '|', '<', '>']) >= 0
                || (colon >= 0 && !validDrive)
                || normalized.Split('/').Any(segment => segment is "." or ".."))
            {
                unsafeReference = true;
                return false;
            }
            candidates.TryAdd(normalized, candidate);
            return true;
        }
    }

    private static bool IsUnquotedPathCharacter(char value)
        => !char.IsWhiteSpace(value) && !"\"'`«»“”()[]{}<>,;?!#|".Contains(value);
}
