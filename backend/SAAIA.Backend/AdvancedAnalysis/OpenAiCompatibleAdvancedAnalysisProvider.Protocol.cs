using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private static AdvancedAnalysisProviderResult ParseResult(
        string raw,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence,
        AdvancedAnalysisProviderRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(UnwrapJson(raw));
            var root = document.RootElement;
            var outcome = ReadString(root, "outcome");
            var answerText = RemoveInternalEvidenceAnnotations(
                ReadString(root, "answerText"));
            if (outcome is not (
                    "answered"
                    or "insufficient_documentation"
                    or "clarification_required")
                || string.IsNullOrWhiteSpace(answerText))
            {
                throw new JsonException();
            }

            var knownEvidence = evidence
                .Select(static item => item.Reference.EvidenceId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            var claims = new List<AdvancedAnalysisResultClaim>();
            if (root.TryGetProperty("claims", out var rawClaims)
                && rawClaims.ValueKind == JsonValueKind.Array)
            {
                foreach (var rawClaim in rawClaims.EnumerateArray())
                {
                    var claimId = ReadString(rawClaim, "claimId");
                    var selectedItem = ReadString(rawClaim, "selectedItem");
                    var text = RemoveInternalEvidenceAnnotations(
                        ReadString(rawClaim, "text"));
                    if (string.IsNullOrWhiteSpace(claimId)
                        || string.IsNullOrWhiteSpace(text)
                        || !rawClaim.TryGetProperty(
                            "evidenceIds",
                            out var rawEvidenceIds)
                        || rawEvidenceIds.ValueKind != JsonValueKind.Array)
                    {
                        throw new JsonException();
                    }
                    var evidenceIds = rawEvidenceIds
                        .EnumerateArray()
                        .Where(static item =>
                            item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString()?.Trim())
                        .Where(static id => !string.IsNullOrWhiteSpace(id))
                        .Select(static id => id!)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (evidenceIds.Count == 0
                        || evidenceIds.Any(id => !knownEvidence.Contains(id)))
                    {
                        throw new AdvancedAnalysisProviderException(
                            "advanced_writer_evidence_id_invalid");
                    }
                    claims.Add(new AdvancedAnalysisResultClaim
                    {
                        ClaimId = claimId,
                        SelectedItem = string.IsNullOrWhiteSpace(selectedItem)
                            ? null
                            : selectedItem.Trim(),
                        Text = text,
                        EvidenceIds = evidenceIds
                    });
                }
            }
            if (outcome == "answered" && claims.Count == 0)
                throw new JsonException();
            if (outcome == "answered"
                && request.Handoff.Load.AnswerUnitCount > 0
                && claims.Count != request.Handoff.Load.AnswerUnitCount)
            {
                throw new AdvancedAnalysisProviderException(
                    "advanced_writer_claim_count_invalid");
            }
            if (outcome == "answered"
                && claims
                    .Select(claim => NormalizeClaimDeduplicationKey(
                        claim,
                        request.Handoff.Load))
                    .GroupBy(static text => text, StringComparer.Ordinal)
                    .Any(static group => group.Count() > 1)
                && !RequiresDistinctStructuredSelection(request.Handoff.Load))
            {
                throw new AdvancedAnalysisProviderException(
                    "advanced_writer_duplicate_claims");
            }
            if (outcome == "answered"
                && claims.Any(claim => Regex.Matches(
                        answerText,
                        Regex.Escape($"[{claim.ClaimId}]"),
                        RegexOptions.CultureInvariant).Count != 1))
            {
                throw new AdvancedAnalysisProviderException(
                    "advanced_writer_claim_markers_invalid");
            }
            if (ContainsInternalProtocolIdentifier(answerText)
                || claims.Any(static claim =>
                    ContainsInternalProtocolIdentifier(claim.Text)
                    || ContainsInternalProtocolIdentifier(
                        claim.SelectedItem ?? string.Empty)))
            {
                throw new AdvancedAnalysisProviderException(
                    "advanced_writer_protocol_invalid");
            }

            return new AdvancedAnalysisProviderResult
            {
                Outcome = outcome,
                AnswerText = answerText,
                Claims = claims
            };
        }
        catch (JsonException)
        {
            throw new AdvancedAnalysisProviderException(
                "advanced_writer_protocol_invalid");
        }
    }

    private static string NormalizeClaimText(string value)
        => Regex.Replace(
                value ?? string.Empty,
                @"[^\p{L}\p{N}]+",
                " ",
                RegexOptions.CultureInvariant)
            .Trim()
            .ToLowerInvariant();

    private static string NormalizeClaimDeduplicationKey(
        AdvancedAnalysisResultClaim claim,
        AdvancedAnalysisLoadDescriptor load)
    {
        var selectedItem = claim.SelectedItem?.Trim() ?? string.Empty;
        return string.Equals(
                   load.AtomicEvidenceMode,
                   "named_item",
                   StringComparison.OrdinalIgnoreCase)
               && selectedItem.Length > 0
            ? NormalizeClaimText(selectedItem)
            : NormalizeClaimText(claim.Text);
    }

    private static bool ContainsInternalProtocolIdentifier(string value)
        => Regex.IsMatch(
            value ?? string.Empty,
            @"\b(?:internal-source-\d+|advanced-evidence-[0-9a-f]{16,64})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string RemoveInternalEvidenceAnnotations(string value)
    {
        const string evidenceId = @"advanced-evidence-[0-9a-f]{16,64}";
        var cleaned = Regex.Replace(
            value ?? string.Empty,
            @"\s*\((?:(?:citation|source|preuve)\s*:\s*)?" + evidenceId + @"\s*\)",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"\b" + evidenceId + @"\b",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(
                cleaned,
                @"[ \t]{2,}",
                " ",
                RegexOptions.CultureInvariant)
            .Trim();
    }

    private AdvancedAnalysisProviderResult WithMetrics(
        AdvancedAnalysisProviderResult result,
        IReadOnlyList<CompletionResult> completions,
        AdvancedAnalysisLoadDescriptor load)
        => new()
        {
            Outcome = result.Outcome,
            AnswerText = result.AnswerText,
            Claims = result.Claims,
            ModelId = _options.LlmModel.Trim(),
            ProviderCallCount = completions.Count,
            SelectionMode = ResolveSelectionMode(load),
            InputTokens = SumKnownUsage(
                completions.Select(static item => item.Usage.InputTokens)),
            OutputTokens = SumKnownUsage(
                completions.Select(static item => item.Usage.OutputTokens)),
            CachedInputTokens = SumKnownUsage(
                completions.Select(static item => item.Usage.CachedInputTokens)),
            EstimatedCostUsd = completions.Any(static item =>
                    item.EstimatedCostUsd.HasValue)
                ? completions.Sum(static item => item.EstimatedCostUsd ?? 0m)
                : null
        };

    private static int? SumKnownUsage(IEnumerable<int?> values)
    {
        var known = values.Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();
        return known.Length == 0 ? null : known.Sum();
    }

    private static AdvancedAnalysisLlmUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return new AdvancedAnalysisLlmUsage(null, null, null);
        }
        var input = ReadNonNegativeInt(usage, "prompt_tokens")
                    ?? ReadNonNegativeInt(usage, "input_tokens");
        var output = ReadNonNegativeInt(usage, "completion_tokens")
                     ?? ReadNonNegativeInt(usage, "output_tokens");
        int? cached = null;
        if (usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            cached = ReadNonNegativeInt(details, "cached_tokens");
        }
        return new AdvancedAnalysisLlmUsage(input, output, cached);
    }

    private static int? ReadNonNegativeInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.TryGetInt32(out var parsed)
           && parsed >= 0
            ? parsed
            : null;
}
