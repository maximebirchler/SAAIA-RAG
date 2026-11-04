using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static (string Name, string Arguments)[] PassiveAuxiliaryReads(int first, int count)
        => Enumerable.Range(first, count).Select(page => ("read_source", JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", pageStart = page, pageCount = 1, topK = 20,
            tableOrdinal = (int?)null, tableRowOffset = (int?)null, tableRowLimit = (int?)null,
            includeVisualSources = false, visualSourceOffset = (int?)null,
            visualSourceLimit = page <= 8 ? (int?)4 : null,
            nativeObservationView = (string?)null, nativeObservationBlockId = (string?)null,
            nativeObservationRawSha256 = (string?)null, nativeObservationOffset = (int?)null,
            nativeObservationLimit = 16
        }))).ToArray();

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Advertised_passive_read_limits_do_not_reject_a_ten_call_prefix_or_activate_auxiliary_reads(string protocol)
    {
        HttpResponseMessage Calls((string Name, string Arguments)[] calls)
            => protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls);
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)),
            Calls(PassiveAuxiliaryReads(1, 10)), Calls(PassiveAuxiliaryReads(9, 2)),
            EarlyReply(protocol, GuidanceInsufficiency));
        var options = PrefixOptions(protocol, 4);
        options.NativeObservationReadEnabled = true;
        options.VisualSourceCitationsEnabled = true;
        var gateway = new PrefixToolGateway();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        var reads = gateway.Requests.Where(request => request.Operation == "read_source").ToArray();
        Assert.Equal(Enumerable.Range(1, 10), reads.Select(read => read.PageStart!.Value));
        Assert.All(reads, read =>
        {
            Assert.False(AdvancedAnalysisNativeObservationReader.HasRequest(read));
            AdvancedAnalysisNativeObservationReader.ValidateRequest(read);
            AdvancedAnalysisVisualSourceReader.ValidateRequest(read, enabled: false);
            Assert.False(read.IncludeVisualSources);
            Assert.Equal(gateway.Initial.Reference.DocId, read.DocId);
            Assert.Equal(gateway.Initial.Reference.RevisionId, read.RevisionId);
        });
        using var initialWire = JsonDocument.Parse(factory.Requests[1].Body);
        var readTool = initialWire.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.TryGetProperty("function", out var function) ? function : tool)
            .Single(tool => tool.GetProperty("name").GetString() == "read_source");
        Assert.Contains("Populate every declared property. Use null for nullable options inactive in the selected reading mode",
            readTool.GetProperty("description").GetString());
        Assert.Contains("A documented default limit alone does not select an auxiliary reading.", readTool.GetProperty("description").GetString());
        var parameters = readTool.GetProperty("parameters");
        Assert.Equal(parameters.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(),
            parameters.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order());
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
        Assert.Equal(10, outputs.Length);
        for (var i = 0; i < outputs.Length; i++)
            Assert.Equal(i < 8 ? "executed" : "non_executed_batch_limit", outputs[i].Body.GetProperty("status").GetString());
        Assert.All(NativeCorrelationOutputs(factory.Requests[3].Body, protocol),
            output => Assert.Equal("executed", output.Body.GetProperty("status").GetString()));
        Assert.Equal(4, factory.Requests.Count);
        Assert.Equal("insufficient_documentation", result.Outcome);
    }

    [Fact]
    public void Advertised_passive_limits_are_inactive_without_view_flag_or_locator_even_when_features_are_disabled()
    {
        var read = new AdvancedAnalysisSearchRequest("canonical body", Operation: "read_source",
            PageStart: 1, PageEnd: 1, NativeObservationLimit: 16, VisualSourceLimit: 4);
        Assert.False(AdvancedAnalysisNativeObservationReader.HasRequest(read));
        AdvancedAnalysisNativeObservationReader.ValidateRequest(read);
        AdvancedAnalysisVisualSourceReader.ValidateRequest(read, enabled: false);
    }
    [Theory]
    [InlineData("native-limit")]
    [InlineData("native-offset")]
    [InlineData("native-block")]
    [InlineData("native-hash")]
    [InlineData("native-view")]
    [InlineData("visual-limit")]
    [InlineData("visual-offset")]
    public void Active_or_nondefault_auxiliary_controls_still_require_complete_selectors(string variant)
    {
        var read = new AdvancedAnalysisSearchRequest("canonical body", Operation: "read_source", PageStart: 1, PageEnd: 1);
        if (variant.StartsWith("native-", StringComparison.Ordinal))
        {
            read = variant switch
            {
                "native-limit" => read with { NativeObservationLimit = 17 },
                "native-offset" => read with { NativeObservationOffset = 0, NativeObservationLimit = 16 },
                "native-block" => read with { NativeObservationBlockId = "unobserved", NativeObservationLimit = 16 },
                "native-hash" => read with { NativeObservationRawSha256 = new string('a', 64), NativeObservationLimit = 16 },
                _ => read with { NativeObservationView = "unsupported", NativeObservationLimit = 16 }
            };
            Assert.True(AdvancedAnalysisNativeObservationReader.HasRequest(read));
            Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisNativeObservationReader.ValidateRequest(read));
        }
        else
        {
            read = variant == "visual-limit" ? read with { VisualSourceLimit = 5 }
                : read with { VisualSourceOffset = 0, VisualSourceLimit = 4 };
            Assert.True(AdvancedAnalysisVisualSourceReader.HasRequest(read));
            Assert.Throws<AdvancedAnalysisToolException>(() => AdvancedAnalysisVisualSourceReader.ValidateRequest(read, enabled: true));
            Assert.Equal("visual_source_citations_disabled", Assert.Throws<AdvancedAnalysisToolException>(
                () => AdvancedAnalysisVisualSourceReader.ValidateRequest(read, enabled: false)).ErrorCode);
        }
    }

    [Theory]
    [InlineData("responses", false, false)]
    [InlineData("chat-completions", false, false)]
    [InlineData("responses", true, false)]
    [InlineData("chat-completions", true, false)]
    [InlineData("responses", false, true)]
    [InlineData("chat-completions", false, true)]
    [InlineData("responses", true, true)]
    [InlineData("chat-completions", true, true)]
    public async Task Malformed_active_auxiliary_selector_returns_atomic_typed_feedback_with_bounded_recovery(
        string protocol, bool visual, bool exhausted)
    {
        var valid = PassiveAuxiliaryReads(2, 1).Single();
        var invalid = System.Text.Json.Nodes.JsonNode.Parse(valid.Arguments)!.AsObject();
        if (visual) invalid["visualSourceOffset"] = 0;
        else invalid["nativeObservationBlockId"] = "PRIVATE_SELECTOR_SENTINEL";
        var rejected = new[] { PassiveAuxiliaryReads(1, 1).Single(), ("read_source", invalid.ToJsonString()) };
        HttpResponseMessage Calls((string Name, string Arguments)[] calls)
            => protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls);
        var replies = new List<HttpResponseMessage> { Completion(GuidancePlan(1)), Calls(rejected) };
        if (!exhausted)
        {
            replies.Add(Calls([valid]));
            replies.Add(EarlyReply(protocol, GuidanceInsufficiency));
        }
        using var factory = new QueuedHttpClientFactory(replies.ToArray());
        var options = PrefixOptions(protocol, exhausted ? 3 : 4);
        options.NativeObservationReadEnabled = true;
        options.VisualSourceCitationsEnabled = true;
        var gateway = new PrefixToolGateway();
        var logger = new NativeCorrelationLogger();
        var run = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        if (exhausted)
        {
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() => run);
            Assert.Equal("advanced_synthesis_research_protocol_invalid", error.ErrorCode);
            Assert.DoesNotContain(gateway.Requests, request => request.Operation == "read_source");
            Assert.Equal(2, factory.Requests.Count);
        }
        else
        {
            Assert.Equal("insufficient_documentation", (await run).Outcome);
            var read = Assert.Single(gateway.Requests, request => request.Operation == "read_source");
            Assert.Equal(2, read.PageStart);
            var feedback = WorkspaceUser(factory.Requests[2].Body).GetProperty("researchArgumentFeedback");
            Assert.Equal(visual ? "visual_source_request_invalid" : "native_observation_read_window_invalid",
                feedback.GetProperty("correction").GetProperty("reasonCode").GetString());
            Assert.All(NativeCorrelationOutputs(factory.Requests[2].Body, protocol),
                output => Assert.Equal("batch_rejected_before_execution", output.Body.GetProperty("status").GetString()));
            Assert.Equal(4, factory.Requests.Count);
        }
        var probe = Assert.Single(logger.Entries, line => line.Contains("operation=llm.research_argument_rejection ", StringComparison.Ordinal));
        Assert.Contains(exhausted ? "outcome=terminal" : "outcome=recovery", probe);
        Assert.Contains(visual ? "reason=visual_source_request_invalid" : "reason=native_observation_read_window_invalid", probe);
        Assert.DoesNotContain("PRIVATE_SELECTOR_SENTINEL", probe);
        Assert.DoesNotContain("internal-source-1", probe);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Passive_auxiliary_defaults_and_nulls_share_one_effective_canonical_read_identity(string protocol)
    {
        var documented = PassiveAuxiliaryReads(1, 1).Single();
        var absent = System.Text.Json.Nodes.JsonNode.Parse(documented.Arguments)!.AsObject();
        absent["nativeObservationLimit"] = null;
        absent["visualSourceLimit"] = null;
        var calls = new[] { documented, ("read_source", absent.ToJsonString()) };
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(1)),
            protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls),
            EarlyReply(protocol, GuidanceInsufficiency));
        var options = PrefixOptions(protocol, 3);
        options.NativeObservationReadEnabled = true;
        options.VisualSourceCitationsEnabled = true;
        var gateway = new PrefixToolGateway();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Single(gateway.Requests, request => request.Operation == "read_source");
        var outputs = NativeCorrelationOutputs(factory.Requests[2].Body, protocol);
        Assert.Equal(2, outputs.Length);
        Assert.All(outputs, output => Assert.Equal("executed", output.Body.GetProperty("status").GetString()));
        Assert.True(outputs[1].Body.GetProperty("resultReusedWithinBatch").GetBoolean());
    }

}
