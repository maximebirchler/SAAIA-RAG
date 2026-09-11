using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolMemory
{
    private const int MaximumPersistentSourceBackedTurns = 32;
    private static readonly JsonSerializerOptions ConversationMemoryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed record ConversationMemoryMessage(
        string Role,
        string Content,
        string? SourcesJson);

    public void RehydrateConversationState(
        IEnumerable<ConversationMemoryMessage>? messages)
    {
        var retained = (messages ?? Array.Empty<ConversationMemoryMessage>())
            .Where(static message => message is not null)
            .ToArray();
        if (retained.Length == 0)
            return;

        foreach (var message in retained)
        {
            if (!IsAssistantRole(message.Role)
                || string.IsNullOrWhiteSpace(message.SourcesJson))
            {
                continue;
            }

            if (TryReadConversationMemory(message.SourcesJson, out var turn))
                RememberSourceBackedConversationTurn(turn);
        }

        LastUserMessage = retained
            .LastOrDefault(static message => IsUserRole(message.Role)
                                             && !string.IsNullOrWhiteSpace(message.Content))
            ?.Content.Trim();
        LastAssistantAnswer = retained
            .LastOrDefault(static message => IsAssistantRole(message.Role)
                                             && !string.IsNullOrWhiteSpace(message.Content))
            ?.Content.Trim();

        var latestSources = retained
            .Where(static message => IsAssistantRole(message.Role)
                                     && !string.IsNullOrWhiteSpace(message.SourcesJson))
            .Reverse()
            .Select(static message => TryReadSourceRefs(message.SourcesJson))
            .FirstOrDefault(static sources => sources.Count > 0);
        if (latestSources is { Count: > 0 })
            LastSourcesUsed = latestSources;
    }

    public void RememberSourceBackedConversationTurn(
        SourceBackedConversationTurnMemory? turn)
    {
        if (turn is null || string.IsNullOrWhiteSpace(turn.TurnId))
            return;

        var normalized = NormalizeConversationTurn(turn);
        SourceBackedConversationTurns = SourceBackedConversationTurns
            .Where(existing => !string.Equals(
                existing.TurnId,
                normalized.TurnId,
                StringComparison.OrdinalIgnoreCase))
            .Append(normalized)
            .OrderBy(static existing => existing.CreatedAtUtc)
            .TakeLast(MaximumPersistentSourceBackedTurns)
            .ToList();
    }

    internal static bool TryReadConversationMemory(
        string? sourcesJson,
        out SourceBackedConversationTurnMemory? turn)
    {
        turn = null;
        if (string.IsNullOrWhiteSpace(sourcesJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(sourcesJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var nested = root.GetString();
                if (!string.IsNullOrWhiteSpace(nested)
                    && !string.Equals(nested, sourcesJson, StringComparison.Ordinal))
                {
                    return TryReadConversationMemory(nested, out turn);
                }

                return false;
            }

            if (!TryGetPropertyIgnoreCase(root, "conversationMemory", out var memory)
                || memory.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            turn = memory.Deserialize<SourceBackedConversationTurnMemory>(
                ConversationMemoryJsonOptions);
            return turn is not null && !string.IsNullOrWhiteSpace(turn.TurnId);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static List<SourceRef> TryReadSourceRefs(string? sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson))
            return new List<SourceRef>();

        try
        {
            using var document = JsonDocument.Parse(sourcesJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var nested = root.GetString();
                return !string.IsNullOrWhiteSpace(nested)
                       && !string.Equals(nested, sourcesJson, StringComparison.Ordinal)
                    ? TryReadSourceRefs(nested)
                    : new List<SourceRef>();
            }

            if (!TryGetPropertyIgnoreCase(root, "sources", out var sources)
                || sources.ValueKind != JsonValueKind.Array)
            {
                return new List<SourceRef>();
            }

            return sources.EnumerateArray()
                .Where(static source => source.ValueKind == JsonValueKind.Object)
                .Select(ToSourceRef)
                .Where(static source => !string.IsNullOrWhiteSpace(source.DocId)
                                        || !string.IsNullOrWhiteSpace(source.DocPath)
                                        || !string.IsNullOrWhiteSpace(source.DocName))
                .ToList();
        }
        catch (JsonException)
        {
            return new List<SourceRef>();
        }
    }

    private static SourceRef ToSourceRef(JsonElement source)
    {
        var pageStart = ReadInt(source, "pageStart");
        var pageEnd = ReadInt(source, "pageEnd");
        var normalizedPageStart = pageStart is > 0 ? pageStart.Value : 1;
        var normalizedPageEnd = pageEnd is > 0
            ? Math.Max(normalizedPageStart, pageEnd.Value)
            : normalizedPageStart;
        return new SourceRef
        {
            EvidenceId = ReadString(source, "evidenceId"),
            DocId = ReadString(source, "docId"),
            DocPath = ReadString(source, "docPath") ?? string.Empty,
            DocName = ReadString(source, "docName"),
            PageStart = normalizedPageStart,
            PageEnd = normalizedPageEnd,
            Label = ReadString(source, "label") ?? string.Empty,
            SourceHash = ReadString(source, "sourceHash"),
            RevisionId = ReadString(source, "revisionId"),
            DocLanguage = ReadString(source, "docLanguage"),
            ProfileLanguage = ReadString(source, "profileLanguage"),
            Category = ReadString(source, "category"),
            CategoryRef = ReadString(source, "categoryRef"),
            CategoryPath = ReadString(source, "categoryPath"),
            ChunkId = ReadString(source, "chunkId"),
            AnchorId = ReadString(source, "anchorId"),
            ContentCardId = ReadString(source, "contentCardId"),
            SectionTitle = ReadString(source, "sectionTitle"),
            HeadingPath = ReadString(source, "headingPath"),
            PrevChunkId = ReadString(source, "prevChunkId"),
            NextChunkId = ReadString(source, "nextChunkId"),
            SameSectionChunkId = ReadString(source, "sameSectionChunkId"),
            OriginalChunkType = ReadString(source, "originalChunkType")
        };
    }

    private static SourceBackedConversationTurnMemory NormalizeConversationTurn(
        SourceBackedConversationTurnMemory turn)
        => turn with
        {
            SchemaVersion = Math.Max(1, turn.SchemaVersion),
            TurnId = turn.TurnId.Trim(),
            TaskKind = turn.TaskKind?.Trim() ?? string.Empty,
            UserQuestion = turn.UserQuestion?.Trim() ?? string.Empty,
            Outcome = turn.Outcome?.Trim() ?? string.Empty,
            ExecutedActions = turn.ExecutedActions ?? Array.Empty<SourceBackedConversationActionMemory>(),
            EvidenceDecisions = turn.EvidenceDecisions ?? Array.Empty<SourceBackedConversationEvidenceMemory>(),
            UsedItems = turn.UsedItems ?? Array.Empty<SourceBackedConversationUsedItemMemory>(),
            SemanticReviewNotes = turn.SemanticReviewNotes ?? Array.Empty<string>()
        };

    private static bool IsAssistantRole(string? role)
        => string.Equals(role?.Trim(), "assistant", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserRole(string? role)
        => string.Equals(role?.Trim(), "user", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement property)
    {
        property = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        if (element.TryGetProperty(propertyName, out property))
            return true;

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(
                    candidate.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        return false;
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => TryGetPropertyIgnoreCase(element, propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String
               && int.TryParse(
                   property.GetString(),
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out number)
            ? number
            : null;
    }
}
