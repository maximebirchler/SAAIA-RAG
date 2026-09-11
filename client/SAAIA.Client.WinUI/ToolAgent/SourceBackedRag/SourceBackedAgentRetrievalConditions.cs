using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string BuildObservedRetrievalConditions(EvidenceBundle bundle, int maximumAttempts)
    {
        var recent = bundle.RetrievalAttempts.TakeLast(Math.Max(1, maximumAttempts)).ToArray();
        if (!recent.Any(attempt => !string.Equals(attempt.Outcome, "completed", StringComparison.OrdinalIgnoreCase)))
            return string.Empty;

        var text = new StringBuilder("OBSERVED RETRIEVAL CONDITIONS (technical observations, not source evidence):\n");
        foreach (var attempt in recent)
        {
            text.Append("- sequence=").Append(attempt.Sequence)
                .Append(" | tool=").Append(attempt.ToolName)
                .Append(" | queries=").Append(JsonSerializer.Serialize(attempt.Queries))
                .Append(" | outcome=").Append(attempt.Outcome)
                .Append(" | degraded_retrievers=").Append(string.Join(",", attempt.DegradedRetrievers))
                .Append(" | evidence_items=").Append(attempt.EvidenceItemCount)
                .Append(" | duration_ms=").Append(attempt.DurationMs);
            if (!string.IsNullOrWhiteSpace(attempt.ErrorCode))
                text.Append(" | error_code=").Append(attempt.ErrorCode);
            if (attempt.Busy) text.Append(" | busy=true");
            text.AppendLine();
        }
        text.Append("A degraded or failed retrieval does not establish absence from the document or corpus.");
        return text.ToString();
    }
}
