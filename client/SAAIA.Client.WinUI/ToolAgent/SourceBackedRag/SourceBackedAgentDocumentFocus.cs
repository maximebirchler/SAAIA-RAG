namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string GlobalDocumentFocusEvidenceId = "GLOBAL";
    private const int MaximumDocumentFocusOptions = 8;

    private sealed record DocumentFocusOption(
        string EvidenceId,
        string DocumentKey,
        string DocumentLabel,
        EvidenceItem Evidence);

    private static IReadOnlyList<DocumentFocusOption> BuildDocumentFocusOptions(
        EvidenceBundle bundle,
        int maximumWorkingEvidenceItems)
    {
        var maximum = Math.Min(
            MaximumDocumentFocusOptions,
            Math.Max(1, maximumWorkingEvidenceItems));
        var seenDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<DocumentFocusOption>(maximum);
        foreach (var item in bundle.Items)
        {
            if (!IsMechanicallyCitableCandidate(item)
                && !IsMechanicallyResolvableNavigationLocator(item))
                continue;
            var documentLabel = CollapseDocumentFocusValue(
                item.DocPath ?? item.DocName);
            var documentKey = !string.IsNullOrWhiteSpace(item.DocId)
                ? "id:" + item.DocId.Trim()
                : "path:" + documentLabel;
            if (documentLabel.Length == 0
                || !seenDocuments.Add(documentKey))
            {
                continue;
            }

            options.Add(new DocumentFocusOption(
                item.EvidenceId,
                documentKey,
                documentLabel,
                item));
            if (options.Count >= maximum)
                break;
        }

        return options;
    }

    private static string BuildDocumentFocusInventory(
        EvidenceBundle bundle,
        int maximumWorkingEvidenceItems)
    {
        var context = new StringBuilder();
        context.AppendLine("DOCUMENTS OBSERVES UTILISABLES POUR LA RECUPERATION:");
        foreach (var option in BuildDocumentFocusOptions(
                     bundle,
                     maximumWorkingEvidenceItems))
        {
            context.Append("- ")
                .Append(option.EvidenceId)
                .Append(" | document=")
                .Append(TrimPromptValue(option.DocumentLabel, 160));
            if (!string.IsNullOrWhiteSpace(option.Evidence.DocId))
            {
                context.Append(" | docId=")
                    .Append(TrimPromptValue(option.Evidence.DocId, 100));
            }
            if (option.Evidence.PageStart is > 0)
                context.Append(" | page=").Append(option.Evidence.PageStart.Value);
            context.AppendLine();
        }
        context.AppendLine(
            "- GLOBAL | decision explicite d'explorer d'autres documents sans focus courant");
        return context.ToString().TrimEnd();
    }

    private static string DescribeDocumentFocusOptions(
        IReadOnlyList<DocumentFocusOption> options)
        => options.Count == 0
            ? "Aucun document observe n'est utilisable comme focus; choisis GLOBAL."
            : "Choisis GLOBAL pour une exploration inter-documents, ou preserve le document "
              + "d'une preuve ou d'un localisateur visible. Un localisateur de navigation "
              + "reste non citable tant que son contenu n'a pas ete lu: "
              + string.Join(
                  "; ",
                  options.Select(static option =>
                      option.EvidenceId + "=" + option.DocumentLabel));

    private static bool TryResolveDocumentFocus(
        EvidenceBundle bundle,
        int maximumWorkingEvidenceItems,
        string requestedEvidenceId,
        out EvidenceItem? focusedEvidence,
        out string error)
    {
        focusedEvidence = null;
        error = string.Empty;
        var normalized = CollapseDocumentFocusValue(requestedEvidenceId);
        if (string.Equals(
                normalized,
                GlobalDocumentFocusEvidenceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        focusedEvidence = BuildDocumentFocusOptions(
                bundle,
                maximumWorkingEvidenceItems)
            .FirstOrDefault(option => string.Equals(
                option.EvidenceId,
                normalized,
                StringComparison.OrdinalIgnoreCase))
            ?.Evidence;
        if (focusedEvidence is not null)
            return true;

        error = "research_transition_document_focus_invalid";
        return false;
    }

    private static string CollapseDocumentFocusValue(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(
                " ",
                value.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));
}
