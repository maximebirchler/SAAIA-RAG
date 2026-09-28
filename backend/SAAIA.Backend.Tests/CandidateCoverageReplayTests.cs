using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CandidateCoverageReplayTests
{
    [Fact]
    public void Historical_anonymized_ledgers_preserve_the_measured_assignment_frontier()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "candidate_coverage_replays.v1.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        var root = fixture.RootElement;
        Assert.Equal(
            "saaia.candidate-coverage-replays.v1",
            root.GetProperty("schemaVersion").GetString());
        Assert.False(root.GetProperty("privateContentEmbedded").GetBoolean());
        var roles = root.GetProperty("roles").EnumerateArray()
            .Select(role => role.GetString()!)
            .ToArray();
        var rowCount = root.GetProperty("rowCount").GetInt32();
        var slots = Enumerable.Range(0, rowCount)
            .SelectMany(row => roles.Select((role, column) =>
                new CandidateCoverageSlot($"C{row * roles.Length + column + 1}", role)))
            .ToArray();

        foreach (var replay in root.GetProperty("cases").EnumerateArray())
        {
            var candidates = replay.GetProperty("candidates").EnumerateArray()
                .Select(candidate => new CandidateCoverageOption(
                    candidate.GetProperty("candidateKey").GetString()!,
                    candidate.GetProperty("targetRoles").EnumerateArray()
                        .Select(role => role.GetString()!)
                        .ToArray()))
                .ToArray();

            var solution = CandidateCoverageSolver.Solve(slots, candidates);

            Assert.Equal(
                replay.GetProperty("expectedMaximumAssignableCount").GetInt32(),
                solution.MaximumAssignableCount);
            Assert.Equal(
                replay.GetProperty("expectedComplete").GetBoolean(),
                solution.Complete);
            Assert.Equal(
                slots.Length - solution.MaximumAssignableCount,
                solution.MissingByRole.Values.Sum());
            Assert.Equal(
                solution.Assignments.Count,
                solution.Assignments.Select(assignment => assignment.CandidateKey)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }
    }
}
