using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    public async Task Discovery_batch_retains_uncited_substantive_source_groups_on_writer_and_critic_wire(
        string protocol, bool mixed)
    {
        const int cap = 25_000;
        var initial = new[]
        {
            PreviousProjectionBody("INITIAL-INDEX", "Table of contents\nAlpha 124\nBeta 130\nGamma 140")
                with { SourceOverview = new(1, 200, 200) },
            PreviousProjectionBody("INITIAL-ONE", "Canonical first condition: isolate the assembly before opening.", page: 124),
            PreviousProjectionBody("INITIAL-TWO", "Canonical independent condition: retain the specified seal.", source: 2, page: 27)
        };
        Assert.Equal(RetrievalContentClassifier.NavigationRole,
            RetrievalContentClassifier.AnalyzeEvidenceContent(initial[0].Content, initial[0].ExactTitle).ContentRole);
        var groups = Enumerable.Range(1, 8).Select(index => index == 7 ? [] : Enumerable.Range(1, 3)
            .Select(hit => PreviousProjectionBody($"DISCOVERY{index}-{hit}",
                $"Setting {index}: isolate the assembly before using the prescribed value. "
                + string.Concat(Enumerable.Repeat("\"scope\\path\" setting ", 112)), page: 130 + index)).ToArray()).ToArray();
        var calls = Enumerable.Range(1, 8).Select(index => mixed && index <= 7
            ? ("find_source_text", JsonSerializer.Serialize(new { sourceKey = "internal-source-1", query = $"Setting {index}", offset = 0, topK = 4 }))
            : ("search_corpus", JsonSerializer.Serialize(new { sourceKey = "", query = $"Setting {index}", category = "", documentHint = "", topK = 4 }))).ToArray();
        const string insufficient = """{"outcome":"insufficient_documentation","answerText":"Le seuil ne peut pas être confirmé.","claims":[]}""";
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls),
            EarlyReply(protocol, insufficient), EarlyReply(protocol, insufficient));
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = cap;
        options.ExternalMaximumCallsPerJob = 4;
        options.SemanticCriticEnabled = true;
        options.NativeResearchWorkspaceEnabled = false;
        options.NativeResearchActiveProposalEnabled = false;
        var gateway = new SequencedToolGateway([initial, .. groups]);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
        Assert.Equal(4, factory.Requests.Count);
        Assert.Equal(9, gateway.Searches.Count);
        Assert.Equal(mixed ? 7 : 0, gateway.Searches.Count(request => request.Operation == "find_source_text"));
        Assert.DoesNotContain(gateway.Searches, request => request.Operation == "read_source");
        Assert.Equal(initial.Length + groups.Sum(group => group.Length), gateway.Evidence.Count);
        foreach (var call in factory.Requests.Skip(2))
        {
            var wire = call.Body;
            var evidence = WorkspaceUser(wire).GetProperty("evidence").EnumerateArray().ToArray();
            Assert.StartsWith("DISCOVERY", evidence[0].GetProperty("evidenceId").GetString());
            Assert.Equal("INITIAL-ONE", evidence[1].GetProperty("evidenceId").GetString());
            Assert.Equal("INITIAL-TWO", evidence[3].GetProperty("evidenceId").GetString());
            foreach (var prior in initial.Skip(1))
            {
                var visible = Assert.Single(evidence, item => item.GetProperty("evidenceId").GetString() == prior.Reference.EvidenceId);
                Assert.Equal(prior.Content, visible.GetProperty("content").GetString());
                Assert.False(visible.GetProperty("contentTruncated").GetBoolean());
            }
            Assert.True(evidence.Length < gateway.Evidence.Count);
            Assert.Equal(8, NativeCorrelationOutputs(wire, protocol).Length);
            Assert.InRange(CrossBatchDocumentaryDifference(wire, compact: true), 1, cap);
        }
    }

    [Theory]
    [InlineData("chat-completions", false, true)]
    [InlineData("responses", false, true)]
    [InlineData("chat-completions", true, true)]
    [InlineData("responses", true, true)]
    [InlineData("chat-completions", false, false)]
    [InlineData("responses", false, false)]
    public async Task Find_projection_review_keeps_valid_cited_bodies_with_eight_new_find_results(
        string protocol, bool activeProposal, bool substantiveFindHit)
    {
        const int cap = 65_536;
        var initial = Enumerable.Range(1, 3).Select(FindProjectionInitialCard).ToArray();
        var locator = PreviousProjectionBody("LOCATOR", "Table of contents\nOptionA 121\nOptionB 122\nOptionC 123",
            page: 1) with { SourceOverview = new(1, 200, 200) };
        var groups = Enumerable.Range(1, 8).Select(index =>
        {
            var navigation = PreviousProjectionBody($"INDEX{index}", "Table of contents\n"
                + string.Join("\n", Enumerable.Range(1, 80).Select(page => $"Observed option {index} {page}")), page: 4);
            var body = PreviousProjectionBody($"FIND{index}", $"Observed option {index}\nComposition et préparation documentées. "
                + string.Concat(Enumerable.Repeat("\"setting\\path\" <tag>& 漢字 ", 110)), page: 124 + index);
            return substantiveFindHit ? new[] { navigation, body } : new[] { navigation };
        }).ToArray();
        Assert.All(groups, group => Assert.Equal(RetrievalContentClassifier.NavigationRole,
            RetrievalContentClassifier.AnalyzeEvidenceContent(group[0].Content, group[0].ExactTitle).ContentRole));
        if (substantiveFindHit)
            Assert.All(groups, group => Assert.NotEqual(RetrievalContentClassifier.NavigationRole,
                RetrievalContentClassifier.AnalyzeEvidenceContent(group[1].Content, group[1].ExactTitle).ContentRole));
        var calls = Enumerable.Range(1, 8).Select(index => ("find_source_text", JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", query = $"Observed option {index}", offset = 0, topK = 4
        }))).ToArray();
        var insufficient = JsonSerializer.Serialize(new
        {
            outcome = "insufficient_documentation", answerText = "Trois options sont documentées, le reste du planning reste non établi.",
            claims = initial.Select((item, index) => new
            {
                claimId = $"C{index + 1}", selectedItem = $"Option{(char)('A' + index)}",
                text = $"Option{(char)('A' + index)} conserve sa condition documentaire.",
                evidenceIds = new[] { item.Reference.EvidenceId! }
            }).ToArray()
        });
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan), EarlyReply(protocol, insufficient),
            protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls),
            EarlyReply(protocol, insufficient));
        var options = EarlyOptions(protocol, 6);
        options.NativeResearchActiveProposalEnabled = activeProposal;
        options.NativeObservationReadEnabled = false; options.VisualSourceCitationsEnabled = false;
        options.MaximumEvidencePromptCharacters = cap;
        var gateway = new SequencedToolGateway(new[] { locator }.Concat(initial).ToArray(),
            groups[0], groups[1], groups[2], groups[3], groups[4], groups[5], groups[6], groups[7]);
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger);
        var request = BuildRequest(20, "named_item", "structured_layout", "documented_option");

        var result = await provider.ExecuteAsync(request, gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(3, result.Claims.Count); Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(4, factory.Requests.Count); Assert.Equal(9, gateway.Searches.Count);
        Assert.All(gateway.Searches.Skip(1), search =>
        {
            Assert.Equal("find_source_text", search.Operation); Assert.Equal(4, search.TopK);
            Assert.Equal(initial[0].Reference.DocId, search.DocId); Assert.Equal(0, search.Offset);
        });
        var review = WorkspaceUser(factory.Requests[2].Body);
        Assert.Equal("initial_insufficiency_before_documentary_research",
            review.GetProperty("insufficiencyReview").GetProperty("reasonCode").GetString());
        // This soft continuity is independent of the optional feature. Its old
        // branch still skips ActiveProposal at the review continue.
        Assert.Equal(activeProposal, review.TryGetProperty("activeProposal", out var active));
        if (activeProposal) Assert.Empty(active.GetProperty("retainedEvidenceIds").EnumerateArray());
        var sent = factory.Requests[3].Body;
        var final = WorkspaceUser(sent);
        var visible = final.GetProperty("evidence").EnumerateArray().ToArray();
        foreach (var item in initial)
        {
            Assert.Contains(visible, row => row.GetProperty("evidenceId").GetString() == item.Reference.EvidenceId);
            var row = Assert.Single(visible, row => row.GetProperty("evidenceId").GetString() == item.Reference.EvidenceId);
            Assert.Equal(item.Content, row.GetProperty("content").GetString());
            Assert.False(row.GetProperty("contentTruncated").GetBoolean());
            Assert.Equal("content_card", row.GetProperty("evidenceKind").GetString());
            Assert.Equal(item.Reference.PageStart, row.GetProperty("physicalPageStart").GetInt32());
        }
        Assert.Equal(new[] { substantiveFindHit ? "FIND1" : "INDEX1", "PRIOR1",
                substantiveFindHit ? "FIND2" : "INDEX2", "PRIOR2", substantiveFindHit ? "FIND3" : "INDEX3", "PRIOR3" },
            visible.Take(6).Select(row => row.GetProperty("evidenceId").GetString()));
        Assert.Equal(activeProposal, final.TryGetProperty("activeProposal", out _));
        Assert.Equal(8, NativeCorrelationOutputs(sent, protocol).Length);
        Assert.InRange(CrossBatchDocumentaryDifference(sent, compact: true), 1, cap);
        Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(sent), 1, cap);

        // The canonical results remain physical-page ordered, including every
        // index. Only the prompt projection changes, never the returned records.
        foreach (var group in groups)
            Assert.All(group, item => Assert.Same(item, Assert.Single(gateway.Evidence,
                value => value.Reference.EvidenceId == item.Reference.EvidenceId)));
        Assert.Equal(groups.SelectMany(group => group).Select(item => item.Reference.EvidenceId),
            gateway.Evidence.Skip(4).Select(item => item.Reference.EvidenceId));
        if (substantiveFindHit)
        {
            options.MaximumEvidencePromptCharacters = 1_000_000;
            var full = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", request,
                gateway.Evidence, new Dictionary<string, HashSet<string>>(), gateway.Evidence,
                new Dictionary<string, string>(), null, null)!;
            var inner = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
            var records = JsonSerializer.SerializeToNode(full, inner)!.AsArray().ToDictionary(
                item => item!["evidenceId"]!.GetValue<string>(), item => item!, StringComparer.Ordinal);
            var smallIds = new[] { "FIND1", "PRIOR1", "FIND2", "PRIOR2", "FIND3", "PRIOR3" };
            Assert.InRange(CrossBatchDocumentaryDifference(PreviousProjectionOracleWire(sent, records, smallIds, inner), compact: true), 1, cap);
            Assert.True(CrossBatchDocumentaryDifference(PreviousProjectionOracleWire(sent, records,
                gateway.Evidence.Select(item => item.Reference.EvidenceId!), inner), compact: true) > cap);
            Assert.True(visible.Length < gateway.Evidence.Count);
        }
        var reviewProbe = Assert.Single(logger.Events, entry => entry.Values.GetValueOrDefault("ReasonCode")?.ToString()
            == "initial_insufficiency_before_documentary_research");
        using var measurements = JsonDocument.Parse(reviewProbe.Values["Measurements"]!.ToString()!);
        Assert.Equal(3, measurements.RootElement.GetProperty("previous_cited_evidence_count").GetInt32());
    }

    [Theory]
    [InlineData("read_source")]
    [InlineData("read_evidence")]
    public void Find_projection_does_not_override_a_new_explicit_page_or_window_read(string operation)
    {
        var prior = PreviousProjectionBody("PRIOR", card: true);
        var index = PreviousProjectionBody("INDEX", "Table of contents\nAlpha 124\nBeta 130\nGamma 140");
        var body = PreviousProjectionBody("BODY", "Documented preparation: retain the specified seal condition.", page: 124);
        var page = PreviousProjectionBody("PAGE", "Explicitly requested page, canonical content.", page: 150);
        var active = new[] { prior, index, body, page };
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        PreviousProjectionRemember(context, [prior]);
        context.GetType().GetProperty("PreviousCitedEvidenceIds")!.SetValue(context, new[] { "PRIOR" });
        AccumulatedRemember(context, "find_source_text", [index, body]);
        Assert.Equal(new[] { "BODY", "PRIOR" }, PreviousProjectionPriority(context, active, active)
            .Take(2).Select(item => item.Reference.EvidenceId));
        if (operation == "read_source") AccumulatedRemember(context, operation, [page]);
        else
        {
            var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("EvidenceReadWindow", BindingFlags.NonPublic)!;
            var window = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [0, 256, null], null)!;
            var updates = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), type))!;
            updates.Add("PAGE", window);
            Assert.Equal(1, (int)InvokeSourceCatalogue(null, "ApplyEvidenceReadWindows", updates, context)!);
        }
        Assert.Equal("PAGE", PreviousProjectionPriority(context, active, active)[0].Reference.EvidenceId);
    }

    [Fact]
    public void Find_projection_without_citations_interleaves_current_primaries_and_prior_bodies()
    {
        var prior = new[] { PreviousProjectionBody("A1"), PreviousProjectionBody("A2"),
            PreviousProjectionBody("C1", source: 2) };
        var groups = new[] { AccumulatedReadBodies("B1", "B2"), AccumulatedReadBodies("D1", "D2"),
            AccumulatedReadBodies("F1", "F2") };
        var active = prior.Concat(groups.SelectMany(group => group)).ToArray();
        var context = AccumulatedReadContext(new AccumulatedReadGateway(active));
        PreviousProjectionRemember(context, prior);
        RememberBalancedExplicitBatch(context, groups);
        var ordered = PreviousProjectionPriority(context, active, active);
        // Discovery keeps the first fresh hit, but does not require a citation
        // to retain prior substantive groups between current literal finds.
        Assert.Equal(new[] { "B1", "A1", "D1", "C1", "F1", "A2", "B2", "D2", "F2" },
            ordered.Select(item => item.Reference.EvidenceId));
        Assert.Empty((IReadOnlyList<string>)context.GetType().GetProperty("PreviousCitedEvidenceIds")!.GetValue(context)!);
        Assert.Equal(active.Length, ordered.Count);
        foreach (var item in active)
            Assert.Same(item, Assert.Single(ordered, value => value.Reference.EvidenceId == item.Reference.EvidenceId));
        var pins = PreviousProjectionRemember(context, []);
        Assert.DoesNotContain("A1", pins); Assert.DoesNotContain("A2", pins); Assert.DoesNotContain("C1", pins);
    }

    [Fact]
    public void Find_projection_rebinds_citations_and_does_not_recover_retired_or_rejected_bodies()
    {
        var prior = PreviousProjectionBody("PRIOR", card: true);
        var retired = PreviousProjectionBody("RETIRED", card: true);
        var body = PreviousProjectionBody("BODY");
        var gateway = new AccumulatedReadGateway([prior, retired, body]);
        var context = AccumulatedReadContext(gateway);
        PreviousProjectionRemember(context, [prior, retired]);
        context.GetType().GetProperty("PreviousCitedEvidenceIds")!.SetValue(context, new[] { "PRIOR", "RETIRED" });
        AccumulatedRemember(context, "find_source_text", [body]);
        var rebound = prior with { Content = "Current canonical replacement, not remembered documentary text." };
        gateway.Active = [rebound, body];
        var ordered = PreviousProjectionPriority(context, gateway.Active, [retired, prior, body]);
        Assert.DoesNotContain(ordered, item => item.Reference.EvidenceId == "RETIRED");
        Assert.Same(rebound, Assert.Single(ordered, item => item.Reference.EvidenceId == "PRIOR"));
        var type = typeof(OpenAiCompatibleAdvancedAnalysisProvider).GetNestedType("ResearchWorkspaceItem", BindingFlags.NonPublic)!;
        var rejected = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, ["prior", "Prior candidate", "selection", "rejected", "Explicit decision", new[] { "PRIOR" }], null)!;
        var items = Array.CreateInstance(type, 1); items.SetValue(rejected, 0);
        context.GetType().GetProperty("Workspace")!.SetValue(context, items);
        var later = PreviousProjectionBody("LATER"); gateway.Active = [rebound, body, later];
        ordered = PreviousProjectionPriority(context, gateway.Active, [body, later, prior]);
        Assert.Equal(new[] { "BODY", "LATER", "PRIOR" }, ordered.Select(item => item.Reference.EvidenceId));
        Assert.Same(rebound, ordered[2]);
        // The new soft queue adds no protection. Canonical access and the
        // independently explicit BODY are unchanged by an empty projection.
        Assert.DoesNotContain("PRIOR", PreviousProjectionRemember(context, []));
        Assert.DoesNotContain("RETIRED", PreviousProjectionRemember(context, []));
    }

    private static AdvancedAnalysisResolvedEvidence FindProjectionInitialCard(int index)
    {
        var item = PreviousProjectionBody($"PRIOR{index}",
            $"Option{(char)('A' + index - 1)}\nComposition et préparation documentées : conserver la condition propre à cette option. "
                + new string((char)('a' + index), 600), page: 120 + index);
        var source = item.Reference;
        return item with { Reference = new AdvancedAnalysisResultEvidence
        {
            EvidenceId = source.EvidenceId, DocId = source.DocId, RevisionId = source.RevisionId,
            SourceHash = source.SourceHash, DocPath = source.DocPath, FileName = source.FileName,
            PageStart = source.PageStart, PageEnd = source.PageEnd, ContentCardId = Guid.NewGuid().ToString("D")
        }};
    }

    [Fact]
    public void Find_projection_soft_citations_require_a_valid_proposal_and_a_visible_current_body()
    {
        var shown = PreviousProjectionBody("SHOWN");
        var hidden = PreviousProjectionBody("HIDDEN");
        var canonical = new[] { shown, hidden };
        var context = AccumulatedReadContext(new AccumulatedReadGateway(canonical));
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeResearchActiveProposalEnabled = false;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var request = BuildDirectRequest();
        var observations = InvokeSourceCatalogue(provider, "BuildPromptEvidenceWithPersistentSourceKeys", request,
            new[] { shown }, new Dictionary<string, HashSet<string>>(), new[] { shown },
            new Dictionary<string, string>(), null, null)!;
        var valid = JsonSerializer.Serialize(new
        {
            outcome = "insufficient_documentation", answerText = "One current body is visible.",
            claims = new[] { new { claimId = "C1", text = "Documented condition.", evidenceIds = new[] { "SHOWN", "HIDDEN" } } }
        });
        InvokeSourceCatalogue(null, "RememberPreviousCitedEvidence", valid, canonical, observations, request, context);
        var property = context.GetType().GetProperty("PreviousCitedEvidenceIds")!;
        Assert.Equal(new[] { "SHOWN" }, (IReadOnlyList<string>)property.GetValue(context)!);
        Assert.Empty((IReadOnlyList<string>)context.GetType().GetProperty("ActiveProposalEvidenceIds")!.GetValue(context)!);
        foreach (var rejected in new[] { "invalid JSON", valid.Replace("SHOWN", "UNKNOWN", StringComparison.Ordinal) })
        {
            InvokeSourceCatalogue(null, "RememberPreviousCitedEvidence", rejected, canonical, observations, request, context);
            Assert.Equal(new[] { "SHOWN" }, (IReadOnlyList<string>)property.GetValue(context)!);
        }
        Assert.DoesNotContain("SHOWN", PreviousProjectionRemember(context, []));
        Assert.DoesNotContain("HIDDEN", PreviousProjectionRemember(context, []));
    }
}
