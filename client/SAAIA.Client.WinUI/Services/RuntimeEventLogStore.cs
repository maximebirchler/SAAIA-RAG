using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record RuntimeEventLogArtifact(
    string Artifact,
    string CdcAlignment,
    IReadOnlyList<RuntimeEventLogItem> Items);

internal sealed record RuntimeEventLogItem(
    DateTimeOffset At,
    string RuntimeId,
    string EventKind,
    string? Build,
    string? PreviousBuild,
    string? ModelId,
    string? Detail);

internal static class RuntimeEventLogStore
{
    private const int MaxItems = 100;

    internal static string? RootOverride { get; set; }

    public static Task AppendAsync(
        RuntimeEventLogItem item,
        string? root = null,
        CancellationToken ct = default)
        => AppendManyAsync(new[] { item }, root, ct);

    public static void Append(RuntimeEventLogItem item, string? root = null)
        => AppendAsync(item, root ?? RootOverride).GetAwaiter().GetResult();

    public static async Task<IReadOnlyList<RuntimeEventLogItem>> ReadLatestAsync(
        int count = 5,
        string? root = null,
        CancellationToken ct = default)
    {
        var read = await GovernanceArtifactStore.ReadAsync<RuntimeEventLogArtifact>(
            GovernanceArtifactStore.RuntimeEventLogFile,
            root ?? RootOverride,
            ct).ConfigureAwait(false);

        return read.Status == GovernanceArtifactReadStatus.Ok && read.Value is not null
            ? read.Value.Items
                .OrderByDescending(item => item.At)
                .Take(Math.Max(0, count))
                .ToArray()
            : Array.Empty<RuntimeEventLogItem>();
    }

    private static async Task AppendManyAsync(
        IReadOnlyList<RuntimeEventLogItem> itemsToAppend,
        string? root,
        CancellationToken ct)
    {
        var effectiveRoot = root ?? RootOverride;
        await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), effectiveRoot, ct).ConfigureAwait(false);

        var read = await GovernanceArtifactStore.ReadAsync<RuntimeEventLogArtifact>(
            GovernanceArtifactStore.RuntimeEventLogFile,
            effectiveRoot,
            ct).ConfigureAwait(false);

        var items = read.Status == GovernanceArtifactReadStatus.Ok && read.Value is not null
            ? read.Value.Items.ToList()
            : new List<RuntimeEventLogItem>();

        foreach (var item in itemsToAppend)
            items.Insert(0, item);

        await GovernanceArtifactStore.WriteAsync(
            GovernanceArtifactStore.RuntimeEventLogFile,
            new RuntimeEventLogArtifact(
                GovernanceArtifactStore.RuntimeEventLogFile,
                "v3.1",
                items
                    .OrderByDescending(item => item.At)
                    .Take(MaxItems)
                    .ToArray()),
            effectiveRoot,
            ct).ConfigureAwait(false);
    }
}
