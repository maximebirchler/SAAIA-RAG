using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Every_admitted_primary_read_fragment_has_a_current_read_path_beyond_eight_omissions(string protocol)
    {
        const int cap = 98_304;
        var bodies = VisibleReadBodies("PRIMARY-TAIL", 14, 60, 6_000);
        var gateway = new OmittedHandleGateway(bodies);
        var ordinal = 0;
        string? tail = null;
        using var factory = new OmittedHandleHttpFactory(async request =>
        {
            var wire = await request.Content!.ReadAsStringAsync();
            var user = WorkspaceUser(wire);
            if (ordinal > 0)
                Assert.InRange(OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, cap);
            var response = ordinal++ switch
            {
                0 => Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
                1 => OmittedHandleFunction(protocol, "whole-primary-page", "read_source",
                    """{"sourceKey":"internal-source-1","pageStart":14,"pageCount":1,"topK":60}"""),
                2 => RequestTail(),
                3 => CompleteFromTail(),
                _ => throw new InvalidOperationException("Reopening a disclosed primary fragment needs no extra corpus request.")
            };
            return await AdaptProtocolAgnosticFixtureAsync(request, response, CancellationToken.None);

            System.Net.Http.HttpResponseMessage RequestTail()
            {
                var visible = user.GetProperty("evidence").EnumerateArray()
                    .Select(item => item.GetProperty("evidenceId").GetString()).ToHashSet(StringComparer.Ordinal);
                var omitted = bodies.Where(item => !visible.Contains(item.Reference.EvidenceId)).ToArray();
                Assert.True(omitted.Length > 8);
                tail = omitted[^1].Reference.EvidenceId!;
                Assert.Contains(gateway.Evidence, item => item.Reference.EvidenceId == tail);
                var offered = user.GetProperty("omittedEvidenceReadHandles").GetProperty("items");
                Assert.Contains(offered.EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == tail);
                Assert.DoesNotContain(offered.EnumerateArray(), item => item.TryGetProperty("content", out _));
                Assert.InRange(offered.GetArrayLength(), 1, 64);
                var feedback = Assert.Single(NativeCorrelationOutputs(wire, protocol)).Body.GetProperty("projectionFeedback");
                Assert.Contains(feedback.GetProperty("omittedEvidenceIds").EnumerateArray(), item => item.GetString() == tail);
                AssertWindowOnlyCapability(user, wire, protocol, tail, false);
                return OmittedHandleFunction(protocol, "read-primary-tail", "read_evidence",
                    JsonSerializer.Serialize(new { evidenceId = tail, offset = 0, length = 512 }));
            }

            System.Net.Http.HttpResponseMessage CompleteFromTail()
            {
                AssertCanonicalWindow(user, tail!, bodies);
                Assert.Equal(2, gateway.Requests.Count);
                return Completion(JsonSerializer.Serialize(new
                {
                    outcome = "answered", answerText = "The canonical tail is available [C1].",
                    claims = new[] { new { claimId = "C1", text = "The tail is documented.", evidenceIds = new[] { tail } } }
                }));
            }
        });
        var options = WorkspaceOptions(protocol);
        options.MaximumEvidencePromptCharacters = cap;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(tail, Assert.Single(result.Claims).EvidenceIds.Single());
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Single(gateway.Requests, request => request.Operation == "read_source");
        Assert.DoesNotContain(gateway.Requests, request => request.Operation == "read_evidence");
    }
}
