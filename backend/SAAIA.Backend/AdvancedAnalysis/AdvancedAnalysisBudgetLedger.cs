using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

/// <summary>
/// Serializes paid admission across processes. Starts are flushed before HTTP; an
/// unmatched start remains reserved after a crash. Terminal usage stays in the
/// original JSONL ledger so historical consumers do not double-count starts.
/// </summary>
internal sealed class AdvancedAnalysisBudgetLedger(string rawPath)
{
    private readonly string _path = Path.GetFullPath(rawPath);

    internal sealed class JobTotals
    {
        internal int Calls;
        internal decimal Charged;
        internal decimal Reserved;
    }

    internal sealed class Snapshot
    {
        internal decimal Charged;
        internal decimal Reserved;
        internal readonly Dictionary<Guid, JobTotals> Jobs = new();
        internal readonly HashSet<string> OpenRequests = new(StringComparer.Ordinal);
        internal JobTotals Job(Guid jobId)
        {
            if (!Jobs.TryGetValue(jobId, out var job)) Jobs.Add(jobId, job = new());
            return job;
        }
    }

    internal T Locked<T>(Func<Snapshot, T> operation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var elapsed = Stopwatch.StartNew();
        FileStream fileLock;
        while (true)
        {
            try
            {
                fileLock = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(10);
            }
        }
        using (fileLock) return operation(Read());
    }

    internal void AppendStart(AdvancedAnalysisExternalBudgetGuard.Reservation reservation)
        => AppendDurably(_path + ".reservations.jsonl", new
        {
            schema = "advanced-budget-reservation.v1",
            timestampUtc = DateTimeOffset.UtcNow,
            reservation.RequestId,
            reservation.JobId,
            reservation.Role,
            reservation.EstimatedInputTokens,
            reservation.MaximumOutputTokens,
            reservation.ReservedCostUsd
        });

    internal void AppendCharge(object entry) => AppendDurably(_path, entry);

    private Snapshot Read()
    {
        var snapshot = new Snapshot();
        var completed = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var line in Rows(_path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var cost = RequiredNonnegativeCost(root, "costUsd");
                snapshot.Charged += cost;
                Guid? jobId = root.TryGetProperty("jobId", out var rawJob) && rawJob.TryGetGuid(out var job) ? job : null;
                if (jobId.HasValue)
                {
                    var totals = snapshot.Job(jobId.Value);
                    totals.Calls++;
                    totals.Charged += cost;
                }
                // Older ledgers without request ids still count their cost/calls.
                if (root.TryGetProperty("requestId", out var rawRequest) && !string.IsNullOrWhiteSpace(rawRequest.GetString())
                    && !completed.TryAdd(rawRequest.GetString()!, jobId)) throw InvalidLedger();
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
            {
                throw InvalidLedger(error);
            }
        }
        var starts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Rows(_path + ".reservations.jsonl"))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.GetProperty("schema").GetString() != "advanced-budget-reservation.v1") throw InvalidLedger();
                // Start records use the Reservation property casing explicitly.
                var request = root.GetProperty("RequestId").GetString();
                var job = root.GetProperty("JobId").GetGuid();
                var reserved = RequiredNonnegativeCost(root, "ReservedCostUsd");
                if (string.IsNullOrWhiteSpace(request) || !starts.Add(request)) throw InvalidLedger();
                if (completed.TryGetValue(request, out var terminalJob))
                {
                    if (terminalJob != job) throw InvalidLedger();
                    continue;
                }
                snapshot.OpenRequests.Add(request);
                snapshot.Reserved += reserved;
                var totals = snapshot.Job(job);
                totals.Calls++;
                totals.Reserved += reserved;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
            {
                throw InvalidLedger(error);
            }
        }
        return snapshot;
    }

    private static IEnumerable<string> Rows(string path)
        => File.Exists(path) ? File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)) : [];

    private static decimal RequiredNonnegativeCost(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || !property.TryGetDecimal(out var cost) || cost < 0) throw InvalidLedger();
        return cost;
    }

    private static void AppendDurably(string path, object entry)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        stream.Seek(0, SeekOrigin.End);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            var last = stream.ReadByte();
            if (last != '\n') stream.WriteByte((byte)'\n');
        }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static InvalidOperationException InvalidLedger(Exception? inner = null)
        => new("The advanced-analysis usage ledger is invalid and must be reviewed before paid calls resume.", inner);
}
