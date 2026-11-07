using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", "selected")]
    [InlineData("chat-completions", "selected")]
    [InlineData("responses", "body_verified")]
    [InlineData("chat-completions", "body_verified")]
    public async Task Explicit_inventory_choices_keep_current_proof_after_primary_read_under_budget(string protocol, string status)
    {
        const int cap = 12_000;
        const string doc = "11111111-1111-4111-8111-111111111111";
        const string revision = "22222222-2222-4222-8222-222222222222";
        AdvancedAnalysisResolvedEvidence Body(int i) => BuildEvidence("S" + i,
            $"Capteur {i}\nCe capteur mesure une grandeur physique dans les conditions décrites. "
            + string.Concat(Enumerable.Repeat("La calibration doit respecter la plage et la température spécifiées. ", 15)),
            "sensors.pdf", doc, revision, exactTitle: "Capteur " + i);
        var initial = Enumerable.Range(1, 14).Select(Body).ToArray();
        var fresh = Enumerable.Range(15, 3).Select(Body).ToArray();
        var gateway = new SequencedToolGateway(initial, fresh);
        var selected = new List<JsonElement>();
        var prompts = new List<JsonElement>();
        var ordinal = 0;
        string partial = "";
        using var factory = new OmittedHandleHttpFactory(async http =>
        {
            var wire = await http.Content!.ReadAsStringAsync();
            var prompt = WorkspaceUser(wire); prompts.Add(prompt);
            var turn = ordinal++;
            System.Net.Http.HttpResponseMessage Tool(string name, string args)
                => protocol == "responses" ? NativeCorrelationResponseFunctions([(name, args)]) : NativeCompletion((name, args));
            System.Net.Http.HttpResponseMessage response;
            if (turn == 0)
                response = Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"capteurs de mesure","topK":20}]}""");
            else if (turn is 1 or 2)
            {
                if (turn == 1)
                {
                    selected.AddRange(prompt.GetProperty("evidence").EnumerateArray().SkipLast(1).TakeLast(2).Select(e => e.Clone()));
                    Assert.Equal(2, selected.Count);
                }
                var item = selected[turn - 1];
                var role = prompt.GetProperty("claimCoordinates")[turn - 1].GetProperty("columnLabel").GetString()!;
                response = Tool("save_candidate_inventory", CandidateUpdate("zz-choice-" + turn,
                    item.GetProperty("candidateTitle").GetString()!, item.GetProperty("sourceKey").GetString()!,
                    role, status, [], [item.GetProperty("evidenceId").GetString()!]));
            }
            else if (turn == 3)
                response = Tool("read_source", """{"sourceKey":"internal-source-1","pageStart":1,"pageCount":1,"topK":30}""");
            else
            {
                var visible = prompt.GetProperty("evidence").EnumerateArray().ToArray();
                Assert.True(visible.Length < gateway.Evidence.Count);
                foreach (var chosen in selected)
                {
                    var id = chosen.GetProperty("evidenceId").GetString();
                    var matches = visible.Where(item => item.GetProperty("evidenceId").GetString() == id).ToArray();
                    Assert.True(matches.Length == 1, $"Selected canonical body {id} absent at call {turn}.");
                    var current = matches[0];
                    Assert.Equal(chosen.GetProperty("content").GetString(), current.GetProperty("content").GetString());
                }
                Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, cap);
                partial = JsonSerializer.Serialize(new
                {
                    outcome = "insufficient_documentation", answerText = "Deux choix documentés [C1] [C2], sélection incomplète.",
                    claims = selected.Select((e, i) => new
                    {
                        claimId = "C" + (i + 1), selectedItem = e.GetProperty("candidateTitle").GetString(),
                        text = e.GetProperty("candidateTitle").GetString() + " mesure une grandeur physique.",
                        evidenceIds = new[] { e.GetProperty("evidenceId").GetString() }
                    }).ToArray()
                });
                response = Completion(partial);
            }
            return await AdaptProtocolAgnosticFixtureAsync(http, response, CancellationToken.None);
        });
        var options = EarlyOptions(protocol, 6);
        options.NativeResearchWorkspaceEnabled = true;
        options.SemanticCriticEnabled = true;
        options.MaximumEvidencePromptCharacters = cap;
        var request = new AdvancedAnalysisProviderRequest(Guid.NewGuid(), Guid.NewGuid(), "test-user",
            new AdvancedAnalysisHandoffEnvelope
            {
                HandoffId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow,
                RequestText = "Sélectionne vingt capteurs distincts pour cinq postes et quatre grandeurs mesurées.",
                Language = "fr", OriginIntent = "structured_answer", ReasonCode = "advanced_capacity_required", TransferStage = "pre_retrieval",
                Load = new AdvancedAnalysisLoadDescriptor
                {
                    PlanKind = "bounded_grid", Deliverable = "sélection de capteurs", AnswerUnitCount = 20,
                    AtomicEvidenceCount = 20, StructuredLayout = true, RowCount = 5, ColumnCount = 4,
                    RowLabels = ["poste A", "poste B", "poste C", "poste D", "poste E"],
                    Columns = ["pression", "température", "humidité", "débit"],
                    AtomicEvidenceType = "documented_sensor", AtomicEvidenceMode = "named_item", SelectionPolicy = "distinct_structured_layout"
                },
                ResearchState = new AdvancedAnalysisResearchState { EvidenceRevalidationRequired = true, MemoryIsEvidence = false }
            }, [], []);
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        AdvancedAnalysisProviderResult result;
        try
        {
            result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
                .ExecuteAsync(request, gateway, CancellationToken.None);
        }
        catch (AdvancedAnalysisProviderException error)
        {
            throw new InvalidOperationException($"After {ordinal} calls: {error.ErrorCode}; "
                + JsonSerializer.Serialize(logger.Events.Where(e => e.Values.GetValueOrDefault("Operation")?.ToString()
                    == "llm.research_argument_rejection").Select(e => e.Values)), error);
        }
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(2, result.Claims.Count);
        Assert.Equal(6, result.ProviderCallCount);
        Assert.Equal("read_source", gateway.Searches[1].Operation);
    }

    [Theory]
    [InlineData("retired")]
    [InlineData("navigation")]
    [InlineData("current")]
    public void Selected_inventory_priority_uses_only_current_substantive_objects(string state)
    {
        var first = PrimaryReadBody("FRESH", "New canonical inspection procedure.");
        var second = PrimaryReadBody("FRESH2", "Second canonical inspection procedure.");
        var selected = PrimaryReadBody("CHOSEN", state == "navigation"
            ? "Table of contents\nProcedure Alpha 7\nProcedure Beta 8\nProcedure Gamma 9"
            : "Chosen inspection procedure with documented calibration limits.");
        var read = new AdvancedAnalysisSearchRequest("", PageStart: 7, PageEnd: 7, Operation: "read_source");
        var turn = PrimaryReadProjectionTurn(read, [first, second], first.Reference);
        turn.GetType().GetProperty("SelectedCandidateEvidenceIds")!.SetValue(turn, new[] { "CHOSEN" });
        var active = state == "retired" ? new[] { first, second } : new[] { first, second, selected };
        var result = (IReadOnlyList<AdvancedAnalysisResolvedEvidence>)InvokeSourceCatalogue(null,
            "PrioritizeCurrentPrimaryReadEvidence", active, active, turn, null)!;
        Assert.Same(first, result[0]);
        Assert.Same(state == "current" ? selected : second, result[1]);
        Assert.Equal(active.Length, result.Count);
        Assert.All(result, item => Assert.Contains(active, current => ReferenceEquals(current, item)));
    }
}
