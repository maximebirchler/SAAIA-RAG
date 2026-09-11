namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedQuestionFocus
{
    public static bool IsDocumentFamilyOrTypeQuestion(SourceBackedIntake intake)
        => string.Equals(
            intake.QuestionFocus,
            "document_family_or_type",
            StringComparison.OrdinalIgnoreCase);

    public static bool MentionsSpecificDocument(SourceBackedIntake intake)
        => !string.IsNullOrWhiteSpace(intake.RequestedDocumentName);

    public static string? ExtractFirstSpecificDocumentName(SourceBackedIntake intake)
        => string.IsNullOrWhiteSpace(intake.RequestedDocumentName)
            ? null
            : intake.RequestedDocumentName.Trim();

    public static bool MatchesSpecificDocument(SourceBackedIntake intake, EvidenceItem item)
    {
        var requestedDocument = ExtractFirstSpecificDocumentName(intake);
        var requested = NormalizeDocumentName(FileNameOnly(requestedDocument));
        var requestedRaw = NormalizeDocumentName(requestedDocument);
        if (string.IsNullOrWhiteSpace(requested))
            return true;

        var docName = NormalizeDocumentName(item.DocName);
        var docPathName = NormalizeDocumentName(FileNameOnly(item.DocPath));
        return string.Equals(requested, docName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(requested, docPathName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(requestedRaw, docName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(requestedRaw, docPathName, StringComparison.OrdinalIgnoreCase)
               || IsDocumentReferencePrefix(requested, docName)
               || IsDocumentReferencePrefix(requested, docPathName)
               || IsDocumentReferencePrefix(requestedRaw, docName)
               || IsDocumentReferencePrefix(requestedRaw, docPathName);
    }

    private static bool IsDocumentReferencePrefix(
        string requested,
        string candidate)
    {
        if (string.IsNullOrWhiteSpace(requested)
            || candidate.Length <= requested.Length
            || !candidate.StartsWith(
                requested,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var boundary = candidate[requested.Length];
        return char.IsWhiteSpace(boundary)
               || boundary is '.' or '-' or '_' or '(' or '[';
    }

    private static string FileNameOnly(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace('\\', '/')
            .Trim('`', '"', ' ', '\t', '\r', '\n');
        var separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static string NormalizeDocumentName(string? value)
        => (value ?? string.Empty)
            .Trim('`', '"', ' ', '\t', '\r', '\n')
            .ToLowerInvariant();

}
