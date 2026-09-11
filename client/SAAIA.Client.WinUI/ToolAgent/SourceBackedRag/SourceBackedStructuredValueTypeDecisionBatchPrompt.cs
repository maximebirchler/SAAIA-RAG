using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedStructuredValueTypeDecisionBatchPrompt
{
    internal static IReadOnlyList<string> DecisionClasses { get; } = new[]
    {
        "named_item_suitable_for_slot",
        "instruction_or_action",
        "isolated_component_or_ingredient",
        "heading_or_broad_category",
        "incomplete_or_other_wrong_type"
    };

    public static IReadOnlyList<(string role, string content)> BuildMessages(
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        IReadOnlyList<SourceBackedStructuredValueTypeCandidate> candidates)
        => new[]
        {
            ("system", string.Join('\n', new[]
            {
                "SAAIA_SOURCE_BACKED_STEP=StructuredValueTypeFitDecisionBatch",
                "Classify the semantic type of each VALUE_AS_WRITTEN relative to its REQUESTED_FIELD. Do not rewrite values, plan retrieval or judge layout.",
                "Classify the grammatical and semantic role of VALUE_AS_WRITTEN itself. CITED_EXCERPTS only prove what that value refers to; instructions or ingredients elsewhere in an excerpt do not turn a self-standing item name into an action or component.",
                "named_item_suitable_for_slot: the value is a self-standing named item of the type requested by the field.",
                "instruction_or_action: the value tells someone to do something or describes a procedure step.",
                "isolated_component_or_ingredient: the value is only one component or ingredient where the field expects the assembled item.",
                "heading_or_broad_category: the value is only a heading, family, topic or broad category rather than one item.",
                "incomplete_or_other_wrong_type: the value is a fragment or another semantic type not requested by the field.",
                "Decision order: first test whether the value is an action; then whether it is only a component or measured ingredient; then whether it is a broad heading or an incomplete reference. Use named_item_suitable_for_slot only when the value itself can independently fill the requested field.",
                "A noun phrase is not automatically a suitable named item. When the field expects an assembled meal, a measured amount of flour, oil, stock or another ingredient is isolated_component_or_ingredient.",
                "Examples:",
                "REQUESTED_FIELD=transport mode, VALUE_AS_WRITTEN=train => named_item_suitable_for_slot.",
                "REQUESTED_FIELD=lunch, VALUE_AS_WRITTEN=mushroom risotto => named_item_suitable_for_slot.",
                "REQUESTED_FIELD=technical document, VALUE_AS_WRITTEN=FIT-PTFE_TF_1620-EN.pdf => named_item_suitable_for_slot.",
                "REQUESTED_FIELD=lunch, VALUE_AS_WRITTEN=mix all ingredients => instruction_or_action.",
                "REQUESTED_FIELD=complete meal, VALUE_AS_WRITTEN=olive oil => isolated_component_or_ingredient.",
                "REQUESTED_FIELD=product, VALUE_AS_WRITTEN=industrial components => heading_or_broad_category.",
                "REQUESTED_FIELD=technical document, VALUE_AS_WRITTEN=the latest one => incomplete_or_other_wrong_type.",
                "REQUESTED_FIELD=meal, VALUE_AS_WRITTEN=roasted vegetable soup, CITED_EXCERPT=roasted vegetable soup followed by ingredients and preparation => named_item_suitable_for_slot.",
                "REQUESTED_FIELD=meal, VALUE_AS_WRITTEN=stir until smooth, CITED_EXCERPT=a complete recipe containing that step => instruction_or_action.",
                "Judge each immutable candidate independently using its cited excerpt. Return exactly one class per schema property and no explanation."
            })),
            ("user", BuildUserPrompt(intake, bundle, candidates))
        };

    internal static LlmStructuredOutputContract BuildContract(
        IReadOnlyList<SourceBackedStructuredValueTypeCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            properties[candidate.CandidateId] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["enum"] = DecisionClasses
            };
        }

        var schema = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = candidates.Select(static candidate => candidate.CandidateId).ToArray(),
            ["additionalProperties"] = false
        });
        return new LlmStructuredOutputContract("structured_value_type_decision_batch", schema);
    }

    private static string BuildUserPrompt(
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        IReadOnlyList<SourceBackedStructuredValueTypeCandidate> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("USER_QUESTION: " + Trim(intake.UserQuestion, 900));
        sb.AppendLine("LANGUAGE: " + Trim(intake.Language, 20));
        sb.AppendLine("VALUE_CANDIDATES:");
        foreach (var candidate in candidates)
        {
            sb.AppendLine(
                $"{candidate.CandidateId} | REQUESTED_FIELD={Trim(candidate.ColumnHeader, 100)} | VALUE_AS_WRITTEN={Trim(candidate.ClaimText, 180)} | EVIDENCE_IDS={string.Join(',', candidate.EvidenceIds)}");
        }

        sb.AppendLine("CITED_EXCERPTS:");
        var usedIds = candidates
            .SelectMany(static candidate => candidate.EvidenceIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in bundle.Items.Where(item => usedIds.Contains(item.EvidenceId)))
        {
            var excerpt = string.IsNullOrWhiteSpace(item.NormalizedExcerpt) ? item.Excerpt : item.NormalizedExcerpt;
            sb.AppendLine($"[{item.EvidenceId}] {Trim(excerpt, 260)}");
        }

        sb.AppendLine("EXPECTED_CANDIDATE_IDS: " + string.Join(",", candidates.Select(static candidate => candidate.CandidateId)));
        return sb.ToString();
    }

    private static string Trim(string? value, int maxLength)
    {
        var normalized = string.Join(" ", (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd() + "...";
    }
}
