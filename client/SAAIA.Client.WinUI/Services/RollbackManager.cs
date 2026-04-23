using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record RollbackLogArtifact(
    string Artifact,
    string CdcAlignment,
    IReadOnlyList<RollbackLogItem> Items);

internal sealed record RollbackLogItem(
    DateTimeOffset At,
    string FromProfileId,
    string ToProfileId,
    string Cause);

internal static class RollbackManager
{
    public static async Task<QualifiedProfile?> ReadLastKnownGoodAsync(
        string? root = null,
        CancellationToken ct = default)
    {
        var read = await GovernanceArtifactStore.ReadAsync<LastKnownGoodProfileArtifact>(
            GovernanceArtifactStore.LastKnownGoodProfileFile,
            root,
            ct).ConfigureAwait(false);

        return read.Status == GovernanceArtifactReadStatus.Ok ? read.Value?.Profile : null;
    }

    public static Task SaveLastKnownGoodAsync(
        QualifiedProfile profile,
        string? root = null,
        CancellationToken ct = default)
        => GovernanceArtifactStore.WriteAsync(
            GovernanceArtifactStore.LastKnownGoodProfileFile,
            new LastKnownGoodProfileArtifact(
                GovernanceArtifactStore.LastKnownGoodProfileFile,
                "v3.1",
                profile,
                DateTimeOffset.UtcNow),
            root,
            ct);

    public static async Task AppendRollbackAsync(
        QualifiedProfile from,
        QualifiedProfile to,
        string cause,
        string? root = null,
        CancellationToken ct = default)
    {
        var read = await GovernanceArtifactStore.ReadAsync<RollbackLogArtifact>(
            GovernanceArtifactStore.RollbackLogFile,
            root,
            ct).ConfigureAwait(false);

        var items = read.Status == GovernanceArtifactReadStatus.Ok && read.Value is not null
            ? read.Value.Items.ToList()
            : new List<RollbackLogItem>();

        items.Insert(0, new RollbackLogItem(
            DateTimeOffset.UtcNow,
            from.ProfileId,
            to.ProfileId,
            cause));

        await GovernanceArtifactStore.WriteAsync(
            GovernanceArtifactStore.RollbackLogFile,
            new RollbackLogArtifact(
                GovernanceArtifactStore.RollbackLogFile,
                "v3.1",
                items.Take(100).ToArray()),
            root,
            ct).ConfigureAwait(false);
    }
}
