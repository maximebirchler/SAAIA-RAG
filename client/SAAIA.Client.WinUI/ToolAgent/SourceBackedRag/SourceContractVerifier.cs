using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class SourceContractVerifier
{
    private static readonly Regex EvidenceIdPattern = new(
        @"(?<![A-Za-z0-9])\[?(E\d{1,4})\]?(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex WordPattern = new(
        @"[\p{L}\p{N}][\p{L}\p{N}'-]{1,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StructuredLinePattern = new(
        @"^\s*(?:[-*]|\d+[\.)]|\|)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MarkdownTableSeparatorPattern = new(
        @"^:?-{3,}:?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SourceLocatorPattern = new(
        @"(?:\((?:p|pp|page|pages)\.?\s*\d+[A-Za-z]?(?:\s*[-,]\s*\d+[A-Za-z]?)*\))|\b(?:p|pp|page|pages)\.?\s*\d+[A-Za-z]?(?:\s*[-,]\s*\d+[A-Za-z]?)*\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static SourceVerificationResult Verify(
        WriterDraft draft,
        EvidenceBundle bundle,
        SourceBackedIntake? intake = null,
        IReadOnlyList<string>? allowedEvidenceIds = null,
        bool enforceRequestedShape = true,
        bool requireEveryAllowedEvidenceIdExactlyOnce = false,
        bool allowMultipleEvidencePerVisibleSource = false,
        bool requireSeparateAtomicClaims = false,
        IReadOnlyList<IReadOnlyList<string>>? requiredEvidenceIdGroups = null,
        bool requireCitedAllowedEvidenceIdsAtMostOnce = false)
    {
        var errors = new List<SourceVerificationError>();
        var answerIds = ExtractEvidenceIds(draft.Answer).ToArray();
        var declaredIds = (draft.CitedEvidenceIds ?? Array.Empty<string>())
            .SelectMany(NormalizeDeclaredEvidenceIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var citedIds = answerIds
            .Concat(declaredIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.IsNullOrWhiteSpace(draft.Answer))
        {
            errors.Add(new SourceVerificationError(
                "empty_answer",
                "Writer returned an empty answer.",
                null,
                SourceBackedPipelineStep.SourceVerifier));
        }

        AddNamedDocumentScopeDisclosureError(errors, draft, intake);

        if (citedIds.Length == 0)
        {
            errors.Add(new SourceVerificationError(
                "missing_citations",
                "No evidence ids were cited by the writer.",
                null,
                SourceBackedPipelineStep.SourceVerifier));
        }

        if (IsCitationDominatedAnswer(draft.Answer, answerIds.Length))
        {
            errors.Add(new SourceVerificationError(
                "citation_dominated_answer",
                "Writer answer contains citation markers without enough substantive non-citation item/fact/action/value text.",
                null,
                SourceBackedPipelineStep.SourceVerifier));
        }

        foreach (var evidenceId in FindCitationOnlyStructuredItemEvidenceIds(
                     draft.Answer))
        {
            errors.Add(new SourceVerificationError(
                "citation_only_structured_item",
                "A structured item contains a citation marker but no substantive visible value.",
                evidenceId,
                SourceBackedPipelineStep.SourceVerifier));
        }

        if (enforceRequestedShape
            && intake is not null
            && RequiresStructuredAnswer(intake)
            && !LooksStructured(draft.Answer))
        {
            errors.Add(new SourceVerificationError(
                "requested_structure_not_realized",
                "User requested a structured plan/table/list/schedule, but the answer is a single paragraph or lacks repeated rows/items.",
                null,
                SourceBackedPipelineStep.SourceVerifier));
        }

        if (enforceRequestedShape
            && intake is not null
            && RequiresStructuredAnswer(intake))
        {
            var missingAxes = FindMissingRequestedStructureAxes(draft.Answer, intake);
            if (missingAxes.Count > 0)
            {
                errors.Add(new SourceVerificationError(
                    "requested_structure_not_realized",
                    "Structured answer is missing requested visible axis labels: " + string.Join(", ", missingAxes.Take(12)),
                    null,
                    SourceBackedPipelineStep.SourceVerifier));
            }

            // In exact source-item mode the stronger immutable-card contract below
            // validates the complete title/EvidenceId pair. A legitimate one-word
            // source title must not be rewritten merely to satisfy a word-count hint.
            var exactCanonicalCardSelection = allowedEvidenceIds is { Count: > 0 }
                && allowedEvidenceIds
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .All(id => bundle.ById.TryGetValue(id, out var evidence)
                               && string.Equals(
                                   evidence.SourceKind,
                                   "canonical_content_card",
                                   StringComparison.OrdinalIgnoreCase));
            var thinCells = SourceBackedCanonicalContentCardInventory.IsEnabled(intake)
                            || exactCanonicalCardSelection
                ? Array.Empty<string>()
                : FindThinMarkdownTableEvidenceCells(draft.Answer, intake);
            if (thinCells.Count > 0)
            {
                errors.Add(new SourceVerificationError(
                    "thin_structured_cell",
                    "Structured answer has evidence-backed table cells that look like source labels, page locators or too-thin placeholders instead of concrete answer content: " + string.Join("; ", thinCells.Take(6)),
                    null,
                    SourceBackedPipelineStep.SourceVerifier));
            }

            var incompleteCells = FindIncompleteRequestedMarkdownTableCells(draft.Answer, intake);
            if (incompleteCells.Count > 0)
            {
                errors.Add(new SourceVerificationError(
                    "missing_structured_cell",
                    "Structured answer has requested table cells that are empty or placeholder-only: " + string.Join("; ", incompleteCells.Take(8)),
                    null,
                    SourceBackedPipelineStep.SourceVerifier));
            }

            if (SourceBackedCanonicalContentCardInventory.IsEnabled(intake))
            {
                var nonCanonicalCells = FindNonCanonicalStructuredCellClaims(
                    draft.Answer,
                    intake,
                    bundle,
                    allowedEvidenceIds,
                    out var hasCanonicalCards);
                if (!hasCanonicalCards)
                {
                    errors.Add(new SourceVerificationError(
                        "missing_canonical_content_cards",
                        "The LLM selected exact source-item mode, but the selected evidence contains no immutable named content cards.",
                        null,
                        SourceBackedPipelineStep.SourceVerifier));
                }
                else if (nonCanonicalCells.Count > 0)
                {
                    errors.Add(new SourceVerificationError(
                        "non_canonical_structured_cell",
                        "Exact source-item mode contains cells that are not an immutable card title paired with that card's EvidenceId: "
                        + string.Join("; ", nonCanonicalCells.Take(6)),
                        null,
                        SourceBackedPipelineStep.SourceVerifier));
                }

                var diversityProblems = FindCanonicalStructuredDiversityProblems(
                    draft.Answer,
                    intake);
                if (diversityProblems.Count > 0)
                {
                    errors.Add(new SourceVerificationError(
                        "insufficient_structured_value_diversity",
                        "The rendered exact-item grid does not satisfy the LLM-authored diversity policy: "
                        + string.Join("; ", diversityProblems.Take(8)),
                        null,
                        SourceBackedPipelineStep.SourceVerifier));
                }
            }

        }

        if (enforceRequestedShape
            && intake is not null
            && RequiresStructuredAnswer(intake)
            && (draft.Answer?.Length ?? 0) > 2400)
        {
            errors.Add(new SourceVerificationError(
                "answer_too_long",
                "Structured source-backed answer is too long for the requested UI-friendly format.",
                null,
                SourceBackedPipelineStep.SourceVerifier));
        }

        foreach (var declaredId in declaredIds.Except(answerIds, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new SourceVerificationError(
                "missing_inline_citation",
                $"Evidence id '{declaredId}' was declared but does not appear in the visible answer text.",
                declaredId,
                SourceBackedPipelineStep.SourceVerifier));
        }

        var evidenceById = bundle.ById;
        var allowedIds = allowedEvidenceIds is null
            ? null
            : allowedEvidenceIds
                .SelectMany(NormalizeDeclaredEvidenceIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddSemanticSelectionCitationErrors(
            errors,
            draft.Answer,
            allowedIds,
            requireEveryAllowedEvidenceIdExactlyOnce,
            requiredEvidenceIdGroups,
            requireCitedAllowedEvidenceIdsAtMostOnce,
            requireSeparateAtomicClaims);

        var citedEvidence = new List<EvidenceItem>();
        foreach (var id in citedIds)
        {
            if (evidenceById.TryGetValue(id, out var item))
            {
                citedEvidence.Add(item);
                if (allowedIds is not null && !allowedIds.Contains(id))
                {
                    errors.Add(new SourceVerificationError(
                        "unselected_evidence_id",
                        $"Evidence id '{id}' was cited but was not selected by the evidence judge for writing.",
                        id,
                        SourceBackedPipelineStep.SourceVerifier));
                }

                continue;
            }

            errors.Add(new SourceVerificationError(
                "unknown_evidence_id",
                $"Evidence id '{id}' is not present in the EvidenceBundle.",
                id,
                SourceBackedPipelineStep.SourceVerifier));
        }

        foreach (var item in citedEvidence)
        {
            AddCanonicalEvidenceIdentityErrors(errors, item);

            if (item.RiskFlags.Any(static flag => string.Equals(flag, "orientation_only", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new SourceVerificationError(
                    "orientation_only_source_cited",
                    $"Evidence id '{item.EvidenceId}' is an orientation/navigation item and cannot be used as final factual proof.",
                    item.EvidenceId,
                    SourceBackedPipelineStep.SourceVerifier));
            }

            if (string.IsNullOrWhiteSpace(item.DocPath) && string.IsNullOrWhiteSpace(item.DocName))
            {
                errors.Add(new SourceVerificationError(
                    "missing_visible_source",
                    $"Evidence id '{item.EvidenceId}' has no visible document source.",
                    item.EvidenceId,
                    SourceBackedPipelineStep.SourceVerifier));
            }

            if (item.PageStart is null or <= 0)
            {
                errors.Add(new SourceVerificationError(
                    "missing_visible_page",
                    $"Evidence id '{item.EvidenceId}' has no valid visible page.",
                    item.EvidenceId,
                SourceBackedPipelineStep.SourceVerifier));
            }

            AddNamedDocumentIdentityError(errors, item, intake);
        }

        if (!allowMultipleEvidencePerVisibleSource)
            AddDuplicateVisibleSourceErrors(errors, citedEvidence);

        return new SourceVerificationResult(errors.Count == 0, errors, citedEvidence);
    }

    public static IReadOnlyList<string> ExtractEvidenceIds(string? answer)
        => EvidenceIdPattern.Matches(answer ?? string.Empty)
            .Select(static match => match.Groups[1].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<string> NormalizeDeclaredEvidenceIds(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
            yield break;

        var matches = EvidenceIdPattern.Matches(rawId);
        if (matches.Count > 0)
        {
            foreach (Match match in matches)
                yield return match.Groups[1].Value.ToUpperInvariant();
            yield break;
        }

        yield return rawId.Trim().ToUpperInvariant();
    }

    private static bool IsCitationDominatedAnswer(string? answer, int visibleCitationCount)
    {
        if (visibleCitationCount < 3 || string.IsNullOrWhiteSpace(answer))
            return false;

        var textWithoutCitations = EvidenceIdPattern.Replace(answer, " ");
        var substantiveWordCount = WordPattern.Matches(textWithoutCitations)
            .Count(static match => match.Value.Any(char.IsLetterOrDigit));
        return substantiveWordCount < Math.Max(6, visibleCitationCount);
    }

    private static IReadOnlyList<string> FindCitationOnlyStructuredItemEvidenceIds(
        string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return Array.Empty<string>();

        var evidenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in answer.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !EvidenceIdPattern.IsMatch(line))
                continue;

            AddCitationOnlyMarkdownTableCellEvidenceIds(line, evidenceIds);

            // Markdown table cells were checked independently above. Treating the
            // last colon of the whole row as a list key/value separator makes a
            // legitimate visible label such as "Conseils : [E20]" look like a
            // citation-only value.
            if (line.Contains('|'))
                continue;

            var content = line.TrimStart('-', '*', ' ', '\t');
            var colonIndex = content.LastIndexOf(':');
            if (colonIndex < 0)
                continue;

            var value = content[(colonIndex + 1)..];
            if (EvidenceIdPattern.IsMatch(value) && CountSubstantiveWordsWithoutEvidenceIds(value) == 0)
            {
                foreach (Match match in EvidenceIdPattern.Matches(value))
                    evidenceIds.Add(match.Groups[1].Value.ToUpperInvariant());
            }
        }

        return evidenceIds.ToArray();
    }

    private static void AddCitationOnlyMarkdownTableCellEvidenceIds(
        string line,
        ISet<string> evidenceIds)
    {
        if (!line.Contains('|'))
            return;

        foreach (var rawCell in line.Split('|'))
        {
            var cell = rawCell.Trim();
            if (cell.Length == 0 || MarkdownTableSeparatorPattern.IsMatch(cell))
                continue;

            if (EvidenceIdPattern.IsMatch(cell) && CountSubstantiveWordsWithoutEvidenceIds(cell) == 0)
            {
                foreach (Match match in EvidenceIdPattern.Matches(cell))
                    evidenceIds.Add(match.Groups[1].Value.ToUpperInvariant());
            }
        }
    }

    private static int CountSubstantiveWordsWithoutEvidenceIds(string text)
    {
        var withoutEvidenceIds = EvidenceIdPattern.Replace(text, " ");
        return WordPattern.Matches(withoutEvidenceIds)
            .Count(static match => match.Value.Any(char.IsLetterOrDigit));
    }

    internal static bool HasSubstantiveVisibleValue(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && CountSubstantiveWordsWithoutEvidenceIds(value) > 0;

    private static void AddDuplicateVisibleSourceErrors(
        List<SourceVerificationError> errors,
        IReadOnlyList<EvidenceItem> citedEvidence)
    {
        foreach (var group in citedEvidence
                     .Where(HasCompleteVisibleSource)
                     .GroupBy(static item => item.VisibleSourceKey, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToArray();
            if (items.Length <= 1)
                continue;

            var keptId = items[0].EvidenceId;
            foreach (var duplicate in items.Skip(1))
            {
                errors.Add(new SourceVerificationError(
                    "duplicate_visible_source",
                    $"Evidence id '{duplicate.EvidenceId}' cites the same visible source as '{keptId}'. Use only one evidence id per document/page source.",
                    duplicate.EvidenceId,
                    SourceBackedPipelineStep.SourceVerifier));
            }
        }
    }

    private static bool HasCompleteVisibleSource(EvidenceItem item)
        => (!string.IsNullOrWhiteSpace(item.DocPath) || !string.IsNullOrWhiteSpace(item.DocName))
           && item.PageStart is > 0;

}
