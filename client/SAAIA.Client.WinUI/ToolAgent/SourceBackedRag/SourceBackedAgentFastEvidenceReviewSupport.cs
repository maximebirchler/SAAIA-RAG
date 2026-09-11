using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static void AppendFastEvidenceQuestionFocusContext(
        StringBuilder context,
        SourceBackedIntake intake)
    {
        if (!string.IsNullOrWhiteSpace(intake.RequestedDocumentName))
        {
            context.Append("DOCUMENT_DEMANDE: ")
                .AppendLine(TrimPromptValue(
                    intake.RequestedDocumentName,
                    180));
        }

        if (SourceBackedQuestionFocus.IsDocumentFamilyOrTypeQuestion(intake))
            context.AppendLine("QUESTION_FOCUS: document_family_or_type");
    }

    private static FastEvidenceProtocolDecision
        ReadFastEvidenceProtocolDecision(
            SourceBackedAgentCompletion completion)
    {
        if (completion.ToolCalls.Count > 0)
            return new FastEvidenceProtocolDecision("", "", "", "", false);

        var content = (completion.Content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        if (content.StartsWith("```", StringComparison.Ordinal)
            && content.EndsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = content.IndexOf('\n');
            content = firstBreak >= 0
                ? content[(firstBreak + 1)..^3].Trim()
                : string.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return new FastEvidenceProtocolDecision("", "", "", "", false);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 4)
            {
                return new FastEvidenceProtocolDecision("", "", "", "", false);
            }

            var action = ReadFastEvidenceString(root, "action").ToLowerInvariant();
            var evidenceId = ReadFastEvidenceString(root, "evidenceId");
            var anchorId = ReadFastEvidenceString(root, "anchorId");
            var text = ReadFastEvidenceString(root, "text");
            var protocolValid = action is
                                    "answer" or "writer" or "clarify" or "context" or "research"
                                && evidenceId.Length > 0
                                && anchorId.Length > 0
                                && text.Length <= 300
                                && (action is "answer" or "clarify" or "context" or "research"
                                    ? text.Length > 0
                                    : true);
            return new FastEvidenceProtocolDecision(
                action,
                evidenceId,
                anchorId,
                text,
                protocolValid);
        }
    }

    private static string ReadFastEvidenceString(
        JsonElement root,
        string propertyName)
        => TryGetPropertyIgnoreCase(
               root,
               propertyName,
               out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<FastEvidenceCandidate>
        BuildFastEvidenceCandidates(
            EvidenceBundle bundle,
            IReadOnlyList<string> candidateEvidenceIds,
            IReadOnlySet<string> semanticallyRejectedEvidenceIds,
            int maximumCandidates = 3,
            int? maximumSourceWindowItems = 2)
    {
        var eligibleEvidence = candidateEvidenceIds
            .Where(id => !semanticallyRejectedEvidenceIds.Contains(id))
            .Select(id => bundle.ById.TryGetValue(id, out var item) ? item : null)
            .Where(static item => item is not null
                                  && !item.RiskFlags.Contains(
                                      "orientation_only",
                                      StringComparer.OrdinalIgnoreCase))
            .Cast<EvidenceItem>()
            .ToArray();

        return eligibleEvidence
            .GroupBy(
                static item => BuildFastEvidenceSourceWindowKey(item),
                StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maximumCandidates))
            .Select(group =>
            {
                var first = group.First();
                var anchorChunkId = first.CodeHints.TryGetValue(
                    "source_window_anchor_chunk_id",
                    out var anchorValue)
                    ? anchorValue?.Trim()
                    : null;
                var representative =
                    !string.IsNullOrWhiteSpace(anchorChunkId)
                        ? bundle.Items.FirstOrDefault(item =>
                            !semanticallyRejectedEvidenceIds.Contains(
                                item.EvidenceId)
                            && string.Equals(
                                BuildFastEvidenceSourceWindowKey(item),
                                BuildFastEvidenceSourceWindowKey(first),
                                StringComparison.OrdinalIgnoreCase)
                            && string.Equals(
                                item.ChunkId,
                                anchorChunkId,
                                StringComparison.OrdinalIgnoreCase))
                          ?? first
                        : first;
                var sourceWindowItems = bundle.Items
                    .Where(item =>
                        !semanticallyRejectedEvidenceIds.Contains(
                            item.EvidenceId)
                        && !item.RiskFlags.Contains(
                            "orientation_only",
                            StringComparer.OrdinalIgnoreCase)
                        && string.Equals(
                            BuildFastEvidenceSourceWindowKey(item),
                            BuildFastEvidenceSourceWindowKey(representative),
                            StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(
                        static item => item.NormalizedExcerpt,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var sourceWindow = maximumSourceWindowItems.HasValue
                    ? SelectFastEvidenceSourceWindow(
                        sourceWindowItems,
                        anchorChunkId,
                        Math.Max(1, maximumSourceWindowItems.Value))
                    : OrderSourceWindowInDocumentOrder(sourceWindowItems)
                        .ToArray();
                return new FastEvidenceCandidate(
                    representative,
                    sourceWindow);
            })
            .ToArray();
    }

    private static IReadOnlyList<EvidenceItem>
        SelectFastEvidenceSourceWindow(
            IReadOnlyCollection<EvidenceItem> items,
            string? anchorChunkId,
            int maximumItems)
    {
        var ordered = OrderSourceWindowInDocumentOrder(items).ToArray();
        if (ordered.Length <= maximumItems)
            return ordered;

        var anchorIndex = Array.FindIndex(
            ordered,
            item => string.Equals(
                item.ChunkId,
                anchorChunkId,
                StringComparison.OrdinalIgnoreCase));
        if (anchorIndex < 0)
            return ordered.Take(maximumItems).ToArray();
        if (maximumItems == 2 && anchorIndex > 0)
        {
            return new[] { ordered[0], ordered[anchorIndex] }
                .DistinctBy(
                    static item => item.ChunkId,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var selected = new HashSet<int> { anchorIndex };
        for (var distance = 1;
             selected.Count < maximumItems
             && (anchorIndex - distance >= 0
                 || anchorIndex + distance < ordered.Length);
             distance++)
        {
            if (anchorIndex - distance >= 0)
                selected.Add(anchorIndex - distance);
            if (selected.Count < maximumItems
                && anchorIndex + distance < ordered.Length)
            {
                selected.Add(anchorIndex + distance);
            }
        }

        return selected
            .OrderBy(static index => index)
            .Select(index => ordered[index])
            .ToArray();
    }

    private static string BuildFastEvidenceReviewSignature(
        EvidenceBundle bundle,
        IReadOnlyList<string> candidateEvidenceIds,
        IReadOnlySet<string> semanticallyRejectedEvidenceIds,
        int maximumCandidates = 3)
        => string.Join(
            "|",
            BuildFastEvidenceCandidates(
                    bundle,
                    candidateEvidenceIds,
                    semanticallyRejectedEvidenceIds,
                    maximumCandidates)
                .Select(static candidate => string.Join(
                    "+",
                    candidate.SourceWindow.Select(static item =>
                        item.EvidenceId))));

    private static IReadOnlyList<string>
        BuildFastEvidenceReviewCandidateInput(
            EvidenceBundle bundle,
            IReadOnlyList<string> newlyObservedEvidenceIds,
            IReadOnlyList<string> carriedCandidateIds,
            IReadOnlySet<string> semanticallyRejectedEvidenceIds)
    {
        const int maximumInputEvidenceIds = 24;

        var newlyObservedMechanicalIdentities = newlyObservedEvidenceIds
            .Select(id => bundle.ById.TryGetValue(id, out var item)
                ? item
                : null)
            .Where(static item => item is not null)
            .Cast<EvidenceItem>()
            .Select(BuildFastEvidenceCarryIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eligibleCarriedCandidateIds = BuildFastEvidenceCandidates(
                bundle,
                carriedCandidateIds,
                semanticallyRejectedEvidenceIds)
            .Where(candidate =>
                !newlyObservedMechanicalIdentities.Contains(
                    BuildFastEvidenceCarryIdentityKey(
                        candidate.Representative)))
            .Select(static candidate =>
                candidate.Representative.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // A protocol-invalid review has made no usable semantic decision.
        // Keep every bounded, still-eligible carried candidate ahead of the
        // new batch so the widened review window cannot silently evict one.
        return eligibleCarriedCandidateIds
            .Concat(newlyObservedEvidenceIds)
            .Where(id =>
                !string.IsNullOrWhiteSpace(id)
                && !semanticallyRejectedEvidenceIds.Contains(id)
                && bundle.ById.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximumInputEvidenceIds)
            .ToArray();
    }

    internal static int ResolveFastEvidenceReviewCandidateLimitForTests(
        SourceBackedIntake intake,
        int carriedCandidateCount)
        => ResolveFastEvidenceReviewCandidateLimit(
            intake,
            carriedCandidateCount);

    private static int ResolveFastEvidenceReviewCandidateLimit(
        SourceBackedIntake intake,
        int carriedCandidateCount)
    {
        if (carriedCandidateCount > 0)
            return 6;

        var resolution = intake.RequestedDocumentResolution;
        return !string.IsNullOrWhiteSpace(intake.RequestedDocumentName)
               && resolution is
               {
                   Status: SourceBackedDocumentResolutionStatus.Resolved,
                   CatalogObservationComplete: true
               }
               && resolution.Candidates.Count == 1
            ? 6
            : 3;
    }

    private static string BuildFastEvidenceCarryIdentityKey(
        EvidenceItem item)
    {
        var stableAnchor = !string.IsNullOrWhiteSpace(item.ContentCardId)
            ? "content-card:" + item.ContentCardId.Trim()
            : !string.IsNullOrWhiteSpace(item.ChunkId)
                ? "chunk:" + item.ChunkId.Trim()
                : !string.IsNullOrWhiteSpace(item.AnchorId)
                    ? "anchor:" + item.AnchorId.Trim()
                    : string.Empty;
        return stableAnchor.Length > 0
            ? item.VisibleSourceKey + "|" + stableAnchor
            : "evidence:" + item.EvidenceId;
    }

    private static string BuildFastEvidenceSourceWindowKey(
        EvidenceItem item)
        => BuildAgentSourceWindowKey(item);

    private static string BuildFastEvidenceSourceWindow(
        FastEvidenceCandidate candidate)
    {
        var anchorChunkId =
            candidate.Representative.CodeHints.TryGetValue(
                "source_window_anchor_chunk_id",
                out var configuredAnchor)
                ? configuredAnchor?.Trim()
                : null;
        if (string.IsNullOrWhiteSpace(anchorChunkId))
            anchorChunkId = candidate.Representative.ChunkId;

        return string.Join(
            " || ",
            candidate.SourceWindow
                .Take(2)
                .Select((item, index) =>
                {
                    var isAnchor = string.Equals(
                        item.ChunkId,
                        anchorChunkId,
                        StringComparison.OrdinalIgnoreCase);
                    return $"EXTRAIT {index + 1} [{item.EvidenceId}]"
                           + (isAnchor ? " (ancre)" : string.Empty)
                           + BuildFastEvidenceWindowMetadata(item)
                           + ": "
                           + CompactReviewExcerpt(
                               item.Excerpt,
                               isAnchor ? 340 : 70);
                }));
    }

    private static string BuildFastEvidenceReviewMetadata(
        EvidenceItem item,
        bool includeDocumentPath)
    {
        if (includeDocumentPath)
            return BuildEvidenceDocumentMetadata(item);

        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.DocName))
        {
            values.Add("f=" + TrimPromptValue(item.DocName, 60));
        }
        if (item.PageStart is > 0)
        {
            values.Add("p=" + item.PageStart.Value);
        }

        return values.Count > 0
            ? string.Join(" | ", values)
            : "source";
    }

    private static string BuildFastEvidenceWindowMetadata(
        EvidenceItem item)
    {
        item.CodeHints.TryGetValue(
            "section_title",
            out var sectionTitle);
        item.CodeHints.TryGetValue(
            "heading_path",
            out var headingPath);
        var metadata = new List<string>();
        if (!string.IsNullOrWhiteSpace(sectionTitle))
        {
            metadata.Add(
                "section="
                + TrimPromptValue(sectionTitle, 80));
        }
        if (!string.IsNullOrWhiteSpace(headingPath)
            && !string.Equals(
                headingPath,
                sectionTitle,
                StringComparison.OrdinalIgnoreCase))
        {
            metadata.Add(
                "hierarchie="
                + TrimPromptValue(headingPath, 100));
        }

        return metadata.Count == 0
            ? string.Empty
            : " [" + string.Join("; ", metadata) + "]";
    }

    private static string CompactReviewExcerpt(
        string? value,
        int maximumCharacters)
    {
        var normalized = string.Join(
            " ",
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries));
        if (normalized.Length <= maximumCharacters)
            return normalized;

        var tailCharacters = Math.Min(
            120,
            Math.Max(24, maximumCharacters / 3));
        var headCharacters = maximumCharacters - tailCharacters - 5;
        return normalized[..headCharacters].TrimEnd()
               + " ... "
               + normalized[^tailCharacters..].TrimStart();
    }

}
