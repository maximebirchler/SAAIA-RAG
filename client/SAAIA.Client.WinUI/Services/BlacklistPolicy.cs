using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record BlacklistArtifact(
    string Artifact,
    string CdcAlignment,
    IReadOnlyList<BlacklistRule> Items);

internal sealed record BlacklistRule(
    string RuleId,
    bool Active,
    string? Runtime,
    string? ModelId,
    string? ProfileId,
    string? DriverContains,
    string Reason,
    DateTimeOffset? ExpiresAt = null);

internal sealed record BlacklistMatch(
    string RuleId,
    string Reason);

internal static class BlacklistPolicy
{
    public static async Task<BlacklistMatch?> FindMatchAsync(
        QualifiedProfile profile,
        string? driverVersion,
        string? root = null,
        CancellationToken ct = default)
    {
        var read = await GovernanceArtifactStore.ReadAsync<BlacklistArtifact>(
            GovernanceArtifactStore.BlacklistFile,
            root,
            ct).ConfigureAwait(false);

        if (read.Status == GovernanceArtifactReadStatus.Missing)
            return null;

        if (read.Status != GovernanceArtifactReadStatus.Ok)
        {
            return new BlacklistMatch(
                "blacklist_unreadable",
                $"Blacklist unavailable or invalid: {read.Status}.");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var rule in read.Value!.Items)
        {
            if (!rule.Active)
                continue;

            if (rule.ExpiresAt is { } expiresAt && expiresAt <= now)
                continue;

            if (!Matches(rule.Runtime, profile.Runtime))
                continue;

            if (!Matches(rule.ModelId, profile.ModelId))
                continue;

            if (!Matches(rule.ProfileId, profile.ProfileId))
                continue;

            if (!string.IsNullOrWhiteSpace(rule.DriverContains)
                && (driverVersion ?? string.Empty).IndexOf(rule.DriverContains, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            return new BlacklistMatch(rule.RuleId, rule.Reason);
        }

        return null;
    }

    public static BlacklistArtifact Create(params BlacklistRule[] rules)
        => new(GovernanceArtifactStore.BlacklistFile, "v3.1", rules);

    private static bool Matches(string? pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
            return true;

        return string.Equals(pattern.Trim(), value, StringComparison.OrdinalIgnoreCase);
    }
}
