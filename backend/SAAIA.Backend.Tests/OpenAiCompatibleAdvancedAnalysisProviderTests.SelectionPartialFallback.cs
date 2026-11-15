using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("unsupported_middle", 3)]
    [InlineData("duplicate", 3)]
    [InlineData("locator_only", 0)]
    [InlineData("none_supported", 0)]
    public async Task Final_selection_fallback_keeps_only_supported_choices_at_original_coordinates(
        string defect, int retainedCount)
    {
        var names = new[] { "METHOD ALPHA", "METHOD BETA", "METHOD GAMMA", "METHOD DELTA" };
        var selected = names.ToArray();
        selected[1] = defect == "duplicate" ? names[0] : "UNOBSERVED PROCEDURE";
        if (defect == "none_supported")
            selected = Enumerable.Range(1, 4).Select(i => $"UNOBSERVED PROCEDURE {i}").ToArray();
        var answer = JsonSerializer.Serialize(new
        {
            outcome = "answered",
            answerText = "|Session|A|B|C|D|\n|---|---|---|---|---|\n|S1|"
                + string.Join("|", selected.Select((name, i) => $"{name} [C{i + 1}]")) + "|",
            claims = selected.Select((name, i) => new
            {
                claimId = $"C{i + 1}", selectedItem = name,
                text = "Vérifier le joint et enregistrer le résultat.",
                evidenceIds = new[] { defect == "duplicate" && i == 1 ? "E1" : $"E{i + 1}" }
            })
        });
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"inspection methods","topK":20}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), Completion(answer));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.CandidateBindingFeedbackEnabled = false;
        options.ExternalMaximumCallsPerJob = 3;
        var request = new AdvancedAnalysisProviderRequest(Guid.NewGuid(), Guid.NewGuid(), "test-user",
            new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow, Language = "fr",
                RequestText = "Propose quatre méthodes d'inspection distinctes et sourcées.",
                OriginIntent = "structured_answer", ReasonCode = "advanced_capacity_required", TransferStage = "pre_retrieval",
                Load = new AdvancedAnalysisLoadDescriptor
                {
                    PlanKind = "bounded_grid", Deliverable = "inspection methods", StructuredLayout = true,
                    AnswerUnitCount = 4, AtomicEvidenceCount = 4, AtomicEvidenceType = "documented_method",
                    RowCount = 1, ColumnCount = 4, RowLabels = ["S1"], Columns = ["A", "B", "C", "D"],
                    AtomicEvidenceMode = "named_item", SelectionPolicy = "distinct_structured_layout"
                },
                ResearchState = new AdvancedAnalysisResearchState { EvidenceRevalidationRequired = true, MemoryIsEvidence = false }
            }, [], []);
        var evidence = names.Select((name, i) => BuildEvidence($"E{i + 1}", name
            + (defect == "locator_only" && i == 0 ? "" : "\nVérifier le joint puis enregistrer le résultat."))).ToArray();
        var execution = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, new RecordingToolGateway(evidence), CancellationToken.None);
        if (defect == "locator_only")
        {
            // The earlier substantive-content guard must still reject a title
            // alone, even without a candidateTitle annotation. It never reaches
            // the fallback for an otherwise parsed complete proposal.
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => execution);
            Assert.Equal("advanced_synthesis_candidate_body_not_supported", error.ErrorCode);
            return;
        }
        var result = await execution;

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(retainedCount, result.Claims.Count);
        Assert.Equal(3, factory.Requests.Count);
        if (retainedCount == 0)
            return;
        Assert.Contains("S1 / C : METHOD GAMMA [C3]", result.AnswerText);
        Assert.Contains("S1 / D : METHOD DELTA [C4]", result.AnswerText);
        Assert.DoesNotContain("[C2]", result.AnswerText);
        Assert.DoesNotContain("UNOBSERVED", result.AnswerText);
        Assert.DoesNotContain(result.Claims, c => c.ClaimId == "C2");
        Assert.Equal("E3", Assert.Single(result.Claims.Single(c => c.ClaimId == "C3").EvidenceIds));
        Assert.All(result.Claims, c => Assert.Equal("Vérifier le joint et enregistrer le résultat.", c.Text));
    }
}
