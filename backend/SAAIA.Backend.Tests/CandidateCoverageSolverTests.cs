using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class CandidateCoverageSolverTests
{
    [Fact]
    public void Solve_finds_a_complete_distinct_assignment_through_reallocation()
    {
        var slots = new[]
        {
            new CandidateCoverageSlot("C1", "breakfast"),
            new CandidateCoverageSlot("C2", "lunch")
        };
        var candidates = new[]
        {
            new CandidateCoverageOption("flexible", ["breakfast", "lunch"]),
            new CandidateCoverageOption("breakfast-only", ["breakfast"])
        };

        var solution = CandidateCoverageSolver.Solve(slots, candidates);

        Assert.True(solution.Complete);
        Assert.Equal(2, solution.MaximumAssignableCount);
        Assert.Empty(solution.MissingByRole);
        Assert.Equal("breakfast-only", solution.Assignments[0].CandidateKey);
        Assert.Equal("flexible", solution.Assignments[1].CandidateKey);
    }

    [Fact]
    public void Solve_rejects_overlapping_role_counts_that_cannot_fill_distinct_slots()
    {
        var roles = new[] { "breakfast", "lunch", "snack", "dinner" };
        var slots = Enumerable.Range(0, 5)
            .SelectMany(row => roles.Select((role, column) =>
                new CandidateCoverageSlot($"C{row * roles.Length + column + 1}", role)))
            .ToArray();
        var candidates = Enumerable.Range(1, 15)
            .Select(index => new CandidateCoverageOption(
                $"breakfast-{index:D2}",
                ["breakfast"]))
            .Concat(Enumerable.Range(1, 5).Select(index =>
                new CandidateCoverageOption($"shared-{index:D2}", roles)))
            .ToArray();

        var solution = CandidateCoverageSolver.Solve(slots, candidates);

        Assert.False(solution.Complete);
        Assert.Equal(20, solution.RequiredSlotCount);
        Assert.Equal(10, solution.MaximumAssignableCount);
        Assert.Equal(10, solution.MissingByRole.Values.Sum());
        Assert.Equal(
            solution.Assignments.Count,
            solution.Assignments.Select(assignment => assignment.CandidateKey)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void Solve_is_deterministic_when_candidate_input_order_changes()
    {
        var slots = new[]
        {
            new CandidateCoverageSlot("C1", "breakfast"),
            new CandidateCoverageSlot("C2", "lunch"),
            new CandidateCoverageSlot("C3", "dinner")
        };
        var candidates = new[]
        {
            new CandidateCoverageOption("c", ["breakfast", "dinner"]),
            new CandidateCoverageOption("a", ["breakfast", "lunch"]),
            new CandidateCoverageOption("b", ["lunch", "dinner"])
        };

        var forward = CandidateCoverageSolver.Solve(slots, candidates);
        var reverse = CandidateCoverageSolver.Solve(slots, candidates.Reverse().ToArray());

        Assert.Equal(forward.Assignments, reverse.Assignments);
        Assert.Equal(forward.MissingByRole, reverse.MissingByRole);
    }
}
