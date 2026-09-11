using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private void RememberUniqueSourceBackedFocusedDocument(
        IReadOnlyCollection<ToolMemory.SourceRef> verifiedSources)
    {
        var focused = TryBuildUniqueSourceBackedFocusedDocument(verifiedSources);
        if (focused is null)
            return;

        var previous = _mem.LastFocusedDocument;
        if (previous is not null && SameDocument(previous, focused))
        {
            focused.PdfRef = previous.PdfRef;
            focused.Pages = previous.Pages;
            focused.ModifiedAt = previous.ModifiedAt;
            focused.IngestedAt = previous.IngestedAt;
        }

        _mem.LastFocusedDocument = focused;
        _mem.LastRequestedDocumentRef = FirstNonBlank(
            focused.DocPath,
            focused.DocId,
            focused.DocName);
        _mem.PromoteDocumentsToWorkspace(new[] { focused });

        EmitRagTrace(
            "source_backed_memory.focused_document.updated",
            ("doc_id", focused.DocId),
            ("doc_path", focused.DocPath),
            ("doc_name", focused.DocName),
            ("category_path", focused.CategoryPath),
            ("reason", "single_verified_source_document"));
    }

    internal static ToolMemory.DocumentItem? TryBuildUniqueSourceBackedFocusedDocument(
        IReadOnlyCollection<ToolMemory.SourceRef>? verifiedSources)
    {
        if (verifiedSources is null || verifiedSources.Count == 0)
            return null;

        var documents = verifiedSources
            .Where(HasDocumentIdentity)
            .GroupBy(BuildVerifiedSourceDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(2)
            .ToArray();

        if (documents.Length != 1)
            return null;

        var source = documents[0];
        return new ToolMemory.DocumentItem
        {
            DocId = source.DocId?.Trim() ?? string.Empty,
            DocPath = source.DocPath?.Trim() ?? string.Empty,
            DocName = FirstNonBlank(
                source.DocName,
                Path.GetFileName(source.DocPath ?? string.Empty)),
            Category = source.Category?.Trim() ?? string.Empty,
            CategoryRef = source.CategoryRef,
            CategoryPath = source.CategoryPath?.Trim() ?? string.Empty,
            SourceHash = source.SourceHash,
            DocLanguage = source.DocLanguage,
            ProfileLanguage = source.ProfileLanguage
        };
    }

    private static bool HasDocumentIdentity(ToolMemory.SourceRef source)
        => !string.IsNullOrWhiteSpace(source.DocId)
           || !string.IsNullOrWhiteSpace(source.DocPath)
           || !string.IsNullOrWhiteSpace(source.DocName);

    private static string BuildVerifiedSourceDocumentKey(ToolMemory.SourceRef source)
    {
        if (!string.IsNullOrWhiteSpace(source.DocId))
            return "id:" + source.DocId.Trim();
        if (!string.IsNullOrWhiteSpace(source.DocPath))
            return "path:" + source.DocPath.Replace('\\', '/').Trim();
        return "name:" + (source.DocName?.Trim() ?? string.Empty);
    }

    private static bool SameDocument(
        ToolMemory.DocumentItem left,
        ToolMemory.DocumentItem right)
    {
        if (!string.IsNullOrWhiteSpace(left.DocId)
            && !string.IsNullOrWhiteSpace(right.DocId))
        {
            return string.Equals(left.DocId, right.DocId, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(left.DocPath)
            && !string.IsNullOrWhiteSpace(right.DocPath))
        {
            return string.Equals(
                left.DocPath.Replace('\\', '/').Trim(),
                right.DocPath.Replace('\\', '/').Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(left.DocName)
               && !string.IsNullOrWhiteSpace(right.DocName)
               && string.Equals(left.DocName, right.DocName, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? string.Empty;
}
