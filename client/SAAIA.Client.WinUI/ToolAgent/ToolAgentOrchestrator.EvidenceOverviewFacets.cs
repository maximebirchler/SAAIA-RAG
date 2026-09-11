using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record EvidenceOverviewAnchorCandidate(
        string EvidenceId,
        string ChunkId,
        string AnchorId,
        string Label,
        string NavigationKind,
        string SourceKind,
        int PageStart,
        int PageEnd);

    private sealed record EvidenceOverviewFacetAcquisition(
        EvidenceOverviewContextSelection[] Selections,
        string[][] SelectedChunkIdsByFacet,
        int NavigationCandidateCount,
        int EligibleCandidateCount,
        long NavigationMs,
        long SelectorMs,
        long MaterializationMs);

    private async Task<EvidenceOverviewFacetAcquisition?>
        TryAcquireEvidenceOverviewFacetsAsync(
            ResolvedDocRef resolved,
            string userRequest,
            IReadOnlyList<string> facets,
            CancellationToken ct)
    {
        if (facets.Count is < 2 or > 5)
            return null;

        try
        {
            const int pageSize = 80;
            const int maximumNavigationItems = 320;
            var navigationWatch = Stopwatch.StartNew();
            var firstPage = await _api.DocumentsNavigationAsync(
                    path: null,
                    categoryRef: null,
                    resolved.DocId,
                    resolved.DocPath,
                    q: null,
                    kind: null,
                    limit: pageSize,
                    offset: 0,
                    ct)
                .ConfigureAwait(false);
            var total = Math.Min(
                EvidenceOverviewReadInt(firstPage, "total"),
                maximumNavigationItems);
            var offsets = Enumerable.Range(
                    1,
                    Math.Max(0, (total - 1) / pageSize))
                .Select(static page => page * pageSize)
                .Where(offset => offset < total)
                .ToArray();
            var remainingPages = await Task.WhenAll(offsets.Select(offset =>
                _api.DocumentsNavigationAsync(
                    path: null,
                    categoryRef: null,
                    resolved.DocId,
                    resolved.DocPath,
                    q: null,
                    kind: null,
                    limit: pageSize,
                    offset,
                    ct)));
            navigationWatch.Stop();

            var allAnchors = new[] { firstPage }
                .Concat(remainingPages)
                .SelectMany(ReadEvidenceOverviewNavigationAnchors)
                .GroupBy(
                    static anchor => anchor.ChunkId,
                    StringComparer.Ordinal)
                .Select(static group => group.First())
                .OrderBy(static anchor => anchor.PageStart)
                .ThenBy(static anchor => anchor.ChunkId, StringComparer.Ordinal)
                .ToArray();
            var eligibleAnchors = allAnchors
                .Where(IsMechanicallyEligibleEvidenceOverviewAnchor)
                .ToArray();
            if (eligibleAnchors.Length < facets.Count)
            {
                EmitRagTrace(
                    "document_overview.facet_selector.insufficient_candidates",
                    ("facet_count", facets.Count),
                    ("navigation_candidate_count", allAnchors.Length),
                    ("eligible_candidate_count", eligibleAnchors.Length),
                    ("navigation_ms", navigationWatch.ElapsedMilliseconds));
                return null;
            }

            var boundedAnchors = SelectStratifiedEvidenceOverviewAnchors(
                    eligibleAnchors,
                    maximumCount: 80)
                .Select((anchor, index) => anchor with
                {
                    EvidenceId = "A" + (index + 1)
                })
                .ToArray();
            var selectorPrompt = BuildEvidenceOverviewFacetSelectionPrompt(
                userRequest,
                facets,
                boundedAnchors);
            var selectorWatch = Stopwatch.StartNew();
            var rawSelection = await _llm.CompleteAsync(
                    new[]
                    {
                        (
                            "system",
                            "You are the semantic evidence selector. Map every explicit user facet to canonical anchor IDs. Return only the required F-number lines, without claims or commentary."),
                        ("user", selectorPrompt)
                    },
                    forceJson: false,
                    ct)
                .ConfigureAwait(false);
            selectorWatch.Stop();
            var selectedIdsByFacet = TryParseEvidenceOverviewFacetSelection(
                rawSelection,
                facets,
                boundedAnchors.Select(static anchor => anchor.EvidenceId)
                    .ToArray());
            if (selectedIdsByFacet is null)
            {
                EmitRagTrace(
                    "document_overview.facet_selector.rejected",
                    ("facet_count", facets.Count),
                    ("candidate_count", boundedAnchors.Length),
                    ("selector_ms", selectorWatch.ElapsedMilliseconds));
                return null;
            }

            var byId = boundedAnchors.ToDictionary(
                static anchor => anchor.EvidenceId,
                StringComparer.OrdinalIgnoreCase);
            var selectedAnchors = selectedIdsByFacet
                .SelectMany(static ids => ids)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => byId[id])
                .ToArray();
            var materializationWatch = Stopwatch.StartNew();
            var materialized = await Task.WhenAll(selectedAnchors.Select(
                async anchor =>
                {
                    var watch = Stopwatch.StartNew();
                    var context = await _api.DocumentsContextAsync(
                            resolved.DocId,
                            resolved.DocPath,
                            anchor.ChunkId,
                            pageStart: null,
                            pageEnd: null,
                            before: 0,
                            after: 0,
                            limit: 3,
                            offset: 0,
                            ct)
                        .ConfigureAwait(false);
                    watch.Stop();
                    return BuildEvidenceOverviewAnchorContextSelection(
                        anchor,
                        context,
                        watch.ElapsedMilliseconds);
                }));
            materializationWatch.Stop();
            if (materialized.Any(static selection => selection is null))
            {
                EmitRagTrace(
                    "document_overview.facet_selector.materialization_rejected",
                    ("facet_count", facets.Count),
                    ("selected_anchor_count", selectedAnchors.Length),
                    ("materialized_count", materialized.Count(
                        static selection => selection is not null)),
                    ("materialization_ms", materializationWatch.ElapsedMilliseconds));
                return null;
            }

            var selectedChunkIdsByFacet = selectedIdsByFacet
                .Select(ids => ids.Select(id => byId[id].ChunkId).ToArray())
                .ToArray();
            var selections = materialized
                .Cast<EvidenceOverviewContextSelection>()
                .ToArray();
            EmitRagTrace(
                "document_overview.facet_selector.accepted",
                ("facet_count", facets.Count),
                ("candidate_count", boundedAnchors.Length),
                ("selected_anchor_count", selectedAnchors.Length),
                ("selector_ms", selectorWatch.ElapsedMilliseconds),
                ("navigation_ms", navigationWatch.ElapsedMilliseconds),
                ("materialization_ms", materializationWatch.ElapsedMilliseconds));
            return new EvidenceOverviewFacetAcquisition(
                selections,
                selectedChunkIdsByFacet,
                allAnchors.Length,
                eligibleAnchors.Length,
                navigationWatch.ElapsedMilliseconds,
                selectorWatch.ElapsedMilliseconds,
                materializationWatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EmitRagTrace(
                "document_overview.facet_selector.unavailable",
                ("facet_count", facets.Count),
                ("error_type", ex.GetType().Name));
            return null;
        }
    }

    private static IReadOnlyList<EvidenceOverviewAnchorCandidate>
        ReadEvidenceOverviewNavigationAnchors(JsonElement navigation)
    {
        if (navigation.ValueKind != JsonValueKind.Object
            || !navigation.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray()
            .Select(static item => new EvidenceOverviewAnchorCandidate(
                string.Empty,
                EvidenceOverviewReadString(item, "targetChunkId"),
                EvidenceOverviewReadString(item, "targetAnchorId"),
                EvidenceOverviewReadString(item, "label"),
                EvidenceOverviewReadString(item, "kind"),
                EvidenceOverviewReadString(item, "sourceKind"),
                EvidenceOverviewReadInt(item, "targetPageStart"),
                EvidenceOverviewReadInt(item, "targetPageEnd")))
            .Where(static anchor =>
                anchor.ChunkId.Length > 0
                && anchor.Label.Length > 0
                && anchor.PageStart > 0)
            .ToArray();
    }

    private static bool IsMechanicallyEligibleEvidenceOverviewAnchor(
        EvidenceOverviewAnchorCandidate anchor)
    {
        var compact = Regex.Replace(anchor.Label, @"\s+", " ").Trim();
        if (compact.Length == 0)
            return false;
        var wordCount = Regex.Matches(
            compact,
            @"[\p{L}\p{N}]+",
            RegexOptions.CultureInvariant).Count;
        return wordCount >= 4
               || Regex.IsMatch(
                   compact,
                   @"[.!?:;]",
                   RegexOptions.CultureInvariant);
    }

    private static EvidenceOverviewAnchorCandidate[]
        SelectStratifiedEvidenceOverviewAnchors(
            IReadOnlyList<EvidenceOverviewAnchorCandidate> anchors,
            int maximumCount)
    {
        if (anchors.Count <= maximumCount)
            return anchors.ToArray();
        return Enumerable.Range(0, maximumCount)
            .Select(index => anchors[Math.Min(
                anchors.Count - 1,
                (int)Math.Floor(
                    (index + 0.5d) * anchors.Count / maximumCount))])
            .DistinctBy(static anchor => anchor.ChunkId, StringComparer.Ordinal)
            .ToArray();
    }

    private static EvidenceOverviewContextSelection?
        BuildEvidenceOverviewAnchorContextSelection(
            EvidenceOverviewAnchorCandidate anchor,
            JsonElement context,
            long elapsedMs)
    {
        if (context.ValueKind != JsonValueKind.Object
            || !context.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || !context.TryGetProperty("document", out var document)
            || document.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var item = items.EnumerateArray().FirstOrDefault(candidate =>
            string.Equals(
                EvidenceOverviewReadString(candidate, "chunkId"),
                anchor.ChunkId,
                StringComparison.Ordinal));
        return item.ValueKind == JsonValueKind.Object
            ? new EvidenceOverviewContextSelection(
                anchor.PageStart,
                document.Clone(),
                item.Clone(),
                elapsedMs)
            : null;
    }

    private static string BuildEvidenceOverviewFacetSelectionPrompt(
        string userRequest,
        IReadOnlyList<string> facets,
        IReadOnlyList<EvidenceOverviewAnchorCandidate> anchors)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("SAAIA_DOCUMENT_OVERVIEW_SELECTOR FACET_MODE");
        prompt.AppendLine("USER_REQUEST:");
        prompt.AppendLine(userRequest);
        prompt.AppendLine("EXPLICIT_FACETS:");
        for (var index = 0; index < facets.Count; index++)
            prompt.Append('F').Append(index + 1).Append('=').AppendLine(facets[index]);
        prompt.AppendLine("For each facet, select one or two anchor IDs whose underlying canonical text best supports a faithful answer to that facet.");
        prompt.AppendLine("Use semantic judgment. Prefer direct, self-contained, answer-bearing labels; exact facet coverage matters more than document-wide variety.");
        prompt.AppendLine("For a practical-use facet, choose passages that justify the use without pretending the source states the recommendation verbatim.");
        prompt.AppendLine("Use each anchor ID for at most one facet so every selected excerpt has one unambiguous local role.");
        prompt.AppendLine("Do not invent missing evidence and do not write any answer claim.");
        prompt.AppendLine("Return exactly one line per facet in order, for example F1=A2,A5. Use one or two distinct IDs per line. No other text, JSON or brackets.");
        prompt.AppendLine("MECHANICALLY_ELIGIBLE_CANONICAL_ANCHORS:");
        foreach (var anchor in anchors)
        {
            prompt.Append(anchor.EvidenceId)
                .Append(" page=").Append(anchor.PageStart)
                .Append(" kind=").Append(anchor.NavigationKind)
                .Append(" label=").AppendLine(CompactEvidenceOverviewText(
                    anchor.Label,
                    240));
        }

        return prompt.ToString();
    }

    private static string[][]? TryParseEvidenceOverviewFacetSelection(
        string? rawSelection,
        IReadOnlyList<string> facets,
        IReadOnlyCollection<string> allowedAnchorIds)
    {
        if (facets.Count is < 2 or > MaximumEvidenceOverviewPointCount)
            return null;
        var lines = (rawSelection ?? string.Empty).Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
        if (lines.Length != facets.Count)
            return null;

        var allowed = allowedAnchorIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var selections = new string[facets.Count][];
        for (var index = 0; index < facets.Count; index++)
        {
            var match = Regex.Match(
                lines[index],
                "^F(?<index>\\d{1,2})\\s*=\\s*(?<ids>A\\d{1,3}(?:\\s*,\\s*A\\d{1,3})?)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success
                || !int.TryParse(match.Groups["index"].Value, out var parsedIndex)
                || parsedIndex != index + 1)
            {
                return null;
            }

            var ids = match.Groups["ids"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries)
                .Select(static id => id.ToUpperInvariant())
                .ToArray();
            if (ids.Length is < 1 or > 2
                || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != ids.Length
                || ids.Any(id => !allowed.Contains(id)))
            {
                return null;
            }

            selections[index] = ids;
        }

        var flattened = selections.SelectMany(static ids => ids).ToArray();
        if (flattened.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != flattened.Length)
        {
            return null;
        }

        return selections;
    }

    private static EvidenceItem[][]?
        TryMapEvidenceOverviewFacetsToEvidence(
            IReadOnlyList<string[]> selectedChunkIdsByFacet,
            IReadOnlyList<EvidenceItem> evidence)
    {
        if (selectedChunkIdsByFacet.Count is < 2 or > 5)
            return null;
        var byChunkId = evidence
            .Where(HasCompleteEvidenceOverviewIdentity)
            .Where(static item => !string.IsNullOrWhiteSpace(item.ChunkId))
            .GroupBy(
                static item => item.ChunkId!,
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        var mapped = new EvidenceItem[selectedChunkIdsByFacet.Count][];
        for (var facetIndex = 0;
             facetIndex < selectedChunkIdsByFacet.Count;
             facetIndex++)
        {
            var chunkIds = selectedChunkIdsByFacet[facetIndex];
            if (chunkIds.Length is < 1 or > 2
                || chunkIds.Distinct(StringComparer.Ordinal).Count()
                != chunkIds.Length)
            {
                return null;
            }

            var facetEvidence = new List<EvidenceItem>(chunkIds.Length);
            foreach (var chunkId in chunkIds)
            {
                if (!byChunkId.TryGetValue(chunkId, out var item))
                    return null;
                facetEvidence.Add(item);
            }

            mapped[facetIndex] = facetEvidence.ToArray();
        }

        var flattened = mapped.SelectMany(static items => items).ToArray();
        return flattened
                   .Select(static item => item.EvidenceId)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .Count()
               == flattened.Length
            ? mapped
            : null;
    }

    private static string BuildEvidenceOverviewExtractiveFallbackText(
        string responseLanguage,
        IReadOnlyList<string> facets,
        IReadOnlyList<IReadOnlyList<EvidenceItem>> evidenceByFacet)
    {
        if (facets.Count != evidenceByFacet.Count)
            return string.Empty;
        var language = NormalizeLanguageCode(responseLanguage);
        var (warning, facetPrefix) = language switch
        {
            "en" => (
                "I could not verify a sufficiently reliable synthesis. The following are only the selected canonical excerpts grouped by facet; they are neither a synthesis nor conclusions.",
                "Facet — "),
            "es" => (
                "No pude verificar una síntesis suficientemente fiable. A continuación se muestran únicamente los extractos canónicos seleccionados, agrupados por faceta; no son una síntesis ni conclusiones.",
                "Faceta — "),
            "pt" => (
                "Não foi possível verificar uma síntese suficientemente confiável. Abaixo estão apenas os excertos canônicos selecionados, agrupados por faceta; não são uma síntese nem conclusões.",
                "Faceta — "),
            "de" => (
                "Ich konnte keine ausreichend verlässliche Zusammenfassung verifizieren. Es folgen nur die ausgewählten kanonischen Auszüge nach Aspekt; sie sind weder eine Zusammenfassung noch Schlussfolgerungen.",
                "Aspekt — "),
            "it" => (
                "Non è stato possibile verificare una sintesi sufficientemente affidabile. Seguono soltanto gli estratti canonici selezionati, raggruppati per aspetto; non sono una sintesi né conclusioni.",
                "Aspetto — "),
            _ => (
                "Je n'ai pas pu vérifier une synthèse suffisamment fiable. Voici uniquement les extraits canoniques sélectionnés, regroupés par facette : ce ne sont ni une synthèse ni des conclusions.",
                "Facette — ")
        };

        var text = new StringBuilder();
        text.AppendLine(warning);
        for (var index = 0; index < facets.Count; index++)
        {
            text.AppendLine();
            text.Append(facetPrefix).AppendLine(facets[index]);
            foreach (var evidence in evidenceByFacet[index].Take(2))
            {
                text.Append("- ")
                    .Append(CompactEvidenceOverviewText(
                        evidence.Excerpt,
                        600))
                    .Append(" [")
                    .Append(evidence.EvidenceId)
                    .AppendLine("]");
            }
        }

        return text.ToString().Trim();
    }

    private JsonElement? TryBuildEvidenceOverviewExtractiveFallbackPayload(
        ResolvedDocRef resolved,
        ToolMemory.SourceRef? fallbackSource,
        string responseLanguage,
        string docLanguage,
        IReadOnlyList<string> facets,
        IReadOnlyList<IReadOnlyList<EvidenceItem>>? evidenceByFacet,
        EvidenceBundle bundle,
        int pageCount,
        IReadOnlyList<int> targetPages,
        int materializedChunkCount,
        long materializationMs,
        EvidenceOverviewFacetAcquisition? acquisition,
        string reason,
        long writerMs)
    {
        if (acquisition is null
            || evidenceByFacet is null
            || facets.Count != evidenceByFacet.Count)
        {
            return null;
        }

        var fallbackText = BuildEvidenceOverviewExtractiveFallbackText(
            responseLanguage,
            facets,
            evidenceByFacet);
        var citedEvidence = evidenceByFacet
            .SelectMany(static items => items)
            .ToArray();
        var allowedEvidenceIds = citedEvidence
            .Select(static item => item.EvidenceId)
            .ToArray();
        if (fallbackText.Length == 0
            || citedEvidence.Length == 0
            || allowedEvidenceIds
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .Count()
               != allowedEvidenceIds.Length)
        {
            return null;
        }

        var verification = SourceContractVerifier.Verify(
            new WriterDraft(fallbackText, allowedEvidenceIds),
            bundle,
            intake: null,
            allowedEvidenceIds: allowedEvidenceIds,
            enforceRequestedShape: false,
            requireEveryAllowedEvidenceIdExactlyOnce: true,
            allowMultipleEvidencePerVisibleSource: true,
            requireSeparateAtomicClaims: true);
        if (!verification.IsValid)
        {
            EmitRagTrace(
                "document_overview.extractive_fallback.rejected",
                ("reason", reason),
                ("verification_errors", verification.Errors
                    .Select(static error => error.Code)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));
            return null;
        }

        var sourcePayloads = citedEvidence
            .Select(BuildEvidenceOverviewSourcePayload)
            .ToArray();
        var primarySource = citedEvidence[0];
        EmitRagTrace(
            "document_overview.extractive_fallback.accepted",
            ("reason", reason),
            ("facet_count", facets.Count),
            ("evidence_count", citedEvidence.Length));
        return JsonSerializer.SerializeToElement(new
        {
            docId = resolved.DocId,
            docPath = resolved.DocPath,
            docName = resolved.DocName,
            mode = "live_evidence_overview_extractive_fallback",
            level = "medium",
            strategy = "evidence_overview",
            language = responseLanguage,
            responseLanguage,
            docLanguage,
            profileLanguage = primarySource.ProfileLanguage
                              ?? fallbackSource?.ProfileLanguage,
            sourceHash = primarySource.SourceHash
                         ?? fallbackSource?.SourceHash,
            category = fallbackSource?.Category ?? resolved.Category,
            categoryRef = fallbackSource?.CategoryRef
                          ?? resolved.CategoryRef,
            categoryPath = primarySource.CategoryPath
                           ?? fallbackSource?.CategoryPath
                           ?? resolved.CategoryPath,
            sourceMetadata = sourcePayloads,
            sourceMetadataTotal = sourcePayloads.Length,
            sourceMetadataTruncated = false,
            sourceMetadataSample = sourcePayloads,
            fallback = new
            {
                reason,
                notSynthesis = true,
                synthesisVerified = false,
                canonicalExcerptsOnly = true,
                semanticFacetSelectionPerformed = true
            },
            sampling = new
            {
                method = "navigation_anchor_semantic_facets_exact_context",
                pageCount,
                targetPages,
                materializedChunkCount,
                evidenceCount = citedEvidence.Length,
                facetCount = facets.Count,
                navigationCandidateCount = acquisition.NavigationCandidateCount,
                eligibleCandidateCount = acquisition.EligibleCandidateCount,
                navigationMs = acquisition.NavigationMs,
                facetSelectorMs = acquisition.SelectorMs,
                materializationMs,
                writerMs
            },
            evidenceBundle = new
            {
                bundle.BundleId,
                itemCount = bundle.Items.Count,
                citedEvidenceIds = allowedEvidenceIds,
                sourceVerified = verification.IsValid,
                synthesisVerified = false,
                traceEvents = bundle.TraceEvents
            },
            summaryText = fallbackText,
            anchors = sourcePayloads
        });
    }

    internal static string BuildEvidenceOverviewFacetSelectionPromptForTests(
        string userRequest,
        IReadOnlyList<string> facets,
        IReadOnlyList<(string EvidenceId, int PageStart, string Label)> anchors)
        => BuildEvidenceOverviewFacetSelectionPrompt(
            userRequest,
            facets,
            anchors.Select(static anchor => new EvidenceOverviewAnchorCandidate(
                anchor.EvidenceId,
                "chunk-" + anchor.EvidenceId,
                "anchor-" + anchor.EvidenceId,
                anchor.Label,
                "title_anchor",
                "chunk_lead",
                anchor.PageStart,
                anchor.PageStart)).ToArray());

    internal static string[][]? TryParseEvidenceOverviewFacetSelectionForTests(
        string? rawSelection,
        IReadOnlyList<string> facets,
        IReadOnlyCollection<string> allowedAnchorIds)
        => TryParseEvidenceOverviewFacetSelection(
            rawSelection,
            facets,
            allowedAnchorIds);

    internal static string BuildEvidenceOverviewExtractiveFallbackTextForTests(
        string responseLanguage,
        IReadOnlyList<string> facets,
        IReadOnlyList<IReadOnlyList<EvidenceItem>> evidenceByFacet)
        => BuildEvidenceOverviewExtractiveFallbackText(
            responseLanguage,
            facets,
            evidenceByFacet);

    internal static EvidenceItem[][]?
        TryMapEvidenceOverviewFacetsToEvidenceForTests(
            IReadOnlyList<string[]> selectedChunkIdsByFacet,
            IReadOnlyList<EvidenceItem> evidence)
        => TryMapEvidenceOverviewFacetsToEvidence(
            selectedChunkIdsByFacet,
            evidence);
}
