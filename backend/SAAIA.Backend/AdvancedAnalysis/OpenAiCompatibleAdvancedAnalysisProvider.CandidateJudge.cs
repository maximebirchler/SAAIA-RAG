using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private const int CandidateJudgeMaximumBatchSize = 12;

    private sealed record CandidateJudgeBatch(
        IReadOnlyList<CandidateInventoryItem> Candidates,
        IReadOnlyList<PromptEvidenceItem> Evidence);

    private static CandidateJudgeBatch BuildCandidateJudgeBatch(
        IReadOnlyList<CandidateInventoryItem> inventory,
        IReadOnlyList<PromptEvidenceItem> observations)
    {
        var visible = observations.Where(item => !string.IsNullOrWhiteSpace(item.EvidenceId))
            .ToDictionary(item => item.EvidenceId!, StringComparer.Ordinal);
        var visibleEvidenceIds = visible.Keys.ToHashSet(StringComparer.Ordinal);
        var candidates = inventory.Where(item =>
                item.Status == "awaiting_review"
                && item.BodyEvidenceIds.Any(visible.ContainsKey))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Take(CandidateJudgeMaximumBatchSize)
            .Select(item => item with
            {
                LocatorEvidenceIds = [],
                BodyEvidenceIds = ProjectCandidateJudgeEvidenceIds(
                    item.BodyEvidenceIds,
                    visibleEvidenceIds)
            })
            .ToArray();
        var evidenceIds = candidates.SelectMany(item => item.BodyEvidenceIds)
            .Where(visible.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        return new CandidateJudgeBatch(
            candidates,
            observations.Where(item => item.EvidenceId is not null
                                       && evidenceIds.Contains(item.EvidenceId))
                .ToArray());
    }

    internal static IReadOnlyList<string> ProjectCandidateJudgeEvidenceIds(
        IReadOnlyList<string> candidateEvidenceIds,
        IReadOnlySet<string> visibleEvidenceIds)
        => candidateEvidenceIds
            .Where(visibleEvidenceIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();

    private string BuildCandidateJudgeUserPrompt(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<CandidateInventoryItem> inventory,
        CandidateExplorerCoverage coverage,
        CandidateJudgeBatch batch)
        => JsonSerializer.Serialize(new
        {
            request = request.Handoff.RequestText,
            language = request.Handoff.Language,
            load = BuildPromptLoad(request.Handoff.Load),
            claimCoordinates = BuildStructuredClaimCoordinates(request.Handoff.Load),
            assignmentGap = new
            {
                coverage.RequiredDistinctCount,
                coverage.MaximumAssignableCount,
                coverage.MissingByRole
            },
            candidateInventory = new
            {
                enabled = true,
                items = batch.Candidates,
                instruction = "Return one updated item for every supplied key, preserving its key and sourceKey. An observed heading or provisional text prefix is a locator, not an approved identity. Inspect its body and replace the prefix with the complete exact documented identity when present. If its body contains multiple distinct named items, additionally return one item per extra documentary identity using a new unique key. At most 32 items total. Every item must cite supplied same-source body evidence."
            },
            evidence = batch.Evidence,
            allowedTargetRoles = request.Handoff.Load.Columns,
            outputContract = new
            {
                shape = new { items = "array" },
                requiredItemFields = new[]
                {
                    "key", "exactTitle", "sourceKey", "targetRoles",
                    "selectedRoles", "status", "note", "locatorEvidenceIds",
                    "bodyEvidenceIds"
                },
                allowedStatus = new[] { "body_verified", "needs_body", "rejected" },
                instruction = "For a structured request, body_verified needs at least one semantically justified targetRole. For a flat request with no allowedTargetRoles, body_verified has empty targetRoles and means suitable for the request as a whole. needs_body retains a potentially suitable identifiable item whose evidence is incomplete; explain the missing evidence or physical continuation in note. rejected is for substantively unsuitable items or non-entity headings. needs_body and rejected use empty targetRoles. Always include note as a JSON string; use an empty string when no note is needed."
            },
            currentEligibleCandidateKeys = inventory.Where(item =>
                    item.Status is "body_verified" or "selected"
                    && (request.Handoff.Load.Columns.Count == 0
                        || item.TargetRoles.Count > 0))
                .Select(item => item.Key)
                .ToArray()
        }, JsonOptions);

    private static string BuildCandidateJudgeSystemPrompt()
        => """
           You are the SAAIA Candidate Judge. Assess only the small batch of
           substantive canonical bodies in the user JSON. For every supplied
           candidate key, decide whether the evidence identifies one named item
           and supports its suitability for the user's actual requested use.
           Preserve the opaque key and sourceKey. A seed's exactTitle may be an
           observed heading or provisional text prefix; neither proves that the
           text names a complete item. Keep a valid source-exact identity, or
           replace a provisional prefix or subordinate title with the complete identity displayed as
           an exact line in that same body. Repeated headings on separate pages
           are provisional observations and must not be treated as one entity
           merely because their strings match. Reject non-entity headings and
           items demonstrably unsuitable for the request. Components or templates
           are unsuitable when the user needs complete named items, but judge this
           from the actual request rather than a domain-specific blanket rule.
           A provisional body may contain several distinct named items. Update
           every supplied key once and, when additional complete exact identities
           are visibly documented in that same body, add candidates with new unique
           opaque keys. A new item must have a source-exact identity supported by
           supplied same-source body evidence. Do not invent keys already present
           in the inventory. Return at most 32 items, including seed updates.
           When a body contains more additional identities than fit, prioritize
           those relevant to assignmentGap missing roles. The gap is an operational
           priority, never evidence that an item is suitable for a role.

           Require only the evidence needed for the requested answer: a documented
           named proposal does not require the full procedure unless the user asks
           for that procedure. Do not infer unseen details. When the identity is
           meaningful and potentially suitable but the supplied fragment does not
           establish suitability, use needs_body and describe exactly what should
           be retrieved or read next in note; do not mark missing evidence as a
           permanent rejection. Rejected means substantive unsuitability or a
           non-entity observation, not merely a clipped beginning or continuation.

           Assign targetRoles by semantic judgment from the body and request. The
           application performs only identity validation and assignment counting;
           it does not decide suitability. For structured roles use body_verified
           with one or more roles. For a flat collection with no allowed roles,
           body_verified has empty targetRoles and means suitable for the request
           as a whole. needs_body and rejected have no roles. selectedRoles is
           always empty. Cite
           only supplied evidence IDs in bodyEvidenceIds; locatorEvidenceIds is
           empty for this body batch. Every returned item must contain key,
           exactTitle, sourceKey, targetRoles, selectedRoles, status, note,
           locatorEvidenceIds and bodyEvidenceIds. note is always a JSON string;
           use "" when no note is needed. Return one item per supplied key, plus
           any additional distinct documented identities discovered in that batch,
           and only this JSON object: {"items":[...]}. Do not research, draft the
           user answer, count the whole dossier or invent missing evidence.
           """ + "\n" + DocumentaryDataBoundary;

    private static JsonDocument NormalizeCandidateJudgeOptionalNotes(
        JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return JsonDocument.Parse(root.GetRawText());

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.Name != "items"
                    || property.Value.ValueKind != JsonValueKind.Array)
                {
                    property.Value.WriteTo(writer);
                    continue;
                }

                writer.WriteStartArray();
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        item.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartObject();
                    var hasNote = false;
                    foreach (var itemProperty in item.EnumerateObject())
                    {
                        hasNote |= itemProperty.NameEquals("note");
                        itemProperty.WriteTo(writer);
                    }
                    if (!hasNote)
                        writer.WriteString("note", "");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private IReadOnlyList<CandidateInventoryItem> ParseCandidateJudgeUpdates(
        string raw,
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<CandidateInventoryItem> inventory,
        CandidateExplorerCoverage coverage,
        CandidateJudgeBatch batch)
    {
        var prompt = BuildCandidateJudgeUserPrompt(
            request,
            inventory,
            coverage,
            batch);
        try
        {
            using var output = JsonDocument.Parse(UnwrapJson(raw));
            using var normalizedOutput = NormalizeCandidateJudgeOptionalNotes(
                output.RootElement);
            using var promptDocument = JsonDocument.Parse(prompt);
            var updates = ParseCandidateInventoryUpdates(
                normalizedOutput.RootElement,
                promptDocument.RootElement,
                allowConvergedIdentities: true);
            var expectedKeys = batch.Candidates.Select(item => item.Key)
                .ToHashSet(StringComparer.Ordinal);
            var actualKeys = updates.Select(item => item.Key)
                .ToHashSet(StringComparer.Ordinal);
            var expectedSources = batch.Candidates.ToDictionary(
                item => item.Key, item => item.SourceKey, StringComparer.Ordinal);
            var existingKeys = inventory.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            if (!expectedKeys.IsSubsetOf(actualKeys)
                || updates.Any(item => expectedSources.TryGetValue(item.Key, out var sourceKey)
                    ? !string.Equals(item.SourceKey, sourceKey, StringComparison.Ordinal)
                    : existingKeys.Contains(item.Key))
                || updates.Any(item => item.Status is not ("body_verified" or "needs_body" or "rejected")
                                       || item.SelectedRoles.Count > 0
                                       || item.Status is "needs_body" or "rejected"
                                       && item.TargetRoles.Count > 0))
            {
                throw new JsonException();
            }
            return updates;
        }
        catch (Exception error) when (error is JsonException or AdvancedAnalysisProviderException)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_candidate_judge_protocol_invalid");
        }
    }
}
