using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static SemanticReview ParseSemanticReview(string? raw)
    {
        var value = raw?.Trim() ?? string.Empty;
        try
        {
            var start = value.IndexOf('{');
            var end = value.LastIndexOf('}');
            if (start < 0 || end <= start)
                throw new JsonException("No JSON object found.");

            using var document = JsonDocument.Parse(value[start..(end + 1)]);
            var root = document.RootElement;
            var decision = GetString(root, "decision")?.ToLowerInvariant() switch
            {
                "accept" => "accept",
                "revise" => "revise",
                "need_more_evidence" => "need_more_evidence",
                _ => "need_more_evidence"
            };
            var reasons = ReadReviewReasons(root);
            var rejectedEvidenceIds = ReadReviewEvidenceIds(root, "rejectedEvidenceIds");
            var preferredAlternativeEvidenceIds = ReadReviewEvidenceIds(
                root,
                "preferredAlternativeEvidenceIds");
            if (reasons.Count == 0)
            {
                return new SemanticReview(
                    "need_more_evidence",
                    new[]
                    {
                        "Contrat du juge invalide: aucune justification ni audit semantique n'a ete fourni."
                    },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    value);
            }

            return new SemanticReview(
                decision,
                reasons,
                rejectedEvidenceIds,
                preferredAlternativeEvidenceIds,
                value);
        }
        catch (JsonException ex)
        {
            return new SemanticReview(
                "need_more_evidence",
                new[] { "Contrat JSON invalide du juge semantique: " + ex.Message },
                Array.Empty<string>(),
                Array.Empty<string>(),
                value);
        }
    }

    private static IReadOnlyList<string> ReadReviewReasons(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "reasons", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return property.Value
                .EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()?.Trim())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .Take(6)
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> ReadReviewEvidenceIds(
        JsonElement root,
        string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return property.Value
                .EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .SelectMany(static item =>
                    SourceContractVerifier.ExtractEvidenceIds(item.GetString()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray();
        }

        return Array.Empty<string>();
    }
}
