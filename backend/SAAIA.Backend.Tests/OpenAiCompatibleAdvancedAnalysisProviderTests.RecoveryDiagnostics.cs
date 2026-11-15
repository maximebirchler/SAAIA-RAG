using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("repaired")]
    [InlineData("partial")]
    [InlineData("unrepaired")]
    [InlineData("call_limit")]
    public async Task Recovery_identifies_interrupted_name_and_preserves_repaired_or_partial_findings(string recoveryMode)
    {
        var partial = recoveryMode != "repaired";
        const string interrupted = "INSPECTION\n25min\n1,5€\n4\nDE LA VANNE PRINCIPALE";
        const string combined = "INSPECTION DE LA VANNE PRINCIPALE";
        var names = new[] { "METHOD ALPHA", "METHOD BETA", "METHOD GAMMA", combined };
        string Answer(string[] items, string outcome) => JsonSerializer.Serialize(new
        {
            outcome,
            answerText = outcome == "answered"
                ? "|Session|A|B|C|D|\n|---|---|---|---|---|\n|S1|" + string.Join("|", items.Select((name, i) => $"{name} [C{i + 1}]")) + "|"
                : string.Join(". ", items.Select((name, i) => $"{name} [C{i + 1}]")) + ". La quatrième identité reste à vérifier.",
            claims = items.Select((name, i) => new
            {
                claimId = $"C{i + 1}", selectedItem = name,
                text = "La méthode dispose d'une procédure d'inspection documentée.", evidenceIds = new[] { $"E{i + 1}" }
            })
        });
        var prior = Answer(names, "answered");
        var repaired = recoveryMode is "unrepaired" or "call_limit" ? prior
            : Answer(partial ? names[..3] : [.. names[..3], "METHOD DELTA"],
                partial ? "insufficient_documentation" : "answered");
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"inspection methods","topK":20}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), Completion(prior), Completion(repaired));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.CandidateBindingFeedbackEnabled = false;
        if (recoveryMode == "call_limit")
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
        var evidence = names.Select((name, i) => BuildEvidence($"E{i + 1}",
            (i == 3 ? interrupted : name) + "\nVérifier le joint, mesurer le jeu puis enregistrer le résultat."
                + (i == 3 ? "\nMETHOD DELTA\nInspecter la fixation et noter sa position." : ""))).ToArray();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(request, new RecordingToolGateway(evidence), CancellationToken.None);

        Assert.Equal(partial ? "insufficient_documentation" : "answered", result.Outcome);
        Assert.Equal(partial ? 3 : 4, result.Claims.Count);
        Assert.Equal(names[..3], result.Claims.Take(3).Select(c => c.SelectedItem));
        Assert.Equal(recoveryMode == "call_limit" ? 3 : 4, factory.Requests.Count);
        if (recoveryMode is "unrepaired" or "call_limit")
        {
            Assert.StartsWith("Réponse partielle", result.AnswerText);
            Assert.Contains("sources : 3", result.AnswerText);
            Assert.Contains("Il reste 1 choix", result.AnswerText);
            Assert.Contains("S1 / C : METHOD GAMMA [C3]", result.AnswerText);
            Assert.DoesNotContain("[C4]", result.AnswerText);
            Assert.DoesNotContain(combined, result.AnswerText);
            Assert.Equal(new[] { "E1", "E2", "E3" }, result.Claims.SelectMany(c => c.EvidenceIds));
        }
        if (recoveryMode == "call_limit")
            return;
        var prompt = WorkspaceUser(factory.Requests[3].Body);
        Assert.Equal("answered", prompt.GetProperty("priorCandidate").GetProperty("outcome").GetString());
        var diagnostic = prompt.GetProperty("selectionValidation");
        Assert.False(diagnostic.GetProperty("isValid").GetBoolean());
        Assert.False(diagnostic.GetProperty("documentaryEvidence").GetBoolean());
        Assert.False(diagnostic.GetProperty("suitabilityVerified").GetBoolean());
        Assert.Equal(1, diagnostic.GetProperty("unsupportedIdentityCount").GetInt32());
        Assert.Equal("C4", Assert.Single(diagnostic.GetProperty("unsupportedIdentityClaimIds").EnumerateArray()).GetString());
        Assert.Equal(combined, Assert.Single(diagnostic.GetProperty("unsupportedIdentities").EnumerateArray()).GetString());
        Assert.Empty(diagnostic.GetProperty("missingIdentityClaimIds").EnumerateArray());
        Assert.Empty(diagnostic.GetProperty("duplicateIdentityClaimIds").EnumerateArray());
        Assert.Equal(4, prompt.GetProperty("selectionProgress").GetProperty("distinctNamedItemCount").GetInt32());
        Assert.Contains(interrupted, Assert.Single(prompt.GetProperty("evidence").EnumerateArray(),
            e => e.GetProperty("evidenceId").GetString() == "E4").GetProperty("content").GetString());
    }
}
