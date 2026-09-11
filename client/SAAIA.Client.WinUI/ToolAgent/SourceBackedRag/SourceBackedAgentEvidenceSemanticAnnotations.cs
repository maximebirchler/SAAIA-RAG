namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static readonly string[] AgentSemanticSelectionHintKeys =
    {
        SemanticDisplayValueHint,
        SemanticCompatibleColumnLabelsHint,
        SemanticNavigationAnchorEligibilityHint
    };

    internal static EvidenceBundle PreserveAgentSemanticAnnotations(
        EvidenceBundle previousBundle,
        EvidenceBundle rebuiltBundle,
        out int preservedAnnotationCount)
    {
        preservedAnnotationCount = 0;
        if (previousBundle.Items.Count == 0 || rebuiltBundle.Items.Count == 0)
            return rebuiltBundle;

        var annotatedByEvidenceId = previousBundle.Items
            .Where(static item => AgentSemanticSelectionHintKeys.Any(key =>
                item.SelectionHints.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value)))
            .GroupBy(
                static item => item.EvidenceId,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        if (annotatedByEvidenceId.Count == 0)
            return rebuiltBundle;

        var preservedCount = 0;
        var rebuiltItems = rebuiltBundle.Items
            .Select(item =>
            {
                if (!annotatedByEvidenceId.TryGetValue(
                        item.EvidenceId,
                        out var previousItem)
                    || !HasSameImmutableEvidenceIdentity(previousItem, item))
                {
                    return item;
                }

                var selectionHints = new Dictionary<string, string>(
                    item.SelectionHints,
                    StringComparer.OrdinalIgnoreCase);
                var itemAnnotationPreserved = false;
                foreach (var key in AgentSemanticSelectionHintKeys)
                {
                    if (!previousItem.SelectionHints.TryGetValue(
                            key,
                            out var value)
                        || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }
                    selectionHints[key] = value;
                    itemAnnotationPreserved = true;
                }
                if (!itemAnnotationPreserved)
                    return item;
                preservedCount++;
                return item with { SelectionHints = selectionHints };
            })
            .ToArray();

        preservedAnnotationCount = preservedCount;
        return preservedCount == 0
            ? rebuiltBundle
            : rebuiltBundle with { Items = rebuiltItems };
    }

    private static bool HasSameImmutableEvidenceIdentity(
        EvidenceItem previousItem,
        EvidenceItem rebuiltItem)
        => string.Equals(
               previousItem.VisibleSourceKey,
               rebuiltItem.VisibleSourceKey,
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(
               previousItem.SourceKind?.Trim(),
               rebuiltItem.SourceKind?.Trim(),
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(
               previousItem.ChunkId?.Trim(),
               rebuiltItem.ChunkId?.Trim(),
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(
               previousItem.SourceHash?.Trim(),
               rebuiltItem.SourceHash?.Trim(),
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(
               previousItem.RevisionId?.Trim(),
               rebuiltItem.RevisionId?.Trim(),
               StringComparison.OrdinalIgnoreCase);
}
