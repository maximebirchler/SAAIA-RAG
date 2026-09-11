using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    internal static JsonElement ApplySpecificDocumentQueryGuard(JsonElement rawData, string? searchQuery)
    {
        var query = (searchQuery ?? string.Empty).Trim();
        if (!LooksLikeSpecificDocumentReferenceQuery(query))
            return rawData;

        if (!rawData.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return rawData;

        var filtered = new List<JsonElement>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var docName = TryGetString(entry, "docName");
            var docPath = TryGetString(entry, "docPath");
            if (MatchesSpecificDocumentReferenceQuery(query, docName, docPath))
                filtered.Add(entry.Clone());
        }

        var normalized = new
        {
            scopePath = TryGetString(rawData, "scopePath"),
            searchQuery = query,
            limit = TryGetInt(rawData, "limit") ?? filtered.Count,
            offset = TryGetInt(rawData, "offset") ?? 0,
            total = filtered.Count,
            endOfList = true,
            dropped = TryGetInt(rawData, "dropped") ?? 0,
            items = filtered
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(normalized));
        return doc.RootElement.Clone();
    }

    internal static bool LooksLikeSpecificDocumentReferenceQuery(string? searchQuery)
    {
        var query = (searchQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
            return false;

        return Regex.IsMatch(query, @"(?i)\.pdf\b")
            || query.Contains('/')
            || query.Contains('\\')
            || Regex.IsMatch(query, @"(?i)\bPDF\s*0*\d{1,4}\b");
    }

    internal static bool MatchesSpecificDocumentReferenceQuery(string searchQuery, string? docName, string? docPath)
    {
        var query = (searchQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalizedQueryPath = query.Replace('\\', '/').Trim().Trim('/');
        var normalizedQueryText = NormalizeDocumentLookupText(query);
        var normalizedQueryCompact = NormalizeDocumentLookupCompact(query);
        var queryFileName = Path.GetFileName(normalizedQueryPath);
        var queryFileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalizedQueryPath);
        var normalizedQueryFileName = NormalizeDocumentLookupText(queryFileName);
        var normalizedQueryFileNameWithoutExtension = NormalizeDocumentLookupText(queryFileNameWithoutExtension);

        IEnumerable<string> EnumerateCandidates()
        {
            if (!string.IsNullOrWhiteSpace(docName))
                yield return docName!;
            if (!string.IsNullOrWhiteSpace(docPath))
            {
                yield return docPath!;
                var fileName = Path.GetFileName(docPath!);
                if (!string.IsNullOrWhiteSpace(fileName))
                    yield return fileName;
            }
        }

        foreach (var raw in EnumerateCandidates().Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = raw.Trim();
            var normalizedCandidatePath = candidate.Replace('\\', '/').Trim().Trim('/');
            var normalizedCandidateText = NormalizeDocumentLookupText(candidate);
            var normalizedCandidateCompact = NormalizeDocumentLookupCompact(candidate);
            var candidateFileName = Path.GetFileName(normalizedCandidatePath);
            var candidateFileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalizedCandidatePath);
            var normalizedCandidateFileName = NormalizeDocumentLookupText(candidateFileName);
            var normalizedCandidateFileNameWithoutExtension = NormalizeDocumentLookupText(candidateFileNameWithoutExtension);

            if (string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedCandidatePath, normalizedQueryPath, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(normalizedQueryCompact) && string.Equals(normalizedCandidateCompact, normalizedQueryCompact, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(normalizedQueryText) && string.Equals(normalizedCandidateText, normalizedQueryText, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(normalizedQueryFileName) && string.Equals(normalizedCandidateFileName, normalizedQueryFileName, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(normalizedQueryFileNameWithoutExtension) && string.Equals(normalizedCandidateFileNameWithoutExtension, normalizedQueryFileNameWithoutExtension, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
