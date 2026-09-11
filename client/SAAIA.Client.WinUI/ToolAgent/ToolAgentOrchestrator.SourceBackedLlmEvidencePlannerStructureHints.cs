using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static string BuildSourceBackedStructureHintsForPrompt(
        ToolResults toolResults,
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        string query,
        string language,
        int maxLines = 48)
    {
        _ = query;

        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in toolResults.Items
                     .Where(static item => item.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(item.Error))
                     .TakeLast(2))
        {
            AppendTreeStructureHints(item.Result, lines, seen);
        }

        foreach (var item in toolResults.Items
                     .Where(static item => item.ToolName == "summary.search" && string.IsNullOrWhiteSpace(item.Error))
                     .TakeLast(2))
        {
            AppendSummarySearchStructureHints(item.Result, lines, seen);
        }

        foreach (var item in toolResults.Items
                     .Where(static item => item.ToolName == "documents.navigation" && string.IsNullOrWhiteSpace(item.Error))
                     .TakeLast(3))
        {
            AppendDocumentNavigationStructureHints(item.Result, lines, seen);
        }

        AppendPreviousSourceStructureHints(lastSourcesUsed, language, lines, seen);

        return lines.Count == 0
            ? "none"
            : LimitPromptBlockLines(lines, maxLines, maxLineLength: 220);
    }

    private static void AppendSummarySearchStructureHints(JsonElement result, List<string> lines, ISet<string> seen)
    {
        if (lines.Count >= 48)
            return;

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (lines.Count >= 48)
                return;
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var docPath = CollapseWhitespace(TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath") ?? string.Empty);
            var docName = CollapseWhitespace(TryGetString(entry, "docName") ?? TryGetString(entry, "DocName") ?? Path.GetFileName(docPath));
            var label = CollapseWhitespace(
                TryGetString(entry, "label")
                ?? TryGetString(entry, "Label")
                ?? docName
                ?? docPath);
            var summaryText = CollapseWhitespace(TryGetString(entry, "summaryText") ?? TryGetString(entry, "SummaryText") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(summaryText))
                continue;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(label))
                parts.Add($"source: {label}");
            if (!string.IsNullOrWhiteSpace(docPath) && !string.Equals(docPath, docName, StringComparison.OrdinalIgnoreCase))
                parts.Add($"path: {docPath}");

            var category = CollapseWhitespace(TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath") ?? TryGetString(entry, "category") ?? TryGetString(entry, "Category") ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(category))
                parts.Add($"category: {category}");

            var pageStart = TryGetInt(entry, "pageStart") ?? TryGetInt(entry, "PageStart");
            var pageEnd = TryGetInt(entry, "pageEnd") ?? TryGetInt(entry, "PageEnd");
            if (pageStart is not null)
            {
                parts.Add(pageEnd is not null && pageEnd.Value > pageStart.Value
                    ? $"pages: {pageStart}-{pageEnd}"
                    : $"page: {pageStart}");
            }

            var level = CollapseWhitespace(TryGetString(entry, "level") ?? TryGetString(entry, "Level") ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(level))
                parts.Add($"summaryLevel: {level}");

            if (!string.IsNullOrWhiteSpace(summaryText))
                parts.Add($"summary: {TruncateForPrompt(summaryText, 220)}");

            AddStructureHintLine(
                lines,
                seen,
                $"navigationOnly summary: {TruncateForPrompt(string.Join(" | ", parts), 320)}");
        }
    }

    private static void AppendDocumentNavigationStructureHints(JsonElement result, List<string> lines, ISet<string> seen)
    {
        if (lines.Count >= 48)
            return;

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (lines.Count >= 48)
                return;

            var label = CleanNavigationRouteAnchorTitle(
                TryGetString(entry, "label")
                ?? TryGetString(entry, "Label")
                ?? TryGetString(entry, "title")
                ?? TryGetString(entry, "Title"));
            if (!IsUsableSourceBackedOptionTitle(label) || LooksLikeNavigationIndexHeadingTitle(label))
                continue;

            var docPath = CollapseWhitespace(TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath") ?? string.Empty);
            var docName = CollapseWhitespace(TryGetString(entry, "docName") ?? TryGetString(entry, "DocName") ?? Path.GetFileName(docPath));
            var source = string.IsNullOrWhiteSpace(docPath)
                ? docName
                : string.IsNullOrWhiteSpace(docName) || string.Equals(docName, docPath, StringComparison.OrdinalIgnoreCase)
                    ? docPath
                    : $"{docName} | {docPath}";

            var pageStart = TryGetInt(entry, "targetPageStart") ?? TryGetInt(entry, "TargetPageStart") ?? TryGetInt(entry, "sourcePage") ?? TryGetInt(entry, "SourcePage");
            var pageEnd = TryGetInt(entry, "targetPageEnd") ?? TryGetInt(entry, "TargetPageEnd");
            var pagePart = pageStart is null
                ? string.Empty
                : pageEnd is not null && pageEnd.Value > pageStart.Value
                    ? $" | pages: {pageStart}-{pageEnd}"
                    : $" | page: {pageStart}";

            var parts = new List<string> { label };
            if (!string.IsNullOrWhiteSpace(source))
                parts.Add($"source: {source}");
            if (!string.IsNullOrWhiteSpace(pagePart))
                parts.Add(pagePart.Trim(' ', '|'));

            var category = CollapseWhitespace(TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath") ?? TryGetString(entry, "category") ?? TryGetString(entry, "Category") ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(category))
                parts.Add($"category: {category}");

            var kind = CollapseWhitespace(TryGetString(entry, "kind") ?? TryGetString(entry, "Kind") ?? string.Empty);
            var method = CollapseWhitespace(TryGetString(entry, "resolutionMethod") ?? TryGetString(entry, "ResolutionMethod") ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(kind) || !string.IsNullOrWhiteSpace(method))
                parts.Add($"navigation: {string.Join("/", new[] { kind, method }.Where(static value => !string.IsNullOrWhiteSpace(value)))}");

            var confidence = TryGetDouble(entry, "confidence") ?? TryGetDouble(entry, "Confidence");
            if (confidence is not null)
                parts.Add(FormattableString.Invariant($"confidence: {confidence.Value:0.##}"));

            AddStructureHintLine(
                lines,
                seen,
                $"navigationOnly documentNavigation: {TruncateForPrompt(string.Join(" | ", parts), 280)}");
        }
    }

    private static void AppendTreeStructureHints(JsonElement result, List<string> lines, ISet<string> seen)
    {
        if (lines.Count >= 36)
            return;

        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("markdown", out var markdown)
            && markdown.ValueKind == JsonValueKind.String)
        {
            AppendMarkdownTreeStructureHints(markdown.GetString(), lines, seen);
        }

        AppendTreeJsonStructureHints(result, lines, seen, depth: 0);
    }

    private static void AppendMarkdownTreeStructureHints(string? markdown, List<string> lines, ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return;

        foreach (var raw in markdown.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (lines.Count >= 36)
                return;

            var text = CollapseWhitespace(raw);
            text = Regex.Replace(text, @"^[\s\-\*\+\u2022\u00b7|`>\\/.]+", string.Empty, RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"^(?:[????]+\s*)+", string.Empty, RegexOptions.CultureInvariant);
            text = CollapseWhitespace(text.Trim());
            if (string.IsNullOrWhiteSpace(text))
                continue;

            AddStructureHintLine(lines, seen, $"navigationOnly tree: {TruncateForPrompt(text, 160)}");
        }
    }

    private static void AppendTreeJsonStructureHints(JsonElement element, List<string> lines, ISet<string> seen, int depth)
    {
        if (lines.Count >= 36 || depth > 5)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var label = TryBuildTreeJsonStructureLabel(element);
                if (!string.IsNullOrWhiteSpace(label))
                    AddStructureHintLine(lines, seen, $"navigationOnly tree: {TruncateForPrompt(label, 160)}");

                foreach (var property in element.EnumerateObject())
                {
                    if (lines.Count >= 36)
                        return;

                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && IsTreeStructureProperty(property.Name))
                    {
                        AppendTreeJsonStructureHints(property.Value, lines, seen, depth + 1);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (lines.Count >= 36)
                        return;

                    AppendTreeJsonStructureHints(item, lines, seen, depth + 1);
                }

                break;
        }
    }

    private static bool IsTreeStructureProperty(string name)
        => name is "nodes" or "items" or "entries" or "children" or "folders" or "documents" or "categories"
           || name.Equals("Nodes", StringComparison.Ordinal)
           || name.Equals("Items", StringComparison.Ordinal)
           || name.Equals("Entries", StringComparison.Ordinal)
           || name.Equals("Children", StringComparison.Ordinal)
           || name.Equals("Folders", StringComparison.Ordinal)
           || name.Equals("Documents", StringComparison.Ordinal)
           || name.Equals("Categories", StringComparison.Ordinal);

    private static string TryBuildTreeJsonStructureLabel(JsonElement value)
    {
        var label =
            TryGetString(value, "name")
            ?? TryGetString(value, "displayName")
            ?? TryGetString(value, "title")
            ?? TryGetString(value, "canonicalName")
            ?? TryGetString(value, "docName")
            ?? TryGetString(value, "label")
            ?? TryGetString(value, "path")
            ?? TryGetString(value, "categoryPath")
            ?? TryGetString(value, "docPath")
            ?? TryGetString(value, "categoryRef");

        label = CollapseWhitespace(label ?? string.Empty);
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        var path =
            TryGetString(value, "categoryPath")
            ?? TryGetString(value, "path")
            ?? TryGetString(value, "docPath");
        path = CollapseWhitespace(path ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(path)
            && !string.Equals(path, label, StringComparison.OrdinalIgnoreCase))
        {
            label = $"{path} > {label}";
        }

        var totalDocuments = TryGetInt(value, "totalDocuments")
                             ?? TryGetInt(value, "directDocuments")
                             ?? TryGetInt(value, "documentCount");
        if (totalDocuments is > 0)
            label = $"{label} ({totalDocuments} docs)";

        return label;
    }

    private static void AppendPreviousSourceStructureHints(
        IReadOnlyList<ToolMemory.SourceRef>? lastSourcesUsed,
        string language,
        List<string> lines,
        ISet<string> seen)
    {
        if (lastSourcesUsed is null || lastSourcesUsed.Count == 0)
            return;

        foreach (var source in lastSourcesUsed.Take(12))
        {
            if (lines.Count >= 48)
                return;

            var docLabel = CollapseWhitespace(source.DocName ?? Path.GetFileName(source.DocPath) ?? source.Label);
            if (string.IsNullOrWhiteSpace(docLabel))
                continue;

            var parts = new List<string>
            {
                $"{docLabel} {SourceBackedPagePrefix(language)}{Math.Max(1, source.PageStart)}"
            };

            var category = CollapseWhitespace(source.CategoryPath ?? source.Category ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(category))
                parts.Add($"category: {category}");

            var heading = CollapseWhitespace(source.HeadingPath ?? source.SectionTitle ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(heading))
                parts.Add($"heading: {TruncateForPrompt(heading, 120)}");

            var cardHints = source.MatchedContentCards
                .Select(static card => CollapseWhitespace(card.Title))
                .Where(static title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();
            if (cardHints.Length > 0)
                parts.Add($"contentCards: {string.Join("; ", cardHints)}");

            var profileHints = EnumerateSourceProfileHints(source.ProfileSignals)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();
            if (profileHints.Length > 0)
                parts.Add($"profileHints: {string.Join("; ", profileHints)}");

            var role = CollapseWhitespace(source.SelectionHintEvidenceRole ?? source.ContentRole ?? source.NavigationReason ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(role))
                parts.Add($"signalRole: {role}");

            AddStructureHintLine(
                lines,
                seen,
                $"navigationOnly previousSource: {TruncateForPrompt(string.Join(" | ", parts), 260)}");
        }
    }

    private static IEnumerable<string> EnumerateSourceProfileHints(ToolMemory.SourceProfileSignalsRef? profile)
    {
        if (profile is null)
            yield break;

        foreach (var value in profile.Topics
                     .Concat(profile.Keywords)
                     .Concat(profile.Entities)
                     .Concat(profile.MatchedTerms)
                     .Concat(profile.HypotheticalQuestions))
        {
            var cleaned = CollapseWhitespace(value);
            if (!string.IsNullOrWhiteSpace(cleaned))
                yield return cleaned;
        }
    }

    private static void AddStructureHintLine(List<string> lines, ISet<string> seen, string line)
    {
        line = CollapseWhitespace(line);
        if (string.IsNullOrWhiteSpace(line) || !seen.Add(line))
            return;

        lines.Add(line);
    }
}
