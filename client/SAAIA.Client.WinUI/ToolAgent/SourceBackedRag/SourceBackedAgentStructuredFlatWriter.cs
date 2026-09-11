using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    internal sealed record StructuredFlatWriterExecution(
        SourceBackedAgentCompletion Completion,
        bool ProtocolValid,
        string FailureReason,
        string RawOutput,
        int ClaimCount,
        int LlmCallCount);

    private sealed record StructuredFlatWriterClaim(
        string Text,
        IReadOnlyList<string> EvidenceIds);

    internal async Task<StructuredFlatWriterExecution>
        CompleteStructuredFlatWriterAsync(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
            IReadOnlyList<string> selectedEvidenceIds,
            string atomicEvidenceMode,
            string? revisionInstruction,
            CancellationToken ct,
            string? semanticJudgeAssessment = null,
            IReadOnlyList<string>? semanticJudgeLeadEvidenceIds = null,
            int? requiredClaimCount = null)
    {
        if (_llm is not ISourceBackedAgentStructuredLlmClient structuredLlm)
        {
            return FailedStructuredFlatWriterExecution(
                new SourceBackedAgentCompletion(
                    string.Empty,
                    Array.Empty<SourceBackedAgentToolCall>(),
                    "structured_output_unavailable"),
                string.Empty,
                "structured_flat_writer_capability_unavailable");
        }

        var writerContext = BuildSelectedEvidenceWriterContext(
            bundle,
            selectedEvidenceIds,
            atomicEvidenceMode);
        if (writerContext.AllowedEvidenceIds.Count == 0)
        {
            return FailedStructuredFlatWriterExecution(
                new SourceBackedAgentCompletion(
                    string.Empty,
                    Array.Empty<SourceBackedAgentToolCall>(),
                    "protocol_error"),
                string.Empty,
                "structured_flat_writer_evidence_pool_empty");
        }

        if (requiredClaimCount is > 12)
        {
            return FailedStructuredFlatWriterExecution(
                new SourceBackedAgentCompletion(
                    string.Empty,
                    Array.Empty<SourceBackedAgentToolCall>(),
                    "protocol_error"),
                string.Empty,
                "structured_flat_writer_required_claim_count_exceeds_local_limit");
        }

        var maximumClaimCount = Math.Clamp(
            Math.Max(
                requiredClaimCount.GetValueOrDefault(),
                Math.Max(
                    writerContext.Groups.Count,
                    writerContext.AllowedEvidenceIds.Count * 2)),
            1,
            12);
        var maximumOutputTokens = Math.Min(
            _options.MaximumOutputTokens,
            Math.Clamp(
                160 + Math.Max(
                    writerContext.AllowedEvidenceIds.Count,
                    requiredClaimCount.GetValueOrDefault()) * 64,
                320,
                640));
        var messages = BuildStructuredFlatWriterMessages(
            intake,
            bundle,
            selectedEvidenceIds,
            atomicEvidenceMode,
            revisionInstruction,
            semanticJudgeAssessment,
            semanticJudgeLeadEvidenceIds,
            requiredClaimCount);
        var completion = await structuredLlm.CompleteStructuredAsync(
                messages,
                BuildStructuredFlatWriterContract(
                    writerContext.AllowedEvidenceIds,
                    maximumClaimCount,
                    requiredClaimCount),
                maximumOutputTokens,
                ct,
                temperatureOverride: 0)
            .ConfigureAwait(false);
        var rawOutput = completion.Content ?? string.Empty;
        if (!TryReadStructuredFlatWriterOutput(
                rawOutput,
                writerContext,
                atomicEvidenceMode,
                maximumClaimCount,
                requiredClaimCount,
                out var presentation,
                out var claims,
                out var failureReason))
        {
            return FailedStructuredFlatWriterExecution(
                completion with { FinishReason = "protocol_error" },
                rawOutput,
                failureReason);
        }

        var rendered = RenderStructuredFlatWriterClaims(presentation, claims);
        return new StructuredFlatWriterExecution(
            completion with
            {
                Content = rendered,
                ToolCalls = Array.Empty<SourceBackedAgentToolCall>(),
                FinishReason = "structured_flat_writer"
            },
            true,
            string.Empty,
            rawOutput,
            claims.Count,
            1);
    }

    private static StructuredFlatWriterExecution
        FailedStructuredFlatWriterExecution(
            SourceBackedAgentCompletion completion,
            string rawOutput,
            string failureReason)
        => new(
            completion with
            {
                Content = string.Empty,
                ToolCalls = Array.Empty<SourceBackedAgentToolCall>(),
                FinishReason = "protocol_error",
                ProtocolError = failureReason,
                ProtocolRawOutput = rawOutput
            },
            false,
            failureReason,
            rawOutput,
            0,
            string.IsNullOrWhiteSpace(rawOutput) ? 0 : 1);

    private static IReadOnlyList<SourceBackedAgentMessage>
        BuildStructuredFlatWriterMessages(
            SourceBackedIntake intake,
            EvidenceBundle bundle,
        IReadOnlyList<string> selectedEvidenceIds,
        string atomicEvidenceMode,
        string? revisionInstruction,
        string? semanticJudgeAssessment,
        IReadOnlyList<string>? semanticJudgeLeadEvidenceIds,
        int? requiredClaimCount)
    {
        var sourceMessages = BuildSelectedEvidenceWriterMessages(
            intake,
            bundle,
            selectedEvidenceIds,
            atomicEvidenceMode,
            revisionInstruction,
            semanticJudgeAssessment,
            semanticJudgeLeadEvidenceIds);
        return new[]
        {
            SourceBackedAgentMessage.System(
                "Tu es le redacteur final d'un RAG source-backed. Redige chaque "
                + "affirmation documentaire comme un claim autonome dans le schema "
                + "impose. Pour chaque claim, choisis uniquement les EvidenceIds dont "
                + "l'extrait soutient exactement le texte. N'ecris aucune citation, "
                + "aucun EvidenceId ni marqueur de protocole dans text: renseigne-les "
                + "seulement dans evidenceIds. Ne suppose aucun fait et respecte la "
                 + "langue, la portee et la correction demandee.\n\n"
                 + (requiredClaimCount is > 0
                     ? $"Produis exactement {requiredClaimCount.Value} claims autonomes, un par valeur nommee demandee, meme si une preuve soutient plusieurs valeurs. Chaque text contient uniquement la valeur extraite de l'extrait cite, sans role, definition, explication, interpretation ni consequence. Conserve l'ordre source.\n\n"
                     : string.Empty)
                 + BuildSelectedWriterFidelityInstructions(string.Equals(
                    atomicEvidenceMode, "content_claim", StringComparison.OrdinalIgnoreCase))),
            sourceMessages[1]
        };
    }

    private static LlmStructuredOutputContract BuildStructuredFlatWriterContract(
        IReadOnlyList<string> allowedEvidenceIds,
        int maximumClaimCount,
        int? requiredClaimCount)
    {
        var schema = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["presentation"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "paragraphs", "bullets" }
                    },
                    ["claims"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["minItems"] = requiredClaimCount.GetValueOrDefault(1),
                        ["maxItems"] = requiredClaimCount ?? maximumClaimCount,
                        ["items"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["text"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["minLength"] = 1,
                                    ["maxLength"] = 480
                                },
                                ["evidenceIds"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "array",
                                    ["minItems"] = 1,
                                    ["maxItems"] = Math.Min(
                                        4,
                                        allowedEvidenceIds.Count),
                                    ["uniqueItems"] = true,
                                    ["items"] = new Dictionary<string, object?>
                                    {
                                        ["type"] = "string",
                                        ["enum"] = allowedEvidenceIds
                                    }
                                }
                            },
                            ["required"] = new[] { "text", "evidenceIds" },
                            ["additionalProperties"] = false
                        }
                    }
                },
                ["required"] = new[] { "presentation", "claims" },
                ["additionalProperties"] = false
            });
        return new LlmStructuredOutputContract(
            "source_backed_flat_writer_v1",
            schema);
    }

    private static bool TryReadStructuredFlatWriterOutput(
        string rawOutput,
        SelectedEvidenceWriterContext writerContext,
        string atomicEvidenceMode,
        int maximumClaimCount,
        int? requiredClaimCount,
        out string presentation,
        out IReadOnlyList<StructuredFlatWriterClaim> claims,
        out string failureReason)
    {
        presentation = string.Empty;
        claims = Array.Empty<StructuredFlatWriterClaim>();
        failureReason = string.Empty;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawOutput);
        }
        catch (JsonException)
        {
            failureReason = "structured_flat_writer_json_invalid";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasOnlyStructuredFlatWriterProperties(
                    root,
                    "presentation",
                    "claims")
                || !root.TryGetProperty("presentation", out var presentationNode)
                || presentationNode.ValueKind != JsonValueKind.String
                || (presentation = presentationNode.GetString() ?? string.Empty)
                    is not ("paragraphs" or "bullets")
                || !root.TryGetProperty("claims", out var claimsNode)
                 || claimsNode.ValueKind != JsonValueKind.Array
                 || claimsNode.GetArrayLength() is < 1
                 || claimsNode.GetArrayLength() > maximumClaimCount
                 || (requiredClaimCount is > 0
                     && claimsNode.GetArrayLength() != requiredClaimCount.Value))
            {
                failureReason = "structured_flat_writer_root_contract_invalid";
                return false;
            }

            var allowedIds = writerContext.AllowedEvidenceIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var parsedClaims = new List<StructuredFlatWriterClaim>(
                claimsNode.GetArrayLength());
            var citedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var claimNode in claimsNode.EnumerateArray())
            {
                if (claimNode.ValueKind != JsonValueKind.Object
                    || !HasOnlyStructuredFlatWriterProperties(
                        claimNode,
                        "text",
                        "evidenceIds")
                    || !claimNode.TryGetProperty("text", out var textNode)
                    || textNode.ValueKind != JsonValueKind.String)
                {
                    failureReason = "structured_flat_writer_claim_contract_invalid";
                    return false;
                }

                var text = textNode.GetString()?.Trim() ?? string.Empty;
                if (text.Length == 0)
                {
                    failureReason = "structured_flat_writer_claim_text_missing";
                    return false;
                }
                if (text.Length > 480)
                {
                    failureReason = "structured_flat_writer_claim_text_too_long";
                    return false;
                }
                if (ContainsStructuredFlatWriterControlMarker(text))
                {
                    failureReason = "structured_flat_writer_claim_control_marker";
                    return false;
                }
                if (Regex.IsMatch(
                    text,
                    @"\[E(?:#|\d+)\]",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    failureReason = "structured_flat_writer_claim_citation_present";
                    return false;
                }
                if (!claimNode.TryGetProperty(
                        "evidenceIds",
                        out var evidenceIdsNode)
                    || evidenceIdsNode.ValueKind != JsonValueKind.Array
                    || evidenceIdsNode.GetArrayLength() is < 1 or > 4)
                {
                    failureReason =
                        "structured_flat_writer_claim_evidence_ids_invalid";
                    return false;
                }

                var claimEvidenceIds = new List<string>(
                    evidenceIdsNode.GetArrayLength());
                var claimEvidenceIdSet = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var evidenceIdNode in evidenceIdsNode.EnumerateArray())
                {
                    var evidenceId = evidenceIdNode.ValueKind == JsonValueKind.String
                        ? evidenceIdNode.GetString()?.Trim().ToUpperInvariant()
                        : null;
                    if (string.IsNullOrWhiteSpace(evidenceId)
                        || !allowedIds.Contains(evidenceId))
                    {
                        failureReason =
                            "structured_flat_writer_evidence_id_not_allowed";
                        return false;
                    }
                    if (!claimEvidenceIdSet.Add(evidenceId))
                    {
                        failureReason =
                            "structured_flat_writer_evidence_id_duplicate";
                        return false;
                    }
                    claimEvidenceIds.Add(evidenceId);
                    citedIds.Add(evidenceId);
                }
                if (requiredClaimCount is > 0
                    && !IsStrictExtractiveNamedValueClaim(
                        text,
                        claimEvidenceIds,
                        writerContext))
                {
                    failureReason =
                        "structured_flat_writer_named_value_not_extractive";
                    return false;
                }
                parsedClaims.Add(new StructuredFlatWriterClaim(
                    text,
                    claimEvidenceIds));
            }

            if (!string.Equals(
                    atomicEvidenceMode,
                    "content_claim",
                    StringComparison.OrdinalIgnoreCase)
                && writerContext.RequiredEvidenceIdGroups.Any(group =>
                    !group.Any(citedIds.Contains)))
            {
                failureReason = "structured_flat_writer_required_group_missing";
                return false;
            }

            claims = parsedClaims;
            return true;
        }
    }

    private static bool HasOnlyStructuredFlatWriterProperties(
        JsonElement element,
        params string[] expectedNames)
    {
        var expected = expectedNames.ToHashSet(StringComparer.Ordinal);
        var actual = element.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        return actual.Length == expected.Count
               && actual.All(expected.Contains);
    }

    private static bool ContainsStructuredFlatWriterControlMarker(string text)
        => new[]
        {
            "UNITE_SELECTIONNEE",
            "PREUVE_CITABLE",
            "DEMANDE ORIGINALE",
            "POOL DE PREUVES"
        }.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string RenderStructuredFlatWriterClaims(
        string presentation,
        IReadOnlyList<StructuredFlatWriterClaim> claims)
    {
        var renderedClaims = claims.Select(claim =>
        {
            var punctuation = claim.Text[^1] is '.' or '!' or '?' or ';' or ':'
                ? claim.Text[^1].ToString()
                : ".";
            var text = punctuation == "." && claim.Text[^1] != '.'
                ? claim.Text
                : claim.Text[..^1].TrimEnd();
            var citations = string.Join(
                string.Empty,
                claim.EvidenceIds.Select(static id => $"[{id}]"));
            return $"{text} {citations}{punctuation}";
        }).ToArray();
        return string.Equals(
                presentation,
                "bullets",
                StringComparison.Ordinal)
            ? string.Join(Environment.NewLine, renderedClaims.Select(
                static claim => "- " + claim))
            : string.Join(Environment.NewLine + Environment.NewLine, renderedClaims);
    }
}
