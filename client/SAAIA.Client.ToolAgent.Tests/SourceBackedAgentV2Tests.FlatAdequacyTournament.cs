using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{

    [Fact]
    public async Task FlatAdequacyTournament_ShowsNewCandidatesAlongsidePriorLlmSelectionAndFeedback()
    {
        var cards = Enumerable.Range(1, 14)
            .Select(static index => new MealCard(
                $"Documented requirement {index}",
                index))
            .ToArray();
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(cards),
            "Summarize seven documented requirements.");
        var evidenceIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var incumbentIds = evidenceIds.Take(7).ToArray();
        var reviewedIds = evidenceIds.Take(12).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var llm = new ScriptedAgentLlm(Completion(Call(
            "challenger-selection",
            "submit_flat_evidence_selection",
            new
            {
                evidenceIds = new[]
                {
                    evidenceIds[5], evidenceIds[6],
                    evidenceIds[12], evidenceIds[13]
                },
                reason = "The new requirements replace weaker prior candidates."
            })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "ReviewFlatEvidenceAdequacyAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(SourceBackedIntake),
                typeof(string),
                typeof(EvidenceBundle),
                typeof(IReadOnlyList<string>),
                typeof(int),
                typeof(string),
                typeof(IReadOnlyList<string>),
                typeof(HashSet<string>),
                typeof(string),
                typeof(CancellationToken)
            },
            modifiers: null);
        Assert.NotNull(method);
        var reviewTask = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            new object?[]
            {
                Intake("Summarize seven documented requirements."),
                "Select the strongest documented requirements.",
                bundle,
                evidenceIds,
                7,
                "content_claim",
                incumbentIds,
                reviewedIds,
                "The previous draft relied on administrative context instead of requirements.",
                CancellationToken.None
            }));

        await reviewTask;

        var request = Assert.Single(llm.Requests);
        var requestText = string.Join(
            "\n",
            request.Select(static message => message.Content));
        Assert.Contains(evidenceIds[12], requestText, StringComparison.Ordinal);
        Assert.Contains(evidenceIds[13], requestText, StringComparison.Ordinal);
        Assert.Contains(
            "administrative context",
            requestText,
            StringComparison.OrdinalIgnoreCase);
        Assert.All(incumbentIds, id =>
            Assert.Contains(id, requestText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlatAdequacyTournament_GapReviewsUnseenRetrievedCandidatesBeforeMoreResearch()
    {
        var cards = Enumerable.Range(1, 14)
            .Select(static index => new MealCard(
                $"Documented requirement {index}",
                index))
            .ToArray();
        var bundle = EvidenceBundleBuilder.FromToolResults(
            ContentCardInventoryResult(cards),
            "Summarize seven documented requirements.");
        var evidenceIds = bundle.Items
            .Select(static item => item.EvidenceId)
            .ToArray();
        var reviewedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var llm = new ScriptedAgentLlm(
            Completion(Call(
                "partial-pool-gap",
                "submit_flat_evidence_gap",
                new
                {
                    usefulEvidenceIds = new[] { evidenceIds[0], evidenceIds[1] },
                    missingRequirements = new[] { "Five additional requirements" },
                    reason = "Two requirements are useful but the pool is incomplete."
                })),
            Completion(Call(
                "partial-pool-challengers",
                "submit_flat_evidence_selection",
                new
                {
                    evidenceIds = new[]
                    {
                        evidenceIds[0], evidenceIds[1],
                        evidenceIds[12], evidenceIds[13]
                    },
                    reason = "The retained pool and new challengers are the strongest evidence."
                })));
        var runner = new SourceBackedAgentV2Runner(
            llm,
            new ScriptedToolExecutor(),
            Options());
        var method = typeof(SourceBackedAgentV2Runner).GetMethod(
            "ReviewFlatEvidenceAdequacyAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(SourceBackedIntake), typeof(string), typeof(EvidenceBundle),
                typeof(IReadOnlyList<string>), typeof(int), typeof(string),
                typeof(IReadOnlyList<string>), typeof(HashSet<string>),
                typeof(string), typeof(CancellationToken)
            },
            modifiers: null);
        Assert.NotNull(method);
        var arguments = new object?[]
        {
            Intake("Summarize seven documented requirements."),
            "Select the strongest documented requirements.",
            bundle,
            evidenceIds,
            7,
            "content_claim",
            Array.Empty<string>(),
            reviewedIds,
            "The current proof pool is incomplete.",
            CancellationToken.None
        };
        var review = Assert.IsAssignableFrom<Task>(method!.Invoke(
            runner,
            arguments));

        await review;

        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(14, reviewedIds.Count);
        var secondRequestText = string.Join(
            "\n",
            llm.Requests[1].Select(static message => message.Content));
        Assert.Contains(evidenceIds[12], secondRequestText, StringComparison.Ordinal);
        Assert.Contains(evidenceIds[13], secondRequestText, StringComparison.Ordinal);
        Assert.Contains(evidenceIds[0], secondRequestText, StringComparison.Ordinal);
        Assert.Contains(evidenceIds[1], secondRequestText, StringComparison.Ordinal);
        var outcome = review.GetType().GetProperty("Result")!.GetValue(review);
        Assert.Equal(
            "select",
            outcome!.GetType().GetProperty("Decision")!.GetValue(outcome));
    }
}
