using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static class SourceBackedAgentObservationCompactor
{
    public static string Build(
        string externalToolName,
        IReadOnlyList<ToolResults.Item> results,
        EvidenceBundle bundle,
        int firstToolSequence,
        SourceBackedAgentV2Options options,
        JsonElement? requestArguments = null,
        IReadOnlyList<string>? visibleCategoryPaths = null)
    {
        var maximumItems = ResolveMaximumItems(externalToolName, options);
        var maximumExcerptCharacters = ResolveMaximumExcerptCharacters(externalToolName, options);
        var candidates = SelectEvidenceItems(
            bundle,
            firstToolSequence,
            results.Count,
            maximumItems);
        var evidence = candidates
            .Select(item => new Dictionary<string, object?>
            {
                ["evidenceId"] = item.EvidenceId,
                ["orientationOnly"] = item.RiskFlags.Contains("orientation_only", StringComparer.OrdinalIgnoreCase),
                ["mechanicallyCitable"] =
                    !item.RiskFlags.Contains("orientation_only", StringComparer.OrdinalIgnoreCase)
                    && !SourceBackedContentCardEvidenceContract
                        .IsProoflessCanonicalContentCard(item),
                ["navigationOnly"] =
                    item.RiskFlags.Contains("orientation_only", StringComparer.OrdinalIgnoreCase)
                    || SourceBackedContentCardEvidenceContract
                        .IsProoflessCanonicalContentCard(item),
                ["requestedDocumentMismatch"] = item.RiskFlags.Contains(
                    SourceBackedNamedDocumentIdentity.MismatchRiskFlag,
                    StringComparer.OrdinalIgnoreCase),
                ["sourceKind"] = item.SourceKind,
                ["docId"] = item.DocId,
                ["docName"] = item.DocName,
                ["docPath"] = item.DocPath,
                ["revisionId"] = item.RevisionId,
                ["pageStart"] = item.PageStart,
                ["pageEnd"] = item.PageEnd,
                ["chunkId"] = item.ChunkId,
                ["anchorId"] = item.AnchorId,
                ["targetAnchorId"] = item.AnchorId
                                     ?? ReadSelectionHint(item, "targetAnchorId"),
                ["resolutionMethod"] = ReadSelectionHint(item, "resolutionMethod"),
                ["contentCardKind"] = ReadSelectionHint(item, "kind"),
                ["headingPath"] = ReadSelectionHint(item, "headingPath"),
                ["sectionLevel"] = ReadSelectionHint(item, "sectionLevel"),
                ["hasGroundedEvidence"] = ReadSelectionHint(item, "hasGroundedEvidence"),
                ["contentCardId"] = item.ContentCardId
                                    ?? ReadSelectionHint(item, "contentCardId"),
                ["excerpt"] = Truncate(item.Excerpt, maximumExcerptCharacters),
                ["score"] = item.Score
            })
            .ToArray();
        var errors = results
            .Where(static item => !string.IsNullOrWhiteSpace(item.Error))
            .Select(static item => item.Error)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["ok"] = errors.Length == 0,
            ["tool"] = externalToolName,
            ["evidence"] = evidence,
            ["availableEvidenceCount"] = CountAvailableEvidenceItems(
                bundle,
                firstToolSequence,
                results.Count),
            ["shownEvidenceCount"] = evidence.Length,
            ["truncated"] = CountAvailableEvidenceItems(
                bundle,
                firstToolSequence,
                results.Count) > evidence.Length,
            ["errors"] = errors,
            ["pagination"] = BuildPaginationSummary(results)
        };
        if (candidates.Count == 0)
            payload["resultSummary"] = BuildScalarResultSummary(results);
        var scopeYield = BuildScopeYield(
            externalToolName,
            results,
            evidence.Length,
            requestArguments,
            visibleCategoryPaths);
        if (scopeYield is not null)
            payload["scopeYield"] = scopeYield;

        return JsonSerializer.Serialize(payload, ClientJson.CamelCase);
    }

    private static IReadOnlyDictionary<string, object?>? BuildScopeYield(
        string externalToolName,
        IReadOnlyList<ToolResults.Item> results,
        int shownEvidenceCount,
        JsonElement? requestArguments,
        IReadOnlyList<string>? visibleCategoryPaths)
    {
        if (!IsRagSearch(externalToolName)
            || shownEvidenceCount > 0
            || results.Count == 0
            || results.Any(HasFailedOrBusyResult)
            || requestArguments is not { ValueKind: JsonValueKind.Object } arguments)
        {
            return null;
        }

        var requestedCategoryPath = ReadStringArgument(
            arguments,
            "categoryPath",
            "category");
        if (string.IsNullOrWhiteSpace(requestedCategoryPath))
            return null;

        var paths = (visibleCategoryPaths ?? Array.Empty<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToArray();
        var requestedScope = requestedCategoryPath.Trim();
        return new Dictionary<string, object?>
        {
            ["requestedCategoryPath"] = requestedScope,
            ["yieldStatus"] = "zero_evidence_in_requested_scope",
            ["requestedScopeListed"] = paths.Contains(
                requestedScope,
                StringComparer.OrdinalIgnoreCase),
            ["visibleCategoryPaths"] = paths,
            ["recoveryOptions"] = new[]
            {
                "retry_without_category_scope",
                "retry_with_another_exact_catalog_scope",
                "change_query_or_tool"
            },
            ["semanticDecisionOwner"] = "llm"
        };
    }

    private static bool IsRagSearch(string externalToolName)
        => externalToolName.Trim().ToLowerInvariant() is
            "rag_search" or "rag.search" or "rag_multi_search" or "rag.multi_search";

    private static bool HasFailedOrBusyResult(ToolResults.Item item)
    {
        if (!string.IsNullOrWhiteSpace(item.Error))
            return true;
        if (item.Result.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in item.Result.EnumerateObject())
        {
            if (property.Name.Equals("error", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return true;
            }
            if (property.Name.Equals("busy", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.True)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadStringArgument(JsonElement arguments, params string[] names)
    {
        foreach (var property in arguments.EnumerateObject())
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    internal static int ResolveMaximumItems(
        string externalToolName,
        SourceBackedAgentV2Options options)
        => string.Equals(
            externalToolName,
            "documents_content_cards",
            StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               externalToolName,
               "documents_navigation",
               StringComparison.OrdinalIgnoreCase)
           || string.Equals(
               externalToolName,
               "documents_context_batch",
               StringComparison.OrdinalIgnoreCase)
            ? options.MaximumWorkingEvidenceItems
            : options.MaximumObservationItems;

    private static int ResolveMaximumExcerptCharacters(
        string externalToolName,
        SourceBackedAgentV2Options options)
        => string.Equals(
            externalToolName,
            "documents_content_cards",
            StringComparison.OrdinalIgnoreCase)
            ? options.MaximumWorkingExcerptCharacters
            : options.MaximumObservationExcerptCharacters;

    internal static IReadOnlyList<EvidenceItem> SelectEvidenceItems(
        EvidenceBundle bundle,
        int firstToolSequence,
        int resultCount,
        int maximumItems)
    {
        var allCandidates = GetCandidates(bundle, firstToolSequence, resultCount);
        var canonicalCards = allCandidates
            .Where(static item => string.Equals(
                item.SourceKind,
                "canonical_content_card",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var preferredPool = canonicalCards.Length > 0
            ? canonicalCards
            : allCandidates;
        var resolvedNavigationAnchors = preferredPool
            .Where(static item => item.SelectionHints.TryGetValue(
                "sourceAnchorEvidenceId",
                out var sourceAnchorEvidenceId)
                && !string.IsNullOrWhiteSpace(sourceAnchorEvidenceId))
            .ToArray();
        if (resolvedNavigationAnchors.Length > 0)
        {
            var resolvedIds = resolvedNavigationAnchors
                .Select(static item => item.EvidenceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return resolvedNavigationAnchors
                .Concat(preferredPool.Where(item =>
                    !resolvedIds.Contains(item.EvidenceId)))
                .Take(maximumItems)
                .ToArray();
        }

        return preferredPool
            .Take(maximumItems)
            .ToArray();
    }

    private static int CountAvailableEvidenceItems(
        EvidenceBundle bundle,
        int firstToolSequence,
        int resultCount)
    {
        var allCandidates = GetCandidates(bundle, firstToolSequence, resultCount);
        var canonicalCount = allCandidates.Count(static item => string.Equals(
            item.SourceKind,
            "canonical_content_card",
            StringComparison.OrdinalIgnoreCase));
        return canonicalCount > 0 ? canonicalCount : allCandidates.Length;
    }

    private static EvidenceItem[] GetCandidates(
        EvidenceBundle bundle,
        int firstToolSequence,
        int resultCount)
    {
        var sequences = Enumerable
            .Range(firstToolSequence, Math.Max(1, resultCount))
            .Select(static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);
        return bundle.Items
            .Where(item => item.CodeHints.TryGetValue("tool_sequence", out var sequence)
                           && sequences.Contains(sequence))
            .OrderBy(static item => item.Rank)
            .ToArray();
    }

    public static string BuildProtocolError(
        string externalToolName,
        string error,
        string guidance)
        => JsonSerializer.Serialize(
            new
            {
                ok = false,
                tool = externalToolName,
                error,
                guidance,
                evidence = Array.Empty<object>()
            },
            ClientJson.CamelCase);

    private static IReadOnlyDictionary<string, object?> BuildPaginationSummary(
        IReadOnlyList<ToolResults.Item> results)
    {
        var summary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            AddScalarIfPresent(summary, result.Result, "nextOffset", "next_offset");
            AddScalarIfPresent(summary, result.Result, "offset");
            AddScalarIfPresent(summary, result.Result, "limit");
            AddScalarIfPresent(summary, result.Result, "hasMore", "has_more");
            AddScalarIfPresent(summary, result.Result, "total", "totalCount", "total_count");
        }

        return summary;
    }

    internal static int? ReadNextOffset(IReadOnlyList<ToolResults.Item> results)
    {
        foreach (var result in results)
        {
            if (result.Result.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var property in result.Result.EnumerateObject())
            {
                if (property.Name.Equals("nextOffset", StringComparison.OrdinalIgnoreCase)
                    && property.Value.TryGetInt32(out var nextOffset)
                    && nextOffset >= 0)
                {
                    return nextOffset;
                }
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, object?> BuildScalarResultSummary(
        IReadOnlyList<ToolResults.Item> results)
    {
        var summary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            foreach (var name in new[]
                     {
                         "query", "q", "resolvedPath", "resolvedCategoryPath", "docId",
                         "docName", "docPath", "count", "message", "error"
                     })
            {
                AddScalarIfPresent(summary, result.Result, name);
            }
        }

        return summary;
    }

    private static string? ReadSelectionHint(EvidenceItem item, string name)
        => item.SelectionHints.TryGetValue(name, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;

    private static void AddScalarIfPresent(
        IDictionary<string, object?> destination,
        JsonElement root,
        params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in root.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                || property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    or JsonValueKind.Undefined)
            {
                continue;
            }

            destination[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number when property.Value.TryGetDouble(out var number) => number,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
            return;
        }
    }

    private static string? Truncate(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length <= maximumCharacters)
            return normalized;

        var cut = normalized.LastIndexOf(' ', maximumCharacters - 1, maximumCharacters);
        if (cut < maximumCharacters / 2)
            cut = maximumCharacters;
        return normalized[..cut].TrimEnd() + "…";
    }
}
