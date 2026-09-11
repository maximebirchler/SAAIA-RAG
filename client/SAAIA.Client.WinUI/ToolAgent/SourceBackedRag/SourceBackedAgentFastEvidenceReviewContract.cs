using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static LlmStructuredOutputContract BuildFastEvidenceReviewContract(
        IReadOnlyList<FastEvidenceCandidate> candidates)
    {
        var candidateIds = candidates
            .Select(static candidate => candidate.Representative.EvidenceId)
            .Append("NONE")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var anchorIds = candidates
            .SelectMany(static candidate => candidate.SourceWindow)
            .Select(static item => item.EvidenceId)
            .Append("NONE")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var schema = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["action"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new[]
                        {
                            "answer",
                            "writer",
                            "clarify",
                            "context",
                            "research"
                        }
                    },
                    ["evidenceId"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = candidateIds
                    },
                    ["anchorId"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = anchorIds
                    },
                    ["text"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["maxLength"] = 300
                    }
                },
                ["required"] = new[]
                {
                    "action",
                    "evidenceId",
                    "anchorId",
                    "text"
                },
                ["additionalProperties"] = false
            });
        return new LlmStructuredOutputContract(
            "source_backed_fast_evidence_review_v1",
            schema);
    }

    private static string BuildFastEvidenceCitedAnswer(
        string text,
        string anchorEvidenceId)
    {
        var withoutModelCitations = Regex.Replace(
                text ?? string.Empty,
                @"\s*\[E(?:#|\d+)\]",
                string.Empty,
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant)
            .Trim();
        if (string.IsNullOrWhiteSpace(withoutModelCitations))
            return string.Empty;

        var terminalPunctuation = withoutModelCitations[^1] is
            '.' or '!' or '?' or ';' or ':'
                ? withoutModelCitations[^1].ToString()
                : string.Empty;
        var answerText = terminalPunctuation.Length == 0
            ? withoutModelCitations
            : withoutModelCitations[..^1].TrimEnd();
        return $"{answerText} [{anchorEvidenceId}]{terminalPunctuation}";
    }
}
