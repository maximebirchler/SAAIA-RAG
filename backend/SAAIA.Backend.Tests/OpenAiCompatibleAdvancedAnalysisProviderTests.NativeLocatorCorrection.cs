using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task NativeLocatorCorrection_legacy_json_rejects_the_whole_batch_before_reader_then_accepts_correction(string protocol, bool mixedBatch)
    {
        var gateway = new NativeAuditGateway();
        object Read(string view, string? sha) => new
        {
            operation = "read_source", sourceKey = "internal-source-1", pageStart = 1, pageEnd = 1, topK = 20,
            nativeObservationView = view, nativeObservationBlockId = sha is null ? null : "native-audit",
            nativeObservationRawSha256 = sha, nativeObservationOffset = 0, nativeObservationLimit = 2
        };
        string Research(params object[] queries) => JsonSerializer.Serialize(new { outcome = "research_required", queries });
        HttpResponseMessage Text(string value) => protocol == "responses" ? NativeResponseAnswer(value) : Completion(value);
        var wrong = (gateway.RawSha[0] == '0' ? "1" : "0") + gateway.RawSha[1..];
        var rejectedQueries = mixedBatch
            ? new object[] { new { operation = "search_corpus", query = "new search must not execute", topK = 12 }, Read("raw", wrong) }
            : [Read("raw", wrong)];
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Text(Research(Read("inventory", null))), Text(Research(rejectedQueries)),
            Text(Research(Read("raw", gateway.RawSha.ToUpperInvariant()))), Text(NativeAnswered));
        var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 64_000;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(5, factory.Requests.Count);
        Assert.Equal(3, gateway.Searches.Count);
        Assert.Single(gateway.Searches, request => request.NativeObservationView == "raw");
        Assert.DoesNotContain(gateway.Searches, request => request.Query == "new search must not execute");
        Assert.Equal(gateway.RawSha.ToUpperInvariant(), gateway.Searches[^1].NativeObservationRawSha256);
        var feedback = WorkspaceUser(factory.Requests[3].Body).GetProperty("researchArgumentFeedback");
        Assert.Equal("research_batch_rejected_before_execution", feedback.GetProperty("reasonCode").GetString());
        Assert.Equal("read_source_native_raw_locator_not_observed", feedback.GetProperty("correction").GetProperty("reasonCode").GetString());
        Assert.DoesNotContain(WorkspaceUser(factory.Requests[3].Body).GetProperty("evidence").EnumerateArray(),
            item => item.TryGetProperty("nativeObservation", out var native) && native.ValueKind != JsonValueKind.Null);
        foreach (var line in gateway.NativeLines)
            Assert.Equal(1, CountLiteralInJsonStrings(factory.Requests[^1].Body, line));
    }

    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    public async Task NativeLocatorCorrection_wrong_well_formed_sha_is_rejected_atomically_then_corrected(string protocol, bool mixedBatch)
    {
        var gateway = new NativeAuditGateway();
        string Read(string view, string? sha) => JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", pageStart = 1, pageEnd = 1, topK = 20,
            nativeObservationView = view, nativeObservationBlockId = sha is null ? null : "native-audit",
            nativeObservationRawSha256 = sha, nativeObservationOffset = 0, nativeObservationLimit = 2
        });
        var wrong = (gateway.RawSha[0] == '0' ? "1" : "0") + gateway.RawSha[1..];
        HttpResponseMessage Call(string arguments) => protocol == "responses"
            ? NativeResponseFunction("read_source", arguments) : NativeCompletion(("read_source", arguments));
        var rejected = mixedBatch && protocol == "responses"
            ? ResponseWithTwoCalls(Read("raw", wrong))
            : mixedBatch ? NativeCompletion(("search_corpus", WorkspaceSearch), ("read_source", Read("raw", wrong)))
                : Call(Read("raw", wrong));
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Call(Read("inventory", null)), rejected, Call(Read("raw", gateway.RawSha.ToUpperInvariant())),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var options = WorkspaceOptions(protocol); options.NativeObservationReadEnabled = true;
        options.MaximumEvidencePromptCharacters = 64_000;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(5, factory.Requests.Count);
        Assert.Equal(3, gateway.Searches.Count);
        Assert.DoesNotContain(gateway.Searches, item => item.NativeObservationRawSha256 == wrong);
        Assert.Equal(gateway.RawSha.ToUpperInvariant(), gateway.Searches[^1].NativeObservationRawSha256);
        using var request = JsonDocument.Parse(factory.Requests[3].Body);
        var outputs = request.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray().Where(
            item => protocol == "responses" ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : item.TryGetProperty("role", out var role) && role.GetString() == "tool").ToArray();
        Assert.Equal(mixedBatch ? 2 : 1, outputs.Length);
        foreach (var output in outputs)
        {
            using var feedback = JsonDocument.Parse(output.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
            Assert.Equal("batch_rejected_before_execution", feedback.RootElement.GetProperty("status").GetString());
        }
        Assert.DoesNotContain(WorkspaceUser(factory.Requests[3].Body).GetProperty("evidence").EnumerateArray(),
            item => item.TryGetProperty("nativeObservation", out var native) && native.ValueKind != JsonValueKind.Null);
        foreach (var line in gateway.NativeLines)
            Assert.Equal(1, CountLiteralInJsonStrings(factory.Requests[^1].Body, line));

        static HttpResponseMessage ResponseWithTwoCalls(string arguments)
            => new(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(new
            {
                status = "completed", output = new object[]
                {
                    new { type = "function_call", status = "completed", call_id = "mixed-search", name = "search_corpus", arguments = WorkspaceSearch },
                    new { type = "function_call", status = "completed", call_id = "mixed-read", name = "read_source", arguments }
                }, usage = new { input_tokens = 100, output_tokens = 50 }
            }) };
    }
}
