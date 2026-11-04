using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", 8, true, false)]
    [InlineData("responses", 8, true, false)]
    [InlineData("chat-completions", 20, false, false)]
    [InlineData("responses", 20, false, false)]
    [InlineData("chat-completions", 8, true, true)]
    [InlineData("responses", 8, true, true)]
    public async Task Current_writer_draft_citations_remain_visible_on_critic_wire_after_cache_reads_and_discovery(
        string protocol, int citationCount, bool escaped, bool correctBinding)
    {
        const int cap = 98_304;
        var gateway = new DraftCitationGateway(escaped);
        var bodies = gateway.Bodies.Take(8).ToArray();
        var wires = new List<string>();
        string? proposal = null;
        string[] cited = [];
        using var factory = new OmittedHandleHttpFactory(async request =>
        {
            var wire = await request.Content!.ReadAsStringAsync();
            wires.Add(wire);
            var response = wires.Count switch
            {
                1 => Completion(GuidancePlan(1)),
                2 => EvidencePrefixReply(protocol, [("read_source", """{"sourceKey":"internal-source-1","pageStart":7,"pageCount":1,"topK":20}""")]),
                3 => EvidencePrefixReply(protocol, EvidencePrefixCalls(bodies)),
                4 => EvidencePrefixReply(protocol, [("search_corpus", """{"category":"","query":"independent inspection condition","sourceKey":"","documentHint":"","topK":2}""")]),
                _ => null
            };
            if (response is not null)
                return await AdaptProtocolAgnosticFixtureAsync(request, response, CancellationToken.None);
            var user = WorkspaceUser(wire);
            Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, cap);
            var evidence = user.GetProperty("evidence").EnumerateArray().ToArray();
            if (wires.Count == 5)
            {
                // These are current substantive excerpts, not hidden IDs or body memory.
                cited = evidence.Where(item => item.GetProperty("content").GetString()!.Length > 100)
                    .TakeLast(citationCount).Select(item => item.GetProperty("evidenceId").GetString()!).ToArray();
                Assert.Equal(citationCount, cited.Length);
                proposal = JsonSerializer.Serialize(new
                {
                    outcome = "insufficient_documentation", answerText = "These observed conditions do not establish the complete requested selection.",
                    claims = cited.Select((id, index) => new
                    {
                        claimId = $"C{index + 1}", selectedItem = $"Observed option {index + 1}",
                        text = "An observed documentary condition remains applicable.", evidenceIds = new[] { id }
                    }).ToArray()
                });
            }
            else
            {
                var current = evidence.Select(item => item.GetProperty("evidenceId").GetString()).ToHashSet(StringComparer.Ordinal);
                Assert.True(cited.All(current.Contains), "Current writer citations omitted at next role: " + string.Join(",", cited.Where(id => !current.Contains(id))));
                if (correctBinding && wires.Count == 6)
                    Assert.True(user.TryGetProperty("insufficiencyReview", out _));
                else if (correctBinding && wires.Count == 7)
                {
                    Assert.True(user.TryGetProperty("candidate", out _));
                    var hidden = gateway.Evidence.First(item => !current.Contains(item.Reference.EvidenceId)).Reference.EvidenceId!;
                    var invalid = JsonNode.Parse(proposal!)!.AsObject();
                    invalid["claims"]![0]!["evidenceIds"]!.AsArray().Add(hidden);
                    return await AdaptProtocolAgnosticFixtureAsync(request, EarlyReply(protocol, invalid.ToJsonString()), CancellationToken.None);
                }
                else if (correctBinding)
                {
                    Assert.Equal(8, wires.Count);
                    Assert.Contains(user.GetProperty("candidateSupportCorrections").GetProperty("corrections").EnumerateArray(),
                        item => item.GetProperty("reasonCode").GetString() == "candidate_evidence_not_visible");
                }
                else
                {
                    Assert.Equal(6, wires.Count);
                    Assert.True(user.TryGetProperty("candidate", out _));
                }
            }
            return await AdaptProtocolAgnosticFixtureAsync(request, EarlyReply(protocol, proposal!), CancellationToken.None);
        });
        var options = PrefixOptions(protocol, correctBinding ? 9 : 6);
        options.SemanticCriticEnabled = true;
        options.NativeResearchWorkspaceEnabled = false;
        options.NativeResearchActiveProposalEnabled = false;
        options.MaximumEvidencePromptCharacters = options.NativeResearchMaximumHistoryCharacters = cap;
        var logger = new NativeCorrelationLogger();
        AdvancedAnalysisProviderResult result;
        try
        {
            result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
                .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("HTTP count=" + wires.Count + " " + string.Join(" / ", logger.Entries.Where(entry => entry.Contains("rejection") || entry.Contains("invalid"))), error);
        }
        Assert.Equal(correctBinding ? 8 : 6, wires.Count);
        Assert.Equal(3, gateway.Requests.Count);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(citationCount, result.Claims.Count);
    }

    [Fact]
    public void Draft_citations_follow_current_explicit_primary_before_uncited_historical_bodies()
    {
        var initial = Enumerable.Range(1, 3).Select(index => PreviousProjectionBody($"DRAFT-{index}",
            $"Canonical observed condition {index}: preserve the documented sequence.", page: index)).ToArray();
        var other = Enumerable.Range(1, 30).Select(index => PreviousProjectionBody($"OTHER-{index}", page: index + 10)).ToArray();
        var current = PreviousProjectionBody("CURRENT", page: 80);
        var active = other.Concat(initial).Append(current).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        PreviousProjectionRemember(context, other.Concat(initial).ToArray());
        context.GetType().GetProperty("PreviousCitedEvidenceIds")!.SetValue(context, initial.Select(i => i.Reference.EvidenceId!).ToArray());
        AccumulatedRemember(context, "read_source", [current]);
        context.GetType().GetProperty("LatestExplicitReadIsFindOnly")!.SetValue(context, false);
        var ordered = PreviousProjectionPriority(context, active, other.Append(current).Concat(initial).ToArray());
        Assert.Equal(new[] { "CURRENT", "DRAFT-1", "DRAFT-2", "DRAFT-3" }, ordered.Take(4).Select(i => i.Reference.EvidenceId));
        Assert.Equal(active.Length, ordered.Count);
        Assert.All(ordered, item => Assert.Same(item, Assert.Single(active, a => a.Reference.EvidenceId == item.Reference.EvidenceId)));
    }

    [Theory]
    [InlineData("retired")]
    [InlineData("rejected")]
    [InlineData("ambiguous_scope")]
    [InlineData("empty_duplicate")]
    public void Draft_citation_priority_never_recovers_retired_rejected_or_ambiguous_copies(string change)
    {
        var valid = PreviousProjectionBody("VALID", page: 10);
        var invalid = PreviousProjectionBody("INVALID", page: 11);
        var current = PreviousProjectionBody("CURRENT", page: 80);
        var older = PreviousProjectionBody("OLDER", page: 90);
        var active = new List<AdvancedAnalysisResolvedEvidence> { older, valid, current };
        if (change != "retired") active.Insert(1, invalid);
        if (change == "ambiguous_scope") active.Insert(1, PreviousProjectionBody("INVALID", source: 2));
        if (change == "empty_duplicate") active.Insert(1, invalid with { Content = "" });
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        PreviousProjectionRemember(context, [older, valid, current]);
        context.GetType().GetProperty("PreviousCitedEvidenceIds")!.SetValue(context, new[] { "INVALID", "VALID" });
        AccumulatedRemember(context, "read_source", [current]);
        if (change == "rejected")
        {
            var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("ResearchWorkspaceItem", BindingFlags.NonPublic)!;
            var rejected = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, ["invalid", "Invalid option", "selection", "rejected", "Explicit model decision", new[] { "INVALID" }], null)!;
            var items = Array.CreateInstance(type, 1); items.SetValue(rejected, 0);
            context.GetType().GetProperty("Workspace")!.SetValue(context, items);
        }
        var ordered = PreviousProjectionPriority(context, active, active);
        Assert.Equal(new[] { "CURRENT", "VALID" }, ordered.Take(2).Select(i => i.Reference.EvidenceId));
        Assert.DoesNotContain(ordered.Take(2), i => i.Reference.EvidenceId == "INVALID");
        if (change == "retired") Assert.DoesNotContain(ordered, i => i.Reference.EvidenceId == "INVALID");
        Assert.Same(valid, ordered[1]);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public void An_unfittable_cited_draft_keeps_the_cap_and_offers_non_citable_reopening_choices(string protocol)
    {
        var bodies = OmittedHandleBodies();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(bodies));
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(protocol); options.MaximumEvidencePromptCharacters = 1_000_000;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var turn = OmittedHandleTurn(bodies);
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, turn);
        var full = OmittedHandleProjection(provider, bodies, turn, context);
        using var first = JsonDocument.Parse(OmittedHandlePrompt(provider, full, context));
        Assert.Equal(20, first.RootElement.GetProperty("evidence").GetArrayLength());
        context.GetType().GetProperty("PreviousCitedEvidenceIds")!.SetValue(context, bodies.Select(i => i.Reference.EvidenceId!).ToArray());
        context.GetType().GetProperty("NativeTurn")!.SetValue(context, null);
        options.MaximumEvidencePromptCharacters = 10_000;
        var priority = PreviousProjectionPriority(context, bodies, bodies.Reverse().ToArray());
        var projection = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithOmittedReadRegistry", BuildDirectRequest(), bodies,
            new Dictionary<string, HashSet<string>>(), priority, new Dictionary<string, string>(),
            context.GetType().GetProperty("EvidenceReadWindows")!.GetValue(context), null,
            context.GetType().GetProperty("RetainedOmittedEvidenceReadHandles")!.GetValue(context))!;
        var actual = OmittedHandlePrompt(provider, projection, context);
        using var user = JsonDocument.Parse(actual);
        var visible = user.RootElement.GetProperty("evidence").EnumerateArray().Select(i => i.GetProperty("evidenceId").GetString()!).ToArray();
        Assert.InRange(visible.Length, 1, 19);
        var hints = user.RootElement.GetProperty("omittedEvidenceReadHandles");
        Assert.True(hints.GetProperty("navigationOnly").GetBoolean()); Assert.False(hints.GetProperty("citable").GetBoolean());
        var choice = hints.GetProperty("items")[0].GetProperty("evidenceId").GetString()!;
        Assert.DoesNotContain(choice, visible);
        using var read = JsonDocument.Parse(JsonSerializer.Serialize(new { evidenceReads = new[] { new { evidenceId = choice, offset = 0, length = 512 } } }));
        Assert.NotNull(InvokeSourceCatalogue(null, "ParseEvidenceReadRequests", read.RootElement, projection, context));
        var premature = new AdvancedAnalysisProviderResult { Outcome = "answered", AnswerText = "Unshown citation [C1].",
            Claims = [new() { ClaimId = "C1", Text = "Unshown.", EvidenceIds = [choice] }] };
        Assert.Throws<AdvancedAnalysisProviderException>(() => OpenAiCompatibleAdvancedAnalysisProvider.EnsureVisibleClaimCitations(premature, visible));
        var wire = protocol == "responses"
            ? JsonSerializer.Serialize(new { input = new[] { new { role = "user", content = actual } } })
            : JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = actual } } });
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, 10_000);
        Assert.Empty(factory.Requests);
    }

    private sealed class DraftCitationGateway(bool escaped) : IAdvancedAnalysisToolGateway
    {
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Bodies { get; } = Enumerable.Range(0, 19)
            .Select(i => NavigationCard("DRAFT-CACHE-" + i, "Inspection procedure " + i, 1, i + 1, card: false) with
            {
                Content = "Procedure " + i + ": isolate the documented assembly before inspection.\n" + new string(escaped ? '<' : 'x', 1_000)
            }).ToArray();
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];
        private readonly List<AdvancedAnalysisResolvedEvidence> active = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => active;
        public AdvancedAnalysisToolBudget Budget => new(64, Requests.Count, 300_000, 0, 128, Evidence.Count,
            EvidenceRotationEnabled: true, ReplaceableEvidenceItems: Evidence.Count);
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                var older = Enumerable.Range(0, 109).Select(index => NavigationCard("DRAFT-OLDER-" + index, "Earlier inspection " + index, 1, index + 1, card: false)
                    with { Content = "Earlier observed body " + index + ": " + new string('x', 1_000) }).ToArray();
                active.AddRange(older);
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, older, [], 0, 1));
            }
            if (Requests.Count == 2)
            {
                Assert.Equal("read_source", request.Operation);
                active.AddRange(Bodies);
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, Bodies, [], 0, 2));
            }
            Assert.Equal(3, Requests.Count);
            Assert.Equal("search_corpus", request.Operation);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, active.Take(2).ToArray(), [], 0, 3));
        }
    }
}
