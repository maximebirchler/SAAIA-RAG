using System.Reflection;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

internal sealed class QueueLlmClient : ILlmClient
{
    private readonly Queue<string> _responses;
    private Queue<bool>? _legacyYieldItemDecisions;

    public QueueLlmClient(params string[] responses)
    {
        _responses = new Queue<string>(responses);
    }

    public List<string> Calls { get; } = new();

    public bool SupportsStructuredOutput { get; init; }

    public List<LlmStructuredOutputContract> StructuredOutputContracts { get; } = new();

    public Task<string> CompleteStructuredAsync(
        IReadOnlyList<(string role, string content)> messages,
        LlmStructuredOutputContract contract,
        CancellationToken ct)
    {
        StructuredOutputContracts.Add(contract);
        return CompleteAsync(messages, forceJson: true, ct);
    }

    public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
    {
        var prompt = string.Join('\n', messages.Select(message => message.content));
        if (prompt.Contains("SAAIA_SOURCE_BACKED_STEP=PlannerAcceptedQueryYieldItemReview", StringComparison.Ordinal))
        {
            if (_legacyYieldItemDecisions is { Count: > 0 })
            {
                var storedDecision = _legacyYieldItemDecisions.Dequeue();
                if (_legacyYieldItemDecisions.Count == 0)
                    _legacyYieldItemDecisions = null;
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    containsItemIdentifierAndDetails = storedDecision
                }));
            }

            if (_responses.Count > 0
                && TryReadLegacyYieldVerdict(_responses.Peek(), out var insufficientRequestIndexes))
            {
                Calls.Add(prompt);
                _responses.Dequeue();
                _legacyYieldItemDecisions = new Queue<bool>(
                    Enumerable.Range(1, 4)
                        .Select(index => !insufficientRequestIndexes.Contains(index)));
                var firstDecision = _legacyYieldItemDecisions.Dequeue();
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    containsItemIdentifierAndDetails = firstDecision
                }));
            }
        }

        if (prompt.Contains("SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisCompletenessReview", StringComparison.Ordinal)
            && (_responses.Count == 0
                || !_responses.Peek().Contains("\"count\"", StringComparison.OrdinalIgnoreCase)))
        {
            const string countMarker = "SAAIA_SOURCE_BACKED_CURRENT_COLUMN_COUNT=";
            var markerIndex = prompt.IndexOf(countMarker, StringComparison.Ordinal);
            var count = markerIndex >= 0
                        && int.TryParse(
                            prompt[(markerIndex + countMarker.Length)..]
                                .Split('\n', 2)[0]
                                .Trim(),
                            out var parsedCount)
                ? parsedCount
                : 2;
            return Task.FromResult(JsonSerializer.Serialize(new { count }));
        }

        if (prompt.Contains("SAAIA_SOURCE_BACKED_STEP=PlannerIntakeColumnAxisMissingAnchors", StringComparison.Ordinal)
            && (_responses.Count == 0
                || !_responses.Peek().Contains("\"missing\"", StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult("{\"missing\":[]}");
        }

        if (prompt.Contains("SAAIA_SOURCE_BACKED_STEP=StructuredValueTypeFitJudge", StringComparison.Ordinal))
        {
            if (_responses.Count > 0
                && _responses.Peek().Contains("\"checks\"", StringComparison.OrdinalIgnoreCase))
            {
                Calls.Add(prompt);
                return Task.FromResult(_responses.Dequeue());
            }

            const string marker = "EXPECTED_CANDIDATE_IDS:";
            var markerIndex = prompt.LastIndexOf(marker, StringComparison.Ordinal);
            var ids = markerIndex < 0
                ? Array.Empty<string>()
                : prompt[(markerIndex + marker.Length)..]
                    .Split('\n', 2)[0]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                checks = ids.Select(candidateId => new
                {
                    candidateId,
                    decision = "complete_instance",
                    reason = "Default focused type-fit opinion for an unrelated queue-based test fixture."
                })
            }));
        }

        if (prompt.Contains(
                "SAAIA_SOURCE_BACKED_STEP=PlannerStructuredCellValueModeReview",
                StringComparison.Ordinal)
            && (_responses.Count == 0
                || !_responses.Peek().Contains("\"structuredCellValueMode\"", StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult("{\"structuredCellValueMode\":\"composed_claim\"}");
        }

        if (prompt.Contains(
                "SAAIA_SOURCE_BACKED_STEP=CanonicalColumnSemanticRoles",
                StringComparison.Ordinal)
            && (_responses.Count == 0
                || !_responses.Peek().Contains("\"roles\"", StringComparison.OrdinalIgnoreCase)))
        {
            var columns = prompt.Split('\n')
                .FirstOrDefault(static line => line.StartsWith("EXACT_COLUMN_KEYS:", StringComparison.Ordinal))
                ?.Split(':', 2)[1]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? Array.Empty<string>();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                roles = columns.ToDictionary(
                    static column => column,
                    static column => "Neutral placement slot for " + column + ".")
            }));
        }

        if (prompt.Contains(
                "SAAIA_SOURCE_BACKED_STEP=CanonicalDiversityPolicy",
                StringComparison.Ordinal)
            && (_responses.Count == 0
                || !_responses.Peek().Contains(
                    "\"minimumDistinctValuesPerColumn\"",
                    StringComparison.OrdinalIgnoreCase)))
        {
            var lines = prompt.Split('\n');
            var columns = lines
                .FirstOrDefault(static line => line.StartsWith(
                    "COLUMN_LABELS:",
                    StringComparison.Ordinal))
                ?.Split(':', 2)[1]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? Array.Empty<string>();
            var rowCount = int.TryParse(
                lines.FirstOrDefault(static line => line.StartsWith(
                        "ROW_COUNT:",
                        StringComparison.Ordinal))
                    ?.Split(':', 2)[1]
                    .Trim(),
                out var parsedRowCount)
                ? parsedRowCount
                : 1;
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                minimumDistinctValuesPerColumn = columns.ToDictionary(
                    static column => column,
                    _ => rowCount),
                minimumDistinctValuesOverall = Math.Max(1, rowCount * columns.Length),
                reason = "Default full-grid diversity policy for an unrelated queue-based test fixture."
            }));
        }

        if (prompt.Contains(
                "SAAIA_SOURCE_BACKED_STEP=CanonicalCardShortlist",
                StringComparison.Ordinal)
            && (_responses.Count == 0
                || !_responses.Peek().Contains("\"selectedValueRefs\"", StringComparison.OrdinalIgnoreCase)))
        {
            var valueRefs = prompt.Split('\n')
                .Where(static line => line.StartsWith("VALUE_REF:", StringComparison.Ordinal))
                .Select(static line => line.Split('|', 2)[0].Split(':', 2)[1].Trim())
                .Take(SourceBackedCanonicalContentCardInventory.ShortlistBatchSelectionLimit)
                .ToArray();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                selectedValueRefs = valueRefs
            }));
        }

        if (prompt.Contains(
                "SAAIA_SOURCE_BACKED_STEP=CanonicalCardColumnCompatibility",
                StringComparison.Ordinal)
            && (_responses.Count == 0
                || !_responses.Peek().Contains("\"assignments\"", StringComparison.OrdinalIgnoreCase)))
        {
            var lines = prompt.Split('\n');
            var columns = lines
                .FirstOrDefault(static line => line.StartsWith("COLUMN_LABELS:", StringComparison.Ordinal))
                ?.Split(':', 2)[1]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? Array.Empty<string>();
            var valueRefs = lines
                .Where(static line => line.StartsWith("VALUE_REF:", StringComparison.Ordinal))
                .Select(static line => line.Split('|', 2)[0].Split(':', 2)[1].Trim())
                .ToArray();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                assignments = valueRefs.ToDictionary(
                    static valueRef => valueRef,
                    _ => columns)
            }));
        }

        Calls.Add(prompt);
        if (_responses.Count > 0)
            return Task.FromResult(_responses.Dequeue());

        throw new InvalidOperationException("Queue empty.");
    }

    private static bool TryReadLegacyYieldVerdict(
        string raw,
        out HashSet<int> insufficientRequestIndexes)
    {
        insufficientRequestIndexes = new HashSet<int>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("insufficientRequestIndexes", out var indexes)
                || indexes.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in indexes.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var index))
                    return false;
                insufficientRequestIndexes.Add(index);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
        => throw new NotSupportedException();
}

internal sealed class HangingLlmClient : ILlmClient
{
    public List<string> Calls { get; } = new();

    public Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
    {
        Calls.Add(string.Join('\n', messages.Select(message => message.content)));
        return new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }

    public Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
        => throw new NotSupportedException();
}

internal sealed class StubToolExecutor : ISourceBackedRagToolExecutor
{
    private readonly ToolResults _toolResults;

    public StubToolExecutor(ToolResults toolResults)
    {
        _toolResults = toolResults;
    }

    public RetrievalPlan? ExecutedPlan { get; private set; }

    public Task<ToolResults> ExecuteAsync(SourceBackedIntake intake, RetrievalPlan plan, CancellationToken ct)
    {
        ExecutedPlan = plan;
        return Task.FromResult(_toolResults);
    }
}

internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(responder(request));
}

internal static class SourceBackedRagTestDoubles
{
    public static ApiClient CreateApiClient(HttpMessageHandler handler)
    {
        var api = new ApiClient();
        api.Configure("http://localhost:5122", "test-api-key", "test-user");

        var field = typeof(ApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(api, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5122")
        });

        return api;
    }
}
