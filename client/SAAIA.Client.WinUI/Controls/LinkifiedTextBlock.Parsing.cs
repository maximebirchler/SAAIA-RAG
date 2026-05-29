using System;
using System.Linq;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class LinkifiedTextBlock
{
    private static bool IsListOrTreeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return BulletLineRegex.IsMatch(line) || NumberLineRegex.IsMatch(line);
    }

    private static bool IsSingleNumberedSourceBlock(string[] lines)
    {
        var numberedCount = lines.Count(x => !string.IsNullOrWhiteSpace(x) && NumberLineRegex.IsMatch(x));
        if (numberedCount != 1) return false;

        return lines.Any(x =>
            !string.IsNullOrWhiteSpace(x) &&
            LooksLikeSourceHeadingLine(x));
    }

    private static bool LooksLikeSourceHeadingLine(string line)
    {
        var trimmed = (line ?? string.Empty).TrimStart();
        return trimmed.StartsWith("Source", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Sources", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Fuente", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Fuentes", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Fonte", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Fontes", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Quelle", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Quellen", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Fonti", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseListLine(string line, bool singleSourceBlock, out int level, out string marker, out string rest, out bool isNumber)
    {
        level = 0;
        marker = string.Empty;
        rest = line;
        isNumber = false;

        var mNum = NumberLineRegex.Match(line);
        if (mNum.Success)
        {
            var indent = mNum.Groups["indent"].Value ?? string.Empty;
            level = Math.Max(0, indent.Length / 2);
            rest = (mNum.Groups["rest"].Value ?? string.Empty).Trim();

            if (singleSourceBlock)
            {
                marker = TreeBullets[0];
                isNumber = false;
            }
            else
            {
                marker = (mNum.Groups["num"].Value ?? string.Empty).Trim() + ".";
                isNumber = true;
            }

            return true;
        }

        var mBul = BulletLineRegex.Match(line);
        if (mBul.Success)
        {
            var indent = mBul.Groups["indent"].Value ?? string.Empty;
            level = Math.Max(0, indent.Length / 2);
            rest = (mBul.Groups["rest"].Value ?? string.Empty).Trim();
            marker = TreeBullets[Math.Min(level, TreeBullets.Length - 1)];
            isNumber = false;
            return true;
        }

        return false;
    }

    private static bool TryParseSingleOpenToken(string s, out string docPath, out int page, out string label)
    {
        docPath = string.Empty;
        page = 1;
        label = string.Empty;

        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim();

        var m = TokenRegex.Match(trimmed);
        if (!m.Success) return false;
        if (m.Index != 0 || m.Length != trimmed.Length) return false;

        docPath = (m.Groups["path"].Value ?? string.Empty).Trim();
        var pageRaw = (m.Groups["page"].Value ?? "1").Trim();
        _ = int.TryParse(pageRaw, out page);
        if (page <= 0) page = 1;
        label = (m.Groups["label"].Value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(label)) label = docPath;

        return !string.IsNullOrWhiteSpace(docPath);
    }

    /// <summary>
    /// Converts a tokenized message (containing [[open|..]] tokens) to the plain text
    /// that the user actually sees (labels only).
    /// Used for Copy-to-clipboard.
    /// </summary>
    public static string ToPlainText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var s = TokenRegex.Replace(raw, m =>
        {
            var label = (m.Groups["label"].Value ?? string.Empty).Trim();
            if (label.Length == 0)
                label = (m.Groups["path"].Value ?? string.Empty).Trim();
            return label;
        });

        s = MarkdownBoldRegex.Replace(s, m => m.Groups["text"].Value);
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        return s;
    }
}
