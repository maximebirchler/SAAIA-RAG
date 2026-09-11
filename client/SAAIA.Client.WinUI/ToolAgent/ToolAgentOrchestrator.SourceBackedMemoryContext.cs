using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int SourceBackedMemorySourceLimit = 6;
    private const int SourceBackedMemoryResearchNoteLimit = 6;

    private SourceBackedMemoryContext BuildSourceBackedMemoryContext()
    {
        var previousSources = (_mem.LastSourcesUsed ?? new List<ToolMemory.SourceRef>())
            .Where(static source => source is not null
                                    && (!string.IsNullOrWhiteSpace(source.DocId)
                                        || !string.IsNullOrWhiteSpace(source.DocPath)
                                        || !string.IsNullOrWhiteSpace(source.DocName)))
            .TakeLast(SourceBackedMemorySourceLimit)
            .Select(static source => new SourceBackedMemorySourceAnchor(
                CompactSourceBackedMemoryText(source.DocId, 120),
                CompactSourceBackedMemoryText(source.DocPath, 180),
                CompactSourceBackedMemoryText(source.DocName, 140),
                source.PageStart > 0 ? source.PageStart : null,
                source.PageEnd > 0 ? source.PageEnd : null,
                CompactSourceBackedMemoryText(source.SourceHash, 120)))
            .ToArray();

        var researchNotes = (_mem.ResearchWorkingNotes ?? new List<ToolMemory.ResearchWorkingNote>())
            .Where(static note => note is not null)
            .OrderByDescending(static note => note.CreatedAtUtc)
            .Take(SourceBackedMemoryResearchNoteLimit)
            .Select(static note => new SourceBackedMemoryResearchNote(
                CompactSourceBackedMemoryText(note.TopicKey, 120) ?? string.Empty,
                CompactSourceBackedMemoryText(note.RequestShape, 160) ?? string.Empty,
                (note.Queries ?? new List<string>())
                    .Where(static query => !string.IsNullOrWhiteSpace(query))
                    .Select(static query => CompactSourceBackedMemoryText(query, 140) ?? string.Empty)
                    .Where(static query => query.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToArray(),
                CompactSourceBackedMemoryText(note.Outcome, 180) ?? string.Empty,
                note.Accepted,
                note.CreatedAtUtc))
            .ToArray();

        return new SourceBackedMemoryContext(
            _mem.MemoryProfile,
            CompactSourceBackedMemoryText(_mem.LastLanguage, 16) ?? "fr",
            CompactSourceBackedMemoryText(_mem.LastStyle, 40) ?? "auto",
            CompactSourceBackedMemoryText(_mem.LastMode, 24) ?? "auto",
            CompactSourceBackedMemoryText(_mem.LastUserMessage, 360),
            CompactSourceBackedMemoryText(_mem.LastAssistantAnswer, 480),
            CompactSourceBackedMemoryText(_mem.LastRouterIntent, 80),
            CompactSourceBackedMemoryText(_mem.LastAnswerSource, 120),
            CompactSourceBackedMemoryText(_mem.LastFocusedDocument?.DocId, 120),
            CompactSourceBackedMemoryText(_mem.LastFocusedDocument?.DocPath, 180),
            CompactSourceBackedMemoryText(_mem.LastFocusedDocument?.DocName, 140),
            CompactSourceBackedMemoryText(_mem.LastResolvedCategory?.CategoryPath, 160),
            CompactSourceBackedMemoryText(_mem.PendingClarification?.Kind, 80),
            CompactSourceBackedMemoryText(_mem.PendingClarification?.Hint, 180),
            previousSources,
            researchNotes,
            _mem.SourceBackedConversationTurns.ToArray());
    }

    private static string? CompactSourceBackedMemoryText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = string.Join(
            ' ',
            value.Replace('\r', ' ').Replace('\n', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= maxLength
            ? text
            : text[..maxLength].TrimEnd() + "...";
    }
}
