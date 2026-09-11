using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool TryExtractJsonObject(string raw, out string json)
    {
        json = (raw ?? string.Empty).Trim();

        if (json.StartsWith("```"))
        {
            var i = json.IndexOf('\n');
            if (i >= 0)
                json = json.Substring(i + 1);
            json = json.Replace("```", string.Empty).Trim();
        }

        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start)
            return false;

        json = json.Substring(start, end - start + 1);
        return true;
    }

    private static bool TryRepairJsonObjectForParsing(string json, out string repaired)
    {
        repaired = (json ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(repaired))
            return false;

        if (CanParseJsonObject(repaired))
            return true;

        var candidates = new[]
            {
                RepairJsonDelimiters(repaired, closeBeforeMismatchedCloser: false),
                RepairJsonDelimiters(repaired, closeBeforeMismatchedCloser: true)
            }
            .Select(RemoveTrailingCommasBeforeJsonClosers)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, repaired, StringComparison.Ordinal))
                continue;

            if (CanParseJsonObject(candidate))
            {
                repaired = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool CanParseJsonObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static string RepairJsonDelimiters(string json, bool closeBeforeMismatchedCloser)
    {
        var sb = new StringBuilder(json.Length + 8);
        var expectedClosers = new Stack<char>();
        var inString = false;
        var escaped = false;

        foreach (var ch in json)
        {
            if (inString)
            {
                sb.Append(ch);
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                sb.Append(ch);
                continue;
            }

            if (ch == '{')
            {
                expectedClosers.Push('}');
                sb.Append(ch);
                continue;
            }

            if (ch == '[')
            {
                expectedClosers.Push(']');
                sb.Append(ch);
                continue;
            }

            if (ch is '}' or ']')
            {
                if (expectedClosers.Count == 0)
                    continue;

                if (expectedClosers.Peek() == ch)
                {
                    expectedClosers.Pop();
                    sb.Append(ch);
                    continue;
                }

                if (closeBeforeMismatchedCloser)
                {
                    while (expectedClosers.Count > 0 && expectedClosers.Peek() != ch)
                        sb.Append(expectedClosers.Pop());

                    if (expectedClosers.Count > 0 && expectedClosers.Peek() == ch)
                    {
                        expectedClosers.Pop();
                        sb.Append(ch);
                    }
                }

                continue;
            }

            sb.Append(ch);
        }

        while (expectedClosers.Count > 0)
            sb.Append(expectedClosers.Pop());

        return sb.ToString();
    }

    private static string RemoveTrailingCommasBeforeJsonClosers(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        var sb = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        foreach (var ch in json)
        {
            if (inString)
            {
                sb.Append(ch);
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                sb.Append(ch);
                continue;
            }

            if (ch is '}' or ']')
            {
                var i = sb.Length - 1;
                while (i >= 0 && char.IsWhiteSpace(sb[i]))
                    i--;
                if (i >= 0 && sb[i] == ',')
                    sb.Remove(i, 1);
            }

            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }

    private static bool TryExtractFinalAnswerFromRaw(string raw, out string finalAnswer)
    {
        finalAnswer = string.Empty;
        raw ??= string.Empty;

        if (TryExtractJsonObject(raw, out var json) && TryParseAnswerEnvelope(json, out var parsedFinalAnswer, out _))
        {
            finalAnswer = (parsedFinalAnswer ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(finalAnswer);
        }

        var match = Regex.Match(raw, "\\\"finalAnswer\\\"\\s*:\\s*\\\"(?<v>(?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.Singleline);
        if (match.Success)
        {
            var value = match.Groups["v"].Value;
            try
            {
                finalAnswer = JsonSerializer.Deserialize<string>("\"" + value + "\"") ?? string.Empty;
                finalAnswer = finalAnswer.Trim();
                return !string.IsNullOrWhiteSpace(finalAnswer);
            }
            catch
            {
                finalAnswer = value.Replace("\\n", "\n").Replace("\\t", "\t").Trim();
                return !string.IsNullOrWhiteSpace(finalAnswer);
            }
        }

        return false;
    }

    private static string BuildJsonEnvelopeError(string? lang)
        => DeterministicAgentText.JsonEnvelopeError(lang);
}
