using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string EvidenceAuditNamedAnswer = """{"outcome":"answered","answerText":"Option alpha [C1].","claims":[{"claimId":"C1","selectedItem":"Option alpha","text":"Option alpha est documentée avec ses paramètres de contrôle.","evidenceIds":["E1"]}]}""";

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Changing_requested_length_beyond_chunk_end_is_not_new_documentary_progress(string protocol)
    {
        var canonical = BuildEvidence("E1", new string('x', 3_000) + "The threshold is 17 bar.");
        const string firstRead = """{"evidenceId":"E1","offset":3000,"length":512}""";
        const string sameRead = """{"evidenceId":"E1","offset":3000,"length":1024}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_evidence", firstRead) : NativeCompletion(("read_evidence", firstRead)),
            protocol == "responses" ? NativeResponseFunction("read_evidence", sameRead) : NativeCompletion(("read_evidence", sameRead)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var options = WorkspaceOptions(protocol);
        options.ExternalMaximumCallsPerJob = 4;
        var gateway = new CheckpointToolGateway(canonical);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        var retained = Assert.Single(gateway.Checkpoint!.EvidenceReadWindows!).Value;
        Assert.Equal(512, retained.Length);
        Assert.Null(retained.PreviousWindows);
        var finalPayload = WorkspaceUser(factory.Requests[^1].Body);
        Assert.Equal("search_already_executed", finalPayload.GetProperty("researchFeedback")
            .GetProperty("reasonCode").GetString());
        var visible = Assert.Single(finalPayload.GetProperty("evidence").EnumerateArray());
        Assert.Equal("The threshold is 17 bar.", visible.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Evidence_read_preserves_bounded_independent_windows_and_restores_them_without_new_reads()
    {
        var content = "The threshold is 17 bar.\n" + string.Concat(Enumerable.Range(0, 6)
            .Select(index => new string((char)('a' + index), 3_000)));
        var canonical = BuildEvidence("E1", content);
        var gateway = new CheckpointToolGateway(canonical);
        var replies = new List<HttpResponseMessage>
        {
            Completion("""{"queries":[{"query":"overview","topK":12}]}""")
        };
        foreach (var offset in new[] { 3_000, 6_000, 9_000, 12_000, 15_000 })
            replies.Add(NativeCompletion(("read_evidence", $$"""{"evidenceId":"E1","offset":{{offset}},"length":512}""")));
        replies.Add(Completion(NativeAnswered));
        var options = WorkspaceOptions();
        options.ExternalMaximumCallsPerJob = 7;
        using (var factory = new QueuedHttpClientFactory(replies.ToArray()))
        {
            await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
            var visible = Assert.Single(WorkspaceUser(factory.Requests[^1].Body).GetProperty("evidence").EnumerateArray());
            Assert.Equal(15_000, visible.GetProperty("contentOffset").GetInt32());
            var supporting = visible.GetProperty("supportingContentWindows").EnumerateArray().ToArray();
            Assert.Equal(new[] { 0, 6_000, 9_000, 12_000 },
                supporting.Select(item => item.GetProperty("contentOffset").GetInt32()).ToArray());
            foreach (var window in supporting)
            {
                var projected = window.GetProperty("content").GetString()!;
                Assert.Equal(content.Substring(window.GetProperty("contentOffset").GetInt32(), projected.Length), projected);
            }
        }

        using var resumed = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        await new OpenAiCompatibleAdvancedAnalysisProvider(resumed, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        var restored = Assert.Single(WorkspaceUser(resumed.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        Assert.Equal(15_000, restored.GetProperty("contentOffset").GetInt32());
        Assert.Equal(new[] { 0, 6_000, 9_000, 12_000 }, restored.GetProperty("supportingContentWindows")
            .EnumerateArray().Select(item => item.GetProperty("contentOffset").GetInt32()).ToArray());
        Assert.Same(canonical, Assert.Single(gateway.Evidence));
    }

    [Fact]
    public async Task Disjoint_evidence_windows_do_not_invent_a_contiguous_selected_identity()
    {
        const string supported = "Option gamma is controlled at 18 bar.\nOption delta is controlled at 19 bar.\nOption epsilon is controlled at 20 bar.\n";
        var canonical = BuildEvidence("E1", supported + new string('x', 2_394 - supported.Length) + " alpha" + new string('z', 600)
            + "beta module sets pressure to 17 bar and holds it for three minutes.");
        const string fabricated = """{"outcome":"answered","answerText":"alpha beta [C1], Option gamma [C2], Option delta [C3], Option epsilon [C4].","claims":[{"claimId":"C1","selectedItem":"alpha beta","text":"alpha beta est documentée.","evidenceIds":["E1"]},{"claimId":"C2","selectedItem":"Option gamma","text":"Option gamma est documentée.","evidenceIds":["E1"]},{"claimId":"C3","selectedItem":"Option delta","text":"Option delta est documentée.","evidenceIds":["E1"]},{"claimId":"C4","selectedItem":"Option epsilon","text":"Option epsilon est documentée.","evidenceIds":["E1"]}]}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("read_evidence", """{"evidenceId":"E1","offset":3000,"length":512}""")),
            Completion(fabricated));
        var options = WorkspaceOptions();
        options.CandidateBindingFeedbackEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildRequest(answerUnitCount: 4, atomicEvidenceMode: "named_item"),
                    new RecordingToolGateway(canonical), CancellationToken.None));

        Assert.Equal("advanced_synthesis_candidate_identity_not_supported", error.ErrorCode);
        Assert.DoesNotContain("alpha beta", canonical.Content);
        var visible = Assert.Single(WorkspaceUser(factory.Requests[^1].Body).GetProperty("evidence").EnumerateArray());
        Assert.StartsWith("beta", visible.GetProperty("content").GetString());
        Assert.EndsWith("alpha", visible.GetProperty("supportingContentWindows")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_named_selection_cannot_publish_an_identity_invented_between_disjoint_windows(bool bindingFeedback)
    {
        var canonical = BuildEvidence("E1", new string('x', 2_394) + " alpha" + new string('z', 600)
            + "beta module sets pressure to 17 bar and holds it for three minutes.");
        var fabricated = EvidenceAuditNamedAnswer.Replace("Option alpha", "alpha beta");
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("read_evidence", """{"evidenceId":"E1","offset":3000,"length":512}""")),
            Completion(fabricated));
        var options = WorkspaceOptions();
        options.CandidateBindingFeedbackEnabled = bindingFeedback;
        options.ExternalMaximumCallsPerJob = 3;

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildRequest(atomicEvidenceMode: "named_item"),
                    new RecordingToolGateway(canonical), CancellationToken.None));

        Assert.Equal("advanced_synthesis_candidate_identity_not_supported", error.ErrorCode);
        Assert.DoesNotContain("alpha beta", canonical.Content);
        Assert.Equal(3, factory.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_named_selection_accepts_actual_identity_without_requiring_an_optional_name(bool provideName)
    {
        var canonical = BuildEvidence("E1", "beta module\nSet the pressure to 17 bar and hold for three minutes.",
            exactTitle: "Observed procedure");
        var answer = provideName ? EvidenceAuditNamedAnswer.Replace("Option alpha", "beta module") : NativeAnswered;
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(answer));
        var options = CreateOptions();
        options.CandidateBindingFeedbackEnabled = true;
        options.ExternalMaximumCallsPerJob = 2;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(atomicEvidenceMode: "named_item"),
                new RecordingToolGateway(canonical), CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(provideName ? "beta module" : null, Assert.Single(result.Claims).SelectedItem);
        Assert.Equal(2, factory.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hidden_canonical_body_cannot_validate_a_current_heading_only_view(bool reserveCritic)
    {
        var canonical = BuildEvidence("E1", "Option alpha" + new string(' ', 3_000)
            + "\nSet the pressure to 17 bar and hold for three minutes.", exactTitle: "Option alpha");
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[]}"""), Completion(EvidenceAuditNamedAnswer));
        var options = CreateOptions();
        options.SemanticCriticEnabled = reserveCritic;
        options.ExternalMaximumCallsPerJob = reserveCritic ? 3 : 2;

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildRequest(atomicEvidenceMode: "named_item"),
                    new RecordingToolGateway(canonical), CancellationToken.None));

        Assert.Equal("advanced_synthesis_candidate_body_not_supported", error.ErrorCode);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Contains("17 bar", canonical.Content);
        var visible = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        Assert.Equal("Option alpha", visible.GetProperty("content").GetString()!.Trim());
        Assert.DoesNotContain("17 bar", visible.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Model_can_repair_heading_only_view_by_reading_its_actual_canonical_body(string protocol)
    {
        var canonical = BuildEvidence("E1", "Option alpha" + new string(' ', 3_000)
            + "\nSet the pressure to 17 bar and hold for three minutes.", exactTitle: "Option alpha");
        const string read = """{"evidenceId":"E1","offset":0,"length":4000}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseAnswer(EvidenceAuditNamedAnswer) : Completion(EvidenceAuditNamedAnswer),
            protocol == "responses" ? NativeResponseFunction("read_evidence", read) : NativeCompletion(("read_evidence", read)),
            protocol == "responses" ? NativeResponseAnswer(EvidenceAuditNamedAnswer) : Completion(EvidenceAuditNamedAnswer));
        var options = WorkspaceOptions(protocol);
        options.ExternalMaximumCallsPerJob = 4;
        var gateway = new RecordingToolGateway(canonical);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(atomicEvidenceMode: "named_item"), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Single(gateway.Searches);
        var correction = Assert.Single(WorkspaceUser(factory.Requests[2].Body)
            .GetProperty("candidateSupportCorrections").GetProperty("corrections").EnumerateArray());
        Assert.Equal("candidate_body_required", correction.GetProperty("reasonCode").GetString());
        var finalView = Assert.Single(WorkspaceUser(factory.Requests[3].Body).GetProperty("evidence").EnumerateArray());
        Assert.Contains("17 bar", finalView.GetProperty("content").GetString());
        Assert.Equal("E1", finalView.GetProperty("evidenceId").GetString());
        Assert.Same(canonical, Assert.Single(gateway.Evidence));
    }

    [Fact]
    public async Task Source_scoped_search_preserves_the_observed_revision_identity()
    {
        var canonical = BuildEvidence("E1", "The threshold is 17 bar.");
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeCompletion(("search_corpus", """{"query":"threshold","sourceKey":"internal-source-1","category":"","documentHint":"","topK":20}""")),
            Completion(NativeAnswered));
        var gateway = new RecordingToolGateway(canonical);

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal(canonical.Reference.DocId, gateway.Searches[1].DocId);
        Assert.Equal(canonical.Reference.RevisionId, gateway.Searches[1].RevisionId);
    }
}
