using System.Text;
using System.Text.Json;

namespace SAAIA.Backend;

internal sealed class CapabilityBBackofficeSummaryService
{
    private readonly LocalLlmChatClient _llmClient;
    private readonly ChatOptions _chatOptions;

    public CapabilityBBackofficeSummaryService(
        LocalLlmChatClient llmClient,
        ChatOptions chatOptions)
    {
        _llmClient = llmClient;
        _chatOptions = chatOptions;
    }

    internal async Task<CapabilityBGeneratedSummaryPayload> BuildSummaryAsync(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        CancellationToken ct)
    {
        if (!_llmClient.IsConfigured)
            return BuildDeterministicSummary(doc, sectionTitles, excerpts, "llm_not_configured");

        var systemPrompt = """
You produce concise enterprise backoffice summaries for indexed documents.
Return plain text only.
Constraints:
- 2 to 5 short paragraphs or bullets
- no markdown heading
- no JSON
- mention concrete scope, sections, and operational takeaways when available
- keep the answer under 900 characters
""";

        var userPrompt = BuildUserPrompt(doc, sectionTitles, excerpts);
        var completion = await _llmClient.TryCompleteAsync(systemPrompt, userPrompt, maxTokens: 320, temperature: 0.1, ct);
        var summaryText = NormalizeSummary(completion);
        if (string.IsNullOrWhiteSpace(summaryText))
            return BuildDeterministicSummary(doc, sectionTitles, excerpts, "llm_empty_response");

        var meta = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["generator"] = "capability_b_worker_v2",
            ["strategy"] = "llm_document_foundation",
            ["docPath"] = doc.DocPath,
            ["indexedVersion"] = doc.IndexedVersion,
            ["sectionCount"] = sectionTitles.Count,
            ["excerptCount"] = excerpts.Count,
            ["llmModel"] = _chatOptions.LlmModel,
            ["fallbackUsed"] = false,
            ["generatedAt"] = DateTimeOffset.UtcNow
        });

        return new CapabilityBGeneratedSummaryPayload(summaryText, meta);
    }

    internal static CapabilityBGeneratedSummaryPayload BuildDeterministicSummary(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts,
        string? fallbackReason = null)
    {
        var summaryText = ComposeSummaryText(doc, sectionTitles, excerpts);
        var meta = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["generator"] = "capability_b_worker_v2",
            ["strategy"] = "deterministic_document_foundation",
            ["docPath"] = doc.DocPath,
            ["indexedVersion"] = doc.IndexedVersion,
            ["sectionCount"] = sectionTitles.Count,
            ["excerptCount"] = excerpts.Count,
            ["fallbackUsed"] = true,
            ["fallbackReason"] = fallbackReason,
            ["generatedAt"] = DateTimeOffset.UtcNow
        });

        return new CapabilityBGeneratedSummaryPayload(summaryText, meta);
    }

    private static string BuildUserPrompt(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var normalizedSections = sectionTitles
            .Where(static title => !string.IsNullOrWhiteSpace(title))
            .Select(static title => title.Trim())
            .Take(5)
            .ToArray();
        var normalizedExcerpts = excerpts
            .Where(static excerpt => !string.IsNullOrWhiteSpace(excerpt))
            .Select(TrimExcerpt)
            .Take(3)
            .ToArray();

        return $"""
Document name: {doc.DocName}
Document path: {doc.DocPath}
Category: {doc.Category ?? "unknown"}
Page count: {(doc.PageCount is > 0 ? doc.PageCount.Value.ToString() : "unknown")}
Indexed version: {doc.IndexedVersion}
Section titles:
{string.Join(Environment.NewLine, normalizedSections.Select(static title => $"- {title}"))}
Excerpt highlights:
{string.Join(Environment.NewLine, normalizedExcerpts.Select(static excerpt => $"- {excerpt}"))}
Write a short backoffice summary for operators who need to understand this document quickly.
""";
    }

    private static string NormalizeSummary(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var normalized = string.Join(
            Environment.NewLine,
            content
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => line.Trim()));

        return normalized.Trim();
    }

    private static string ComposeSummaryText(
        CapabilityBDocumentRow doc,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
    {
        var lines = new List<string>
        {
            BuildOverviewLine(doc)
        };

        if (sectionTitles.Count > 0)
            lines.Add("Key sections: " + string.Join("; ", sectionTitles.Select(NormalizeInlineText)) + ".");

        if (excerpts.Count > 0)
        {
            lines.Add("Highlights:");
            foreach (var excerpt in excerpts.Select(TrimExcerpt))
                lines.Add($"- {excerpt}");
        }
        else
        {
            lines.Add("No extracted unit excerpts were available, so this summary relies on the indexed document metadata.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildOverviewLine(CapabilityBDocumentRow doc)
    {
        var builder = new StringBuilder();
        builder.Append(doc.DocName);
        builder.Append(" is an indexed");
        if (!string.IsNullOrWhiteSpace(doc.Category))
        {
            builder.Append(' ');
            builder.Append(doc.Category!.Trim());
        }

        builder.Append(" document");
        if (doc.PageCount is > 0)
        {
            builder.Append(" with ");
            builder.Append(doc.PageCount.Value);
            builder.Append(doc.PageCount.Value == 1 ? " page" : " pages");
        }

        builder.Append(" at ");
        builder.Append(doc.DocPath);
        builder.Append('.');
        return builder.ToString();
    }

    private static string TrimExcerpt(string text)
    {
        var normalized = NormalizeInlineText(text);
        return normalized.Length <= 220
            ? normalized
            : normalized[..217] + "...";
    }

    private static string NormalizeInlineText(string text)
        => string.Join(" ", text
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();
}

internal sealed record CapabilityBGeneratedSummaryPayload(string SummaryText, JsonElement Meta);
