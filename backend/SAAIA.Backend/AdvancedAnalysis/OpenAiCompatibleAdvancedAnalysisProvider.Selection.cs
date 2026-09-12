using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record DistinctSelectionValidation(
        bool IsValid,
        int MissingIdentityCount,
        int DuplicateIdentityCount,
        int UnsupportedIdentityCount,
        IReadOnlyList<string> UnsupportedIdentities);

    private static AdvancedAnalysisProviderResult CanonicalizeDistinctSelectedItems(
        AdvancedAnalysisProviderRequest request,
        AdvancedAnalysisProviderResult result,
        IReadOnlyList<PromptEvidenceItem> evidence)
    {
        if (!RequiresDistinctStructuredSelection(request.Handoff.Load)
            || !string.Equals(result.Outcome, "answered", StringComparison.Ordinal)
            || result.Claims.Count == 0)
        {
            return result;
        }

        var titleByEvidenceId = evidence
            .Where(static item => !string.IsNullOrWhiteSpace(item.EvidenceId)
                                  && !string.IsNullOrWhiteSpace(item.CandidateTitle))
            .GroupBy(static item => item.EvidenceId!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static item => NormalizeDisplayWhitespace(
                        item.CandidateTitle))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var used = result.Claims
            .Select(static claim => NormalizeClaimText(
                claim.SelectedItem ?? string.Empty))
            .Where(static item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var claims = result.Claims.ToArray();
        var answerText = result.AnswerText;
        var changed = false;
        for (var index = 0; index < claims.Length; index++)
        {
            var claim = claims[index];
            var selectedItem = (claim.SelectedItem ?? string.Empty).Trim();
            var normalizedSelectedItem = NormalizeClaimText(selectedItem);
            if (normalizedSelectedItem.Length == 0
                || claim.EvidenceIds.Any(evidenceId => evidence.Any(item =>
                    string.Equals(item.EvidenceId, evidenceId, StringComparison.Ordinal)
                    && (NormalizeClaimText(item.Content).Contains(
                            normalizedSelectedItem,
                            StringComparison.Ordinal)
                        || NormalizeClaimText(item.CandidateTitle ?? string.Empty)
                            .Contains(
                                normalizedSelectedItem,
                                StringComparison.Ordinal)))))
            {
                continue;
            }

            var candidates = claim.EvidenceIds
                .Where(titleByEvidenceId.ContainsKey)
                .SelectMany(evidenceId => titleByEvidenceId[evidenceId])
                .Select(title => new
                {
                    Title = title,
                    Normalized = NormalizeClaimText(title),
                    Similarity = ComputeNormalizedEditSimilarity(
                        normalizedSelectedItem,
                        NormalizeClaimText(title))
                })
                .Where(candidate => candidate.Normalized.Length > 0
                                    && !used.Contains(candidate.Normalized))
                .OrderByDescending(static candidate => candidate.Similarity)
                .ThenBy(static candidate => candidate.Normalized, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0
                || candidates[0].Similarity < 0.82
                || (candidates.Length > 1
                    && candidates[0].Similarity - candidates[1].Similarity < 0.08)
                || CountOrdinalIgnoreCase(answerText, selectedItem) != 1)
            {
                return result;
            }

            var replacement = candidates[0];
            used.Remove(normalizedSelectedItem);
            used.Add(replacement.Normalized);
            answerText = ReplaceOnceOrdinalIgnoreCase(
                answerText,
                selectedItem,
                replacement.Title);
            claims[index] = new AdvancedAnalysisResultClaim
            {
                ClaimId = claim.ClaimId,
                SelectedItem = replacement.Title,
                Text = CountOrdinalIgnoreCase(claim.Text, selectedItem) == 1
                    ? ReplaceOnceOrdinalIgnoreCase(
                        claim.Text,
                        selectedItem,
                        replacement.Title)
                    : claim.Text,
                EvidenceIds = claim.EvidenceIds
            };
            changed = true;
        }

        return changed
            ? new AdvancedAnalysisProviderResult
            {
                Outcome = result.Outcome,
                AnswerText = answerText,
                Claims = claims.ToList()
            }
            : result;
    }

    private static AdvancedAnalysisProviderResult
        RebindDistinctSelectedItemsToSupportingEvidence(
            AdvancedAnalysisProviderRequest request,
            AdvancedAnalysisProviderResult result,
            IReadOnlyList<PromptEvidenceItem> evidence)
    {
        if (!RequiresDistinctStructuredSelection(request.Handoff.Load)
            || !string.Equals(result.Outcome, "answered", StringComparison.Ordinal)
            || result.Claims.Count == 0)
        {
            return result;
        }

        var coordinateColumns = BuildStructuredClaimCoordinates(
                request.Handoff.Load)
            .ToDictionary(
                static item => item.ClaimId,
                static item => item.ColumnLabel,
                StringComparer.Ordinal);
        var used = result.Claims
            .Select(static claim => NormalizeClaimText(
                claim.SelectedItem ?? string.Empty))
            .Where(static item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var claims = result.Claims.ToArray();
        var answerText = result.AnswerText;
        var changed = false;
        for (var index = 0; index < claims.Length; index++)
        {
            var claim = claims[index];
            var selectedItem = (claim.SelectedItem ?? string.Empty).Trim();
            var normalizedSelectedItem = NormalizeClaimText(selectedItem);
            if (normalizedSelectedItem.Length == 0)
                continue;

            coordinateColumns.TryGetValue(claim.ClaimId, out var targetColumn);
            var matches = evidence
                .Select(item => BuildSelectedItemEvidenceMatch(
                    selectedItem,
                    item,
                    targetColumn))
                .Where(static item => item is not null)
                .Select(static item => item!)
                .OrderByDescending(static item => item.TargetColumnMatch)
                .ThenByDescending(static item => item.MatchRank)
                .ThenByDescending(static item => item.Similarity)
                .ThenBy(static item => item.EvidenceId, StringComparer.Ordinal)
                .ToArray();
            if (matches.Length == 0)
                continue;

            var citedMatches = matches
                .Where(item => claim.EvidenceIds.Contains(
                    item.EvidenceId,
                    StringComparer.Ordinal))
                .ToArray();
            var candidates = citedMatches.Length > 0 ? citedMatches : matches;
            var best = candidates[0];
            var bestCanonicalIdentity = NormalizeClaimText(best.CanonicalItem);
            var competing = candidates
                .Skip(1)
                .FirstOrDefault(item => !string.Equals(
                    NormalizeClaimText(item.CanonicalItem),
                    bestCanonicalIdentity,
                    StringComparison.Ordinal));
            if (competing is not null
                && competing.TargetColumnMatch == best.TargetColumnMatch
                && competing.MatchRank == best.MatchRank
                && best.Similarity - competing.Similarity < 0.08)
            {
                continue;
            }

            var canonicalItem = NormalizeDisplayWhitespace(
                best.CanonicalItem);
            var normalizedCanonicalItem = NormalizeClaimText(canonicalItem);
            var itemChanged = !string.Equals(
                canonicalItem,
                selectedItem,
                StringComparison.Ordinal);
            if (itemChanged
                && (normalizedCanonicalItem.Length == 0
                    || (used.Contains(normalizedCanonicalItem)
                        && !string.Equals(
                            normalizedCanonicalItem,
                            normalizedSelectedItem,
                            StringComparison.Ordinal))
                    || CountOrdinalIgnoreCase(answerText, selectedItem) != 1))
            {
                continue;
            }

            if (itemChanged)
            {
                used.Remove(normalizedSelectedItem);
                used.Add(normalizedCanonicalItem);
                answerText = ReplaceOnceOrdinalIgnoreCase(
                    answerText,
                    selectedItem,
                    canonicalItem);
            }
            var evidenceChanged = claim.EvidenceIds.Count != 1
                                  || !string.Equals(
                                      claim.EvidenceIds[0],
                                      best.EvidenceId,
                                      StringComparison.Ordinal);
            if (!itemChanged && !evidenceChanged)
                continue;

            claims[index] = new AdvancedAnalysisResultClaim
            {
                ClaimId = claim.ClaimId,
                SelectedItem = itemChanged ? canonicalItem : selectedItem,
                Text = itemChanged
                       && CountOrdinalIgnoreCase(claim.Text, selectedItem) == 1
                    ? ReplaceOnceOrdinalIgnoreCase(
                        claim.Text,
                        selectedItem,
                        canonicalItem)
                    : claim.Text,
                EvidenceIds = [best.EvidenceId]
            };
            changed = true;
        }

        return changed
            ? new AdvancedAnalysisProviderResult
            {
                Outcome = result.Outcome,
                AnswerText = answerText,
                Claims = claims.ToList()
            }
            : result;
    }

    private static SelectedItemEvidenceMatch? BuildSelectedItemEvidenceMatch(
        string selectedItem,
        PromptEvidenceItem evidence,
        string? targetColumn)
    {
        var evidenceId = evidence.EvidenceId?.Trim() ?? string.Empty;
        if (evidenceId.Length == 0)
            return null;

        var normalizedSelected = NormalizeClaimText(selectedItem);
        var foldedSelected = NormalizeClaimText(FoldDiacritics(selectedItem));
        if (normalizedSelected.Length == 0 || foldedSelected.Length == 0)
            return null;

        var targetColumnMatch = !string.IsNullOrWhiteSpace(targetColumn)
                                && evidence.TargetColumns.Contains(
                                    targetColumn,
                                    StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(evidence.CandidateTitle))
        {
            var title = NormalizeDisplayWhitespace(evidence.CandidateTitle);
            var normalizedTitle = NormalizeClaimText(title);
            var foldedTitle = NormalizeClaimText(FoldDiacritics(title));
            if (string.Equals(
                    normalizedSelected,
                    normalizedTitle,
                    StringComparison.Ordinal))
            {
                return new SelectedItemEvidenceMatch(
                    evidenceId,
                    title,
                    targetColumnMatch,
                    4,
                    1);
            }
            if (string.Equals(
                    foldedSelected,
                    foldedTitle,
                    StringComparison.Ordinal))
            {
                return new SelectedItemEvidenceMatch(
                    evidenceId,
                    title,
                    targetColumnMatch,
                    3,
                    1);
            }
            var titleSimilarity = ComputeNormalizedEditSimilarity(
                foldedSelected,
                foldedTitle);
            return titleSimilarity >= 0.82
                ? new SelectedItemEvidenceMatch(
                    evidenceId,
                    title,
                    targetColumnMatch,
                    2,
                    titleSimilarity)
                : null;
        }

        var contentPhrase = FindNearContentPhrase(selectedItem, evidence.Content);
        return contentPhrase is null
            ? null
            : new SelectedItemEvidenceMatch(
                evidenceId,
                contentPhrase.Value.Text,
                targetColumnMatch,
                contentPhrase.Value.IsExact ? 3 : 1,
                contentPhrase.Value.Similarity);
    }

    private static ContentPhraseMatch? FindNearContentPhrase(
        string? selectedItem,
        string? content)
    {
        var safeSelectedItem = selectedItem ?? string.Empty;
        var safeContent = content ?? string.Empty;
        var selectedTokens = Regex.Matches(
            safeSelectedItem,
            @"[\p{L}\p{N}]+",
            RegexOptions.CultureInvariant);
        if (selectedTokens.Count == 0 || selectedTokens.Count > 24)
            return null;
        var contentTokens = Regex.Matches(
            safeContent,
            @"[\p{L}\p{N}]+",
            RegexOptions.CultureInvariant);
        if (contentTokens.Count < selectedTokens.Count)
            return null;

        var normalizedSelected = NormalizeClaimText(safeSelectedItem);
        var foldedSelected = NormalizeClaimText(FoldDiacritics(safeSelectedItem));
        if (ContainsSelectedTokenSequenceWithIgnorableInterruptions(
                safeSelectedItem,
                safeContent))
        {
            return new ContentPhraseMatch(
                NormalizeDisplayWhitespace(safeSelectedItem),
                true,
                1);
        }
        ContentPhraseMatch? best = null;
        ContentPhraseMatch? second = null;
        for (var index = 0;
             index <= contentTokens.Count - selectedTokens.Count;
             index++)
        {
            var first = contentTokens[index];
            var last = contentTokens[index + selectedTokens.Count - 1];
            var phrase = safeContent.Substring(
                first.Index,
                last.Index + last.Length - first.Index);
            var normalizedPhrase = NormalizeClaimText(phrase);
            var foldedPhrase = NormalizeClaimText(FoldDiacritics(phrase));
            var isExact = string.Equals(
                              normalizedPhrase,
                              normalizedSelected,
                              StringComparison.Ordinal)
                          || string.Equals(
                              foldedPhrase,
                              foldedSelected,
                              StringComparison.Ordinal);
            var similarity = isExact
                ? 1
                : ComputeNormalizedEditSimilarity(
                    foldedSelected,
                    foldedPhrase);
            var candidate = new ContentPhraseMatch(
                NormalizeDisplayWhitespace(phrase),
                isExact,
                similarity);
            if (best is null || candidate.Similarity > best.Value.Similarity)
            {
                second = best;
                best = candidate;
            }
            else if (second is null
                     || candidate.Similarity > second.Value.Similarity)
            {
                second = candidate;
            }
        }

        if (best is null)
            return null;
        if (best.Value.IsExact)
            return best;
        if (selectedTokens.Count < 2
            || best.Value.Similarity < 0.90
            || (second is not null
                && best.Value.Similarity - second.Value.Similarity < 0.08
                && !string.Equals(
                    NormalizeClaimText(best.Value.Text),
                    NormalizeClaimText(second.Value.Text),
                    StringComparison.Ordinal)))
        {
            return null;
        }
        return best;
    }

    private static bool ContainsSelectedTokenSequenceWithIgnorableInterruptions(
        string selectedItem,
        string content)
    {
        var selectedTokens = Regex.Matches(
                FoldDiacritics(selectedItem),
                @"[\p{L}\p{N}]+",
            RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value.ToLowerInvariant())
            .ToArray();
        var contentTokens = Regex.Matches(
                FoldDiacritics(content),
                @"[\p{L}\p{N}]+",
            RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value.ToLowerInvariant())
            .ToArray();
        if (selectedTokens.Length == 0
            || contentTokens.Length < selectedTokens.Length)
        {
            return false;
        }

        for (var start = 0; start < contentTokens.Length; start++)
        {
            var selectedIndex = 0;
            var contentIndex = start;
            var skipped = 0;
            while (selectedIndex < selectedTokens.Length
                   && contentIndex < contentTokens.Length)
            {
                if (string.Equals(
                        selectedTokens[selectedIndex],
                        contentTokens[contentIndex],
                        StringComparison.Ordinal))
                {
                    selectedIndex++;
                    contentIndex++;
                    continue;
                }
                if (skipped < 2
                    && IsIgnorableEvidenceInterruption(
                        contentTokens[contentIndex]))
                {
                    skipped++;
                    contentIndex++;
                    continue;
                }
                break;
            }
            if (selectedIndex == selectedTokens.Length)
                return true;
        }
        return false;
    }

    private static bool IsIgnorableEvidenceInterruption(string token)
        => Regex.IsMatch(
            token,
            @"^(?:\d+|pers|personnes?|portions?|min|minutes?|h|heures?|pages?|p)$",
            RegexOptions.CultureInvariant);

    private sealed record SelectedItemEvidenceMatch(
        string EvidenceId,
        string CanonicalItem,
        bool TargetColumnMatch,
        int MatchRank,
        double Similarity);

    private readonly record struct ContentPhraseMatch(
        string Text,
        bool IsExact,
        double Similarity);

    private static string NormalizeDisplayWhitespace(string? value)
        => Regex.Replace(
                value ?? string.Empty,
                @"\s+",
                " ",
                RegexOptions.CultureInvariant)
            .Trim();

    private static int CountOrdinalIgnoreCase(string source, string value)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value))
            return 0;
        var count = 0;
        var start = 0;
        while ((start = source.IndexOf(
                   value,
                   start,
                   StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    private static string ReplaceOnceOrdinalIgnoreCase(
        string source,
        string value,
        string replacement)
    {
        var index = source.IndexOf(value, StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? source
            : string.Concat(
                source.AsSpan(0, index),
                replacement,
                source.AsSpan(index + value.Length));
    }

    private static double ComputeNormalizedEditSimilarity(
        string left,
        string right)
    {
        if (left.Length == 0 || right.Length == 0)
            return 0;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = left[leftIndex - 1] == right[rightIndex - 1]
                    ? 0
                    : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(
                        current[rightIndex - 1] + 1,
                        previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitution);
            }
            (previous, current) = (current, previous);
        }
        return 1d - (double)previous[right.Length]
            / Math.Max(left.Length, right.Length);
    }

    private static DistinctSelectionValidation AnalyzeDistinctSelectedItems(
        AdvancedAnalysisLoadDescriptor load,
        AdvancedAnalysisProviderResult result,
        IReadOnlyList<PromptEvidenceItem> evidence)
    {
        if (result.Claims.Count == 0)
            return new DistinctSelectionValidation(false, 1, 0, 0, []);
        var selectedItems = result.Claims
            .Select(static claim => NormalizeClaimText(
                claim.SelectedItem ?? string.Empty))
            .ToArray();
        var missingIdentityCount = selectedItems.Count(
            static item => item.Length == 0);
        var duplicateIdentityCount = selectedItems
            .Where(static item => item.Length > 0)
            .GroupBy(static item => item, StringComparer.Ordinal)
            .Sum(static group => Math.Max(0, group.Count() - 1));
        var unsupportedIdentities = result.Claims
            .Select((claim, index) => new
                   {
                       claim,
                       selectedItem = selectedItems[index],
                       displayItem = (claim.SelectedItem ?? string.Empty).Trim()
                   })
            .Where(item => item.selectedItem.Length > 0
                && !item.claim.EvidenceIds.Any(evidenceId =>
                    evidence.Any(evidenceItem =>
                        string.Equals(
                            evidenceItem.EvidenceId,
                            evidenceId,
                            StringComparison.Ordinal)
                        && EvidenceSupportsSelectedIdentity(
                            item.displayItem,
                            evidenceItem))))
            .Select(static item => item.displayItem)
            .ToArray();
        return new DistinctSelectionValidation(
            missingIdentityCount == 0
            && duplicateIdentityCount == 0
            && unsupportedIdentities.Length == 0,
            missingIdentityCount,
            duplicateIdentityCount,
            unsupportedIdentities.Length,
            unsupportedIdentities);
    }

    private static bool EvidenceSupportsSelectedIdentity(
        string selectedItem,
        PromptEvidenceItem evidence)
    {
        var match = BuildSelectedItemEvidenceMatch(
            selectedItem,
            evidence,
            targetColumn: null);
        return match is not null
               && string.Equals(
                   NormalizeClaimText(match.CanonicalItem),
                   NormalizeClaimText(selectedItem),
                   StringComparison.Ordinal);
    }

    private static string BuildDistinctSelectionFailureDetail(
        string language,
        DistinctSelectionValidation validation,
        string? synthesisRecoveryErrorCode)
    {
        var normalizedLanguage = language.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(synthesisRecoveryErrorCode))
        {
            return normalizedLanguage switch
            {
                "en" => "The bounded correction pass returned an invalid structured result.",
                "de" => "Der begrenzte Korrekturdurchlauf lieferte ein ungültiges strukturiertes Ergebnis.",
                _ => "La passe de correction bornée a renvoyé un résultat structuré invalide."
            };
        }
        if (validation.MissingIdentityCount > 0)
        {
            return normalizedLanguage switch
            {
                "en" => $"{validation.MissingIdentityCount} selection(s) had no verifiable identity.",
                "de" => $"{validation.MissingIdentityCount} Auswahl(en) hatten keine prüfbare Identität.",
                _ => $"{validation.MissingIdentityCount} sélection(s) n'avaient pas d'identité vérifiable."
            };
        }
        if (validation.DuplicateIdentityCount > 0)
        {
            return normalizedLanguage switch
            {
                "en" => $"{validation.DuplicateIdentityCount} duplicate selection(s) remained.",
                "de" => $"{validation.DuplicateIdentityCount} doppelte Auswahl(en) blieben übrig.",
                _ => $"{validation.DuplicateIdentityCount} sélection(s) dupliquée(s) subsistaient."
            };
        }
        var examples = string.Join(
            ", ",
            validation.UnsupportedIdentities
                .Where(static item => item.Length > 0)
                .Take(3)
                .Select(static item => item.Length <= 120
                    ? item
                    : item[..120]));
        return normalizedLanguage switch
        {
            "en" => $"{validation.UnsupportedIdentityCount} selection(s) did not exactly match their cited evidence: {examples}.",
            "de" => $"{validation.UnsupportedIdentityCount} Auswahl(en) entsprachen nicht genau den zitierten Belegen: {examples}.",
            _ => $"{validation.UnsupportedIdentityCount} sélection(s) ne correspondaient pas exactement aux preuves citées : {examples}."
        };
    }
}
