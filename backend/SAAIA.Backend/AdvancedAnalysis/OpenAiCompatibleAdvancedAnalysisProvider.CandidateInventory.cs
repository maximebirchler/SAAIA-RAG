using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record CandidateInventoryItem(
        string Key,
        string ExactTitle,
        string SourceKey,
        IReadOnlyList<string> TargetRoles,
        IReadOnlyList<string> SelectedRoles,
        string Status,
        string Note,
        IReadOnlyList<string> LocatorEvidenceIds,
        IReadOnlyList<string> BodyEvidenceIds);

    private static readonly string[] CandidateInventoryStates =
        ["navigation_only", "body_requested", "body_verified", "selected", "rejected"];

    private static string BuildCandidateInventoryKey(string sourceKey, string title)
    {
        var identity = sourceKey + "\n" + NormalizeClaimText(title);
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16];
        return "candidate-" + hash;
    }

    private static bool CandidateInventoryEnabled(AdvancedAnalysisProviderRequest request)
        => request.Handoff.Load.StructuredLayout
           && request.Handoff.Load.AtomicEvidenceMode.Contains(
               "named_item",
               StringComparison.OrdinalIgnoreCase)
           && request.Handoff.Load.Columns.Count > 0;

    private static object BuildCandidateInventoryFunction(JsonElement prompt)
    {
        var evidence = prompt.GetProperty("evidence").EnumerateArray().ToArray();
        var evidenceIds = evidence
            .Select(item => ReadString(item, "evidenceId"))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceKeys = evidence
            .Select(item => ReadString(item, "sourceKey"))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var targetRoles = prompt.GetProperty("claimCoordinates").EnumerateArray()
            .Select(item => ReadString(item, "columnLabel"))
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        object Text(int maximum) => new { type = "string", maxLength = maximum };
        object EvidenceIds() => new
        {
            type = "array",
            minItems = 0,
            maxItems = 4,
            uniqueItems = true,
            items = new { type = "string", @enum = evidenceIds }
        };
        var itemProperties = new Dictionary<string, object>
        {
            ["key"] = Text(80),
            ["exactTitle"] = Text(240),
            ["sourceKey"] = new { type = "string", @enum = sourceKeys },
            ["targetRoles"] = new
            {
                type = "array",
                minItems = 1,
                maxItems = Math.Min(8, targetRoles.Length),
                uniqueItems = true,
                items = new { type = "string", @enum = targetRoles }
            },
            ["selectedRoles"] = new
            {
                type = "array",
                minItems = 0,
                maxItems = Math.Min(8, targetRoles.Length),
                uniqueItems = true,
                items = new { type = "string", @enum = targetRoles }
            },
            ["status"] = new { type = "string", @enum = CandidateInventoryStates },
            ["note"] = Text(200),
            ["locatorEvidenceIds"] = EvidenceIds(),
            ["bodyEvidenceIds"] = EvidenceIds()
        };
        return new
        {
            type = "function",
            function = new
            {
                name = "save_candidate_inventory",
                strict = true,
                description = "Upsert observed named candidates for this bounded structured job. Keep exact titles and proposed target roles, record final selected roles separately, distinguish navigation locators from substantive body evidence, and update status as research progresses. Existing candidates omitted from this update remain stored. Memory is not documentary proof; only current evidence IDs may be added.",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "items" },
                    properties = new
                    {
                        items = new
                        {
                            type = "array",
                            minItems = 1,
                            maxItems = 32,
                            items = new
                            {
                                type = "object",
                                properties = itemProperties,
                                required = itemProperties.Keys.ToArray(),
                                additionalProperties = false
                            }
                        }
                    }
                }
            }
        };
    }

    private static IReadOnlyList<CandidateInventoryItem> ParseCandidateInventoryUpdates(
        JsonElement value,
        JsonElement prompt)
    {
        void Reject() => throw new AdvancedAnalysisProviderException(
            "advanced_native_candidate_inventory_invalid");
        void Properties(JsonElement item, string[] expected)
        {
            if (item.ValueKind != JsonValueKind.Object) Reject();
            var names = item.EnumerateObject().Select(static property => property.Name).ToArray();
            if (names.Length != expected.Length
                || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
                Reject();
        }
        string Text(JsonElement item, string name, int maximum, bool required = false)
        {
            if (!item.TryGetProperty(name, out var property)
                || property.ValueKind != JsonValueKind.String)
                Reject();
            var text = property.GetString()!;
            if (text.Length > maximum || required && string.IsNullOrWhiteSpace(text))
                Reject();
            return text;
        }
        IReadOnlyList<string> TextArray(
            JsonElement item,
            string name,
            int maximum,
            IReadOnlySet<string> allowed,
            bool required)
        {
            if (!item.TryGetProperty(name, out var array)
                || array.ValueKind != JsonValueKind.Array
                || array.GetArrayLength() > maximum
                || required && array.GetArrayLength() == 0)
                Reject();
            var result = new List<string>();
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String
                    || !allowed.Contains(element.GetString()!)
                    || result.Contains(element.GetString()!, StringComparer.Ordinal))
                    Reject();
                result.Add(element.GetString()!);
            }
            return result;
        }

        if (!prompt.TryGetProperty("candidateInventory", out var inventory)
            || !inventory.TryGetProperty("enabled", out var enabled)
            || enabled.ValueKind != JsonValueKind.True
            || value.GetRawText().Length > 16_384)
            Reject();
        Properties(value, ["items"]);
        var items = value.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() is < 1 or > 32)
            Reject();

        var visible = prompt.GetProperty("evidence").EnumerateArray()
            .ToDictionary(item => ReadString(item, "evidenceId"), item => item, StringComparer.Ordinal);
        var visibleIds = visible.Keys.ToHashSet(StringComparer.Ordinal);
        var sourceKeys = visible.Values.Select(item => ReadString(item, "sourceKey"))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        var roles = prompt.GetProperty("claimCoordinates").EnumerateArray()
            .Select(item => ReadString(item, "columnLabel"))
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .ToHashSet(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CandidateInventoryItem>();

        foreach (var item in items.EnumerateArray())
        {
            Properties(item,
                ["key", "exactTitle", "sourceKey", "targetRoles", "selectedRoles", "status", "note",
                    "locatorEvidenceIds", "bodyEvidenceIds"]);
            var key = Text(item, "key", 80, true);
            var title = Text(item, "exactTitle", 240, true);
            var sourceKey = Text(item, "sourceKey", 200, true);
            var status = Text(item, "status", 40, true);
            var note = Text(item, "note", 200);
            if (!keys.Add(key)
                || !sourceKeys.Contains(sourceKey)
                || !CandidateInventoryStates.Contains(status, StringComparer.Ordinal)
                || !identities.Add(sourceKey + "\n" + NormalizeClaimText(title)))
                Reject();
            var targetRoles = TextArray(item, "targetRoles", 8, roles, required: true);
            var selectedRoles = TextArray(item, "selectedRoles", 8, roles, required: false);
            var locatorIds = TextArray(
                item,
                "locatorEvidenceIds",
                4,
                visibleIds,
                required: false);
            var bodyIds = TextArray(
                item,
                "bodyEvidenceIds",
                4,
                visibleIds,
                required: false);
            if (locatorIds.Concat(bodyIds).Any(id =>
                    !string.Equals(
                        ReadString(visible[id], "sourceKey"),
                        sourceKey,
                        StringComparison.Ordinal)))
                Reject();
            if (bodyIds.Any(id => string.Equals(
                    ReadString(visible[id], "contentRole"),
                    RetrievalContentClassifier.NavigationRole,
                    StringComparison.Ordinal)))
                Reject();
            var references = locatorIds.Concat(bodyIds).Distinct(StringComparer.Ordinal).ToArray();
            if (references.Length == 0
                || status is "navigation_only" or "body_requested" && locatorIds.Count == 0
                || status is "body_verified" or "selected" && bodyIds.Count == 0
                || status == "selected" && selectedRoles.Count == 0
                || status != "selected" && selectedRoles.Count > 0)
                Reject();
            var normalizedTitle = NormalizeClaimText(title);
            if (normalizedTitle.Length == 0 || !references.Any(id =>
                NormalizeClaimText(ReadString(visible[id], "candidateTitle")) == normalizedTitle
                || NormalizeClaimText(ReadString(visible[id], "content")).Contains(
                    normalizedTitle,
                    StringComparison.Ordinal)))
                Reject();
            result.Add(new CandidateInventoryItem(
                key,
                title,
                sourceKey,
                targetRoles,
                selectedRoles,
                status,
                note,
                locatorIds,
                bodyIds));
        }
        return result;
    }

    private static IReadOnlyList<CandidateInventoryItem> MergeCandidateInventory(
        IReadOnlyList<CandidateInventoryItem> current,
        IReadOnlyList<CandidateInventoryItem> updates)
    {
        static int Rank(string status) => status switch
        {
            "navigation_only" => 0,
            "body_requested" => 1,
            "body_verified" => 2,
            "selected" => 3,
            "rejected" => 4,
            _ => -1
        };
        var merged = current.ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var update in updates)
        {
            if (!merged.TryGetValue(update.Key, out var prior))
            {
                var matchingIdentity = merged.Values.FirstOrDefault(item =>
                    string.Equals(item.SourceKey, update.SourceKey, StringComparison.Ordinal)
                    && string.Equals(
                        NormalizeClaimText(item.ExactTitle),
                        NormalizeClaimText(update.ExactTitle),
                        StringComparison.Ordinal));
                if (matchingIdentity is null)
                {
                    merged.Add(update.Key, update);
                    continue;
                }
                prior = matchingIdentity;
            }
            if (!string.Equals(prior.ExactTitle, update.ExactTitle, StringComparison.Ordinal)
                || !string.Equals(prior.SourceKey, update.SourceKey, StringComparison.Ordinal))
                throw new AdvancedAnalysisProviderException(
                    "advanced_native_candidate_inventory_invalid");
            var status = update.Status == "rejected" || prior.Status == "rejected"
                ? update.Status
                : Rank(update.Status) >= Rank(prior.Status) ? update.Status : prior.Status;
            merged[prior.Key] = prior with
            {
                TargetRoles = prior.TargetRoles.Concat(update.TargetRoles)
                    .Distinct(StringComparer.Ordinal).Take(8).ToArray(),
                SelectedRoles = status == "selected"
                    ? prior.SelectedRoles.Concat(update.SelectedRoles)
                        .Distinct(StringComparer.Ordinal).Take(8).ToArray()
                    : [],
                Status = status,
                Note = string.IsNullOrWhiteSpace(update.Note) ? prior.Note : update.Note,
                LocatorEvidenceIds = prior.LocatorEvidenceIds.Concat(update.LocatorEvidenceIds)
                    .Distinct(StringComparer.Ordinal).Take(4).ToArray(),
                BodyEvidenceIds = prior.BodyEvidenceIds.Concat(update.BodyEvidenceIds)
                    .Distinct(StringComparer.Ordinal).Take(4).ToArray()
            };
        }
        if (merged.Count > 64
            || merged.Values.GroupBy(
                    item => item.SourceKey + "\n" + NormalizeClaimText(item.ExactTitle),
                    StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1))
            throw new AdvancedAnalysisProviderException(
                "advanced_native_candidate_inventory_invalid");
        return merged.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<CandidateInventoryItem> ObserveCandidateInventory(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<CandidateInventoryItem> current,
        IReadOnlyList<PromptEvidenceItem> observations)
    {
        if (!CandidateInventoryEnabled(request)) return current;
        var merged = current.ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var evidence in observations.Where(item =>
                     item.CandidateTitleIsSourceExact
                     && !string.IsNullOrWhiteSpace(item.CandidateTitle)
                     && !string.IsNullOrWhiteSpace(item.EvidenceId)
                     && !string.IsNullOrWhiteSpace(item.SourceKey)))
        {
            var title = evidence.CandidateTitle!;
            var identity = evidence.SourceKey + "\n" + NormalizeClaimText(title);
            var existing = merged.Values.FirstOrDefault(item =>
                string.Equals(
                    item.SourceKey + "\n" + NormalizeClaimText(item.ExactTitle),
                    identity,
                    StringComparison.OrdinalIgnoreCase));
            var roles = evidence.TargetColumns
                .Select(target => request.Handoff.Load.Columns.FirstOrDefault(column =>
                    string.Equals(column, target, StringComparison.OrdinalIgnoreCase)))
                .Where(static target => target is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var navigation = string.Equals(
                evidence.ContentRole,
                RetrievalContentClassifier.NavigationRole,
                StringComparison.Ordinal);
            if (existing is null)
            {
                if (merged.Count >= 64) continue;
                var observed = new CandidateInventoryItem(
                    BuildCandidateInventoryKey(evidence.SourceKey, title),
                    title,
                    evidence.SourceKey,
                    roles,
                    [],
                    navigation ? "navigation_only" : "body_verified",
                    "Automatically retained exact source title; suitability remains a model decision.",
                    navigation ? [evidence.EvidenceId!] : [],
                    navigation ? [] : [evidence.EvidenceId!]);
                merged.Add(observed.Key, observed);
                continue;
            }
            var status = existing.Status is "selected" or "rejected"
                ? existing.Status
                : navigation ? existing.Status : "body_verified";
            merged[existing.Key] = existing with
            {
                TargetRoles = existing.TargetRoles.Concat(roles)
                    .Distinct(StringComparer.Ordinal).Take(8).ToArray(),
                Status = status,
                LocatorEvidenceIds = navigation
                    ? existing.LocatorEvidenceIds.Append(evidence.EvidenceId!)
                        .Distinct(StringComparer.Ordinal).Take(4).ToArray()
                    : existing.LocatorEvidenceIds,
                BodyEvidenceIds = navigation
                    ? existing.BodyEvidenceIds
                    : existing.BodyEvidenceIds.Append(evidence.EvidenceId!)
                        .Distinct(StringComparer.Ordinal).Take(4).ToArray()
            };
        }
        return merged.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence> PrioritizeCandidateInventoryEvidence(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        IReadOnlyList<CandidateInventoryItem> inventory,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> focused)
    {
        var ids = inventory
            .Where(item => item.Status != "rejected")
            .SelectMany(item => item.BodyEvidenceIds.Concat(item.LocatorEvidenceIds))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var byId = evidence.ToDictionary(item => item.Reference.EvidenceId!, StringComparer.Ordinal);
        var retained = ids.Where(byId.ContainsKey).ToArray();
        var retainedSet = retained.ToHashSet(StringComparer.Ordinal);
        return retained.Select(id => byId[id])
            .Concat(focused.Where(item => !retainedSet.Contains(item.Reference.EvidenceId!)))
            .ToArray();
    }

    private static object BuildCandidateInventoryForPrompt(
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<CandidateInventoryItem> inventory,
        IReadOnlyList<PromptEvidenceItem> observations)
    {
        var visible = observations.Select(item => item.EvidenceId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var coverage = request.Handoff.Load.Columns.Select(column => new
        {
            targetRole = column,
            requiredCount = request.Handoff.Load.RowCount,
            bodyVerifiedCount = inventory.Count(item =>
                (item.Status is "body_verified" or "selected")
                && item.TargetRoles.Contains(column, StringComparer.Ordinal)
                && item.BodyEvidenceIds.Count > 0),
            selectedCount = inventory.Count(item => item.Status == "selected"
                && item.SelectedRoles.Contains(column, StringComparer.Ordinal))
        }).ToArray();
        return new
        {
            enabled = true,
            version = "candidate-inventory.v1",
            updateMode = "upsert",
            items = inventory,
            coverage,
            currentVisibleEvidenceIds = inventory
                .SelectMany(item => item.LocatorEvidenceIds.Concat(item.BodyEvidenceIds))
                .Distinct(StringComparer.Ordinal)
                .Where(visible.Contains)
                .ToArray(),
            instruction = "Operational candidate memory, not proof. Use save_candidate_inventory to retain exact observed titles across focus changes. A navigation locator discovers an item; body_verified and selected require current substantive body evidence. Omitted candidates remain stored. Continue research from coverage gaps, and do not turn a bounded search limit into corpus absence."
        };
    }

    private static void RememberSelectedCandidates(
        AdvancedAnalysisProviderRequest request,
        AdvancedAnalysisProviderResult result,
        IReadOnlyList<PromptEvidenceItem> observations,
        SynthesisResearchContext context)
    {
        if (!CandidateInventoryEnabled(request)
            || result.Outcome != "answered")
            return;

        var coordinates = BuildStructuredClaimCoordinates(request.Handoff.Load)
            .ToDictionary(item => item.ClaimId, item => item.ColumnLabel, StringComparer.Ordinal);
        var inventory = context.CandidateInventory.ToArray();
        foreach (var claim in result.Claims.Where(claim =>
                     !string.IsNullOrWhiteSpace(claim.SelectedItem)))
        {
            if (!coordinates.TryGetValue(claim.ClaimId, out var role))
                continue;
            var normalizedTitle = NormalizeClaimText(claim.SelectedItem!);
            var bodyObservations = observations.Where(observation =>
                    observation.EvidenceId is not null
                    && claim.EvidenceIds.Contains(observation.EvidenceId, StringComparer.Ordinal)
                    && observation.ContentRole != RetrievalContentClassifier.NavigationRole
                    && (NormalizeClaimText(observation.CandidateTitle ?? string.Empty) == normalizedTitle
                        || NormalizeClaimText(observation.Content).Contains(
                            normalizedTitle,
                            StringComparison.Ordinal)))
                .ToArray();
            var sourceKeys = bodyObservations.Select(item => item.SourceKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (normalizedTitle.Length == 0 || sourceKeys.Length != 1)
                continue;
            var sourceKey = sourceKeys[0];
            var bodyIds = bodyObservations.Select(item => item.EvidenceId!)
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToArray();
            var matches = inventory.Select((item, index) => new { Item = item, Index = index })
                .Where(candidate =>
                    string.Equals(candidate.Item.SourceKey, sourceKey, StringComparison.Ordinal)
                    && NormalizeClaimText(candidate.Item.ExactTitle) == normalizedTitle)
                .ToArray();
            if (matches.Length == 0)
            {
                if (inventory.Length >= 64)
                    continue;
                Array.Resize(ref inventory, inventory.Length + 1);
                inventory[^1] = new CandidateInventoryItem(
                    BuildCandidateInventoryKey(sourceKey, claim.SelectedItem!),
                    claim.SelectedItem!,
                    sourceKey,
                    [role],
                    [role],
                    "selected",
                    "Automatically retained from a Writer selection with visible substantive body evidence.",
                    [],
                    bodyIds);
                continue;
            }
            if (matches.Length != 1)
                continue;

            var match = matches[0];
            var roles = match.Item.TargetRoles.Append(role);
            var selectedRoles = match.Item.SelectedRoles.Append(role);
            inventory[match.Index] = match.Item with
            {
                Status = "selected",
                TargetRoles = roles.Distinct(StringComparer.Ordinal).Take(8).ToArray(),
                SelectedRoles = selectedRoles
                    .Distinct(StringComparer.Ordinal).Take(8).ToArray(),
                BodyEvidenceIds = match.Item.BodyEvidenceIds.Concat(bodyIds)
                    .Distinct(StringComparer.Ordinal).Take(4).ToArray()
            };
        }
        context.CandidateInventory = inventory
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
    }
}
